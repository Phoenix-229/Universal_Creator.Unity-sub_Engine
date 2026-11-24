// UC-T05-06: accepted-product ownership, physical normals/water and actual Texture Apply share the native vegetation fixture.
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Windows;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWorldGraphProducts(UniverseCreatorObject world, UnityEngine.Terrain terrain, OutputBag planting)
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            var foreign = new GameObject("Foreign accepted-product world"); foreign.SetActive(false);
            var waterObject = new GameObject("Accepted graph-input water"); waterObject.transform.SetParent(world.transform, false);
            var window = ScriptableObject.CreateInstance<TextureGraphWindow>();
            string folder = "Assets/UCGraphProductAcceptance_" + Guid.NewGuid().ToString("N");
            GraphProductHandle<RiverChannelSnapshot> first = null, sibling = null, replacement = null;
            GraphProductHandle<SurfaceProduct> materialPin = null;
            try
            {
                graph.SetDomain(UCGraphDomain.TextureMaterialShader);
                Vector3 centre = terrain.transform.position + terrain.terrainData.size * 0.5f;
                var bindings = world.GetComponent<UCWorldGraphBindings>();
                var run = world.GenerationScheduler.CurrentRun;
                Check(WorldGraphProducts.TryAcquireGround(world, null, centre, out first, out _) &&
                    WorldGraphProducts.TryAcquireGround(world, world.graph, centre, out sibling, out _) &&
                    first.TryGet(run, first.PublicationVersion, GraphProductUnits.WorldMetres, out var ground) &&
                    sibling.TryGet(run, GraphProductUnits.WorldMetres, out var same) && ReferenceEquals(ground, same),
                    "UC-T05-06 two consumers share one accepted ground payload with explicit world/revision/domain/units metadata.");
                first.TryGet(run, GraphProductUnits.WorldMetres, out var acceptedGround);
                var other = foreign.AddComponent<UniverseCreatorObject>(); other.enabled = false; other.graph = world.graph;
                Check(!first.TryGet(other.GenerationScheduler.CurrentRun, GraphProductUnits.WorldMetres, out _) &&
                    !first.TryGet(run, GraphProductUnits.Coverage, out _) && !first.TryGet(run, Guid.NewGuid(), GraphProductUnits.WorldMetres, out _) &&
                    !WorldGraphProducts.TryAcquireGround(other, null, centre, out _, out _) &&
                    !WorldGraphProducts.TryAcquireGround(world, graph, centre, out _, out _),
                    "UC-T05-06 foreign worlds, incompatible main selectors, units and same-run publication mismatches are rejected without regeneration.");
                sibling.Dispose(); sibling.Dispose();
                Check(sibling.IsReleased && !sibling.TryGet(run, GraphProductUnits.WorldMetres, out _) && first.TryGet(run, GraphProductUnits.WorldMetres, out _),
                    "UC-T05-06 retiring one consumer twice preserves the independent sibling pin.");
                Check(UCNodeView.CanSelectMainGraph(world.graph) && !UCNodeView.CanSelectMainGraph(graph),
                    "UC-T05-06 the actual main-graph picker rejects companion assets while admitting the validated main graph.");

                var plane = new float[3, 3]; for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++) plane[z, x] = (2 * x + 3 * z) / 100f;
                var sloped = new RiverChannelSnapshot(run, "normal-oracle", new Rect(0, 0, 2, 2), Array.Empty<ChannelGraphPublication>(), plane, 0, 100);
                Vector3 expectedNormal = new Vector3(-2, 1, -3).normalized;
                Check(Vector3.Distance(sloped.GroundNormal(1, 1), expectedNormal) < 1e-5f &&
                    Vector3.Distance(sloped.GroundNormal(0, 2), expectedNormal) < 1e-5f,
                    "UC-T05-06 accepted normals match an independent physical plane at the interior and one-sided boundary.");
                var holes = new bool[,] { { false, true }, { true, true } };
                var perforated = new RiverChannelSnapshot(run, "hole-oracle", new Rect(0, 0, 2, 2), Array.Empty<ChannelGraphPublication>(), plane, 0, 100, holes);
                Check(!perforated.TryGround(0.25f, 0.25f, out _) && perforated.GroundNormal(0.25f, 0.25f) == Vector3.up,
                    "UC-T05-06 holes are absent accepted ground rather than fabricated height or a nonfinite normal.");

                var volume = waterObject.AddComponent<UCWaterVolume>(); volume.WorldOwner = world;
                var wet = JunctionQuad(centre.x - 2, centre.x + 2, centre.z - 2, centre.z + 2, false);
                volume.SetAcceptedSurface(wet, Matrix4x4.identity, false);
                var water = WorldGraphWaterInput.Capture(world);
                var environment = bindings.CaptureEnvironmentInputs(true);
                var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 3, 3), terrain.transform.position, terrain.terrainData.size, terrain.terrainData.size.y);
                tile.AssignRun(run); tile.AttachMemory(world.ProductMemory);
                try
                {
                    bindings.BindAcceptedInputs(tile, world.graph, acceptedGround, water, environment);
                    var input = new WorldGraphInputNode();
                    Matrix coverage = (Matrix)input.Generate(5, tile, null), temperature = (Matrix)input.Generate(6, tile, null);
                    Check(coverage.arr[4] == 1 && coverage.arr[0] == 0 && Mathf.Abs(temperature.arr[4] - 4) < 1e-5f,
                        "UC-T05-06 accepted water footprint and immutable climate bias reach the real input node with their native units.");
                    volume.SetAcceptedSurface(null, Matrix4x4.identity);
                    Check(!water.IsCurrent() && ((Matrix)input.Generate(5, tile, null)).arr[4] == 1,
                        "UC-T05-06 producer replacement makes admission stale while a pinned worker still safely reads its captured water payload.");
                }
                finally { tile.ReleaseProductMemory(); }

                var source = new SurfaceProduct(); bindings.AcceptTexture(graph, acceptedGround, source);
                Check(WorldGraphProducts.TryAcquireSurface(world, null, centre, out materialPin, out _) && materialPin.ProducerDomain == UCGraphDomain.TextureMaterialShader,
                    "UC-T05-06 material descriptors have their own producer domain, bounds, version and independent consumer pin.");
                var acceptedInput = new WorldGraphInputNode { id = "product-main-input" };
                var tint = new VegetationColorNode { id = "product-tint", season = 0.25f };
                var output = new ShaderOutputNode { id = "product-texture-output", outputFolder = folder, materialName = "ProductFixture", writeTerrainLayers = false };
                graph.nodes.Add(acceptedInput); graph.nodes.Add(tint); graph.nodes.Add(output);
                graph.connections.Add(new Connection(acceptedInput.id, 9, tint.id, 0));
                graph.connections.Add(new Connection(tint.id, 0, output.id, 0));
                float[,] before = terrain.terrainData.GetHeights(0, 0, 33, 33);
                window.SetGraph(graph);
                typeof(TextureGraphWindow).GetField("targetTerrain", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, terrain);
                typeof(TextureGraphWindow).GetMethod("ApplyToTerrain", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                WaitTextureBake(window); // UC-T07-09: the same production Apply now yields during graph baking.
                Check(bindings.TextureRevision == 2 && bindings.TextureGraph == graph && terrain.materialTemplate != null &&
                    world.GenerationScheduler.CurrentRun.Equals(run) && terrain.terrainData.GetHeights(0, 0, 1, 1)[0, 0] == before[0, 0] &&
                    planting.scatter["trees"].Count > 0,
                    "UC-T05-06 actual Texture Apply uses shared accepted inputs and persists a material without changing terrain or accepted planting.");
                Check(materialPin.TryGet(run, GraphProductUnits.SurfaceDescription, out var oldSurface) && !oldSurface.vegetationTintSet,
                    "UC-T05-06 replacing the producer preserves the old consumer descriptor without sibling mutation.");
                Check(RiverChannelRegistry.RefreshGround(world, run, acceptedGround.OwnerId, before, terrain.terrainData) &&
                    WorldGraphProducts.TryAcquireGround(world, null, centre, out replacement, out _) &&
                    replacement.PublicationVersion != first.PublicationVersion &&
                    !first.TryGet(run, replacement.PublicationVersion, GraphProductUnits.WorldMetres, out _) &&
                    first.TryGet(run, first.PublicationVersion, GraphProductUnits.WorldMetres, out _),
                    "UC-T05-06 same-run accepted ground replacement has a new version while the original pinned payload remains readable.");
                bindings.RetireStaleSurfaces();
                Check(!WorldGraphProducts.TryAcquireSurface(world, null, centre, out _, out _) && materialPin.TryGet(run, GraphProductUnits.SurfaceDescription, out _),
                    "UC-T05-06 stale producer materials retire and report pending without invalidating an active consumer pin.");
            }
            finally
            {
                first?.Dispose(); sibling?.Dispose(); replacement?.Dispose(); materialPin?.Dispose();
                UnityEngine.Object.DestroyImmediate(window); UnityEngine.Object.DestroyImmediate(waterObject); UnityEngine.Object.DestroyImmediate(foreign);
                UnityEngine.Object.DestroyImmediate(graph);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
