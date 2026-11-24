// UC-T07-10: no graph GPU readback, native guards, final-consumer leases and actual editor scene retirement.
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTextureLifetimes()
        {
            var context = new SnapshotRetirementContext(); var previous = SynchronizationContext.Current;
            var memory = new ProductMemoryBudget(1024 * 1024); var tile = MemoryTestTile(memory);
            var startup = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool untitled = string.IsNullOrEmpty(startup.path);
            string startupFolder = "Assets/UCTextureLifetimeStartup_" + Guid.NewGuid().ToString("N");
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true); source.Apply(false, true);
            GraphTexturePreview preview = null; IDisposable first = null, second = null;
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.TextureMaterialShader); // UC-T07-10: actual serialized domain.
            var field = new ConstantNode { id = "lifetime-preview-field", value = 0.5f }; graph.nodes.Add(field);
            GraphPreviewScheduler scheduler = null;
            Event previousEvent = Event.current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context); Event.current = null;
                // UC-T07-10: ClosePreviewScene does not dispatch Unity's sceneClosing event; use an actual additive scene beside only a saved or verified empty test startup.
                if (untitled)
                {
                    if (!startup.IsValid() || UnityEngine.SceneManagement.SceneManager.sceneCount != 1 || startup.GetRootGameObjects().Length != 0)
                        throw new InvalidOperationException("Run this scene-close fixture with a saved scene or one empty untitled startup scene.");
                    AssetDatabase.CreateFolder("Assets", startupFolder.Substring(7));
                    if (!EditorSceneManager.SaveScene(startup, startupFolder + "/Startup.unity")) throw new InvalidOperationException("Could not save the empty lifetime-test startup scene.");
                }
                bool rejected = false;
                try { MaskMapPackerNode.PackMask(source, null, null, null, 0, 1, 0.5f, 0.5f, false, 32, new StopToken(), tile); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("Read/Write") && error.Message.Contains("prepacked"); }
                Check(rejected && memory.ManagedBytes == 0 && memory.NativeBytes == 0 &&
                    new MaskMapPackerNode().ExecutionDomain == NodeExecutionDomain.MainThread && new NormalMapLayerNode().ExecutionDomain == NodeExecutionDomain.MainThread,
                    "UC-T07-10 unreadable mask inputs reject ordinary Apply before allocation/readback, with an actionable importer or prepacked-mask diagnostic.");
                Task<bool> worker = Task.Run(() =>
                {
                    int refused = 0;
                    foreach (Generator node in new Generator[] { new TextureSamplerNode(), new AlbedoLayerNode(), new NormalMapLayerNode(), new MaskMapPackerNode(), new ColorGradeNode(), new ShaderOutputNode() })
                    {
                        try { node.Generate(0, null, new StopToken()); }
                        catch (InvalidOperationException) { refused++; }
                    }
                    return refused == 6;
                });
                Check(worker.Wait(5000) && worker.Result,
                    "UC-T07-10 direct native sampler/layer/inline pack/surface/output calls refuse workers before native access or tile pulls.");
                var image = tile.GenerateTextureImage(new TextureImageDescriptor(2, 2, TextureImageColourSpace.Linear, TextureImageSemantic.Colour), _ => Color.white);
                var native = TextureImageUploadNode.Upload(image, tile, null); first = native.AcquireConsumer(); second = native.AcquireConsumer();
                preview = GraphTexturePreview.Sample(new LayerProduct { albedo = native }, tile); preview.CreateMaterial();
                Check(preview.Material != null && preview.MaterialEditor != null && memory.NativeBytes >= 4096 + 1024 + 32 * 32 * 16,
                    "UC-T07-10 actual material swatches admit their generated control texture/material estimate through the captured tile owner.");
                scheduler = new GraphPreviewScheduler((id, samples, message) =>
                {
                    if (message.Contains("retired")) { preview?.Dispose(); preview = null; }
                });
                scheduler.Request(graph, field.id);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Check(EditorSceneManager.CloseScene(scene, true) && scheduler.IsIdle && preview == null && native.texture != null && memory.NativeBytes > 0,
                    "UC-T07-10 actual scene close retires the pending/displayed preview while two independent texture consumers keep their native input valid.");
                first.Dispose(); first.Dispose();
                Check(native.texture != null && memory.NativeBytes > 0,
                    "UC-T07-10 retiring one texture consumer twice preserves the other final-consumer lease.");
                second.Dispose(); second.Dispose();
                Check(native.texture == null && memory.NativeBytes == 0,
                    "UC-T07-10 the final consumer retires its generated native input and reservation exactly once.");
                var cancellable = new PreviewCancellationNode { id = "lifetime-worker" }; graph.nodes.Add(cancellable); EditorUtility.SetDirty(graph);
                Volatile.Write(ref PreviewCancellationNode.started, 0); Volatile.Write(ref PreviewCancellationNode.exited, 0);
                scheduler.Request(graph, cancellable.id);
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref PreviewCancellationNode.started) == 0 && timeout.Elapsed.TotalSeconds < 5)
                { scheduler.Pump(); context.Drain(); Thread.Sleep(1); }
                Check(Volatile.Read(ref PreviewCancellationNode.started) == 1 && GraphPreviewScheduler.CancelAndDrain(2000) &&
                    Volatile.Read(ref PreviewCancellationNode.exited) == 1 && GraphPreviewScheduler.CapturedJobCount == 0,
                    "UC-T07-10 reload/shutdown drain cooperatively stops the actual admitted worker and retires captured inputs on their owner thread.");
                scheduler.Request(graph, field.id); WaitGraphPreview(scheduler, context);
                Check(scheduler.IsIdle && GraphPreviewScheduler.CapturedJobCount == 0,
                    "UC-T07-10 a surviving canvas can preview again after scene/Play mode cancellation without a stale worker or disposed scheduler.");
            }
            finally
            {
                scheduler?.Dispose(); preview?.Dispose(); tile.ReleaseProductMemory(); first?.Dispose(); second?.Dispose(); context.Drain();
                Event.current = previousEvent; SynchronizationContext.SetSynchronizationContext(previous);
                UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(graph);
                if (untitled && AssetDatabase.IsValidFolder(startupFolder))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    AssetDatabase.DeleteAsset(startupFolder);
                }
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0 && memory.PinnedBytes == 0,
                    "UC-T07-10 scene/reload cancellation and final consumers leave no admitted image/material/texture residency.");
            }
        }
    }
}
