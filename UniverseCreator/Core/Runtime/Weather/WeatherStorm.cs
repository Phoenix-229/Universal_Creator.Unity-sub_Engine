// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherStorm.cs
//  Purpose: Storm activity and the lightning it throws. Activity is the
//           convective cover - instability and moisture together - deepened by a
//           synoptic low, so an afternoon airmass storm and a driven frontal one
//           both read strong while a flat stratus deck reads nothing. Strikes are
//           sampled deterministically from the cell and the simulated minute, so
//           two machines given the same world flash on the same beats; thunder
//           trails each flash by the time sound takes to cross the distance.
//
//  WHY A DETERMINISTIC PER-MINUTE DRAW
//  -----------------------------------
//  A strike is a discrete event, but the world must look the same on the second
//  visit (C-06). Hashing the cell index and the minute into a private stream and
//  drawing a Poisson count from it means the flashes are random-looking yet fixed:
//  reload the save at the same minute and the same storm flashes the same way.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. Takes the convective cover and the low depth as scalars; nothing
//  here reaches into the ecology layer or the terrain module. The one-frame light
//  pulse the strike drives is a rendering concern and lives downstream.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// Storm intensity and lightning. Every method is a pure function of its
    /// arguments; the integer hash below is the only state, and it is local to a call.
    /// </summary>
    public static class WeatherStorm
    {
        // Integer hash and xorshift stream constants, kept local like the lattice's
        // own hash offsets. Not physics, so not in AtmoConstants.
        private const uint StormHashMixCell = 2246822519u;
        private const uint StormHashMixMinute = 3266489917u;
        private const uint StormHashFinal = 668265263u;
        private const int XorShiftLeftA = 13;
        private const int XorShiftRightB = 17;
        private const int XorShiftLeftC = 5;
        private const int UnitStateBits = 32;
        private const int UnitMantissaShift = 8;
        private const int MaxStrikesPerMinute = 240;

        /// <summary>
        /// 0..1 storm activity: the convective cover, amplified by how deep the low is.
        /// A deep low at full convective cover reads a severe storm; a stable column
        /// reads nothing, because its convective cover is already zero.
        /// </summary>
        public static float Activity01(float convectiveCover01, float lowDepthHpa)
        {
            float depthFraction01 =
                Mathf.Clamp01(lowDepthHpa / AtmoConstants.StormReferenceDepthHpa);
            float dynamicBoost = 1f + depthFraction01 * AtmoConstants.StormLowDepthGain;
            return Mathf.Clamp01(Mathf.Clamp01(convectiveCover01) * dynamicBoost);
        }

        /// <summary>
        /// strikes/min. Expected lightning rate from the storm activity, zero below the
        /// threshold so a stratus deck never flashes. This is the Poisson mean, not a count.
        /// </summary>
        public static float LightningRatePerMinute(float stormActivity01)
        {
            if (stormActivity01 < AtmoConstants.StormActivityThreshold01) return 0f;
            return Mathf.Clamp01(stormActivity01) * AtmoConstants.LightningMaxStrikesPerMinute;
        }

        /// <summary>
        /// The number of strikes in one simulated minute for one cell: a Poisson draw
        /// from a stream fixed by the cell and the minute, so it is the same every time
        /// the same minute is asked about. Zero rate is zero strikes without a draw.
        /// </summary>
        public static int StrikesInMinute(int cellIndex, int minuteOfSimulatedTime,
                                          float ratePerMinute)
        {
            if (ratePerMinute <= 0f) return 0;

            uint state = Hash(cellIndex, minuteOfSimulatedTime);
            float threshold = Mathf.Exp(-ratePerMinute);
            float product = 1f;
            int count = 0;
            do
            {
                count++;
                product *= NextUnit(ref state);
            }
            while (product > threshold && count <= MaxStrikesPerMinute);

            return count - 1;
        }

        /// <summary>
        /// s. How long thunder trails the flash: the distance divided by the speed of
        /// sound. A strike overhead cracks at once; one a kilometre off arrives late.
        /// </summary>
        public static float ThunderDelaySeconds(float distanceM)
        {
            if (distanceM <= 0f) return 0f;
            return distanceM / AtmoConstants.SpeedOfSoundMPerS;
        }

        // Mixes the cell and the minute into a non-zero xorshift seed.
        private static uint Hash(int cellIndex, int minuteOfSimulatedTime)
        {
            unchecked
            {
                uint h = (uint)cellIndex * StormHashMixCell;
                h ^= (uint)minuteOfSimulatedTime * StormHashMixMinute;
                h ^= h >> XorShiftRightB;
                h *= StormHashFinal;
                h ^= h >> XorShiftRightB;
                return h == 0u ? StormHashFinal : h;
            }
        }

        // One uniform in [0,1) from an advancing xorshift32 stream.
        private static float NextUnit(ref uint state)
        {
            unchecked
            {
                state ^= state << XorShiftLeftA;
                state ^= state >> XorShiftRightB;
                state ^= state << XorShiftLeftC;
            }
            return (state >> UnitMantissaShift)
                   / (float)(1u << (UnitStateBits - UnitMantissaShift));
        }
    }
}
