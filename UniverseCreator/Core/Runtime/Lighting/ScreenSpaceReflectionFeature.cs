// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ScreenSpaceReflectionFeature.cs
//  Purpose: The flagship's sharp reflections - a wet road that mirrors the building above it,
//           a still puddle that holds the sky and the trees. A hierarchical-depth ray march in
//           screen space at half resolution, with a roughness-aware ray count (a mirror gets one
//           sharp ray, a satin surface a small spread), temporal accumulation with a variance
//           clamp so a moving camera cannot ghost the reflection, and a HARD per-ray step budget
//           so the cost can never run away with scene depth complexity. Ultra tier only.
//
//  THE FALLBACK IS THE WHOLE DESIGN (S-78)
//  ---------------------------------------
//  A screen-space ray only knows what is on screen. When it leaves the screen, finds nothing, or
//  exhausts its step budget, it must NOT return black - a black off-screen reflection reads as a
//  hole and is the classic screen-space artefact. The shader falls back in order: the height-field
//  trace against the terrain, then the irradiance grid for its radiance, then the analytic sky. So
//  turning SSR off (or dropping below Ultra) degrades to terrain-and-sky, never to black, and the
//  lower tiers are byte-identical to before this feature existed because it never enqueues there.
//
//  GUARDED (C-07 / S-54): the whole file is behind UC_URP, and with the pass material or the
//  history buffers unavailable the feature enqueues nothing - the frame renders without SSR, which
//  is a correct image (grid + sky reflections), not a broken one.
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
    /// Half-resolution screen-space reflections (trace -> resolve+temporal -> composite) that fall
    /// back to the height-field trace, the irradiance grid and then the analytic sky on any ray
    /// miss, and fail to a no-op whenever the material or history buffers are unavailable. Ultra
    /// tier only; the driver enables it from <see cref="LightingTierSettings.screenSpaceReflections"/>.
    /// </summary>
    [DisallowMultipleRendererFeature("UC Screen Space Reflections")]
    public sealed class ScreenSpaceReflectionFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            [Tooltip("Hard per-ray step ceiling for the hierarchical-depth march. 64 is the default.")]
            [Range(8, 256)] public int stepBudget = LightingConstants.SsrStepBudgetDefault;

            [Tooltip("How much reprojected history to keep each frame (0..1), reined in by a variance " +
                     "clamp so a moving reflection cannot ghost. 0.9 is the authored default.")]
            [Range(0f, 0.98f)] public float temporalWeight = 0.9f;

            [Tooltip("Strength of the composited reflection over the lit colour.")]
            [Range(0f, 2f)] public float intensity = 1.0f;

            [Tooltip("Surfaces rougher than this get no SSR (they fall back to grid+sky, which is " +
                     "smooth enough to be indistinguishable). Keeps rays off surfaces that cannot " +
                     "show a sharp reflection anyway.")]
            [Range(0f, 1f)] public float maxRoughness = 0.6f;
        }

        public Settings settings = new Settings();

        public const string ShaderName = "Hidden/UniverseCreator/SSR";

        private Material m_Material;
        private LightingCameraPasses<ScreenSpaceReflectionPass> m_Passes;

        public override void Create()
        {
            m_Passes?.Dispose(); // UC-T08-11: renderer recreation retires old native histories first.
            m_Passes = new LightingCameraPasses<ScreenSpaceReflectionPass>(() => new ScreenSpaceReflectionPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents }, () => this != null && isActive);
            // After opaque colour exists (the march samples it as the reflected radiance) but
            // before transparents, so transparent surfaces are lit over the reflected result.
            TryLoadMaterial();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!TryLoadMaterial()) return;   // C-07: no material -> enqueue nothing, grid+sky light the frame

            ref CameraData cam = ref renderingData.cameraData;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;

            var pass = m_Passes?.For(cam.camera); if (pass == null) return; // UC-T08-11: camera overflow retains the existing cheap reflection fallback.
            var lighting = pass.LightingFor(cam.camera);
            if (lighting == null || !lighting.isActiveAndEnabled || !lighting.lightingEnabled || !lighting.ResolvedTierSettings.screenSpaceReflections)
            { pass.Dispose(); return; } // UC-T08-12: retain the small managed pass, release native history without allocating every disabled frame.
            pass.Setup(m_Material, settings);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Passes?.Dispose(); m_Passes = null;
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        private void OnDisable() => Dispose(); // UC-T08-11: asset unload/destruction also releases subscribed owners.

        private bool TryLoadMaterial()
        {
            if (m_Material != null) return true;
            Shader s = Shader.Find(ShaderName);
            if (s == null) return false;
            m_Material = CoreUtils.CreateEngineMaterial(s);
            return m_Material != null;
        }
        /// <summary>
        /// Trace (half-res hierarchical-depth march) -> resolve+temporal (variance-clamped
        /// accumulation into ping-pong history) -> composite (Fresnel-weighted add onto the lit
        /// colour). History carries the accumulated reflection across frames and is reprojected
        /// through last frame's view-projection; a depth mismatch on reprojection rejects it.
        /// </summary>
        private sealed class ScreenSpaceReflectionPass : ScriptableRenderPass, System.IDisposable
        {
            private Material m_Material;
            private int m_StepBudget = LightingConstants.SsrStepBudgetDefault;
            private float m_TemporalWeight = 0.9f;
            private float m_Intensity = 1f;
            private float m_MaxRoughness = 0.6f;

            // Ping-pong history: read [prev], write [cur], swap each frame. rgb = the
            // accumulated reflected radiance, a = the eye depth it was accumulated at (so
            // the reprojection can detect a disocclusion and drop the stale sample).
            private readonly RTHandle[] m_History = new RTHandle[2];
            private int m_Cur;
            private bool m_HistoryValid;
            private Matrix4x4 m_PrevViewProj = Matrix4x4.identity;
            private readonly LightingHistoryKey historyKey = new LightingHistoryKey();
            internal UCLightingSystemManager LightingFor(Camera camera) => historyKey.LightingFor(camera);

            private static readonly int s_Params       = Shader.PropertyToID("_UCSSRParams");
            private static readonly int s_HalfTexel     = Shader.PropertyToID("_UCSSRHalfTexel");
            private static readonly int s_PrevViewProj  = Shader.PropertyToID("_UCSSRPrevViewProj");
            private static readonly int s_HistoryValid  = Shader.PropertyToID("_UCSSRHistoryValid");
            private static readonly int s_TraceTex      = Shader.PropertyToID("_UCSSRTrace");
            private static readonly int s_HistoryTex    = Shader.PropertyToID("_UCSSRHistory");
            private static readonly int s_ResultTex     = Shader.PropertyToID("_UCSSRResult");
            private static readonly int s_SurfaceTex    = Shader.PropertyToID("_UCSSRSurface");
            private static readonly ShaderTagId s_SurfaceTag = new ShaderTagId("UCReflectionMask");

            public void Setup(Material material, Settings s)
            {
                m_Material       = material;
                m_StepBudget     = Mathf.Clamp(s.stepBudget, 8, 256);
                m_TemporalWeight = Mathf.Clamp(s.temporalWeight, 0f, 0.98f);
                m_Intensity      = Mathf.Clamp(s.intensity, 0f, 2f);
                m_MaxRoughness   = Mathf.Clamp01(s.maxRoughness);

                // The march samples opaque colour (the reflected radiance), scene depth
                // (the hierarchical-depth march) and scene normals (the reflect vector);
                // request all three so URP produces them for us.
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
                public Vector4 ssrParams;      // x=stepBudget, y=intensity, z=temporalWeight, w=maxRoughness
                public Vector4 halfTexel;      // xy=1/size, zw=size (half-res)
                public Matrix4x4 prevViewProj; // last frame's view-projection, for reprojection
                public float historyValid;     // 0 first frame / after a resize, 1 otherwise
                public TextureHandle history;  // prev-frame history, bound for the temporal read
            }

            private class SurfacePassData
            {
                public RendererListHandle renderers;
            }

            // Half-resolution RGBA16F: HDR reflected radiance in rgb, alpha carries the eye
            // depth the sample was accumulated at (the reprojection reads it to reject).
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
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_UCSSRHistory");
            }
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Material == null) return;

                var cameraData   = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                TextureHandle color = resourceData.activeColorTexture;
                TextureHandle depth = resourceData.cameraDepthTexture;
                if (!color.IsValid() || !depth.IsValid()) return;   // no-op: grid+sky light the frame

                RenderTextureDescriptor camDesc = cameraData.cameraTargetDescriptor;
                if (camDesc.dimension != TextureDimension.Tex2D || camDesc.volumeDepth > 1) return; // Explicit mono-camera fallback; XR histories require a separate verified view contract.
                if (!resourceData.activeDepthTexture.IsValid()) return;

                // UC midday repair: normals alpha has no roughness contract in URP. Only material-owned passes publish eligibility.
                // Full-resolution RG8 is transient and Ultra-only; share camera depth/MSAA and preserve sharp dry/wet boundaries.
                var surfaceDesc = camDesc;
                surfaceDesc.depthBufferBits = 0; surfaceDesc.graphicsFormat = GraphicsFormat.R8G8_UNorm;
                surfaceDesc.bindMS = false;
                var surface = UniversalRenderer.CreateRenderGraphTexture(renderGraph, surfaceDesc, "_UCSSRSurface", true);
                var rendering = frameData.Get<UniversalRenderingData>();
                var drawing = RenderingUtils.CreateDrawingSettings(s_SurfaceTag, rendering, cameraData,
                    frameData.Get<UniversalLightData>(), cameraData.defaultOpaqueSortFlags);
                var filtering = new FilteringSettings(RenderQueueRange.opaque, cameraData.camera.cullingMask);
                var renderers = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filtering));
                using (var b = renderGraph.AddRasterRenderPass<SurfacePassData>("UC SSR Surface", out var d))
                {
                    d.renderers = renderers;
                    b.SetRenderAttachment(surface, 0, AccessFlags.Write);
                    b.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                    b.UseRendererList(renderers); b.UseAllGlobalTextures(true);
                    b.SetGlobalTextureAfterPass(surface, s_SurfaceTex);
                    b.SetRenderFunc((SurfacePassData data, RasterGraphContext ctx) => ctx.cmd.DrawRendererList(data.renderers));
                }
                if (historyKey.Changed(cameraData.camera, camDesc, cameraData.camera.nonJitteredProjectionMatrix, Time.frameCount,
                    new Vector4(m_StepBudget, m_Intensity, m_TemporalWeight, m_MaxRoughness))) m_HistoryValid = false;
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

                // Half-res transient trace target (HDR, matches the history).
                var traceDesc = new RenderTextureDescriptor(halfW, halfH,
                    GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None) { msaaSamples = 1 };
                TextureHandle trace = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, traceDesc, "_UCSSRTraceRT", false);

                Vector4 ssrParams = new Vector4(m_StepBudget, m_Intensity, m_TemporalWeight, m_MaxRoughness);
                Vector4 halfTexel = new Vector4(1f / halfW, 1f / halfH, halfW, halfH);
                float historyValid = m_HistoryValid ? 1f : 0f;
                // Pass 0 - TRACE. A hierarchical-depth march per pixel at half res, with a
                // roughness-aware ray count (a mirror gets one sharp ray, a satin surface a
                // small spread) and the HARD m_StepBudget ceiling. A surface rougher than
                // m_MaxRoughness is skipped entirely (it cannot show a sharp reflection). On a
                // miss - off-screen, no hit, or budget exhausted - the shader falls back to the
                // height-field trace, the grid and then the sky (S-78), never to black.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC SSR Trace", out var d))
                {
                    d.material = m_Material; d.passIndex = 0;
                    d.ssrParams = ssrParams; d.halfTexel = halfTexel;
                    d.prevViewProj = m_PrevViewProj; d.historyValid = historyValid;
                    b.SetRenderAttachment(trace, 0, AccessFlags.Write);
                    b.UseTexture(surface, AccessFlags.Read);
                    b.UseTexture(depth, AccessFlags.Read);
                    b.UseAllGlobalTextures(true);
                    b.AllowGlobalStateModification(true);
                    b.SetGlobalTextureAfterPass(trace, s_TraceTex);
                    b.SetRenderFunc((PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }

                // Pass 1 - RESOLVE + TEMPORAL. Blend the trace with the reprojected history
                // under a variance clamp (the history is clamped to the neighbourhood mean +/-
                // its variance, so a moving reflection cannot ghost); a depth mismatch on
                // reprojection rejects it outright. Writes into this frame's history half.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC SSR Resolve+Temporal", out var d))
                {
                    d.material = m_Material; d.passIndex = 1;
                    d.ssrParams = ssrParams; d.halfTexel = halfTexel;
                    d.prevViewProj = m_PrevViewProj; d.historyValid = historyValid;
                    d.history = historyPrev;
                    b.SetRenderAttachment(historyCur, 0, AccessFlags.Write);
                    b.UseTexture(depth, AccessFlags.Read);
                    b.UseTexture(historyPrev, AccessFlags.Read);
                    b.UseAllGlobalTextures(true);
                    b.AllowGlobalStateModification(true);
                    b.SetGlobalTextureAfterPass(historyCur, s_ResultTex);
                    b.SetRenderFunc((PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }

                // Pass 2 - COMPOSITE. Upsample the accumulated reflection and add it onto the
                // lit colour, weighted by the Fresnel-and-wetness term (S-76): a grazing wet
                // road reflects strongly, a dry matte surface not at all. Additive, so on any
                // pixel the trace missed the added reflection is the fallback radiance, not a hole.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC SSR Composite", out var d))
                {
                    d.material = m_Material; d.passIndex = 2;
                    d.ssrParams = ssrParams; d.halfTexel = halfTexel;
                    d.prevViewProj = m_PrevViewProj; d.historyValid = historyValid;
                    b.SetRenderAttachment(color, 0, AccessFlags.Write);
                    b.UseTexture(surface, AccessFlags.Read);
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

            // Every pass is the same fullscreen triangle through the shared material; the pass
            // index selects trace / resolve+temporal / composite. Globals are set here so the
            // shader reads one consistent parameter set across all three.
            private static void Exec(PassData data, RasterGraphContext ctx)
            {
                var cmd = ctx.cmd;
                cmd.SetGlobalVector(s_Params, data.ssrParams);
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
