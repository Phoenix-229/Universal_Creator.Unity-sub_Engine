// UC-T07-03: exercise actual new input owners with known pixels and accepted categorical fields.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Nodes.Biome;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private sealed class TextureInputFieldProbe : CreatorNode
        {
            public override string NodeName => "Texture field input fixture";
            public override NodeCategory Category => NodeCategory.Creator; // UC-T07-03: field fixture declares its required catalog category.
            protected override void Create(Matrix matrix, TileData tile, StopToken stop) { matrix.Fill(0.5f); }
        }

        private static void TestTextureImageInputs()
        {
            var memory = new ProductMemoryBudget(4 * 1024 * 1024);
            TileData tile = MemoryTestTile(memory);
            Graph graph = ScriptableObject.CreateInstance<Graph>(), main = ScriptableObject.CreateInstance<Graph>();
            Texture2D texture = null; GameObject root = null; TileData acceptedTile = null;
            try
            {
                graph.SetDomain(UCGraphDomain.TextureMaterialShader); main.SetDomain(UCGraphDomain.TerrainWater);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }); texture.Apply(false, false);
                var sampler = new TextureSamplerNode { id = "image-input-sampler", texture = texture };
                var import = new TextureImageImportNode { id = "image-input-import", colourSpace = TextureImageColourSpace.Linear, semantic = TextureImageSemantic.Data };
                var source = new TextureInputFieldProbe { id = "image-input-field" };
                var fieldImage = new TextureFieldImageNode { id = "image-input-field-image", semantic = TextureImageSemantic.HeightMetres, normalizedHeightToMetres = true };
                var scalar = new TextureScalarParameterNode { id = "image-input-scalar", value = 0.7f };
                var grade = new ColorGradeNode { id = "image-input-grade" };
                graph.nodes.Add(sampler); graph.nodes.Add(import); graph.nodes.Add(source); graph.nodes.Add(fieldImage);
                graph.nodes.Add(scalar); graph.nodes.Add(grade);
                graph.connections.Add(new Connection(sampler.id, 0, import.id, 0));
                graph.connections.Add(new Connection(source.id, 0, fieldImage.id, 0));
                graph.connections.Add(new Connection(scalar.id, 0, grade.id, 1));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph);
                tile.Bind(evaluator); // UC-T07-03: direct fixture pulls resolve wired inputs through the same binding as Evaluate.
                var image = (TextureImageProduct)evaluator.PullNode(import, 0, tile, new StopToken());
                Check(image.Descriptor.Width == 2 && image[1].g == 1 && image.Descriptor.Semantic == TextureImageSemantic.Data &&
                    TextureImageSampling.Sample(image, -0.25f, 0.25f, TextureFilterKind.Point, TextureWrapKind.Repeat).g == 1 &&
                    TextureImageSampling.Sample(image, -0.25f, 0.25f, TextureFilterKind.Point, TextureWrapKind.Mirror).r == 1,
                    "UC-T07-03 real texture imports preserve declared pixels/semantics and negative repeat/mirror coordinates sample known texels.");
                ulong before = sampler.GetPreparedInputHash(); texture.SetPixel(0, 0, Color.black); texture.Apply(false, false);
                Check(before != sampler.GetPreparedInputHash(), "UC-T07-03 native pixel revision changes invalidate the sampler dependency without pixel readback hashing.");
                var heightImage = (TextureImageProduct)evaluator.PullNode(fieldImage, 0, tile, new StopToken());
                Check(heightImage.Descriptor.Semantic == TextureImageSemantic.HeightMetres && heightImage[0].r == 50f,
                    "UC-T07-03 normalized accepted field conversion declares physical metres rather than silently treating height as colour.");
                long bytes = memory.ManagedBytes;
                Check((float)new TextureScalarParameterNode { value = 0.7f }.Generate(0, tile, null) == 0.7f &&
                    (Vector4)new TextureVectorParameterNode { value = new Vector4(1, 2, 3, 4) }.Generate(0, tile, null) == new Vector4(1, 2, 3, 4) &&
                    (Color)new TextureColourParameterNode { value = Color.red }.Generate(0, tile, null) == Color.red && memory.ManagedBytes == bytes,
                    "UC-T07-03 scalar/vector/colour parameter updates allocate no CPU image payload.");
                var graded = (SurfaceProduct)evaluator.PullNode(grade, 0, tile, null);
                Check(graded.floats[UCShaderProps.Brightness] == 0.7f && ReferenceEquals(image, evaluator.PullNode(import, 0, tile, null)),
                    "UC-T07-03 scalar parameters reach the existing shader-grading material path while unrelated image work reuses its product.");
                var coordinates = new TextureCoordinatesNode { width = 2, height = 1, space = TextureCoordinateSpace.WorldMetres };
                tile.worldPos = new Vector3(-16, 0, -8);
                var xy = (TextureImageProduct)coordinates.Generate(0, tile, null);
                Check(xy[0].r == -12 && xy[1].r == -4 && xy[0].g == 0,
                    "UC-T07-03 world-coordinate images use absolute metres at pixel centres across negative origins.");
                var pattern = new TexturePatternNode { width = 2, height = 1, pattern = TextureImagePattern.CheckerMetres, cellMetres = 8 };
                var checker = (TextureImageProduct)pattern.Generate(0, tile, null);
                Check(checker[0].r == 0 && checker[1].r == 1, "UC-T07-03 analytical checker repeats in declared world metres without losing negative-coordinate parity.");
                texture.Apply(false, true); bool unreadable = false;
                try { new TextureImageImportNode { texture = texture }.Generate(0, tile, null); } catch (InvalidOperationException) { unreadable = true; }
                Check(unreadable, "UC-T07-03 unreadable imports fail explicitly before any blocking GPU readback.");

                root = new GameObject("UC accepted biome input fixture");
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.graph = main;
                var run = world.GenerationScheduler.CurrentRun;
                var labels = new Matrix(new CoordRect(0, 0, 2, 2)) { worldPos = Vector3.zero, worldSize = new Vector3(16, 100, 16), worldHeight = 100 };
                labels.arr[0] = labels.arr[2] = Ecology.WhittakerBiome.Encode((int)BiomeType.Tundra);
                labels.arr[1] = labels.arr[3] = Ecology.WhittakerBiome.Encode((int)BiomeType.Desert);
                var ground = new RiverChannelSnapshot(run, "biome-input-fixture", new Rect(0, 0, 16, 16), Array.Empty<ChannelGraphPublication>(), new float[2, 2], 0, 100, biomes: labels);
                labels.Fill(Ecology.WhittakerBiome.Encode((int)BiomeType.TemperateForest));
                acceptedTile = MemoryTestTile(world.ProductMemory); acceptedTile.AssignRun(run); acceptedTile.BindWorldGraphInputs(main, ground, null, null);
                var input = new WorldGraphInputNode { mainGraph = main, selectedBiome = BiomeType.Tundra };
                Matrix membership = (Matrix)input.Generate(10, acceptedTile, null);
                input.selectedBiome = BiomeType.TemperateForest; Matrix absent = (Matrix)input.Generate(10, acceptedTile, null);
                Check(membership.arr[0] == 1 && membership.arr[8] == 0.5f && absent.arr[8] == 0 && input.GetOutputs()[9].type == PortType.Surface,
                    "UC-T07-03 actual accepted-world input samples immutable nonadjacent biome membership and preserves prior material port indices without regenerating the main graph.");
            }
            finally
            {
                tile.ReleaseProductMemory(); acceptedTile?.ReleaseProductMemory();
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(main);
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC-T07-03 input fixtures release every admitted image and borrowed native test texture.");
            }
        }
    }
}
