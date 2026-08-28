// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WetnessSimulation.cs
//  Purpose: How wet the ground is, on a 0..1 scale, as it wets in the rain and
//           dries afterwards. The wetting is easy - it tracks the rain. The whole
//           reason this is a simulation and not a constant is the drying: the rate
//           of drying is the potential evapotranspiration, the same PET (L-13) that
//           decides where forests grow. Drive drying by PET and every seasonal
//           behaviour falls out for free - a July afternoon dries in minutes, a
//           cold damp December stays wet for a day, shade dries slower than sun,
//           and wind dries faster than still air - with no seasonal constant
//           written anywhere.
//
//  WHY A RATE-LIMITED STEP AND NOT A SNAP
//  --------------------------------------
//  Wetness is a physical store of water; it cannot appear or vanish in one frame.
//  The step clamps its own rate of change so a spike in rain or a long frame can
//  never make the ground flash from dry to soaked between two images.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. It is handed the rain rate and the PET as scalars - it does not
//  compute PET here, and never reaches into the ecology layer or terrain. The PET
//  it consumes is the one AtmoMath produces (Hargreaves-Samani).
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Surface wetness on a 0..1 scale. Every method is a pure function of its
    /// arguments; the wetness store itself is held by the caller and advanced by Step.
    /// </summary>
    public static class WetnessSimulation
    {
        /// <summary>
        /// 1/s. How fast a surface wets under a given rain rate: proportional to the
        /// rain, so a downpour soaks the ground far faster than a drizzle.
        /// </summary>
        public static float WettingRatePerSecond(float precipMmPerH)
        {
            float rain = Mathf.Max(0f, precipMmPerH);
            return (rain / AtmoConstants.WetnessReferenceRainMmPerH)
                   / AtmoConstants.WetnessWetTimeSeconds;
        }

        /// <summary>
        /// 1/s. How fast a surface dries under a given PET: proportional to the PET, so
        /// a hot dry afternoon dries in minutes and a cold damp day barely dries at all.
        /// This is the substitution that carries the whole seasonal behaviour.
        /// </summary>
        public static float DryingRatePerSecond(float petMmPerDay)
        {
            float pet = Mathf.Max(0f, petMmPerDay);
            return (pet / AtmoConstants.WetnessReferencePetMmPerDay)
                   / AtmoConstants.WetnessDryTimeSeconds;
        }

        /// <summary>
        /// Advances the wetness store one step: it rises with the rain and falls with the
        /// PET, and its rate of change is clamped so it can never jump in a single frame.
        /// Returns the new wetness, always in 0..1.
        /// </summary>
        public static float Step(float wetness01, float precipMmPerH, float petMmPerDay,
                                 float dtSeconds)
        {
            if (dtSeconds <= 0f) return Mathf.Clamp01(wetness01);

            float ratePerSecond = WettingRatePerSecond(precipMmPerH)
                                  - DryingRatePerSecond(petMmPerDay);

            float maxStep = AtmoConstants.WetnessMaxRatePerSecond * dtSeconds;
            float delta = Mathf.Clamp(ratePerSecond * dtSeconds, -maxStep, maxStep);

            return Mathf.Clamp01(wetness01 + delta);
        }

        // ---------------------------------------------------------------------
        //  Puddling by topography
        //
        //  Surface wetness above is a film that coats every exposed face; a puddle
        //  is standing water, and standing water is a matter of where the ground
        //  can hold it. These take the shape of the ground - its slope, its
        //  curvature and its wetness index, all deterministic - and turn it into a
        //  pool depth in metres that fills, drains and evaporates.
        // ---------------------------------------------------------------------

        /// <summary>
        /// 0..1. The share of arriving water a cell can hold as standing water rather
        /// than shedding downhill. One on the level, falling to zero by the hold
        /// angle, so anything steeper - a hillside, a 20-degree bank - keeps no puddle
        /// at all. This is the gate that keeps standing water off the slopes.
        /// </summary>
        public static float PuddleRetention01(float slopeDeg)
        {
            float tanSlope = Mathf.Tan(Mathf.Clamp(slopeDeg, 0f, 89.9f) * Mathf.Deg2Rad);
            float tanHold = Mathf.Tan(AtmoConstants.PuddleHoldAngleDegrees * Mathf.Deg2Rad);
            if (tanHold < Mathf.Epsilon) return 0f;

            return Mathf.Clamp01(1f - tanSlope / tanHold);
        }

        /// <summary>
        /// The multiple of the local rainfall a cell receives once runoff from the
        /// ground above it is counted in. One on a ridge or an open flat, which get
        /// only the rain that lands on them; several times that in a concave hollow
        /// with a high wetness index, which draws in a whole slope's worth. Whichever
        /// of curvature and wetness index argues harder for gathering wins, the way
        /// either a steep slope or thin soil alone is enough to bare the rock.
        /// </summary>
        public static float PuddleCatchmentGain(float twi, float convexityPerM)
        {
            float span = AtmoConstants.PuddleTwiGatherFull - AtmoConstants.PuddleTwiGatherMin;
            float fromTwi = span > 0f
                ? Mathf.Clamp01((twi - AtmoConstants.PuddleTwiGatherMin) / span)
                : 0f;

            // Negative convexity is concavity: a dish that collects. A ridge or plane
            // contributes nothing here.
            float concavePerM = Mathf.Max(0f, -convexityPerM);
            float fromCurve = Mathf.Clamp01(concavePerM / AtmoConstants.PuddleConcavityFullPerM);

            float gather01 = Mathf.Max(fromTwi, fromCurve);
            return 1f + AtmoConstants.PuddleCatchmentMaxGain * gather01;
        }

        /// <summary>
        /// Advances a cell's puddle depth one step, in metres. Water arrives as the
        /// local rain multiplied by what the topography gathers and gated by what the
        /// slope can hold; it leaves by soaking into the soil at a rate set by the
        /// cell's permeability and by evaporating at the PET, the very depth of water
        /// PET is defined as. Depth is held in 0..PuddleMaxDepthMetres - past that it
        /// is a pond, which belongs to the hydrology system, not to the weather.
        /// </summary>
        public static float PuddleStep(float puddleDepthM, float precipMmPerH, float petMmPerDay,
                                       float slopeDeg, float convexityPerM, float twi,
                                       float soilPermeability01, float dtSeconds)
        {
            float depth = Mathf.Max(0f, puddleDepthM);
            if (dtSeconds <= 0f) return Mathf.Min(depth, AtmoConstants.PuddleMaxDepthMetres);

            // Rain rate as a depth laid down per second, then amplified by upslope
            // convergence and gated by the slope: a hillside sheds it all.
            float rainDepthPerSecond = Mathf.Max(0f, precipMmPerH)
                                       / AtmoConstants.MillimetresPerMetre
                                       / AtmoConstants.SecondsPerHour;
            float inflow = rainDepthPerSecond
                           * PuddleCatchmentGain(twi, convexityPerM)
                           * PuddleRetention01(slopeDeg);

            // Soaking into the ground: full rate on open soil, nothing on rock or
            // saturated clay.
            float infiltration = Mathf.Clamp01(soilPermeability01)
                                 * AtmoConstants.PuddleInfiltrationRateMetresPerSecond;

            // PET is a depth of water evaporated per day, so it converts straight to a
            // loss rate with no coefficient of its own.
            float evaporation = Mathf.Max(0f, petMmPerDay)
                                / AtmoConstants.MillimetresPerMetre
                                / AtmoConstants.SecondsPerDay;

            depth += (inflow - infiltration - evaporation) * dtSeconds;
            return Mathf.Clamp(depth, 0f, AtmoConstants.PuddleMaxDepthMetres);
        }

        // ---------------------------------------------------------------------
        //  Surface traction
        //
        //  The surface state resolves to one grip number that other systems
        //  read; this module never applies it as a force. Dry ground grips
        //  fully; a wet film gives some of that up; snow cover gives up more;
        //  and glaze ice - liquid water caught below freezing - is slipperiest
        //  of all, well past snow.
        // ---------------------------------------------------------------------

        /// <summary>
        /// 0..1 grip. Composes the surface state into one friction: dry is full grip,
        /// a wet film lowers it toward the wet floor, snow cover lowers it further, and
        /// liquid water below freezing glazes to ice - the slipperiest of the four, and
        /// only when there is water present to freeze, so cold but dry ground still grips.
        /// </summary>
        public static float SurfaceFriction01(float wetness01, float snowDepthM, float wetBulbC)
        {
            float wet = Mathf.Clamp01(wetness01);
            float snowCover = Mathf.Clamp01(Mathf.Max(0f, snowDepthM)
                                            / AtmoConstants.SnowFrictionFullDepthM);

            float friction = Mathf.Lerp(1f, AtmoConstants.WetFrictionFloor, wet);
            friction = Mathf.Lerp(friction, AtmoConstants.SnowFrictionFloor, snowCover);

            // Ice forms only where liquid water sits below freezing; a dry frozen road
            // is not slippery. Ramp over a band so grip does not snap at exactly zero.
            float freezing = Mathf.Clamp01((AtmoConstants.WaterFreezingPointC - wetBulbC)
                                           / AtmoConstants.IceFreezeBandC);
            float iceAmount = wet * freezing;
            friction = Mathf.Lerp(friction, AtmoConstants.IceFriction01, iceAmount);

            return Mathf.Clamp01(friction);
        }
    }
}
