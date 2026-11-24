// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    ScatterRemovalStore.cs
//  Purpose: PERF-2's persistent delta. Remembers which scattered instances have
//           been taken out of the world - harvested, chopped, mined, erased by a
//           brush - so they stay gone when their chunk unloads and streams back
//           in, and across a save and reload.
//
//  WHY A SPARSE DELTA AND NOT A LIST OF WHAT SURVIVES
//  --------------------------------------------------
//  A world holds millions of instances and a player removes a handful. Storing
//  the survivors would mean writing the whole world to disk to record that one
//  bush is gone, and it would also freeze the world: regenerate with a different
//  seed and a saved survivor list describes instances that no longer exist. A
//  delta of removals is proportional to what the player actually did, and it
//  composes with a regenerated world instead of fighting it.
//
//  WHY IT KEYS ON instanceId AND NOT ON AN ARRAY INDEX
//  ---------------------------------------------------
//  An index is not an identity. Scatter emits instances in whatever order the
//  solve produced them, budgets drop the worst-sited ones first, and a chunk is
//  rebuilt whenever anything upstream changes - so index 400 is a different
//  plant after almost any edit, and a saved "index 400 removed" would delete
//  something the player never touched. VEG-13 already solved this:
//  ScatterInstance.instanceId is derived from the instance's quantised world
//  position, so it is the same value across re-bakes and survives reordering,
//  relocation and budget drops.
//
//  ZERO IS NOT AN ID
//  -----------------
//  instanceId 0 means "the scatter pass never stamped this one" - no identity.
//  If 0 could enter the store, every unstamped instance in the world would
//  vanish at once. Remove(0) is therefore refused outright rather than stored
//  and filtered later, because a store that cannot hold the dangerous value can
//  never leak it.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// UC-T06-06: persistent removed instances, stored as full versioned keys with
    /// an unchanged legacy int list for explicit migration. Runtime hash sets use
    /// full equality; lookup-hash collisions cannot identify another plant.
    /// </summary>
    /// <remarks>Mutate on the owning thread between application passes; serialized legacy/future data is never discarded by a keyed migration.</remarks>
    [System.Serializable]
    public partial class ScatterRemovalStore // UC-T06-18: sparse manual intent shares this existing persistence owner.
    {
        // UC-T06-06: retain full tuples in serialized storage; HashSet resolves lookup-hash collisions by full equality.
        [SerializeField, HideInInspector] private List<VegetationInstanceKey> removedKeys = new List<VegetationInstanceKey>();
        [System.NonSerialized] private HashSet<VegetationInstanceKey> keyIndex;
        // The serialized half. A List and not a HashSet because Unity serializes
        // lists and does not serialize hash sets - the set below is rebuilt from it.
        [SerializeField, HideInInspector]
        private List<int> removedIds = new List<int>();

        // The runtime half. Not serialized, rebuilt lazily, and deliberately
        // allowed to be null: that is how a freshly deserialized store knows it
        // has not indexed its list yet.
        [System.NonSerialized] private HashSet<int> index;

        /// <summary>How many instances this world has had removed.</summary>
        public int Count { get { return (removedIds != null ? removedIds.Count : 0) + (removedKeys != null ? removedKeys.Count : 0); } } // UC-T06-06

        /// <summary>True when nothing has been removed - the common case, and the
        /// one the applier short-circuits on so an untouched world pays nothing.</summary>
        public bool IsEmpty { get { return Count == 0; } }

        /// <summary>
        /// Marks one instance as gone. Returns false when nothing changed: an id of
        /// 0 (no identity - see the header) or an instance already removed.
        /// </summary>
        public bool Remove(int instanceId)
        {
            if (instanceId == 0) return false;

            EnsureIndex();
            if (!index.Add(instanceId)) return false;

            removedIds.Add(instanceId);
            return true;
        }

        /// <summary>
        /// Puts one instance back - an undo, or a world event that regrows it.
        /// Returns false when that id was not removed in the first place.
        /// </summary>
        public bool Restore(int instanceId)
        {
            if (instanceId == 0) return false;

            EnsureIndex();
            if (!index.Remove(instanceId)) return false;

            // Swap-and-pop: order carries no meaning here, so there is no reason to
            // shift the tail of a list that may hold thousands of entries.
            int last = removedIds.Count - 1;
            for (int i = 0; i <= last; i++)
            {
                if (removedIds[i] != instanceId) continue;

                removedIds[i] = removedIds[last];
                removedIds.RemoveAt(last);
                break;
            }
            return true;
        }

        /// <summary>
        /// Whether this instance should be skipped when its chunk builds. The hot
        /// path: called once per instance per chunk build, so it is a set lookup and
        /// it answers false immediately for an unstamped instance and for a world
        /// where nothing has been removed.
        /// </summary>
        public bool IsRemoved(int instanceId)
        {
            if (instanceId == 0) return false;
            if (IsEmpty) return false;

            EnsureIndex();
            return index.Contains(instanceId);
        }

        /// <summary>UC-T06-06: records removal of an exact full key; unsupported/unassigned keys are rejected.</summary>
        /// <param name="key">Captured version 1 key for the intended instance.</param>
        /// <returns>True when a removal was added; false for an already removed key.</returns>
        /// <exception cref="System.ArgumentException">The key is unsupported or unassigned.</exception>
        /// <remarks>Owner thread only; do not mutate the store while an application pass reads it.</remarks>
        public bool Remove(VegetationInstanceKey key)
        {
            if (!key.IsAssigned) throw new System.ArgumentException("A supported full instance identity is required.", nameof(key));
            EnsureKeyIndex();
            if (!keyIndex.Add(key)) return false;
            removedKeys.Add(key); return true;
        }

        /// <summary>UC-T06-06: restores an exact keyed removal without changing any unresolved legacy edit.</summary>
        /// <param name="key">Full key to restore; unassigned keys return false.</param>
        /// <returns>True when the keyed removal existed and was removed.</returns>
        public bool Restore(VegetationInstanceKey key)
        {
            if (!key.IsAssigned) return false;
            EnsureKeyIndex();
            if (!keyIndex.Remove(key)) return false;
            removedKeys.Remove(key); return true;
        }

        /// <summary>UC-T06-06: tests full identity, retaining the old integer path for unstamped legacy instances.</summary>
        /// <param name="instance">Immutable placed instance with its original legacy ID and optional full key.</param>
        /// <returns>True only for an exact keyed match, or an unstamped legacy ID match.</returns>
        /// <exception cref="System.InvalidOperationException">A matching legacy removal has not been explicitly migrated, or persisted keyed data has an unsupported schema.</exception>
        /// <remarks>A keyed instance cannot safely inherit an ambiguous 32-bit removal. Resolve/migrate it before applying this placement; saved edits are retained.</remarks>
        public bool IsRemoved(UniverseCreator.Nodes.ScatterInstance instance)
        {
            if (IsEmpty) return false;
            if (instance.identity.Version != 0 && !instance.identity.IsAssigned)
                throw new System.InvalidOperationException("Unsupported placed-instance identity schema. Resolve its migration before applying persistent removals."); // UC-T06-06
            if (!instance.identity.IsAssigned) return IsRemoved(instance.instanceId);
            EnsureKeyIndex();
            if (keyIndex.Contains(instance.identity)) return true;
            if (IsRemoved(instance.instanceId))
                throw new System.InvalidOperationException("A legacy scatter removal matches this keyed candidate. Explicitly migrate or select the intended instance before applying placement; the saved removal was retained.");
            return false;
        }

        /// <summary>UC-T06-06: explicitly resolves one legacy edit to the selected intended full key.</summary>
        /// <param name="legacyId">Nonzero ID in this store's original removedIds list.</param>
        /// <param name="selectedKey">Supported key selected from the original placement; selecting among ambiguous matches is an author decision.</param>
        /// <returns>True when the legacy entry was replaced by the exact key; false when that legacy edit is absent.</returns>
        /// <exception cref="System.ArgumentException">The selected key is unsupported/unassigned.</exception>
        public bool ResolveLegacyRemoval(int legacyId, VegetationInstanceKey selectedKey)
        {
            if (!selectedKey.IsAssigned) throw new System.ArgumentException("Select a supported full instance identity.", nameof(selectedKey));
            if (!IsRemoved(legacyId)) return false;
            Remove(selectedKey); Restore(legacyId); return true;
        }

        /// <summary>UC-T06-06: migrates unique legacy matches from a complete original placement; ambiguous/unmatched edits remain unchanged.</summary>
        /// <param name="originalPlacement">Complete original candidate set for this world, already stamped with full keys; at most 1048576 records.</param>
        /// <param name="worldId">Nonempty persistent world namespace shared by all records.</param>
        /// <param name="completeCoverage">True only when all original placement regions/layers/prototypes are present. Partial residency cannot prove a unique match.</param>
        /// <returns>Migration counts. Invalid matched identities prevent every migration; no legacy removal is discarded.</returns>
        /// <exception cref="System.ArgumentException">Placement/world is absent, coverage is incomplete, size exceeds the work limit, or a keyed record belongs to another world.</exception>
        /// <remarks>Explicit owner-thread operation over immutable evidence; run before applying keyed placement. Quality-thinned/current-resident subsets are insufficient evidence.</remarks>
        public ScatterRemovalMigrationReport MigrateLegacyRemovals(IReadOnlyList<UniverseCreator.Nodes.ScatterInstance> originalPlacement, string worldId, bool completeCoverage)
        {
            if (originalPlacement == null || string.IsNullOrEmpty(worldId) || !completeCoverage || originalPlacement.Count > 1048576)
                throw new System.ArgumentException("Legacy migration needs complete original placement, a persistent world ID and at most 1048576 records.");
            EnsureIndex(); EnsureKeyIndex();
            var matches = new Dictionary<int, VegetationInstanceKey>();
            var ambiguous = new HashSet<int>();
            int invalid = 0;
            foreach (var instance in originalPlacement)
            {
                if (instance.identity.IsAssigned && !string.Equals(instance.identity.World, worldId, System.StringComparison.Ordinal))
                    throw new System.ArgumentException("Legacy migration cannot combine placement from different worlds.");
                if (!index.Contains(instance.instanceId)) continue;
                if (!instance.identity.IsAssigned) { invalid++; continue; }
                if (matches.TryGetValue(instance.instanceId, out var previous) && !previous.Equals(instance.identity)) ambiguous.Add(instance.instanceId);
                else matches[instance.instanceId] = instance.identity;
            }
            int unmatched = index.Count - matches.Count;
            if (invalid > 0) return new ScatterRemovalMigrationReport(0, ambiguous.Count, unmatched, invalid);
            int migrated = 0;
            foreach (var match in matches)
                if (!ambiguous.Contains(match.Key) && ResolveLegacyRemoval(match.Key, match.Value)) migrated++;
            return new ScatterRemovalMigrationReport(migrated, ambiguous.Count, unmatched, 0);
        }

        /// <summary>Forgets every removal, so the world regrows whole.</summary>
        public void Clear()
        {
            if (removedIds != null) removedIds.Clear();
            if (index != null) index.Clear();
            if (removedKeys != null) removedKeys.Clear(); // UC-T06-06: explicit reset covers both schemas.
            if (keyIndex != null) keyIndex.Clear();
        }

        private void EnsureKeyIndex()
        {
            if (keyIndex != null) return;
            if (removedKeys == null) removedKeys = new List<VegetationInstanceKey>();
            var built = new HashSet<VegetationInstanceKey>();
            foreach (var key in removedKeys)
            {
                if (!key.IsAssigned) throw new System.InvalidOperationException("Unsupported or invalid persisted scatter removal key. Data was retained; restore its supported schema before applying placement.");
                built.Add(key);
            }
            keyIndex = built;
        }

        /// <summary>
        /// Builds the lookup set from the serialized list, once. Also the repair
        /// point: a hand-edited or merged scene file can carry duplicates and
        /// zeroes in the list, and both are dropped here rather than being trusted
        /// into the hot path.
        /// </summary>
        private void EnsureIndex()
        {
            if (removedIds == null) removedIds = new List<int>();
            if (index != null) return;

            index = new HashSet<int>();

            int write = 0;
            for (int i = 0; i < removedIds.Count; i++)
            {
                int id = removedIds[i];
                if (id == 0) continue;            // never an identity
                if (!index.Add(id)) continue;     // duplicate from a merge

                removedIds[write] = id;
                write++;
            }

            if (write < removedIds.Count)
                removedIds.RemoveRange(write, removedIds.Count - write);
        }
    }

    /// <summary>UC-T06-06: evidence from one explicit legacy-removal migration; ambiguous/unmatched edits remain serialized.</summary>
    public readonly struct ScatterRemovalMigrationReport
    {
        /// <summary>Legacy edits replaced by unique full keys.</summary>
        public readonly int Migrated;
        /// <summary>Legacy IDs matching different intended full keys; explicit selection is required.</summary>
        public readonly int Ambiguous;
        /// <summary>Legacy IDs without a matching keyed record in the supplied original placement.</summary>
        public readonly int Unmatched;
        /// <summary>Matched records lacking supported identity; a nonzero count prevents all migration.</summary>
        public readonly int Invalid;
        internal ScatterRemovalMigrationReport(int migrated, int ambiguous, int unmatched, int invalid)
        { Migrated = migrated; Ambiguous = ambiguous; Unmatched = unmatched; Invalid = invalid; }
    }
}
