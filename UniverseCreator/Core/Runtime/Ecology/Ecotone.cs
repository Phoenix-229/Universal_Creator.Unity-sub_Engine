// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    Ecotone.cs
//  Purpose: Applies the four ecotone widths (V-25), so every transition in the
//           world is as wide as that kind of transition actually is.
//
//  WHAT GOES WRONG WITHOUT THIS. Each gradient in the system was reaching for
//  whatever smoothing was convenient where it was written, which is how a world ends
//  up with a treeline drawn like a contour line and a river bank smeared over fifty
//  metres. The widths are measurements and they belong in one place; this file is
//  where they are turned into the 0-1 factors the rules multiply by.
//
//  ONE FUNCTION PER NAMED BOUNDARY, NOT ONE GENERIC BLEND. A generic Blend(value,
//  threshold, width) is offered at the bottom because some caller will always need
//  it, but the named entry points are the ones to use: they carry the width with
//  them, so a rule cannot be written against the wrong one, and reading the call
//  site tells you which boundary is being crossed.
//
//  THE TREELINE IS THE ONE THAT MATTERS MOST. It is the most visible ecological
//  boundary on Earth and the easiest to get wrong, because the temptation is to
//  compare against 6.4 degC and take the answer. What that produces is a hard edge at
//  a fixed altitude, running round a mountain at constant height like a painted
//  stripe. What is really there is a band roughly a hundred metres deep in which
//  trees are present but dwarfed - krummholz - and the depth of that band is the
//  whole reason a real treeline looks ragged and follows the ground.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static class Ecotone
    {
        /// <summary>
        /// 0-1 tree presence across the treeline (L-22). One at the warm side of the
        /// band, zero at the cold side, and the band itself is where krummholz lives.
        ///
        /// Read against temperature and not altitude on purpose: temperature is what
        /// stops a tree, and letting the altitude follow from it is what makes the
        /// treeline dip into cold valleys and climb sunny spurs by itself.
        /// </summary>
        public static float Treeline(float warmestMonthTempC)
        {
            float cold = EcoConstants.TreelineWarmestMonthC - EcoConstants.EcotoneTreelineBandC;
            float warm = EcoConstants.TreelineWarmestMonthC + EcoConstants.EcotoneTreelineBandC;

            return Ramp(warmestMonthTempC, cold, warm);
        }

        /// <summary>
        /// 0-1 how far inside the treeline band a site sits: 0 below it, 1 at the top.
        /// Krummholz stature scales off this, which is why it is separate from presence -
        /// a tree at the top of the band is present and prostrate, not absent.
        /// </summary>
        public static float TreelineBandPosition(float warmestMonthTempC)
        {
            float warm = EcoConstants.TreelineWarmestMonthC + EcoConstants.EcotoneTreelineBandC;
            float band = 2f * EcoConstants.EcotoneTreelineBandC;

            return Mathf.Clamp01((warm - warmestMonthTempC) / Mathf.Max(1e-3f, band));
        }

        /// <summary>
        /// The altitude the treeline sits at, given the temperature at a reference
        /// height and the lapse rate. Not used to place anything - the temperature gate
        /// does that - but it is what a report quotes and what V-26 checks.
        /// </summary>
        public static float TreelineAltitudeM(float referenceTempC, float referenceAltitudeM)
        {
            float drop = referenceTempC - EcoConstants.TreelineWarmestMonthC;
            if (drop <= 0f) return referenceAltitudeM;

            return referenceAltitudeM + drop * 1000f / EcoConstants.LapseRateCPerKm;
        }

        /// <summary>
        /// 0-1 riparian influence with distance from a bank. Falls off over metres,
        /// because that is how fast the water table drops away from a channel.
        /// </summary>
        public static float Riparian(float distanceToWaterM, float reachM)
        {
            if (distanceToWaterM <= reachM) return 1f;

            return 1f - Mathf.Clamp01(
                (distanceToWaterM - reachM) / EcoConstants.EcotoneRiparianMetres);
        }

        /// <summary>
        /// 0-1 coastal influence with distance inland, over the distance salt spray
        /// itself carries. Exponential rather than linear because deposition is a decay:
        /// the same fraction is dropped per unit distance, which is what leaves a long
        /// thin tail of salt-tolerant scrub well inland of the beach.
        /// </summary>
        public static float Coastal(float distanceToWaterM)
        {
            if (distanceToWaterM <= 0f) return 1f;

            return Mathf.Exp(-distanceToWaterM / EcoConstants.EcotoneCoastalMetres);
        }

        /// <summary>
        /// 0-1 mixing weight across a biome boundary on level ground. Narrow, because a
        /// biome edge with no relief behind it is sharp; where it looks gradual in the
        /// world it is because the terrain under it is doing the work.
        /// </summary>
        public static float BiomeBoundary(float distanceToBoundaryM)
        {
            return 1f - Mathf.Clamp01(
                Mathf.Abs(distanceToBoundaryM) / EcoConstants.EcotoneBiomeMetres);
        }

        /// <summary>
        /// Smoothstep between two bounds, clamped, and tolerant of them arriving in
        /// either order. Smooth rather than linear so an ecotone has no visible crease
        /// at either edge - a linear ramp reads as two straight lines meeting.
        /// </summary>
        public static float Ramp(float value, float zeroAt, float oneAt)
        {
            if (Mathf.Approximately(zeroAt, oneAt))
                return value >= oneAt ? 1f : 0f;

            float t = Mathf.Clamp01((value - zeroAt) / (oneAt - zeroAt));

            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// The generic form, for a gradient that has no named width yet. Prefer a named
        /// entry point: a width passed in at the call site is a width nobody can find
        /// again when the world comes out looking soft.
        /// </summary>
        public static float Blend(float value, float threshold, float widthM)
        {
            return Ramp(value, threshold - widthM * 0.5f, threshold + widthM * 0.5f);
        }
    }
}
