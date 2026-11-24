// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    DropletErosionGPU.cs   (RIV-1b, the GPU half)
//  Purpose: Runs the droplet solver on the GPU through UC_DropletErosion.compute,
//           with a single read-back - the compute path RIV-1b asked for.
//
//  THIS IS THE "BLOCKED PATH", AND HERE IS EXACTLY WHAT BLOCKED IT
//  --------------------------------------------------------------
//  RIV-1b's original wording put a `useGpuErosion` flag on the erosion MODIFIER NODE.
//  That cannot work, and it is worth writing down so nobody tries it again: a node's
//  Generate/Modify runs on a worker Task - `ChunkManager.cs` states "Generation runs
//  on Task.Run (worker). This class only ever touches Unity API on the main thread",
//  and `UniverseCreatorObject.cs` states "the worker touches no UnityEngine.Object".
//  `ComputeShader`, `ComputeBuffer` and `Dispatch` are graphics API and are MAIN
//  THREAD ONLY, so a dispatch inside the node would throw the moment it ran.
//
//  So the path is opened the only way it safely can be: as a standalone MAIN-THREAD
//  service that any main-thread caller can hand a height array to. It is deliberately
//  NOT auto-wired into the chunk graph. Two reasons, both load-bearing:
//    1. The thread rule above.
//    2. Ordering. Erosion AFTER the graph would chew the carved beds of rivers and
//       lakes, because those are cut by the graph and folded in again at settle. An
//       erosion pass has to run on the LAND, before water shaping - which is where
//       the node already sits. So a GPU pass belongs in a main-thread pre-pass over
//       the coarse world field, not as a post-pass over finished chunks.
//
//  FAILURE IS ALWAYS SAFE
//  ----------------------
//  Every exit that is not a complete success returns false and leaves `heights`
//  BYTE-IDENTICAL. The read-back lands in a scratch array and is copied over the
//  caller's data only after the whole dispatch succeeded, so a device loss, a missing
//  kernel, an unsupported platform, or being called from the wrong thread can never
//  leave a half-eroded field. Callers treat false as "use the CPU path", which is
//  what `DropletErosion.Run` is for and remains the default.
//
//  NOT BIT-REPRODUCIBLE, UNLIKE THE CPU PATH
//  -----------------------------------------
//  UC-T04-10: the shipped kernel has unprotected float read/modify/write in
//  DepositBilinear and ErodeBrush. Concurrent droplets can lose updates; batching
//  does not synchronize threads. The synchronous GetData also blocks its caller.
//  Refuse this kernel before allocating/dispatching until a race-free staged
//  implementation and graphics acceptance exist. Managed erosion remains usable.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// Main-thread GPU droplet erosion. <see cref="TryRun"/> returns false and changes
    /// nothing whenever the GPU path is unavailable for any reason, so a caller can
    /// always fall back to <see cref="DropletErosion.Run"/>.
    /// </summary>
    public static class DropletErosionGPU
    {
        /// <summary>Kernel name in UC_DropletErosion.compute.</summary>
        public const string KernelName = "CSDroplet";

        /// <summary>Must match [numthreads(64,1,1)] in the compute shader.</summary>
        public const int ThreadsPerGroup = 64;

        /// <summary>
        /// Upper bound on droplets per dispatch. A single enormous dispatch can trip the
        /// driver's watchdog (TDR) and kill the editor, so the work is split into batches
        /// of at most this many threads.
        /// </summary>
        public const int MaxDropletsPerDispatch = 262144;

        /// <summary>UC-T04-10: false while the shipped shared-write kernel lacks validated synchronization and staged readback ownership.</summary>
        public static bool HasValidatedKernel => false;

        /// <summary>
        /// UC-T04-10: true only for a validated race-free kernel on a supported graphics device; the shipped shared-write kernel is refused.
        ///
        /// BOTH tests are needed, and the second one was found by the gate rather than reasoned
        /// out: `SystemInfo.supportsComputeShaders` reports the PLATFORM's capability and comes
        /// back TRUE in batchmode with `-nographics`, where there is no graphics device at all and
        /// no kernel is ever loaded (`ComputeShader.HasKernel` returns false there even for a
        /// perfectly compiled shader). Relying on the capability flag alone would let a headless
        /// caller walk all the way into ComputeBuffer creation and back out on an exception - which
        /// works, but by accident. `graphicsDeviceType == Null` is the precise test for "there is
        /// no device", so it is checked first.
        ///
        /// UC-T02-05: reject workers before querying Unity; headless execution uses the existing managed CPU erosion.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                return HasValidatedKernel && UniverseCreator.Nodes.GraphExecutionGuard.HasGraphicsDevice && SystemInfo.supportsComputeShaders; // UC-T04-10: hardware support is not correctness approval for a racing kernel.
            }
        }

        /// <summary>
        /// Erodes <paramref name="heights"/> in place on the GPU. Returns false - having
        /// changed NOTHING - when the platform, the shader, the arguments or the calling
        /// thread make it impossible; the caller must then use the CPU path.
        /// Honours <see cref="DropletSettings.coarseFactor"/> the same way the CPU entry
        /// point does, by eroding a downsampled copy and adding the delta back.
        /// </summary>
        public static bool TryRun(float[] heights, int width, int height,
                                  DropletSettings s, ComputeShader shader)
        {
            string why;
            return TryRun(heights, width, height, s, shader, out why);
        }

        /// <summary>As <see cref="TryRun(float[],int,int,DropletSettings,ComputeShader)"/>,
        /// reporting WHY it refused - for the diagnostics window and the self-test.</summary>
        public static bool TryRun(float[] heights, int width, int height,
                                  DropletSettings s, ComputeShader shader, out string why)
        {
            why = null;

            // UC-T02-05: this helper performs GetData; refuse workers/repaints before native shader equality or dispatch.
            if (!UniverseCreator.Nodes.GraphExecutionGuard.IsMainThread) { why = "GPU erosion requires the main thread; use CPU erosion on workers"; return false; }
#if UNITY_EDITOR
            if (Event.current != null && Event.current.type == EventType.Repaint) { why = "GPU erosion readback is unavailable during Editor repaint"; return false; }
#endif

            if (heights == null) { why = "no height array"; return false; }
            if (width < 3 || height < 3) { why = "field smaller than 3x3"; return false; }
            if (heights.Length < (long)width * height) { why = "height array shorter than width*height"; return false; } // UC-T04-10: reject dimensions without overflowing before any allocation.
            if (shader == null) { why = "no compute shader assigned"; return false; }
            if (!HasValidatedKernel) { why = "GPU erosion shared writes/readback are unvalidated; use deterministic managed erosion"; return false; } // UC-T04-10: no buffers or pending readback can survive this refusal.
            if (!IsSupported) { why = "compute shaders unsupported on this platform (or called off the main thread)"; return false; }
            if (Mathf.Max(0, s.droplets) == 0) { why = "zero droplets requested"; return false; }

            int factor = Mathf.Max(1, s.coarseFactor);
            if (factor == 1)
                return Dispatch(heights, width, height, s, shader, out why);

            // HYBRID COARSE PASS, mirroring DropletErosion.Run: erode a downsampled copy,
            // then add the DIFFERENCE back at full resolution so the fine detail the graph
            // produced survives and only the erosion signal is coarse.
            int cw = Mathf.Max(3, width / factor);
            int ch = Mathf.Max(3, height / factor);

            float[] coarse = DropletErosion.Downsample(heights, width, height, cw, ch);
            if (coarse == null) { why = "downsample failed"; return false; }

            float[] before = (float[])coarse.Clone();

            DropletSettings cs = s;
            cs.coarseFactor = 1;
            cs.droplets = Mathf.Max(1, s.droplets / (factor * factor));

            if (!Dispatch(coarse, cw, ch, cs, shader, out why)) return false;

            for (int i = 0; i < coarse.Length; i++) coarse[i] -= before[i];
            DropletErosion.AddUpsampled(heights, width, height, coarse, cw, ch);
            return true;
        }

        /// <summary>
        /// One full-resolution GPU pass. The read-back lands in scratch and is copied over
        /// the caller's array only on complete success, so a failure leaves it untouched.
        /// </summary>
        private static bool Dispatch(float[] heights, int width, int height,
                                     DropletSettings s, ComputeShader shader, out string why)
        {
            why = null;

            int cells = width * height;
            int kernel;

            ComputeBuffer hBuf = null, bxBuf = null, bzBuf = null, bwBuf = null;

            try
            {
                kernel = shader.FindKernel(KernelName);
            }
            catch
            {
                why = "kernel " + KernelName + " not found in the compute shader";
                return false;
            }

            try
            {
                int[] brushX, brushZ;
                float[] brushW;
                DropletErosion.BuildBrush(Mathf.Max(0, s.brushRadius), out brushX, out brushZ, out brushW);
                if (brushX == null || brushX.Length == 0)
                {
                    why = "brush build produced no taps";
                    return false;
                }

                // Scratch copy: the GPU works on this, and it only reaches the caller's
                // array once every dispatch and the read-back have succeeded.
                float[] scratch = new float[cells];
                System.Array.Copy(heights, scratch, cells);

                hBuf = new ComputeBuffer(cells, sizeof(float));
                hBuf.SetData(scratch);

                bxBuf = new ComputeBuffer(brushX.Length, sizeof(int));
                bzBuf = new ComputeBuffer(brushZ.Length, sizeof(int));
                bwBuf = new ComputeBuffer(brushW.Length, sizeof(float));
                bxBuf.SetData(brushX);
                bzBuf.SetData(brushZ);
                bwBuf.SetData(brushW);

                shader.SetBuffer(kernel, "_Heights", hBuf);
                shader.SetBuffer(kernel, "_BrushX", bxBuf);
                shader.SetBuffer(kernel, "_BrushZ", bzBuf);
                shader.SetBuffer(kernel, "_BrushW", bwBuf);

                shader.SetInt("_Width", width);
                shader.SetInt("_Height", height);
                shader.SetInt("_BrushCount", brushX.Length);
                shader.SetInt("_MaxLifetime", Mathf.Clamp(s.maxLifetime, 1, 256));
                shader.SetInt("_Seed", s.seed);

                shader.SetFloat("_Inertia", Mathf.Clamp01(s.inertia));
                shader.SetFloat("_CapacityFactor", s.capacityFactor);
                shader.SetFloat("_MinCapacity", s.minCapacity);
                shader.SetFloat("_DepositSpeed", Mathf.Clamp01(s.depositSpeed));
                shader.SetFloat("_ErodeSpeed", Mathf.Clamp01(s.erodeSpeed));
                shader.SetFloat("_EvaporateSpeed", Mathf.Clamp01(s.evaporateSpeed));
                shader.SetFloat("_Gravity", s.gravity);
                shader.SetFloat("_StartWater", Mathf.Max(0f, s.startWater));
                shader.SetFloat("_StartSpeed", Mathf.Max(0f, s.startSpeed));

                // Batched so one dispatch cannot trip the driver watchdog.
                int remaining = Mathf.Max(0, s.droplets);
                while (remaining > 0)
                {
                    int batch = Mathf.Min(remaining, MaxDropletsPerDispatch);
                    shader.SetInt("_DropletCount", batch);

                    int groups = (batch + ThreadsPerGroup - 1) / ThreadsPerGroup;
                    if (groups < 1) groups = 1;
                    shader.Dispatch(kernel, groups, 1, 1);

                    remaining -= batch;
                }

                hBuf.GetData(scratch);

                // Only now is the caller's field allowed to change.
                System.Array.Copy(scratch, heights, cells);
                return true;
            }
            catch (System.Exception e)
            {
                // Device loss, a worker-thread call, an out-of-memory buffer - all land here
                // and all mean the same thing to the caller: use the CPU path.
                why = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                if (hBuf != null) hBuf.Release();
                if (bxBuf != null) bxBuf.Release();
                if (bzBuf != null) bzBuf.Release();
                if (bwBuf != null) bwBuf.Release();
            }
        }
    }
}
