// UC-T06-12: diagnostic tallies are staged with native resources and published once.
using System;
using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCVegetationSystemManager
    {
        private readonly Dictionary<int, RegistrationReplacement> registrationReplacements = new Dictionary<int, RegistrationReplacement>();

        internal sealed class RegistrationReplacement : IDisposable
        {
            private readonly UCVegetationSystemManager manager;
            private readonly int owner;
            internal readonly ChunkRecord record;
            private bool finished;

            internal RegistrationReplacement(UCVegetationSystemManager manager, int owner)
            {
                this.manager = manager; this.owner = owner;
                record = manager.TakeRecord(); record.suspended = true;
            }

            internal void PrepareCommit()
            {
                if (finished) throw new InvalidOperationException("Vegetation registration replacement already retired.");
                manager.chunkRecords.EnsureCapacity(manager.chunkRecords.Count + 1);
                manager.livePerSpecies.EnsureCapacity(manager.livePerSpecies.Count + record.species.Count);
            }

            internal void Commit()
            {
                bool live = !manager.chunkRecords.TryGetValue(owner, out var old) || !old.suspended;
                manager.ReleaseChunk(owner);
                record.suspended = !live; manager.chunkRecords.Add(owner, record);
                if (live) manager.AddRecord(record); else manager.suspendedChunks++;
                manager.registryVersion++; manager.registrationReplacements.Remove(owner); finished = true;
            }

            /// <summary>Returns the unpublished tally to its pool without changing accepted live totals.</summary>
            public void Dispose()
            {
                if (finished) return;
                manager.registrationReplacements.Remove(owner); manager.ReturnRecord(record); finished = true;
            }
        }

        internal RegistrationReplacement BeginRegistrationReplacement(int owner)
        {
            if (registrationReplacements.ContainsKey(owner)) throw new InvalidOperationException("Vegetation owner already has a staged replacement.");
            var replacement = new RegistrationReplacement(this, owner);
            registrationReplacements.Add(owner, replacement); return replacement;
        }
    }
}
