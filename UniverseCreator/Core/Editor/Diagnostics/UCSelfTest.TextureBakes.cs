// UC-T07-09: actual incremental evaluation, accepted-cache isolation and explicit material publication.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Editor.Windows;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTextureBakes()
        {
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.TextureMaterialShader); // UC-T07-09: actual serialized domain.
            var field = new ConstantNode { id = "incremental-field", value = 0.25f };
            var image = new TextureFieldImageNode { id = "incremental-image" };
            var unrelated = new TexturePatternNode { id = "incremental-unrelated", width = 3, height = 3 };
            graph.nodes.Add(field); graph.nodes.Add(image); graph.nodes.Add(unrelated); graph.connections.Add(new Connection(field.id, 0, image.id, 0));
            var context = new SnapshotRetirementContext(); var previous = System.Threading.SynchronizationContext.Current;
            var cache = new TextureBakeCache(); var memory = cache.MemoryFor(null);
            var snapshots = new List<GenerationSnapshot>(); var tiles = new List<TileData>();
            var native = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
            var terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(native);
            var terrain = terrainObject.GetComponent<UnityEngine.Terrain>();
            Graph materialGraph = null; Material initial = null;
            string folder = "Assets/UCTextureBakeAcceptance_" + Guid.NewGuid().ToString("N");
            try
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                GenerationSnapshot Capture() { var capture = GenerationSnapshot.CaptureForDomains(graph,
                    NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu); snapshots.Add(capture); return capture; }
                TileData CreateTile(int size)
                {
                    var tile = new TileData(Coord.Zero, new CoordRect(0, 0, size, size), Vector3.zero, native.size, 100);
                    tile.AttachMemory(memory); tile.retained = cache.ForRegion(graph, terrain, tile, Guid.Empty, () => true);
                    tiles.Add(tile); return tile;
                }
                var captured = Capture(); var cold = CreateTile(3); var stop = new StopToken();
                var evaluation = new IncrementalGraphEvaluation(captured.Graph, cold, stop, captured.Graph.GetNodeById(image.id));
                var timing = Stopwatch.StartNew();
                Check(evaluation.Total == 2 && evaluation.Completed == 0 && cold.GetCached(image.id, 0) == null,
                    "UC-T07-09 preparing incremental dependencies performs no pixel work or material publication.");
                evaluation.StepNext();
                Check(evaluation.Completed == 1 && cold.GetCached(field.id, 0) != null && cold.GetCached(image.id, 0) == null,
                    "UC-T07-09 one slice pulls only its required upstream output through the existing evaluator.");
                evaluation.StepNext(); timing.Stop(); double coldMs = timing.Elapsed.TotalMilliseconds;
                var accepted = (TextureImageProduct)cold.GetCached(image.id, 0); cache.Accept(terrain, cold); cold.ReleaseProductMemory();
                var warm = CreateTile(3); timing.Restart();
                var repeat = new IncrementalGraphEvaluation(captured.Graph, warm, new StopToken(), captured.Graph.GetNodeById(image.id));
                while (repeat.StepNext()) { }
                timing.Stop(); double warmMs = timing.Elapsed.TotalMilliseconds;
                Check(evaluation.IsComplete && repeat.IsComplete && ReferenceEquals(accepted, warm.GetCached(image.id, 0)) && accepted[4].r == 0.25f,
                    "UC-T07-09 identical geometry/format/semantic inputs reuse the same admitted immutable image across warm runs.");
                warm.ReleaseProductMemory();
                var otherBranch = CreateTile(3);
                var otherEvaluation = new IncrementalGraphEvaluation(captured.Graph, otherBranch, new StopToken(), captured.Graph.GetNodeById(unrelated.id));
                otherEvaluation.StepNext(); cache.Accept(terrain, otherBranch); otherBranch.ReleaseProductMemory();
                field.value = 0.75f; EditorUtility.SetDirty(graph); var edited = Capture(); var obsolete = CreateTile(3);
                var cancelled = new StopToken(); var pending = new IncrementalGraphEvaluation(edited.Graph, obsolete, cancelled, edited.Graph.GetNodeById(image.id));
                pending.StepNext(); cancelled.RequestStop();
                Check(!pending.StepNext() && !pending.IsComplete && obsolete.GetCached(image.id, 0) == null,
                    "UC-T07-09 cancellation between dependencies skips downstream image allocation and completion.");
                obsolete.ReleaseProductMemory();
                var restored = CreateTile(3);
                Check(restored.TryRetainedProduct(restored.retained[image.id + ":0"], out var prior) && ReferenceEquals(prior, accepted),
                    "UC-T07-09 cancelled staging leaves the previous accepted cache entry and its payload intact.");
                restored.ReleaseProductMemory();
                var editedTile = CreateTile(3); timing.Restart();
                var editedEvaluation = new IncrementalGraphEvaluation(edited.Graph, editedTile, new StopToken(), edited.Graph.GetNodeById(image.id));
                while (editedEvaluation.StepNext()) { }
                timing.Stop(); double editMs = timing.Elapsed.TotalMilliseconds;
                Check(((TextureImageProduct)editedTile.GetCached(image.id, 0))[4].r == 0.75f &&
                    editedTile.TryRetainedProduct(editedTile.retained[unrelated.id + ":0"], out _),
                    "UC-T07-09 editing one branch rebuilds its image while preserving an unused accepted image branch.");
                cache.Accept(terrain, editedTile); editedTile.ReleaseProductMemory();
                var resized = CreateTile(5);
                var changed = new IncrementalGraphEvaluation(edited.Graph, resized, new StopToken(), edited.Graph.GetNodeById(image.id));
                while (changed.StepNext()) { }
                var newImage = (TextureImageProduct)resized.GetCached(image.id, 0);
                Check(newImage.Descriptor.Width == 5 && newImage.Descriptor.Height == 5 && newImage[12].r == 0.75f && !ReferenceEquals(newImage, accepted), // UC-T07-09: dimensions belong to the existing immutable descriptor.
                    "UC-T07-09 changed resolution invalidates old lattice products and changed settings reach the new image.");
                cache.Accept(terrain, resized); resized.ReleaseProductMemory();
                UnityEngine.Debug.Log("UC-T07-09 fixture CPU elapsed: cold 3x3 " + coldMs.ToString("F3") + " ms; warm 3x3 " +
                    warmMs.ToString("F3") + " ms; edited 3x3 " + editMs.ToString("F3") + " ms. Matched small fixture only; no device/GPU claim.");

                materialGraph = TextureGraphTemplates.CreateBasicSurface();
                var output = (ShaderOutputNode)materialGraph.nodes.Find(node => node is ShaderOutputNode);
                var blend = materialGraph.nodes.Find(node => node is TerrainBlendFinalNode);
                var grade = new ColorGradeNode { id = "bake-grade", brightness = 0.5f }; materialGraph.nodes.Add(grade);
                materialGraph.connections.RemoveAll(edge => edge.targetNodeId == output.id);
                materialGraph.connections.Add(new Connection(blend.id, 0, grade.id, 0));
                materialGraph.connections.Add(new Connection(grade.id, 0, output.id, 0));
                output.outputFolder = folder; output.writeTerrainLayers = false;
                initial = new Material(Shader.Find(UCShaderProps.ShaderName)); initial.SetFloat(UCShaderProps.Brightness, 0.1f);
                terrain.materialTemplate = initial;
                using (var cancelledBake = new TextureGraphBake(materialGraph, terrain, null, 129, cache))
                {
                    Check(ReferenceEquals(terrain.materialTemplate, initial) && !AssetDatabase.IsValidFolder(folder),
                        "UC-T07-09 capturing Run/Apply keeps the current material and persistent assets untouched.");
                    cancelledBake.Cancel(); cancelledBake.Pump();
                    Check(cancelledBake.IsComplete && !cancelledBake.Succeeded && cancelledBake.AppliedRegions == 0 && terrain.materialTemplate == initial,
                        "UC-T07-09 cancelling captured Apply retires staged resources without changing a terrain material.");
                }
                using (var stale = new TextureGraphBake(materialGraph, terrain, null, 129, cache))
                {
                    grade.brightness = 0.75f; EditorUtility.SetDirty(materialGraph); stale.Pump();
                    Check(stale.IsComplete && !stale.Succeeded && stale.AppliedRegions == 0 && terrain.materialTemplate.GetFloat(UCShaderProps.Brightness) == 0.1f,
                        "UC-T07-09 an authored edit rejects the older capture before persistent material writes.");
                }
                using (var applied = new TextureGraphBake(materialGraph, terrain, null, 129, cache))
                {
                    var timeout = Stopwatch.StartNew(); while (!applied.IsComplete && timeout.Elapsed.TotalSeconds < 10) applied.Pump();
                    Check(applied.IsComplete && applied.Succeeded && applied.AppliedRegions == 1 &&
                        terrain.materialTemplate != initial && terrain.materialTemplate.GetFloat(UCShaderProps.Brightness) == 0.75f,
                        "UC-T07-09 explicit production Run/Apply publishes only the newest captured shader settings to the native terrain.");
                }
                TestTextureWorldBake();
            }
            finally
            {
                foreach (var tile in tiles) tile.ReleaseProductMemory(); cache.Dispose();
                foreach (var snapshot in snapshots) snapshot.Release(); context.Drain();
                System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
                UnityEngine.Object.DestroyImmediate(terrainObject); UnityEngine.Object.DestroyImmediate(native);
                if (initial != null) UnityEngine.Object.DestroyImmediate(initial);
                if (materialGraph != null) UnityEngine.Object.DestroyImmediate(materialGraph);
                UnityEngine.Object.DestroyImmediate(graph);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0 && memory.PinnedBytes == 0,
                    "UC-T07-09 cache/scene retirement releases all admitted CPU/native bake resources after their final consumer.");
            }
        }

        // UC-T07-09: two accepted cells deliberately have different ground fields, with a foreign nested world beside them.
        private static void TestTextureWorldBake()
        {
            var root = new GameObject("Texture world bake fixture"); root.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.maxHeightMeters = 100;
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater); world.graph = main;
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.TextureMaterialShader); // UC-T07-09: actual serialized domain.
            var inputs = new WorldGraphInputNode { id = "world-bake-input" };
            var field = new TextureFieldImageNode { id = "world-bake-field" };
            var colour = new TextureImageCombineNode { id = "world-bake-colour", semantic = TextureImageSemantic.Colour };
            var upload = new TextureImageUploadNode { id = "world-bake-upload" };
            var layer = new AlbedoLayerNode { id = "world-bake-layer" };
            var splat = new TextureSplatNode { id = "world-bake-splat" };
            var blend = new TerrainBlendFinalNode { id = "world-bake-blend" };
            string folder = "Assets/UCTextureWorldBake_" + Guid.NewGuid().ToString("N");
            var output = new ShaderOutputNode { id = "world-bake-output", outputFolder = folder, writeTerrainLayers = true }; // UC-T07-09: native TerrainLayers are the actual albedo recipient, not the material's engine-owned _Splat slots.
            graph.nodes.AddRange(new Generator[] { inputs, field, colour, upload, layer, splat, blend, output });
            graph.connections.Add(new Connection(inputs.id, 0, field.id, 0)); graph.connections.Add(new Connection(field.id, 0, colour.id, 0));
            graph.connections.Add(new Connection(colour.id, 0, upload.id, 0)); graph.connections.Add(new Connection(upload.id, 0, layer.id, 0));
            graph.connections.Add(new Connection(layer.id, 0, splat.id, 0)); graph.connections.Add(new Connection(splat.id, 0, blend.id, 1));
            graph.connections.Add(new Connection(blend.id, 0, output.id, 0));
            var terrains = new List<UnityEngine.Terrain>(); var data = new List<TerrainData>(); var cache = new TextureBakeCache();
            var run = world.GenerationScheduler.BeginRun();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var native = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) }; data.Add(native);
                    var cell = UnityEngine.Terrain.CreateTerrainGameObject(native); cell.transform.SetParent(root.transform, false);
                    cell.transform.position = new Vector3(i * 32, 0, 0); terrains.Add(cell.GetComponent<UnityEngine.Terrain>());
                    float value = i == 0 ? 0.2f : 0.8f; var heights = new float[,] { { value, value }, { value, value } };
                    var bounds = new Rect(i * 32, 0, 32, 32); string owner = "bake-cell-" + i;
                    var ground = new RiverChannelSnapshot(run, owner, bounds, Array.Empty<ChannelGraphPublication>(), heights, 0, 100);
                    Check(RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, owner, bounds, Array.Empty<ChannelGraphPublication>(), null, ground, world),
                        "UC-T07-09 world bake fixture publishes accepted ground for cell " + i + ".");
                }
                var foreignRoot = new GameObject("Foreign nested texture world"); foreignRoot.SetActive(false); foreignRoot.transform.SetParent(root.transform, false);
                var foreign = foreignRoot.AddComponent<UniverseCreatorObject>(); foreign.enabled = false;
                var foreignData = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) }; data.Add(foreignData);
                var foreignCell = UnityEngine.Terrain.CreateTerrainGameObject(foreignData); foreignCell.transform.SetParent(foreignRoot.transform, false);
                var foreignMaterial = foreignCell.GetComponent<UnityEngine.Terrain>().materialTemplate; // UC-T07-09: preserve the actual Unity-assigned material, including the installed Editor's default.
                using (var bake = new TextureGraphBake(graph, terrains[0], world, 129, cache))
                {
                    var timeout = Stopwatch.StartNew(); while (!bake.IsComplete && timeout.Elapsed.TotalSeconds < 10) bake.Pump();
                    var first = terrains[0].materialTemplate; var second = terrains[1].materialTemplate;
                    var firstLayers = terrains[0].terrainData.terrainLayers; var secondLayers = terrains[1].terrainData.terrainLayers;
                    var a = firstLayers.Length == 1 ? firstLayers[0].diffuseTexture : null;
                    var b = secondLayers.Length == 1 ? secondLayers[0].diffuseTexture : null;
                    Check(bake.IsComplete && bake.Succeeded && bake.AppliedRegions == 2 && first != second && a != null && b != null && a != b &&
                        Mathf.Abs(a.GetPixel(0, 0).r - 0.2f) < 1e-5f && Mathf.Abs(b.GetPixel(0, 0).r - 0.8f) < 1e-5f,
                        "UC-T07-09 world Apply persists each cell's own accepted spatial pixels and independent material assets. " + bake.Status +
                        " First=" + (a != null ? a.GetPixel(0, 0).r.ToString("G9") + "/" + a.format + "/" + AssetDatabase.GetAssetPath(a) : "missing") +
                        "; second=" + (b != null ? b.GetPixel(0, 0).r.ToString("G9") + "/" + b.format + "/" + AssetDatabase.GetAssetPath(b) : "missing"));
                    Check(foreignCell.GetComponent<UnityEngine.Terrain>().materialTemplate == foreignMaterial && world.GenerationScheduler.IsCurrent(run) &&
                        world.GetComponent<UCWorldGraphBindings>().TextureRevision == 2,
                        "UC-T07-09 world Apply preserves nested foreign terrain and the accepted terrain run while publishing both owned regions. " + bake.Status);
                }
            }
            finally
            {
                cache.Dispose(); RiverChannelRegistry.Withdraw(run.WorldId); UnityEngine.Object.DestroyImmediate(root);
                foreach (var native in data) UnityEngine.Object.DestroyImmediate(native);
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(main);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        // UC-T07-09: existing world-product acceptance drives the production window's incremental Apply to completion.
        private static void WaitTextureBake(TextureGraphWindow window)
        {
            var timeout = Stopwatch.StartNew();
            while (window.IsBakeRunning && timeout.Elapsed.TotalSeconds < 10) window.PumpBake();
            if (window.IsBakeRunning) throw new TimeoutException("UC-T07-09 Texture Run/Apply did not retire within ten seconds.");
        }
    }
}
