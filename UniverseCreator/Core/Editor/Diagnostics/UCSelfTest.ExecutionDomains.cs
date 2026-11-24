using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T02-05: the body itself is harmless, so a rejected execution proves the admission gate acted first.
        [Serializable]
        private sealed class ExecutionDomainProbe : CreatorNode
        {
            public NodeExecutionDomain domain = NodeExecutionDomain.MainThread;
            public bool fallback, cancel;
            public int calls;
            public override string NodeName => "Execution domain probe";
            // UC-T02-05: domain admission still requires valid generator metadata.
            public override NodeCategory Category => NodeCategory.Creator;
            public override NodeExecutionDomain ExecutionDomain => domain;
            public override bool HasCpuFallback => fallback;
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                calls++; matrix.Fill(0.25f);
                if (cancel) stop.RequestStop();
            }
        }

        private static void TestExecutionDomains()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var unreadable = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            TextureRef packed = null;
            Event previousEvent = Event.current;
            try
            {
                Check(GraphExecutionGuard.IsMainThread, "UC-T02-05 Unity lifecycle registers the actual main thread before graph execution.");
                var probe = new ExecutionDomainProbe { id = "execution-domain-probe", contentHash = 71 };
                graph.nodes.Add(probe); graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph);
                TileData tile = SnapshotTile(graph);
                Check(evaluator.PullNode(probe, 0, tile, new StopToken()) is Matrix && probe.calls == 1,
                    "UC-T02-05 main-thread nodes execute in their supported domain.");
                probe.domain = NodeExecutionDomain.Worker; // Compiled requirements must remain captured until rebuilt.
                Task<bool> cached = Task.Run(() =>
                {
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                    try
                    {
                        try { evaluator.PullNode(probe, 0, tile, new StopToken()); return false; }
                        catch (InvalidOperationException) { return !GraphExecutionGuard.IsMainThread && probe.calls == 1; }
                    }
                    finally { SynchronizationContext.SetSynchronizationContext(null); }
                });
                Check(cached.Wait(5000) && cached.Result,
                    "UC-T02-05 a warm cached Unity product and a fake synchronization context cannot bypass worker-domain rejection.");
                probe.domain = NodeExecutionDomain.MainThreadGpu;
                graph.compiledPlan = CompiledGraphPlan.Build(graph); tile = SnapshotTile(graph);
                bool refused = false;
                try { evaluator.PullNode(probe, 0, tile, new StopToken()); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused == !GraphExecutionGuard.HasGraphicsDevice,
                    "UC-T02-05 GPU-only nodes reject unavailable graphics instead of pretending to produce a fallback.");
                probe.fallback = true; graph.compiledPlan = CompiledGraphPlan.Build(graph);
                Check(evaluator.PullNode(probe, 0, SnapshotTile(graph), new StopToken()) is Matrix,
                    "UC-T02-05 explicitly declared CPU fallback remains usable on a headless main thread.");
                probe.domain = NodeExecutionDomain.Worker; probe.cancel = true; graph.compiledPlan = CompiledGraphPlan.Build(graph);
                tile = SnapshotTile(graph);
                Check(evaluator.PullNode(probe, 0, tile, new StopToken()) == null && tile.GetCached(probe.id, 0) == null,
                    "UC-T02-05 cancellation between compute and publication prevents a cached result.");

                source.SetPixels(new[] { new Color(0.25f, 0f, 0f, 1f), new Color(0.25f, 0f, 0f, 1f),
                    new Color(0.25f, 0f, 0f, 1f), new Color(0.25f, 0f, 0f, 1f) }); source.Apply(false, false);
                Event.current = new Event { type = EventType.Repaint };
                packed = MaskMapPackerNode.PackMask(source, null, null, source, 0f, 1f, 0.5f, 0.5f, false, 32, new StopToken());
                Color pixel = packed.texture.GetPixel(17, 9);
                Check(Mathf.Abs(pixel.r - 0.25f) < 0.01f && pixel.g > 0.99f && Mathf.Abs(pixel.b - 0.5f) < 0.01f && Mathf.Abs(pixel.a - 0.75f) < 0.01f,
                    "UC-T02-05 readable masks pack correct URP channels during repaint without GPU readback.");
                unreadable.Apply(false, true);
                refused = false;
                try { MaskMapPackerNode.PackMask(unreadable, null, null, null, 0f, 1f, 0.5f, 0.5f, false, 32, new StopToken()); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "UC-T02-05 unreadable mask inputs fail explicitly before a blocking readback during repaint.");
                var stop = new StopToken(); stop.RequestStop();
                Check(MaskMapPackerNode.PackMask(source, null, null, null, 0f, 1f, 0.5f, 0.5f, false, 32, stop) == null,
                    "UC-T02-05 cancelled mask preparation never publishes a native texture.");
                Task<bool> gpu = Task.Run(() =>
                {
                    bool rejected = false;
                    try { MaskMapPackerNode.PackMask(source, null, null, null, 0f, 1f, 0.5f, 0.5f, false, 32, null); }
                    catch (InvalidOperationException) { rejected = true; }
                    var field = new float[9]; field[4] = 0.4f;
                    return rejected && !DropletErosionGPU.IsSupported && !DropletErosionGPU.TryRun(field, 3, 3, default(DropletSettings), null, out _) && field[4] == 0.4f;
                });
                Check(gpu.Wait(5000) && gpu.Result, "UC-T02-05 direct mask/erosion helpers reject workers before native APIs and preserve CPU inputs.");
            }
            finally
            {
                Event.current = previousEvent;
                if (packed?.texture != null) UnityEngine.Object.DestroyImmediate(packed.texture);
                UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(unreadable);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
