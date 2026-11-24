using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T01-06: opt-in copying of nonserialized, pushed inputs that graph cloning cannot preserve.</summary>
    /// <remarks>Main thread only. Deep-copy mutable lists/arrays; the target has the same node type and serialized ID.</remarks>
    public interface IGenerationSnapshotInputs
    {
        /// <summary>Copies pushed input state without changing the authoring node.</summary>
        /// <param name="snapshot">Same-type detached node; implementations reject a mismatched type.</param>
        void CopyInputsTo(Generator snapshot);
    }

    /// <summary>UC-T07-08: nested graph inputs captured and retired with their detached parent snapshot.</summary>
    /// <remarks>Main thread only; capture must release partial work on failure and release must be idempotent.</remarks>
    public interface IGenerationSnapshotDependencies
    {
        /// <summary>Captures immutable dependencies using the parent's world and admitted execution stages.</summary>
        /// <param name="world">Bound world, or null for explicitly pushed standalone inputs.</param>
        /// <param name="domains">Execution stages admitted by the parent executor.</param>
        void CaptureDependencies(UniverseCreatorObject world, NodeExecutionDomain domains);
        /// <summary>Releases nested snapshot owners after the parent and all its consumers retire.</summary>
        void ReleaseDependencies();
    }

    /// <summary>UC-T01-06: owns one detached, prepared graph and its world ecology until all workers and tiles release it.</summary>
    /// <remarks>
    /// Capture on Unity's main thread. Node values, topology and prepared inputs are read-only during a bake;
    /// per-tile evaluation scratch belongs to the evaluator/tile. Shared Unity asset handles remain read-only.
    /// Release the owner on replacement/cancellation; each worker and published tile retains its own lease.
    /// Native graph destruction is posted to the captured Unity synchronization context after the final lease.
    /// </remarks>
    public sealed class GenerationSnapshot
    {
        private readonly object lifetimeLock = new object();
        private readonly SynchronizationContext mainContext;
        private readonly int mainThreadId;
        private Graph graph;
        private readonly List<Geology.LithologyProfile> ownedProfiles;
        private int references = 1;
        private bool ownerReleased;

        /// <summary>Copied ecology settings/species tables; null when ecology is disabled.</summary>
        public readonly Ecology.EcoScatterContext Ecology;

        /// <summary>Captured world origin/extent in metres, height scale, seed and whole-world resolution.</summary>
        public readonly GenerationSettings Settings;
        /// <summary>Captured chunk edge length in metres; zero for a snapshot with no world owner.</summary>
        public readonly float ChunkSizeMetres;
        /// <summary>Captured base chunk resolution in samples; zero for a snapshot with no world owner.</summary>
        public readonly int ChunkResolution;

        private GenerationSnapshot(Graph graph, Ecology.EcoScatterContext ecology, SynchronizationContext context,
                                   GenerationSettings settings, float chunkSize, int chunkResolution,
                                   List<Geology.LithologyProfile> ownedProfiles)
        {
            this.graph = graph;
            this.ownedProfiles = ownedProfiles;
            Ecology = ecology;
            mainContext = context;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Settings = settings;
            ChunkSizeMetres = chunkSize;
            ChunkResolution = chunkResolution;
        }

        /// <summary>Detached graph; access requires the owner or a live lease.</summary>
        /// <exception cref="ObjectDisposedException">The final owner/consumer has released the snapshot.</exception>
        public Graph Graph
        {
            get
            {
                lock (lifetimeLock)
                {
                    if (references == 0) throw new ObjectDisposedException("GenerationSnapshot");
                    return graph;
                }
            }
        }

        /// <summary>True after the final reference was released, including pending main-thread destruction.</summary>
        public bool IsReleased { get { lock (lifetimeLock) return references == 0; } }

        /// <summary>Captures copied node settings/topology, prepares asset data, then hashes the exact bake inputs.</summary>
        /// <param name="authoring">Live graph asset; null or unresolved nodes are rejected without altering it.</param>
        /// <param name="world">World owner for fresh scene inputs, ecology and context hashes; null copies explicitly pushed inputs.</param>
        /// <param name="requireOutput">UC-T01-09: true for whole-world dispatch; false permits explicit node sampling without an output root.</param>
        /// <returns>Snapshot with one owner reference; caller must release it even if dispatch fails.</returns>
        /// <exception cref="ArgumentException">Authoring graph is absent or contains unresolved slots.</exception>
        /// <exception cref="InvalidOperationException">Main-thread context is absent or graph validation rejects topology/domains.</exception>
        /// <remarks>Main thread once per bake; preparation failures destroy the temporary graph and propagate to the caller.</remarks>
        public static GenerationSnapshot Capture(Graph authoring, UniverseCreatorObject world = null, bool requireOutput = false)
            => CaptureForDomains(authoring, NodeExecutionDomain.Worker, world, requireOutput); // UC-T05-02: preserve the original worker-only API.

        /// <summary>Captures an immutable graph using the execution domains admitted by an explicit companion executor.</summary>
        /// <param name="authoring">Non-null authored graph; unsupported metadata, missing types and invalid topology are rejected.</param>
        /// <param name="allowedDomains">Nonzero combination of Worker, MainThread and MainThreadGpu requirements supported by the caller.</param>
        /// <param name="world">Owning world for captured inputs/ecology, or null for a standalone graph.</param>
        /// <param name="requireOutput">Whether at least one usable terminal output must be present.</param>
        /// <returns>Detached snapshot with one owner reference; release it after the last consumer.</returns>
        /// <exception cref="ArgumentException">The graph or admitted domain flags are invalid.</exception>
        /// <exception cref="InvalidOperationException">Called without Unity's main-thread context or validation fails.</exception>
        /// <remarks>UC-T05-02: allowing a main-thread node does not authorize dispatching it to a worker.</remarks>
        public static GenerationSnapshot CaptureForDomains(Graph authoring, NodeExecutionDomain allowedDomains,
            UniverseCreatorObject world = null, bool requireOutput = false)
        {
            const NodeExecutionDomain known = NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu;
            if (allowedDomains == 0 || (allowedDomains & ~known) != 0) throw new ArgumentException("Unsupported execution domain flags.", nameof(allowedDomains));
            SynchronizationContext context = SynchronizationContext.Current;
            if (context == null) throw new InvalidOperationException("Capture requires Unity's main-thread synchronization context.");
            if (authoring == null || authoring.nodes == null) throw new ArgumentException("A live graph is required.", "authoring");
            for (int i = 0; i < authoring.nodes.Count; i++)
                if (authoring.nodes[i] == null) throw new ArgumentException("Restore unresolved graph nodes before generation.", "authoring");

            // UC-T01-09: reject unsupported graph execution before native copying, preparation or worker admission.
            GraphValidationReport.Analyze(authoring, allowedDomains, requireOutput).ThrowIfInvalid(); // UC-T05-02: the same capture/validation path serves companions.

            GenerationSettings settings = world != null ? world.BuildSettings() : default(GenerationSettings);
            Graph copy = UnityEngine.Object.Instantiate(authoring);
            copy.hideFlags = HideFlags.HideAndDontSave;
            var profiles = new List<Geology.LithologyProfile>();
            var profileCopies = new Dictionary<Geology.LithologyProfile, Geology.LithologyProfile>();
            try
            {
                for (int i = 0; i < authoring.nodes.Count; i++)
                {
                    IGenerationSnapshotInputs inputs = authoring.nodes[i] as IGenerationSnapshotInputs;
                    if (inputs != null) inputs.CopyInputsTo(copy.nodes[i]);
                    // UC-T01-06: preserve shared-profile identity inside this run, never read the live profile on a worker.
                    ILithologySnapshotInput geology = copy.nodes[i] as ILithologySnapshotInput;
                    if (geology == null) continue;
                    if (geology.SourceProfile == null) { geology.BindCapturedProfile(null); continue; }
                    Geology.LithologyProfile prepared;
                    if (!profileCopies.TryGetValue(geology.SourceProfile, out prepared))
                    {
                        prepared = UnityEngine.Object.Instantiate(geology.SourceProfile);
                        profiles.Add(prepared);
                        prepared.hideFlags = HideFlags.HideAndDontSave;
                        profileCopies.Add(geology.SourceProfile, prepared);
                    }
                    geology.BindCapturedProfile(prepared);
                }
                if (world != null)
                {
                    Biome.UCBiomeRegion.PushToGraph(copy, world.transform);
                    UCWaterSystemManager.PrepareWorldInputs(copy, world);
                }
                for (int i = 0; i < copy.nodes.Count; i++)
                {
                    // UC-T07-08: function bodies share this capture's preparation, hashing and final-consumer lifecycle.
                    (copy.nodes[i] as IGenerationSnapshotDependencies)?.CaptureDependencies(world, allowedDomains);
                    copy.nodes[i].PrepareMainThread();
                }
                UCWaterSystemManager.BakeRiverProfilesInGraph(copy);
                // UC-T01-08: compile once for the fixed copied topology, before hashing and worker pulls.
                copy.compiledPlan = CompiledGraphPlan.Build(copy);
                Ecology.EcoScatterContext ecology = world != null ? EcoScatterContextBuilder.BuildFor(world) : null;
                ulong ecologyHash = HashEcology(ecology);
                // UC-T06-06: capture persistent namespaces once, even with ecology off; workers never read prototype assets.
                ScatterIdentitySnapshot scatterIdentity = null;
                foreach (Generator node in copy.nodes)
                    if (node is Scatter.ScatterNodeBase scatter)
                    {
                        if (scatterIdentity == null && world != null) scatterIdentity = ScatterIdentitySnapshot.Capture(world);
                        scatter.BindIdentity(scatterIdentity);
                    }
                for (int i = 0; i < copy.nodes.Count; i++)
                {
                    Generator node = copy.nodes[i];
                    if (node.Category == NodeCategory.Scatter || node is Selectors.EcologyNode)
                        node.preparedContextHash = ecologyHash;
                }
                GraphContentHasher.ComputeHashes(copy, world != null ? world.ComputeWorldFactsHash() : 0UL);
                return new GenerationSnapshot(copy, ecology, context, settings,
                    world != null ? world.GetChunkSize() : 0f, world != null ? world.GetChunkResolution() : 0, profiles);
            }
            catch
            {
                DestroyInputs(copy, profiles);
                throw;
            }
        }

        [Serializable]
        private sealed class PreparedSpecies
        {
            public Ecology.EcoEnvelope[] envelopes;
            public float[] weights;
            public int count;
        }

        [Serializable]
        private sealed class PreparedEcology
        {
            public Ecology.EcoSampleSettings settings;
            public PreparedSpecies trees, shrubs, details, objects;
        }

        private static PreparedSpecies Species(Ecology.SpeciesTable table)
        {
            if (table == null) return null; // UC-T06-09: an unpopulated category remains absent in the placement hash.
            // UC-T06-09: appearance-only envelope edits cannot invalidate candidate/placement products.
            // Physical climate, density, allometry, collision and resource settings remain in this hash.
            var placement = table.envelopes != null ? new Ecology.EcoEnvelope[table.envelopes.Length] : null;
            if (placement != null) for (int i = 0; i < placement.Length; i++) placement[i] = ScatterApplier.PlacementEnvelope(table.envelopes[i]);
            return new PreparedSpecies { envelopes = placement, weights = table.weights, count = table.count };
        }

        private static ulong HashEcology(Ecology.EcoScatterContext ecology)
        {
            if (ecology == null) return 1UL; // Absence must differ from an enabled context.
            var placementSettings = ecology.settings; placementSettings.seasonalPhenology = false; // UC-T06-09: this switch is read by appearance, not habitat/placement.
            string json = JsonUtility.ToJson(new PreparedEcology
            {
                settings = placementSettings,
                trees = Species(ecology.trees), shrubs = Species(ecology.shrubs),
                details = Species(ecology.details), objects = Species(ecology.objects)
            });
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < json.Length; i++) hash = PreparedRasterInputs.Mix(hash, json[i]);
            return hash == 0UL ? 1UL : hash;
        }

        /// <summary>Retains the snapshot for a worker, tile or other consumer; does not resurrect released data.</summary>
        /// <returns>Independent lease; disposing it twice is harmless.</returns>
        /// <exception cref="ObjectDisposedException">No live owner/consumer remains.</exception>
        public Lease Acquire()
        {
            lock (lifetimeLock)
            {
                if (references == 0) throw new ObjectDisposedException("GenerationSnapshot");
                references++;
                return new Lease(this);
            }
        }

        /// <summary>Releases the bake owner's reference once; live consumers remain valid after cancellation/replacement.</summary>
        public void Release()
        {
            lock (lifetimeLock)
            {
                if (ownerReleased) return;
                ownerReleased = true;
            }
            ReleaseReference();
        }

        private void ReleaseReference()
        {
            Graph retired = null;
            lock (lifetimeLock)
            {
                references--;
                if (references == 0) { retired = graph; graph = null; }
            }
            if (ReferenceEquals(retired, null)) return;
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId) DestroyInputs(retired, ownedProfiles);
            else mainContext.Post(_ => DestroyInputs(retired, ownedProfiles), null);
        }

        // UC-T01-06: native copied inputs share the graph's last-consumer lifetime, including failed preparation.
        private static void DestroyInputs(Graph retired, List<Geology.LithologyProfile> profiles)
        {
            foreach (Generator node in retired.nodes) (node as IGenerationSnapshotDependencies)?.ReleaseDependencies(); // UC-T07-08: includes failed captures.
            if (Application.isPlaying) UnityEngine.Object.Destroy(retired);
            else UnityEngine.Object.DestroyImmediate(retired);
            foreach (Geology.LithologyProfile profile in profiles)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(profile);
                else UnityEngine.Object.DestroyImmediate(profile);
            }
            profiles.Clear();
        }

        /// <summary>One independently disposable reference to copied generation inputs; safe to release from a worker.</summary>
        public sealed class Lease : IDisposable
        {
            private GenerationSnapshot snapshot;
            internal Lease(GenerationSnapshot snapshot) { this.snapshot = snapshot; }
            /// <summary>Retained snapshot; null after disposal.</summary>
            public GenerationSnapshot Snapshot { get { return snapshot; } }
            /// <summary>Releases this consumer once; native destruction runs on the captured main thread.</summary>
            public void Dispose()
            {
                GenerationSnapshot previous = Interlocked.Exchange(ref snapshot, null);
                if (previous != null) previous.ReleaseReference();
            }
        }
    }
}
