// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RiverBankZones.cs
//  Purpose: Step 9 of the river rebuild - the procedural bank/trench dressing, as
//           bands measured off the river's own distance field (the lateral distance
//           to the centreline the carve already computes). This is the MANDATORY
//           bank skirt: it tells a scatter pass which band a point sits in - open
//           channel, waterline, bank, or floodplain - so reeds land at the water's
//           edge, rocks and grass on the bank, and larger plants out on the
//           floodplain, with a deterministic density so the same world always
//           dresses the same way.
//
//  WHY BANDS OFF A DISTANCE, NOT A PAINTED MASK
//  --------------------------------------------
//  The carve already knows, for any world point, its lateral distance to the
//  channel centreline and the local half-width (TryRiverSurfaceAt). A band is just
//  a comparison against that distance, so the dressing follows the channel exactly,
//  for free, however the course meanders - no second mask to author or keep in sync.
//
//  NO HARDCODED CONTENT. This file decides WHERE and HOW DENSELY to dress, never
//  WHAT with: the prototype slots the scatter fills ship empty for the buyer, per
//  the project's no-hardcoded-asset rule. Density is a pure hash of the world cell
//  (the same seeded-hash discipline the scatter nodes use - never System.Random),
//  so it is deterministic and reproducible.
//
//  Pure managed C#: comparisons and an integer hash, no Unity API beyond Mathf.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Which dressing band a point falls in, measured from the channel centreline.</summary>
    public enum RiverBankZone
    {
        /// <summary>Open water, inside the channel - no bank dressing.</summary>
        Channel,
        /// <summary>The water's edge - reeds and marsh plants.</summary>
        Waterline,
        /// <summary>The bank just outside the water - rocks, grass, bank cover.</summary>
        Bank,
        /// <summary>The floodplain beyond the bank - larger riparian plants.</summary>
        Floodplain,
        /// <summary>Beyond the river's influence - ordinary ground, not dressed by the river.</summary>
        Dry
    }

    /// <summary>Classifies a point's river dressing band and its deterministic density.</summary>
    public static class RiverBankZones
    {
        /// <summary>Fraction of the half-width that reads as the waterline reed band.</summary>
        public const float WaterlineFraction = 0.1f;

        /// <summary>
        /// Classifies a lateral distance (metres from the centreline) given the local
        /// half-width, the bank band width and the floodplain band width (both metres
        /// beyond the water's edge).
        /// </summary>
        public static RiverBankZone Classify(float lateralM, float halfWidthM,
                                             float bankBandM, float floodplainM)
        {
            if (halfWidthM <= 0f) return RiverBankZone.Dry;

            float waterline = halfWidthM * (1f - Mathf.Clamp01(WaterlineFraction));
            if (lateralM <= waterline) return RiverBankZone.Channel;
            if (lateralM <= halfWidthM) return RiverBankZone.Waterline;

            float outside = lateralM - halfWidthM;
            if (outside <= Mathf.Max(0f, bankBandM)) return RiverBankZone.Bank;
            if (outside <= Mathf.Max(0f, bankBandM) + Mathf.Max(0f, floodplainM)) return RiverBankZone.Floodplain;
            return RiverBankZone.Dry;
        }

        /// <summary>
        /// A deterministic 0..1 density for a world cell, seeded so the same world
        /// always dresses the same way. FNV-1a over the cell coordinates and the seed -
        /// the same seeded-hash discipline the scatter nodes use, never System.Random,
        /// so it is reproducible between runs and machines.
        /// </summary>
        public static float Density01(int cellX, int cellZ, int seed)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)cellX) * 16777619u;
                h = (h ^ (uint)cellZ) * 16777619u;
                h = (h ^ (uint)seed) * 16777619u;
                // Final avalanche so neighbouring cells do not correlate.
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                return (h & 0xFFFFFF) / (float)0x1000000;   // 24-bit mantissa -> [0,1)
            }
        }

        /// <summary>
        /// Whether a point should carry a scatter instance of a given base density for
        /// its zone: the per-zone density scales the hash threshold, so the waterline
        /// can be dense with reeds while the floodplain is sparse. Channel and Dry
        /// never dress.
        /// </summary>
        public static bool ShouldDress(RiverBankZone zone, float zoneDensity01,
                                       int cellX, int cellZ, int seed)
        {
            if (zone == RiverBankZone.Channel || zone == RiverBankZone.Dry) return false;
            if (zoneDensity01 <= 0f) return false;
            return Density01(cellX, cellZ, seed) < Mathf.Clamp01(zoneDensity01);
        }
    }
}
