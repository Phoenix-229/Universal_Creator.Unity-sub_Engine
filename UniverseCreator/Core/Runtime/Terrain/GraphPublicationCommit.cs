using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    // UC-T02-07: reserve publication ownership before native changes; transfer it only after a current-run commit.
    internal static class GraphPublicationCommit
    {
        internal static ProductMemoryBudget.Lease Prepare(UniverseCreatorObject world, OutputBag outputs)
        {
            if (world == null || outputs == null || outputs.channelPublications == null || !outputs.RunId.IsAssigned) return null;
            // UC-T03-06: retained topology must belong to the world accepting the candidate terrain.
            if (!OwnsOutletRecords(outputs)) throw new System.InvalidOperationException("River outlet metadata belongs to another world.");
            // UC-T03-09: reserve the immutable final-ground copy before the native commit; retained candidates stay pinned too.
            return outputs.channelPublications.Length == 0 && outputs.heightmap == null ? null : PrepareGround(world,
                outputs.channelPublications, outputs.heightmap?.LongLength ?? 0, outputs.trenchRelief, outputs.biomeClassification);
        }

        /// <summary>UC repair 30: admits copied cell ground and identity-shared immutable channels before native publication or refresh.</summary>
        /// <param name="world">Owning world and its unchanged hard payload budget.</param>
        /// <param name="channels">Borrowed immutable channel records; never copied or mutated here.</param>
        /// <param name="samples">Nonnegative number of float ground samples; includes a conservative one-byte hole allowance per sample.</param>
        /// <param name="relief">Optional normalized trench field copied by the snapshot.</param>
        /// <param name="biomes">Optional categorical field copied by the snapshot.</param>
        /// <returns>Cell reservation owning independent channel pins, released together on rollback or retirement.</returns>
        /// <exception cref="System.InvalidOperationException">Private or shared storage cannot fit; all provisional pins retire.</exception>
        internal static ProductMemoryBudget.Lease PrepareGround(UniverseCreatorObject world, ChannelGraphPublication[] channels,
            long samples, float[,] relief, Matrix biomes)
        {
            var memory = world.ProductMemory.Reserve(4096L + (channels?.LongLength ?? 0) * System.IntPtr.Size +
                samples * (sizeof(float) + sizeof(bool)) + (relief?.LongLength ?? 0) * sizeof(float) + ProductMemoryFootprint.Estimate(biomes));
            try
            {
                if (channels != null) foreach (ChannelGraphPublication channel in channels)
                {
                    var pin = world.ProductMemory.Acquire(channel, ProductMemoryFootprint.Estimate(channel), 0, channel.Key);
                    try { memory.Own(pin); }
                    catch { pin.Dispose(); throw; }
                }
                return memory;
            }
            catch { memory.Dispose(); throw; }
        }

        internal static bool Commit(UniverseCreatorObject world, OutputBag outputs, ProductMemoryBudget.Lease memory, UnityEngine.TerrainData native = null)
        {
            if (world == null || outputs == null || outputs.channelPublications == null || !outputs.RunId.IsAssigned)
            { memory?.Dispose(); return true; }
            if (!OwnsOutletRecords(outputs)) { memory?.Dispose(); return false; } // UC-T03-06: defend direct commit callers too.
            if (!world.GenerationScheduler.IsCurrent(outputs.RunId)) { memory?.Dispose(); return false; } // UC-T03-09: no clone for an already rejected run.
            GraphExecutionGuard.RequireMainThread("Accepted ground capture"); // UC-T03-10: GetHoles belongs to the native commit, never a worker.
            bool[,] surface = outputs.heightmap == null ? null : native != null ? native.GetHoles(0, 0, native.holesResolution, native.holesResolution) :
                outputs.holes == null ? null : (bool[,])outputs.holes.Clone();
            // UC-T03-09: this runs after water/path/sculpt/guard and successful uploads, never inside a generator.
            var snapshot = new RiverChannelSnapshot(outputs.RunId, outputs.publicationOwner, outputs.publicationBounds,
                outputs.channelPublications, outputs.heightmap, outputs.publicationOriginY, outputs.publicationHeight, surface, outputs.trenchRelief,
                outputs.biomeClassification); // UC-T07-03: accepted categorical fields share the exact ground publication lifetime.
            return RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, outputs.RunId, outputs.publicationOwner,
                outputs.publicationBounds, outputs.channelPublications, memory, snapshot, world); // UC-T03-11: scene unload also retires never-active world publications.
        }

        private static bool OwnsOutletRecords(OutputBag outputs)
        {
            foreach (ChannelGraphPublication publication in outputs.channelPublications)
            {
                if (publication.Outlets != null && publication.Outlets.WorldId != outputs.RunId.WorldId) return false;
                if (publication.LakeOutlets != null && publication.LakeOutlets.WorldId != outputs.RunId.WorldId) return false; // UC-T03-07.
            }
            return true;
        }
    }
}
