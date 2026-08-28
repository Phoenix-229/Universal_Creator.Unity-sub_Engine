// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LightingTier.cs
//  Purpose: The ONE place the product's quality tier turns into lighting settings.
//           Every lighting cost lever - screen-space GI, contact shadows, shadow
//           distance and cascades, probe density - is resolved here from the single
//           product tier enum (AdaptiveQuality.QualityTier), so there is never a
//           second tier ladder to drift out of step with the rest of the engine.
//
//  NOT A SECOND ENUM
//  -----------------
//  QualityTier lives in UniverseCreator.Optimization.AdaptiveQuality and is shared by
//  vegetation, props and effects. This file keys off THAT enum - Ultra/High/Medium/Low
//  - and returns a settings block per tier. "Mid-range" in the design tables is the
//  Medium tier; "flagship" is Ultra. The mapping is data, authored once, right here.
//
//  WHY MEDIUM DROPS SCREEN SPACE GI BUT KEEPS CONTACT SHADOWS
//  ----------------------------------------------------------
//  Screen-space GI is a refinement over the irradiance grid (it can be off and the
//  frame stays correct, S-52), so it is the first thing to go on constrained hardware.
//  Contact shadows are kept at Medium precisely because that tier also softens or drops
//  cascade soft-shadowing - the contact term compensates for the harder cascade edge at
//  almost no cost (S-55). Low drops both: the grid and cloud shadows carry the frame.
// -----------------------------------------------------------------------------

using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>Soft-shadow strength a tier asks the pipeline for.</summary>
    public enum SoftShadowLevel { Off = 0, Low = 1, Medium = 2, High = 3 }

    /// <summary>
    /// The resolved lighting settings for one quality tier. A plain value block - the
    /// lighting driver (§ 8.12) reads it and applies each field to the URP asset, the
    /// renderer features and the probe scheduler. No behaviour, no second source of truth.
    /// </summary>
    public struct LightingTierSettings
    {
        // The T1.5 rung (§ 8.14): multi-bounce, sky visibility, bent normal and foliage
        // colour bleed. All four are computed CPU-side (the probe fill and bake) and read
        // shader-side - NONE of them turns on a renderer feature - so the whole rung runs
        // on Mobile High. It is the largest visual gain per millisecond in the ladder, and
        // it is optional: with the four off, the plain grid (T1) is still a complete image.
        public bool  multiBounce;          // S-67 feedback pass in the probe fill
        public bool  skyVisibility;        // S-71 open-sky fraction per probe
        public bool  bentNormal;           // S-71 bent normal per probe
        public bool  foliageBleed;         // S-72 canopy colour bleed onto the floor

        // The ray-free wet-sky environment reflection (S-79/S-81). This is the single value
        // the driver publishes to the _UCEnvReflectionStrength shader global. Per the S-83 budget
        // it is a baseline on EVERY device class (sky + wetness + water plane, ~0.1 ms even on
        // Mobile Low) - it is Fresnel x wetness weighted, so it costs and shows nothing on a dry
        // surface and only lights a wet one, which is tier-independent physics. Higher tiers add
        // SSGI specular and SSR on TOP of it, not more of it. The uniform's own 0 default is the
        // pre-manager safe state (no manager in scene -> the term stays inert).
        public float envReflectionStrength;

        // Screen-space refinements (additive over the base tier; safe to disable).
        public bool  screenSpaceGI;        // S-50 feature enabled
        public int   ssgiSampleCount;      // rays per pixel when enabled
        public bool  contactShadows;       // S-48 feature enabled
        public bool  screenSpaceReflections; // S-77 feature enabled (Ultra/T3 only)

        // Cascaded sun shadows (S-55 policy).
        public int   cascadeCount;         // 2 or 3
        public float shadowDistance;       // metres
        public int   shadowAtlasResolution;// square atlas edge, texels
        public SoftShadowLevel softShadows;

        // Shadow bias (S-57). URP stores one depth+normal bias pair and scales it per
        // cascade by that cascade's texel world size at runtime - so a coarser atlas over
        // a given distance means bigger texels means more self-shadow acne, which the
        // NORMAL bias offsets (it pushes the sample along the surface normal, the exact
        // fix for grazing-angle terrain self-shadowing at sunrise). Depth bias handles the
        // rest. Both grow as the tier's texels coarsen; too much of either is peter-panning,
        // so the flagship - with the finest texels - carries the least.
        public float shadowDepthBias;      // URP m_ShadowDepthBias, base (URP scales per cascade)
        public float shadowNormalBias;     // URP m_ShadowNormalBias, base (URP scales per cascade)

        // Irradiance grid density: a multiplier on the authored base spacing. >1 spreads
        // probes out (cheaper, coarser); 1 is the authored density. Never below 1 here -
        // densification is a per-region author choice (LightingProbeVolume), not a tier.
        public float probeSpacingScale;

        /// <summary>True if any screen-space lighting effect is on for this tier. The
        /// S-52 "all screen-space off" checklist preset is simply the tier where this is
        /// false (Low), which must still be a complete, shippable image.</summary>
        public bool AnyScreenSpaceEffect { get { return screenSpaceGI || contactShadows || screenSpaceReflections; } }

        /// <summary>True when this tier runs the T1.5 rung - the four cheap, renderer-feature-free
        /// § 8.14 refinements (multi-bounce, sky visibility, bent normal, foliage bleed) that ship
        /// as a set from Mobile High up. The wet-sky reflection is NOT part of this gate: it is a
        /// baseline on every tier (S-83), so a Low-tier wet surface still glosses.</summary>
        public bool RunsMultiBounceRung { get { return multiBounce && skyVisibility && bentNormal && foliageBleed; } }
    }

    /// <summary>
    /// Resolves <see cref="AdaptiveQuality.QualityTier"/> to a <see cref="LightingTierSettings"/>.
    /// Static and pure: the same tier always yields the same settings, so the driver and
    /// the debug overlay never disagree about what "in force" means.
    /// </summary>
    public static class LightingTier
    {
        /// <summary>The per-tier table. Flagship = Ultra, mid-range = Medium (design § 8.11).</summary>
        public static LightingTierSettings Resolve(AdaptiveQuality.QualityTier tier)
        {
            switch (tier)
            {
                case AdaptiveQuality.QualityTier.Ultra:   // flagship
                    return new LightingTierSettings
                    {
                        multiBounce = true, skyVisibility = true, bentNormal = true, foliageBleed = true,
                        envReflectionStrength = 1.0f,
                        screenSpaceGI = true,  ssgiSampleCount = 8,  contactShadows = true,
                        screenSpaceReflections = true,
                        cascadeCount = 3, shadowDistance = 180f, shadowAtlasResolution = 4096,
                        softShadows = SoftShadowLevel.Medium, probeSpacingScale = 1.0f,
                        shadowDepthBias = 1.0f, shadowNormalBias = 1.0f,
                    };

                case AdaptiveQuality.QualityTier.High:
                    return new LightingTierSettings
                    {
                        multiBounce = true, skyVisibility = true, bentNormal = true, foliageBleed = true,
                        envReflectionStrength = 1.0f,
                        screenSpaceGI = true,  ssgiSampleCount = 6,  contactShadows = true,
                        cascadeCount = 3, shadowDistance = 150f, shadowAtlasResolution = 2048,
                        softShadows = SoftShadowLevel.Medium, probeSpacingScale = 1.0f,
                        shadowDepthBias = 1.0f, shadowNormalBias = 1.1f,
                    };

                case AdaptiveQuality.QualityTier.Medium:  // mid-range == Mobile High: the T1.5 rung
                    return new LightingTierSettings
                    {
                        multiBounce = true, skyVisibility = true, bentNormal = true, foliageBleed = true,
                        envReflectionStrength = 1.0f,
                        screenSpaceGI = false, ssgiSampleCount = 4,  contactShadows = true,
                        cascadeCount = 2, shadowDistance = 100f, shadowAtlasResolution = 2048,
                        softShadows = SoftShadowLevel.Low, probeSpacingScale = 1.5f,
                        shadowDepthBias = 1.0f, shadowNormalBias = 1.2f,
                    };

                default:                                  // Low == T0/T1: analytic ambient + plain grid
                    return new LightingTierSettings
                    {
                        multiBounce = false, skyVisibility = false, bentNormal = false, foliageBleed = false,
                        envReflectionStrength = 1.0f,
                        screenSpaceGI = false, ssgiSampleCount = 4,  contactShadows = false,
                        cascadeCount = 2, shadowDistance = 80f,  shadowAtlasResolution = 1024,
                        softShadows = SoftShadowLevel.Off, probeSpacingScale = 2.0f,
                        shadowDepthBias = 1.25f, shadowNormalBias = 1.5f,
                    };
            }
        }

        /// <summary>
        /// Convenience for the driver: resolve straight from a live AdaptiveQuality, or a
        /// safe High default when none is present so lighting never depends on the tier
        /// sampler existing in the scene.
        /// </summary>
        public static LightingTierSettings Resolve(AdaptiveQuality source)
        {
            return Resolve(source != null ? source.CurrentTier : AdaptiveQuality.QualityTier.High);
        }

        /// <summary>
        /// The one shadow distance every caster answers to. The shadow map ends at
        /// <c>s.shadowDistance</c> (§8.11), so a vegetation caster (§6.5) is only ever
        /// trimmed DOWN to it, never allowed to introduce a second, longer distance:
        /// past the map edge the cast is drawn per cascade with nothing to receive it -
        /// pure cost. A preference of 0 means the species opts out of casting entirely.
        /// This keeps the shadow distance a single owned value, so raising it is the one
        /// dial that costs both the sun cascades and the vegetation casters (S-56).
        /// </summary>
        public static float ShadowCasterBudgetM(LightingTierSettings s, float vegetationPreferenceM)
        {
            if (vegetationPreferenceM <= 0f) return 0f;
            return UnityEngine.Mathf.Min(vegetationPreferenceM, s.shadowDistance);
        }
    }
}
