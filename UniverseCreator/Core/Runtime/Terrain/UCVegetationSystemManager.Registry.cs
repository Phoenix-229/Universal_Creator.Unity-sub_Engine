// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    UCVegetationSystemManager.Registry.cs
//  Purpose: V-58. The per-chunk instance registry the vegetation system owns, and
//           the only source for the counts V-50 and V-52 display.
//
//  WHY THE MANAGER OWNS IT AND NOT THE INSPECTOR
//  ---------------------------------------------
//  Two panels need these numbers - the per-species count in the prototype foldout
//  (V-50) and the diagnostics panel (V-52) - and a third consumer, the GPU
//  instancing owner id, already exists. Had each panel accumulated its own totals
//  they would have disagreed the moment one of them missed a stream-out, and a
//  diagnostics panel that disagrees with the world is worse than none. So there is
//  one registry, it lives with the system that owns the instances, and the panels
//  only read it.
//
//  MEASURED, NOT PREDICTED - AND THAT IS WHY RegisteredChunkCount EXISTS
//  --------------------------------------------------------------------
//  ResidentChunkCount (Resolve.cs:333) is a PREDICTION: chunkCountPerSide squared,
//  the authored grid, which is what the detail-memory ceiling has to be checked
//  against before a build. RegisteredChunkCount below is a MEASUREMENT: how many
//  chunks have actually registered instances right now. They answer different
//  questions and they are supposed to differ while a world streams in. Do not
//  "deduplicate" them into one - the difference between the two is itself the
//  useful signal, and collapsing them would hide a chunk that failed to apply.
//
//  MAIN THREAD ONLY, DELIBERATELY UNLOCKED
//  ---------------------------------------
//  Scatter solving happens on workers, but registration does not: every call site
//  is inside ScatterApplier.Apply and its release path, which touch TerrainData and
//  instantiate GameObjects and therefore already run on the main thread. Adding a
//  lock would buy nothing and cost a contended monitor on the streaming path, so
//  there is none. If a worker ever needs to register, that decision changes here
//  and nowhere else.
//
//  NO ENTRY OUTLIVES ITS CHUNK
//  ---------------------------
//  BeginChunkRegistration replaces a chunk's whole record rather than adding to it,
//  so a chunk re-applied after a recentre cannot double-count itself, and
//  ReleaseChunk subtracts exactly what that chunk contributed. Records are pooled
//  because chunks stream continuously and a per-chunk allocation per stream-in is
//  precisely the kind of steady GC churn a mid-range phone shows as a stutter.
//
//  STREAMING OUT IS A SUSPEND, NOT A RELEASE
//  -----------------------------------------
//  ChunkManager destroys nothing: a cell that leaves the streaming window is
//  deactivated with its TerrainData attached and its props still parented to it, so
//  returning to it costs no bake. So the two verbs here are not interchangeable.
//  SuspendChunk keeps the record and drops it out of the live totals, because a
//  deactivated cell still holds memory but draws nothing, collides with nothing and
//  batches nothing. ReleaseChunk forgets the record, and is only correct when the
//  chunk's instances are actually going away - a recycle onto another cell, or a
//  re-apply. Using release for a stream-out would throw away a tally that is still
//  true and force a re-scatter to get it back.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Which prototype list an instance was drawn from. This is the library slot,
    /// not the plant: EcoEnvelope.lifeForm says what a species IS, and this says
    /// which of the four authored lists holds its prototype. A shrub-shaped tree
    /// prototype is still a Tree here, because that is the list its index means.
    /// </summary>
    public enum UCScatterKind
    {
        Tree = 0,
        Shrub = 1,
        Detail = 2,
        Object = 3
    }

    public partial class UCVegetationSystemManager
    {
        /// <summary>Number of <see cref="UCScatterKind"/> members.</summary>
        private const int ScatterKindCount = 4;

        /// <summary>
        /// Bits reserved for the prototype index when a kind and an index are packed
        /// into one dictionary key. A million prototypes per list is far past any
        /// authored library, and packing avoids a second dictionary per kind.
        /// </summary>
        private const int SpeciesKeyIndexBits = 20;

        /// <summary>Largest prototype index the packed key can carry.</summary>
        private const int SpeciesKeyIndexMax = (1 << SpeciesKeyIndexBits) - 1;

        /// <summary>What one chunk contributed for one prototype.</summary>
        private struct SpeciesTally
        {
            public int key;
            public int count;
        }

        /// <summary>
        /// One chunk's contribution. Pooled and reused; every field is reset in
        /// <see cref="ClearRecord"/> rather than by allocating a fresh instance.
        /// </summary>
        private class ChunkRecord
        {
            public readonly int[] perKind = new int[ScatterKindCount];

            /// <summary>
            /// Instances this chunk placed in each landform zone (V-52). Sized by the
            /// shared zone count so adding a zone never leaves a record short. Kept per
            /// chunk, like perKind, so a suspend or release adjusts the world histogram
            /// by exactly what this chunk contributed.
            /// </summary>
            public readonly int[] perZone = new int[Ecology.LandformAssemblage.ZoneCount];

            public readonly List<SpeciesTally> species = new List<SpeciesTally>(16);
            public int colliders;
            public int gameObjects;
            public int instancedDraws;

            /// <summary>
            /// True while this chunk's cell is outside the streaming window. The record
            /// keeps its tallies - the instances are still in memory, still parented to
            /// a deactivated cell - but they are subtracted from the live totals,
            /// because a deactivated cell draws nothing, collides with nothing and
            /// submits no batches. See SuspendChunk on why this is not a release.
            /// </summary>
            public bool suspended;
        }

        // ---- live state ------------------------------------------------------

        private readonly Dictionary<int, ChunkRecord> chunkRecords = new Dictionary<int, ChunkRecord>(64);
        private readonly Stack<ChunkRecord> recordPool = new Stack<ChunkRecord>(16);

        private readonly int[] livePerKind = new int[ScatterKindCount];
        private readonly Dictionary<int, int> livePerSpecies = new Dictionary<int, int>(128);

        /// <summary>Live per-zone histogram, summed across the chunks in the window (V-52).</summary>
        private readonly int[] livePerZone = new int[Ecology.LandformAssemblage.ZoneCount];

        private int liveColliders;
        private int liveGameObjects;
        private int liveInstancedDraws;
        private float lastBuildMs;

        /// <summary>
        /// Graph products the last chunk to bake was missing, and whether any chunk has
        /// reported yet. Two fields rather than a nullable, because the distinction the
        /// panel needs is "complete" versus "unknown" and None means the first of those.
        /// Not serialized: a mask from a previous editor session describes a graph that
        /// may since have been rewired, and a stale clean bill of health is worse than
        /// an honest "not built yet".
        /// </summary>
        [System.NonSerialized] private Ecology.EnvironmentChannel lastMissingChannels;
        [System.NonSerialized] private bool bakeReported;

        /// <summary>
        /// How many records are currently suspended. Kept as a counter rather than
        /// counted on demand, because the diagnostics panel reads the live chunk count
        /// every repaint and walking the dictionary for it would scale with the grid.
        /// </summary>
        private int suspendedChunks;

        /// <summary>
        /// Bumped on every mutation. An inspector polls this instead of comparing
        /// every count it drew last frame, so a panel repaints when the world
        /// changes and stays quiet when it does not.
        /// </summary>
        private int registryVersion;

        // =====================================================================
        //  WRITING
        // =====================================================================

        /// <summary>
        /// Starts a chunk's registration, discarding whatever it registered before.
        ///
        /// Called before the first Register call for that chunk. Replacing rather
        /// than accumulating is what makes a re-apply safe: a recentred grid applies
        /// the same owner id again, and adding would count that chunk twice.
        ///
        /// The suspended flag SURVIVES this call, deliberately. A cell can be
        /// deactivated between the moment its heights were dispatched to a worker and
        /// the moment they land, so an apply is not proof that the cell is live.
        /// Clearing the flag here would add a deactivated cell's instances to totals
        /// that are supposed to describe what is drawing right now. Liveness is owned
        /// by SuspendChunk and ResumeChunk, which the streaming pass drives from the
        /// cell's actual active state, and by nothing else.
        /// </summary>
        public void BeginChunkRegistration(int owner)
        {
            ChunkRecord record;
            if (chunkRecords.TryGetValue(owner, out record))
            {
                bool wasSuspended = record.suspended;

                // Only a live record contributed to the totals, so only a live record
                // has anything to take back out.
                if (!wasSuspended) SubtractRecord(record);

                ClearRecord(record);
                record.suspended = wasSuspended;

                registryVersion++;
                return;
            }

            chunkRecords[owner] = TakeRecord();
            registryVersion++;
        }

        /// <summary>
        /// Adds instances of one prototype to a chunk's record.
        ///
        /// The kind is supplied by the caller rather than derived from the channel
        /// name, because ScatterApplier already knows which prototype list it is
        /// applying and a second channel-to-list mapping here would be one more
        /// place to fix when a channel is added.
        ///
        /// The record always accumulates; the LIVE totals only do so while the chunk
        /// is not suspended. That split is what lets a cell be applied while it sits
        /// outside the streaming window: the tally is kept so resuming it costs one
        /// addition instead of a re-scatter, and the world's totals stay a description
        /// of what is actually drawing.
        /// </summary>
        public void RegisterInstances(int owner, UCScatterKind kind, int prototypeIndex, int count)
        {
            if (count <= 0) return;
            if (prototypeIndex < 0 || prototypeIndex > SpeciesKeyIndexMax) return;

            ChunkRecord record = EnsureRecord(owner);

            int k = (int)kind;
            if (k < 0 || k >= ScatterKindCount) return;

            bool live = !record.suspended;

            record.perKind[k] += count;
            if (live) livePerKind[k] += count;

            int key = SpeciesKey(kind, prototypeIndex);

            // Folded into an existing tally when this prototype already appears in
            // this chunk, so a channel that emits a prototype in several batches
            // still leaves one entry to subtract on release.
            for (int i = 0; i < record.species.Count; i++)
            {
                if (record.species[i].key != key) continue;

                SpeciesTally merged = record.species[i];
                merged.count += count;
                record.species[i] = merged;

                if (live) AddSpeciesTotal(key, count);
                registryVersion++;
                return;
            }

            SpeciesTally tally;
            tally.key = key;
            tally.count = count;
            record.species.Add(tally);

            if (live) AddSpeciesTotal(key, count);
            registryVersion++;
        }

        /// <summary>
        /// Adds to what a chunk cost beyond its instance count: colliders, spawned
        /// GameObjects and instanced draw calls. These are the numbers a budget is
        /// actually spent in, which is why V-52 shows them beside the counts.
        ///
        /// ADDITIVE, like RegisterInstances, and for the same reason: a chunk's
        /// overheads arrive from more than one channel - shrubs and scattered objects
        /// each spawn GameObjects, each attach colliders, each submit instanced draws -
        /// and there is no point in the apply where one caller knows the whole total.
        /// Replacing here would let the object channel silently erase what the shrub
        /// channel reported. <see cref="BeginChunkRegistration"/> is what resets, and
        /// it is the only thing that does.
        /// </summary>
        public void RegisterChunkOverheads(int owner, int colliders, int gameObjects, int instancedDraws)
        {
            if (colliders == 0 && gameObjects == 0 && instancedDraws == 0) return;

            ChunkRecord record = EnsureRecord(owner);

            record.colliders += colliders;
            record.gameObjects += gameObjects;
            record.instancedDraws += instancedDraws;

            // Suspended, so the record keeps the figures and the world does not claim
            // them - see RegisterInstances.
            if (!record.suspended)
            {
                liveColliders += colliders;
                liveGameObjects += gameObjects;
                liveInstancedDraws += instancedDraws;
            }

            registryVersion++;
        }

        /// <summary>
        /// Adds this chunk's per-zone placement tally - the V-20 histogram V-52 shows.
        ///
        /// ADDITIVE and suspended-aware, like the counts above and for the same reason:
        /// each instance is classified into one landform zone during assignment, where
        /// the environment sample already exists, accumulated there into a single array,
        /// and reported here once per chunk. The array is indexed by (int)LandformZone;
        /// a short or long one is clamped rather than throwing, because a diagnostic must
        /// never be the thing that stops a build.
        /// </summary>
        public void RegisterZones(int owner, int[] zoneCounts)
        {
            if (zoneCounts == null) return;

            ChunkRecord record = EnsureRecord(owner);
            bool live = !record.suspended;

            int n = Mathf.Min(zoneCounts.Length, record.perZone.Length);
            bool any = false;

            for (int z = 0; z < n; z++)
            {
                int c = zoneCounts[z];
                if (c <= 0) continue;

                record.perZone[z] += c;
                if (live) livePerZone[z] += c;
                any = true;
            }

            if (any) registryVersion++;
        }

        /// <summary>
        /// Takes a chunk out of the live totals without forgetting what it holds.
        ///
        /// WHY SUSPEND EXISTS AND WHY IT IS NOT ReleaseChunk
        /// ------------------------------------------------
        /// ChunkManager never destroys a cell. A cell that leaves the streaming window
        /// is deactivated with its TerrainData still attached and its scattered props
        /// still parented to it, so that walking back into it re-enables a surface that
        /// is already correct - no bake, no allocation, no hitch (ChunkManager's
        /// recycling contract). Releasing the record would therefore be a lie in both
        /// directions: the record would be gone while the instances are still resident,
        /// and coming back would need a full re-scatter to rebuild a tally we already
        /// had.
        ///
        /// What a deactivated cell genuinely stops doing is DRAWING. Its trees are not
        /// submitted, its detail patches are not built, its collider is off and its
        /// props are inactive - so every figure V-52 shows is a figure it no longer
        /// spends. That is exactly the split here: the record keeps its tallies, the
        /// live totals lose them.
        ///
        /// Idempotent. The streaming pass calls this from the same choke point that
        /// deactivates the cell, which can run on a cell that is already parked.
        /// </summary>
        public void SuspendChunk(int owner)
        {
            ChunkRecord record;
            if (!chunkRecords.TryGetValue(owner, out record)) return;
            if (record.suspended) return;

            SubtractRecord(record);

            record.suspended = true;
            suspendedChunks++;

            registryVersion++;
        }

        /// <summary>
        /// Puts a suspended chunk's tallies back into the live totals. The other half
        /// of <see cref="SuspendChunk"/>, and idempotent for the same reason.
        /// </summary>
        public void ResumeChunk(int owner)
        {
            ChunkRecord record;
            if (!chunkRecords.TryGetValue(owner, out record)) return;
            if (!record.suspended) return;

            record.suspended = false;
            suspendedChunks = Mathf.Max(0, suspendedChunks - 1);

            AddRecord(record);

            registryVersion++;
        }

        /// <summary>
        /// Drops a chunk's whole contribution. Called when the chunk is recycled onto a
        /// different cell or its scatter is about to be re-applied, which is what keeps
        /// the totals a measurement of what is resident rather than of everything ever
        /// built.
        ///
        /// NOT the streaming path - that is <see cref="SuspendChunk"/>. A cell leaving
        /// the window keeps its instances, so releasing it would forget a tally that is
        /// still true.
        /// </summary>
        public void ReleaseChunk(int owner)
        {
            ChunkRecord record;
            if (!chunkRecords.TryGetValue(owner, out record)) return;

            // A suspended record already gave its figures back, so subtracting again
            // would drive the totals below what is actually standing.
            if (record.suspended) suspendedChunks = Mathf.Max(0, suspendedChunks - 1);
            else SubtractRecord(record);

            chunkRecords.Remove(owner);
            ReturnRecord(record);

            registryVersion++;
        }

        /// <summary>
        /// Empties the registry. For a full rebuild, where every chunk is discarded
        /// at once and releasing them one at a time would be slower and would leave
        /// the totals briefly wrong.
        /// </summary>
        public void ClearRegistry()
        {
            foreach (KeyValuePair<int, ChunkRecord> kvp in chunkRecords)
                ReturnRecord(kvp.Value);

            chunkRecords.Clear();
            livePerSpecies.Clear();

            for (int i = 0; i < ScatterKindCount; i++) livePerKind[i] = 0;
            for (int i = 0; i < livePerZone.Length; i++) livePerZone[i] = 0;

            liveColliders = 0;
            liveGameObjects = 0;
            liveInstancedDraws = 0;
            suspendedChunks = 0;

            // The bake report goes too. A rebuild is the moment the graph may have
            // been rewired, so the mask from the previous graph is no longer evidence
            // about this one - and lastBuildMs is deliberately kept, because it is the
            // figure an author compares the next build against.
            lastMissingChannels = Ecology.EnvironmentChannel.None;
            bakeReported = false;

            registryVersion++;
        }

        /// <summary>
        /// How long the last build took, in milliseconds. Reported by whoever timed
        /// it; the registry does not time anything itself, because the only honest
        /// figure is the one measured around the actual work.
        /// </summary>
        public void ReportBuildMilliseconds(float milliseconds)
        {
            lastBuildMs = Mathf.Max(0f, milliseconds);
            registryVersion++;
        }

        /// <summary>
        /// Records which graph products the last chunk to bake was missing (V-51).
        ///
        /// Reported rather than computed here, and reported from the apply rather than
        /// from the bake: the mask is a property of what the graph produced, the sampler
        /// already derives it in one place
        /// (<see cref="Ecology.EnvironmentSampler.MissingFrom"/>), and the apply is the
        /// first main-thread moment at which it is known. Computing a second opinion in
        /// the inspector would let the panel clear a graph the sampler treats as
        /// half-wired.
        ///
        /// LAST CHUNK, NOT THE UNION. Water is the reason: a cell with no lake in it
        /// legitimately has no water probes, so OR-ing every cell's mask together would
        /// report a missing water channel for every world that is not entirely coastline.
        /// The last cell to land is a live sample of what the graph is producing, which
        /// is what an author is looking at while they wire it.
        /// </summary>
        public void ReportMissingChannels(Ecology.EnvironmentChannel missing)
        {
            // First report is always news, even when the mask is None: the panel
            // reads "not built yet" until it arrives, so a complete graph has to be
            // able to flip that row too.
            if (bakeReported && lastMissingChannels == missing) return;

            lastMissingChannels = missing;
            bakeReported = true;
            registryVersion++;
        }

        /// <summary>
        /// Graph products the last baked chunk lacked, or None. <see cref="HasBakeReport"/>
        /// distinguishes None-because-complete from None-because-nothing-has-baked.
        /// </summary>
        public Ecology.EnvironmentChannel LastMissingChannels
        {
            get { return lastMissingChannels; }
        }

        /// <summary>True once any chunk has reported, so the panel can say "not built".</summary>
        public bool HasBakeReport { get { return bakeReported; } }

        // =====================================================================
        //  READING
        // =====================================================================

        /// <summary>Live tree instances across every registered chunk.</summary>
        public int RegisteredTreeCount { get { return livePerKind[(int)UCScatterKind.Tree]; } }

        /// <summary>Live shrub instances across every registered chunk.</summary>
        public int RegisteredShrubCount { get { return livePerKind[(int)UCScatterKind.Shrub]; } }

        /// <summary>Live detail instances across every registered chunk.</summary>
        public int RegisteredDetailCount { get { return livePerKind[(int)UCScatterKind.Detail]; } }

        /// <summary>Live scattered-object instances across every registered chunk.</summary>
        public int RegisteredObjectCount { get { return livePerKind[(int)UCScatterKind.Object]; } }

        /// <summary>Colliders the resident chunks are holding.</summary>
        public int RegisteredColliderCount { get { return liveColliders; } }

        /// <summary>GameObjects the resident chunks spawned.</summary>
        public int RegisteredGameObjectCount { get { return liveGameObjects; } }

        /// <summary>Instanced draws the resident chunks submitted.</summary>
        public int RegisteredInstancedDrawCount { get { return liveInstancedDraws; } }

        /// <summary>
        /// Chunks that have actually registered instances, parked ones included.
        /// Compare against ResidentChunkCount, which is the authored prediction - see
        /// the header.
        /// </summary>
        public int RegisteredChunkCount { get { return chunkRecords.Count; } }

        /// <summary>
        /// Registered chunks that are inside the streaming window right now, which is
        /// the figure the other live totals were accumulated from. In edit mode the
        /// whole grid is active and this equals RegisteredChunkCount; in play mode the
        /// gap between them is the parked ring, and that gap is the useful reading -
        /// memory is being held for those cells, frame time is not.
        /// </summary>
        public int LiveChunkCount
        {
            get { return Mathf.Max(0, chunkRecords.Count - suspendedChunks); }
        }

        /// <summary>Registered chunks currently outside the streaming window.</summary>
        public int SuspendedChunkCount { get { return suspendedChunks; } }

        /// <summary>Milliseconds the last reported build took, or 0 if none has been.</summary>
        public float LastBuildMilliseconds { get { return lastBuildMs; } }

        /// <summary>Changes whenever any count does. Cheap to poll from a repaint.</summary>
        public int RegistryVersion { get { return registryVersion; } }

        /// <summary>
        /// Live instances of one prototype, across every chunk. The per-species
        /// number V-50's foldout shows, so an author can see that the species they
        /// are looking at placed nothing.
        /// </summary>
        public int InstanceCountFor(UCScatterKind kind, int prototypeIndex)
        {
            if (prototypeIndex < 0 || prototypeIndex > SpeciesKeyIndexMax) return 0;

            int total;
            return livePerSpecies.TryGetValue(SpeciesKey(kind, prototypeIndex), out total) ? total : 0;
        }

        /// <summary>
        /// Copies the live per-zone histogram into the caller's buffer, indexed by
        /// (int)LandformZone, and returns how many entries were written.
        ///
        /// A caller buffer rather than a returned array because the diagnostics panel
        /// reads this every repaint, and handing back a fresh array each time would be
        /// steady editor garbage for a number that rarely changes. The internal array
        /// is never exposed, so nothing outside the registry can drive a zone total
        /// negative behind the suspend bookkeeping.
        /// </summary>
        public int CopyZoneHistogram(int[] into)
        {
            if (into == null) return 0;

            int n = Mathf.Min(into.Length, livePerZone.Length);
            for (int i = 0; i < n; i++) into[i] = livePerZone[i];
            return n;
        }

        // =====================================================================
        //  INTERNALS
        // =====================================================================

        private static int SpeciesKey(UCScatterKind kind, int prototypeIndex)
        {
            return ((int)kind << SpeciesKeyIndexBits) | prototypeIndex;
        }

        private ChunkRecord EnsureRecord(int owner)
        {
            ChunkRecord record;
            if (chunkRecords.TryGetValue(owner, out record)) return record;

            record = TakeRecord();
            chunkRecords[owner] = record;
            return record;
        }

        private void AddSpeciesTotal(int key, int delta)
        {
            int total;
            livePerSpecies.TryGetValue(key, out total);

            total += delta;

            // Removed rather than left at zero, so the dictionary does not grow a
            // permanent entry for every prototype any chunk ever placed.
            if (total <= 0) livePerSpecies.Remove(key);
            else livePerSpecies[key] = total;
        }

        private void SubtractRecord(ChunkRecord record)
        {
            for (int i = 0; i < ScatterKindCount; i++)
                livePerKind[i] = Mathf.Max(0, livePerKind[i] - record.perKind[i]);

            for (int i = 0; i < livePerZone.Length; i++)
                livePerZone[i] = Mathf.Max(0, livePerZone[i] - record.perZone[i]);

            for (int i = 0; i < record.species.Count; i++)
                AddSpeciesTotal(record.species[i].key, -record.species[i].count);

            liveColliders = Mathf.Max(0, liveColliders - record.colliders);
            liveGameObjects = Mathf.Max(0, liveGameObjects - record.gameObjects);
            liveInstancedDraws = Mathf.Max(0, liveInstancedDraws - record.instancedDraws);
        }

        /// <summary>
        /// The exact inverse of <see cref="SubtractRecord"/>, for a chunk coming back
        /// into the window. No clamp on the way up: a clamp here would hide a mismatch
        /// between the two rather than prevent one, and there is nothing to clamp
        /// against - the record is the authority for what it contributed.
        /// </summary>
        private void AddRecord(ChunkRecord record)
        {
            for (int i = 0; i < ScatterKindCount; i++)
                livePerKind[i] += record.perKind[i];

            for (int i = 0; i < livePerZone.Length; i++)
                livePerZone[i] += record.perZone[i];

            for (int i = 0; i < record.species.Count; i++)
                AddSpeciesTotal(record.species[i].key, record.species[i].count);

            liveColliders += record.colliders;
            liveGameObjects += record.gameObjects;
            liveInstancedDraws += record.instancedDraws;
        }

        private static void ClearRecord(ChunkRecord record)
        {
            for (int i = 0; i < ScatterKindCount; i++) record.perKind[i] = 0;
            for (int i = 0; i < record.perZone.Length; i++) record.perZone[i] = 0;

            // Cleared rather than replaced, so the list keeps its capacity for the
            // next chunk that reuses this record.
            record.species.Clear();

            record.colliders = 0;
            record.gameObjects = 0;
            record.instancedDraws = 0;

            // Pooled records outlive the chunk they described. A stale suspended flag
            // would make the next chunk to take this record register into nothing.
            record.suspended = false;
        }

        private ChunkRecord TakeRecord()
        {
            if (recordPool.Count == 0) return new ChunkRecord();

            ChunkRecord record = recordPool.Pop();
            ClearRecord(record);
            return record;
        }

        private void ReturnRecord(ChunkRecord record)
        {
            if (record == null) return;

            ClearRecord(record);

            // Bounded, so a world that streamed a thousand chunks does not keep a
            // thousand empty records alive for a grid that only ever holds dozens.
            if (recordPool.Count < 64) recordPool.Push(record);
        }
    }
}
