// UC-T07-11: actual accepted context, continuous world coordinates/native control upload, weather-only appearance and measured quality response.
using System;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Optimization;
using UniverseCreator.Editor.Utilities;
using QualityTier = UniverseCreator.Optimization.AdaptiveQuality.QualityTier;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private sealed class SurfaceContextField : Generator
        {
            internal Matrix field;
            public override string NodeName => "Surface context field fixture";
            public override NodeCategory Category => NodeCategory.Utility; // UC-T07-11: borrowed field fixture is a shared input, with no upstream ports.
            public override Port[] GetInputs() => SharedPorts.None;
            public override Port[] GetOutputs() => new[] { new Port("Field", PortType.Matrix, PortDirection.Output) };
            public override object Generate(int outputIndex, TileData tile, StopToken stop) => field;
        }

        private static void TestTextureSurfaceContext()
        {
            TestMeasuredTextureQuality(); TestSurfaceControlCoordinates(); TestSurfaceWaterProfiles();
            var root = new GameObject("UC surface context fixture"); var main = ScriptableObject.CreateInstance<Graph>();
            var graph = ScriptableObject.CreateInstance<Graph>(); TileData tile = null; TerrainData data = null; GameObject terrainRoot = null;
            Material material = null;
            try
            {
                main.SetDomain(UCGraphDomain.TerrainWater); graph.SetDomain(UCGraphDomain.TextureMaterialShader);
                var world = root.AddComponent<UniverseCreatorObject>(); world.graph = main;
                var run = world.GenerationScheduler.CurrentRun;
                var heights = new float[2, 2]; heights[0, 1] = heights[1, 1] = 0.16f;
                var accepted = new RiverChannelSnapshot(run, "surface-context", new Rect(0, 0, 16, 16), Array.Empty<ChannelGraphPublication>(), heights, 0, 100);
                tile = MemoryTestTile(world.ProductMemory); tile.AssignRun(run);
                tile.BindWorldGraphInputs(main, accepted, WorldGraphWaterInput.Capture(world), null);
                var input = new WorldGraphInputNode { mainGraph = main };
                Matrix elevation = (Matrix)input.Generate(11, tile, null), slope = (Matrix)input.Generate(12, tile, null);
                Check(Mathf.Abs(elevation.arr[8] - 8) < 0.001f && Mathf.Abs(slope.arr[8] - 45) < 0.01f &&
                    input.GetOutputs()[9].type == PortType.Surface && input.GetOutputs().Length == 18,
                    "UC-T07-11 accepted elevation/slope outputs use absolute metres/degrees, retain material port 9 and preserve the original wire indices.");
                Check(((Matrix)input.Generate(13, tile, null)).arr[8] == 0 && ((Matrix)input.Generate(14, tile, null)).arr[8] == 0,
                    "UC-T07-11 an explicitly captured dry world reports zero water membership/depth, with no invented river/lake/ocean observation.");
                var shore = tile.CreateMatrix();
                for (int z = 0; z < 17; z++) for (int x = 0; x < 17; x++) shore.arr[z * 17 + x] = x - 8;
                var field = new SurfaceContextField { id = "surface-shore", field = shore };
                var profile = new SurfaceSuitabilityNode { id = "surface-profile", habitat = SurfaceHabitat.Shore,
                    elevationMetres = new Vector2(-100, 100), slopeDegrees = new Vector2(0, 90), shoreDistanceMetres = new Vector2(-2, 2), shoreBandMetres = 1 };
                graph.nodes.Add(field); graph.nodes.Add(profile); graph.connections.Add(new Connection(field.id, 0, profile.id, 4));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); var evaluator = new GraphEvaluator(graph);
                tile.Bind(evaluator); // UC-T07-11: bind direct fixture pulls so the authored shore input executes.
                Matrix membership = evaluator.PullNode(profile, 0, tile, new StopToken()) as Matrix;
                Check(membership != null && membership.arr[8] > 0.99f && Mathf.Abs(membership.arr[6] - 0.5f) < 0.001f && membership.arr[4] == 0,
                    "UC-T07-11 the actual surface suitability caller produces configurable continuous shore membership over physical metre bands.");
                bool missing = false;
                try { new SurfaceSuitabilityNode { requireSubstrate = true }.Generate(0, tile, null); }
                catch (InvalidOperationException error) { missing = error.Message.Contains("Substrate Membership"); }
                Check(missing, "UC-T07-11 required unavailable geology/substrate produces an actionable input diagnostic rather than guessed soil.");
                missing = false;
                try { new SurfaceSuitabilityNode { habitat = SurfaceHabitat.Shore }.Generate(0, tile, null); }
                catch (InvalidOperationException error) { missing = error.Message.Contains("Signed Shore Distance"); }
                Check(missing, "UC-T07-11 a skirt/bounds estimate is never silently substituted for a missing physical shore-distance field.");

                var splat = new SplatProduct(); splat.layers[0] = new LayerProduct { triplanarOnSlopes = true, triplanarSharpness = 7, uv = new UVTransform(new Vector2(3, 3)) };
                var surface = new SurfaceProduct { splat = splat }; surface.PublishLayerParameters();
                Check(surface.floats[UCShaderProps.TriplanarSharpness] == 7 && surface.floats[UCShaderProps.SlopeTriplanarScale] == 3 && surface.keywords.Contains(UCShaderProps.KeywordSlopeBlend),
                    "UC-T07-11 actual layer projection reaches the existing cliff shader channel with its authored metre scale and sharpness.");

                var quality = root.AddComponent<AdaptiveQuality>(); quality.pinTier = true; quality.pinnedTier = QualityTier.Medium;
                typeof(AdaptiveQuality).GetMethod("EnsureConfiguration", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(quality, null);
                var binding = root.AddComponent<UCWorldGraphBindings>();
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 100, 16) };
                terrainRoot = UnityEngine.Terrain.CreateTerrainGameObject(data); terrainRoot.transform.SetParent(root.transform, false);
                var terrain = terrainRoot.GetComponent<UnityEngine.Terrain>(); material = new Material(Shader.Find(UCShaderProps.ShaderName)); terrain.materialTemplate = material;
                binding.RegisterTextureQuality(terrain);
                var tick = typeof(UCWorldGraphBindings).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic); tick.Invoke(binding, null);
                var block = new MaterialPropertyBlock(); terrain.GetSplatMaterialPropertyBlock(block);
                Check(block.GetFloat(UCShaderProps.TextureQuality) == 1 && material.GetFloat(UCShaderProps.TextureQuality) == 0 && data.heightmapResolution == 33,
                    "UC-T07-11 a measured/pinned medium tier reaches the actual terrain consumer through its property block without shared-material mutation or height rebuild.");
                quality.enabled = false; tick.Invoke(binding, null); terrain.GetSplatMaterialPropertyBlock(block);
                Check(block.GetFloat(UCShaderProps.TextureQuality) == 0,
                    "UC-T07-11 disabling the quality source reverses the terrain sampling override to its authored value.");
                block.Clear(); terrain.SetSplatMaterialPropertyBlock(block); material.SetFloat(UCShaderProps.TextureQuality, 2);
                binding.RegisterTextureQuality(terrain); quality.enabled = true; tick.Invoke(binding, null);
                terrain.GetSplatMaterialPropertyBlock(block);
                Check(block.GetFloat(UCShaderProps.TextureQuality) == 2,
                    "UC-T07-11 absent property-block values inherit the authored material quality floor instead of silently becoming full sampling.");
                quality.enabled = false; tick.Invoke(binding, null); terrain.GetSplatMaterialPropertyBlock(block);
                Check(block.GetFloat(UCShaderProps.TextureQuality) == 2,
                    "UC-T07-11 repeated Apply and controller retirement preserve an authored cheaper sampling value.");
                binding.enabled = false;
            }
            finally
            {
                tile?.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(root);
                if (terrainRoot != null) UnityEngine.Object.DestroyImmediate(terrainRoot);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main); UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestMeasuredTextureQuality()
        {
            var response = new MeasuredQualityResponse(4); QualityTier tier = QualityTier.High;
            for (int i = 0; i < 20; i++) tier = response.Observe(tier, QualityTier.Medium, 25, 23, true, 0.1f, 16.67f, 0.5f, 1, false);
            Check(tier == QualityTier.Medium, "UC-T07-11 sustained measured overload lowers one tier and respects the configured minimum quality floor.");
            for (int i = 0; i < 50; i++) tier = response.Observe(tier, QualityTier.Medium, 16.67f, float.NaN, false, 0.1f, 16.67f, 0.5f, 1, false);
            Check(tier == QualityTier.Medium && !response.HasRecoveryMetrics,
                "UC-T07-11 display-paced frames and unavailable CPU/GPU readings cannot be counted as recovery headroom.");
            for (int i = 0; i < 50; i++) tier = response.Observe(tier, QualityTier.Medium, 16.67f, 8, true, 0.1f, 16.67f, 0.5f, 1, false);
            Check(tier == QualityTier.Ultra, "UC-T07-11 sustained actual CPU/GPU headroom restores quality after the recovery hold without device-model assumptions.");
            response.Reset(); tier = QualityTier.High;
            for (int i = 0; i < 15; i++) tier = response.Observe(tier, QualityTier.Medium, 16.67f, 10, true, 0.1f, 16.67f, 0.5f, 10, true);
            Check(tier == QualityTier.Medium, "UC-T07-11 measured memory pressure reduces reversible work even when frame-time samples fit the target.");
        }

        private static void TestSurfaceWaterProfiles()
        {
            // UC-T07-11: exercise captured native water geometry/category through the production surface caller.
            var root = new GameObject("UC physical bed fixture"); var main = ScriptableObject.CreateInstance<Graph>();
            main.SetDomain(UCGraphDomain.TerrainWater); // UC-T07-11: accepted inputs require the same explicit Main Graph metadata as production.
            TileData tile = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.graph = main;
                var volume = root.AddComponent<UCWaterVolume>(); volume.WorldOwner = world;
                var data = new WaterBodyData {
                    vertices = new[] { new Vector3(0, 10, 0), new Vector3(0, 10, 16), new Vector3(8, 10, 16), new Vector3(8, 10, 0) },
                    uv = new[] { new Vector2(0, 10), new Vector2(0, 10), new Vector2(0, 10), new Vector2(0, 10) },
                    colors = new[] { Color.white, Color.white, Color.white, Color.white }, triangles = new[] { 0, 1, 2, 0, 2, 3 },
                    shorelineSegmentsLocal = new[] { new Vector3(8, 10, 0), new Vector3(8, 10, 16) }
                };
                foreach (var kind in new[] { WaterBodyKind.River, WaterBodyKind.Lake, WaterBodyKind.Ocean })
                {
                    volume.kind = (UCWaterVolume.BodyKind)(int)kind; volume.SetAcceptedSurface(data, Matrix4x4.identity, false, new Vector2(2, 0));
                    var run = world.GenerationScheduler.CurrentRun;
                    var accepted = new RiverChannelSnapshot(run, "bed-context", new Rect(0, 0, 16, 16), Array.Empty<ChannelGraphPublication>(), new float[2, 2], 0, 100);
                    tile = MemoryTestTile(world.ProductMemory); tile.AssignRun(run); tile.BindWorldGraphInputs(main, accepted, WorldGraphWaterInput.Capture(world), null);
                    var habitat = kind == WaterBodyKind.River ? SurfaceHabitat.RiverBed : kind == WaterBodyKind.Lake ? SurfaceHabitat.LakeBed : SurfaceHabitat.OceanFloor;
                    Matrix bed = (Matrix)new SurfaceSuitabilityNode { habitat = habitat, waterTransitionMetres = 2 }.Generate(0, tile, null);
                    Matrix land = (Matrix)new SurfaceSuitabilityNode { waterTransitionMetres = 2 }.Generate(0, tile, null);
                    int row = 8 * 17;
                    Check(bed.arr[row + 6] > 0.99f && Mathf.Abs(bed.arr[row + 7] - 0.5f) < 0.001f && bed.arr[row + 8] == 0 && bed.arr[row + 9] == 0 &&
                        Mathf.Abs(land.arr[row + 7] + bed.arr[row + 7] - 1) < 0.001f && land.arr[row + 8] > 0.99f,
                        "UC-T07-11 actual " + kind + " bed and land blend continuously across a two-metre physical shore band without an encoded-category cutoff.");
                    var input = new WorldGraphInputNode { mainGraph = main, selectedWaterKind = kind, shoreSearchMetres = 4 };
                    Check(((Matrix)input.Generate(14, tile, null)).arr[row + 6] == 10 && ((Matrix)input.Generate(15, tile, null)).arr[row + 6] == 2 &&
                        Mathf.Abs(((Matrix)input.Generate(17, tile, null)).arr[row + 7] + 1) < 0.001f,
                        "UC-T07-11 accepted " + kind + " supplies actual metre depth, signed bank distance and current in metres per second.");
                    tile.ReleaseProductMemory(); tile = null;
                }
                var diagonal = new WaterShorelineIndex(new[] { Vector3.zero, new Vector3(16, 0, 16), new Vector3(0, 0, 1), new Vector3(16, 0, 15) }, Matrix4x4.identity);
                Check(diagonal.Distance(8, 8, 32, out _, out bool observed) == 0 && observed,
                    "UC-T07-11 wide diagonal shore searches count distinct segments rather than repeated spatial-cell references.");
            }
            finally { tile?.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(main); }
        }

        private static void TestSurfaceControlCoordinates()
        {
            TerrainData data = null; TerrainLayer first = null, second = null, reserved = null;
            try
            {
                data = new TerrainData { heightmapResolution = 33, alphamapResolution = 16, size = new Vector3(16, 100, 16) };
                first = new TerrainLayer(); second = new TerrainLayer(); reserved = new TerrainLayer(); data.terrainLayers = new[] { first, second, reserved };
                var old = new float[16, 16, 3]; for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) { old[z, x, 0] = 0.75f; old[z, x, 2] = 0.25f; }
                data.SetAlphamaps(0, 0, old);
                var a = new Matrix(new CoordRect(-5, 7, 3, 3)) { worldPos = new Vector3(-16, 0, -32), worldSize = data.size };
                var b = a.Blank();
                for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++) { a.arr[z * 3 + x] = 1 - x * 0.5f; b.arr[z * 3 + x] = x * 0.5f; }
                var surface = new SurfaceProduct { maxLayers = 2, splat = new SplatProduct() };
                surface.splat.layers[0] = new LayerProduct(); surface.splat.layers[1] = new LayerProduct(); surface.splat.coverage[0] = a; surface.splat.coverage[1] = b;
                float[,,] weights = UCTextureGraphApplier.BuildCoverage(surface, data, a.worldPos); data.SetAlphamaps(0, 0, weights);
                float[,,] uploaded = data.GetAlphamaps(0, 0, 16, 16); bool valid = true;
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                    valid &= Mathf.Abs(uploaded[z, x, 0] + uploaded[z, x, 1] + uploaded[z, x, 2] - 1) < 0.005f && Mathf.Abs(uploaded[z, x, 2] - 0.25f) < 0.005f;
                Check(valid && uploaded[0, 0, 0] > 0.74f && uploaded[0, 15, 1] > 0.74f && weights[0, 7, 1] > 0.3f && weights[0, 7, 1] < 0.4f,
                    "UC-T07-11 actual native control upload bilinearly resamples negative/nonzero-offset world coordinates, preserves reserved paint and normalizes every membership.");
                bool rejected = false;
                try { UCTextureGraphApplier.BuildCoverage(surface, data, Vector3.zero); }
                catch (ArgumentException error) { rejected = error.Message.Contains("coordinates"); }
                Check(rejected, "UC-T07-11 a foreign terrain coordinate mapping rejects before native upload rather than becoming edge-clamped colour.");
                a.Fill(1e-10f); b.Fill(2e-10f);
                Check(Mathf.Abs(SurfaceCoverageSampling.Weights(surface.splat, -8, -24).y - 2f / 3) < 0.00001f,
                    "UC-T07-11 small nonzero memberships retain their relative coverage and do not become a first-material fallback.");
            }
            finally
            {
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (first != null) UnityEngine.Object.DestroyImmediate(first); if (second != null) UnityEngine.Object.DestroyImmediate(second); if (reserved != null) UnityEngine.Object.DestroyImmediate(reserved);
            }
        }
    }
}
