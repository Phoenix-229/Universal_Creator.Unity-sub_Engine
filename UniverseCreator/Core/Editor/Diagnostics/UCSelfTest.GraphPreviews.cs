// UC-T05-08: real admitted previews, obsolete-request rejection, cancellation and bounded sampling.
using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T05-08: a bounded worker fixture keeps cancellation observable after admission, without native reads.
        [Serializable] private sealed class PreviewCancellationNode : Generator
        {
            internal static int started, exited;
            public override string NodeName => "Preview cancellation fixture";
            public override NodeCategory Category => NodeCategory.Utility;
            public override Port[] GetInputs() => SharedPorts.None;
            public override Port[] GetOutputs() => new[] { new Port("Field", PortType.Matrix, PortDirection.Output) };
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                Volatile.Write(ref started, 1);
                var timeout = Stopwatch.StartNew();
                while (!stop.StopRequested && timeout.Elapsed.TotalSeconds < 3) Thread.Sleep(1);
                Volatile.Write(ref exited, 1);
                return tile.CreateMatrix();
            }
        }

        private static void TestGraphPreviews()
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            var context = new SnapshotRetirementContext();
            var savedContext = SynchronizationContext.Current;
            float[] result = null; string message = null;
            var scheduler = new GraphPreviewScheduler((id, samples, status) => { result = samples; message = status; });
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                graph.SetDomain(UCGraphDomain.TerrainWater);
                var field = new ConstantNode { id = "preview-field", value = 0.25f };
                var output = new HeightOutputNode { id = "preview-output" };
                graph.nodes.Add(field); graph.nodes.Add(output); graph.connections.Add(new Connection(field.id, 0, output.id, 0));
                scheduler.Request(graph, field.id);
                field.value = 0.75f; EditorUtility.SetDirty(graph); scheduler.AuthoredEdit(graph);
                WaitGraphPreview(scheduler, context);
                Check(result != null && result.Length == 1024 && Mathf.Abs(result[512] - 0.75f) < 1e-6f && message.Contains("Preview only"),
                    "UC-T05-08 rapid authoring requests coalesce into the latest bounded field preview without applying a world output.");
                Check(graph.nodes.Count == 2 && graph.connections.Count == 1 && field.value == 0.75f && GraphPreviewScheduler.CapturedJobCount == 0,
                    "UC-T05-08 preview retirement preserves authored topology/settings and releases every captured job.");
                var authority = new UniverseCreator.TerrainSystem.WorldGenerationScheduler(); authority.Metrics.Enabled = true;
                var measured = new TileData(Coord.Zero, new CoordRect(0, 0, 3, 3), Vector3.zero, new Vector3(2, 100, 2), 100);
                measured.metrics = authority.Metrics.Capture(authority.CurrentRun);
                var evaluator = new GraphEvaluator(graph); measured.Bind(evaluator);
                evaluator.PullNode(field, 0, measured, new StopToken()); evaluator.PullNode(field, 0, measured, new StopToken());
                Check(authority.Metrics.TryGetNode(authority.CurrentRun, field.id, out var timing) && timing.computes == 1 && timing.localHits == 1 &&
                    timing.retainedHits == 0 && timing.inclusiveMilliseconds >= 0,
                    "UC-T05-08 actual evaluator diagnostics distinguish measured compute/local reuse from isolated preview time.");
                measured.ReleaseProductMemory();
                var matrix = new Matrix(new CoordRect(0, 0, 2, 2));
                matrix.arr[0] = float.NaN; matrix.arr[1] = float.PositiveInfinity; matrix.arr[2] = -1; matrix.arr[3] = 2;
                var sampled = GraphPreviewScheduler.SampleField(matrix);
                Check(sampled.Length == 1024 && sampled[0] == 0 && sampled[1023] == 1,
                    "UC-T05-08 thumbnail sampling is bounded and sanitizes nonfinite/out-of-range field values.");
                scheduler.Request(graph, field.id); scheduler.Clear(); scheduler.Pump();
                Check(scheduler.IsIdle, "UC-T05-08 rebind cancels a pending preview before capture or worker admission.");
                scheduler.Request(graph, field.id); scheduler.Dispose(); scheduler.Pump();
                Check(scheduler.IsIdle && GraphPreviewScheduler.CapturedJobCount == 0,
                    "UC-T05-08 closing authoring retires pending previews without retaining a canvas callback.");
                var cancellable = new PreviewCancellationNode { id = "preview-cancellation" }; graph.nodes.Add(cancellable);
                int publications = 0;
                using (var active = new GraphPreviewScheduler((id, samples, status) => { publications++; result = samples; message = status; }))
                {
                    Volatile.Write(ref PreviewCancellationNode.started, 0); Volatile.Write(ref PreviewCancellationNode.exited, 0);
                    active.Request(graph, cancellable.id); WaitGraphPreview(active, context);
                    Check(Volatile.Read(ref PreviewCancellationNode.started) == 1 && Volatile.Read(ref PreviewCancellationNode.exited) == 1 &&
                        result == null && message.Contains("cancelled") && GraphPreviewScheduler.CapturedJobCount == 0,
                        "UC-T05-08 an admitted slow field cooperatively cancels at its execution budget and retires captured products without publishing a thumbnail.");
                    Volatile.Write(ref PreviewCancellationNode.started, 0); Volatile.Write(ref PreviewCancellationNode.exited, 0);
                    active.Request(graph, cancellable.id);
                    var timeout = Stopwatch.StartNew();
                    while (Volatile.Read(ref PreviewCancellationNode.started) == 0 && timeout.Elapsed.TotalSeconds < 5)
                    { active.Pump(); context.Drain(); Thread.Sleep(1); }
                    Check(Volatile.Read(ref PreviewCancellationNode.started) == 1 && GraphPreviewScheduler.CapturedJobCount == 1,
                        "UC-T05-08 closing-preview fixture reaches actual worker admission with one owned capture.");
                    int beforeClose = publications; active.Dispose(); WaitGraphPreview(active, context);
                    Check(Volatile.Read(ref PreviewCancellationNode.exited) == 1 && active.IsIdle &&
                        GraphPreviewScheduler.CapturedJobCount == 0 && publications == beforeClose,
                        "UC-T05-08 closing a canvas cancels its active worker, releases the capture after exit and never invokes the retired canvas callback.");
                }
            }
            finally
            {
                scheduler.Dispose(); context.Drain(); SynchronizationContext.SetSynchronizationContext(savedContext);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void WaitGraphPreview(GraphPreviewScheduler scheduler, SnapshotRetirementContext context)
        {
            var timeout = Stopwatch.StartNew();
            while (!scheduler.IsIdle && timeout.Elapsed.TotalSeconds < 5)
            { scheduler.Pump(); context.Drain(); Thread.Sleep(1); }
            if (!scheduler.IsIdle) throw new TimeoutException("UC-T05-08 preview failed to retire within five seconds.");
            context.Drain();
        }
    }
}
