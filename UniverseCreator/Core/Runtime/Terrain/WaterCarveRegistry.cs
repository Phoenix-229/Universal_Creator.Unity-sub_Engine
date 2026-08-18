// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    WaterCarveRegistry.cs
//  Purpose: The list of digs a world must honour while its chunks are being built.
//
//  WHY A REGISTRY AND NOT A LOOKUP
//  -------------------------------
//  ApplyOutputs runs once per chunk, and a world being regenerated applies hundreds
//  of them. Finding the water bodies with GetComponentsInChildren on each would walk
//  the whole hierarchy hundreds of times to get the same answer. Bodies register
//  themselves when they are enabled and drop out when they are not, so the apply
//  path reads a list that is already correct.
//
//  It is keyed by world, because two Universe Creator objects in one scene must not
//  dig each other's terrain.
//
//  THREADING
//  ---------
//  Registration happens on the main thread from OnEnable/OnDisable, and application
//  happens on the main thread from ApplyChunk. Generation itself is off-thread, but
//  it never touches this - the dig is applied after the worker returns, on the
//  staged heightfield, which is why the carve needs no locking at all.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Anything that can dig terrain. Implemented by UCWaterBody today; a quarry or
    /// a crater node could implement it tomorrow without this file changing.
    /// </summary>
    public interface IUCTerrainCarver
    {
        /// <summary>The world whose chunks this carver may dig, or null for none.</summary>
        UniverseCreatorObject CarveWorld { get; }

        /// <summary>The dig itself, or null when this carver is switched off.</summary>
        WaterCarveLayer CarveLayer { get; }
    }

    /// <summary>
    /// Per-world list of active carvers, consulted as each chunk's heights are
    /// written.
    /// </summary>
    public static class WaterCarveRegistry
    {
        private static readonly Dictionary<UniverseCreatorObject, List<IUCTerrainCarver>> byWorld =
            new Dictionary<UniverseCreatorObject, List<IUCTerrainCarver>>();

        /// <summary>Bumped whenever the set changes, so an inspector can tell that a
        /// rebuild is owed without comparing lists.</summary>
        public static int Version { get; private set; }

        public static void Register(IUCTerrainCarver carver)
        {
            if (carver == null) return;

            UniverseCreatorObject world = carver.CarveWorld;
            if (world == null) return;

            List<IUCTerrainCarver> list;
            if (!byWorld.TryGetValue(world, out list))
            {
                list = new List<IUCTerrainCarver>();
                byWorld[world] = list;
            }

            if (!list.Contains(carver))
            {
                list.Add(carver);
                Version++;
            }
        }

        public static void Unregister(IUCTerrainCarver carver)
        {
            if (carver == null) return;

            // The world reference may already be gone if the whole hierarchy is being
            // torn down, so every list is searched rather than just the one this
            // carver claims. A leftover entry would dig terrain for an object that no
            // longer exists.
            foreach (KeyValuePair<UniverseCreatorObject, List<IUCTerrainCarver>> pair in byWorld)
            {
                if (pair.Value.Remove(carver)) Version++;
            }
        }

        /// <summary>Live carvers for a world, never null.</summary>
        public static List<IUCTerrainCarver> For(UniverseCreatorObject world)
        {
            List<IUCTerrainCarver> list;
            if (world != null && byWorld.TryGetValue(world, out list)) return list;

            return Empty;
        }

        private static readonly List<IUCTerrainCarver> Empty = new List<IUCTerrainCarver>();

        /// <summary>
        /// Number of digs that would touch a chunk. Used by the inspector to say
        /// whether a body is actually reaching the terrain, which is the first thing
        /// to check when a river looks like it did nothing.
        /// </summary>
        public static int CountAffecting(UniverseCreatorObject world, Bounds chunkWorldBounds)
        {
            List<IUCTerrainCarver> list = For(world);

            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                IUCTerrainCarver c = list[i];
                if (c == null) continue;

                WaterCarveLayer layer = c.CarveLayer;
                if (layer != null && layer.AffectsRegion(chunkWorldBounds)) n++;
            }

            return n;
        }

        /// <summary>
        /// Folds every registered dig into a staged heightfield. Returns how many
        /// actually touched it.
        ///
        /// Order within the list does not matter for separate bodies, because each
        /// dig only ever lowers ground and Min is commutative. Two bodies that
        /// overlap therefore produce the deeper of the two, which is what a river
        /// running into a lake should look like.
        /// </summary>
        public static int ApplyAll(UniverseCreatorObject world, float[,] heights,
                                   Bounds chunkWorldBounds, float metresPerSample)
        {
            if (world == null || heights == null || metresPerSample <= 0f) return 0;

            List<IUCTerrainCarver> list = For(world);
            if (list.Count == 0) return 0;

            float maxHeight = world.maxHeightMeters;
            if (maxHeight <= 0f) return 0;

            int applied = 0;
            for (int i = 0; i < list.Count; i++)
            {
                IUCTerrainCarver c = list[i];
                if (c == null) continue;

                WaterCarveLayer layer = c.CarveLayer;
                if (layer == null || !layer.enabled || !layer.HasShape) continue;
                if (!layer.AffectsRegion(chunkWorldBounds)) continue;

                layer.ApplyTo(heights, chunkWorldBounds, metresPerSample, maxHeight);
                applied++;
            }

            return applied;
        }

        /// <summary>Drops every entry. Called when a scene closes so a stale carver
        /// from an unloaded scene cannot dig a world in the next one.</summary>
        public static void Clear()
        {
            if (byWorld.Count == 0) return;

            byWorld.Clear();
            Version++;
        }
    }
}
