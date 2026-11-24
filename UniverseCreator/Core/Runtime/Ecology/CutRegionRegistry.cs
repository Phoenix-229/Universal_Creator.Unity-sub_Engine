// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    CutRegionRegistry.cs
//  Purpose: Records the volumes the road system has cut, so vegetation can be kept
//           out of them (V-23). A terrain-shaping plugin's cut (an IUCTerrainShapeProvider
//           CutOpening, or any system that carves) records the volume here so the next
//           scatter bake keeps vegetation off it.
//
//  WHY A SHARED REGISTRY AND NOT A REFERENCE. A terrain-shaping system records a cut
//  here without holding a reference to the vegetation
//  system, and it should not have to hold one - a road does not depend on there
//  being plants. A shared registry is the honest shape for a fact about the world
//  that one system reports and another consumes.
//
//  WHY THE READ SIDE IS A FLAT ARRAY AND THE WRITE SIDE IS A LIST. Scatter runs on
//  worker threads and a List being appended to while it is enumerated is a crash
//  waiting for the wrong frame. So writes go to the List on the main thread, and
//  Flatten() takes a snapshot into a plain array once per bake. Workers read the
//  snapshot and nothing else. The same discipline the water probes already follow,
//  and for the same reason.
//
//  WHY VERSION AND NOT A DIRTY FLAG. A bake needs to know whether the snapshot it
//  holds is still current. A bool answers "has anything changed since someone last
//  cleared me", which is a question about the flag rather than about the data; a
//  counter answers "is what I have the same as what exists", which is the question
//  actually being asked and cannot be got wrong by two readers.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Ecology
{
    public sealed class CutRegionRegistry
    {
        /// <summary>
        /// The instance the road bridge reports to. Not a lazy property with a lock:
        /// every writer is the main thread by contract, and a lock here would suggest
        /// otherwise to the next reader of this file.
        /// </summary>
        public static readonly CutRegionRegistry Shared = new CutRegionRegistry();

        private readonly List<CutRegion> regions = new List<CutRegion>();

        private int version;

        /// <summary>Changes whenever the set of regions changes. Compare, do not clear.</summary>
        public int Version { get { return version; } }

        public int Count { get { return regions.Count; } }

        /// <summary>
        /// Records a cut volume. Main thread only.
        ///
        /// Degenerate bounds are dropped rather than stored: a zero-size Bounds is
        /// what an uninitialised one looks like, and storing it would exclude
        /// vegetation from a single point for no reason anyone could later explain.
        /// </summary>
        public void Add(Bounds worldBounds)
        {
            Vector3 size = worldBounds.size;
            if (size.x <= 0f || size.z <= 0f) return;

            regions.Add(CutRegion.FromBounds(worldBounds));
            version++;
        }

        /// <summary>Forgets every region. Called when a world is rebuilt from scratch.</summary>
        public void Clear()
        {
            if (regions.Count == 0) return;

            regions.Clear();
            version++;
        }

        /// <summary>
        /// Snapshots the regions into <paramref name="buffer"/>, growing it only when
        /// it is genuinely too small, and returns how many were written. Reusing the
        /// caller's buffer keeps a per-chunk bake from allocating for a list that
        /// almost never changes.
        /// </summary>
        public int Flatten(ref CutRegion[] buffer)
        {
            int count = regions.Count;

            if (buffer == null || buffer.Length < count)
                buffer = new CutRegion[Mathf.Max(4, count)];

            for (int i = 0; i < count; i++)
                buffer[i] = regions[i];

            return count;
        }
    }
}
