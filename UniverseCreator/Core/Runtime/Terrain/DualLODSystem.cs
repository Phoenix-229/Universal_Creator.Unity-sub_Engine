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
        /// stayed. Resolution is therefore not the LOD dial - PixelErrorFor is.
        ///
        /// Kept as a method rather than deleted so callers keep a single place to ask,
        /// and so re-introducing per-ring resolution has to be a deliberate edit here.
        /// </summary>
        public static int ResolutionFor(LODLevel level, int baseResolution)
        {
            return baseResolution;
        }

        /// <summary>
        /// Screen-space error in pixels for a LOD class. This is Unity's native terrain
        /// LOD: the renderer decimates each patch until its projected error reaches this
        /// budget, continuously and per-patch, with no change to the stored heightfield.
        /// Distant chunks tolerate more error and so draw far fewer triangles, which is
        /// the saving the resolution divisor used to buy - minus the broken seams and
        /// minus re-baking a cell every time it changes ring.
        /// </summary>
        public static float PixelErrorFor(LODLevel level, float basePixelError)
        {
            float scale;
            switch (level)
            {
                case LODLevel.High: scale = 1f; break;
                case LODLevel.Low: scale = 2f; break;
                default: scale = 4f; break;   // Buffer
            }

            // Unity's own ceiling. Past 200 the setting is ignored rather than clamped.
            return Mathf.Clamp(basePixelError * scale, 1f, 200f);
        }

        /// <summary>
        /// Whether a chunk at this LOD gets a physics collider. High-LOD chunks
        /// (where the player stands) always do; Low and Buffer skip it unless the
        /// caller forces it, since the player is never on them. This is the
        /// anti-fall-through guarantee: the centre ring is never collider-free.
        /// </summary>
        public static bool ShouldHaveCollider(LODLevel level, bool generateColliders)
        {
            if (!generateColliders) return false;
            return level == LODLevel.High;
        }
    }
}
