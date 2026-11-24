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

        /// <summary>
        /// WAT-11: restrict coverage to a band along the water's edge, so reeds and
        /// marsh grass band a shore even with ecology switched off. Off by default, so
        /// an existing grass layer is untouched.
        /// </summary>
        public bool shoreBand = false;

        /// <summary>How far inland the shore band reaches, in metres from the water's edge.</summary>
        public float shoreBandEndM = 6f;

        /// <summary>
        /// The last part of the band fades out over this distance instead of ending on a
        /// line - the same reasoning as the slope falloff above: a hard edge reads as a
        /// painted strip rather than as a stand thinning out.
        /// </summary>
        public float shoreBandFalloffM = 2f;

        public override bool WasStubbed { get { return false; } }

        /// <summary>
        /// WAT-11: 1 at the water's edge, unchanged until the fade begins, 0 at the reach
        /// and beyond. A zero or negative reach returns 0 - "no band" has to mean no
        /// grass, because the other direction is the one that silently carpets a world.
        /// </summary>
        internal static float ShoreBandWeight(float distanceM, float endM, float falloffM)
        {
            if (endM <= 0f) return 0f;

            float feather = Mathf.Max(0.01f, falloffM);
            return Mathf.Clamp01((endM - Mathf.Max(0f, distanceM)) / feather);
        }

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
            float expectedPerCell = cap; // UC-T06-17: preserve fractional metre-area density until the final count quantization.
            int[,] grid = new int[resZ, resX];

            // ---- V-13: the ceiling, and whether ecology has a say ---------------
            //
            // With a vegetation manager present the per-cell ceiling is its shipped
            // blades-per-square-metre figure times this cell's own ground area, so the
            // authored number is a density rather than a per-cell integer whose meaning
            // changes with grid resolution. ScatterApplier preserves that density when
            // it resamples to the terrain's own detail grid.
            Ecology.EcoScatterContext ctx = tile != null ? tile.ecology : null;
            bool ecoActive = false;

            if (ctx != null)
            {
                float cellArea = mppX * mppZ; // UC-T06-17: density uses the actual sampled world area, without inflating tiny cells.
                if (float.IsNaN(cellArea) || float.IsInfinity(cellArea) || cellArea <= 0) throw new System.InvalidOperationException("Grass cells require finite positive world area.");
                float perCell = Ecology.EcoDensity.CeilingPerSquareMetre(
                    Channel, in ctx.settings, density) * cellArea;

                if (float.IsNaN(perCell) || float.IsInfinity(perCell) || perCell < 0 || perCell >= int.MaxValue - 1024f)
                    throw new System.InvalidOperationException("Grass density and world cell area must produce finite nonnegative expected counts.");
                expectedPerCell = perCell;

                // A manager asking for no grass gets none, and gets the empty grid
                // written anyway so a previous build's blades are cleared rather than
                // left standing.
                if (expectedPerCell <= 0)
                {
                    tile.outputs.detailLayers[detailLayerIndex] = grid;
                    VegetationPlacementReport.Publish(tile, "grass density", (long)resX * resZ, 0, "cells -> blades",
                        "Resolved grass density is zero; check grass/m² and global density settings.");
                    return;
                }

                ctx.EnsureBound(height);
                ecoActive = ctx.CanSelect() && ctx.details != null && ctx.details.IsUsable();
            }

            // Scratch for the per-cell suitability, allocated once for the tile. The
            // detail table is reached through density rather than through selection -
            // there is no instance here to name a species on - so only the best factor
            // any of its species achieves is read.
            float[] ecoWeights = null;
            float[] ecoSuitabilities = null;
            float ecoFloor = 0f;

            if (ecoActive)
            {
                int speciesCount = Mathf.Max(1, ctx.details.count);
                ecoWeights = new float[speciesCount];
                ecoSuitabilities = new float[speciesCount];
                ecoFloor = ctx.SuitabilityFloor;
            }

            // WAT-11. Resolved exactly the way the land scatter nodes resolve it, so the
            // grass band and the tree/shrub shore filters cannot disagree about where the
            // water is: the shared whole-world drainage solve, with the authored normalised
            // sea level converted through that solve's own vertical frame. Only paid for
            // when an author asks for the band.
            Ecology.HydroField shore = null;
            if (shoreBand)
            {
                WorldDrainage wd = WorldDrainage.Solve(
                    this, 0, tile, SeaLevelFloor.DefaultResolution, 0f, false, stop);
                if (wd != null && wd.height != null)
                {
                    float baseY = wd.height.worldPos.y;
                    float scaleY = wd.height.worldHeight;
                    shore = Ecology.HydroField.For(wd, baseY + Mathf.Clamp01(seaLevel) * scaleY);
                }
            }

            long maskRejected = 0, terrainRejected = 0, habitatRejected = 0, waterRejected = 0, bladeCount = 0;
            Noise countDither = new Noise(seed + 982451653); // UC-T06-17: independent, world-coordinate count stream; changes no masks or habitat rules.
            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                int row = z * resX;
                float wz = originZ + z * mppZ;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float wx = originX + x * mppX;

                    float weight = mask != null ? Mathf.Clamp01(mask.arr[i]) : 1f;
                    if (weight <= 0.001f) { maskRejected++; continue; }

                    float h = height.arr[i];
                    if (h < minHeight || h > maxHeight) { terrainRejected++; continue; }

                    if (slopes != null)
                    {
                        float angle = slopes.arr[i];
                        if (angle < minSlope || angle > maxSlope) { terrainRejected++; continue; }

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
                        float p = patchNoise.Perlin(wx / safePatch, wz / safePatch);
                        weight *= Mathf.Lerp(1f, Mathf.Clamp01(p * 1.6f), patchAmount);
                    }

                    // Sampled after the cheap filters, never before: a cell already
                    // rejected on slope or mask must not pay for an environment sample.
                    if (ecoActive)
                    {
                        Ecology.EnvironmentSample site = ctx.sampler.Sample(wx, wz);

                        weight *= Ecology.EcoDensity.BestSiteFactor(
                            ctx.details, in site, ecoFloor, ecoWeights, ecoSuitabilities);

                        // V-24. Bare ground suppresses grass the same way it suppresses a
                        // stand: a snowfield, a scree slope or a saline pan carries no sward
                        // either, and over the same hard edge. Opt-in; off changes nothing.
                        if (ctx.settings.assertBareGround)
                        {
                            Ecology.BareGroundState bare = Ecology.BareGround.Resolve(
                                in site, ctx.settings.maxSlopeDeg, patchNoise);
                            weight *= Ecology.BareGround.VegetationFactor(in bare);
                        }

                        if (weight <= 0.001f) { habitatRejected++; continue; }
                    }

                    // WAT-11. The band is a RESTRICTION, like the height and slope bands
                    // above, not a boost: a shore band that left the rest of the field
                    // untouched would be a second grass layer, not banding.
                    if (shore != null)
                    {
                        float dWater = shore.DistanceToWaterM(wx, wz);

                        // Inside a body the distance is zero, which is the STRONGEST band
                        // weight - so submersion has to be excluded by depth rather than
                        // by distance. The sampled surface is the pool level under water
                        // and the cell's own ground everywhere else, so a positive
                        // difference is exactly "this cell's roots would be wet".
                        float groundY = height.worldPos.y + h * height.worldHeight;
                        bool underWater = shore.WaterSurfaceY(wx, wz) > groundY + 0.02f;

                        weight *= underWater
                                ? 0f
                                : ShoreBandWeight(dWater, shoreBandEndM, shoreBandFalloffM);
                        if (weight <= 0.001f) { waterRejected++; continue; }
                    }

                    float exact = weight * expectedPerCell;
                    if (float.IsNaN(exact) || float.IsInfinity(exact) || exact < 0) throw new System.InvalidOperationException("Grass mask/suitability produced an invalid count.");
                    int count = ctx == null ? Mathf.RoundToInt(exact) : Mathf.FloorToInt(exact);
                    if (ctx != null && exact - count > countDither.Random(Mathf.RoundToInt(wx * 100f), Mathf.RoundToInt(wz * 100f))) count++;
                    if (count > 0) { grid[z, x] = count; bladeCount += count; }
                }
            }

            tile.outputs.detailLayers[detailLayerIndex] = grid;
            // UC-T06-17: observed cell rejections precede blade counts; native uploads and renderer counts remain separate.
            long remaining = (long)resX * resZ;
            VegetationPlacementReport.Publish(tile, "grass mask", remaining, remaining - maskRejected, "cells"); remaining -= maskRejected;
            VegetationPlacementReport.Publish(tile, "grass terrain", remaining, remaining - terrainRejected, "cells"); remaining -= terrainRejected;
            VegetationPlacementReport.Publish(tile, "grass suitability", remaining, remaining - habitatRejected, "cells"); remaining -= habitatRejected;
            VegetationPlacementReport.Publish(tile, "grass shore", remaining, remaining - waterRejected, "cells"); remaining -= waterRejected;
            VegetationPlacementReport.Publish(tile, "grass density", remaining, bladeCount, "cells -> blades",
                "Expected blades/cell " + expectedPerCell + ". Fractional metre-area density is dithered; prototype/native/quality budgets remain downstream.");
        }
    }
}
