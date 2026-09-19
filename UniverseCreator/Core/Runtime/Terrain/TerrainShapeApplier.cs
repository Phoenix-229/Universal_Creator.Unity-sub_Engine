// -----------------------------------------------------------------------------
//  Universe Creator - Path Tool Integration (Module 6 seam)
//  File:    TerrainShapeApplier.cs
//  Purpose: Folds the heightfield-shaping requests a path tool left in the
//           TerrainShapeInbox into a chunk's heights, at the same point in the
//           rebuild where the water carve and the manual override already fold in.
//           This is the UC side of the seam: the tool asked, UC shapes its own
//           ground.
//
//  WHERE IT RUNS AND WHY THE ORDER IS FIXED
//  ----------------------------------------
//  ReapplyCarves rebuilds each chunk from pristine graph heights and folds edits
//  in a fixed order: water carve (lower a channel), THEN these shapings, THEN the
//  manual override (the author's own sculpt), THEN the height guard. This applier
//  sits in the middle for a reason:
//
//    * after the water carve, so a road that runs beside a carved river blends
//      down to the carved bank rather than to ground the river has not cut yet;
//    * before the override, so the author's hand always rides on top -- a sculpt
//      near a road is never undone by the conform.
//
//  Everything here is a pure function of absolute world position and the request's
//  own fields, so two chunks that share a seam compute the same height on both
//  sides and a road does not step at a chunk edge -- the same property the water
//  carve depends on.
//
//  WHAT IT DOES AND DOES NOT TOUCH
//  -------------------------------
//  Only the two kinds that are heightfield writes:
//    ConformCorridor  cut-and-fill the ground to the road, flat across the
//                     carriageway and shoulder, then a creaseless blend back to
//                     natural over the batter. It both lowers (a cut) and raises
//                     (an embankment), which is why it is modelled on the override
//                     layer's bidirectional write, not the water carve's lower-only
//                     Min.
//    KeyAbutments     fill the ground up into a dam's two ends so the crest keys
//                     into the valley walls and water cannot run around it. Fill
//                     only: an abutment ties the structure into high ground, it
//                     never digs the wall away.
//  RaiseWaterBody is a water-surface change, not a heightfield write, and belongs
//  to the water system; CutOpening removes ground and is the tunnel/portal hole
//  path. Both are skipped here so no shaping is applied twice.
//
//  THE BLEND MATHS IS UC's OWN
//  ---------------------------
//  The tool computed the widths (it is the authority on the road) and shipped them
//  per station in the request. The smoothstep from road to natural is reproduced
//  here rather than shared, because the tool lives in another assembly UC must not
//  reference. Reproducing a four-line S-curve is the seam working as intended, not
//  duplication: UC owns every heightfield write, curve included.
//
//  NO DOTS, NO BURST, NO NATIVEARRAY. Flat float[,] and managed lists.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Integration;

namespace UniverseCreator.TerrainSystem
{
    /// <summary> Applies the heightfield <see cref="TerrainShapeRequest"/>s held for a world to one chunk's normalised
    /// heightfield. Static and stateless, like <see cref="HeightGuard"/>: everything it needs arrives as arguments, so it
    /// can be called from ReapplyCarves and from a self test with equal ease. </summary>
    public static class TerrainShapeApplier
    {
        /// <summary> Folds every heightfield-shaping request that can reach this chunk into <paramref name="heights"/>,
        /// and returns how many samples were changed -- a number the self test and the inspector can read rather than a
        /// claim. <paramref name="heights"/> is [z,x] normalised 0..1, the Unity convention; the world position of sample
        /// (lz,lx) is (chunkWorldBounds.min.x + lx*mps, chunkWorldBounds.min.z + lz*mps), the same mapping the water
        /// carve uses. </summary>
        public static int ApplyAll(IReadOnlyList<TerrainShapeRequest> requests, float[,] heights,
            Bounds chunkWorldBounds, float metresPerSample, float maxHeightMetres)
        {
            if (requests == null || requests.Count == 0 || heights == null || maxHeightMetres <= 0f) { return 0; }

            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            if (resZ < 2 || resX < 2) { return 0; }

            float mps = Mathf.Max(1e-4f, metresPerSample);
            int changed = 0;

            for (int i = 0; i < requests.Count; i++)
            {
                TerrainShapeRequest r = requests[i];
                if (r == null || !r.HasWork) { continue; }

                Bounds fp;
                if (!r.TryGetFootprint(out fp)) { continue; }

                // Plan-view reject: a request whose footprint does not overlap this chunk in X/Z cannot change a sample
                // in it. Same predicate Affects uses, so the gate a caller tests with and the work done here agree.
                if (!OverlapsXZ(fp, chunkWorldBounds)) { continue; }

                switch (r.kind)
                {
                    case TerrainShapeKind.ConformCorridor:
                        changed += ApplyCorridor(r, heights, chunkWorldBounds, mps, maxHeightMetres, resZ, resX, fp);
                        break;
                    case TerrainShapeKind.KeyAbutments:
                        changed += ApplyAbutments(r, heights, chunkWorldBounds, mps, maxHeightMetres, resZ, resX, fp);
                        break;
                    // RaiseWaterBody and CutOpening are not heightfield writes -- see the file header.
                }
            }

            return changed;
        }

        /// <summary> True if any request that this applier actually writes -- ConformCorridor or KeyAbutments -- has a
        /// footprint overlapping this chunk in plan. The rebuild uses it as a gate: it decides whether a chunk is worth
        /// re-deriving from pristine ground and whether its pristine snapshot must be kept, on the same terms the water
        /// carve's CountAffecting decides those for a river. Deliberately blind to RaiseWaterBody and CutOpening, which
        /// this applier never writes -- reporting them here would keep a chunk alive in the height rebuild for a change
        /// that a different pass makes, which is how a cell gets stuck rewriting itself forever. </summary>
        public static bool Affects(IReadOnlyList<TerrainShapeRequest> requests, Bounds chunkWorldBounds)
        {
            if (requests == null || requests.Count == 0) { return false; }

            for (int i = 0; i < requests.Count; i++)
            {
                TerrainShapeRequest r = requests[i];
                if (r == null || !r.HasWork) { continue; }
                if (r.kind != TerrainShapeKind.ConformCorridor && r.kind != TerrainShapeKind.KeyAbutments) { continue; }

                Bounds fp;
                if (!r.TryGetFootprint(out fp)) { continue; }
                if (OverlapsXZ(fp, chunkWorldBounds)) { return true; }
            }

            return false;
        }

        // =====================================================================
        //  CONFORM CORRIDOR  (the shoulder/terrain fix)
        // =====================================================================

        /// <summary> Cut-and-fills the ground to a road corridor over the samples this chunk shares with the request's
        /// footprint. For each sample it finds the nearest station on the centreline, reads the road height and the
        /// flattened/blend half-widths the tool interpolated there, and writes the road level flat inside the flattened
        /// half-width, a smoothstep blend to natural ground out to the blend edge, and nothing beyond. </summary>
        private static int ApplyCorridor(TerrainShapeRequest r, float[,] heights, Bounds chunk, float mps,
            float maxHeight, int resZ, int resX, Bounds fp)
        {
            TerrainCorridorSample[] c = r.corridor;
            if (c == null || c.Length < 2) { return 0; }

            // The segments of THIS road that can reach THIS chunk, gathered once so a long road does not re-test every
            // one of its segments against every sample. A segment counts if its span, widened by the wider of its two
            // ends' blend half-widths, overlaps the chunk in plan.
            List<int> segs = null;
            for (int s = 0; s < c.Length - 1; s++)
            {
                float pad = Mathf.Max(c[s].blendOuterHalfWidthMetres, c[s + 1].blendOuterHalfWidthMetres) + mps;
                float sMinX = Mathf.Min(c[s].worldX, c[s + 1].worldX) - pad;
                float sMaxX = Mathf.Max(c[s].worldX, c[s + 1].worldX) + pad;
                float sMinZ = Mathf.Min(c[s].worldZ, c[s + 1].worldZ) - pad;
                float sMaxZ = Mathf.Max(c[s].worldZ, c[s + 1].worldZ) + pad;
                if (sMinX > chunk.max.x || sMaxX < chunk.min.x || sMinZ > chunk.max.z || sMaxZ < chunk.min.z)
                {
                    continue;
                }
                if (segs == null) { segs = new List<int>(8); }
                segs.Add(s);
            }
            if (segs == null) { return 0; }

            float invMax = 1f / maxHeight;
            int changed = 0;

            SampleWindow(fp, chunk, mps, resZ, resX, out int x0, out int x1, out int z0, out int z1);

            for (int lz = z0; lz <= z1; lz++)
            {
                float wz = chunk.min.z + lz * mps;
                for (int lx = x0; lx <= x1; lx++)
                {
                    float wx = chunk.min.x + lx * mps;

                    float d, roadH, flatHW, blendHW;
                    if (!NearestOnCorridor(c, segs, wx, wz, out d, out roadH, out flatHW, out blendHW)) { continue; }

                    // Beyond the blend edge the deformed height equals natural, so writing it would be a no-op; skip it
                    // so a road never marks a sample it did not actually move.
                    if (d >= blendHW) { continue; }

                    float natural = heights[lz, lx] * maxHeight;
                    float deformed = DeformedHeight(roadH, natural, d, flatHW, blendHW);

                    // Assigned, not accumulated, and both directions are allowed: a road in a cutting lowers the ground,
                    // one on an embankment raises it, and applying the same corridor twice gives the same answer.
                    heights[lz, lx] = Mathf.Clamp01(deformed * invMax);
                    changed++;
                }
            }

            return changed;
        }

        /// <summary> Nearest point on the corridor to a world position, and the road parameters interpolated there.
        /// Returns false only when there are no candidate segments. <paramref name="segs"/> is the prefiltered segment
        /// start-indices for the current chunk. </summary>
        private static bool NearestOnCorridor(TerrainCorridorSample[] c, List<int> segs, float px, float pz,
            out float distance, out float roadHeight, out float flattenedHalfWidth, out float blendOuterHalfWidth)
        {
            distance = 0f; roadHeight = 0f; flattenedHalfWidth = 0f; blendOuterHalfWidth = 0f;

            float bestSqr = float.MaxValue;
            int bestSeg = -1;
            float bestT = 0f;

            for (int k = 0; k < segs.Count; k++)
            {
                int i = segs[k];
                float ax = c[i].worldX, az = c[i].worldZ;
                float dx = c[i + 1].worldX - ax;
                float dz = c[i + 1].worldZ - az;

                float lenSqr = dx * dx + dz * dz;
                float t = lenSqr < 1e-8f ? 0f : Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / lenSqr);

                float ox = px - (ax + dx * t);
                float oz = pz - (az + dz * t);
                float sqr = ox * ox + oz * oz;

                if (sqr < bestSqr) { bestSqr = sqr; bestSeg = i; bestT = t; }
            }

            if (bestSeg < 0) { return false; }

            distance = Mathf.Sqrt(bestSqr);
            roadHeight = Mathf.Lerp(c[bestSeg].roadHeightMetres, c[bestSeg + 1].roadHeightMetres, bestT);
            flattenedHalfWidth = Mathf.Lerp(c[bestSeg].flattenedHalfWidthMetres,
                c[bestSeg + 1].flattenedHalfWidthMetres, bestT);
            blendOuterHalfWidth = Mathf.Lerp(c[bestSeg].blendOuterHalfWidthMetres,
                c[bestSeg + 1].blendOuterHalfWidthMetres, bestT);

            // The blend edge must be at least the flattened edge, or a station with the two set equal would divide by a
            // zero-width blend band; nudge it out by a sample so the smoothstep has somewhere to run.
            if (blendOuterHalfWidth < flattenedHalfWidth) { blendOuterHalfWidth = flattenedHalfWidth; }
            return true;
        }

        /// <summary> The deformed ground height at a lateral distance from the centreline: the road level flat out to the
        /// flattened half-width, then a smoothstep to natural ground by the blend edge, then natural beyond. The
        /// smoothstep 3t^2-2t^3 has zero slope at both ends, so the flat band and the natural ground are met tangentially
        /// -- no crease either side. UC's own copy of the curve; see the file header for why it is not shared. </summary>
        private static float DeformedHeight(float roadHeight, float naturalHeight, float lateralDistance,
            float flattenedHalfWidth, float blendOuterHalfWidth)
        {
            float frac = Smoothstep01(flattenedHalfWidth, blendOuterHalfWidth, Mathf.Abs(lateralDistance));
            return roadHeight + (naturalHeight - roadHeight) * frac;
        }

        // =====================================================================
        //  KEY ABUTMENTS  (a dam ties into its valley walls)
        // =====================================================================

        /// <summary> Fills the ground up into the two ends of a dam crest so the structure keys into the valley walls.
        /// The crest is a segment of crestLengthMetres centred on the dam and running along abutmentAxisHeadingDeg; its
        /// two ends are the abutments. Around each end the ground is raised to the crest level within an inner radius and
        /// smoothstepped back to natural by an outer radius. Fill only -- Max keeps whichever is higher, so a wall that
        /// already stands above the crest is left as it is and never cut. </summary>
        private static int ApplyAbutments(TerrainShapeRequest r, float[,] heights, Bounds chunk, float mps,
            float maxHeight, int resZ, int resX, Bounds fp)
        {
            float half = r.crestLengthMetres * 0.5f;
            if (half <= 0f || r.baseWidthMetres <= 0f) { return 0; }

            // Heading convention matches the request: 0 deg = +Z, 90 deg = +X.
            float rad = r.abutmentAxisHeadingDeg * Mathf.Deg2Rad;
            float dirX = Mathf.Sin(rad);
            float dirZ = Mathf.Cos(rad);

            float e1x = r.abutmentCentreX + dirX * half;
            float e1z = r.abutmentCentreZ + dirZ * half;
            float e2x = r.abutmentCentreX - dirX * half;
            float e2z = r.abutmentCentreZ - dirZ * half;

            // The embedment: raised to crest within half the base width of an end, blended to natural by a full base
            // width. Chosen so the farthest raised sample sits at crestLength/2 + baseWidth/2 from the dam centre, which
            // is exactly within the footprint the request reports for any crest at least as long as the base is wide --
            // so the fill is never clipped at a chunk boundary.
            float coreR = Mathf.Max(0.25f, r.baseWidthMetres * 0.25f);
            float outerR = Mathf.Max(coreR + mps, r.baseWidthMetres * 0.5f);

            float crest = r.crestLevelMetres;
            float invMax = 1f / maxHeight;
            int changed = 0;

            SampleWindow(fp, chunk, mps, resZ, resX, out int x0, out int x1, out int z0, out int z1);

            for (int lz = z0; lz <= z1; lz++)
            {
                float wz = chunk.min.z + lz * mps;
                for (int lx = x0; lx <= x1; lx++)
                {
                    float wx = chunk.min.x + lx * mps;

                    float d1x = wx - e1x, d1z = wz - e1z;
                    float d2x = wx - e2x, d2z = wz - e2z;
                    float dEnd = Mathf.Sqrt(Mathf.Min(d1x * d1x + d1z * d1z, d2x * d2x + d2z * d2z));

                    if (dEnd >= outerR) { continue; }

                    float natural = heights[lz, lx] * maxHeight;

                    // crest at the core, easing to natural by the outer radius. frac is 0 at the core (target = crest)
                    // and 1 at the outer edge (target = natural), so the fill fades to nothing rather than ending at a
                    // ring.
                    float frac = Smoothstep01(coreR, outerR, dEnd);
                    float target = Mathf.Lerp(crest, natural, frac);

                    float raised = Mathf.Max(natural, target);
                    if (raised <= natural) { continue; }

                    heights[lz, lx] = Mathf.Clamp01(raised * invMax);
                    changed++;
                }
            }

            return changed;
        }

        // =====================================================================
        //  SHARED
        // =====================================================================

        /// <summary> Whether a request footprint overlaps a chunk in the X/Z plane. The footprint's height axis is left
        /// deliberately wide by <see cref="TerrainShapeRequest.TryGetFootprint"/>, so only X and Z are meaningful; a
        /// road far below or above the chunk still shapes the ground beneath it. </summary>
        private static bool OverlapsXZ(Bounds footprint, Bounds chunk)
        {
            return !(footprint.min.x > chunk.max.x || footprint.max.x < chunk.min.x
                     || footprint.min.z > chunk.max.z || footprint.max.z < chunk.min.z);
        }

        /// <summary> The sample window this chunk shares with a request footprint, clamped to the heightfield. One place
        /// for the maths so the two appliers cannot drift apart on it. </summary>
        private static void SampleWindow(Bounds fp, Bounds chunk, float mps, int resZ, int resX,
            out int x0, out int x1, out int z0, out int z1)
        {
            float minX = Mathf.Max(chunk.min.x, fp.min.x);
            float maxX = Mathf.Min(chunk.max.x, fp.max.x);
            float minZ = Mathf.Max(chunk.min.z, fp.min.z);
            float maxZ = Mathf.Min(chunk.max.z, fp.max.z);

            x0 = Mathf.Max(0, Mathf.FloorToInt((minX - chunk.min.x) / mps));
            x1 = Mathf.Min(resX - 1, Mathf.CeilToInt((maxX - chunk.min.x) / mps));
            z0 = Mathf.Max(0, Mathf.FloorToInt((minZ - chunk.min.z) / mps));
            z1 = Mathf.Min(resZ - 1, Mathf.CeilToInt((maxZ - chunk.min.z) / mps));
        }

        /// <summary> Smoothstep between two edges: 0 at or below edge0, 1 at or above edge1, and 3t^2-2t^3 between, whose
        /// derivative is zero at both ends. A degenerate band (edge1 &lt;= edge0) snaps at edge0. </summary>
        private static float Smoothstep01(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) { return x < edge0 ? 0f : 1f; }
            float t = (x - edge0) / (edge1 - edge0);
            if (t <= 0f) { return 0f; }
            if (t >= 1f) { return 1f; }
            return t * t * (3f - 2f * t);
        }
    }
}
