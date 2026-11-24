using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Utilities;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T01-12: uses the public mutation admission contract, including its copy-at-write requirement.
        [Serializable]
        private sealed class OwnershipWriterNode : Generator
        {
            internal static int copies;
            public float addition = 0.3f;
            public override string NodeName { get { return "Ownership writer"; } }
            public override NodeCategory Category { get { return NodeCategory.Modifier; } }
            public override Port[] GetInputs() { return SharedPorts.HeightIn; }
            public override Port[] GetOutputs() { return SharedPorts.HeightOut; }
            /// <inheritdoc />
            public override object Generate(int outputIndex, TileData tile, StopToken stopToken)
            {
                Matrix borrowed = tile.PullInputForMutation(this, 0, stopToken, out bool mayMutate) as Matrix;
                if (borrowed == null) return null;
                Matrix owned = borrowed;
                if (!mayMutate) { owned = new Matrix(borrowed); copies++; }
                for (int i = 0; i < owned.arr.Length; i++) owned.arr[i] += addition;
                return owned;
            }
        }

        [Serializable]
        private sealed class OwnershipReaderNode : Generator
        {
            public override string NodeName { get { return "Ownership reader"; } }
            public override NodeCategory Category { get { return NodeCategory.Utility; } }
            public override Port[] GetInputs() { return SharedPorts.HeightIn; }
            public override Port[] GetOutputs() { return SharedPorts.HeightOut; }
            /// <inheritdoc />
            public override object Generate(int outputIndex, TileData tile, StopToken stopToken)
            {
                return tile.PullInput(this, 0, stopToken);
            }
        }

        private static void TestGraphProductOwnership()
        {
            for (int route = 0; route < 4; route++)
                for (int flags = 0; flags < 8; flags++)
                    TestFieldProductOwnership(route, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
            TestTextureDescriptorOwnership();
        }

        private static void TestFieldProductOwnership(int route, bool retained, bool reverse, bool indexed)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var source = new ConstantNode { id = "source", value = 0.2f };
                var writer = new OwnershipWriterNode { id = "writer" };
                var reader = new OwnershipReaderNode { id = "reader" };
                graph.nodes.AddRange(new Generator[] { source, writer, reader });
                Generator inlet = source;
                if (route == 1)
                {
                    var first = new CurveNode { id = "bypass1", disabled = true };
                    var second = new CurveNode { id = "bypass2", disabled = true };
                    graph.nodes.Add(first); graph.nodes.Add(second);
                    graph.connections.Add(new Connection(source.id, 0, first.id, 0));
                    graph.connections.Add(new Connection(first.id, 0, second.id, 0));
                    inlet = second;
                }
                else if (route >= 2)
                {
                    Generator sender = route == 2 ? (Generator)new PortalOutNode { channel = "ownership" }
                        : new TexturePortalOutNode { channel = "ownership" };
                    Generator receiver = route == 2 ? (Generator)new PortalInNode { channel = "ownership" }
                        : new TexturePortalInNode { channel = "ownership" };
                    sender.id = "sender"; receiver.id = "receiver";
                    graph.nodes.Add(sender); graph.nodes.Add(receiver);
                    graph.connections.Add(new Connection(source.id, 0, sender.id, 0));
                    inlet = receiver;
                }
                graph.connections.Add(new Connection(inlet.id, 0, writer.id, 0));
                graph.connections.Add(new Connection(source.id, 0, reader.id, 0));
                if (indexed) graph.compiledPlan = CompiledGraphPlan.Build(graph);
                GraphContentHasher.ComputeHashes(graph);
                var store = retained ? new Dictionary<string, RetainedProduct>() : null;
                TileData tile = SnapshotTile(graph, store);
                var evaluator = new GraphEvaluator(graph);
                OwnershipWriterNode.copies = 0;
                Matrix sibling = reverse ? (Matrix)evaluator.PullNode(reader, 0, tile, null) : null;
                Matrix changed = (Matrix)evaluator.PullNode(writer, 0, tile, null);
                if (!reverse) sibling = (Matrix)evaluator.PullNode(reader, 0, tile, null);
                Matrix original = (Matrix)evaluator.PullNode(source, 0, tile, null);
                string label = "UC-T01-12 route=" + route + " retained=" + retained + " reverse=" + reverse + " indexed=" + indexed;
                Check(Mathf.Abs(changed.arr[0] - 0.5f) < 1e-6f && Mathf.Abs(sibling.arr[0] - 0.2f) < 1e-6f &&
                      Mathf.Abs(original.arr[0] - 0.2f) < 1e-6f, label + " preserves source and sibling values.");
                Check(ReferenceEquals(sibling, original) && !ReferenceEquals(changed, original) && OwnershipWriterNode.copies == 1,
                    label + " shares read-only pulls and makes exactly one writer copy.");
                Matrix inletProduct = (Matrix)evaluator.PullNode(inlet, 0, tile, null);
                Check(Mathf.Abs(inletProduct.arr[0] - 0.2f) < 1e-6f &&
                      ReferenceEquals(inletProduct, evaluator.PullNode(inlet, 0, tile, null)),
                    label + " keeps bypass/portal publication unchanged and memoizes repeated pulls.");
                var products = new HashSet<Matrix> { original, sibling, changed, inletProduct };
                Check(products.Count == (route >= 2 ? 3 : 2), label + " has bounded field ownership without eager sibling copies.");
                if (!retained) return;
                TileData warm = SnapshotTile(graph, store);
                Check(ReferenceEquals(changed, evaluator.PullNode(writer, 0, warm, null)) && OwnershipWriterNode.copies == 1,
                    label + " retained writer reuse allocates no extra write copy.");
                writer.addition = 0.4f; GraphContentHasher.ComputeHashes(graph);
                TileData edited = SnapshotTile(graph, store);
                Matrix regenerated = (Matrix)evaluator.PullNode(writer, 0, edited, null);
                Matrix cold = SnapshotPull(graph, writer.id);
                Check(Mathf.Abs(regenerated.arr[0] - 0.6f) < 1e-6f && regenerated.arr[0] == cold.arr[0] && original.arr[0] == 0.2f &&
                      ((Matrix)store[source.id + ":0"].product).arr[0] == 0.2f && OwnershipWriterNode.copies == 3,
                    label + " edited retained and cold results agree without poisoning their parent.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestTextureDescriptorOwnership()
        {
            // UC-T01-12: one fixture owns the native texture; every graph descriptor only borrows it.
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                var reference = new TextureRef(texture) { filter = TextureFilterKind.Point, wrap = TextureWrapKind.Clamp, mipBias = 0.25f };
                var layer = new LayerProduct { albedo = reference, normal = reference, mask = reference };
                var splat = new SplatProduct { heightBlend = true, heightTransition = 0.7f }; splat.layers[2] = layer;
                var gradient = new GradientProduct(); gradient.stops.Add(new GradientStop(0.2f, Color.red));
                var water = new WaterProduct { normalMap = reference, foamTexture = reference, causticsTexture = reference, shorelineTexture = reference };
                var surface = new SurfaceProduct { splat = splat, water = water };
                Matrix map = new Matrix(new CoordRect(0, 0, 3, 3)); map.Fill(0.4f);
                surface.textures["test"] = reference; surface.bakedGradients["ramp"] = gradient; surface.bakedMaps["map"] = map;
                SurfaceProduct left = surface.Copy(), right = surface.Copy();
                left.textures["test"].filter = TextureFilterKind.Trilinear;
                left.splat.layers[2].albedo.wrap = TextureWrapKind.Mirror;
                left.splat.layers[2].uv.tileSizeMetres = new Vector2(3f, 4f);
                left.water.foamTexture.mipBias = 1.5f;
                left.bakedGradients["ramp"].stops[0] = new GradientStop(0.9f, Color.blue);
                Check(reference.filter == TextureFilterKind.Point && right.textures["test"].filter == TextureFilterKind.Point &&
                      reference.wrap == TextureWrapKind.Clamp && right.splat.layers[2].albedo.wrap == TextureWrapKind.Clamp &&
                      layer.uv.tileSizeMetres == new Vector2(8f, 8f) && right.splat.layers[2].uv.tileSizeMetres == new Vector2(8f, 8f) &&
                      reference.mipBias == 0.25f && right.water.foamTexture.mipBias == 0.25f &&
                      gradient.stops[0].position == 0.2f && right.bakedGradients["ramp"].stops[0].position == 0.2f,
                    "UC-T01-12 surface branches isolate sampling, splat, water and gradient edits from siblings and source.");
                Check(left.splat.layers[0] == null && left.splat.layers[3] == null && left.splat.heightBlend && left.splat.heightTransition == 0.7f,
                    "UC-T01-12 splat metadata copies preserve ordered gaps and blend values.");
                Check(ReferenceEquals(left.bakedMaps["map"], map) && ReferenceEquals(right.bakedMaps["map"], map),
                    "UC-T01-12 scalar material branches share immutable large baked fields.");
                left.bakedMaps["map"] = new Matrix(left.bakedMaps["map"]); left.bakedMaps["map"].arr[0] = 0.8f;
                Check(map.arr[0] == 0.4f && right.bakedMaps["map"].arr[0] == 0.4f,
                    "UC-T01-12 a baked-field writer copies only the map it changes.");
                var portalSplat = (SplatProduct)PortalRouting.CopyProduct(splat);
                var portalGradient = (GradientProduct)PortalRouting.CopyProduct(gradient);
                var portalTexture = (TextureRef)PortalRouting.CopyProduct(reference);
                portalSplat.layers[2].mask.mipBias = 2f; portalGradient.stops.Clear(); portalTexture.wrap = TextureWrapKind.Repeat;
                Check(reference.mipBias == 0.25f && reference.wrap == TextureWrapKind.Clamp && gradient.stops.Count == 1,
                    "UC-T01-12 portal copies isolate splat, gradient and texture descriptors.");
                Check(ReferenceEquals(left.textures["test"].texture, texture) && ReferenceEquals(right.water.foamTexture.texture, texture) &&
                      ReferenceEquals(portalTexture.texture, texture) && ReferenceEquals(portalSplat.layers[2].albedo.texture, texture),
                    "UC-T01-12 descriptor copies borrow the single native texture without allocating native duplicates.");
                UnityEngine.Object.DestroyImmediate(texture);
                Check(reference.texture == null && left.textures["test"].texture == null && right.water.foamTexture.texture == null && portalTexture.texture == null,
                    "UC-T01-12 the original native owner releases the borrowed texture once for every descriptor.");
                Check(PortalRouting.CopyProduct(null) == null && new GradientProduct().Copy().stops.Count == 0 && new TextureRef().Copy().texture == null,
                    "UC-T01-12 empty and missing descriptor inputs retain their shape.");
            }
            finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
