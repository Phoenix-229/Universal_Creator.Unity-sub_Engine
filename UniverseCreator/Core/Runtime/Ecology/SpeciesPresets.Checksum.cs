// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Checksum.cs
//  Purpose: One number that answers "has the author changed this organism since
//           the preset was applied?"
//
//  The list below is the definition of what a preset owns. Every field a preset
//  writes is folded in; nothing else is. That is deliberate and it cuts both ways:
//
//    - The author's machine decisions - instancing policy, collider policy, LOD
//      bias, billboard distance, per-chunk budget, shadow distance - are absent,
//      so changing a LOD bias does not make a spruce claim to be a modified
//      spruce. It is still a spruce.
//    - Every biological field is present, so nudging the treeline by a tenth of a
//      degree does show, which is the whole point.
//
//  Folded with FNV-1a over exact float bit patterns rather than over rounded
//  decimals, because 0.1f and 0.10000001f really are different numbers and an
//  author who typed the second one has made an edit.
//
//  Adding a biological field to EcoEnvelope means adding it here. A field missing
//  from this list is a field an author can silently change, which is the one thing
//  the "(modified)" marker exists to prevent.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        // FNV-1a, 32-bit. Chosen because it is a handful of instructions, spreads
        // single-bit input changes across the whole output, and is specified
        // exactly - so the same envelope hashes the same on every machine and in
        // every Unity version, which a runtime's own GetHashCode does not promise.
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime = 16777619u;

        /// <summary>
        /// Folds every field a preset writes into one int. Never zero-sensitive:
        /// two envelopes differing in any single biological field produce
        /// different values.
        /// </summary>
        public static int ChecksumOf(EcoEnvelope eco)
        {
            if (eco == null) return 0;

            uint h = FnvOffsetBasis;

            // ---- identity ---------------------------------------------------

            h = Fold(h, eco.displayName);
            h = Fold(h, (int)eco.lifeForm);
            h = Fold(h, eco.weight);
            h = Fold(h, (int)eco.successionStage);

            // ---- thermal ----------------------------------------------------

            h = Fold(h, eco.tempOptimumC);
            h = Fold(h, eco.tempToleranceC);
            h = Fold(h, eco.tempLethalMinC);
            h = Fold(h, eco.tempLethalMaxC);
            h = Fold(h, eco.warmestMonthMinC);
            h = Fold(h, eco.frostFreeFractionMin);
            h = Fold(h, eco.gddBaseC);
            h = Fold(h, eco.gddMinRequired);

            // ---- water ------------------------------------------------------

            h = Fold(h, eco.moistureOptimum);
            h = Fold(h, eco.moistureTolerance);
            h = Fold(h, eco.precipOptimumMm);
            h = Fold(h, eco.precipToleranceMm);
            h = Fold(h, eco.aridityMin);
            h = Fold(h, eco.aridityMax);
            h = Fold(h, eco.twiOptimum);
            h = Fold(h, eco.twiTolerance);
            h = Fold(h, eco.waterTableMaxDepthM);
            h = Fold(h, eco.floodTolerance);
            h = Fold(h, eco.droughtTolerance);

            // ---- shore and salt ---------------------------------------------

            h = Fold(h, eco.distanceToWaterMinM);
            h = Fold(h, eco.distanceToWaterMaxM);
            h = Fold(h, eco.saltTolerance);
            h = Fold(h, (int)eco.tidalZone);
            h = Fold(h, eco.submergedDepthRangeM);

            // ---- ground -----------------------------------------------------

            h = Fold(h, eco.useAbsoluteAltitude ? 1 : 0);
            h = Fold(h, eco.minAltitudeM);
            h = Fold(h, eco.maxAltitudeM);
            h = Fold(h, eco.slopeOptimumDeg);
            h = Fold(h, eco.slopeToleranceDeg);
            h = Fold(h, eco.aspectPreferenceDeg);
            h = Fold(h, eco.aspectStrength);
            h = Fold(h, eco.curvaturePreference);
            h = Fold(h, eco.soilDepthMinM);
            h = Fold(h, eco.soilFertilityOptimum);
            h = Fold(h, eco.soilFertilityTolerance);
            h = Fold(h, eco.rockinessMax);

            // ---- light and neighbours ---------------------------------------

            h = Fold(h, eco.shadeTolerance);
            h = Fold(h, (int)eco.canopyLayer);
            h = Fold(h, eco.laiContribution);
            h = Fold(h, eco.dispersalLambdaM);
            h = Fold(h, eco.allelopathyRadiusM);

            // ---- form -------------------------------------------------------

            h = Fold(h, eco.matureHeightM);
            h = Fold(h, eco.dbhRangeM);
            h = Fold(h, eco.crownAllometryA);
            h = Fold(h, eco.crownAllometryB);
            h = Fold(h, eco.rootPlateRadiusM);
            h = Fold(h, eco.trunkFlexibility);
            h = Fold(h, eco.selfThinningK);

            // ---- disturbance ------------------------------------------------

            h = Fold(h, (int)eco.fireResponse);
            h = Fold(h, eco.windExposureMax);
            h = Fold(h, eco.krummholzAboveExposure ? 1 : 0);
            h = Fold(h, eco.krummholzHeightScale);
            h = Fold(h, eco.snowLoadMaxKgPerM2);
            h = Fold(h, eco.avalancheTolerance);
            h = Fold(h, eco.browseTolerance);

            // ---- season -----------------------------------------------------

            h = Fold(h, eco.deciduous ? 1 : 0);
            h = Fold(h, eco.budburstGdd);
            h = Fold(h, eco.senescenceDayLengthH);
            h = Fold(h, eco.springColour);
            h = Fold(h, eco.summerColour);
            h = Fold(h, eco.autumnColour);
            h = Fold(h, eco.winterColour);
            h = Fold(h, eco.leafOffFraction);
            h = Fold(h, eco.snowCanopyAccumulation);
            h = Fold(h, eco.wetDarkening);
            h = Fold(h, eco.seasonalHeightScale);

            // ---- biome ------------------------------------------------------
            //
            //  Folded even though it is a community statement rather than a
            //  tolerance, because it changes which ground a species appears on as
            //  decisively as any curve here, and an unfolded field is one an author
            //  can edit while the inspector still claims the preset is untouched.

            h = Fold(h, eco.allowedBiomes);
            h = Fold(h, eco.perBiomeWeight);

            // Reinterpreted rather than clamped, so the whole 32 bits survive into
            // the serialised int.
            return unchecked((int)h);
        }

        private static uint Fold(uint h, int value)
        {
            unchecked
            {
                h = (h ^ (uint)(value & 0xFF)) * FnvPrime;
                h = (h ^ (uint)((value >> 8) & 0xFF)) * FnvPrime;
                h = (h ^ (uint)((value >> 16) & 0xFF)) * FnvPrime;
                h = (h ^ (uint)((value >> 24) & 0xFF)) * FnvPrime;
            }

            return h;
        }

        private static uint Fold(uint h, float value)
        {
            // Negative zero is folded to positive. The two have different bit
            // patterns but they are the same number, and an author who happens to
            // type -0 has not changed the plant.
            if (value == 0f) return Fold(h, System.BitConverter.SingleToInt32Bits(0f));

            return Fold(h, System.BitConverter.SingleToInt32Bits(value));
        }

        private static uint Fold(uint h, Vector2 value)
        {
            h = Fold(h, value.x);
            return Fold(h, value.y);
        }

        private static uint Fold(uint h, Color value)
        {
            h = Fold(h, value.r);
            h = Fold(h, value.g);
            h = Fold(h, value.b);
            return Fold(h, value.a);
        }

        private static uint Fold(uint h, string value)
        {
            if (value == null) return Fold(h, 0);

            for (int i = 0; i < value.Length; i++)
                h = Fold(h, (int)value[i]);

            return h;
        }

        private static uint Fold(uint h, float[] value)
        {
            // Length first, and -1 for null. Without it [1,1] and [1,1,1] fold
            // identically once the trailing ones contribute nothing an author can
            // see, and a null would match an empty array - three different
            // statements collapsing into one checksum.
            if (value == null) return Fold(h, -1);

            h = Fold(h, value.Length);

            for (int i = 0; i < value.Length; i++)
                h = Fold(h, value[i]);

            return h;
        }
    }
}
