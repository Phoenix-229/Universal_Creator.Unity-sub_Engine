// UC-T07-04: known-image equations and the actual categorical splat path, including halo/warp/graded borders.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Biome;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTextureImageOperations()
        {
            var memory = new ProductMemoryBudget(8 * 1024 * 1024); TileData tile = MemoryTestTile(memory);
            try
            {
                var d = new TextureImageDescriptor(2, 1, TextureImageColourSpace.Linear, TextureImageSemantic.Colour);
                var image = tile.CreateTextureImage(d, new[] { new Color(0, 0.25f, 0.5f, 0.2f), new Color(1, 0.75f, 0.5f, 0.8f) });
                var adjust = new TextureImageAdjustmentNode { operation = TextureImageAdjustment.AutoLevels };
                var auto = adjust.Adjust(image, tile, null);
                Check(auto[0].r == 0 && auto[1].r == 1 && auto[0].g == 0 && auto[1].g == 1 && auto[0].b == 0.5f && auto[0].a == 0.2f,
                    "UC-T07-04 per-channel auto levels handles black/white and constant channels while retaining alpha.");
                adjust.operation = TextureImageAdjustment.BrightnessContrast; adjust.brightness = 2; adjust.contrast = 1;
                var bright = adjust.Adjust(image, tile, null);
                Check(bright[1].r == 2 && bright[0].a == 0.2f, "UC-T07-04 brightness operates in linear RGB with explicit HDR clipping policy and unchanged alpha.");
                var red = tile.GenerateTextureImage(d, _ => new Color(1, 0, 0, 0.4f));
                adjust.operation = TextureImageAdjustment.RgbToHsv; var hsv = adjust.Adjust(red, tile, null);
                adjust.operation = TextureImageAdjustment.HsvToRgb; var roundtrip = adjust.Adjust(hsv, tile, null);
                Check(hsv.Descriptor.Semantic == TextureImageSemantic.Hsv && roundtrip[0] == red[0], "UC-T07-04 HSV conversion declares its channel domain and round-trips known red with alpha.");
                var blend = new TextureImageBlendNode { operation = ColorMathOp.Lerp, opacity = 0.25f };
                var mixed = blend.Blend(image, red, null, tile, null);
                Check(Mathf.Abs(mixed[0].r - 0.25f) < 0.00001f && Mathf.Abs(mixed[0].a - 0.25f) < 0.00001f,
                    "UC-T07-04 image blending uses the existing RGB equations and separate expected alpha interpolation.");
                var resize = new TextureImageResampleNode { width = 4, height = 1 };
                var resized = resize.Resample(image, tile, null);
                Check(resized[0].r == 0 && Mathf.Abs(resized[1].r - 0.25f) < 0.00001f && Mathf.Abs(resized[2].r - 0.75f) < 0.00001f && resized[3].r == 1,
                    "UC-T07-04 explicit bilinear resampling uses pixel centres and clamp edges without a half-texel shift.");
                var constant = tile.GenerateTextureImage(new TextureImageDescriptor(3, 3, TextureImageColourSpace.Linear, TextureImageSemantic.Colour), _ => new Color(0.25f, 0.5f, 0.75f, 0.4f));
                var blurred = new TextureImageBlurNode { sigma = 1 }.Blur(constant, tile, null);
                Check(Mathf.Abs(blurred[0].r - 0.25f) < 0.00001f && Mathf.Abs(blurred[8].b - 0.75f) < 0.00001f && blurred[4].a == 0.4f,
                    "UC-T07-04 separable filtering preserves constant colour/edges and selected alpha policy.");
                adjust.operation = TextureImageAdjustment.Levels; adjust.low = 0.25f; adjust.high = 0.75f; adjust.gamma = 1;
                var levels = adjust.Adjust(image, tile, null);
                Check(levels[0].g == 0 && levels[1].g == 1 && levels[0].b == 0.5f && levels[1].a == 0.8f,
                    "UC-T07-04 manual levels uses ordered endpoints and retains colour alpha.");
                adjust.operation = TextureImageAdjustment.Threshold; adjust.threshold = 0.2126f;
                var threshold = adjust.Adjust(red, tile, null);
                Check(threshold[0].r == 1 && threshold[0].a == 0.4f, "UC-T07-04 linear luminance threshold includes its exact boundary.");
                var mask = tile.GenerateTextureImage(new TextureImageDescriptor(2, 1, TextureImageColourSpace.Linear, TextureImageSemantic.Membership),
                    i => new Color(i, 0, 0, 0));
                blend.opacity = 1; var masked = blend.Blend(image, red, mask, tile, null);
                Check(masked[0] == image[0] && masked[1] == red[1], "UC-T07-04 independent membership masks control both blend endpoints.");
                var stop = new StopToken(); stop.RequestStop(); long bytes = memory.ManagedBytes;
                adjust.operation = TextureImageAdjustment.HsvToRgb;
                Check(adjust.Adjust(image, tile, stop) == null && new TextureImageBlurNode().Blur(image, tile, stop) == null && memory.ManagedBytes == bytes,
                    "UC-T07-04 cancelled image operations do not allocate or publish obsolete pixels.");
            }
            finally { tile.ReleaseProductMemory(); Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC-T07-04 image operations retire their output and scratch bytes."); }
            var coverage = new float[2, 2, 2]; coverage[0, 0, 0] = 1; coverage[0, 1, 1] = 1;
            coverage[1, 0, 1] = 1; coverage[1, 1, 0] = 1;
            var filteredCoverage = TerrainSystem.TerrainBuilder.ResampleSplatWeights(coverage, 3, 3);
            Check(filteredCoverage[0, 0, 0] == 1 && filteredCoverage[2, 2, 0] == 1 &&
                filteredCoverage[1, 1, 0] == 0.5f && filteredCoverage[1, 1, 1] == 0.5f,
                "UC-T07-04 native alphamap reconciliation bilinearly filters independent coverage and preserves terrain lattice endpoints.");
            TestCategoricalBiomeSplat();
            TestBiomeRuleNativeUpload(); // UC biome repair: corrupted authoring must not corrupt native maps or accepted geometry.
        }

        private static void TestBiomeRuleNativeUpload()
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            var layers = new[] { new TerrainLayer(), new TerrainLayer(), new TerrainLayer() };
            var data = new TerrainData { heightmapResolution = 33, alphamapResolution = 16, size = new Vector3(16, 100, 16), terrainLayers = layers };
            GameObject ground = null;
            try
            {
                var height = new CategoricalSplatProbe { id = "rule-native-height" };
                var biome = new CategoricalSplatProbe { id = "rule-native-biome", kind = 1 };
                var output = new BiomeSplatOutputNode { id = "rule-native-output", layerCount = 3, defaultLayer = 1,
                    waterBareEnabled = false, beachEnabled = false, snowEnabled = false, wetStrength = 0, biomeSoftening = 0 };
                graph.nodes.Add(height); graph.nodes.Add(biome); graph.nodes.Add(output);
                graph.connections.Add(new Connection(height.id, 0, output.id, 0)); graph.connections.Add(new Connection(biome.id, 0, output.id, 1));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph);
                float[,] accepted = CommitHeights(33, 0.2f); accepted[16, 16] = 0.27f; accepted[10, 10] = 0.11f;
                data.SetHeights(0, 0, accepted); accepted = data.GetHeights(0, 0, 33, 33); data.SetHoles(4, 4, new bool[1, 1]);
                ground = UnityEngine.Terrain.CreateTerrainGameObject(data);
                var terrain = ground.GetComponent<UnityEngine.Terrain>(); var collider = ground.GetComponent<TerrainCollider>();
                for (int scenario = 0; scenario < 5; scenario++)
                {
                    output.rules.Clear(); output.contrast = scenario == 2 ? float.PositiveInfinity : 1.6f;
                    for (int i = 0; i < 2 && scenario != 0; i++)
                    {
                        var rule = BiomeSplatRule.Default(i == 0 ? BiomeType.Tundra : BiomeType.Desert, i * 2);
                        if (scenario == 1) { rule.groundLayer = -100; rule.slopeLayer = int.MaxValue; rule.slopeStart = float.NaN;
                            rule.slopeEnd = float.PositiveInfinity; rule.highStart = float.NaN; rule.weight = float.NaN; }
                        if (scenario == 3) rule.weight = 0;
                        if (scenario == 4) rule.weight = float.MaxValue;
                        output.rules.Add(rule);
                    }
                    var memory = new ProductMemoryBudget(4 * 1024 * 1024);
                    var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 16, 16), Vector3.zero, new Vector3(16, 100, 16), 100);
                    tile.AttachMemory(memory); tile.Bind(evaluator);
                    try
                    {
                        evaluator.PullNode(output, 0, tile, new StopToken()); var splat = tile.outputs.splatmap;
                        bool valid = splat != null;
                        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                        {
                            double sum = 0;
                            for (int l = 0; l < 3; l++) { float v = splat[z, x, l]; valid &= !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0; sum += v; }
                            valid &= Math.Abs(sum - 1) < 0.0001;
                        }
                        Check(valid, "UC biome Rules finite normalized native upload, scenario " + scenario);
                        data.SetAlphamaps(0, 0, splat);
                        Check(terrain.terrainData == data && collider.terrainData == data && terrain.drawHeightmap &&
                            data.GetHeights(16, 16, 1, 1)[0, 0] == accepted[16, 16] && data.GetHeights(10, 10, 1, 1)[0, 0] == accepted[10, 10] && !data.GetHoles(4, 4, 1, 1)[0, 0],
                            "UC biome Rules native upload preserves sculpt, river, holes, visibility and collision identity, scenario " + scenario);
                        if (scenario == 1) Check(float.IsNaN(output.rules[0].weight) && output.rules[0].groundLayer == -100,
                            "UC biome Rules worker sanitization never rewrites saved authoring.");
                    }
                    finally { tile.ReleaseProductMemory(); Check(memory.ManagedBytes == 0, "UC biome Rules native upload retires product arrays."); }
                }
            }
            finally
            {
                if (ground != null) UnityEngine.Object.DestroyImmediate(ground);
                UnityEngine.Object.DestroyImmediate(data); foreach (var layer in layers) UnityEngine.Object.DestroyImmediate(layer);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private sealed class CategoricalSplatProbe : CreatorNode
        {
            internal int kind, shape;
            public override string NodeName => "Categorical splat fixture";
            public override NodeCategory Category => NodeCategory.Creator; // UC-T07-04: category is required even for a test-only field source.
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                if (kind == 0) { matrix.Fill(0.5f); return; }
                if (kind == 2) { matrix.Fill(1); return; }
                for (int z = 0; z < matrix.rect.size.z; z++) for (int x = 0; x < matrix.rect.size.x; x++)
                {
                    Vector3 p = matrix.PixelToWorld(x + matrix.rect.offset.x, z + matrix.rect.offset.z);
                    // UC-T07-04: both boundaries cross the shared x=16 m cell edge.
                    bool a = shape == 0 ? p.x + (p.z - 8) * 0.4f < 16 : (p.x - 16) * (p.x - 16) + (p.z - 8) * (p.z - 8) < 36;
                    matrix.arr[z * matrix.rect.size.x + x] = Ecology.WhittakerBiome.Encode(a ? (int)BiomeType.Tundra : (int)BiomeType.Desert);
                }
            }
        }

        private static void TestCategoricalBiomeSplat()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var height = new CategoricalSplatProbe { id = "category-height" };
                var biome = new CategoricalSplatProbe { id = "category-label", kind = 1 };
                var phase = new CategoricalSplatProbe { id = "category-ecotone", kind = 2 };
                var output = new BiomeSplatOutputNode { id = "category-output", layerCount = 10, waterBareEnabled = false, beachEnabled = false,
                    snowEnabled = false, wetStrength = 0, contrast = 1, biomeSoftening = 1 };
                for (int i = 0; i < Ecology.WhittakerBiome.Count; i++)
                { var rule = BiomeSplatRule.Default((BiomeType)i, i); rule.slopeLayer = i; output.rules.Add(rule); }
                graph.nodes.Add(height); graph.nodes.Add(biome); graph.nodes.Add(phase); graph.nodes.Add(output);
                graph.connections.Add(new Connection(height.id, 0, output.id, 0));
                graph.connections.Add(new Connection(biome.id, 0, output.id, 1));
                graph.connections.Add(new Connection(phase.id, 0, output.id, 4));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); var evaluator = new GraphEvaluator(graph);
                for (int shape = 0; shape < 2; shape++) for (int warp = 0; warp < 2; warp++) for (int resolution = 17; resolution <= 33; resolution += 16)
                {
                    biome.shape = shape; output.biomeBlendNoise = warp;
                    output.biomeSoftening = (resolution - 1) / 16f; // Same physical sigma, one metre, at both source resolutions.
                    float[,] border = new float[resolution, 10];
                    for (int cell = 0; cell < 2; cell++)
                    {
                        var memory = new ProductMemoryBudget(8 * 1024 * 1024);
                        var tile = new TileData(new Coord(cell, 0), new CoordRect(0, 0, resolution, resolution),
                            new Vector3(cell * 16, 0, 0), new Vector3(16, 100, 16), 100);
                        tile.AttachMemory(memory);
                        tile.Bind(evaluator); // UC-T07-04: direct output pulls need the same inlet binding as Evaluate.
                        try
                        {
                            evaluator.PullNode(output, 0, tile, new StopToken()); float[,,] splat = tile.outputs.splatmap;
                            bool valid = splat != null, mixed = false, seamless = true;
                            if (splat != null) for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++)
                            {
                                float sum = 0; for (int layer = 0; layer < 10; layer++)
                                { float weight = splat[z, x, layer]; sum += weight; valid &= weight >= 0 && (layer == 0 || layer == 5 || weight == 0); }
                                valid &= Mathf.Abs(sum - 1) < 0.0001f; mixed |= splat[z, x, 0] > 0.001f && splat[z, x, 5] > 0.001f;
                            }
                            if (splat != null) for (int z = 0; z < resolution; z++) for (int layer = 0; layer < 10; layer++)
                                if (cell == 0) border[z, layer] = splat[z, resolution - 1, layer];
                                else seamless &= Mathf.Abs(border[z, layer] - splat[z, 0, layer]) < 0.0001f;
                            Check(valid && mixed && seamless, "UC-T07-04 actual " + (shape == 0 ? "diagonal" : "curved") + " graded biome boundary, warp " + warp +
                                ", resolution " + resolution + ", cell " + cell + ", preserves only source categories, normalized weights and the shared border.");
                        }
                        finally { tile.ReleaseProductMemory(); Check(memory.ManagedBytes == 0, "UC-T07-04 categorical halo/scratch fixture retires all admitted arrays."); }
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
