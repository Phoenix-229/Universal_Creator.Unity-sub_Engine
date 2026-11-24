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
        /// DEF-13. The FLOOR under the wet beach's clearance above sea level - the least this
        /// pass will ever leave, whatever the author's own freeboard dial says.
        ///
        /// The clearance itself is NOT this number: it is the `freeboard` argument, which the
        /// Height Output derives from its `seaFreeboardMetres` dial (20 m by default) and hands
        /// identically to <see cref="SeaLevelFloor"/> and <see cref="ContinentalRise"/>. One
        /// dial, one definition of "how far dry ground stands above the waterline", three passes
        /// that agree on it. This constant only stops that dial being turned to ZERO, because
        /// zero is the setting that reintroduces the bug below.
        ///
        /// THIS CONSTANT IS A STABILITY FIX, NOT A LOOK. Grading land to exactly `sea` left the
        /// water surface and the beach on the same plane with zero freeboard, across a band
        /// hundreds of metres wide. Anything that then moved either by a hair - the 16-bit
        /// heightmap quantisation (one step is ~9 mm on a 600 m world), a later pass touching
        /// height near the shore, or the smallest nudge of the sea-level dial - put water above
        /// ground across the WHOLE ring at once. The sea did not creep inland, it jumped, and
        /// whatever stood above the band was left as an island. That was the author's "the ocean
        /// water surpass the sea level and creating island".
        ///
        /// 1e-4 is about 6 cm on a 600 m world: invisible to the eye, and six or seven heightmap
        /// steps clear of the waterline, so the shore is a LINE where water meets rising ground
        /// rather than two coincident planes. As a floor it is deliberately tiny - it is the
        /// quantisation guard, not the look; the look is the author's metres dial.
        /// </summary>
        private const float BeachFreeboardNorm = 1e-4f;

        /// <summary>
        /// DEF-13. How much of the rim's rise continues across the OUTER half of the coastal
        /// plain, as a fraction of that rise. The plain used to saturate and then hold dead
        /// flat, which was the second of the two flat rings the author was seeing. A small
        /// continuing grade means the coast reads beach -> gently climbing plain -> ascent with
        /// no plane anywhere in it.
        /// </summary>
        private const float RimContinuedGrade = 0.35f;

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
        /// Width in METRES of the near-shore intertidal beach. It sits just ABOVE the waterline
        /// by <paramref name="freeboard"/>, never exactly on it - grading land onto the water
        /// plane is what let the sea flood the whole band inland at once. With `variety` above
        /// zero this width varies region to region, so one stretch is a wide strand and the
        /// next narrow.
        /// </param>
        /// <param name="coastalPlain">
        /// Width in METRES, measured from the outer edge of the wet beach, of the coastal-plain
        /// rim: the shore rises onto it over the plain's inner half and then keeps climbing
        /// GENTLY across the rest (DEF-13 - it used to hold dead flat, which was the second of
        /// two flat rings). Beyond it the distance feather releases the natural interior terrain
        /// back to full strength (the inland ascent).
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
        /// <param name="freeboard">
        /// DEF-13. Normalised clearance the wet beach keeps ABOVE the waterline - the SAME
        /// value <see cref="SeaLevelFloor"/> floors dry-land dips to and
        /// <see cref="ContinentalRise"/> guarantees, so all three passes agree on how far dry
        /// ground stands above water instead of each inventing its own number. The Height
        /// Output derives it from `seaFreeboardMetres / heightScale`, which is why a metres
        /// dial means the same clearance whatever the world's vertical scale is.
        ///
        /// It is floored at <see cref="BeachFreeboardNorm"/>: a buyer who turns the dial to
        /// zero still gets the quantisation guard, because zero clearance across a band
        /// hundreds of metres wide is what let the sea flood the whole ring at once and leave
        /// islands. The dial controls the LOOK; it cannot switch off the stability fix.
        /// </param>
        /// <param name="lakeMask">Optional accepted-lake coverage, 0..1 on the drainage grid; interiors are preserved and edges feather the grade.</param>
        /// <param name="shoreBlendMetres">Nonnegative inland distance over which shore freeboard reaches full strength; zero retains legacy clearance.</param>
        /// <param name="shoreDeadzoneMetres">Nonnegative distance from ocean within which authored ground is released from freeboard.</param>
        public static void Apply(WorldDrainage wd, Matrix src, float seaLevel,
                                 float coastalRange, float beachFlat, float coastalPlain,
                                 float coastalRise, float cliffThreshold, float variety,
                                 float strength, float freeboard, StopToken stop, float[] lakeMask = null,
                                 float shoreBlendMetres = 0, float shoreDeadzoneMetres = 0)
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

            // DEF-13: the author's own clearance dial, floored by the quantisation guard.
            // See the `freeboard` param doc - this is the SAME clearance SeaLevelFloor and
            // ContinentalRise use, so the three passes no longer disagree about where the
            // shore sits. A target above 1 cannot misbehave: the pass is lower-only, so an
            // unreachable target simply means no grading.
            float free  = Mathf.Max(BeachFreeboardNorm, Mathf.Max(0f, freeboard));

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
            float[] chunkLake = lakeMask == null ? null : wd.ReadAtChunk(lakeMask, src, WorldRead.Bilinear);
            if (stop != null && stop.StopRequested) return;

            // 4) Grade the land side toward the beach ramp, gated by slope (cliff) and by
            //    a regional character field (variety). Lower-only, land-only.
            int outX  = src.rect.size.x;
            float baseX = src.worldPos.x;
            float baseZ = src.worldPos.z;
            Vector2 spix = src.PixelSize;
            // DEF-13: the rise span is now computed per cell, because the beach width it is
            // measured from varies region to region. See inside the loop.

            int n = src.arr.Length;
            for (int i = 0; i < n && i < chunkDist.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return;

                float h = src.arr[i];
                if (h <= sea) continue;                 // water / basin: depth preserved
                if (chunkLake != null && chunkLake[i] >= 0.999f) continue; // UC-T08-15: above-sea lakes are water too.

                float d = chunkDist[i];
                if (d >= range) continue;               // beyond the coastal band

                // Regional character for this cell: one low-frequency, smooth, seamless value
                // (350 m cells, bilinear). DEF-13 reads it BEFORE the profile, because it now
                // shapes the target as well as the blend - see below.
                float chRaw = CharacterAt(baseX + (i % outX) * spix.x,
                                          baseZ + (i / outX) * spix.y);

                // DEF-13: THE VARIATION IS IN THE TARGET, NOT ONLY IN THE WEIGHT.
                // It used to modulate the blend weight alone, so `variety` changed how far the
                // ground moved toward the profile but could not stop the profile being a plane -
                // an author turning that dial watched the flatness soften in amplitude and never
                // break up. Varying the beach WIDTH and the rim HEIGHT region to region is what
                // makes one stretch of coast a wide shallow strand and the next a narrow one.
                // At variety 0 both collapse to the authored values exactly, so the dial still
                // means "no regional variation".
                float flatHere = flat * Mathf.Lerp(1f, 0.35f + 1.30f * chRaw, vary);
                float riseHere = rise * Mathf.Lerp(1f, 0.60f + 0.80f * chRaw, vary);

                float riseSpanHere = Mathf.Max(1e-3f, plain * 0.5f);

                // Piecewise beach profile (asymptotic coastal shelf, adapted lower-only +
                // variety-gated): a wet beach just ABOVE the waterline for the near-shore
                // `flatHere` metres, a smoothstep rise onto the coastal plain over the inner
                // half of `plain`, then a gentle continuing climb across the rest of it.
                // Inland of the band the distance feather releases the natural interior terrain
                // back to full strength, which reads as the mountain ascent.
                float rampH;
                if (d <= flatHere)
                {
                    rampH = sea + free * SeaLevelFloor.ShoreRelease(d, shoreBlendMetres, shoreDeadzoneMetres);
                }
                else
                {
                    float tr = Mathf.Clamp01((d - flatHere) / riseSpanHere);
                    rampH = sea + free * SeaLevelFloor.ShoreRelease(d, shoreBlendMetres, shoreDeadzoneMetres)
                          + riseHere * (tr * tr * (3f - 2f * tr));   // smoothstep

                    // DEF-13: no held plane. This is the big one, and the author's own graph is
                    // what showed its size: with `beachFlat: 10`, `coastalPlainMetres: 40` and
                    // `coastalRange: 500`, the smoothstep saturated by ~30 m and `rampH` then
                    // stayed at exactly `sea + rise` for the REMAINING 470 metres. Every cell
                    // within half a kilometre of the shore that sat below that plateau plus the
                    // cliff gate was dragged onto one height. That is the wide flat ring around
                    // the coast - not the 10 m beach, which was never the problem.
                    //
                    // So the climb continues across the whole rest of the band and hands over to
                    // the natural interior at `range`, instead of plateauing at the end of the
                    // plain. The MAGNITUDE here is a look decision the author should judge; the
                    // structural point is that there is no constant stretch left anywhere.
                    float outerSpan = Mathf.Max(1e-3f, range - flatHere - riseSpanHere);
                    if (d > flatHere + riseSpanHere)
                    {
                        float tp = Mathf.Clamp01((d - flatHere - riseSpanHere) / outerSpan);
                        rampH += riseHere * RimContinuedGrade * (tp * tp * (3f - 2f * tp));
                    }
                }

                // LOWER-ONLY, and this is what makes DEF-13 safe by construction: a HIGHER ramp
                // can only mean LESS grading. The pass can never lift ground, so raising the
                // target cannot introduce new terrain, only leave more of the natural surface.
                if (rampH >= h) continue;               // terrain already at/under the ramp

                // Distance feather: full grade at the shore, fading to zero at the band edge.
                float df = 1f - d / range;
                df = df * df * (3f - 2f * df);

                // Cliff gate: a small excess above the ramp is graded to beach; a large
                // excess is a sea-cliff and is left standing (weight -> 0).
                float excess = h - rampH;
                float cg = Mathf.Clamp01(excess / cliff);
                cg = 1f - cg * cg * (3f - 2f * cg);

                // Regional character as a weight too: keep some gentle coasts near-full (wide
                // beach), soften the grade on others so they read as coastal plain / low hills.
                float ch = Mathf.Lerp(1f, chRaw, vary);

                float w = df * cg * ch * amt;
                if (chunkLake != null) w *= 1 - Mathf.Clamp01(chunkLake[i]);
                if (w <= 0f) continue;

                float target = rampH < h ? rampH : h;   // lower-only
                src.arr[i] = h + (target - h) * w;
            }
        }

        /// <summary>
        /// DEF-13 item 4. Resolves a coastal band width authored in METRES against the world's
        /// own span, as <c>Min(authored, fraction x span)</c>.
        ///
        /// SHRINK-ONLY BY CONSTRUCTION, and that is the whole reason it is safe to have on by
        /// default. On a large world the authored metres win and a tuned graph is untouched; on a
        /// small world the fraction wins and the band stops being wider than the landmass it is
        /// supposed to fringe. It can never widen a band the author did not ask for, so it cannot
        /// make an existing world worse - only less flattened.
        /// </summary>
        /// <param name="authoredMetres">The author's absolute width in metres.</param>
        /// <param name="fraction">Share of the world span this band may occupy.</param>
        /// <param name="worldSpanM">The SHORTER of the world's two horizontal extents, in metres.</param>
        /// <param name="minFraction">Lower clamp on <paramref name="fraction"/>.</param>
        /// <param name="maxFraction">Upper clamp on <paramref name="fraction"/>.</param>
        internal static float ResolveBandMetres(float authoredMetres, float fraction,
                                               float worldSpanM,
                                               float minFraction, float maxFraction)
        {
            float authored = Mathf.Max(0f, authoredMetres);
            float span     = Mathf.Max(1f, worldSpanM);
            float f        = Mathf.Clamp(fraction, minFraction, maxFraction);
            return Mathf.Min(authored, f * span);
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
