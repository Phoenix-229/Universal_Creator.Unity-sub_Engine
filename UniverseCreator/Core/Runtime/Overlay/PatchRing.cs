// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PatchRing.cs
//  Purpose: The addressing maths for the camera-following micro-relief patch ring.
//           A fixed n×n pool of patch meshes follows the camera by SNAPPING its
//           centre in whole-patch steps and RE-ADDRESSING which world cell each
//           physical patch draws - it never allocates a new patch and never rebuilds
//           the pool. This file is the pure, allocation-free core of that: integer
//           cell maths only, so it is directly testable without a scene and cannot
//           itself allocate on the hot path.
//
//  THE TOROIDAL RE-ADDRESS, AND WHY IT COSTS ZERO BYTES
//  ----------------------------------------------------
//  Each physical patch slot is pinned to a fixed residue class mod n. The window of
//  world cells the ring shows is [centre-half, centre+half] on each axis. Mapping a
//  slot's residue into that window means that when the camera crosses one patch
//  boundary and the centre steps by one, exactly ONE slot per axis wraps from the
//  trailing edge to the leading edge - its world cell jumps by n. Nothing is created,
//  nothing is destroyed; a single Transform is repositioned. That is the whole reason
//  a boundary crossing can be proven to allocate zero bytes (G-16 done-when).
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// Pure, static addressing for the patch ring: which patch cell the camera sits in, and which
    /// world cell each fixed slot must draw so the ring re-addresses instead of rebuilding. Integer
    /// maths only - it allocates nothing, which is exactly the property the ring's snap path needs.
    /// </summary>
    public static class PatchRing
    {
        /// <summary>Grid side (odd) for a quality tier: 5 (Low) / 7 (Medium) / 9 (High and Ultra).</summary>
        public static int GridSideForTier(AdaptiveQuality.QualityTier tier)
        {
            switch (tier)
            {
                case AdaptiveQuality.QualityTier.Low:    return 5;
                case AdaptiveQuality.QualityTier.Medium: return 7;
                default:                                 return 9; // High / Ultra
            }
        }

        /// <summary>Floored modulo that is always in [0, m) even for a negative value.</summary>
        public static int FloorMod(int a, int m)
        {
            int r = a % m;
            return r < 0 ? r + m : r;
        }

        /// <summary>
        /// The patch cell a world coordinate falls in, snapped in whole-patch steps. Stable while the
        /// camera stays inside a patch and stepping by one exactly at the halfway boundary.
        /// </summary>
        public static int CentreCell(float worldCoord, float patchSizeM)
        {
            if (patchSizeM <= 1e-6f) return 0;
            return Mathf.RoundToInt(worldCoord / patchSizeM);
        }

        /// <summary>
        /// The world cell a fixed physical slot must draw, given the ring centre. <paramref name="slotBase"/>
        /// is the slot's index along one axis in [0, gridN). The result is the one cell in the window
        /// [centre-half, centre+half] whose value is congruent to that slot's residue mod gridN, so each
        /// slot owns a fixed residue class and only the wrapping edge re-addresses when the centre moves.
        /// </summary>
        public static int WorldCell(int slotBase, int centreCell, int gridN)
        {
            int half = gridN / 2;
            int lo = centreCell - half;
            int shift = FloorMod(slotBase - FloorMod(lo, gridN), gridN);
            return lo + shift;
        }

        /// <summary>
        /// The world-space centre of a patch cell along one axis. Cells are patch-sized and centred on
        /// their integer index, so cell k spans [k·size - size/2, k·size + size/2].
        /// </summary>
        public static float CellCentreM(int cell, float patchSizeM)
        {
            return cell * patchSizeM;
        }
    }
}
