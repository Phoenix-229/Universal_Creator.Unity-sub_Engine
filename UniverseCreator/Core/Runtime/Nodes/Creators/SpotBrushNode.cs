// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SpotBrushNode.cs
//  Purpose: Hand-placed stamps. A list of spots, each with its own world position,
//           radius, height and falloff, accumulated into the field.
//
//  Spots are stored in world coordinates so a chunk only pays for the spots that
//  actually overlap it, and a spot straddling a chunk border resolves identically
//  from both sides.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    public enum SpotBlendMode
    {
        Add = 0,
        Max = 1,
        Replace = 2
    }

    [Serializable]
    public struct BrushSpot
    {
        public Vector2 position;    // world metres
        public float radius;        // world metres
        public float height;
        public float falloff;       // 0 hard edge, 1 fully soft

        public BrushSpot(Vector2 position, float radius, float height, float falloff)
        {
            this.position = position;
            this.radius = radius;
            this.height = height;
            this.falloff = falloff;
        }
    }

    [System.Serializable]
    public class SpotBrushNode : CreatorNode
    {
        public override string NodeName { get { return "Spot Brush"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Hand-placed height stamps at explicit world positions."; } }

        public List<BrushSpot> spots = new List<BrushSpot>();
        public SpotBlendMode blendMode = SpotBlendMode.Max;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            if (spots == null || spots.Count == 0) return;

            int heightmapResolution = m.rect.size.x;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float chunkMinX = originX;
            float chunkMinZ = originZ;
            float chunkMaxX = originX + (heightmapResolution - 1) * mppX;
            float chunkMaxZ = originZ + (heightmapResolution - 1) * mppZ;

            for (int s = 0; s < spots.Count; s++)
            {
                if (stop != null && stop.StopRequested) return;

                BrushSpot spot = spots[s];
                float r = Mathf.Max(0.0001f, spot.radius);

                // Skip spots that cannot touch this chunk.
                if (spot.position.x + r < chunkMinX || spot.position.x - r > chunkMaxX) continue;
                if (spot.position.y + r < chunkMinZ || spot.position.y - r > chunkMaxZ) continue;

                // Restrict the loop to the spot's bounding box in pixel space.
                int x0 = Mathf.Max(0, Mathf.FloorToInt((spot.position.x - r - chunkMinX) / Mathf.Max(1e-6f, mppX)));
                int x1 = Mathf.Min(heightmapResolution - 1, Mathf.CeilToInt((spot.position.x + r - chunkMinX) / Mathf.Max(1e-6f, mppX)));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((spot.position.y - r - chunkMinZ) / Mathf.Max(1e-6f, mppZ)));
                int z1 = Mathf.Min(heightmapResolution - 1, Mathf.CeilToInt((spot.position.y + r - chunkMinZ) / Mathf.Max(1e-6f, mppZ)));

                for (int z = z0; z <= z1; z++)
                {
                    int rowStart = z * heightmapResolution;

                    for (int x = x0; x <= x1; x++)
                    {
                        float wx = originX + x * mppX;
                        float wz = originZ + z * mppZ;

                        float dx = wx - spot.position.x;
                        float dz = wz - spot.position.y;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz) / r;
                        if (dist >= 1f) continue;

                        float t = 1f - dist;
                        float edge = Mathf.Clamp01(spot.falloff);
                        if (edge > 1e-5f) t = InterpolationMath.SmoothStep(0f, edge, t);

                        float contribution = t * spot.height;
                        int i = rowStart + x;

                        switch (blendMode)
                        {
                            case SpotBlendMode.Add:     m.arr[i] += contribution; break;
                            case SpotBlendMode.Replace: m.arr[i] = contribution; break;
                            default:                    m.arr[i] = Mathf.Max(m.arr[i], contribution); break;
                        }
                    }
                }
            }
        }
    }
}
