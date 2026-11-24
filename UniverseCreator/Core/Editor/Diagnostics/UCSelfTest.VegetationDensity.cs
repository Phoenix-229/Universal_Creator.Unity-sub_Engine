// UC-T06-17: actual grass placement at several world-cell areas reproduces fractional-density loss without a density-default change.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationDensityDiagnosis()
        {
            var place = typeof(GrassDetailScatterNode).GetMethod("PlaceInstances", BindingFlags.Instance | BindingFlags.NonPublic);
            var node = new GrassDetailScatterNode { seed = 75431, patchiness = 0, maxSlope = 90, shoreBand = false };
            foreach (int resolution in new[] { 32, 64, 128 })
            {
                var tile = new TileData(Coord.Zero, new CoordRect(0, 0, resolution, resolution), new Vector3(-16, 0, -16), new Vector3(32, 100, 32), 100);
                try
                {
                    Matrix height = tile.CreateMatrix(); Array.Fill(height.arr, 0.5f);
                    tile.ecology = new EcoScatterContext { settings = new EcoSampleSettings { grassDensityPerM2 = 1, densityScale = 1 } };
                    place.Invoke(node, new object[] { height, null, tile, new List<ScatterInstance>(), null });
                    int[,] grid = tile.outputs.detailLayers[node.detailLayerIndex];
                    long count = 0; foreach (int value in grid) count += value;
                    Check(count >= 870 && count <= 1178,
                        "UC-T06-17 actual " + resolution + "-cell grass placement retains one blade/m² over 1,024 m² within its deterministic quantization envelope: " + count);
                    Check(tile.outputs.vegetationPlacementReports["direct:placement:grass density"].Outputs == count &&
                        tile.outputs.vegetationPlacementReports["direct:placement:grass density"].Units == "cells -> blades",
                        "UC-T06-17 grass-stage evidence reports actual blade counts separately from tested cell counts.");
                    int[,] before = (int[,])grid.Clone();
                    tile.ReleaseProductMemory(); tile.ClearOutputs(); // UC-T06-17: use the installed output/publication retirement contract before repeating a pass.
                    place.Invoke(node, new object[] { height, null, tile, new List<ScatterInstance>(), null });
                    grid = tile.outputs.detailLayers[node.detailLayerIndex]; bool same = true;
                    for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++) same &= grid[z, x] == before[z, x];
                    Check(same, "UC-T06-17 grass count dithering is deterministic across repeated placement at absolute negative world coordinates.");
                    tile.ecology.settings.grassDensityPerM2 = 0;
                    tile.ReleaseProductMemory(); tile.ClearOutputs(); place.Invoke(node, new object[] { height, null, tile, new List<ScatterInstance>(), null });
                    Check(tile.outputs.vegetationPlacementReports["direct:placement:grass density"].Outputs == 0,
                        "UC-T06-17 zero authored density remains an explicit empty result rather than falling back to a denser node default.");
                }
                catch (Exception error) { Check(false, "UC-T06-17 grass density fixture exception: " + error); }
                finally { tile.ReleaseProductMemory(); }
            }
        }
    }
}
