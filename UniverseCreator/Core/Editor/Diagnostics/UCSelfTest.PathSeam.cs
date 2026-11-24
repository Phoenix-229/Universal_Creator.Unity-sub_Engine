// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  Module 6: Path Tool Integration (the seam)
//
//  File:    UCSelfTest.PathSeam.cs
//  Purpose: Asserts the maths of TerrainShapeApplier -- the pass that folds a path
//           tool's ConformCorridor and KeyAbutments requests into a chunk's
//           heightfield. A partial of UCSelfTest, so these land in the same tally
//           and the same report and share Check / CheckClose / FlatField.
//
//  WHAT THESE CHECKS ARE FOR
//  -------------------------
//  Not "does it compile" -- the compiler covers that. These pin the behaviour a
//  maintainer could plausibly "tidy" into something wrong and never notice until a
//  world is built:
//    * that a corridor CUTS below and FILLS above natural ground (the whole reason
//      it is modelled on the override layer, not the water carve's lower-only Min);
//    * that the flat carriageway band is held level and the batter blends without a
//      step, and that a sample past the blend edge is left untouched;
//    * that the write is clamped 0..1 inside the applier, not left for HeightGuard;
//    * that an abutment key FILLS ONLY -- it ties a dam's ends into the walls and
//      never digs a wall that already stands above the crest;
//    * that Affects, the chunk keep-alive gate, is blind to the kinds this applier
//      does not write (RaiseWaterBody, CutOpening), or a cell would rebuild forever
//      for a change a different pass makes.
//
//  The world under test is a 17x17 chunk at 1 m per sample spanning 0..16 m in X
//  and Z, 100 m tall, natural ground a flat 0.50 (= 50 m). With min at the origin
//  the sample (lz,lx) sits at world (lx, lz), which keeps the geometry legible:
//  a road down the middle row (z = 8) is exactly |lz - 8| metres from any sample.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Integration;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestPathSeamShaping()
        {
            const int res = 17;
            const float mps = 1f;
            const float maxH = 100f;
            Bounds chunk = new Bounds(new Vector3(8f, maxH * 0.5f, 8f),
                                      new Vector3((res - 1) * mps, maxH, (res - 1) * mps));

            // =============================================================
            //  ConformCorridor: cut, flat band, blend, embankment, clamp
            // =============================================================

            // A straight road down the middle row (z = 8), full width in X. Road held
            // to 30 m -- a CUT into the 50 m ground -- with a 3 m flat half-width and a
            // 6 m blend edge. Distance from the centreline is |lz - 8| for any sample.
            var cutReqs = new List<TerrainShapeRequest> { Corridor(30f, 3f, 6f) };

            float[,] h = FlatField(res, 0.5f);
            int changed = TerrainShapeApplier.ApplyAll(cutReqs, h, chunk, mps, maxH);

            Check(changed > 0, "ConformCorridor changed no samples on a chunk its footprint covers.");

            // Centreline pulled to road level, and that level is below natural: a cut.
            CheckClose(h[8, 8], 0.30f, "corridor centreline did not sit at road level");
            Check(h[8, 8] < 0.5f - 1e-4f, "corridor did not CUT: centreline is not below natural ground.");

            // The flat carriageway band (|d| <= 3 m -> lz 6..10) is held at road level.
            CheckClose(h[6, 8], 0.30f, "corridor flat band (near edge) is not held level");
            CheckClose(h[10, 8], 0.30f, "corridor flat band (far edge) is not held level");

            // A batter sample (lz = 4, d = 4 m) blends strictly between road and natural.
            Check(h[4, 8] > 0.30f + 1e-4f && h[4, 8] < 0.50f - 1e-4f,
                  "corridor blend at d = 4 m is not between road (0.30) and natural (0.50), got " + h[4, 8]);

            // Past the blend edge (lz = 0, d = 8 m) the ground is untouched.
            CheckClose(h[0, 8], 0.50f, "corridor changed ground beyond the blend edge");

            // Re-applying in place must not drift the flat band: it is assigned to road
            // level, not accumulated, so the band is a fixpoint. (The blend zone is not a
            // fixpoint under re-feed by design and is deliberately not asserted here; in
            // production every apply runs on ground re-derived from the pristine snapshot.)
            TerrainShapeApplier.ApplyAll(cutReqs, h, chunk, mps, maxH);
            CheckClose(h[8, 8], 0.30f, "corridor centreline drifted on re-apply (accumulating, not assigning)");
            CheckClose(h[6, 8], 0.30f, "corridor flat band drifted on re-apply (accumulating, not assigning)");

            // Embankment: the same road raised to 80 m FILLS UP over the 50 m ground.
            float[,] he = FlatField(res, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { Corridor(80f, 3f, 6f) }, he, chunk, mps, maxH);
            Check(he[8, 8] > 0.5f + 1e-4f, "corridor did not FILL: embankment centreline is not above natural ground.");
            CheckClose(he[8, 8], 0.80f, "corridor embankment did not reach road level");

            // A road above the world's max height lands at 1.0, clamped inside the applier
            // -- not left past the top of the range for HeightGuard to catch.
            float[,] hc = FlatField(res, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { Corridor(150f, 3f, 6f) }, hc, chunk, mps, maxH);
            CheckClose(hc[8, 8], 1f, "corridor above max height was not clamped to 1.0");

            // =============================================================
            //  KeyAbutments: fill only, at the crest ends
            // =============================================================

            // A crest 8 m long on the +X axis, centred at (8,8): its two ends sit at
            // x = 4 and x = 12. baseWidth 6 m -> filled within 3 m of each end.
            var keyReqs = new List<TerrainShapeRequest> { Abutments(crest: 80f, crestLen: 8f, baseW: 6f) };

            float[,] ha = FlatField(res, 0.5f);
            int keyed = TerrainShapeApplier.ApplyAll(keyReqs, ha, chunk, mps, maxH);

            Check(keyed > 0, "KeyAbutments filled no ground at the crest ends.");
            CheckClose(ha[8, 12], 0.80f, "abutment end (x = 12) was not keyed up to crest level");
            CheckClose(ha[8, 4], 0.80f, "abutment end (x = 4) was not keyed up to crest level");

            // The MIDDLE of the crest (x = 8) is 4 m from either end, past the fill
            // radius, so it is left at natural: the key ties in the ends, it does not
            // raise the whole line.
            CheckClose(ha[8, 8], 0.50f, "KeyAbutments raised the crest middle, not just the ends");

            // FILL ONLY: over ground already above the crest (90 m > 80 m) the key cuts
            // nothing -- Max keeps the higher of natural and target.
            float[,] hi = FlatField(res, 0.9f);
            TerrainShapeApplier.ApplyAll(keyReqs, hi, chunk, mps, maxH);
            CheckClose(hi[8, 12], 0.90f, "KeyAbutments CUT ground that already stood above the crest");

            // =============================================================
            //  Affects: the chunk keep-alive gate
            // =============================================================

            Check(TerrainShapeApplier.Affects(cutReqs, chunk),
                  "Affects missed a corridor whose footprint covers the chunk.");

            // The same corridor 10 km away must not keep this chunk alive, and applying
            // it must change nothing here.
            var far = new List<TerrainShapeRequest> { Corridor(30f, 3f, 6f, offsetX: 10000f, offsetZ: 10000f) };
            Check(!TerrainShapeApplier.Affects(far, chunk), "Affects kept a chunk alive for a corridor 10 km away.");
            float[,] hf = FlatField(res, 0.5f);
            Check(TerrainShapeApplier.ApplyAll(far, hf, chunk, mps, maxH) == 0,
                  "a corridor that misses the chunk still changed samples.");

            // Affects is deliberately blind to the kinds this applier never writes, even
            // when they overlap: reporting them would keep the cell rebuilding for a
            // change the water system (RaiseWaterBody) or the tunnel path (CutOpening) makes.
            var raise = new List<TerrainShapeRequest>
            {
                new TerrainShapeRequest
                {
                    kind = TerrainShapeKind.RaiseWaterBody,
                    waterSurfaceLevelMetres = 40f,
                    reservoirMinX = 0f, reservoirMaxX = 16f, reservoirMinZ = 0f, reservoirMaxZ = 16f,
                }
            };
            Check(!TerrainShapeApplier.Affects(raise, chunk),
                  "Affects reported a RaiseWaterBody request, which this applier does not write.");

            var hole = new List<TerrainShapeRequest>
            {
                new TerrainShapeRequest
                {
                    kind = TerrainShapeKind.CutOpening,
                    opening = new Bounds(new Vector3(8f, 0f, 8f), new Vector3(4f, 10f, 4f)),
                }
            };
            Check(!TerrainShapeApplier.Affects(hole, chunk),
                  "Affects reported a CutOpening request, which the tunnel path handles, not this applier.");

            TestEarthworkCorridorProfile();
        }

        private static void TestEarthworkCorridorProfile()
        {
            const int resZ = 17;
            const int resX = 25;
            const float mps = 1f;
            const float maxH = 100f;
            Bounds chunk = new Bounds(new Vector3(9f, maxH * 0.5f, 8f), new Vector3(24f, maxH, 16f));
            TerrainShapeRequest cut = new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                useEarthworkProfile = true,
                corridor = new[]
                {
                    EarthworkSample(0f, 30f),
                    EarthworkSample(8f, 30f),
                    EarthworkSample(16f, 30f),
                },
                cutFlatHalfWidthMetres = 2.6f,
                fillFlatHalfWidthMetres = 3f,
                cutVergeMetres = 0.6f,
                cutRunPerRise = 1.5f,
                fillRunPerRise = 2f,
                batterMaxReachMetres = 6f,
                endTaperMetres = 2f,
            };
            float[,] cutHeights = EarthworkField(resZ, resX, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { cut }, cutHeights, chunk, mps, maxH);

            CheckClose(cutHeights[5, 11], 0.30f,
                "earthwork cut omitted the one-cell guard beyond its authored verge");
            CheckClose(cutHeights[8, 20], 0.40f,
                "earthwork end taper did not fade to natural ground beyond the road end");
            CheckClose(cutHeights[8, 21], 0.50f,
                "earthwork end taper changed ground beyond its reach");

            TerrainShapeRequest fill = new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                useEarthworkProfile = true,
                corridor = new[]
                {
                    EarthworkSample(0f, 70f),
                    EarthworkSample(8f, 70f),
                    EarthworkSample(16f, 70f),
                },
                cutFlatHalfWidthMetres = 2.6f,
                fillFlatHalfWidthMetres = 3f,
                cutVergeMetres = 0.6f,
                cutRunPerRise = 1.5f,
                fillRunPerRise = 2f,
                batterMaxReachMetres = 3f,
                cutToDaylight = true,
            };
            float[,] fillHeights = EarthworkField(resZ, resX, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { fill }, fillHeights, chunk, mps, maxH);
            CheckClose(fillHeights[14, 11], 0.685f,
                "earthwork embankment did not follow its 2:1 fill slope");
            CheckClose(fillHeights[16, 11], 0.50f,
                "cut daylighting extended the embankment beyond its authored fill reach");

            Bounds deepChunk = new Bounds(new Vector3(40f, maxH * 0.5f, 40f), new Vector3(80f, maxH, 80f));
            TerrainShapeRequest deepCut = new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                useEarthworkProfile = true,
                cutToDaylight = true,
                corridor = new[]
                {
                    EarthworkSample(0f, 40f, 30f),
                    EarthworkSample(8f, 40f, 30f),
                    EarthworkSample(16f, 40f, 30f),
                },
                cutFlatHalfWidthMetres = 2.6f,
                fillFlatHalfWidthMetres = 3f,
                cutVergeMetres = 0.6f,
                cutRunPerRise = 1.5f,
                fillRunPerRise = 2f,
                batterMaxReachMetres = 25f,
            };
            float[,] deepCutHeights = EarthworkField(81, 81, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { deepCut },
                deepCutHeights, deepChunk, 1f, 100f);
            CheckClose(deepCutHeights[71, 16], 0.4866667f,
                "a cut needing more than its authored 25 m reach did not continue along the 1.5:1 profile");
            CheckClose(deepCutHeights[73, 16], 0.50f,
                "the extended cut did not daylight back to the original terrain");

            Bounds adjacentCutChunk = new Bounds(new Vector3(20f, 50f, 110f),
                new Vector3(80f, 100f, 80f));
            float[,] adjacentCutHeights = EarthworkField(81, 81, 0.5f);
            Check(TerrainShapeApplier.Affects(new List<TerrainShapeRequest> { deepCut }, adjacentCutChunk,
                    adjacentCutHeights, 100f, 1f),
                "a daylighting cut did not keep an adjacent chunk beyond the authored fill reach alive");
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { deepCut },
                adjacentCutHeights, adjacentCutChunk, 1f, 100f);
            CheckClose(adjacentCutHeights[0, 28], 0.48f,
                "a daylighting cut stopped at a connected terrain chunk seam");

            cut.corridor[1].earthworkExcluded = true;
            cut.corridor[1].earthworkWeight = 0f;
            float[,] tunnelHeights = EarthworkField(resZ, resX, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { cut }, tunnelHeights, chunk, mps, maxH);
            CheckClose(tunnelHeights[8, 11], 0.50f,
                "earthwork profile modified a tunnel-excluded corridor station");

            Bounds elevatedTerrain = new Bounds(new Vector3(9f, 250f, 8f), new Vector3(24f, 100f, 16f));
            TerrainShapeRequest elevatedCut = new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                useEarthworkProfile = true,
                corridor = new[]
                {
                    EarthworkSample(0f, 240f),
                    EarthworkSample(8f, 240f),
                    EarthworkSample(16f, 240f),
                },
                cutFlatHalfWidthMetres = 2.6f,
                fillFlatHalfWidthMetres = 3f,
                cutVergeMetres = 0.6f,
                cutRunPerRise = 1.5f,
                fillRunPerRise = 2f,
                batterMaxReachMetres = 10f,
            };
            float[,] elevatedCutHeights = EarthworkField(resZ, resX, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { elevatedCut },
                elevatedCutHeights, elevatedTerrain, mps, maxH);
            CheckClose(elevatedCutHeights[8, 8], 0.40f,
                "earthwork cut did not match the road's world elevation on terrain with a non-zero base");
            CheckClose(elevatedCutHeights[13, 8], 0.4133333f,
                "earthwork cut batter did not use world elevation on terrain with a non-zero base");

            elevatedCut.corridor[1].earthworkExcluded = true;
            elevatedCut.corridor[1].earthworkWeight = 0f;
            float[,] elevatedTunnelHeights = EarthworkField(resZ, resX, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { elevatedCut },
                elevatedTunnelHeights, elevatedTerrain, mps, maxH);
            CheckClose(elevatedTunnelHeights[8, 8], 0.50f,
                "tunnel-excluded road terrain was changed on terrain with a non-zero base elevation");

            TerrainShapeRequest elevatedFill = new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                useEarthworkProfile = true,
                corridor = new[]
                {
                    EarthworkSample(0f, 260f),
                    EarthworkSample(8f, 260f),
                    EarthworkSample(16f, 260f),
                },
                cutFlatHalfWidthMetres = 2.6f,
                fillFlatHalfWidthMetres = 3f,
                cutVergeMetres = 0.6f,
                cutRunPerRise = 1.5f,
                fillRunPerRise = 2f,
                batterMaxReachMetres = 10f,
            };
            float[,] elevatedFillHeights = EarthworkField(resZ, resX, 0.5f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { elevatedFill },
                elevatedFillHeights, elevatedTerrain, mps, maxH);
            CheckClose(elevatedFillHeights[8, 8], 0.60f,
                "earthwork embankment did not hold the road level on terrain with a non-zero base");
            CheckClose(elevatedFillHeights[13, 8], 0.59f,
                "earthwork embankment batter did not use world elevation on terrain with a non-zero base");

            TerrainShapeRequest daylightFill = new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                useEarthworkProfile = true,
                fillToDaylight = true,
                corridor = new[]
                {
                    EarthworkSample(0f, 80f, 260f),
                    EarthworkSample(8f, 80f, 260f),
                    EarthworkSample(16f, 80f, 260f),
                },
                cutFlatHalfWidthMetres = 2.6f,
                fillFlatHalfWidthMetres = 3f,
                cutVergeMetres = 0.6f,
                cutRunPerRise = 1.5f,
                fillRunPerRise = 2f,
                batterMaxReachMetres = 25f,
            };
            Bounds remoteFillChunk = new Bounds(new Vector3(8f, 250f, 138f),
                new Vector3(16f, 100f, 32f));
            float[,] remoteFillHeights = EarthworkField(33, 17, 0.30f);
            Check(TerrainShapeApplier.Affects(new List<TerrainShapeRequest> { daylightFill }, remoteFillChunk,
                    remoteFillHeights, 100f, 1f),
                "a fill needing more than its authored 25 m reach did not keep the remote chunk alive");
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { daylightFill },
                remoteFillHeights, remoteFillChunk, 1f, 100f);
            CheckClose(remoteFillHeights[0, 8], 0.405f,
                "a daylighting embankment stopped at its authored 25 m reach instead of meeting natural terrain");
            CheckClose(remoteFillHeights[21, 8], 0.30f,
                "a daylighting embankment changed terrain beyond its natural-ground daylight point");

            daylightFill.corridor[1].earthworkExcluded = true;
            daylightFill.corridor[1].earthworkWeight = 0f;
            float[,] tunnelFillHeights = EarthworkField(33, 17, 0.30f);
            TerrainShapeApplier.ApplyAll(new List<TerrainShapeRequest> { daylightFill },
                tunnelFillHeights, remoteFillChunk, 1f, 100f);
            CheckClose(tunnelFillHeights[0, 8], 0.30f,
                "fill daylighting modified a tunnel-excluded corridor station");
        }

        private static TerrainCorridorSample EarthworkSample(float worldX, float roadHeight)
        {
            return EarthworkSample(worldX, 8f, roadHeight);
        }

        private static TerrainCorridorSample EarthworkSample(float worldX, float worldZ, float roadHeight)
        {
            return new TerrainCorridorSample
            {
                worldX = worldX,
                worldZ = worldZ,
                roadHeightMetres = roadHeight,
                flattenedHalfWidthMetres = 3f,
                blendOuterHalfWidthMetres = 9f,
                earthworkWeight = 1f,
            };
        }

        private static float[,] EarthworkField(int resZ, int resX, float value)
        {
            float[,] field = new float[resZ, resX];
            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++) { field[z, x] = value; }
            }
            return field;
        }

        /// <summary> A straight two-station corridor down the middle row (world z = 8), running the full width of the
        /// test chunk in X, at the given road height and half-widths. <paramref name="offsetX"/>/<paramref name="offsetZ"/>
        /// move the whole road away, to build one that misses the chunk. </summary>
        private static TerrainShapeRequest Corridor(float roadMetres, float flatHalfWidth, float blendHalfWidth,
            float offsetX = 0f, float offsetZ = 0f)
        {
            return new TerrainShapeRequest
            {
                kind = TerrainShapeKind.ConformCorridor,
                corridor = new[]
                {
                    new TerrainCorridorSample
                    {
                        worldX = 0f + offsetX, worldZ = 8f + offsetZ, roadHeightMetres = roadMetres,
                        flattenedHalfWidthMetres = flatHalfWidth, blendOuterHalfWidthMetres = blendHalfWidth,
                    },
                    new TerrainCorridorSample
                    {
                        worldX = 16f + offsetX, worldZ = 8f + offsetZ, roadHeightMetres = roadMetres,
                        flattenedHalfWidthMetres = flatHalfWidth, blendOuterHalfWidthMetres = blendHalfWidth,
                    },
                },
            };
        }

        /// <summary> A dam-abutment key: a crest of <paramref name="crestLen"/> metres on the +X axis centred at (8,8),
        /// filled up to <paramref name="crest"/> metres at each end over a base <paramref name="baseW"/> metres wide. </summary>
        private static TerrainShapeRequest Abutments(float crest, float crestLen, float baseW)
        {
            return new TerrainShapeRequest
            {
                kind = TerrainShapeKind.KeyAbutments,
                crestLevelMetres = crest,
                crestLengthMetres = crestLen,
                crestWidthMetres = 4f,
                baseWidthMetres = baseW,
                abutmentCentreX = 8f,
                abutmentCentreZ = 8f,
                abutmentAxisHeadingDeg = 90f, // 0 = +Z, 90 = +X: the crest runs along X
            };
        }
    }
}
