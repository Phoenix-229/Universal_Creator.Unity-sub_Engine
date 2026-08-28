// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    DualLODSystem.cs
//  Purpose: 25+1 LOD policy. Classifies each virtual-grid chunk as High, Low, or
//           Buffer by its ring distance from the player-centred chunk, and picks a
//           heightmap resolution for that class. Pure math — no Unity objects — so
//           ChunkManager can call it from anywhere.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Where a chunk sits relative to the player, by ring distance.</summary>
    public enum LODLevel
    {
        High = 0,    // centre square: full resolution, colliders on
        Low = 1,     // outer visible ring: reduced resolution
        Buffer = 2   // precomputed ring just outside the visible grid
    }

    /// <summary>
    /// 25+1 LOD policy: the centre square is high fidelity, the surrounding visible
    /// ring is low-poly, and one buffer ring beyond the visible grid is precomputed
    /// so streaming in never shows a seam or lets the player fall through.
    /// </summary>
    public static class DualLODSystem
    {
        /// <summary>
        /// Chebyshev (chessboard) ring distance from the centre chunk. 0 is the
        /// centre itself, 1 is the eight chunks around it, and so on.
        /// </summary>
        public static int RingDistance(int chunkX, int chunkZ, int centreX, int centreZ)
        {
            return Mathf.Max(Mathf.Abs(chunkX - centreX), Mathf.Abs(chunkZ - centreZ));
        }

        /// <summary>
        /// Classifies a chunk. Anything within the high-LOD ring is High; anything
        /// still inside the visible grid is Low; anything beyond it (but inside the
        /// buffer) is Buffer.
        /// </summary>
        public static LODLevel Classify(int ringDistance, int highLODRingSize, int chunkCountPerSide)
        {
            // highLODRingSize is the side length of the centre square (odd), so its
            // ring radius is (size-1)/2. Same for the whole visible grid.
            int highRadius = (highLODRingSize - 1) / 2;
            int visibleRadius = (chunkCountPerSide - 1) / 2;

            if (ringDistance <= highRadius) return LODLevel.High;
            if (ringDistance <= visibleRadius) return LODLevel.Low;
            return LODLevel.Buffer;
        }

        /// <summary>
        /// Heightmap resolution for a LOD class.
        ///
        /// Every class returns the SAME resolution, deliberately. Terrain.SetNeighbors
        /// requires neighbouring TerrainData to share heightmapResolution exactly; when
        /// this scaled the span per ring (1/2/4) Unity refused every stitch and logged
        /// "…neighbor of the terrain has a different heightmap resolution. Stop
        /// neighboring." once per edge per pass, so the grid never seamed and the cracks
        /// stayed. Resolution is therefore not the LOD dial - the heightmap pixel error is,
        /// derived per cell from the visual-acuity budget (L-49/L-50) by
        /// ChunkManager.ApplyDensity and Geometry.TerrainDensityController.
        ///
        /// Kept as a method rather than deleted so callers keep a single place to ask,
        /// and so re-introducing per-ring resolution has to be a deliberate edit here.
        /// </summary>
        public static int ResolutionFor(LODLevel level, int baseResolution)
        {
            return baseResolution;
        }

        /// <summary>
        /// Whether a chunk at this LOD gets a physics collider.
        ///
        /// Every visible cell does - High and Low both. Only the Buffer ring, which is
        /// outside the visible grid entirely, goes without.
        ///
        /// This used to be High only, on the reasoning that the player never stands on
        /// the outer ring. That is not true at the moment it matters: the grid re-centres
        /// on the focus transform, so a player walking outward crosses onto cells that
        /// were Low until the reclassification catches up. For those frames the ground
        /// ahead had no collider and the character fell through it - a hole with no
        /// corresponding hole in the surface. A collider on the visible ring closes that
        /// window, and it costs only the heightfield bake for cells that are already
        /// resident and already drawn.
        /// </summary>
        public static bool ShouldHaveCollider(LODLevel level, bool generateColliders)
        {
            if (!generateColliders) return false;
            return level == LODLevel.High || level == LODLevel.Low;
        }
    }
}
