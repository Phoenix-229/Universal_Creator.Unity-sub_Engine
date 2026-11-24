// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    SnowModel.cs
//  Purpose: Snow on the ground, stored as snow water equivalent (SWE) in
//           millimetres and never as depth. SWE is the conserved quantity - it
//           is what falls, what melts, and what a canopy is loaded by - while
//           depth is a display value that changes as the same water compacts.
//           This file owns the SWE<->depth conversion and how a pack's density
//           firms up with age.
//
//  WHY SWE, NOT DEPTH
//  ------------------
//  Ten millimetres of fresh snow and thirty millimetres of settled snow can be
//  the same water; only the air between the crystals has been squeezed out.
//  Store the water and depth follows from the density of the moment; store the
//  depth and every melt or compaction step has to guess how much water it stood
//  for. One millimetre of SWE is one kilogram per square metre by the definition
//  of water equivalent, which is the exact unit the canopy snow-load budget is
//  authored in, so the load handed on needs no conversion factor.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Age in days and SWE in millimetres arrive as plain scalars; it
//  reaches into no other layer and reads no world state.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// The snowpack as stored water: its load, its firming density, and the depth
    /// that density implies. Every method is a pure function of its arguments.
    /// </summary>
    public static class SnowModel
    {
        /// <summary>
        /// Surface load in kg/m2 of a pack holding this much SWE. One millimetre of
        /// water equivalent is one kilogram per square metre by definition, so this
        /// is the SWE itself, floored at zero - the value the canopy budget reads.
        /// </summary>
        public static float LoadKgPerM2(float sweMm)
        {
            return Mathf.Max(0f, sweMm);
        }

        /// <summary>
        /// Pack density in kg/m3 as a function of age in days. Fresh snow starts
        /// light and full of air and relaxes toward the settled density as it
        /// compacts under its own weight; the approach is exponential, so most of
        /// the firming happens in the first few days and then it eases off.
        /// </summary>
        public static float DensityKgPerM3(float ageDays)
        {
            float age = Mathf.Max(0f, ageDays);
            float toward = 1f - Mathf.Exp(-age / AtmoConstants.SnowSettleTimeConstantDays);
            return Mathf.Lerp(AtmoConstants.SnowFreshDensityKgPerM3,
                              AtmoConstants.SnowSettledDensityKgPerM3, toward);
        }

        /// <summary>
        /// Physical depth in metres of a pack holding this much SWE at this density.
        /// Depth is the water column scaled up by how much lighter the snow is than
        /// water, so the lighter the pack the deeper the same water stands.
        /// </summary>
        public static float DepthMetres(float sweMm, float densityKgPerM3)
        {
            float swe = Mathf.Max(0f, sweMm);
            float rho = Mathf.Max(1f, densityKgPerM3);
            return (swe / AtmoConstants.MillimetresPerMetre)
                 * (AtmoConstants.WaterDensityKgPerM3 / rho);
        }

        /// <summary>
        /// Depth in metres of a pack of a given age, using the density that age
        /// implies - the single call that turns stored water into a depth to show.
        /// </summary>
        public static float DepthMetresAtAge(float sweMm, float ageDays)
        {
            return DepthMetres(sweMm, DensityKgPerM3(ageDays));
        }

        /// <summary>
        /// Snow-surface albedo as a function of age in days. Fresh snow is brilliant
        /// and reflects most sunlight; it darkens toward the old, soiled value as it
        /// ages, which is exactly why an old pack absorbs sun and melts out while a
        /// fresh fall on the same day survives.
        /// </summary>
        public static float SnowAlbedo(float ageDays)
        {
            float age = Mathf.Max(0f, ageDays);
            float toward = 1f - Mathf.Exp(-age / AtmoConstants.SnowAlbedoDecayTimeConstantDays);
            return Mathf.Lerp(AtmoConstants.SnowFreshAlbedo01, AtmoConstants.SnowOldAlbedo01, toward);
        }

        /// <summary>
        /// Melt in mm SWE per day: a degree-day term driven by temperature above
        /// freezing, plus a radiation term that melts the snow the sun's absorbed
        /// energy can. Both are read through the aspect-adjusted effective
        /// temperature, so the equator-facing slope - warmer for the same air -
        /// clears first. The radiation term scales with absorbed sunlight
        /// (1 - albedo), so a dark old pack melts under a sun a fresh white one
        /// reflects, and it fades out through a gate below freezing so a low winter
        /// sun over cold snow clears nothing.
        /// </summary>
        public static float MeltMmPerDay(float effectiveTempC, float surfaceRadiationMjPerM2Day,
                                         float snowAlbedo01)
        {
            float degreeDay = AtmoConstants.DegreeDayMeltMmPerCPerDay * Mathf.Max(0f, effectiveTempC);

            float absorbedMj = Mathf.Max(0f, surfaceRadiationMjPerM2Day)
                             * (1f - Mathf.Clamp01(snowAlbedo01));
            float radiationPotential = AtmoConstants.RadiationMeltEfficiency01
                                     * absorbedMj / AtmoConstants.SnowLatentHeatFusionMjPerKg;
            float gate = Mathf.Clamp01((effectiveTempC - AtmoConstants.SnowMeltGateLowerC)
                                       / AtmoConstants.SnowMeltGateWidthC);

            return degreeDay + radiationPotential * gate;
        }

        /// <summary>
        /// Stored SWE after a timestep of melt, never below zero. The store is the
        /// conserved water; melt only removes it, and it cannot remove more than is
        /// there.
        /// </summary>
        public static float ApplyMelt(float sweMm, float meltMmPerDay, float dtDays)
        {
            float lost = Mathf.Max(0f, meltMmPerDay) * Mathf.Max(0f, dtDays);
            return Mathf.Max(0f, Mathf.Max(0f, sweMm) - lost);
        }

        /// <summary>
        /// Fraction of snow a slope keeps: everything below the shed angle, nothing
        /// above the full-shed angle, easing between the two so a cliff reads as bare
        /// rock with snow only on its ledges.
        /// </summary>
        public static float SlopeRetention01(float slopeDeg)
        {
            float s = Mathf.Max(0f, slopeDeg);
            if (s <= AtmoConstants.SnowSlopeShedStartDeg) return 1f;
            if (s >= AtmoConstants.SnowSlopeShedFullDeg) return 0f;
            float span = AtmoConstants.SnowSlopeShedFullDeg - AtmoConstants.SnowSlopeShedStartDeg;
            return 1f - (s - AtmoConstants.SnowSlopeShedStartDeg) / span;
        }

        /// <summary>
        /// Wind redistribution as a multiplier on snowfall from the windward-exposure
        /// term (0 sheltered lee, 0.5 neutral, 1 exposed windward): a lee hollow piles
        /// up several times the open-ground fall and builds a cornice, a neutral face
        /// is unchanged, and an exposed ridge scours down to a bare floor.
        /// </summary>
        public static float WindRedistributionFactor(float windwardExposure01)
        {
            float e = Mathf.Clamp01(windwardExposure01);
            if (e <= 0.5f)
                return Mathf.Lerp(AtmoConstants.SnowLeeAccumulationMax, 1f, e / 0.5f);
            return Mathf.Lerp(1f, AtmoConstants.SnowWindwardScourFloor, (e - 0.5f) / 0.5f);
        }

        /// <summary>
        /// Fraction of snow that survives contact with the ground: full on frozen
        /// ground, fading to none as the ground warms past freezing, so early-season
        /// snow melts on warm soil.
        /// </summary>
        public static float GroundRetention01(float groundTempC)
        {
            return Mathf.Clamp01(
                (AtmoConstants.SnowGroundMeltUpperC - groundTempC) / AtmoConstants.SnowGroundMeltUpperC);
        }

        /// <summary>
        /// The combined multiplier on a snowfall for where it may lie: zero over open
        /// water, zero on a cliff or warm ground, scoured on a windward ridge and
        /// piled deep in a lee - all three exclusions from one call, so one snowfall
        /// produces bare ridges, deep lee drifts and snow-free cliffs and lakes.
        /// </summary>
        public static float AccumulationFactor(float slopeDeg, float windwardExposure01,
                                               float groundTempC, bool onWater)
        {
            if (onWater) return 0f;
            return SlopeRetention01(slopeDeg)
                 * GroundRetention01(groundTempC)
                 * WindRedistributionFactor(windwardExposure01);
        }

        /// <summary>
        /// Effective surface albedo where snow lies over soil: the reflectance blends
        /// from bare dark soil up to the snow's own albedo as cover fills in, so a
        /// snowfall genuinely brightens the world the irradiance grid then samples,
        /// and clearing the snow returns the soil value exactly. Snow's contribution
        /// darkens with age, so an old grey pack brightens less than a fresh white one.
        /// </summary>
        public static float GroundAlbedo(float soilAlbedo01, float snowCoverFraction01, float snowAgeDays)
        {
            float cover = Mathf.Clamp01(snowCoverFraction01);
            return Mathf.Lerp(Mathf.Clamp01(soilAlbedo01), SnowAlbedo(snowAgeDays), cover);
        }

        /// <summary>
        /// How much of the grass and low detail still shows through lying snow, 1 in
        /// the open and 0 once the pack is deep enough to bury it. Depth, not cover,
        /// decides it: a dusting leaves the blades standing while a deep fall hides
        /// them, and melting the pack back down restores them.
        /// </summary>
        public static float GrassVisibility01(float snowDepthM)
        {
            float d = Mathf.Max(0f, snowDepthM);
            return 1f - Mathf.Clamp01(d / AtmoConstants.SnowGrassBuryDepthM);
        }

        /// <summary>
        /// How strongly a snow blanket decouples the ground beneath it from the air
        /// above, 0 for bare ground and approaching 1 as the pack deepens. Snow is a
        /// mass of trapped air and a poor conductor, so a deep pack lets the soil hold
        /// its own temperature while the air swings - the reason a subnivean layer
        /// stays near freezing through a hard frost.
        /// </summary>
        public static float GroundInsulation01(float snowDepthM)
        {
            float d = Mathf.Max(0f, snowDepthM);
            return 1f - Mathf.Exp(-d / AtmoConstants.SnowInsulationDepthScaleM);
        }

        /// <summary>
        /// Ground temperature under snow: the bare surface tracks the air, but as the
        /// pack deepens the ground pulls toward its own undisturbed temperature and
        /// stops following the air's swings - the insulation term applied to a
        /// temperature.
        /// </summary>
        public static float DecoupledGroundTempC(float airTempC, float undisturbedGroundTempC, float snowDepthM)
        {
            return Mathf.Lerp(airTempC, undisturbedGroundTempC, GroundInsulation01(snowDepthM));
        }
    }
}
