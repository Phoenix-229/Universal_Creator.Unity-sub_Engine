// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ContactShadowFeature.cs
//  Purpose: A screen-space contact-shadow pass that recovers the short, tight
//           darkening a shadow map is too coarse to draw - the line where a rock
//           or a shrub actually meets the ground. It ray-marches the depth buffer
//           a short distance toward the sun; where that march is occluded, the
//           point is in contact shadow.
//
//  HOW IT COMBINES WITH CASCADE SHADOWS (this is the whole point)
//  --------------------------------------------------------------
//  URP's Screen Space Shadows feature writes the sun's cascade attenuation into
//  the global _ScreenSpaceShadowmapTexture (1 = lit, 0 = fully shadowed) just
//  after the prepass. This pass runs immediately after it, reads that texture,
//  and writes back min(cascadeAttenuation, 1 - contactOcclusion). It rebinds the
//  same global name, so every forward-lit surface samples the COMBINED shadow
//  with no shader changes - and because it is a minimum, a pixel already in a
//  cascade shadow is never darkened a second time. That min is the law S-49
//  asks for, made structural rather than hoped for.
//
//  This darkening therefore lands only on the sun's direct term (that is all the
//  screen-space shadow texture gates), never on ambient or sky light - which is
//  what keeps a contact shadow from reading as a smear of dirt.
//
//  GUARDING (so the runtime assembly never depends on this drawing)
//  ----------------------------------------------------------------
//  The whole file is behind UC_URP: with URP absent the type simply is not
//  compiled, and nothing else in the runtime references it. With URP present but
//  the pass material missing (shader stripped from a build), the feature enqueues
//  nothing and the frame renders exactly as it would without contact shadows.
//  A contact shadow is a refinement; its absence is never a failure.
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
    /// Screen-space contact shadows, composited into URP's screen-space shadow
    /// texture as a minimum against the cascade term. Fails to a no-op whenever
    /// the pass material cannot be resolved.
    /// </summary>
    [DisallowMultipleRendererFeature("UC Contact Shadows")]
    public sealed class ContactShadowFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            [Tooltip("How far, in metres, the march reaches toward the sun. Short by " +
                     "design: contact shadows are the near field the cascade cannot resolve.")]
            [Range(0.05f, 2.0f)] public float contactShadowLength = 0.35f;

            [Tooltip("Ray-march steps across that length. 16 is the authored default.")]
            [Range(4, 32)] public int stepCount = 16;

            [Tooltip("Strength of the darkening, 0..1. 1 lets a fully occluded march " +
                     "drive the sun's direct term to black at the contact line.")]
            [Range(0f, 1f)] public float intensity = 1.0f;

            [Tooltip("How much reprojected history to keep each frame (0..1), reined in " +
                     "by a variance clamp so a moving contact shadow cannot ghost. 0.9 is " +
                     "the authored default; higher is steadier but slower to react.")]
            [Range(0f, 0.98f)] public float temporalWeight = 0.9f;
        }

        public Settings settings = new Settings();

        // Hidden pass shader. Kept Hidden/ so it is never offered as a surface
        // material; resolved by name and, at build time, protected from stripping
        // by UCShaderDiagnostics' always-included list.
        public const string ShaderName = "Hidden/UniverseCreator/ContactShadow";

        private Material m_Material;
        private LightingCameraPasses<ContactShadowPass> m_Passes;

        public override void Create()
        {
            m_Passes?.Dispose(); // UC-T08-11: recreation retires the previous per-camera native resources.
            m_Passes = new LightingCameraPasses<ContactShadowPass>(() => new ContactShadowPass { renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 2 }, () => this != null && isActive);
            // Right after URP's Screen Space Shadows pass (AfterRenderingPrePasses+1),
            // so the global cascade texture exists to be read and re-minned before
            // any opaque surface samples it.
            TryLoadMaterial();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Guard 1: no material -> enqueue nothing. The frame is unchanged.
            if (!TryLoadMaterial()) return;

            // Guard 2: contact shadows gate the sun's direct term through the
            // screen-space shadow texture; with main-light shadows off there is
            // no such texture to compose into, so there is nothing to do.
            ref CameraData cam = ref renderingData.cameraData;
            if (cam.cameraType == CameraType.Preview) return;
            if (!renderingData.shadowData.supportsMainLightShadows) return;

            var pass = m_Passes?.For(cam.camera); if (pass == null) return;
            var lighting = pass.LightingFor(cam.camera);
            if (lighting == null || !lighting.isActiveAndEnabled || !lighting.lightingEnabled || !lighting.ResolvedTierSettings.contactShadows)
            { pass.Dispose(); return; } // UC-T08-12: release native history and reuse the managed pass when this world's tier disables it.
            pass.Setup(m_Material, settings, lighting.settings.contactShadowLength);
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

        private sealed class ContactShadowPass : ScriptableRenderPass, System.IDisposable
        {
            private static readonly int s_Params      = Shader.PropertyToID("_UCContactShadowParams");
            private static readonly int s_SSShadowId   = Shader.PropertyToID("_ScreenSpaceShadowmapTexture");
            private static readonly int s_OcclusionId  = Shader.PropertyToID("_UCContactOcclusion");
            private static readonly int s_AccumId       = Shader.PropertyToID("_UCContactAccum");
            private static readonly int s_HistoryId     = Shader.PropertyToID("_UCContactHistory");
            private static readonly int s_PrevViewProj  = Shader.PropertyToID("_UCContactPrevViewProj");
            private static readonly int s_HistoryValid  = Shader.PropertyToID("_UCContactHistoryValid");
            private static readonly int s_TemporalWeight = Shader.PropertyToID("_UCContactTemporalWeight");

            private Material m_Material;
            private readonly Settings m_Settings = new Settings(); // UC-T08-12: copied pass-local settings survive sibling camera setup.

            // Ping-pong history: read [prev], write [cur], swap each frame. r = the
            // accumulated occlusion, g = the eye depth it was accumulated at (so the
            // reprojection can detect a disocclusion and drop the stale sample).
            private readonly RTHandle[] m_History = new RTHandle[2];
            private int m_Cur;
            private bool m_HistoryValid;
            private Matrix4x4 m_PrevViewProj = Matrix4x4.identity;
            private readonly LightingHistoryKey historyKey = new LightingHistoryKey();
            internal UCLightingSystemManager LightingFor(Camera camera) => historyKey.LightingFor(camera);

            public ContactShadowPass()
            {
                profilingSampler = new ProfilingSampler("UC Contact Shadows");
            }

            public void Setup(Material material, Settings settings, float contactLength)
            {
                m_Material = material;
                m_Settings.contactShadowLength = Mathf.Clamp(contactLength, 0.05f, 2);
                m_Settings.stepCount = Mathf.Clamp(settings.stepCount, 4, 32);
                m_Settings.intensity = Mathf.Clamp01(settings.intensity);
                m_Settings.temporalWeight = Mathf.Clamp(settings.temporalWeight, 0, 0.98f);
                // World position is reconstructed from depth; motion vectors carry the
                // per-pixel reprojection (camera AND object motion) for the temporal pass.
                ConfigureInput(ScriptableRenderPassInput.Depth |
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

            // Full-resolution RG16F: r = accumulated occlusion, g = the eye depth the
            // sample was accumulated at (the reprojection reads it to reject a disocclusion).
            private static void EnsureHistory(ref RTHandle handle, in RenderTextureDescriptor cameraDesc)
            {
                var desc = cameraDesc;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                desc.enableRandomWrite = false;
                RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_UCContactHistory");
            }

            private sealed class PassData
            {
                internal Material material;
                internal Vector4 marchParams;
                internal int passIndex;
                internal Matrix4x4 prevViewProj; // last frame's view-projection, static reprojection fallback
                internal float historyValid;     // 0 first frame / after a resize, 1 otherwise
                internal float temporalWeight;    // reprojected-history keep factor (0..0.98)
                internal TextureHandle history;   // prev-frame history, bound for the temporal read
            }

            private static void ExecuteDraw(PassData data, RasterGraphContext ctx)
            {
                var cmd = ctx.cmd;
                cmd.SetGlobalVector(s_Params, data.marchParams);
                cmd.SetGlobalMatrix(s_PrevViewProj, data.prevViewProj);
                cmd.SetGlobalFloat(s_HistoryValid, data.historyValid);
                cmd.SetGlobalFloat(s_TemporalWeight, data.temporalWeight);
                if (data.history.IsValid())
                    cmd.SetGlobalTexture(s_HistoryId, data.history);
                // Fullscreen triangle; pass 0 marches, pass 1 accumulates, pass 2 combines.
                cmd.DrawProcedural(Matrix4x4.identity, data.material, data.passIndex,
                    MeshTopology.Triangles, 3, 1);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Material == null) return;

                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.cameraDepthTexture.IsValid()) return;

                // A fresh R8 target of the SAME size/format URP uses for the screen-space
                // shadow texture, given the SAME name so the rebind below replaces the
                // cascade-only texture with the combined one for every later pass.
                var desc = cameraData.cameraTargetDescriptor;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1;
                desc.graphicsFormat = SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.Blend)
                    ? GraphicsFormat.R8_UNorm
                    : GraphicsFormat.B8G8R8A8_UNorm;
                TextureHandle occlusion = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, "_UCContactOcclusion", true);
                TextureHandle combined = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, "_ScreenSpaceShadowmapTexture", true);
                if (!occlusion.IsValid() || !combined.IsValid()) return;

                // Ping-pong the persistent full-res history. A resize invalidates it: the
                // old texels no longer map to the new frame, so we reject rather than smear.
                RenderTextureDescriptor camDesc = cameraData.cameraTargetDescriptor;
                if (camDesc.dimension != TextureDimension.Tex2D || camDesc.volumeDepth > 1) return; // UC-T08-11: cascade shadows remain usable on unsupported XR views.
                if (historyKey.Changed(cameraData.camera, camDesc, cameraData.camera.nonJitteredProjectionMatrix, Time.frameCount,
                    new Vector4(m_Settings.contactShadowLength, m_Settings.stepCount, m_Settings.intensity, m_Settings.temporalWeight))) m_HistoryValid = false;
                int prev = m_Cur;
                int cur  = m_Cur ^ 1;
                bool sizeChanged =
                    m_History[cur] == null ||
                    m_History[cur].rt == null ||
                    m_History[cur].rt.width != camDesc.width || m_History[cur].rt.height != camDesc.height;
                EnsureHistory(ref m_History[prev], camDesc);
                EnsureHistory(ref m_History[cur],  camDesc);
                if (sizeChanged) m_HistoryValid = false;

                TextureHandle historyPrev = renderGraph.ImportTexture(m_History[prev]);
                TextureHandle historyCur  = renderGraph.ImportTexture(m_History[cur]);

                int steps = Mathf.Clamp(m_Settings.stepCount, 4, 32);
                // x = length (m), y = 1/steps, z = steps, w = intensity.
                Vector4 marchParams = new Vector4(
                    m_Settings.contactShadowLength, 1f / steps, steps,
                    Mathf.Clamp01(m_Settings.intensity));
                float temporalWeight = Mathf.Clamp(m_Settings.temporalWeight, 0f, 0.98f);
                float historyValid = m_HistoryValid ? 1f : 0f;

                // Pass 0: march depth toward the sun, writing raw occlusion, and publish
                // it under _UCContactOcclusion for the temporal pass to read.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var pd0, profilingSampler))
                {
                    pd0.material = m_Material;
                    pd0.marchParams = marchParams;
                    pd0.passIndex = 0;
                    pd0.prevViewProj = m_PrevViewProj;
                    pd0.historyValid = historyValid;
                    pd0.temporalWeight = temporalWeight;
                    builder.SetRenderAttachment(occlusion, 0, AccessFlags.Write);
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                    builder.SetGlobalTextureAfterPass(occlusion, s_OcclusionId);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => ExecuteDraw(data, ctx));
                }

                // Pass 1: accumulate the raw march across frames - motion-vector reprojection,
                // a neighbourhood-variance clamp and a disocclusion reject (S-80) - so the
                // dither resolves and a fast pan leaves no ghost. Writes into this frame's
                // history half and rebinds it under _UCContactAccum for the combine.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var pd1, profilingSampler))
                {
                    pd1.material = m_Material;
                    pd1.marchParams = marchParams;
                    pd1.passIndex = 1;
                    pd1.prevViewProj = m_PrevViewProj;
                    pd1.historyValid = historyValid;
                    pd1.temporalWeight = temporalWeight;
                    pd1.history = historyPrev;
                    builder.SetRenderAttachment(historyCur, 0, AccessFlags.Write);
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    builder.UseTexture(historyPrev, AccessFlags.Read);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetGlobalTextureAfterPass(historyCur, s_AccumId);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => ExecuteDraw(data, ctx));
                }

                // Pass 2: combine the accumulated occlusion with the cascade shadow by
                // minimum, rebinding the result under the screen-space shadow name so every
                // later pass samples the combined value.
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var pd2, profilingSampler))
                {
                    pd2.material = m_Material;
                    pd2.marchParams = marchParams;
                    pd2.passIndex = 2;
                    pd2.prevViewProj = m_PrevViewProj;
                    pd2.historyValid = historyValid;
                    pd2.temporalWeight = temporalWeight;
                    builder.SetRenderAttachment(combined, 0, AccessFlags.Write);
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    // The shader samples the _UCContactAccum and cascade
                    // _ScreenSpaceShadowmapTexture globals; declaring a dependency on all
                    // global textures keeps RenderGraph from scheduling this ahead of them.
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetGlobalTextureAfterPass(combined, s_SSShadowId);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => ExecuteDraw(data, ctx));
                }

                // Bookkeeping for next frame: remember this frame's VP for reprojection,
                // swap the history pair, and mark it valid now that it holds a frame.
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                m_PrevViewProj = gpuProj * cameraData.GetViewMatrix();
                m_Cur = cur;
                m_HistoryValid = true;
            }
        }
    }
}
#endif
