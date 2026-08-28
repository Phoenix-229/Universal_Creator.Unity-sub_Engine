// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SplatPageCache.cs
//  Purpose: The splat cache's residency policy (§ 9.7 G-44): a page table indexes an
//           indirection into a physical atlas, LRU decides what to evict when the atlas
//           is full, and the pages under and immediately around the camera are PINNED so
//           a fast pan can never evict them. The unpinned near ring is exactly how
//           textbook virtual texturing produces the classic blurry-ground-under-your-feet
//           frame - the atlas fills with far pages a spin just requested, the near pages
//           fall off the LRU tail, and the one place the player is always looking goes to
//           mush. Pinning the near ring makes that frame impossible: eviction only ever
//           chooses among UNPINNED pages, so the near ring survives any pan.
//
//  DETERMINISTIC EVICTION, NO HASH-SET ORDER
//  -----------------------------------------
//  Recency is a List with the most-recently-used page at the front; a hit promotes to
//  the front, a miss admits at the front and evicts from the tail. The victim is the
//  first UNPINNED page scanned from the LRU tail, so the choice is a function of the
//  deterministic touch order alone (C-04) - the pinned set is consulted only for
//  membership, never iterated, so its hashing never leaks into the eviction order.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  No page-table texture, no atlas, no GPU. This is the residency bookkeeping; the
//  self-test pins that pinned pages never evict under a pan storm, that a working set
//  fitting capacity reaches zero evictions (steady), that promotion protects a
//  freshly-used page, and that the ring follows the camera when a page leaves it. The
//  texture indirection and the atlas upload are the System D scene layer above.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Residency policy for the splat cache: an LRU over resident <see cref="PageKey"/> pages with a pinned near
    /// ring that eviction can never touch. Deterministic (tail-scan eviction over a recency list, no hash-set
    /// iteration); device-free. Models the page table + atlas residency, not the GPU indirection itself.
    /// </summary>
    public sealed class SplatPageCache
    {
        private readonly int _capacity;
        private readonly List<PageKey> _order = new List<PageKey>();   // front = most-recently-used
        private readonly HashSet<PageKey> _pinned = new HashSet<PageKey>();
        private int _evictionCount;

        /// <summary>The atlas page capacity. A negative request is clamped to zero.</summary>
        public SplatPageCache(int capacity) { _capacity = capacity < 0 ? 0 : capacity; }

        public int Capacity => _capacity;
        public int ResidentCount => _order.Count;
        public int PinnedCount => _pinned.Count;
        public int EvictionCount => _evictionCount;

        public bool IsResident(PageKey key) => _order.Contains(key);
        public bool IsPinned(PageKey key) => _pinned.Contains(key);

        /// <summary>
        /// Touch a page for this frame. <paramref name="pinned"/> marks it part of the near ring (never evictable);
        /// touching a page unpinned clears any prior pin, so the ring follows the camera. A hit promotes to MRU with
        /// no eviction; a miss admits at MRU, evicting the LRU-most UNPINNED page when the atlas is full. Returns
        /// true iff this touch evicted a page.
        /// </summary>
        public bool Touch(PageKey key, bool pinned)
        {
            if (pinned) _pinned.Add(key); else _pinned.Remove(key);

            int at = _order.IndexOf(key);
            if (at >= 0)
            {
                if (at != 0) { _order.RemoveAt(at); _order.Insert(0, key); }   // promote to MRU
                return false;
            }

            if (_capacity == 0) return false;                                  // nothing can be resident

            bool evicted = false;
            if (_order.Count >= _capacity)
            {
                int victim = -1;
                for (int i = _order.Count - 1; i >= 0; i--)                    // scan from LRU tail
                    if (!_pinned.Contains(_order[i])) { victim = i; break; }
                if (victim < 0) return false;                                  // atlas full of pinned pages: drop
                _order.RemoveAt(victim);
                _evictionCount++;
                evicted = true;
            }

            _order.Insert(0, key);
            return evicted;
        }
    }
}
