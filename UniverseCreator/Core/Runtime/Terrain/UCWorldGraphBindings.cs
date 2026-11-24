// UC-T05-03: serialized world bindings and explicit companion execution on the existing world authority/worker pool.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Owns companion graph references and the last explicitly applied vegetation plan for one UC world.</summary>
    /// <remarks>Uses the world's scheduler and ChunkManager commit pump. Authored edits never run this component implicitly.</remarks>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(UniverseCreatorObject))]
    public sealed partial class UCWorldGraphBindings : MonoBehaviour
    {
        [SerializeField, HideInInspector] private int schemaVersion = 1;
        [SerializeField, Tooltip("Vegetation/placement graph. Edits remain pending until Run/Apply.")] private Graph vegetationGraph;
        private UniverseCreatorObject owner;
        private GenerationSnapshot acceptedVegetation;
        private Graph acceptedMain;
        private GenerationRunId acceptedTerrain;
        private StopToken pendingVegetation, streamingStop = new StopToken();
        private Task<bool> pendingTask;
        private Task streamingTask;
        private Queue<Coord> streaming = new Queue<Coord>(); // UC-T05-03: retired workers cannot drain a replacement plan's queue.
        internal bool HasStreamingVegetationWork => streaming.Count > 0 || streamingTask != null && !streamingTask.IsCompleted; // UC-T09-03: author Run and accepted-plan streaming are different readiness work.
        internal bool StreamingVegetationFailed { get; private set; } // UC-T09-03: a completed failed task cannot release dependent presentation.
        private long vegetationRevision;

        /// <summary>Saved companion-binding schema, currently one; unsupported schemas cannot execute.</summary>
        public int SchemaVersion => schemaVersion;
        /// <summary>Bound world; the existing world.graph remains the authoritative main Node Graph.</summary>
        public UniverseCreatorObject Owner => owner != null ? owner : owner = GetComponent<UniverseCreatorObject>();
        /// <summary>Authored vegetation asset. Null unbinds; incompatible metadata is rejected before assignment.</summary>
        /// <exception cref="ArgumentException">A non-null graph is not an explicitly supported Vegetation graph.</exception>
        public Graph VegetationGraph
        {
            get => vegetationGraph;
            set
            {
                if (value != null && (!value.HasSupportedDomainMetadata || value.Domain != UCGraphDomain.Vegetation))
                    throw new ArgumentException("Select an explicit Vegetation Graph asset.", nameof(value));
                if (!ReferenceEquals(vegetationGraph, value)) CancelVegetation();
                vegetationGraph = value;
            }
        }
        /// <summary>Last accepted companion revision; zero means no applied plan exists in this runtime lifetime.</summary>
        public long VegetationRevision => vegetationRevision;
        /// <summary>Current visible execution/dependency diagnostic; failures preserve the previously accepted plan.</summary>
        public string VegetationStatus { get; private set; } = "Pending: select a graph and build accepted main-world ground.";
        /// <summary>Whether a candidate Run is still evaluating; streaming from an accepted plan is separate.</summary>
        public bool IsVegetationRunning => pendingTask != null && !pendingTask.IsCompleted;
        // UC-T06-07: native ground refresh must have exactly one placement owner.
        internal bool UsesAcceptedVegetationGround => acceptedVegetation != null && ReferenceEquals(Owner.graph, acceptedMain) &&
            Owner.GenerationScheduler.IsCurrent(acceptedTerrain);

        /// <summary>Evaluates a frozen vegetation candidate against accepted terrain and publishes only after every current region succeeds.</summary>
        /// <returns>True after managed acceptance; false on missing inputs, cancellation, stale terrain or an explicitly reported error.</returns>
        /// <remarks>Main-thread entry. CPU work uses the existing admitted worker pool; native scatter drains through the existing chunk pump.</remarks>
        public Task<bool> RunVegetationAsync()
            => BeginVegetationRun(null);

        /// <summary>UC-T06-03: applies this world's Vegetation Graph to one resident accepted terrain region through the shared scatter pump.</summary>
        /// <param name="cell">Integer native terrain-region coordinate in the bound world; missing/stale/unloaded cells remain pending.</param>
        /// <returns>True after managed acceptance; false on missing inputs, cancellation, stale ground or reported failure.</returns>
        /// <remarks>Main thread. Other resident regions retain their applied placement. The newly accepted world authoring serves future streamed regions; this does not generate terrain.</remarks>
        public Task<bool> RunVegetationRegionAsync(Coord cell)
            => BeginVegetationRun(cell);

        /// <summary>UC-T06-03: replaces placement only inside a half-open world-metre rectangle, retaining prior accepted placement outside it.</summary>
        /// <param name="worldBounds">Absolute X/Z rectangle in metres; finite with positive width/depth. Each intersecting cell requires prior accepted placement.</param>
        /// <returns>True after managed acceptance; false for unavailable regions, cancellation, stale input or a reported failure.</returns>
        /// <exception cref="ArgumentException">Bounds are nonfinite, nonpositive or collapse at float precision.</exception>
        /// <remarks>Main thread. Existing world authoring/worker/native owners are reused. Unloaded cells remain pending; patch Run does not build terrain.</remarks>
        public Task<bool> RunVegetationPatchAsync(Rect worldBounds)
        {
            GraphVegetationPatch.ValidateBounds(worldBounds);
            return BeginVegetationRun(null, worldBounds);
        }

        private Task<bool> BeginVegetationRun(Coord? onlyCell, Rect? patch = null)
        {
            GraphExecutionGuard.RequireMainThread("Vegetation Graph Run");
            CancelVegetation();
            pendingVegetation = new StopToken();
            pendingTask = EvaluateVegetationAsync(pendingVegetation, onlyCell, patch);
            return pendingTask;
        }

        /// <summary>Cooperatively cancels only the pending vegetation candidate; accepted planting and terrain remain available.</summary>
        public void CancelVegetation()
        {
            pendingVegetation?.RequestStop();
            if (IsVegetationRunning) VegetationStatus = "Cancelled; the previous accepted plan is retained.";
        }

        /// <summary>Reports stale terrain dependencies without running either graph or changing accepted planting.</summary>
        /// <remarks>Main-thread status refresh for inspectors and runtime diagnostics; authoring remains explicit.</remarks>
        public void RefreshVegetationDependencies()
        {
            if (acceptedVegetation != null && (Owner == null || !ReferenceEquals(Owner.graph, acceptedMain) ||
                !Owner.GenerationScheduler.IsCurrent(acceptedTerrain)))
                VegetationStatus = "Stale: main terrain changed; Run vegetation against the new accepted revision.";
        }

        private async Task<bool> EvaluateVegetationAsync(StopToken stop, Coord? onlyCell, Rect? patch)
        {
            GenerationSnapshot candidate = null;
            var results = new List<GraphVegetationResult>();
            var groundPins = new List<ProductMemoryBudget.Lease>(); // UC-T05-03: all captured regions stay admitted while sequential workers await their turn.
            try
            {
                var world = Owner;
                if (schemaVersion != 1 || world == null || vegetationGraph == null || !vegetationGraph.HasSupportedDomainMetadata ||
                    vegetationGraph.Domain != UCGraphDomain.Vegetation || world.graph == null ||
                    !world.graph.HasSupportedDomainMetadata || world.graph.Domain != UCGraphDomain.TerrainWater)
                { VegetationStatus = "Pending: bind explicit Vegetation and main TerrainWater assets to this world."; return false; }
                string missing = MissingPrototypes(vegetationGraph, world.scatterLibrary);
                if (missing != null) { VegetationStatus = missing; return false; }
                var chunks = world.Chunks;
                Graph main = world.graph, authored = vegetationGraph;
                GenerationRunId run = world.GenerationScheduler.CaptureRun();
                var regions = chunks != null ? chunks.CaptureGraphVegetationRegions(run, onlyCell) : new List<GraphVegetationRegion>();
                if (patch.HasValue) regions.RemoveAll(region => !new Rect(region.Position.x, region.Position.z, region.Size.x, region.Size.z).Overlaps(patch.Value));
                if (regions.Count == 0) { VegetationStatus = onlyCell.HasValue ? "Pending: the requested world region has no resident accepted ground." :
                    "Pending: build accepted main-world terrain before running vegetation."; return false; }
                if (patch.HasValue)
                {
                    // UC-T06-03: a partly resident patch must not claim acceptance for its missing ground.
                    bool covered = false;
                    await world.GenerationScheduler.RunWorkerAsync(run, 1, stop, () => covered = GraphVegetationPatch.HasCoverage(regions, patch.Value, stop));
                    if (stop.StopRequested || !world.GenerationScheduler.IsCurrent(run)) throw new OperationCanceledException();
                    if (!covered)
                    { VegetationStatus = "Pending: the full vegetation patch needs resident accepted ground; coverage is missing or exceeds the bounded coverage limit."; return false; }
                }
                if (patch.HasValue && regions.Exists(region => region.PriorOutputs == null || !region.PriorOutputs.RunId.Equals(run)))
                { VegetationStatus = "Pending: Apply vegetation to the intersecting regions before preserving their placement in a patch."; return false; }
                foreach (var region in regions)
                {
                    groundPins.Add(world.ProductMemory.Acquire(region.Ground, ProductMemoryFootprint.Estimate(region.Ground), 0, "vegetation-accepted-ground"));
                    if (patch.HasValue) groundPins.Add(world.ProductMemory.Acquire(region.PriorOutputs, ProductMemoryFootprint.Estimate(region.PriorOutputs), 0,
                        "vegetation-patch-prior")); // UC-T06-03: concurrent retirement cannot unaccount prior arrays while patch workers still read them.
                }
                var water = WorldGraphWaterInput.Capture(world); var environment = CaptureEnvironmentInputs(true); // UC-T05-06: one immutable shared input capture for the entire candidate.
                water.Pin(world.ProductMemory, groundPins); environment?.Pin(world.ProductMemory, groundPins);
                ValidateWorldInputs(vegetationGraph, world.graph);
                candidate = GenerationSnapshot.Capture(vegetationGraph, world, requireOutput: true);
                VegetationStatus = "Running vegetation against accepted terrain " + run.Revision + ".";
                foreach (var region in regions)
                {
                    if (stop.StopRequested || !world.GenerationScheduler.IsCurrent(run)) throw new OperationCanceledException();
                    results.Add(await EvaluateRegionAsync(world, main, candidate, region, run, stop, water: water, environment: environment, patch: patch));
                }
                if (stop.StopRequested || !ReferenceEquals(pendingVegetation, stop) || !ReferenceEquals(world.graph, main) ||
                    !ReferenceEquals(vegetationGraph, authored) || !chunks.AreGraphVegetationRegionsCurrent(results, run) ||
                    !water.IsCurrent() || (environment != null && !environment.IsCurrent()))
                {
                    if (ReferenceEquals(pendingVegetation, stop)) VegetationStatus = "Pending: terrain changed or Run was cancelled; the previous accepted plan is retained.";
                    return false;
                }
                long next = checked(vegetationRevision + 1);
                if (!world.GenerationScheduler.TryPublish(run, () =>
                {
                    streamingStop.RequestStop(); streaming.Clear(); streamingStop = new StopToken();
                    streaming = new Queue<Coord>(); streamingTask = null;
                    StreamingVegetationFailed = false; // UC-T09-03: explicit accepted replacement retires the previous failed streaming plan.
                    chunks.AcceptGraphVegetation(results, run);
                    var old = acceptedVegetation; acceptedVegetation = candidate; candidate = null;
                    acceptedTerrain = run; acceptedMain = main; vegetationRevision = next; old?.Release();
                })) return false;
                VegetationStatus = "Accepted vegetation " + vegetationRevision + (patch.HasValue ? "; world patch in " + regions.Count + " regions" :
                    onlyCell.HasValue ? "; one world region" : "; " + regions.Count + " world regions") + "; queued for the existing scatter pump.";
                return true;
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(pendingVegetation, stop)) VegetationStatus = "Cancelled or stale terrain; the previous accepted plan is retained.";
                return false;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(pendingVegetation, stop))
                {
                    VegetationStatus = "Vegetation Run failed: " + ex.Message;
                    Debug.LogError(VegetationStatus, this);
                }
                return false;
            }
            finally
            {
                foreach (var result in results) result?.Dispose();
                foreach (var pin in groundPins) pin.Dispose();
                candidate?.Release();
                Owner.Chunks?.NotifyReadinessDependencyCompleted(); // UC-T09-03: explicit companion acceptance/cancellation also wakes stationary readiness observation.
            }
        }

        private static string MissingPrototypes(Graph graph, ScatterPrototypeLibrary library)
        {
            foreach (var node in graph.nodes)
            {
                if (node == null || node.disabled || !(node is IOutputNode) || node.Category != NodeCategory.Scatter) continue;
                string channel = node is Nodes.Scatter.ScatterNodeBase scatter ? scatter.Channel : null;
                bool available = library != null;
                if (channel == "trees") available &= library != null && library.trees != null && library.HasTrees;
                else if (channel == "shrubs") available &= library != null && library.shrubs != null && library.HasShrubs;
                else if (channel == "grass" || (channel != null && channel.StartsWith("detail_", StringComparison.Ordinal)))
                    available &= library != null && library.details != null && library.HasDetails;
                else available &= library != null && library.objects != null && library.objects.Exists(item => item != null && item.prefab != null);
                if (!available) return "Pending: assign licensed " + (channel ?? node.NodeName) + " prototypes in the bound world's Scatter Library.";
            }
            return null;
        }

        private static void ValidateWorldInputs(Graph companion, Graph main)
        {
            foreach (var node in companion.nodes)
            {
                if (node is WorldGraphInputNode input && !ReferenceEquals(input.mainGraph, null) && !ReferenceEquals(input.mainGraph, main))
                    throw new InvalidOperationException("World Graph Input selects a foreign/unbuilt main graph; it must use this bound world's accepted graph.");
                if (node is Nodes.Scatter.ScatterNodeBase && !node.disabled &&
                    string.IsNullOrEmpty(companion.GetInputConnection(node.id, 0).sourceNodeId))
                    throw new InvalidOperationException("Missing Height input for " + node.NodeName + "; connect accepted Final Ground before applying vegetation."); // UC-T05-03: an unwired replacement must not erase valid planting silently.
            }
        }

        private async Task<GraphVegetationResult> EvaluateRegionAsync(UniverseCreatorObject world, Graph main, GenerationSnapshot snapshot,
            GraphVegetationRegion region, GenerationRunId run, StopToken stop, Func<Generator, bool> outputFilter = null,
            WorldGraphWaterInput water = null, WorldGraphEnvironmentInput environment = null, Rect? patch = null)
        {
            int resolution = region.Native != null ? Math.Min(257, region.Native.heightmapResolution) : 65; // UC-T05-04: independent baseline fields use a bounded world lattice.
            var tile = new TileData(region.Cell, new CoordRect(0, 0, resolution, resolution), region.Position, region.Size, region.Size.y);
            var result = new GraphVegetationResult { Region = region, Patch = patch };
            try
            {
                tile.AttachMemory(world.ProductMemory); tile.AssignRun(run); tile.RetainGenerationInputs(snapshot);
                if (region.Ground != null) BindAcceptedInputs(tile, main, region.Ground, water, environment); // UC-T05-06: typed leases use the existing tile retirement owner.
                tile.SetFullWorld(snapshot.Settings.worldPos, snapshot.Settings.worldSize);
                float north = snapshot.Settings.worldSize.z > 0 ? Mathf.Clamp01((region.Position.z + region.Size.z * 0.5f -
                    snapshot.Settings.worldPos.z) / snapshot.Settings.worldSize.z) : 0.5f;
                tile.ecology = snapshot.Ecology?.ForChunk(north);
                Graph graph = snapshot.Graph;
                await world.GenerationScheduler.RunWorkerAsync(run, 1, stop, () =>
                {
                    new GraphEvaluator(graph).Evaluate(tile, stop, outputFilter);
                    if (patch.HasValue) GraphVegetationPatch.Merge(region.PriorOutputs, tile.outputs, region.Position, region.Size, patch.Value, stop);
                    // UC-T06-03: immutable hole coverage is managed worker work too; only native publication stays on the main thread.
                    if (region.Ground != null) foreach (var channel in tile.outputs.scatter.Values)
                        channel.RemoveAll(instance => !region.Ground.TryGround(instance.position.x, instance.position.z, out _));
                    if (region.Ground != null) foreach (var coverage in tile.outputs.detailLayers.Values)
                        for (int z = 0; z < coverage.GetLength(0); z++)
                        {
                            if (stop.StopRequested) throw new OperationCanceledException();
                            for (int x = 0; x < coverage.GetLength(1); x++)
                                if (!region.Ground.TryGround(region.Position.x + (x + 0.5f) / coverage.GetLength(1) * region.Size.x,
                                    region.Position.z + (z + 0.5f) / coverage.GetLength(0) * region.Size.z, out _)) coverage[z, x] = 0;
                        }
                });
                if (stop.StopRequested || !world.GenerationScheduler.IsCurrent(run)) throw new OperationCanceledException();
                tile.TrackProductMemory(tile.outputs, "vegetation-output");
                result.Outputs = tile.outputs; result.Memory = tile.DetachProductMemory(tile.outputs);
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { tile.ReleaseProductMemory(); }
        }

        internal void OnGroundAccepted(ChunkManager chunks, Coord cell)
        {
            RetireStaleSurfaces(); // UC-T05-06: replacement/unload cannot indefinitely pin obsolete material regions.
            InvalidateLightingGround(); // UC-T05-04: a same-revision seam/re-cut cannot keep serving old environment ground fields.
            if (acceptedVegetation == null) return;
            RefreshVegetationDependencies();
            if (!ReferenceEquals(Owner.graph, acceptedMain) || !Owner.GenerationScheduler.IsCurrent(acceptedTerrain)) return;
            QueueVegetationRegion(chunks, cell); // UC-T06-11: ground and accepted-water events share exactly one streaming owner.
        }

        internal void OnAcceptedWaterChanged(Bounds changed)
        {
            if (!isActiveAndEnabled || !UsesAcceptedVegetationGround) return;
            var chunks = Owner.Chunks;
            if (chunks == null) return;
            float reach = acceptedVegetation.Ecology != null ? Mathf.Max(0f, acceptedVegetation.Ecology.settings.waterSearchRadiusM) : 0f;
            foreach (var region in chunks.CaptureGraphVegetationRegions(acceptedTerrain))
                if (WaterChangeAffectsRegion(changed, new Rect(region.Position.x, region.Position.z, region.Size.x, region.Size.z), reach))
                    QueueVegetationRegion(chunks, region.Cell);
        }

        internal static bool WaterChangeAffectsRegion(Bounds changed, Rect region, float reach)
        {
            // UC-T06-11: conservative XZ proximity reach; preserve every unaffected region and avoid reacting to animated waves.
            return changed.min.x - reach <= region.xMax && changed.max.x + reach >= region.xMin &&
                changed.min.z - reach <= region.yMax && changed.max.z + reach >= region.yMin;
        }

        private void QueueVegetationRegion(ChunkManager chunks, Coord cell)
        {
            chunks.RequireReadinessRefresh(cell); // UC-T09-03: ground/water-dependent region replacement must not reveal its old placement.
            if (!streaming.Contains(cell)) streaming.Enqueue(cell);
            if (streamingTask == null || streamingTask.IsCompleted) streamingTask = StreamVegetationAsync(chunks, streamingStop, streaming);
        }

        private async Task StreamVegetationAsync(ChunkManager chunks, StopToken stop, Queue<Coord> queue)
        {
            var snapshot = acceptedVegetation;
            Graph main = acceptedMain;
            GenerationRunId run = acceptedTerrain;
            using (snapshot.Acquire())
            {
                try
                {
                    while (!stop.StopRequested && queue.Count > 0 && Owner.GenerationScheduler.IsCurrent(run))
                    {
                        var water = WorldGraphWaterInput.Capture(Owner); var environment = CaptureEnvironmentInputs(true); // UC-T05-06: streaming also consumes accepted water/environment.
                        var regions = chunks.CaptureGraphVegetationRegions(run, queue.Dequeue());
                        foreach (var region in regions)
                        {
                            using (var result = await EvaluateRegionAsync(Owner, main, snapshot, region, run, stop, water: water, environment: environment))
                            {
                                var batch = new[] { result };
                                if (!stop.StopRequested && ReferenceEquals(snapshot, acceptedVegetation) && chunks.AreGraphVegetationRegionsCurrent(batch, run) &&
                                    water.IsCurrent() && (environment == null || environment.IsCurrent()))
                                    Owner.GenerationScheduler.TryPublish(run, () => chunks.AcceptGraphVegetation(batch, run));
                                else if (!stop.StopRequested && ReferenceEquals(snapshot, acceptedVegetation) && Owner.GenerationScheduler.IsCurrent(run))
                                {
                                    StreamingVegetationFailed = true;
                                    VegetationStatus = "Vegetation streaming held: accepted ground/water/environment changed during evaluation; rebuild the dependent region.";
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* A replaced accepted plan keeps ownership until this lease retires. */ }
                catch (Exception ex)
                {
                    if (ReferenceEquals(snapshot, acceptedVegetation))
                    { StreamingVegetationFailed = true; VegetationStatus = "Vegetation streaming failed: " + ex.Message; Debug.LogError(VegetationStatus, this); }
                }
                finally { chunks.NotifyReadinessDependencyCompleted(); } // UC-T09-03: wake stationary presentation even when the async worker is the last outstanding owner.
            }
        }

        private void OnDisable()
        {
            CancelVegetation(); streamingStop.RequestStop(); streaming.Clear();
            acceptedVegetation?.Release(); acceptedVegetation = null; acceptedMain = null;
            RetireLightingGraphs(); // UC-T05-04: both companion domains retire under their existing world binding lifetime.
            RetireGraphProducts(); // UC-T05-06: independently pinned consumers survive producer retirement.
            RetireTextureQuality(); // UC-T07-11: retire only this world's per-terrain sampling overrides and reusable block reservation.
        }

        private void OnDestroy() => OnDisable(); // UC-T05-03: idempotent retirement also covers explicit component destruction.

        private void OnEnable()
        {
            InitializeTextureQuality(); // UC-T07-11: restored Player/Play lifetimes register already-applied materials without rerunning either graph.
            if (acceptedVegetation == null) VegetationStatus = "Pending: references are saved; build accepted ground and Run to capture this lifetime's plan.";
        }
    }
}
