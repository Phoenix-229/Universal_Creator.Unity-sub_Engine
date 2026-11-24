// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ImpostorAtlasBudget.cs
//  Purpose: The measured impostor-atlas budget, and the residency discipline it
//           forces. A billboard atlas is not free: one species costs its tile grid
//           squared, times the compressed bits per pixel, times the number of maps
//           it bakes (albedo+depth packed, plus a normal). Written out for a real
//           tier, the desktop number is uncomfortable - a 2048² BC7 pair is ~8 MB a
//           species, so a 48 MB pool holds only SIX of a fifty-species library.
//
//  WHY THE ARITHMETIC CHANGES THE DESIGN
//  -------------------------------------
//  If every species could stay resident there would be nothing to manage. It cannot:
//  the budget divided by the per-species cost is the resident CAPACITY, and on Desktop
//  that capacity is far below the library. So the atlas needs the same residency
//  discipline as the splat cache - only species present in loaded chunks hold a slot,
//  and least-recently-used species are evicted when a newcomer needs room. The honest
//  done-when is a behavioural one: if the working set (distinct species actually on
//  screen) FITS the capacity, evictions stop after warm-up and the resident set is
//  steady; if it does NOT fit, every cycle evicts and the eviction RATE stays high -
//  which is the thrash an author must be shown rather than left to guess at.
//
//  TWO DEVICE-FREE PIECES
//  ----------------------
//  ImpostorAtlasBudget is pure arithmetic - tile, views, bits, map-count in; bytes and
//  capacity out - so a self-test reproduces the table's numbers exactly. LruResidencySet
//  is a plain managed LRU (no Unity type, no allocation per touch beyond its backing
//  lists) that counts residents and evictions, so the steady-vs-thrash done-when is
//  pinned in edit mode without a camera or a loaded world. The per-frame component that
//  reads real loaded chunks and drives the re-bake queue is the thin scene layer above;
//  the counting and the capacity law live here.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure arithmetic of the impostor atlas budget: the compressed byte cost of one species' atlas and
    /// the resident-species capacity a memory pool therefore allows. Reproduces the § 9.6 tier table
    /// exactly, so the residency need is measured rather than assumed. No Unity type, no allocation.
    /// </summary>
    public static class ImpostorAtlasBudget
    {
        /// <summary>Maps a species bakes into its atlas: albedo+depth packed as one, plus a normal map.</summary>
        public const int DefaultMapsPerSpecies = 2;

        /// <summary>The atlas edge in pixels: a square grid of <paramref name="viewsPerAxis"/> baked views, each a
        /// <paramref name="tilePx"/> tile. Fails closed to 0 on degenerate input.</summary>
        public static int AtlasEdgePx(int tilePx, int viewsPerAxis)
        {
            if (tilePx <= 0 || viewsPerAxis <= 0) return 0;
            return tilePx * viewsPerAxis;
        }

        /// <summary>
        /// The compressed byte cost of one species' atlas: edge² pixels × bitsPerPixel / 8, times the number of
        /// maps baked. Matches the tier table (e.g. 2048² @ 8 bpp × 2 maps ≈ 8 MB on Desktop). Fails closed to 0.
        /// </summary>
        public static long BytesPerSpecies(int atlasEdgePx, float bitsPerPixel, int mapsPerSpecies = DefaultMapsPerSpecies)
        {
            if (atlasEdgePx <= 0 || bitsPerPixel <= 0f || mapsPerSpecies <= 0) return 0L;
            double pixels = (double)atlasEdgePx * atlasEdgePx;
            double bytesPerMap = pixels * bitsPerPixel / 8.0;
            return (long)(bytesPerMap * mapsPerSpecies);
        }

        /// <summary>
        /// How many species a memory pool holds: the budget divided by the per-species cost, floored. This is the
        /// number the residency policy must live within; below the library size it is what forces LRU eviction.
        /// Fails closed to 0 when a species does not fit or inputs are degenerate.
        /// </summary>
        public static int ResidentCapacity(long budgetBytes, long bytesPerSpecies)
        {
            if (budgetBytes <= 0L || bytesPerSpecies <= 0L) return 0;
            return (int)(budgetBytes / bytesPerSpecies);
        }

        /// <summary>Eviction RATE the debug window reports: evictions normalised to per-minute over an elapsed
        /// window. A steady resident set drives this toward 0; a thrashing one holds it high. Guards elapsed ≤ 0.</summary>
        public static float EvictionsPerMinute(int evictions, float elapsedSeconds)
        {
            if (evictions <= 0 || elapsedSeconds <= 0f) return 0f;
            return evictions * 60f / elapsedSeconds;
        }
    }

    /// <summary>
    /// A plain least-recently-used residency set over species ids, with a fixed capacity. Touching a species
    /// marks it most-recently-used and makes it resident; when a newcomer would exceed capacity the
    /// least-recently-used resident is evicted (counted). Device-free and allocation-light - the CPU model the
    /// scene layer drives from loaded chunks, and the object a self-test uses to pin the steady-vs-thrash rule.
    /// </summary>
    public sealed class LruResidencySet
    {
        private readonly int _capacity;
        private readonly List<int> _mruOrder;   // front = most recently used, back = least
        private int _evictions;

        public LruResidencySet(int capacity)
        {
            _capacity = capacity < 0 ? 0 : capacity;
            _mruOrder = new List<int>(_capacity > 0 ? _capacity : 4);
            _evictions = 0;
        }

        /// <summary>Species currently holding an atlas slot.</summary>
        public int ResidentCount => _mruOrder.Count;

        /// <summary>Total evictions since construction - the numerator the debug window turns into a per-minute rate.</summary>
        public int EvictionCount => _evictions;

        /// <summary>Whether a species currently holds a slot.</summary>
        public bool IsResident(int speciesId) => _mruOrder.Contains(speciesId);

        /// <summary>
        /// Marks <paramref name="speciesId"/> most-recently-used and resident. A hit on a resident species costs
        /// nothing (no eviction). A miss admits it, evicting the least-recently-used species first when the set is
        /// full. Returns true when this touch caused an eviction. A zero-capacity set never becomes resident.
        /// </summary>
        public bool Touch(int speciesId)
        {
            int existing = _mruOrder.IndexOf(speciesId);
            if (existing >= 0)
            {
                // Hit: promote to most-recently-used, no eviction.
                _mruOrder.RemoveAt(existing);
                _mruOrder.Insert(0, speciesId);
                return false;
            }
            if (_capacity <= 0) return false;

            bool evicted = false;
            if (_mruOrder.Count >= _capacity)
            {
                _mruOrder.RemoveAt(_mruOrder.Count - 1); // drop the least-recently-used
                _evictions++;
                evicted = true;
            }
            _mruOrder.Insert(0, speciesId);
            return evicted;
        }
    }
}
