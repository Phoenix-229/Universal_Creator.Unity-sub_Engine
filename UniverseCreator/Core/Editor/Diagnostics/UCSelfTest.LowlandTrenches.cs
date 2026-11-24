using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators; // UC-T03-01: trace authored courses independently of scene-water creation.
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Combiners; // UC-T03-02: verify that composition can intentionally remove a trench.
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T03-01: world-coordinate sampling keeps the same lowland when drainage requests another resolution/halo.
        [Serializable]
        private sealed class LowlandTrenchGround : CreatorNode
        {
            public override string NodeName => "Lowland trench fixture";
            public override NodeCategory Category => NodeCategory.Creator;
            public bool basinPair;
            /// <inheritdoc />
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                for (int z = 0; z < matrix.rect.size.z; z++)
                {
                    stop?.ThrowIfStopRequested();
                    float worldZ = matrix.worldPos.z + z * matrix.PixelSize.y;
                    for (int x = 0; x < matrix.rect.size.x; x++)
                    {
                        float worldX = matrix.worldPos.x + x * matrix.PixelSize.x;
                        // 243 m at the head, 207 m at the sea edge; a shallow cross-slope converges flow.
                        matrix.arr[z * matrix.rect.size.x + x] = 0.405f - 0.06f * (worldZ / 2048f)
                            + 0.003f * Mathf.Abs(worldX - 1024f) / 1024f;
                        // UC-T03-01: identical depressions; only the western one has a real lake footprint.
                        if (basinPair)
                        {
                            float dx = Mathf.Min(Mathf.Abs(worldX - 320f), Mathf.Abs(worldX - 1728f));
                            float dz = worldZ - 900f;
                            float bowl = Mathf.Clamp01(1f - (dx * dx + dz * dz) / (160f * 160f));
                            matrix.arr[z * matrix.rect.size.x + x] -= 0.06f * bowl;
                        }
                    }
                }
            }
        }

        // UC-T03-01: a world-space headwater above the configured freeboard, stable on coarse/halo grids.
        [Serializable]
        private sealed class LowlandTrenchSources : CreatorNode
        {
            public override string NodeName => "Lowland source fixture";
            public override NodeCategory Category => NodeCategory.Creator;
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                for (int z = 0; z < matrix.rect.size.z; z++)
                {
                    stop?.ThrowIfStopRequested();
                    for (int x = 0; x < matrix.rect.size.x; x++)
                    {
                        float dx = matrix.worldPos.x + x * matrix.PixelSize.x - 1024f;
                        float dz = matrix.worldPos.z + z * matrix.PixelSize.y - 128f;
                        matrix.arr[z * matrix.rect.size.x + x] = dx * dx + dz * dz <= 80f * 80f ? 1f : 0f;
                    }
                }
            }
        }

        /// <summary>UC-T03-01/02: traces lowland river-to-native-height production and verifies guard/relief composition.</summary>
        /// <remarks>Isolated batch only; creates transient fixtures, writes stage evidence to the launch log and exits nonzero on failure.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunLowlandTrenchBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Lowland trench acceptance requires an isolated batch process.");
            checks = 0; failures.Clear();
            try { TestLowlandTrenchProduction(); }
            catch (Exception error) { Check(false, "UC-T03-01 lowland trace exception: " + error); }
            bool ok = failures.Count == 0;
            Debug.Log(Report(ok)); Console.WriteLine(Report(ok));
            EditorApplication.Exit(ok ? 0 : 1);
        }

        // UC-T03-01: compare every guard stage with the SAME upstream carve, then read committed native terrain.
        private static void TestLowlandTrenchProduction()
        {
            TestLowlandTrenchPipeline(false, false);
            TestLowlandTrenchPipeline(true, false);
            TestLowlandTrenchPipeline(false, true);
            TestLowlandTrenchPipeline(false, false, true);
            TestLowlandTrenchInactive();
        }

        // UC-T03-01/02: compare with independently guarded, identically modified uncarved ground.
        // Absolute world elevation may rise; a trench is its relief below that supported ground.
        private static void TestLowlandTrenchPipeline(bool seeded, bool basins, bool manual = false)
        {
            const int resolution = 129;
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var ground = new LowlandTrenchGround { id = "lowland-ground", basinPair = basins };
            Generator river = seeded
                ? (Generator)new RiverSeedCourseNode { id = "lowland-river", seaLevel = 0.35f, solveResolution = 64,
                    maxRivers = 1, channelHalfWidthMetres = 32f, floodplainMetres = 48f, channelDepth = 0.005f }
                : new RiverNetworkNode { id = "lowland-river", seaLevel = 0.35f, solveResolution = 64,
                    channelInitiationFraction = 0.001f, slopeAreaThreshold = 0f, trunkChannelWidthMetres = 32f, autoTrunkWidth = false };
            if (manual)
            {
                var course = new RiverCourseInputNode.ManualRiverCourse {
                    bodyId = 1, pointsXZ = new[] { new Vector2(1024f, 128f), new Vector2(1024f, 1792f) },
                    halfWidthM = new[] { 32f, 32f }, depthM = new[] { 3f, 3f }, bedY = new[] { 237.75f, 208.5f } };
                course.ComputeBounds(48f);
                var authored = new RiverCourseInputNode { id = "lowland-river", carveTrench = true, bankReachMetres = 48f };
                authored.SetManualCourses(new List<RiverCourseInputNode.ManualRiverCourse> { course });
                river = authored;
            }
            var modifier = new BlurNode { id = "lowland-downstream", radius = 1, intensity = 0.1f };
            var baselineModifier = new BlurNode { id = "lowland-reference", radius = 1, intensity = 0.1f };
            graph.nodes.AddRange(new Generator[] { ground, river, modifier, baselineModifier });
            graph.AddConnection(new Connection(ground.id, 0, river.id, 0));
            graph.AddConnection(new Connection(river.id, 0, modifier.id, 0));
            graph.AddConnection(new Connection(ground.id, 0, baselineModifier.id, 0));
            if (seeded)
            {
                var seeds = new LowlandTrenchSources { id = "lowland-seeds" };
                graph.nodes.Add(seeds);
                graph.AddConnection(new Connection(seeds.id, 0, river.id, 1));
            }
            var lakes = new List<HeightOutputNode.LakeFootprint>();
            if (basins)
                lakes.Add(new HeightOutputNode.LakeFootprint {
                    ringXZ = new[] { new Vector2(150f, 730f), new Vector2(490f, 730f), new Vector2(490f, 1070f), new Vector2(150f, 1070f) },
                    planBounds = new Bounds(new Vector3(320f, 0f, 900f), new Vector3(340f, 1f, 340f)) });
            if (river is RiverNetworkNode network) network.SetLakeFootprints(lakes);
            var outputs = new HeightOutputNode[4];
            var references = new HeightOutputNode[4];
            string[] stages = { "Unguarded", "Floor", "FloorShelf", "FloorShelfRise" };
            for (int stage = 0; stage < outputs.Length; stage++)
            {
                var output = new HeightOutputNode { id = "lowland-output-" + stage, seaLevel = 0.35f,
                    heightScale = 600f, enforceSeaLevelFloor = stage >= 1, enforceCoastalShelf = stage >= 2,
                    enforceContinentalRise = stage >= 3 };
                output.SetLakeFootprints(lakes);
                outputs[stage] = output; graph.nodes.Add(output);
                graph.AddConnection(new Connection(modifier.id, 0, output.id, 0));
                var reference = new HeightOutputNode { id = "lowland-base-output-" + stage, seaLevel = 0.35f,
                    heightScale = 600f, enforceSeaLevelFloor = stage >= 1, enforceCoastalShelf = stage >= 2,
                    enforceContinentalRise = stage >= 3 };
                reference.SetLakeFootprints(lakes);
                references[stage] = reference; graph.nodes.Add(reference);
                graph.AddConnection(new Connection(baselineModifier.id, 0, reference.id, 0));
            }
            Dictionary<string, RetainedProduct> retained = null;
            Matrix coldCarved = null;
            float[,] coldFinal = null;
            var native = new TerrainData { heightmapResolution = resolution, size = new Vector3(2048f, 600f, 2048f) };
            try
            {
                GraphContentHasher.ComputeHashes(graph);
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                Check(UCWaterSystemManager.ResolveHydrology(graph, null) == null,
                    "UC-T03-01 trench-only graph requests no automatic scene water reconciliation.");
                foreach (string phase in new[] { "Cold", "Warm", "Regenerated" })
                {
                    string scenario = (manual ? "Authored " : seeded ? "Seeded " : basins ? "LakeDry " : "Network ") + phase;
                    var tile = new TileData(Coord.Zero, new CoordRect(0, 0, resolution, resolution), Vector3.zero,
                        new Vector3(2048f, 600f, 2048f), 600f);
                    // UC-T03-01: retention is opt-in; cold/regenerated fixtures must install their store too.
                    tile.retained = phase == "Warm" ? retained : new Dictionary<string, RetainedProduct>();
                    var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator);
                    try
                    {
                        var stop = new StopToken();
                        Matrix source = evaluator.PullNode(ground, 0, tile, stop) as Matrix;
                        float[] unchangedSource = (float[])source.arr.Clone();
                        Matrix carved = evaluator.PullNode(river, 0, tile, stop) as Matrix;
                        Matrix mask = evaluator.PullNode(river, 1, tile, stop) as Matrix;
                        Matrix modified = evaluator.PullNode(modifier, 0, tile, stop) as Matrix;
                        Check(source != null && carved != null && mask != null && modified != null,
                            "UC-T03-01 " + scenario + " lowland pipeline produces source, carved height, mask and downstream height.");
                        if (source == null || carved == null || mask == null || modified == null) continue;
                        if (phase == "Warm")
                            Check(ReferenceEquals(coldCarved, carved), "UC-T03-01 warm trace actually reuses the retained river height.");
                        int candidates = 0; float rawCut = 0f, modifierCut = 0f;
                        var protectedSamples = new bool[source.arr.Length];
                        for (int i = 0; i < protectedSamples.Length; i++)
                        {
                            // A genuine interior head above freeboard, with a cut larger than native quantization.
                            if (mask.arr[i] <= 0.1f || source.arr[i] < 0.35f + 20f / 600f ||
                                source.arr[i] - carved.arr[i] <= 20f / 65535f) continue;
                            protectedSamples[i] = true; candidates++;
                            rawCut = Mathf.Max(rawCut, (source.arr[i] - carved.arr[i]) * 600f);
                            modifierCut = Mathf.Max(modifierCut, (source.arr[i] - modified.arr[i]) * 600f);
                        }
                        Check(candidates > 0 && modifierCut > 20f * 600f / 65535f,
                            "UC-T03-01 " + scenario + " measurable lowland river cut survives the downstream modifier before terminal guards.");
                        Debug.Log("UC-T03-01 " + scenario + " candidates=" + candidates + "; riverCutM=" + TrenchNumber(rawCut) +
                            "; modifierCutM=" + TrenchNumber(modifierCut));
                        for (int stage = 0; stage < outputs.Length; stage++)
                        {
                            evaluator.PullNode(references[stage], 0, tile, stop);
                            float[,] baselineHeight = tile.outputs.heightmap;
                            evaluator.PullNode(outputs[stage], 0, tile, stop);
                            float maxCut = 0f, shapeError = 0f, bankError = 0f; int kept = 0;
                            Matrix baselineMatrix = evaluator.PullNode(baselineModifier, 0, tile, stop) as Matrix;
                            for (int i = 0; i < protectedSamples.Length; i++)
                            {
                                float expectedCut = Mathf.Max(0f, baselineMatrix.arr[i] - modified.arr[i]);
                                float actualCut = baselineHeight[i / resolution, i % resolution] - tile.outputs.heightmap[i / resolution, i % resolution];
                                shapeError = Mathf.Max(shapeError, Mathf.Abs(actualCut - expectedCut));
                                if (expectedCut < 1e-7f) bankError = Mathf.Max(bankError, Mathf.Abs(actualCut));
                                if (!protectedSamples[i]) continue;
                                float cut = actualCut * 600f;
                                maxCut = Mathf.Max(maxCut, cut);
                                if (cut > 20f * 600f / 65535f) kept++;
                            }
                            Debug.Log("UC-T03-01 " + scenario + " stage=" + stages[stage] + "; kept=" + kept + "; maxCutM=" + TrenchNumber(maxCut));
                            if (stage == outputs.Length - 1)
                            {
                                Check(kept > 0, "UC-T03-02 " + scenario + " terminal guards preserve a measurable interior procedural trench.");
                                Check(shapeError < 2e-6f && bankError < 2e-6f,
                                    "UC-T03-02 " + scenario + " final cut profile and unaffected banks match independently guarded ground.");
                                TerrainBuilder.ApplyOutputs(native, tile.outputs, null);
                                float[,] nativeHeights = native.GetHeights(0, 0, resolution, resolution);
                                float nativeCut = 0f;
                                for (int i = 0; i < protectedSamples.Length; i++)
                                    if (protectedSamples[i]) nativeCut = Mathf.Max(nativeCut,
                                        baselineHeight[i / resolution, i % resolution] - nativeHeights[i / resolution, i % resolution]);
                                Check(nativeCut > 20f / 65535f, "UC-T03-02 " + scenario + " measurable trench reaches committed native terrain.");
                                if (basins)
                                {
                                    float lakeBed = nativeHeights[56, 20], dryBed = nativeHeights[56, 108];
                                    Check(dryBed > 0.35f + 20f / 600f && lakeBed < dryBed - 1f / 600f,
                                        "UC-T03-02 " + scenario + " real lake retains relief while the unclaimed dry hollow clears sea freeboard.");
                                }
                            }
                        }
                        float[,] committed = native.GetHeights(0, 0, resolution, resolution);
                        float nativeError = 0f, repeatError = 0f;
                        for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++)
                        {
                            nativeError = Mathf.Max(nativeError, Mathf.Abs(committed[z, x] - tile.outputs.heightmap[z, x]));
                            if (coldFinal != null) repeatError = Mathf.Max(repeatError, Mathf.Abs(tile.outputs.heightmap[z, x] - coldFinal[z, x]));
                        }
                        Check(nativeError <= 2f / 65535f && repeatError < 1e-6f,
                            "UC-T03-01 " + scenario + " native commit matches final height and cold/retained/regenerated results agree.");
                        bool inputIntact = true;
                        for (int i = 0; i < source.arr.Length; i++) inputIntact &= source.arr[i] == unchangedSource[i];
                        Check(inputIntact, "UC-T03-02 " + scenario + " river, modifiers and terminal guards leave their published source unchanged.");
                        Debug.Log("UC-T03-01 " + scenario + " nativeErrorM=" + TrenchNumber(nativeError * 600f) +
                            "; repeatErrorM=" + TrenchNumber(repeatError * 600f) + "; sceneWaterRequested=false");
                        if (phase == "Cold")
                        {
                            coldCarved = carved;
                            coldFinal = (float[,])tile.outputs.heightmap.Clone();
                            retained = new Dictionary<string, RetainedProduct>(tile.retained);
                            tile.AcceptRetainedProducts();
                        }
                    }
                    finally { tile.ReleaseProductMemory(); }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(native);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        // UC-T03-01/02: an absent source, disabled carve, or deliberately replaced branch
        // must not exempt arbitrary ground. An unwired Height Output publishes no terrain.
        private static void TestLowlandTrenchInactive()
        {
            foreach (string scenario in new[] { "MissingSources", "MissingHeight", "Disabled", "UnwiredOutput", "Replaced", "FanInMax" })
            {
                Graph graph = ScriptableObject.CreateInstance<Graph>();
                var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 65, 65), Vector3.zero,
                    new Vector3(2048f, 600f, 2048f), 600f);
                try
                {
                    var ground = new LowlandTrenchGround { id = "inactive-ground" };
                    Generator river = scenario == "MissingSources"
                        ? (Generator)new RiverSeedCourseNode { id = "inactive-river", solveResolution = 32 }
                        : new RiverNetworkNode { id = "inactive-river", solveResolution = 32,
                            channelInitiationFraction = 0.001f, slopeAreaThreshold = 0f, disabled = scenario == "Disabled" };
                    var output = new HeightOutputNode { id = "inactive-output" };
                    var reference = new HeightOutputNode { id = "inactive-reference" };
                    graph.nodes.AddRange(new Generator[] { ground, river, output, reference });
                    if (scenario != "MissingHeight") graph.AddConnection(new Connection(ground.id, 0, river.id, 0));
                    graph.AddConnection(new Connection(ground.id, 0, reference.id, 0));
                    Generator selected = river;
                    if (scenario == "Replaced" || scenario == "FanInMax")
                    {
                        selected = scenario == "Replaced" ? (Generator)new MaximumNode { id = "inactive-replace" }
                            : new CombineNode { id = "inactive-fanin", op = MergeOp.Max };
                        graph.nodes.Add(selected);
                        graph.AddConnection(new Connection(river.id, 0, selected.id, 0));
                        graph.AddConnection(new Connection(ground.id, 0, selected.id, scenario == "FanInMax" ? 0 : 1));
                    }
                    if (scenario != "UnwiredOutput") graph.AddConnection(new Connection(selected.id, 0, output.id, 0));
                    graph.compiledPlan = CompiledGraphPlan.Build(graph);
                    var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator);
                    var stop = new StopToken();
                    if (scenario == "MissingHeight")
                    {
                        Matrix height = evaluator.PullNode(river, 0, tile, stop) as Matrix;
                        Matrix mask = evaluator.PullNode(river, 1, tile, stop) as Matrix;
                        bool empty = height != null && mask != null;
                        if (empty) for (int i = 0; i < height.arr.Length; i++) empty &= height.arr[i] == 0f && mask.arr[i] == 0f;
                        Check(empty, "UC-T03-01 missing Height emits no fabricated river height or mask.");
                        continue;
                    }
                    evaluator.PullNode(output, 0, tile, stop);
                    if (scenario == "UnwiredOutput")
                        Check(tile.outputs.heightmap == null, "UC-T03-01 unwired Height Output publishes no native terrain.");
                    else
                    {
                        float[,] actual = tile.outputs.heightmap;
                        evaluator.PullNode(reference, 0, tile, stop);
                        float error = 0f;
                        for (int z = 0; z < 65; z++) for (int x = 0; x < 65; x++)
                            error = Mathf.Max(error, Mathf.Abs(actual[z, x] - tile.outputs.heightmap[z, x]));
                        Check(error < 2e-6f, "UC-T03-02 " + scenario + " retains ordinary ground safeguards without phantom trench protection.");
                        if (scenario == "MissingSources")
                        {
                            Matrix mask = evaluator.PullNode(river, 1, tile, stop) as Matrix;
                            bool empty = mask != null;
                            if (mask != null) foreach (float value in mask.arr) empty &= value == 0f;
                            Check(empty, "UC-T03-01 missing Sources emits an empty river mask.");
                        }
                    }
                }
                finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
            }
        }

        private static string TrenchNumber(float value) => value.ToString("F5", CultureInfo.InvariantCulture);
    }
}
