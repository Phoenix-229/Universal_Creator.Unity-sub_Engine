// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    WetSurfaceOptics.cs
//  Purpose: Turn wetness into the look of wet ground. Outdoors, reflections matter most on wet
//           surfaces, and § 7 already tracks how wet everything is from rainfall and drying and
//           where puddles stand in the low ground. This consumes that: a wet surface darkens
//           (water fills the pores and traps the light that would have scattered back, so soaked
//           soil, rock and wood lose about a third of their diffuse albedo), its roughness drops
//           sharply toward a water sheen, and it gains a Fresnel-weighted sky reflection that is
//           faint head-on and bright at grazing angles - the wet-road look. A standing puddle is
//           the limit of the same optics: full darkening, mirror roughness, strong reflection.
//           This delivers most of the perceptual value of ray-traced reflections on hardware that
//           cannot trace a ray, driven entirely by data the toolkit already computes.
//
//           Pure and deterministic: the caller supplies § 7's wetness and puddle fraction and the
//           view geometry; this owns only the optics. Where a surface gets wet, where puddles
//           form and the drying order (exposed and steep first) all belong to § 7's
//           WetnessSimulation and are not recomputed here.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The optical response of a wet surface (S-76). Diffuse darkening, roughness collapse and a
    /// Schlick-Fresnel sky-reflection weight, all driven by § 7's surface wetness and standing-water
    /// fraction. All static, allocation-free, thread-safe.
    /// </summary>
    public static class WetSurfaceOptics
    {
        /// <summary>The effective wetness the optics see: the rain film wetness, raised to full by
        /// any standing puddle over it - a puddle is saturated ground and then some, so it drives
        /// the darkening and the reflection to their limit regardless of the film value.</summary>
        public static float EffectiveWetness(float wetness01, float puddleFraction01)
        {
            return Mathf.Clamp01(Mathf.Max(Mathf.Clamp01(wetness01), Mathf.Clamp01(puddleFraction01)));
        }

        /// <summary>Wet diffuse albedo: the dry albedo darkened by up to
        /// <see cref="LightingConstants.WetAlbedoDarkening"/> as the surface soaks. Water in the
        /// pores traps back-scatter, so a wet surface is always darker than dry, never brighter.</summary>
        public static Vector3 WetAlbedo(Vector3 dryAlbedo, float wetness01, float puddleFraction01)
        {
            float w = EffectiveWetness(wetness01, puddleFraction01);
            float factor = 1f - LightingConstants.WetAlbedoDarkening * w;
            return dryAlbedo * factor;
        }

        /// <summary>Wet roughness: the dry roughness pulled toward
        /// <see cref="LightingConstants.WetFloorRoughness"/> as the surface wets. The drop is sharp -
        /// squaring the wetness weights it toward the wet end - so a light film already glosses the
        /// ground, and a puddle reaches the water sheen. Never rougher than dry.</summary>
        public static float WetRoughness(float dryRoughness01, float wetness01, float puddleFraction01)
        {
            float w = EffectiveWetness(wetness01, puddleFraction01);
            float sharp = w * w;                                    // wetness glosses the surface quickly
            float wet = Mathf.Lerp(Mathf.Clamp01(dryRoughness01), LightingConstants.WetFloorRoughness, sharp);
            return Mathf.Min(Mathf.Clamp01(dryRoughness01), wet);   // wet is never rougher than dry
        }

        /// <summary>The Schlick-Fresnel reflectance of the water film for a view at
        /// <paramref name="nDotV"/>: <c>F0 + (1-F0)(1-NdotV)^5</c> with water's F0 = 0.02. Near one
        /// at grazing angles, faint head-on - the reason a wet road mirrors the sky far off but not
        /// underfoot.</summary>
        public static float FresnelReflectance(float nDotV)
        {
            float c = 1f - Mathf.Clamp01(nDotV);
            float c5 = c * c; c5 *= c5 * c;                         // (1 - NdotV)^5
            return LightingConstants.WaterFresnelF0 + (1f - LightingConstants.WaterFresnelF0) * c5;
        }

        /// <summary>
        /// How strongly the sky reflection shows on this wet surface: the Fresnel reflectance scaled
        /// by how wet the surface is, so a dry surface reflects nothing extra, a damp one reflects
        /// faintly and a puddle reflects at the full Fresnel weight. This is the blend weight for the
        /// sky/grid specular the caller already has; it adds no new light source.
        /// </summary>
        public static float SkyReflectionWeight(float nDotV, float wetness01, float puddleFraction01)
        {
            float w = EffectiveWetness(wetness01, puddleFraction01);
            return FresnelReflectance(nDotV) * w;
        }
    }
}
