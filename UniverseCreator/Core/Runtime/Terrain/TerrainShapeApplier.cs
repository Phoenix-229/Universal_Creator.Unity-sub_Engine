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
    /// heightfield. Normalised samples are relative to the terrain's world-space base elevation, while request
    /// elevations are world-space metres. Static and stateless, like <see cref="HeightGuard"/>: everything it needs
    /// arrives as arguments, so it can be called from ReapplyCarves and from a self test with equal ease. </summary>
    public static class TerrainShapeApplier
    {
        /// <summary> Folds every heightfield-shaping request that can reach this chunk into <paramref name="heights"/>,
        /// and returns how many samples were changed -- a number the self test and the inspector can read rather than a
        /// claim. Daylighting cuts and fills also process chunks within their maximum run implied by the terrain's
        /// vertical range, so slopes can continue across chunk seams. <paramref name="heights"/> is [z,x] normalised 0..1,
        /// the Unity convention; the world position of sample (lz,lx) is (chunkWorldBounds.min.x + lx*mps,
        /// chunkWorldBounds.min.z + lz*mps), the same mapping the water carve uses. </summary>
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
                // in it. A daylighting cut also reaches terrain beyond its authored footprint.
                bool cutToDaylight = r.kind == TerrainShapeKind.ConformCorridor
                    && r.useEarthworkProfile && r.cutToDaylight;
                bool fillToDaylight = r.kind == TerrainShapeKind.ConformCorridor
                    && r.useEarthworkProfile && r.fillToDaylight;
                bool overlapsFootprint = OverlapsXZ(fp, chunkWorldBounds);
                float chunkMaxHeight = 0f;
                if (!overlapsFootprint)
                {
                    bool cutMayReach = cutToDaylight
                        && CutMayReachChunk(r, chunkWorldBounds, chunkWorldBounds.max.y, mps);
                    bool fillMayReach = fillToDaylight && FillMayReachChunk(r, chunkWorldBounds);
                    if (!cutMayReach && !fillMayReach) { continue; }
                    if (cutMayReach)
                    {
                        chunkMaxHeight = MaximumHeightMetres(heights, maxHeightMetres, chunkWorldBounds.min.y);
                        cutMayReach = CutMayReachChunk(r, chunkWorldBounds, chunkMaxHeight, mps);
                        if (!cutMayReach && !fillMayReach) { continue; }
                    }
                }

                switch (r.kind)
                {
                    case TerrainShapeKind.ConformCorridor:
                        changed += ApplyCorridor(r, heights, chunkWorldBounds, mps, maxHeightMetres, resZ, resX,
                            cutToDaylight || fillToDaylight ? chunkWorldBounds : fp, chunkMaxHeight);
                        break;
                    case TerrainShapeKind.KeyAbutments:
                        changed += ApplyAbutments(r, heights, chunkWorldBounds, mps, maxHeightMetres, resZ, resX, fp);
                        break;
                    // RaiseWaterBody and CutOpening are not heightfield writes -- see the file header.
                }
            }

            return changed;
        }

        /// <summary> True if any request that this applier actually writes -- ConformCorridor or KeyAbutments -- can
        /// affect this chunk in plan. The rebuild uses it as a gate: it decides whether a chunk is worth re-deriving
        /// from pristine ground and whether its pristine snapshot must be kept, on the same terms the water carve's
        /// CountAffecting decides those for a river. Deliberately blind to RaiseWaterBody and CutOpening, which
        /// this applier never writes -- reporting them here would keep a chunk alive in the height rebuild for a change
        /// that a different pass makes, which is how a cell gets stuck rewriting itself forever. </summary>
        public static bool Affects(IReadOnlyList<TerrainShapeRequest> requests, Bounds chunkWorldBounds)
        {
            return Affects(requests, chunkWorldBounds, null, chunkWorldBounds.max.y,
                Mathf.Max(2f, chunkWorldBounds.size.x / 32f));
        }

        public static bool Affects(IReadOnlyList<TerrainShapeRequest> requests, Bounds chunkWorldBounds,
            float[,] heights, float maxHeightMetres, float metresPerSample)
        {
            if (requests == null || requests.Count == 0) { return false; }

            bool haveChunkMaxHeight = false;
            float chunkMaxHeight = 0f;
            for (int i = 0; i < requests.Count; i++)
            {
                TerrainShapeRequest r = requests[i];
                if (r == null || !r.HasWork) { continue; }
                if (r.kind != TerrainShapeKind.ConformCorridor && r.kind != TerrainShapeKind.KeyAbutments) { continue; }

                Bounds fp;
                if (!r.TryGetFootprint(out fp)) { continue; }
                bool cutToDaylight = r.kind == TerrainShapeKind.ConformCorridor
                    && r.useEarthworkProfile && r.cutToDaylight;
                if (OverlapsXZ(fp, chunkWorldBounds)) { return true; }
                if (r.kind == TerrainShapeKind.ConformCorridor && r.useEarthworkProfile
                    && r.fillToDaylight && FillMayReachChunk(r, chunkWorldBounds)) { return true; }
                if (cutToDaylight)
                {
                    if (!CutMayReachChunk(r, chunkWorldBounds, chunkWorldBounds.max.y, metresPerSample))
                    {
                        continue;
                    }
                    if (!haveChunkMaxHeight)
                    {
                        chunkMaxHeight = heights != null
                            ? MaximumHeightMetres(heights, maxHeightMetres, chunkWorldBounds.min.y)
                            : chunkWorldBounds.max.y;
                        haveChunkMaxHeight = true;
                    }
                    if (CutMayReachChunk(r, chunkWorldBounds, chunkMaxHeight, metresPerSample)) { return true; }
                }
            }

            return false;
        }

        // =====================================================================
        //  CONFORM CORRIDOR  (the shoulder/terrain fix)
        // =====================================================================

        /// <summary> Cut-and-fills the ground to a road corridor over the samples this chunk shares with the request's
        /// footprint or daylighting cut. For each sample it finds the nearest station on the centreline, reads the road
        /// height and the flattened/blend half-widths the tool interpolated there, and writes the road level flat inside
        /// the flattened half-width, a smoothstep blend to natural ground out to the blend edge, and nothing beyond.
        /// </summary>
        private static int ApplyCorridor(TerrainShapeRequest r, float[,] heights, Bounds chunk, float mps,
            float maxHeight, int resZ, int resX, Bounds fp, float chunkMaxHeight)
        {
            TerrainCorridorSample[] c = r.corridor;
            if (c == null || c.Length < 2) { return 0; }

            if (r.useEarthworkProfile)
            {
                return ApplyEarthworkCorridor(r, heights, chunk, mps, maxHeight, resZ, resX, fp, chunkMaxHeight);
            }

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
                    if (!NearestOnCorridor(c, segs, wx, wz, 0f, out d, out roadH, out flatHW, out blendHW,
                        out _, out _)) { continue; }

                    // Beyond the blend edge the deformed height equals natural, so writing it would be a no-op; skip it
                    // so a road never marks a sample it did not actually move.
                    if (d >= blendHW) { continue; }

                    float natural = chunk.min.y + (heights[lz, lx] * maxHeight);
                    float deformed = DeformedHeight(roadH, natural, d, flatHW, blendHW);

                    // Assigned, not accumulated, and both directions are allowed: a road in a cutting lowers the ground,
                    // one on an embankment raises it, and applying the same corridor twice gives the same answer.
                    heights[lz, lx] = Mathf.Clamp01((deformed - chunk.min.y) * invMax);
                    changed++;
                }
            }

            return changed;
        }

        private static int ApplyEarthworkCorridor(TerrainShapeRequest r, float[,] heights, Bounds chunk, float mps,
            float maxHeight, int resZ, int resX, Bounds fp, float chunkMaxHeight)
        {
            TerrainCorridorSample[] c = r.corridor;
            if (c == null || c.Length < 2) { return 0; }

            float endTaper = Mathf.Max(0f, r.endTaperMetres);
            float fillReach = Mathf.Max(0f, r.batterMaxReachMetres);
            if (r.fillToDaylight)
            {
                fillReach = Mathf.Max(fillReach, MaximumPotentialFillReach(r, chunk.min.y));
            }
            float cutRun = r.cutRunPerRise > 0f ? r.cutRunPerRise : 1.5f;
            float fillRun = r.fillRunPerRise > 0f ? r.fillRunPerRise : 2f;
            float cutGuard = Mathf.Max(0f, mps - Mathf.Max(0f, r.cutVergeMetres));
            float cutFlat = Mathf.Max(0f, r.cutFlatHalfWidthMetres);
            float fillFlat = Mathf.Max(0f, r.fillFlatHalfWidthMetres);
            float cutReach = r.cutToDaylight
                ? FindCutDaylightReach(c, heights, chunk, mps, maxHeight, cutFlat + cutGuard, cutRun)
                : 0f;
            if (r.cutToDaylight)
            {
                cutReach = Mathf.Max(cutReach, MaximumPotentialCutReach(r, chunkMaxHeight));
            }
            float profileOuter = Mathf.Max(cutFlat + cutGuard + cutReach, fillFlat + fillReach);

            float[] cumulativeDistance = new float[c.Length];
            for (int i = 1; i < c.Length; i++)
            {
                float dx = c[i].worldX - c[i - 1].worldX;
                float dz = c[i].worldZ - c[i - 1].worldZ;
                cumulativeDistance[i] = cumulativeDistance[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
            }
            float corridorLength = cumulativeDistance[cumulativeDistance.Length - 1];

            List<int> segs = null;
            for (int s = 0; s < c.Length - 1; s++)
            {
                float endPad = (s == 0 || s == c.Length - 2) ? endTaper : 0f;
                float pad = Mathf.Max(profileOuter,
                    Mathf.Max(c[s].blendOuterHalfWidthMetres, c[s + 1].blendOuterHalfWidthMetres))
                    + mps + endPad;
                float sMinX = Mathf.Min(c[s].worldX, c[s + 1].worldX) - pad;
                float sMaxX = Mathf.Max(c[s].worldX, c[s + 1].worldX) + pad;
                float sMinZ = Mathf.Min(c[s].worldZ, c[s + 1].worldZ) - pad;
                float sMaxZ = Mathf.Max(c[s].worldZ, c[s + 1].worldZ) + pad;
                if (sMinX > chunk.max.x || sMaxX < chunk.min.x || sMinZ > chunk.max.z || sMaxZ < chunk.min.z) { continue; }
                if (segs == null) { segs = new List<int>(8); }
                segs.Add(s);
            }
            if (segs == null) { return 0; }

            float invMax = 1f / maxHeight;
            float toeEase = Mathf.Max(0f, r.toeEaseMetres);
            float matchSub = Mathf.Max(0f, r.matchTerrainSubtractionMetres);

            Bounds workBounds = r.cutToDaylight || r.fillToDaylight
                ? CorridorBounds(c, profileOuter + endTaper, chunk)
                : fp;
            SampleWindow(workBounds, chunk, mps, resZ, resX, out int x0, out int x1, out int z0, out int z1);
            int changed = 0;

            for (int lz = z0; lz <= z1; lz++)
            {
                float wz = chunk.min.z + lz * mps;
                for (int lx = x0; lx <= x1; lx++)
                {
                    float wx = chunk.min.x + lx * mps;
                    float d, roadH, blendHW;
                    int nearestSegment;
                    float nearestT;
                    if (!NearestOnCorridor(c, segs, wx, wz, endTaper, out d, out roadH, out _,
                        out blendHW, out nearestSegment, out nearestT)) { continue; }
                    if (r.cutToDaylight || r.fillToDaylight) { blendHW = Mathf.Max(blendHW, profileOuter); }
                    if (d >= blendHW) { continue; }

                    float natural = chunk.min.y + (heights[lz, lx] * maxHeight);
                    float band = roadH;
                    float eCut = d - Mathf.Max(0f, r.cutFlatHalfWidthMetres) - cutGuard;
                    float eFill = d - Mathf.Max(0f, r.fillFlatHalfWidthMetres);
                    if (!r.fillToDaylight && natural < band && eFill > fillReach) { continue; }

                    float profileT = Mathf.Clamp01(nearestT);
                    int nearestStation = profileT < 0.5f ? nearestSegment : nearestSegment + 1;
                    if (c[nearestStation].earthworkExcluded) { continue; }
                    float sampleWeight = Mathf.Lerp(c[nearestSegment].earthworkWeight,
                        c[nearestSegment + 1].earthworkWeight, profileT);
                    if (sampleWeight <= 0f) { continue; }

                    float alongMetres = cumulativeDistance[nearestSegment];
                    float segmentX = c[nearestSegment + 1].worldX - c[nearestSegment].worldX;
                    float segmentZ = c[nearestSegment + 1].worldZ - c[nearestSegment].worldZ;
                    float segmentLength = Mathf.Sqrt(segmentX * segmentX + segmentZ * segmentZ);
                    alongMetres += segmentLength * nearestT;
                    if (endTaper > 0f && corridorLength > 0f)
                    {
                        float overshoot = alongMetres < 0f ? -alongMetres
                            : (alongMetres > corridorLength ? alongMetres - corridorLength : 0f);
                        if (overshoot > 0f)
                        {
                            sampleWeight *= Mathf.Clamp01(1f - (overshoot / endTaper));
                        }
                    }
                    if (sampleWeight <= 0f) { continue; }

                    float target = (float)TerrainEarthworkProfile.SurfaceMetres(
                        natural, band, eCut, eFill, cutRun, fillRun, toeEase);
                    float deformed = natural + ((target - natural) * sampleWeight);
                    float raw = deformed;
                    deformed -= matchSub;
                    if (raw > natural && deformed < natural) { deformed = natural; }
                    if (Mathf.Abs(deformed - natural) < 0.0005f) { continue; }

                    heights[lz, lx] = Mathf.Clamp01((deformed - chunk.min.y) * invMax);
                    changed++;
                }
            }

            return changed;
        }

        private static float FindCutDaylightReach(TerrainCorridorSample[] corridor, float[,] heights, Bounds chunk,
            float mps, float maxHeight, float cutEdgeHalfWidth, float cutRun)
        {
            float maximumReach = 0f;
            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            float step = Mathf.Max(1e-4f, mps);

            for (int i = 0; i < corridor.Length; i++)
            {
                TerrainCorridorSample station = corridor[i];
                if (station.earthworkExcluded || station.earthworkWeight <= 0f) { continue; }

                int adjacent = i + 1 < corridor.Length ? i + 1 : i - 1;
                float dx = corridor[adjacent].worldX - station.worldX;
                float dz = corridor[adjacent].worldZ - station.worldZ;
                float length = Mathf.Sqrt(dx * dx + dz * dz);
                if (length < 1e-4f) { continue; }
                float normalX = -dz / length;
                float normalZ = dx / length;

                for (int side = -1; side <= 1; side += 2)
                {
                    float edgeX = station.worldX + normalX * side * cutEdgeHalfWidth;
                    float edgeZ = station.worldZ + normalZ * side * cutEdgeHalfWidth;
                    float directionX = normalX * side;
                    float directionZ = normalZ * side;
                    if (edgeX < chunk.min.x || edgeX > chunk.max.x
                        || edgeZ < chunk.min.z || edgeZ > chunk.max.z) { continue; }
                    float available = DistanceToChunkEdge(edgeX, edgeZ, directionX, directionZ, chunk);
                    if (available <= 0f) { continue; }

                    bool reachedNatural = false;
                    for (float run = 0f; run <= available; run += step)
                    {
                        float worldX = edgeX + directionX * run;
                        float worldZ = edgeZ + directionZ * run;
                        int x = Mathf.RoundToInt((worldX - chunk.min.x) / step);
                        int z = Mathf.RoundToInt((worldZ - chunk.min.z) / step);
                        if (x < 0 || x >= resX || z < 0 || z >= resZ) { break; }

                        float natural = chunk.min.y + (heights[z, x] * maxHeight);
                        float cutLine = station.roadHeightMetres + (run / cutRun);
                        if (natural <= cutLine + 0.001f)
                        {
                            maximumReach = Mathf.Max(maximumReach, run);
                            reachedNatural = true;
                            break;
                        }
                    }

                    if (!reachedNatural) { maximumReach = Mathf.Max(maximumReach, available); }
                }
            }

            return maximumReach;
        }

        private static float DistanceToChunkEdge(float x, float z, float directionX, float directionZ, Bounds chunk)
        {
            float reach = float.PositiveInfinity;
            if (directionX > 1e-6f) { reach = Mathf.Min(reach, (chunk.max.x - x) / directionX); }
            else if (directionX < -1e-6f) { reach = Mathf.Min(reach, (chunk.min.x - x) / directionX); }
            if (directionZ > 1e-6f) { reach = Mathf.Min(reach, (chunk.max.z - z) / directionZ); }
            else if (directionZ < -1e-6f) { reach = Mathf.Min(reach, (chunk.min.z - z) / directionZ); }
            return float.IsInfinity(reach) ? 0f : Mathf.Max(0f, reach);
        }

        private static bool CutMayReachChunk(TerrainShapeRequest request, Bounds chunk, float maxTerrainHeight,
            float metresPerSample)
        {
            float reach = MaximumPotentialCutReach(request, maxTerrainHeight);
            if (reach <= 0f) { return false; }

            float cutFlat = Mathf.Max(0f, request.cutFlatHalfWidthMetres);
            float cutGuard = Mathf.Max(0f, metresPerSample - Mathf.Max(0f, request.cutVergeMetres));
            float pad = cutFlat + cutGuard + reach + Mathf.Max(0f, request.endTaperMetres);
            return OverlapsXZ(CorridorBounds(request.corridor, pad, chunk), chunk);
        }

        private static bool FillMayReachChunk(TerrainShapeRequest request, Bounds chunk)
        {
            float fillFlat = Mathf.Max(0f, request.fillFlatHalfWidthMetres);
            float reach = MaximumPotentialFillReach(request, chunk.min.y);
            float pad = fillFlat + reach + Mathf.Max(0f, request.endTaperMetres);
            return OverlapsXZ(CorridorBounds(request.corridor, pad, chunk), chunk);
        }

        private static float MaximumHeightMetres(float[,] heights, float maxHeight, float baseElevationMetres)
        {
            float maxNormalised = 0f;
            for (int z = 0; z < heights.GetLength(0); z++)
            {
                for (int x = 0; x < heights.GetLength(1); x++)
                {
                    maxNormalised = Mathf.Max(maxNormalised, heights[z, x]);
                }
            }
            return baseElevationMetres + (maxNormalised * maxHeight);
        }

        private static float MaximumPotentialCutReach(TerrainShapeRequest request, float maxTerrainHeight)
        {
            if (request == null || request.corridor == null || request.corridor.Length == 0) { return 0f; }

            float lowestRoadHeight = float.MaxValue;
            for (int i = 0; i < request.corridor.Length; i++)
            {
                TerrainCorridorSample station = request.corridor[i];
                if (station.earthworkExcluded || station.earthworkWeight <= 0f) { continue; }
                lowestRoadHeight = Mathf.Min(lowestRoadHeight, station.roadHeightMetres);
            }
            if (lowestRoadHeight == float.MaxValue) { return 0f; }

            float rise = Mathf.Max(0f, maxTerrainHeight - lowestRoadHeight);
            float cutRun = request.cutRunPerRise > 0f ? request.cutRunPerRise : 1.5f;
            return rise * cutRun;
        }

        private static float MaximumPotentialFillReach(TerrainShapeRequest request, float terrainBaseElevationMetres)
        {
            if (request == null || request.corridor == null || request.corridor.Length == 0) { return 0f; }

            float highestRoadHeight = float.MinValue;
            for (int i = 0; i < request.corridor.Length; i++)
            {
                TerrainCorridorSample station = request.corridor[i];
                if (station.earthworkExcluded || station.earthworkWeight <= 0f) { continue; }
                highestRoadHeight = Mathf.Max(highestRoadHeight, station.roadHeightMetres);
            }
            if (highestRoadHeight == float.MinValue) { return 0f; }

            float rise = Mathf.Max(0f, highestRoadHeight - terrainBaseElevationMetres);
            float fillRun = request.fillRunPerRise > 0f ? request.fillRunPerRise : 2f;
            return rise * fillRun;
        }

        private static Bounds CorridorBounds(TerrainCorridorSample[] corridor, float padding, Bounds chunk)
        {
            float minX = corridor[0].worldX, maxX = corridor[0].worldX;
            float minZ = corridor[0].worldZ, maxZ = corridor[0].worldZ;
            for (int i = 1; i < corridor.Length; i++)
            {
                minX = Mathf.Min(minX, corridor[i].worldX);
                maxX = Mathf.Max(maxX, corridor[i].worldX);
                minZ = Mathf.Min(minZ, corridor[i].worldZ);
                maxZ = Mathf.Max(maxZ, corridor[i].worldZ);
            }
            minX -= padding;
            maxX += padding;
            minZ -= padding;
            maxZ += padding;
            return new Bounds(
                new Vector3((minX + maxX) * 0.5f, chunk.center.y, (minZ + maxZ) * 0.5f),
                new Vector3(maxX - minX, chunk.size.y, maxZ - minZ));
        }

        /// <summary> Nearest point on the corridor to a world position, and the road parameters interpolated there.
        /// Returns false only when there are no candidate segments. <paramref name="segs"/> is the prefiltered segment
        /// start-indices for the current chunk. </summary>
        private static bool NearestOnCorridor(TerrainCorridorSample[] c, List<int> segs, float px, float pz,
            float endExtensionMetres, out float distance, out float roadHeight, out float flattenedHalfWidth,
            out float blendOuterHalfWidth, out int nearestSegment, out float nearestT)
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
                float t = lenSqr < 1e-8f ? 0f : ((px - ax) * dx + (pz - az) * dz) / lenSqr;
                if (i == 0 && endExtensionMetres > 0f && lenSqr >= 1e-8f)
                {
                    t = Mathf.Max(t, -endExtensionMetres / Mathf.Sqrt(lenSqr));
                }
                else { t = Mathf.Max(0f, t); }
                if (i == c.Length - 2 && endExtensionMetres > 0f && lenSqr >= 1e-8f)
                {
                    t = Mathf.Min(t, 1f + (endExtensionMetres / Mathf.Sqrt(lenSqr)));
                }
                else { t = Mathf.Min(1f, t); }

                float ox = px - (ax + dx * t);
                float oz = pz - (az + dz * t);
                float sqr = ox * ox + oz * oz;

                if (sqr < bestSqr) { bestSqr = sqr; bestSeg = i; bestT = t; }
            }

            if (bestSeg < 0) { nearestSegment = -1; nearestT = 0f; return false; }

            distance = Mathf.Sqrt(bestSqr);
            nearestSegment = bestSeg;
            nearestT = bestT;
            float parameterT = Mathf.Clamp01(bestT);
            roadHeight = Mathf.Lerp(c[bestSeg].roadHeightMetres, c[bestSeg + 1].roadHeightMetres, parameterT);
            flattenedHalfWidth = Mathf.Lerp(c[bestSeg].flattenedHalfWidthMetres,
                c[bestSeg + 1].flattenedHalfWidthMetres, parameterT);
            blendOuterHalfWidth = Mathf.Lerp(c[bestSeg].blendOuterHalfWidthMetres,
                c[bestSeg + 1].blendOuterHalfWidthMetres, parameterT);

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
            float baseElevation = chunk.min.y;
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

                    float natural = baseElevation + (heights[lz, lx] * maxHeight);

                    // crest at the core, easing to natural by the outer radius. frac is 0 at the core (target = crest)
                    // and 1 at the outer edge (target = natural), so the fill fades to nothing rather than ending at a
                    // ring.
                    float frac = Smoothstep01(coreR, outerR, dEnd);
                    float target = Mathf.Lerp(crest, natural, frac);

                    float raised = Mathf.Max(natural, target);
                    if (raised <= natural) { continue; }

                    heights[lz, lx] = Mathf.Clamp01((raised - baseElevation) * invMax);
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
