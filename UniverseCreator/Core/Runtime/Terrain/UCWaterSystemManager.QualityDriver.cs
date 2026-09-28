// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    UCWaterSystemManager.QualityDriver.cs
//  Purpose: THE ONE CONTROL SURFACE FOR WATER QUALITY. Resolves the product quality
//           tier (from AdaptiveQuality, or an explicit override on this manager) into a
//           WaterQuality budget and APPLIES it to the whole water world: the interactive
//           ripple simulation's tier, the ambient wave-train ceiling on every body, and
//           the underwater caustics/god-ray gate. One place the buyer or the project
//           picks how heavy the water is; everything downstream reads what it sets.
//
//  IT OWNS NO TIER MATH
//  --------------------
//  The tier->settings table lives in WaterQuality.For (pure, self-tested). This file
//  only reads the resolved bundle and pushes it onto Unity objects. The tier itself
//  comes from the shipped AdaptiveQuality, the same enum vegetation, props, lighting
//  and weather already scale by - there is no second water-only ladder.
//
//  DEGRADE, NEVER DROP
//  -------------------
//  A lower tier lowers the ripple solve to a decal, trims the ocean's four wave trains
//  toward one, and switches the caustics off - it never removes interactive water, the
//  swell or the underwater medium. The octave ceiling is a CAP against each body's
//  AUTHORED count (recomputed from its kind, never from the already-capped live value),
//  so raising the tier again restores the full spectrum.
//
//  NO ASSET MUTATION
//  -----------------
//  _WaveOctaves is a per-material CBUFFER uniform, so it is driven per renderer through
//  a MaterialPropertyBlock - never by writing the shared material asset, which would
//  change it on disk in edit mode and for every body that shares it. GetPropertyBlock
//  first, so the storm/ice the weather driver wrote on the same renderer is preserved.
//
//  EDIT MODE IS EVENT-DRIVEN
//  -------------------------
//  OnEnable/OnValidate (main file) call ApplyQualityEvent once; play mode re-applies
//  only when the resolved tier actually changes. No per-frame ExecuteAlways walk.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWaterSystemManager
    {
        /// <summary>How the water tier is chosen: follow the scene's AdaptiveQuality, or
        /// pin one tier explicitly for a build that targets a known device class.</summary>
        public enum WaterQualityMode { Auto, Ultra, High, Medium, Low }

        [Header("Water Quality")]
        [Tooltip("How heavy the water is allowed to be.\n\n" +
                 "Auto follows the scene's AdaptiveQuality, which lowers the tier when the " +
                 "frame rate drops - the right choice for a build that ships to many devices. " +
                 "The explicit tiers pin one budget: Ultra/High run the full wave solve, four " +
                 "wave trains and the underwater caustics; Medium halves the solve resolution " +
                 "and trims the trains; Low degrades to a stamped ripple decal. Every tier " +
                 "keeps interactive water, swell and an underwater view - a lower one thins " +
                 "them, it never drops them.")]
        public WaterQualityMode qualityMode = WaterQualityMode.Auto;

        // Cached tier sampler and the last tier actually pushed, so play mode re-applies
        // only on a real change rather than walking every body each frame.
        private AdaptiveQuality qualitySource;
        private AdaptiveQuality.QualityTier lastQualityTier;
        private bool qualityApplied;

        // _WaveOctaves is per-material; driven per renderer through a reused block so the
        // shared material asset is never touched. See the header.
        private static readonly int IdWaveOctaves = Shader.PropertyToID(UCWaterProps.WaveOctaves);
        private static MaterialPropertyBlock s_qualityBlock;

        /// <summary>The quality tier in force: the explicit override, or the scene's
        /// AdaptiveQuality when set to Auto (a safe High default when none is present, so
        /// water never depends on the tier sampler existing).</summary>
        private AdaptiveQuality.QualityTier ResolveQualityTier()
        {
            switch (qualityMode)
            {
                case WaterQualityMode.Ultra:  return AdaptiveQuality.QualityTier.Ultra;
                case WaterQualityMode.High:   return AdaptiveQuality.QualityTier.High;
                case WaterQualityMode.Medium: return AdaptiveQuality.QualityTier.Medium;
                case WaterQualityMode.Low:    return AdaptiveQuality.QualityTier.Low;
                default:
                    if (qualitySource == null)
                        qualitySource = FindAnyObjectByType<AdaptiveQuality>();
                    return qualitySource != null
                        ? qualitySource.CurrentTier
                        : AdaptiveQuality.QualityTier.High;
            }
        }

        // Play-mode entry from Update: apply only when the resolved tier changed.
        private void ApplyQualityIfChanged()
        {
            AdaptiveQuality.QualityTier tier = ResolveQualityTier();
            if (qualityApplied && tier == lastQualityTier) return;
            ApplyQuality(tier);
        }

        // Edit-mode entry from OnEnable/OnValidate: always push, so toggling the mode in
        // the inspector takes effect immediately in the scene view.
        private void ApplyQualityEvent()
        {
            ApplyQuality(ResolveQualityTier());
        }

        /// <summary>
        /// Resolves the tier to a WaterQuality budget and applies it: the ripple sim's
        /// tier, the underwater caustics/god-ray gate, and the capped wave-train count on
        /// every body (material + CPU buoyancy mirror). Safe when no sim, view or body
        /// exists - each step guards its own target.
        /// </summary>
        private void ApplyQuality(AdaptiveQuality.QualityTier tier)
        {
            WaterQuality q = WaterQuality.For(tier, Application.isMobilePlatform);

            // Interactive ripple system: the sim degrades High -> Mid (half grid) -> Low
            // (decal) -> Off itself; here it is only told which tier to run.
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim != null) sim.tier = q.ripple;

            // Underwater extras gate, read by every view in the scene (static).
            UCUnderwaterView.QualityEffectScale = q.underwaterEffectScale;

            // Per body: cap the ambient wave trains against the body's AUTHORED count and
            // write the result to both the material (_WaveOctaves, what you see) and the
            // buoyancy surface (WaterSurfaceParams.octaves, what floats), which must agree.
            System.Collections.Generic.IReadOnlyList<UCWaterVolume> all = UCWaterVolume.All;
            for (int i = 0; i < all.Count; i++)
            {
                UCWaterVolume v = all[i];
                if (v == null) continue;

                float authored = UCWaterBody.WaveOctavesFor((WaterBodyKind)(int)v.kind);
                float octaves = Mathf.Min(authored, q.waveOctaveCeiling);

                v.surface.octaves = octaves;

                Renderer r = v.GetComponent<Renderer>();
                if (r == null) continue;

                if (s_qualityBlock == null) s_qualityBlock = new MaterialPropertyBlock();
                r.GetPropertyBlock(s_qualityBlock);
                s_qualityBlock.SetFloat(IdWaveOctaves, octaves);
                r.SetPropertyBlock(s_qualityBlock);
            }

            lastQualityTier = tier;
            qualityApplied = true;
        }

        /// <summary>Editor entry point: resolve and apply the water quality tier now.</summary>
        [ContextMenu("Apply Water Quality Now")]
        private void ApplyWaterQualityNow() => ApplyQualityEvent();
    }
}
