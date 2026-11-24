// UC-T05-08: previews borrow accepted inputs and use the shared CPU admission pool; they never commit world products.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>Debounces one selected typed preview, retaining one pending request, one execution and a bounded warm product cache.</summary>
    /// <remarks>Main-thread owner. CPU previews are 32 by 32 samples, admit at most 128 authored nodes and
    /// 32 MiB of graph products, and request cooperative cancellation after 250 ms of execution.
    /// Main-thread dependencies yield between graph outputs; each node keeps its declared resource/cancellation bounds.
    /// The canvas owns one transient image/material consumer. No preview writes applied world products or persistent assets.</remarks>
    internal sealed class GraphPreviewScheduler : IDisposable
    {
        internal const int Resolution = 32, MaximumNodes = 128;
        private static readonly WorldGenerationScheduler admission = new WorldGenerationScheduler();
        private static int capturedJobs;
        private static readonly System.Collections.Generic.HashSet<GraphPreviewScheduler> owners = new System.Collections.Generic.HashSet<GraphPreviewScheduler>(); // UC-T07-10: the shared reload owner can cancel every canvas, including closed canvases with live workers.
        private readonly Action<string, float[], string> publish;
        private readonly Action<string, GraphTexturePreview, string> publishProduct; // UC-T07-08: typed thumbnail and lit-material consumers.
        private Func<bool> dependenciesCurrent;
        private readonly ProductMemoryBudget memory = new ProductMemoryBudget(32L * 1024 * 1024);
        private System.Collections.Generic.Dictionary<string, RetainedProduct> retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();
        private Graph cacheGraph;
        private Vector3 cachePosition, cacheSize;
        private float cacheHeight;
        private Guid cacheGroundVersion;
        private Func<bool> cacheInputsCurrent;
        private Graph graph;
        private string nodeId;
        private int dirtyCount;
        private double due;
        private bool pending, disposed;
        private Job job;

        private sealed class Job
        {
            internal Graph graph;
            internal string nodeId;
            internal int dirtyCount;
            internal GenerationSnapshot snapshot;
            internal TileData tile;
            internal StopToken stop;
            internal Task task;
            internal long started;
            internal float[] samples;
            internal GraphTexturePreview preview;
            internal IncrementalGraphEvaluation incremental;
            internal Action finish;
            internal TaskCompletionSource<bool> completion;
            internal string message;
            internal Func<bool> inputsCurrent;
        }

        internal GraphPreviewScheduler(Action<string, float[], string> publish, Action<string, GraphTexturePreview, string> publishProduct = null)
        {
            this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
            this.publishProduct = publishProduct;
            owners.Add(this);
            EditorApplication.update += Pump;
        }

        internal bool IsIdle => !pending && job == null;
        internal static int CapturedJobCount => capturedJobs;

        internal void Request(Graph source, string selectedNode)
        {
            if (disposed) return;
            graph = source; nodeId = selectedNode;
            dirtyCount = source != null ? EditorUtility.GetDirtyCount(source) : 0;
            pending = source != null && !string.IsNullOrEmpty(selectedNode);
            due = EditorApplication.timeSinceStartup + 0.2;
            job?.stop.RequestStop();
            if (pending) publish(nodeId, null, "Preview pending; world outputs are unchanged.");
        }

        internal void AuthoredEdit(Graph source)
        {
            if (ReferenceEquals(source, graph) && nodeId != null) Request(source, nodeId);
        }

        internal void Clear()
        {
            pending = false; graph = null; nodeId = null; dependenciesCurrent = null; job?.stop.RequestStop();
            cacheGraph = null; retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>(); memory.ClearRetained(); // UC-T07-09: live pins survive cancellation; only cache ownership is withdrawn.
        }

        // A completed worker is consumed only here on the Editor thread. Closing a window cancels it,
        // but its captured inputs remain owned until it actually exits the shared admission pool.
        internal void Pump()
        {
            // UC-T07-08: edits/Undo in a reusable function invalidate both callers, even in another open canvas.
            if (!disposed && !pending && job == null && graph != null && dependenciesCurrent != null && !dependenciesCurrent()) Request(graph, nodeId);
            if (job != null)
            {
                if (job.incremental != null && !job.task.IsCompleted)
                {
                    try
                    {
                        if (job.started == 0) job.started = Stopwatch.GetTimestamp();
                        var slice = Stopwatch.StartNew();
                        while (!job.stop.StopRequested && !job.incremental.IsComplete && slice.Elapsed.TotalMilliseconds < 3)
                            job.incremental.StepNext();
                        if (job.incremental.IsComplete) { job.finish(); job.completion.TrySetResult(true); }
                        else if (job.stop.StopRequested) job.completion.TrySetResult(false);
                    }
                    catch (Exception error) { job.completion.TrySetException(error); }
                }
                if (job.started != 0 && (Stopwatch.GetTimestamp() - job.started) / (double)Stopwatch.Frequency > 0.25)
                    job.stop.RequestStop();
                if (!job.task.IsCompleted) return;
                Job completed = job; job = null; capturedJobs--;
                try
                {
                    bool current = !disposed && !pending && ReferenceEquals(graph, completed.graph) && graph != null &&
                        nodeId == completed.nodeId && EditorUtility.GetDirtyCount(graph) == completed.dirtyCount &&
                        (completed.inputsCurrent == null || completed.inputsCurrent()) &&
                        (dependenciesCurrent == null || dependenciesCurrent());
                    if (current)
                    {
                        if (!completed.task.IsFaulted && !completed.stop.StopRequested)
                        {
                            completed.tile.AcceptRetainedProducts(retained); retained = completed.tile.retained; // UC-T07-09: accept staging while preserving unused warm branches.
                        }
                        string message = completed.task.IsFaulted ? completed.task.Exception.GetBaseException().Message :
                            completed.stop.StopRequested ? "Preview cancelled or exceeded its cooperative work budget." : completed.message;
                        if (publishProduct != null && !completed.task.IsFaulted && !completed.stop.StopRequested)
                        {
                            bool transfersTile = completed.preview?.Tile != null; // UC-T07-08: CPU thumbnails carry samples, not the evaluation's resource owner.
                            publishProduct(nodeId, completed.preview, message);
                            completed.preview = null; // The canvas now owns any retained tile/native resources.
                            if (transfersTile) completed.tile = null;
                        }
                        else publish(nodeId, completed.stop.StopRequested ? null : completed.samples, message);
                    }
                    else if (!disposed && !pending && graph != null) Request(graph, nodeId);
                }
                finally { completed.preview?.Dispose(); completed.tile?.ReleaseProductMemory(); completed.snapshot.Release(); }
            }
            if (disposed) { memory.ClearRetained(); owners.Remove(this); EditorApplication.update -= Pump; return; }
            if (!pending || EditorApplication.timeSinceStartup < due || capturedJobs >= 2) return;
            pending = false;
            if (graph == null || graph.nodes == null || graph.nodes.Count > MaximumNodes)
            { publish(nodeId, null, "Preview limit: select a reusable block with at most 128 nodes."); return; }
            GenerationSnapshot snapshot = null;
            TileData tile = null;
            try
            {
                dependenciesCurrent = CaptureDependencies(graph);
                // Capturing allows all saved domains; the worker's existing execution guard still refuses
                // native/GPU-only ancestors. Disconnected native branches do not block a safe field preview.
                snapshot = GenerationSnapshot.CaptureForDomains(graph,
                    NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu);
                var selected = snapshot.Graph.GetNodeById(nodeId);
                if (selected == null) throw new InvalidOperationException("The selected preview node is unavailable.");
                tile = new TileData(Coord.Zero, new CoordRect(0, 0, Resolution, Resolution), Vector3.zero, new Vector3(32, 100, 32), 100);
                tile.AttachMemory(memory); // UC-T07-09: one admitted pool includes warm products and the in-flight request.
                tile.RetainGenerationInputs(snapshot);
                var next = new Job { graph = graph, nodeId = nodeId, dirtyCount = EditorUtility.GetDirtyCount(graph),
                    snapshot = snapshot, tile = tile, stop = new StopToken() };
                Guid groundVersion = Guid.Empty;
                if (TextureFunctionCallNode.UsesAcceptedWorldInputs(snapshot.Graph)) // UC-T07-08: world inputs inside captured functions use the same accepted owner.
                {
                    UniverseCreatorObject world = null;
                    foreach (var candidate in UnityEngine.Object.FindObjectsByType<UniverseCreatorObject>(FindObjectsInactive.Include))
                    {
                        var bindings = candidate.GetComponent<UCWorldGraphBindings>();
                        if (candidate.graph != graph && (bindings == null ||
                            (bindings.VegetationGraph != graph && bindings.LightingGraph != graph && bindings.TextureGraph != graph))) continue;
                        if (world != null) throw new InvalidOperationException("Preview is ambiguous: this graph is bound to multiple worlds.");
                        world = candidate;
                    }
                    if (world == null) throw new InvalidOperationException("Preview pending: bind this graph to a world with accepted ground.");
                    float size = world.GetChunkSize();
                    Vector3 centre = world.transform.position + new Vector3(size * 0.5f, 0, size * 0.5f);
                    if (!WorldGraphProducts.TryAcquireGround(world, null, centre, out var handle, out var diagnostic))
                        throw new InvalidOperationException(diagnostic);
                    tile.OwnGraphInput(handle);
                    var run = world.GenerationScheduler.CurrentRun; // Read-only: a preview cannot begin or supersede a world revision.
                    if (!handle.TryGet(run, GraphProductUnits.WorldMetres, out var ground))
                        throw new InvalidOperationException("Preview pending: accepted ground belongs to an older run.");
                    groundVersion = ground.GroundVersion;
                    tile.AssignRun(run); tile.worldPos = new Vector3(ground.Bounds.x, world.transform.position.y, ground.Bounds.y);
                    tile.worldSize = new Vector3(ground.Bounds.width, world.maxHeightMeters, ground.Bounds.height); tile.worldHeight = world.maxHeightMeters;
                    var binding = world.GetComponent<UCWorldGraphBindings>();
                    var water = WorldGraphWaterInput.Capture(world); var environment = binding?.CaptureEnvironmentInputs(true);
                    if (binding != null) binding.BindAcceptedInputs(tile, world.graph, ground, water, environment);
                    else tile.BindWorldGraphInputs(world.graph, ground, water, environment);
                    next.inputsCurrent = () => world != null && world.GenerationScheduler.IsCurrent(run) &&
                        RiverChannelRegistry.TryGetCommittedSnapshot(world, centre.x, centre.z, out var latest) &&
                        latest.GroundVersion == ground.GroundVersion && water.IsCurrent() && (environment == null || environment.IsCurrent());
                }
                if (!ReferenceEquals(cacheGraph, graph) || cachePosition != tile.worldPos || cacheSize != tile.worldSize ||
                    cacheHeight != tile.worldHeight || cacheGroundVersion != groundVersion || (cacheInputsCurrent != null && !cacheInputsCurrent()) || retained.Count > 4096)
                {
                    memory.ClearRetained(); retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();
                    cacheGraph = graph; cachePosition = tile.worldPos; cacheSize = tile.worldSize; cacheHeight = tile.worldHeight; cacheGroundVersion = groundVersion;
                }
                cacheInputsCurrent = next.inputsCurrent;
                tile.retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>(retained); // UC-T07-09: obsolete workers cannot poison warm entries.
                job = next; capturedJobs++;
                next.finish = () =>
                {
                    object product = next.tile.GetCached(selected.id, 0) ?? next.tile.outputs.surface;
                    if (next.stop.StopRequested) return;
                    next.preview = GraphTexturePreview.Sample(product, next.tile); next.samples = next.preview.Field;
                    double milliseconds = (Stopwatch.GetTimestamp() - next.started) * 1000.0 / Stopwatch.Frequency;
                    next.message = next.preview.Surface == null && next.samples == null && next.preview.Pixels == null ? "No image or material at this output. Connect a typed preview result." :
                        "Preview only: " + milliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                        " ms including upstream work. " + (next.preview.Surface != null ? "Lit material swatch; Run/Apply updates the world." :
                        next.preview.Pixels != null ? "Image: " + next.preview.Semantic + ", " + next.preview.Encoding + "." : "World outputs are unchanged.");
                };
                // UC-T07-09: prepared native work yields between dependency/output pulls; each node retains its
                // existing bounded allocation/cancellation contract. CPU-only requests use the shared admission pool.
                if (NeedsMainThread(snapshot.Graph, selected, new System.Collections.Generic.HashSet<string>()))
                {
                    next.incremental = new IncrementalGraphEvaluation(snapshot.Graph, next.tile, next.stop, selected);
                    next.completion = new TaskCompletionSource<bool>(); next.task = next.completion.Task;
                }
                else next.task = admission.RunWorkerAsync(admission.CurrentRun, 1, next.stop, () =>
                {
                    next.started = Stopwatch.GetTimestamp();
                    var evaluator = new GraphEvaluator(next.snapshot.Graph); next.tile.Bind(evaluator);
                    evaluator.PullNode(selected, 0, next.tile, next.stop); next.finish();
                });
            }
            catch (Exception error)
            {
                if (job != null) { job.preview?.Dispose(); job = null; capturedJobs--; }
                tile?.ReleaseProductMemory(); snapshot?.Release();
                publish(nodeId, null, error.Message);
            }
        }

        private static bool NeedsMainThread(Graph graph, Generator node, System.Collections.Generic.HashSet<string> visited)
        {
            if (node == null || !visited.Add(node.id)) return false;
            if (node.ExecutionDomain != NodeExecutionDomain.Worker) return true;
            if (node is TextureFunctionCallNode call && call.CapturedGraph != null)
                foreach (Generator child in call.CapturedGraph.nodes)
                    if (NeedsMainThread(call.CapturedGraph, child, new System.Collections.Generic.HashSet<string>())) return true;
            if (node is IPortalIn portal && NeedsMainThread(graph, portal.ResolveSender(graph), visited)) return true;
            foreach (Connection edge in graph.connections)
                if (edge.targetNodeId == node.id && NeedsMainThread(graph, graph.GetNodeById(edge.sourceNodeId), visited)) return true;
            return false;
        }

        internal static Func<bool> CaptureDependencies(Graph graph, int maximumNodes = MaximumNodes)
        {
            var revisions = new System.Collections.Generic.Dictionary<Graph, int>();
            int nodes = 0;
            Action<Graph, int> capture = null;
            capture = (source, depth) =>
            {
                if (source == null || depth > 8) throw new InvalidOperationException("Restore missing/recursive preview function assets.");
                if (revisions.ContainsKey(source)) return;
                revisions.Add(source, EditorUtility.GetDirtyCount(source)); nodes += source.nodes.Count;
                if (nodes > maximumNodes) throw new InvalidOperationException("Graph function dependencies exceed the " + maximumNodes + "-node execution limit.");
                foreach (Generator node in source.nodes) if (node is TextureFunctionCallNode call) capture(call.function, depth + 1);
            };
            capture(graph, 0);
            return () =>
            {
                foreach (var pair in revisions) if (pair.Key == null || EditorUtility.GetDirtyCount(pair.Key) != pair.Value) return false;
                return true;
            };
        }

        internal static float[] SampleField(Matrix matrix)
        {
            if (matrix == null || matrix.arr == null || matrix.rect.size.x < 1 || matrix.rect.size.z < 1) return null;
            var result = new float[Resolution * Resolution];
            for (int z = 0; z < Resolution; z++)
                for (int x = 0; x < Resolution; x++)
                {
                    int sx = x * (matrix.rect.size.x - 1) / (Resolution - 1);
                    int sz = z * (matrix.rect.size.z - 1) / (Resolution - 1);
                    float value = matrix.arr[sz * matrix.rect.size.x + sx];
                    result[z * Resolution + x] = float.IsNaN(value) || float.IsInfinity(value) ? 0 : Mathf.Clamp01(value);
                }
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Clear();
            if (job == null) { owners.Remove(this); EditorApplication.update -= Pump; }
        }

        internal static void CancelScenePreviews()
        {
            foreach (var owner in new System.Collections.Generic.List<GraphPreviewScheduler>(owners))
            {
                string selected = owner.nodeId; owner.Clear();
                if (!owner.disposed && selected != null) owner.publish(selected, null, "Preview retired; select a node after the scene/Play mode change."); // UC-T07-10: canvas retirement releases its displayed native material too.
            }
        }

        // UC-T07-10: the existing domain reload guard owns this exceptional bounded drain; ordinary cancellation keeps pumping asynchronously.
        internal static bool CancelAndDrain(int timeoutMs)
        {
            CancelScenePreviews(); var deadline = Stopwatch.StartNew(); bool drained = true;
            foreach (var owner in new System.Collections.Generic.List<GraphPreviewScheduler>(owners))
            {
                if (owner.job == null) continue;
                if (owner.job.incremental == null)
                {
                    int remaining = Math.Max(0, timeoutMs - (int)deadline.ElapsedMilliseconds);
                    try { if (!owner.job.task.Wait(remaining)) { drained = false; continue; } }
                    catch (AggregateException error) { UnityEngine.Debug.LogWarning("UC-T07-10: retiring a failed preview during reload: " + error.GetBaseException().Message); }
                }
                owner.Pump();
            }
            return drained;
        }
    }
}
