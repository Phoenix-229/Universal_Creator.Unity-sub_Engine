// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWaterMaterials.cs
//  Purpose: The runtime half of water material resolution. Finds a shader that
//           will actually draw, hands out the one shared material for bodies that
//           do not want their own, and writes a WaterProduct onto a material.
//
//  WHY IT IS SEPARATE FROM THE EDITOR APPLIER
//  ------------------------------------------
//  UCWaterApplier does the same job in the editor assembly and can therefore touch
//  assets - TextureRef.Apply writes filterMode and wrapMode onto the texture
//  itself, which is import-time work. A build has no editor assembly and must not
//  do asset work, so the two exist separately and UCSelfTest validates them against
//  each other property by property. That is the only reason for the duplication,
//  and it is checked rather than trusted.
//
//  WHAT THIS FILE REPLACED
//  -----------------------
//  It was WaterBodyApplier, which also built one throwaway water mesh per chunk per
//  body and a world-sized "GlobalOceanPlane" quad. Both were superseded when
//  discrete, editable UCWaterBody objects became the single source of water: the
//  ocean quad was the rectangle that dug a square hole to Y=0, and the per-chunk
//  meshes were the flood slabs that appeared as dozens of lakes with no handles and
//  no carve. The applier had already been made to skip every body it was handed, so
//  what remained was a name that described work it no longer did.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public static class UCWaterMaterials
    {
        // Logged once, not per body: a missing water shader is a project-wide
        // condition, and one warning per lake would bury it.
        private static bool reportedMissingShader;

        // One material for every body that opted out of its own. Lazily built,
        // because a world of hand-placed bodies that all describe themselves never
        // needs it.
        private static Material sharedDefault;

        // Fallback-only tint. Used when UC_Water is missing and a stock URP shader
        // stands in for it with no WaterProduct to colour it - a plausible water blue
        // so the no-product path reads as water rather than the white default.
        private static readonly Color FallbackWaterTint = new Color(0.13f, 0.32f, 0.48f, 1f);

        /// <summary>
        /// The UC_Water shader if present, otherwise a stock URP shader that at least
        /// draws a surface. UC_Water carries no ShaderLab Fallback on purpose - a
        /// Built-in fallback renders magenta under URP and hides where the failure
        /// came from - so the safety net lives here in C#, where it can pick a
        /// pipeline-correct shader and still log loudly. Mirrors the candidate chain
        /// in TerrainBuilder.FallbackMaterial.
        ///
        /// The real cure for a build is UCRenderPipelineSetup.EnsureAlwaysIncludedShaders,
        /// which stops URP stripping UC_Water in the first place. This covers the
        /// residual case (the shader failed to compile) so water is tinted, not white.
        /// </summary>
        public static Shader ResolveWaterShader()
        {
            Shader s = Shader.Find(UCWaterProps.ShaderName);
            if (s != null) return s;

            if (!reportedMissingShader)
            {
                reportedMissingShader = true;
                Debug.LogWarning("[Universe Creator] Shader '" + UCWaterProps.ShaderName +
                                 "' was not found - falling back to a plain URP surface so water " +
                                 "does not render as the white default material. Add it to Always " +
                                 "Included Shaders (Tools > Universe Creator > Setup) to fix builds.");
            }

            // URP shaders only, most water-like first. None is UC_Water, so the
            // HasProperty guards in ApplyProductRuntime skip every UC_Water-only field
            // and only the _BaseColor tint lands - which is what keeps the surface off
            // white. All are pipeline-correct under URP, so none draws magenta.
            string[] candidates =
            {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Unlit",
                "Sprites/Default"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                Shader f = Shader.Find(candidates[i]);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>
        /// The one material shared by every body with "Use Own Material" off. Null
        /// only when no shader could be found at all, in which case the caller has
        /// nothing to draw with either.
        ///
        /// Shared for the reason the tooltip promises: twenty tarns that all look the
        /// same are one draw call, and twenty copies of the same material are twenty.
        /// Bodies that describe their own water get their own material instead, which
        /// is the cost of describing it.
        /// </summary>
        public static Material SharedDefault()
        {
            if (sharedDefault != null) return sharedDefault;

            Shader shader = ResolveWaterShader();
            if (shader == null) return null;

            sharedDefault = new Material(shader);
            sharedDefault.name = "UC Water (shared)";
            sharedDefault.hideFlags = HideFlags.DontSave;

            // A bare stock-URP fallback is opaque white, so tint it. No-op on
            // UC_Water, which has no _BaseColor.
            if (shader.name != UCWaterProps.ShaderName && sharedDefault.HasProperty("_BaseColor"))
                sharedDefault.SetColor("_BaseColor", FallbackWaterTint);

            return sharedDefault;
        }

        /// <summary>
        /// Drops the shared material on a full regenerate or a scene teardown. It is
        /// HideAndDontSave and owned by nothing, so without this it is one leaked
        /// material per domain reload.
        /// </summary>
        public static void ReleaseShared()
        {
            if (sharedDefault == null) return;

            if (Application.isPlaying) Object.Destroy(sharedDefault);
            else Object.DestroyImmediate(sharedDefault);

            sharedDefault = null;
        }

        /// <summary>
        /// Runtime half of UCWaterApplier.ApplyProduct. See the file header for why
        /// the two exist separately; UCSelfTest checks they stay in step.
        /// </summary>
        public static void ApplyProductRuntime(Material m, WaterProduct w)
        {
            if (m == null || w == null) return;

            Set(m, UCWaterProps.ShallowColor, w.shallowColor);
            Set(m, UCWaterProps.DeepColor, w.deepColor);
            Set(m, UCWaterProps.DepthFadeMetres, w.depthFadeMetres);
            Set(m, UCWaterProps.Transparency, w.transparency);
            Set(m, UCWaterProps.Smoothness, w.smoothness);
            Set(m, UCWaterProps.FresnelPower, w.fresnelPower);
            Set(m, UCWaterProps.Refraction, w.refraction);
            Set(m, UCWaterProps.NormalScale, w.normalScale);
            Set(m, UCWaterProps.WaveTileMetres, w.waveTileMetres);

            m.SetVector(UCWaterProps.FlowSpeed,
                new Vector4(w.flowMetresPerSecond.x, w.flowMetresPerSecond.y, 0f, 0f));

            Set(m, UCWaterProps.FoamEnabled, w.foamEnabled ? 1f : 0f);
            Set(m, UCWaterProps.FoamColor, w.foamColor);
            Set(m, UCWaterProps.FoamIntensity, w.foamIntensity);
            Set(m, UCWaterProps.FoamEdgeWidth, w.foamEdgeWidthMetres);
            Set(m, UCWaterProps.FoamScrollSpeed, w.foamScrollSpeed);
            Set(m, UCWaterProps.FoamTileMetres, w.foamTileMetres);

            Set(m, UCWaterProps.CausticsEnabled, w.causticsEnabled ? 1f : 0f);
            Set(m, UCWaterProps.CausticsColor, w.causticsColor);
            Set(m, UCWaterProps.CausticsIntensity, w.causticsIntensity);
            Set(m, UCWaterProps.CausticsTileMetres, w.causticsTileMetres);
            Set(m, UCWaterProps.CausticsSpeed, w.causticsSpeed);
            Set(m, UCWaterProps.CausticsDepthFade, w.causticsDepthFadeMetres);

            Set(m, UCWaterProps.WaveHeight, w.waveHeightMetres);
            Set(m, UCWaterProps.WaveLength, w.waveLengthMetres);
            Set(m, UCWaterProps.WaveSpeed, w.waveSpeed);

            // The shader half of the shoreline: geometry closes the gap at the bank
            // (ShorelineConformer), and these two soften it against the scene depth so
            // the closure is invisible rather than merely watertight.
            Set(m, UCWaterProps.EdgeSoftenMetres, w.edgeSoftenMetres);

            Set(m, UCWaterProps.RippleStrength, w.rippleStrength);
            Set(m, UCWaterProps.RippleFoam, w.rippleFoam);

            // Textures, but not TextureRef.Apply(). Assigning a texture to a material
            // is a plain reference write; Apply() writes filterMode and wrapMode onto
            // the texture asset itself, which is import-time work this side must never
            // do. Without these three lines a hand-placed body's wave normal map,
            // foam and caustics were silently dropped at runtime - the editor applier
            // set them, so it only went wrong in a build.
            SetTex(m, UCWaterProps.WaveNormalMap, w.normalMap);
            SetTex(m, UCWaterProps.FoamTexture, w.foamTexture);
            SetTex(m, UCWaterProps.CausticsTexture, w.causticsTexture);

            // If UC_Water was missing and this is a stock URP fallback shader (see
            // ResolveWaterShader), tint its base colour so the surface reads as water
            // rather than the opaque white default. Guarded, so it is a no-op on
            // UC_Water, which has no _BaseColor.
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", w.shallowColor);
        }

        private static void SetTex(Material m, string name, TextureRef reference)
        {
            if (!m.HasProperty(name)) return;
            if (reference == null || reference.texture == null) return;
            m.SetTexture(name, reference.texture);
        }

        private static void Set(Material m, string name, float v)
        {
            if (m.HasProperty(name)) m.SetFloat(name, v);
        }

        private static void Set(Material m, string name, Color v)
        {
            if (m.HasProperty(name)) m.SetColor(name, v);
        }
    }
}
