// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    CoastalShelf.cs
//  Purpose: The always-on rule that where land meets the ocean it does so with a
//           VARIED coast - wide beaches and coastal plains on the gentle stretches,
//           sea-cliffs left standing on the steep ones - instead of a boxy wall
//           where a tall hill drops straight into deep water. Runs in the CORE of
//           height output, on the COMPOSED heightmap of every noise creator and
//           combiner, so no graph wiring can bypass it.
//
//  WHY IT IS NOT A UNIFORM BEACH
//  -----------------------------
//  A naive "crush everything near the water flat" pass would erase every sea-cliff
//  and coastal mountain and leave one monotonous beach around the whole world. Real
//  coasts are varied: beaches form by wave erosion where land rises GENTLY from the
//  water; cliffs persist where it rises STEEPLY. So the grade here is gated by how
//  far the terrain stands ABOVE a gentle beach ramp - a gentle shore is pulled onto
//  the ramp (beach/plain), a steep shore (excess above the ramp > cliffThreshold) is
//  left alone (cliff). A low-frequency regional "character" field varies the grading
//  further so neighbouring gentle coasts read as beach / plain / low hills rather
//  than one repeated profile.
//
//  WHAT IT NEVER TOUCHES
//  ---------------------
//    - Genuine ocean and any cell at or below sea: skipped entirely, so the sea's
//      own floor and depth are preserved (this is a LAND-side pass only).
//    - Steep coasts: preserved as cliffs.
//    - It is LOWER-ONLY on land: it can pull a gentle rise down onto the beach ramp,
//      never raise ground, so it can never build a new wall or undo a carve.
//
//  It runs AFTER SeaLevelFloor (which guarantees dry ground stands >= sea), reusing
//  the same whole-world drainage solve and the same ocean classification, then
//  builds a coastline distance field (chamfer transform) the land-side beach ramp is
//  driven by. Managed C# over float[]/bool[]; safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Grades the gentle stretches of coast into beaches and coastal plains while
    /// leaving steep shores as cliffs, driven by a whole-world coastline distance
    /// field. Land-side only; never touches ocean depth. Applied in place.
    /// </summary>
    public static class CoastalShelf
    {
        /// <summary>Metres per regional-character cell (low-freq coast variety).</summary>
        private const float CharacterScaleMetres = 350f;

        /// <summary>
        /// Grades the land side of the coast in place. <paramref name="wd"/> is a
        /// whole-world drainage solve of the same height branch (the one SeaLevelFloor
        /// already built); when it is null the pass is skipped and the height is left
        /// exactly as it was. Runs AFTER <see cref="SeaLevelFloor"/> in the same output
        /// post-pass, on the same working copy.
        /// </summary>
        /// <param name="seaLevel">Normalised sea height (0.35 is the system default).</param>
        /// <param name="coastalRange">
        /// Width of the coastal band in METRES: land further than this from the water is
        /// beyond the beach and is never touched.
        /// </param>
        /// <param name="beachFlat">
        /// Width in METRES of the near-shore intertidal beach that sits at the waterline
        /// before the ramp begins to rise. This is the only stretch pulled to exactly sea
        /// (a wet beach genuinely meets the water); the dry ramp beyond it rises above sea.
        /// </param>
        /// <param name="coastalPlain">
        /// Width in METRES, measured from the outer edge of the wet beach, of the FLAT
        /// coastal-plain rim: the shore rises onto this rim over the plain's inner half and
        /// then holds flat across it, so the coast reads as beach -> level plain -> ascent
        /// rather than one continuous slope. Beyond it the distance feather releases the
        /// natural interior terrain back to full strength (the inland mountain ascent).
        /// </param>
        /// <param name="coastalRise">
        /// Normalised height the beach ramp rises by across the rest of the band, so the
        /// shore eases up to a coastal plain instead of a step.
        /// </param>
        /// <param name="cliffThreshold">
        /// Normalised excess of the natural terrain above the ramp beyond which the shore
        /// is a sea-cliff and is left standing. Small excess is graded; large excess is a
        /// cliff. This is what keeps the coast VARIED rather than one uniform beach.
        /// </param>
        /// <param name="variety">
        /// 0..1 amount the low-frequency regional character field is allowed to vary the
        /// grade region to region (0 = every gentle coast graded identically).
        /// </param>
        /// <param name="strength">0..1 blend of the grade, 1 = full.</param>
        public static void Apply(WorldDrainage wd, Matrix src, float seaLevel,
                                 float coastalRange, float beachFlat, float coastalPlain,
                                 float coastalRise, float cliffThreshold, float variety,
                                 float strength, StopToken stop)
        {
            if (wd == null || src == null || src.arr == null || strength <= 1e-5f) return;
            if (wd.height == null || wd.height.arr == null) return;

            float sea = Mathf.Clamp01(seaLevel);
            if (sea <= 1e-5f) return;                             // no sea: nothing to grade

            float amt   = Mathf.Clamp01(strength);
            float range = Mathf.Max(1f, coastalRange);            // metres
            float flat  = Mathf.Clamp(beachFlat, 0f, range);      // metres
            float plain = Mathf.Clamp(coastalPlain, 0f, range - flat); // metres, rim beyond beach
            float rise  = Mathf.Max(0f, coastalRise);             // normalised
            float cliff = Mathf.Max(1e-4f, cliffThreshold);       // normalised
            float vary  = Mathf.Clamp01(variety);

            int resX = wd.resX, resZ = wd.resZ;
            int count = resX * resZ;
            if (count <= 0 || wd.height.arr.Length < count) return;

            // 1) Ocean mask - the EXACT same classification the sea-level floor uses, so
            //    the two passes agree on what the sea is (no second, drifting definition).
            bool[] ocean = SeaLevelFloor.FloodOceanFromEdges(wd.height.arr, resX, resZ, sea);
            if (stop != null && stop.StopRequested) return;

            // 2) Horizontal distance (in METRES) from every coarse cell to the nearest
            //    ocean cell, by a two-pass chamfer transform. This is the coastline SDF the
            //    land-side beach ramp is driven by; solving it on the whole-world grid is
            //    what keeps the beach continuous across chunk seams instead of restarting.
            Vector2 pix = wd.height.PixelSize;
            float mppX = Mathf.Max(1e-3f, pix.x);
            float mppZ = Mathf.Max(1e-3f, pix.y);
            float[] dist = ChamferDistanceToOcean(ocean, resX, resZ, mppX, mppZ, range);
            if (stop != null && stop.StopRequested) return;

            // 3) Read the coarse coastline distance onto this chunk's fine lattice.
            float[] chunkDist = wd.ReadAtChunk(dist, src, WorldRead.Bilinear);
            if (stop != null && stop.StopRequested) return;

            // 4) Grade the land side toward the beach ramp, gated by slope (cliff) and by
            //    a regional character field (variety). Lower-only, land-only.
            int outX  = src.rect.size.x;
            float baseX = src.worldPos.x;
            float baseZ = src.worldPos.z;
            Vector2 spix = src.PixelSize;
            // The shore rises onto the flat plain rim over the plain's inner half, then holds.
            float riseSpan = Mathf.Max(1e-3f, plain * 0.5f);

            int n = src.arr.Length;
            for (int i = 0; i < n && i < chunkDist.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return;

                float h = src.arr[i];
                if (h <= sea) continue;                 // water / basin: depth preserved

                float d = chunkDist[i];
                if (d >= range) continue;               // beyond the coastal band

                // Piecewise beach profile (asymptotic coastal shelf, adapted lower-only +
                // variety-gated): a flat wet beach AT the waterline for the near-shore `flat`
                // metres, a quick smoothstep rise onto a FLAT coastal-plain rim over the inner
                // half of `plain`, then the rim is HELD flat. Inland of the rim the distance
                // feather (below) releases the natural interior terrain back to full strength,
                // which reads as the gentle mountain ascent - no explicit third slope needed.
                float rampH;
                if (d <= flat) rampH = sea;             // wet intertidal beach, meets the water
                else
                {
                    float tr = Mathf.Clamp01((d - flat) / riseSpan);
                    rampH = sea + rise * (tr * tr * (3f - 2f * tr));    // smoothstep, saturates
                }
                if (rampH >= h) continue;               // terrain already at/under the ramp

                // Distance feather: full grade at the shore, fading to zero at the band edge.
                float df = 1f - d / range;
                df = df * df * (3f - 2f * df);

                // Cliff gate: a small excess above the ramp is graded to beach; a large
                // excess is a sea-cliff and is left standing (weight -> 0).
                float excess = h - rampH;
                float cg = Mathf.Clamp01(excess / cliff);
                cg = 1f - cg * cg * (3f - 2f * cg);

                // Regional character: keep some gentle coasts near-full (wide beach), soften
                // the grade on others so they read as coastal plain / low hills instead.
                float ch = Mathf.Lerp(1f, CharacterAt(baseX + (i % outX) * spix.x,
                                                      baseZ + (i / outX) * spix.y), vary);

                float w = df * cg * ch * amt;
                if (w <= 0f) continue;

                float target = rampH < h ? rampH : h;   // lower-only
                src.arr[i] = h + (target - h) * w;
            }
        }

        /// <summary>
        /// Two-pass chamfer distance transform: metres from each cell to the nearest
        /// ocean cell. Straight steps cost one pixel pitch, diagonals the true hypotenuse,
        /// so the field is a good euclidean approximation (~4% worst case) at O(cells).
        /// Capped: anything past <paramref name="cap"/> is left far and the caller skips it.
        /// Internal (not private) so the ecology hydrology bake (HydroField) reuses the
        /// EXACT same coastline SDF the shelf grades on - one definition of the shore.
        /// </summary>
        internal static float[] ChamferDistanceToOcean(bool[] ocean, int resX, int resZ,
                                                       float mppX, float mppZ, float cap)
        {
            int count = resX * resZ;
            float far = cap * 4f + 1f;
            float[] d = new float[count];
            for (int i = 0; i < count; i++) d[i] = ocean[i] ? 0f : far;

            float sX = mppX;
            float sZ = mppZ;
            float sD = Mathf.Sqrt(mppX * mppX + mppZ * mppZ);

            // Forward pass: top-left neighbourhood.
            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    int i = z * resX + x;
                    float v = d[i];
                    if (x > 0)                    v = Mathf.Min(v, d[i - 1] + sX);
                    if (z > 0)                    v = Mathf.Min(v, d[i - resX] + sZ);
                    if (x > 0 && z > 0)           v = Mathf.Min(v, d[i - resX - 1] + sD);
                    if (x < resX - 1 && z > 0)    v = Mathf.Min(v, d[i - resX + 1] + sD);
                    d[i] = v;
                }
            }

            // Backward pass: bottom-right neighbourhood.
            for (int z = resZ - 1; z >= 0; z--)
            {
                for (int x = resX - 1; x >= 0; x--)
                {
                    int i = z * resX + x;
                    float v = d[i];
                    if (x < resX - 1)                 v = Mathf.Min(v, d[i + 1] + sX);
                    if (z < resZ - 1)                 v = Mathf.Min(v, d[i + resX] + sZ);
                    if (x < resX - 1 && z < resZ - 1) v = Mathf.Min(v, d[i + resX + 1] + sD);
                    if (x > 0 && z < resZ - 1)        v = Mathf.Min(v, d[i + resX - 1] + sD);
                    d[i] = v;
                }
            }

            return d;
        }

        /// <summary>
        /// Deterministic low-frequency value noise of world XZ, 0..1, used to vary the
        /// coastal grade region to region. Bilinear over a hashed lattice at
        /// <see cref="CharacterScaleMetres"/> spacing; smooth, seamless, allocation-free.
        /// </summary>
        private static float CharacterAt(float worldX, float worldZ)
        {
            float fx = worldX / CharacterScaleMetres;
            float fz = worldZ / CharacterScaleMetres;

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0;
            float tz = fz - z0;

            float v00 = Hash01(x0, z0),     v10 = Hash01(x0 + 1, z0);
            float v01 = Hash01(x0, z0 + 1), v11 = Hash01(x0 + 1, z0 + 1);

            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);

            float a = Mathf.Lerp(v00, v10, tx);
            float b = Mathf.Lerp(v01, v11, tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>Stable 0..1 hash of an integer lattice coordinate.</summary>
        private static float Hash01(int x, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + z * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
