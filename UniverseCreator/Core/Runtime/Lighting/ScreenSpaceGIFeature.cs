// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ScreenSpaceGIFeature.cs
//  Purpose: One bounce of indirect light, traced in screen space at half
//           resolution - the local colour bleed a valley floor takes from its
//           walls, the green a canopy throws onto the ground beneath it.
//
//  THE FALLBACK IS THE WHOLE DESIGN (S-51)
//  ---------------------------------------
//  A screen-space ray only knows what is on screen. When it finds no hit -
//  off-screen, behind geometry, past the march - it does NOT go black and it
//  does NOT read a flat constant: it falls back to the irradiance grid
//  (UCSampleIrradiance, S-40), the same value the base tier already lit that
//  point with. So this pass composites a DELTA - (screen bounce - grid value) -
//  which is exactly zero wherever the ray missed. Turning SSGI off therefore
//  removes local detail and never changes overall brightness or colour balance
//  (S-52): the grid was always carrying the floor, screen space only refines it
//  where it has the information to.
//
//  HALF RESOLUTION + REPROJECTION
//  ------------------------------
//  Trace and blur run at half resolution (S-13's discipline). A ping-pong pair
//  of history buffers carries the accumulated result across frames, reprojected
//  through the previous frame's view-projection; a depth mismatch on reprojection
//  rejects the history for that pixel, so a moving camera cannot smear indirect
//  light behind it (which reads worse than no SSGI at all).
//
//  GUARDED (S-54): the whole file is behind UC_URP, and with the pass material
//  or the history buffers unavailable the feature enqueues nothing - the frame
//  renders on the grid alone, which is a correct image, not a broken one.
// -----------------------------------------------------------------------------

#if UC_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Half-resolution screen-space GI (trace -> blur -> composite) that falls back
    /// to the irradiance grid on any ray miss and composites only the delta over it.
    /// Fails to a no-op whenever its material or history buffers are unavailable.
    /// </summary>
    [DisallowMultipleRendererFeature("UC Screen Space GI")]
    public sealed class ScreenSpaceGIFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            [Tooltip("Hemisphere rays traced per pixel. 8 is the authored default.")]
            [Range(1, 16)] public int ssgiSampleCount = 8;

            [Tooltip("How much reprojected history to keep each frame (0..1). 0.9 is " +
                     "the authored default; higher is smoother but slower to react.")]
            [Range(0f, 0.98f)] public float ssgiTemporalWeight = 0.9f;

            [Tooltip("World-space march length for each ray, in metres.")]
            [Range(1f, 30f)] public float marchDistance = 8.0f;

            [Tooltip("Strength of the composited indirect delta.")]
            [Range(0f, 2f)] public float intensity = 1.0f;
        }

        public Settings settings = new Settings();

        public const string ShaderName = "Hidden/UniverseCreator/SSGI";

        private Material m_Material;
        private ScreenSpaceGIPass m_Pass;

        public override void Create()
        {
            m_Pass = new ScreenSpaceGIPass();
            // After opaque colour exists (we sample it as the bounce source) but
            // before transparents, so transparent surfaces are lit by the result.
            m_Pass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            TryLoadMaterial();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!TryLoadMaterial()) return;

            ref CameraData cam = ref renderingData.cameraData;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;

            m_Pass.Setup(m_Material, settings);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        private bool TryLoadMaterial()
        {
            if (m_Material != null) return true;
            Shader s = Shader.Find(ShaderName);
            if (s == null) return false;
            m_Material = CoreUtils.CreateEngineMaterial(s);
            return m_Material != null;
        }

        /// <summary>
        /// Trace (half-res delta) -> blur+temporal (accumulate into ping-pong history)
        /// -> composite (upsample the delta onto the lit colour). History carries the
        /// accumulated indirect across frames and is reprojected through last frame's
        /// view-projection; a depth mismatch on reprojection rejects it for that pixel.
        /// </summary>
        private sealed class ScreenSpaceGIPass : ScriptableRenderPass
        {
            private Material m_Material;
            private int m_SampleCount = 8;
            private float m_TemporalWeight = 0.9f;
            private float m_MarchDistance = 8f;
            private float m_Intensity = 1f;

            // Ping-pong history: read [prev], write [cur], swap each frame. rgb = the
            // accumulated indirect delta, a = the eye depth it was accumulated at (so
            // the reprojection can detect a disocclusion and drop the stale sample).
            private readonly RTHandle[] m_History = new RTHandle[2];
            private int m_Cur;
            private bool m_HistoryValid;
            private Matrix4x4 m_PrevViewProj = Matrix4x4.identity;

            private static readonly int s_Params      = Shader.PropertyToID("_UCSSGIParams");
            private static readonly int s_HalfTexel    = Shader.PropertyToID("_UCSSGIHalfTexel");
            private static readonly int s_PrevViewProj = Shader.PropertyToID("_UCSSGIPrevViewProj");
            private static readonly int s_HistoryValid = Shader.PropertyToID("_UCSSGIHistoryValid");
            private static readonly int s_TraceTex     = Shader.PropertyToID("_UCSSGITrace");
            private static readonly int s_HistoryTex   = Shader.PropertyToID("_UCSSGIHistory");
            private static readonly int s_GITex        = Shader.PropertyToID("_UCSSGIResult");

            public void Setup(Material material, Settings s)
            {
                m_Material      = material;
                m_SampleCount   = Mathf.Clamp(s.ssgiSampleCount, 1, 16);
                m_TemporalWeight = Mathf.Clamp(s.ssgiTemporalWeight, 0f, 0.98f);
                m_MarchDistance = Mathf.Clamp(s.marchDistance, 1f, 30f);
                m_Intensity     = Mathf.Clamp(s.intensity, 0f, 2f);

                // The trace samples opaque colour (the bounce source), scene depth and
                // scene normals; request all three so URP produces them for us.
                ConfigureInput(ScriptableRenderPassInput.Color |
                               ScriptableRenderPassInput.Depth |
                               ScriptableRenderPassInput.Normal |
                               ScriptableRenderPassInput.Motion);   // S-80: object+camera reprojection
            }

            public void Dispose()
            {
                m_History[0]?.Release();
                m_History[1]?.Release();
                m_History[0] = null;
                m_History[1] = null;
                m_HistoryValid = false;
            }

            private class PassData
            {
                public Material material;
                public int passIndex;
                public Vector4 giParams;      // x=sampleCount, y=marchDistance, z=intensity, w=temporalWeight
                public Vector4 halfTexel;      // xy=1/size, zw=size (half-res)
                public Matrix4x4 prevViewProj; // last frame's view-projection, for reprojection
                public float historyValid;     // 0 first frame / after a resize, 1 otherwise
                public TextureHandle history;  // prev-frame history, bound for the temporal read
            }

            // Half-resolution RGBA16F: signed HDR so the delta (bounce - grid) may go
            // negative, alpha carries the eye depth the sample was accumulated at.
            private static void EnsureHistory(ref RTHandle handle, in RenderTextureDescriptor cameraDesc)
            {
                var desc = cameraDesc;
                desc.width  = Mathf.Max(1, cameraDesc.width  / 2);
                desc.height = Mathf.Max(1, cameraDesc.height / 2);
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.enableRandomWrite = false;
                RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_UCSSGIHistory");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Material == null) return;

                var cameraData   = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                TextureHandle color   = resourceData.activeColorTexture;
                TextureHandle depth   = resourceData.cameraDepthTexture;
                if (!color.IsValid() || !depth.IsValid()) return;   // no-op: grid alone lights the frame

                RenderTextureDescriptor camDesc = cameraData.cameraTargetDescriptor;
                int halfW = Mathf.Max(1, camDesc.width  / 2);
                int halfH = Mathf.Max(1, camDesc.height / 2);

                // Ping-pong the persistent history. A resize invalidates it: the old
                // texels no longer map to the new grid, so we reject rather than smear.
                int prev = m_Cur;
                int cur  = m_Cur ^ 1;
                bool sizeChanged =
                    m_History[cur] == null ||
                    m_History[cur].rt == null ||
                    m_History[cur].rt.width != halfW || m_History[cur].rt.height != halfH;
                EnsureHistory(ref m_History[prev], camDesc);
                EnsureHistory(ref m_History[cur],  camDesc);
                if (sizeChanged) m_HistoryValid = false;

                TextureHandle historyPrev = renderGraph.ImportTexture(m_History[prev]);
                TextureHandle historyCur  = renderGraph.ImportTexture(m_History[cur]);

                // Half-res transient trace target (signed HDR, matches the history).
                var traceDesc = new RenderTextureDescriptor(halfW, halfH,
                    GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None) { msaaSamples = 1 };
                TextureHandle trace = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, traceDesc, "_UCSSGITraceRT", false);

                Vector4 giParams  = new Vector4(m_SampleCount, m_MarchDistance, m_Intensity, m_TemporalWeight);
                Vector4 halfTexel = new Vector4(1f / halfW, 1f / halfH, halfW, halfH);
                float historyValid = m_HistoryValid ? 1f : 0f;

                // Pass 0 - TRACE. One indirect bounce in screen space at half res.
                // On a miss it returns UCSampleIrradiance (the grid) so the composited
                // delta (bounce - grid) is exactly zero there. UseAllGlobalTextures so
                // the grid globals and _CameraOpaqueTexture resolve inside the shader.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC SSGI Trace", out var d))
                {
                    d.material = m_Material; d.passIndex = 0;
                    d.giParams = giParams; d.halfTexel = halfTexel;
                    d.prevViewProj = m_PrevViewProj; d.historyValid = historyValid;
                    b.SetRenderAttachment(trace, 0, AccessFlags.Write);
                    b.UseTexture(depth, AccessFlags.Read);
                    b.UseAllGlobalTextures(true);
                    b.AllowGlobalStateModification(true);
                    b.SetGlobalTextureAfterPass(trace, s_TraceTex);
                    b.SetRenderFunc((PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }

                // Pass 1 - BLUR + TEMPORAL. Bilateral blur of the trace, then blend with
                // the reprojected history; a depth mismatch on reprojection rejects it.
                // Writes the accumulated delta into this frame's history half of the pair.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC SSGI Blur+Temporal", out var d))
                {
                    d.material = m_Material; d.passIndex = 1;
                    d.giParams = giParams; d.halfTexel = halfTexel;
                    d.prevViewProj = m_PrevViewProj; d.historyValid = historyValid;
                    d.history = historyPrev;
                    b.SetRenderAttachment(historyCur, 0, AccessFlags.Write);
                    b.UseTexture(depth, AccessFlags.Read);
                    b.UseTexture(historyPrev, AccessFlags.Read);
                    b.UseAllGlobalTextures(true);
                    b.AllowGlobalStateModification(true);
                    b.SetGlobalTextureAfterPass(historyCur, s_GITex);
                    b.SetRenderFunc((PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }

                // Pass 2 - COMPOSITE. Upsample the accumulated delta and add it onto the
                // lit colour. Additive over the grid baseline: toggling SSGI changes only
                // local detail, never overall brightness or colour balance (S-52).
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC SSGI Composite", out var d))
                {
                    d.material = m_Material; d.passIndex = 2;
                    d.giParams = giParams; d.halfTexel = halfTexel;
                    d.prevViewProj = m_PrevViewProj; d.historyValid = historyValid;
                    b.SetRenderAttachment(color, 0, AccessFlags.Write);
                    b.UseTexture(depth, AccessFlags.Read);
                    b.UseAllGlobalTextures(true);
                    b.SetRenderFunc((PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }

                // Bookkeeping for next frame: remember this frame's VP for reprojection,
                // swap the history pair, and mark it valid now that it holds a frame.
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                m_PrevViewProj = gpuProj * cameraData.GetViewMatrix();
                m_Cur = cur;
                m_HistoryValid = true;
            }

            // Every pass is the same fullscreen triangle through the shared material;
            // the pass index selects trace / blur+temporal / composite. Globals are set
            // here so the shader reads one consistent parameter set across all three.
            private static void Exec(PassData data, RasterGraphContext ctx)
            {
                var cmd = ctx.cmd;
                cmd.SetGlobalVector(s_Params, data.giParams);
                cmd.SetGlobalVector(s_HalfTexel, data.halfTexel);
                cmd.SetGlobalFloat(s_HistoryValid, data.historyValid);
                cmd.SetGlobalMatrix(s_PrevViewProj, data.prevViewProj);
                if (data.history.IsValid())
                    cmd.SetGlobalTexture(s_HistoryTex, data.history);
                cmd.DrawProcedural(Matrix4x4.identity, data.material, data.passIndex,
                                   MeshTopology.Triangles, 3, 1);
            }
        }
    }
}
#endif
