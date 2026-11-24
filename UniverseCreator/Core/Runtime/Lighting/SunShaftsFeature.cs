// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SunShaftsFeature.cs
//  Purpose: The consumer for the sun-shaft globals the lighting driver publishes.
//           UCLightingSystemManager.Driver sets _UCShaftIntensity (SunShafts.Intensity01,
//           a coverage x low-sun product) and _UCShaftColor each frame; without this feature
//           those globals have no reader and the crepuscular-ray shader never runs. This
//           registers UC_SunShafts.shader as a fullscreen pass that smears the scene's bright
//           pixels toward the sun's screen position - the screen-space radial term, never a
//           volumetric march.
//
//  ZERO-REGRESSION, SELF-GATING (the whole go-live contract)
//  ---------------------------------------------------------
//  The driver publishes zero intensity for a clear sky, a solid overcast and a high sun, and
//  publishes nothing at all when the lighting manager is not running. This feature reads that
//  global in AddRenderPasses and, when it is <= 0, ENQUEUES NOTHING - so a scene with no shaft
//  to draw pays nothing, and registering the feature can never change an image that had no
//  shafts. Like the sibling screen-space features it is registered by the setup path and
//  self-gates; unlike them its gate is the published intensity, not a tier.
//
//  WHAT HEADLESS CANNOT CLAIM
//  --------------------------
//  The sun's screen UV is projected from _UCSunDir and the camera matrices here; the exact
//  y-convention of that projection, and the look of the smear, are a live-editor sign-off (the
//  render go-live follow-up). The feature compiles, registers and self-gates headlessly; it
//  does not claim its on-screen result.
//
//  GUARDED (UC_URP): the type only exists with URP present. With the pass shader missing it
//  resolves no material and enqueues nothing - a correct image without shafts, not a broken one.
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
    /// Screen-space crepuscular rays: a fullscreen radial smear of the scene's bright pixels
    /// toward the sun's screen position, scaled by the driver-published _UCShaftIntensity and
    /// _UCShaftColor. Enqueues nothing when the published intensity is zero (clear sky, overcast,
    /// high sun, or no lighting driver), so it is zero-cost and zero-regression until a shaft
    /// actually exists. Fails to a no-op whenever the pass shader is unavailable.
    /// </summary>
    [DisallowMultipleRendererFeature("UC Sun Shafts")]
    public sealed class SunShaftsFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            [Tooltip("Per-step falloff along the ray (0..1). Higher reaches farther from the " +
                     "sun; 0.95 is the authored default.")]
            [Range(0.5f, 0.99f)] public float decay = 0.95f;
        }

        public Settings settings = new Settings();

        // Authored under its own name (not Hidden/) exactly as the shader declares it.
        public const string ShaderName = "UniverseCreator/SunShafts";

        private Material m_Material;
        private SunShaftsPass m_Pass;

        private static readonly int s_ShaftIntensityId = Shader.PropertyToID("_UCShaftIntensity");

        public override void Create()
        {
            m_Pass = new SunShaftsPass();
            // After the lit scene and transparents exist, before post, so the rays smear the
            // final HDR colour and the tonemapper then compresses them like any other light.
            m_Pass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            TryLoadMaterial();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!TryLoadMaterial()) return;   // no shader -> enqueue nothing, image unchanged

            ref CameraData cam = ref renderingData.cameraData;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;

            // Self-gate: the driver publishes a non-zero intensity only for a low sun through
            // broken cloud. Zero (the default, and every other sky) means no shaft to draw, so
            // enqueue nothing - registration is then free on every frame that has no rays.
            if (Shader.GetGlobalFloat(s_ShaftIntensityId) <= 0f) return;

            m_Pass.Setup(m_Material, settings);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
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

        private sealed class SunShaftsPass : ScriptableRenderPass
        {
            private static readonly int s_BlitTexId     = Shader.PropertyToID("_BlitTexture");
            private static readonly int s_BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
            private static readonly int s_SunUvId       = Shader.PropertyToID("_UCShaftSunUv");
            private static readonly int s_DecayId       = Shader.PropertyToID("_UCShaftDecay");
            private static readonly int s_SunDirId      = Shader.PropertyToID("_UCSunDir");

            private Material m_Material;
            private float m_Decay = 0.95f;

            public SunShaftsPass() { profilingSampler = new ProfilingSampler("UC Sun Shafts"); }

            public void Setup(Material material, Settings s)
            {
                m_Material = material;
                m_Decay = Mathf.Clamp(s.decay, 0.5f, 0.99f);
            }

            private sealed class PassData
            {
                internal Material material;
                internal int passIndex;
                internal TextureHandle source;
                internal Vector2 sunUv;
                internal float decay;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Material == null) return;

                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

                TextureHandle color = resourceData.activeColorTexture;
                if (!color.IsValid()) return;   // no colour to smear -> no-op

                // The sun's screen UV, projected from the published direction toward the sun. A
                // direction at infinity (w=0 in world); clip.w <= 0 means it is behind the camera,
                // so there is no on-screen origin for the rays and nothing to smear this frame.
                Vector4 sunDir = Shader.GetGlobalVector(s_SunDirId);
                Matrix4x4 vp = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true)
                               * cameraData.GetViewMatrix();
                Vector4 clip = vp * new Vector4(sunDir.x, sunDir.y, sunDir.z, 0f);
                if (clip.w <= 1e-5f) return;
                Vector2 sunUv = new Vector2(clip.x / clip.w, clip.y / clip.w) * 0.5f
                                + new Vector2(0.5f, 0.5f);

                // Ping the camera colour through a transient so the smear reads a stable source
                // and writes the result back - one pass cannot read and write one attachment.
                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                TextureHandle temp = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, "_UCShaftTemp", false);
                if (!temp.IsValid()) return;

                // Pass 0 - smear: read the lit colour, write scene + rays into the transient.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC Sun Shafts", out var d, profilingSampler))
                {
                    d.material = m_Material; d.passIndex = 0; d.source = color;
                    d.sunUv = sunUv; d.decay = m_Decay;
                    b.SetRenderAttachment(temp, 0, AccessFlags.Write);
                    b.UseTexture(color, AccessFlags.Read);
                    b.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }

                // Pass 1 - copy: write the smeared transient back over the camera colour.
                using (var b = renderGraph.AddRasterRenderPass<PassData>("UC Sun Shafts Copy", out var d, profilingSampler))
                {
                    d.material = m_Material; d.passIndex = 1; d.source = temp;
                    d.sunUv = sunUv; d.decay = m_Decay;
                    b.SetRenderAttachment(color, 0, AccessFlags.Write);
                    b.UseTexture(temp, AccessFlags.Read);
                    b.SetRenderFunc(static (PassData data, RasterGraphContext ctx) => Exec(data, ctx));
                }
            }

            private static void Exec(PassData data, RasterGraphContext ctx)
            {
                var cmd = ctx.cmd;
                cmd.SetGlobalVector(s_BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                cmd.SetGlobalTexture(s_BlitTexId, data.source);
                cmd.SetGlobalVector(s_SunUvId, new Vector4(data.sunUv.x, data.sunUv.y, 0f, 0f));
                cmd.SetGlobalFloat(s_DecayId, data.decay);
                cmd.DrawProcedural(Matrix4x4.identity, data.material, data.passIndex,
                                   MeshTopology.Triangles, 3, 1);
            }
        }
    }
}
#endif
