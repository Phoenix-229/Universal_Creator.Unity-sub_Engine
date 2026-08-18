// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: coverage solver implemented
//  File:    GrassDetailScatterNode.cs
//  Purpose: Fills a Unity detail layer with grass/undergrowth coverage.
//
//  Grass is the one scatter type that does NOT go through the dart solver. Unity
//  stores detail as a per-cell density integer grid, and a realistic 4 blades per
//  square metre over a 1 km chunk is four million instances - a blue-noise solve
//  at that count would exhaust maxInstancesPerChunk thousands of times over and
//  throw away the result anyway, because the grid does not record positions.
//
//  So this node writes coverage directly: mask weight times the same height and
//  slope bands the other scatter types are filtered by, scaled to a per-cell
//  count. ScatterApplier resamples the grid to the terrain's detailResolution.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class GrassDetailScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Grass Detail"; } }
        public override string Tooltip { get { return "Fills a Unity detail layer with grass coverage."; } }
        public override string Channel { get { return "grass"; } }

        /// <summary>Index into the terrain's detail prototype array.</summary>
        public int detailLayerIndex;

        /// <summary>Maximum per-cell density written into the detail grid.</summary>
        public int maxDensityPerCell = 8;

        /// <summary>
        /// Thins coverage with a noise field so the layer breaks into patches
        /// instead of carpeting every cell that passes the filters.
        /// </summary>
        public float patchiness = 0.35f;

        /// <summary>Patch size in world metres.</summary>
        public float patchScale = 40f;

        public override bool WasStubbed { get { return false; } }

        // Grass never throws darts, so the dart solve is skipped outright rather
        // than run and discarded.
        protected override List<ScatterCandidate> SolvePlacement(
            Matrix height, Matrix mask, TileData tile, StopToken stop)
        {
            return new List<ScatterCandidate>();
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            if (height == null) return;

            int resX = height.rect.size.x;
            int resZ = height.rect.size.z;
            if (resX < 1 || resZ < 1) return;

            // Slope in degrees, computed once for the tile. Only paid for when the
            // node actually restricts slope.
            bool needSlope = minSlope > 0f || maxSlope < 90f;
            Matrix slopes = needSlope ? MatrixOperations.SlopeAngles(height) : null;

            Noise patchNoise = new Noise(seed + 6151);
            float safePatch = Mathf.Max(1f, patchScale);
            float patchAmount = Mathf.Clamp01(patchiness);

            float mppX = height.PixelSize.x;
            float mppZ = height.PixelSize.y;
            float originX = height.worldPos.x;
            float originZ = height.worldPos.z;

            int cap = Mathf.Max(1, maxDensityPerCell);
            int[,] grid = new int[resZ, resX];

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                int row = z * resX;
                float wz = originZ + z * mppZ;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    float weight = mask != null ? Mathf.Clamp01(mask.arr[i]) : 1f;
                    if (weight <= 0.001f) continue;

                    float h = height.arr[i];
                    if (h < minHeight || h > maxHeight) continue;

                    if (slopes != null)
                    {
                        float angle = slopes.arr[i];
                        if (angle < minSlope || angle > maxSlope) continue;

                        // Same soft cutoff the dart solver uses, so a grass field and
                        // a tree field thin out over the same few degrees instead of
                        // one stopping dead where the other fades.
                        if (slopeFalloff > 0.01f)
                        {
                            float toEdge = Mathf.Min(angle - minSlope, maxSlope - angle);
                            weight *= Mathf.Clamp01(toEdge / slopeFalloff);
                        }
                    }

                    if (patchAmount > 0.001f)
                    {
                        float wx = originX + x * mppX;
                        float p = patchNoise.Perlin(wx / safePatch, wz / safePatch);
                        weight *= Mathf.Lerp(1f, Mathf.Clamp01(p * 1.6f), patchAmount);
                    }

                    int count = Mathf.RoundToInt(weight * cap);
                    if (count > 0) grid[z, x] = count;
                }
            }

            tile.outputs.detailLayers[detailLayerIndex] = grid;
        }
    }
}
