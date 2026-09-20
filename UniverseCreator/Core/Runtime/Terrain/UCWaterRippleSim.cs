// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterRippleSim.cs
//  Purpose: Drives UC_WaterRipple.shader. Owns the simulation textures, runs the
//           wave equation on a fixed timestep, keeps the window world-locked as
//           the viewer moves, and publishes the result as global shader
//           properties the water surface shader reads.
//
//  WHY A FIXED TIMESTEP, NOT deltaTime
//  -----------------------------------
//  The propagation constant C = (c*dt/dx)^2 contains dt. Feeding a variable frame
//  time into it means the simulation's wave speed changes with the framerate, and
//  worse, a single long frame - a shader compile, a chunk apply, an editor
//  recompile - pushes C past the stability limit and the field detonates into a
//  flickering checkerboard that never recovers. So the sim runs at a fixed rate
//  and the frame time only decides how many steps to run, capped so that a stall
//  cannot turn into a spiral of catch-up work.
//
//  WHY THE WINDOW IS SNAPPED TO WHOLE TEXELS
//  -----------------------------------------
//  The simulation covers a square around the viewer. If it followed the viewer
//  continuously, every frame would resample the field at a fractional offset and
//  bilinear filtering would smear the ripples away faster than the damping term
//  does. Snapping to whole texels makes the scroll a pure integer shift, which is
//  lossless, and it is what keeps a ripple sitting on the spot where the rock
//  actually landed instead of sliding along with the camera.
//
//  MANAGED C# ONLY - no jobs, no NativeArray. The heavy work is on the GPU, and
//  the CPU side is a list of impulses and a mesh rebuild, which is nothing.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// One disturbance to be splatted into the height field. Queued by
    /// UCWaterDynamics and consumed on the next simulation step.
    /// </summary>
    public struct WaterImpulse
    {
        public Vector2 worldXZ;

        /// <summary>Radius in metres.</summary>
        public float radius;

        /// <summary>Signed displacement in metres. Negative pushes water down.</summary>
        public float strength;

        /// <summary>
        /// 0 is a broad dome (a mass settling into the water), 1 is a sharp ring
        /// (the crown of a fast impact). Real impacts sit between the two and the
        /// emitter picks where, based on how fast the thing was travelling.
        /// </summary>
        public float ringness;

        /// <summary>Above 1 the dome narrows; used to keep small impacts crisp.</summary>
        public float sharpness;

        /// <summary>Foam injected directly, independent of the wave energy.</summary>
        public float foam;
    }

    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Water Ripple Simulation")]
    public class UCWaterRippleSim : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        //  Inspector
        // ---------------------------------------------------------------------

        [Header("Window")]
        [Tooltip("Transform the simulation window follows. Left empty, it follows " +
                 "the main camera, which is what a first-person or third-person " +
                 "game wants without any setup.")]
        public Transform followTarget;

        [Tooltip("Side of the simulated square, in metres. This is a detail budget, " +
                 "not a world size - ripples outside it fall back to the ambient " +
                 "wave texture, which is indistinguishable at distance.")]
        [Range(16f, 512f)] public float worldSize = 128f;

        [Tooltip("Grid resolution. The texel size is worldSize/resolution, and that " +
                 "is the smallest ripple the sim can carry - 128m over 512 texels is " +
                 "25cm, fine enough for a footstep.")]
        public int resolution = 512;

        [Header("Wave")]
        [Tooltip("Propagation speed as a fraction of the grid stability limit. 1 is " +
                 "the fastest a 4-neighbour stencil can run without exploding; the " +
                 "shader clamps regardless, so this cannot be misused.")]
        [Range(0.05f, 1f)] public float waveSpeed = 0.85f;

        [Tooltip("Energy retained per simulation step. Below about 0.985 ripples die " +
                 "almost on contact; at 1.0 a single splash rings forever.")]
        [Range(0.9f, 1f)] public float damping = 0.996f;

        [Tooltip("Simulation steps per second. Kept fixed on purpose - see the note " +
                 "at the top of this file.")]
        [Range(20f, 120f)] public float stepRate = 60f;

        [Tooltip("Most steps allowed in one frame. A stall must not turn into a " +
                 "spiral where catching up costs more than the stall did.")]
        [Range(1, 8)] public int maxStepsPerFrame = 3;

        [Tooltip("Run the simulation in the scene view without entering play mode.\n\n" +
                 "Off by default, and that default is the point. The editor produces " +
                 "frames as fast as you move the mouse, so a simulation left running " +
                 "in edit mode solves waves and swaps render targets every time you " +
                 "pan - in a scene nobody is playing. Turn this on to look at ripples, " +
                 "turn it off when you are done.")]
        public bool simulateInEditor;

        [Header("Output")]
        [Tooltip("Metres of vertical displacement at full ripple amplitude.")]
        [Range(0f, 2f)] public float heightScale = 0.35f;

        [Tooltip("How strongly ripple slope bends the surface normal.")]
        [Range(0f, 40f)] public float normalStrength = 12f;

        [Header("Foam")]
        [Tooltip("Whiteness produced per unit of surface velocity. This is what puts " +
                 "a white ring on a splash and a white trail behind a boat.")]
        [Range(0f, 200f)] public float foamGain = 45f;

        [Tooltip("Foam retained per step. Foam outlives the wave that made it - " +
                 "entrained air takes seconds to rise back out.")]
        [Range(0.8f, 1f)] public float foamDecay = 0.985f;

        [Tooltip("How fast foam drifts outward from where it was made. Zero leaves " +
                 "hard-edged discs that read as decals rather than froth.")]
        [Range(0f, 0.5f)] public float foamSpread = 0.12f;

        [Header("Rain")]
        [Tooltip("Drops per second per square kilometre of the simulation window. " +
                 "Drives the same impulse path everything else uses, so rain " +
                 "interferes with a boat wake instead of drawing over it.")]
        public float rainDropsPerSecond = 0f;

        [Range(0.02f, 1f)] public float rainDropRadius = 0.18f;
        [Range(0f, 0.4f)] public float rainDropStrength = 0.06f;

        [Header("Obstacles")]
        [Tooltip("Optional mask where red marks solid. Waves reflect off it rather " +
                 "than passing through, which is what makes a ripple in a bay " +
                 "behave like it is in a bay. Left empty, water is unbounded.")]
        public Texture obstacleMask;

        // ---------------------------------------------------------------------
        //  Globals the water shader reads
        // ---------------------------------------------------------------------

        private static readonly int IdRippleTex = Shader.PropertyToID("_UCRippleTex");
        private static readonly int IdFoamTex = Shader.PropertyToID("_UCRippleFoamTex");
        private static readonly int IdWindow = Shader.PropertyToID("_UCRippleWindow");
        private static readonly int IdEnabled = Shader.PropertyToID("_UCRippleEnabled");

        private static readonly int IdRippleParams = Shader.PropertyToID("_RippleParams");
        private static readonly int IdScroll = Shader.PropertyToID("_RippleScroll");
        private static readonly int IdResolve = Shader.PropertyToID("_ResolveParams");
        private static readonly int IdFoamParams = Shader.PropertyToID("_FoamParams");
        private static readonly int IdFoamPrev = Shader.PropertyToID("_FoamPrev");
        private static readonly int IdObstacle = Shader.PropertyToID("_ObstacleTex");

        // ---------------------------------------------------------------------
        //  State
        // ---------------------------------------------------------------------

        private Material material;
        private RenderTexture stateA, stateB;
        private RenderTexture foamA, foamB;
        private RenderTexture resolved;

        private CommandBuffer commands;
        private Mesh impulseMesh;

        // Window origin in world XZ, snapped to a texel. The whole point of the
        // scroll pass is that this only ever moves by whole texels.
        private Vector2 origin;
        private bool originValid;

        private float stepAccumulator;
        private float rainAccumulator;
        private int allocatedResolution;

        private readonly List<WaterImpulse> pending = new List<WaterImpulse>();

        // Mesh scratch, grown rather than reallocated. A rainstorm queues hundreds
        // of impulses a second and this is on the main thread every step.
        private Vector3[] meshVerts;
        private Vector2[] meshUVs;
        private Color[] meshColors;
        private int[] meshTris;
        private int meshCapacity;

        /// <summary>
        /// The active simulation, if a scene has one. Static so anything that
        /// touches water can emit without holding a reference - a rigidbody, a
        /// footstep event, a particle collision.
        /// </summary>
        public static UCWaterRippleSim Active { get; private set; }

        /// <summary>Metres per texel. The smallest feature the sim can carry.</summary>
        public float TexelMetres
        {
            get { return worldSize / Mathf.Max(1, resolution); }
        }

        // ---------------------------------------------------------------------
        //  Lifecycle
        // ---------------------------------------------------------------------

        private void OnEnable()
        {
            Active = this;
            stepAccumulator = 0f;
            originValid = false;

            // Not allocated in edit mode unless previewing. EnsureResources takes
            // five render targets at resolution squared, and holding them for a
            // scene nobody is playing is most of this component's memory. The
            // simulation allocates on demand the moment it is allowed to run.
            if (Application.isPlaying || simulateInEditor) EnsureResources();
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
            ReleaseResources();

            // Tell the surface shader the sim is gone. Leaving the flag on would
            // have every water material sampling a released texture, which reads
            // as a frozen ripple field baked permanently into the world.
            Shader.SetGlobalFloat(IdEnabled, 0f);
        }

        private void LateUpdate()
        {
            // EDIT MODE COSTS NOTHING UNLESS ASKED FOR.
            //
            // [ExecuteAlways] is on this class so a designer can preview ripples
            // without entering play mode. That is not the same as "always simulate".
            // Unity ticks an [ExecuteAlways] component once per editor frame, and the
            // editor emits frames as fast as the mouse moves - so ungated, panning the
            // scene view drives a full wave solve, two state swaps and a foam pass per
            // mouse-move, indefinitely, in a scene that is not playing. That reads
            // exactly like the terrain "still executing" while you look at it.
            //
            // Releasing rather than just returning, because the render targets are the
            // memory: an idle simulation still holding five of them has not given
            // anything back.
            if (!Application.isPlaying && !simulateInEditor)
            {
                if (allocatedResolution != 0)
                {
                    ReleaseResources();
                    Shader.SetGlobalFloat(IdEnabled, 0f);
                }
                return;
            }

            if (material == null || stateA == null) { EnsureResources(); if (material == null) return; }

            if (resolution != allocatedResolution) EnsureResources();

            UpdateWindow();
            EmitRain();

            // Fixed step. deltaTime only decides how many, never how big.
            float step = 1f / Mathf.Max(1f, stepRate);
            stepAccumulator += Application.isPlaying ? Time.deltaTime : 1f / 60f;

            int steps = 0;
            while (stepAccumulator >= step && steps < maxStepsPerFrame)
            {
                stepAccumulator -= step;
                steps++;
                Simulate();
            }

            // Drop the backlog rather than carrying it. A four-second editor stall
            // must not be followed by four seconds of the sim running flat out.
            if (stepAccumulator > step * maxStepsPerFrame) stepAccumulator = 0f;

            // Impulses that arrived while no step ran are kept, not dropped - a
            // splash on a stalled frame should still happen, just late.
            if (steps > 0) PublishGlobals();
        }

        // ---------------------------------------------------------------------
        //  Public emission API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Queues a disturbance. Safe to call from anywhere on the main thread and
        /// safe to call when no simulation exists - it is then a no-op, so callers
        /// never need to null-check the water system.
        /// </summary>
        public void Emit(WaterImpulse impulse)
        {
            if (impulse.radius <= 0f) return;

            // Anything outside the window is dropped here rather than clipped in
            // the shader, because the mesh it would add is pure waste - a quad
            // entirely off-screen still costs a draw's worth of vertex work.
            if (originValid)
            {
                Vector2 local = impulse.worldXZ - origin;
                float pad = impulse.radius;
                if (local.x < -pad || local.y < -pad ||
                    local.x > worldSize + pad || local.y > worldSize + pad) return;
            }

            // A cap, because a physics system gone wrong can queue impulses faster
            // than steps consume them and the list would grow without bound.
            if (pending.Count >= 4096) return;

            pending.Add(impulse);
        }

        /// <summary>True when a point lies inside the simulated window.</summary>
        public bool Covers(Vector3 worldPosition)
        {
            if (!originValid) return false;
            float lx = worldPosition.x - origin.x;
            float lz = worldPosition.z - origin.y;
            return lx >= 0f && lz >= 0f && lx <= worldSize && lz <= worldSize;
        }

        // ---------------------------------------------------------------------
        //  Window
        // ---------------------------------------------------------------------

        private void UpdateWindow()
        {
            Transform t = followTarget;
            if (t == null)
            {
                Camera cam = Camera.main;
#if UNITY_EDITOR
                // In the editor with no play mode running, the scene view camera is
                // the one the user is actually looking through.
                if (cam == null && UnityEditor.SceneView.lastActiveSceneView != null)
                    cam = UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
                if (cam != null) t = cam.transform;
            }

            Vector3 centre = t != null ? t.position : transform.position;

            float texel = TexelMetres;
            Vector2 want = new Vector2(centre.x - worldSize * 0.5f, centre.z - worldSize * 0.5f);

            // Snapped. This is the line that keeps ripples world-locked.
            Vector2 snapped = new Vector2(
                Mathf.Floor(want.x / texel) * texel,
                Mathf.Floor(want.y / texel) * texel);

            if (!originValid)
            {
                origin = snapped;
                originValid = true;
                return;
            }

            Vector2 delta = snapped - origin;
            int shiftX = Mathf.RoundToInt(delta.x / texel);
            int shiftZ = Mathf.RoundToInt(delta.y / texel);

            if (shiftX == 0 && shiftZ == 0) return;

            // A jump larger than the window has nothing to preserve. Teleporting
            // the player across the map should not smear the old field across the
            // new location.
            if (Mathf.Abs(shiftX) >= resolution || Mathf.Abs(shiftZ) >= resolution)
            {
                origin = snapped;
                ClearField();
                return;
            }

            origin += new Vector2(shiftX * texel, shiftZ * texel);
            ScrollField(shiftX, shiftZ);
        }

        private void ScrollField(int shiftX, int shiftZ)
        {
            material.SetVector(IdScroll, new Vector4(shiftX, shiftZ, resolution, 0f));

            commands.Clear();
            commands.Blit(stateA, stateB, material, 2);
            commands.Blit(foamA, foamB, material, 2);
            Graphics.ExecuteCommandBuffer(commands);

            Swap(ref stateA, ref stateB);
            Swap(ref foamA, ref foamB);
        }

        // ---------------------------------------------------------------------
        //  Simulation
        // ---------------------------------------------------------------------

        private void Simulate()
        {
            float texel = TexelMetres;

            // C is expressed as a fraction of the stability limit rather than as a
            // physical wave speed, because the physical speed the user would want
            // depends on the texel size and nobody should have to do that division
            // in their head to stop the sim exploding.
            float c = 0.5f * Mathf.Clamp01(waveSpeed);

            // Border fade width as a fraction of the window. Wide enough that a
            // wave has room to die, narrow enough not to eat the usable area.
            float border = 0.06f;

            material.SetVector(IdRippleParams, new Vector4(c, damping, texel, border));
            material.SetTexture(IdObstacle, obstacleMask != null ? obstacleMask : Texture2D.blackTexture);

            commands.Clear();

            // 1. Impulses, splatted additively into the current state so two that
            //    land together interfere instead of one winning.
            if (pending.Count > 0 && BuildImpulseMesh())
            {
                commands.SetRenderTarget(stateA);
                commands.DrawMesh(impulseMesh, Matrix4x4.identity, material, 0, 1);
            }
            pending.Clear();

            // 2. Propagate.
            commands.Blit(stateA, stateB, material, 0);

            // 3. Foam, fed by the propagated field's velocity.
            material.SetVector(IdFoamParams, new Vector4(foamGain, foamDecay, foamSpread, 0f));
            commands.SetGlobalTexture(IdFoamPrev, foamA);
            commands.Blit(stateB, foamB, material, 4);

            // 4. Resolve into the packed texture the surface shader samples.
            material.SetVector(IdResolve,
                new Vector4(heightScale, normalStrength, foamGain, foamDecay));
            commands.Blit(stateB, resolved, material, 3);

            Graphics.ExecuteCommandBuffer(commands);

            Swap(ref stateA, ref stateB);
            Swap(ref foamA, ref foamB);
        }

        private void PublishGlobals()
        {
            Shader.SetGlobalTexture(IdRippleTex, resolved);
            Shader.SetGlobalTexture(IdFoamTex, foamA);
            Shader.SetGlobalVector(IdWindow,
                new Vector4(origin.x, origin.y, worldSize, 1f / Mathf.Max(0.001f, worldSize)));
            Shader.SetGlobalFloat(IdEnabled, 1f);
        }

        // ---------------------------------------------------------------------
        //  Impulse mesh
        // ---------------------------------------------------------------------

        // One quad per impulse, positions written straight in clip space so the
        // vertex shader is a pass-through. Building geometry rather than issuing a
        // draw per impulse is what lets rain run at hundreds of drops a second: a
        // downpour is one draw call, not one per drop.
        private bool BuildImpulseMesh()
        {
            int count = pending.Count;
            EnsureMeshCapacity(count);

            float inv = 1f / Mathf.Max(0.001f, worldSize);
            int v = 0, t = 0, used = 0;

            for (int i = 0; i < count; i++)
            {
                WaterImpulse imp = pending[i];
                if (imp.radius <= 0f || Mathf.Abs(imp.strength) < 1e-5f) continue;

                // World -> window UV -> clip.
                float u = (imp.worldXZ.x - origin.x) * inv;
                float w = (imp.worldXZ.y - origin.y) * inv;
                float r = imp.radius * inv;

                float cx = u * 2f - 1f;
                float cy = w * 2f - 1f;
                float rr = r * 2f;

                Color data = new Color(
                    imp.strength,
                    Mathf.Max(0.05f, imp.sharpness <= 0f ? 1f : imp.sharpness),
                    Mathf.Clamp01(imp.ringness),
                    Mathf.Clamp01(imp.foam));

                meshVerts[v + 0] = new Vector3(cx - rr, cy - rr, 0f);
                meshVerts[v + 1] = new Vector3(cx + rr, cy - rr, 0f);
                meshVerts[v + 2] = new Vector3(cx + rr, cy + rr, 0f);
                meshVerts[v + 3] = new Vector3(cx - rr, cy + rr, 0f);

                meshUVs[v + 0] = new Vector2(-1f, -1f);
                meshUVs[v + 1] = new Vector2(1f, -1f);
                meshUVs[v + 2] = new Vector2(1f, 1f);
                meshUVs[v + 3] = new Vector2(-1f, 1f);

                meshColors[v + 0] = data;
                meshColors[v + 1] = data;
                meshColors[v + 2] = data;
                meshColors[v + 3] = data;

                meshTris[t + 0] = v + 0;
                meshTris[t + 1] = v + 1;
                meshTris[t + 2] = v + 2;
                meshTris[t + 3] = v + 0;
                meshTris[t + 4] = v + 2;
                meshTris[t + 5] = v + 3;

                v += 4;
                t += 6;
                used++;
            }

            if (used == 0) return false;

            // Degenerate the unused tail rather than resizing the arrays. Resizing
            // every step is the allocation this whole scratch-buffer scheme exists
            // to avoid, and a zero-area triangle rasterises nothing.
            for (int i = v; i < meshVerts.Length; i++) meshVerts[i] = Vector3.zero;
            for (int i = t; i < meshTris.Length; i++) meshTris[i] = 0;

            impulseMesh.Clear(true);
            impulseMesh.vertices = meshVerts;
            impulseMesh.uv = meshUVs;
            impulseMesh.colors = meshColors;
            impulseMesh.SetTriangles(meshTris, 0, false);

            // The quads are already in clip space and have no meaningful bounds.
            // Without this Unity culls the whole mesh the moment the window origin
            // moves away from zero.
            impulseMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return true;
        }

        private void EnsureMeshCapacity(int impulses)
        {
            if (meshCapacity >= impulses && meshVerts != null) return;

            meshCapacity = Mathf.NextPowerOfTwo(Mathf.Max(64, impulses));
            meshVerts = new Vector3[meshCapacity * 4];
            meshUVs = new Vector2[meshCapacity * 4];
            meshColors = new Color[meshCapacity * 4];
            meshTris = new int[meshCapacity * 6];
        }

        // ---------------------------------------------------------------------
        //  Rain
        // ---------------------------------------------------------------------

        private void EmitRain()
        {
            if (rainDropsPerSecond <= 0f || !originValid) return;

            float dt = Application.isPlaying ? Time.deltaTime : 1f / 60f;
            rainAccumulator += rainDropsPerSecond * dt;

            // Capped per frame. A user typing an extra zero into the rate must
            // slow the sim down, not lock the editor while it queues a million
            // quads.
            int drops = Mathf.Min(Mathf.FloorToInt(rainAccumulator), 512);
            rainAccumulator -= drops;

            for (int i = 0; i < drops; i++)
            {
                WaterImpulse imp = new WaterImpulse();
                imp.worldXZ = origin + new Vector2(
                    Random.value * worldSize, Random.value * worldSize);

                // Drop size varies. Uniform drops produce a visibly regular
                // interference pattern - real rain does not, because the drops are
                // not all the same size.
                float size = Mathf.Lerp(0.6f, 1.6f, Random.value);
                imp.radius = rainDropRadius * size;

                // Down, not up. A drop punches a hole in the surface; the crown
                // that rises after it is the water coming back, which the wave
                // equation produces on its own from the hole.
                imp.strength = -rainDropStrength * size;

                // Sharply ringed, because a drop hits fast and small.
                imp.ringness = 0.75f;
                imp.sharpness = 2.2f;
                imp.foam = 0.05f;

                Emit(imp);
            }
        }

        // ---------------------------------------------------------------------
        //  Resources
        // ---------------------------------------------------------------------

        private void EnsureResources()
        {
            resolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(resolution), 64, 2048);

            if (material == null)
            {
                Shader s = Shader.Find("Hidden/UniverseCreator/WaterRipple");
                if (s == null)
                {
                    // No exception and no dialog. A missing shader must degrade to
                    // "no ripples", not to a broken scene.
                    enabled = false;
                    Debug.LogWarning("[Universe Creator] Water ripple shader not found. " +
                                     "Interactive water is disabled.", this);
                    return;
                }
                material = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (commands == null) commands = new CommandBuffer { name = "UC Water Ripple" };

            if (impulseMesh == null)
            {
                impulseMesh = new Mesh { name = "UC Water Impulses" };
                impulseMesh.hideFlags = HideFlags.HideAndDontSave;
                impulseMesh.MarkDynamic();
            }

            if (allocatedResolution == resolution && stateA != null && stateA.IsCreated()) return;

            ReleaseTextures();

            // RGHalf: current height and previous height, which is the whole state
            // the second-order scheme needs. See the precision note in the shader.
            stateA = NewTarget(RenderTextureFormat.RGHalf);
            stateB = NewTarget(RenderTextureFormat.RGHalf);
            foamA = NewTarget(RenderTextureFormat.RHalf);
            foamB = NewTarget(RenderTextureFormat.RHalf);
            resolved = NewTarget(RenderTextureFormat.ARGBHalf);

            allocatedResolution = resolution;
            ClearField();
        }

        private RenderTexture NewTarget(RenderTextureFormat format)
        {
            // Clamped, not repeated. A ripple wrapping around to the far side of
            // the window is the single most obvious artefact this system can
            // produce, and it looks like a bug in the game rather than in the water.
            RenderTexture rt = new RenderTexture(resolution, resolution, 0, format,
                                                 RenderTextureReadWrite.Linear);
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.name = "UC Water Sim";
            rt.Create();
            return rt;
        }

        private void ClearField()
        {
            ClearTarget(stateA);
            ClearTarget(stateB);
            ClearTarget(foamA);
            ClearTarget(foamB);
            ClearTarget(resolved);
        }

        private static void ClearTarget(RenderTexture rt)
        {
            if (rt == null) return;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        private void ReleaseResources()
        {
            ReleaseTextures();

            if (commands != null) { commands.Release(); commands = null; }
            if (impulseMesh != null) { DestroyImmediate(impulseMesh); impulseMesh = null; }
            if (material != null) { DestroyImmediate(material); material = null; }
        }

        private void ReleaseTextures()
        {
            Release(ref stateA);
            Release(ref stateB);
            Release(ref foamA);
            Release(ref foamB);
            Release(ref resolved);
            allocatedResolution = 0;
        }

        private static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        private static void Swap(ref RenderTexture a, ref RenderTexture b)
        {
            RenderTexture t = a; a = b; b = t;
        }

        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            // Only when THIS object is the selection - see the note on UCWaterVolume.
            // Unity runs this for children of the selection too, and the sim usually
            // lives under the water system under the world root.
            if (UnityEditor.Selection.activeGameObject != gameObject) return;
            // Same debug-only gate as the water volume box.
            if (!UCWaterGizmos.ShowDebugBounds) return;
#endif
            if (!originValid) return;

            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.5f);
            Vector3 centre = new Vector3(origin.x + worldSize * 0.5f,
                                         transform.position.y,
                                         origin.y + worldSize * 0.5f);
            Gizmos.DrawWireCube(centre, new Vector3(worldSize, 0.01f, worldSize));
        }
    }
}
