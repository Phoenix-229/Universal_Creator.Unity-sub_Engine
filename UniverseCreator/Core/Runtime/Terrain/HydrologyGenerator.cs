using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem
{
    public static class HydrologyGenerator
    {
        // The "nothing to build - targets already met" line is genuine the first time
        // it explains why a Generate produced no water, but the edit-mode chunk driver
        // pumps GenerateFromNode on a loop, so left ungated it reprints on every pump
        // and floods the console. Remember the last thing said for each node and only
        // speak when it changes, so the explanation appears once per distinct state
        // and not once per frame.
        [System.NonSerialized]
        private static readonly Dictionary<string, string> s_lastNothingToBuild =
            new Dictionary<string, string>();

        // onComplete is the CONTINUATION the editor's settle path passes so the lake
        // spawn can be spread ONE basin per pump (spec §15: one body at a time, no
        // synchronous all-at-once flood that spikes RAM/CPU and freezes the editor).
        // When it is null the whole generate runs synchronously in this call - the
        // self-test and any direct caller then see every lake the instant Generate
        // returns. When it is non-null the ocean is spawned now, the basins are
        // ranked now, and each lake is then built on its own after-settle step; the
        // continuation fires once, after the LAST lake, so the caller can fold the
        // digs and reconcile the beach only when the water is all standing. Every
        // early-out fires it too, so those follow-on steps always run.
        public static void GenerateFromNode(UniverseCreatorObject uc, HydrologyNode node,
                                            System.Action onComplete = null)
        {
            if (uc == null || node == null) { if (onComplete != null) onComplete(); return; }

            Transform waterRoot = uc.transform.Find("WaterSystem");
            if (waterRoot == null)
            {
                GameObject rootGo = new GameObject("WaterSystem");
                rootGo.transform.SetParent(uc.transform);
                rootGo.transform.localPosition = Vector3.zero;
                waterRoot = rootGo.transform;
            }

            // Gather (not just count) the generator's own lakes so the excess can be
            // culled. A body is treated as procedural ONLY when its name matches this
            // generator's scheme ("Lake 12"); a hand-placed body is "Lake" or, when
            // duplicated, "Lake (1)", so IsGeneratedLakeName rejects it and the user's
            // own lakes are never destroyed. Before this, the generator only ADDED
            // lakes (spawn = desired - current, then bailed when that went negative),
            // so a world first built by an older high-density build kept its saved
            // lakes forever - lowering the percentage could never bring the count down.
            List<UCWaterBody> proceduralLakes = new List<UCWaterBody>();
            // The generator owns at most ONE ocean, named "Ocean <n>", gathered the same
            // name-based way as lakes so a hand-placed "Ocean" (or its "Ocean (1)" de-dupe)
            // is never a candidate for the ocean reconcile below. (T10)
            List<UCWaterBody> proceduralOceans = new List<UCWaterBody>();
            // Every lake standing in the scene, whoever made it, because a river must be
            // able to end in a hand-placed lake exactly as in a generated one. Kept apart
            // from proceduralLakes, which is only the set this generator may cull.
            List<UCWaterBody> allLakes = new List<UCWaterBody>();
            // A hand-placed ocean. It is never a reconcile candidate, so if one exists its
            // own surface is the sea level everything else aims at - read off the water
            // rather than computed a second time from the terrain.
            UCWaterBody handOcean = null;

            foreach (Transform child in waterRoot)
            {
                UCWaterBody body = child.GetComponent<UCWaterBody>();
                if (body == null) continue;
                if (body.kind == WaterBodyKind.Ocean)
                {
                    if (IsGeneratedOceanName(child.name)) proceduralOceans.Add(body);
                    else if (handOcean == null) handOcean = body;
                }
                else
                {
                    // Rivers (RiverSpline) are placed by hand and are never spawned or
                    // reconciled here, so they are not counted. A lake is still gathered
                    // so a hand-placed river can end in it, and the procedural lake set is
                    // gathered so the spawner can reconcile its own lake count.
                    if (body.kind == WaterBodyKind.Lake) allLakes.Add(body);
                    if (body.shape == UCWaterBody.ShapeMode.Polygon && IsGeneratedLakeName(child.name))
                        proceduralLakes.Add(body);
                }
            }

            int totalChunks = Mathf.Max(0, uc.chunkCountPerSide * uc.chunkCountPerSide);

            // "24% of chunks" is a proportion, so round to nearest - not ceil. Ceil
            // on 0.24f*chunks can tip 24.0 up to 25 from float error (the spec's
            // "100 -> 24, NOT 25").
            //
            // FLOORED AT ONE WHEN A DENSITY IS ASKED FOR AT ALL
            // Rounding to nearest sends any world under about two chunks a side to zero:
            // 1 chunk x 0.24 is 0.24, which rounds to none. The percentage is a DENSITY -
            // "about one lake every four chunks" - and a world smaller than that spacing
            // is not a world with no lakes in it, it is a world with one. Left rounding
            // to zero, a user on a small test world filled in the lake fields, pressed
            // Generate and got nothing, with no way to tell a working field from a dead
            // one. This cannot invent a lake: the count is only permission to look, and
            // every candidate still has to be a real basin that holds
            // MinLakeAreaSquareMetres of water at more than the minimum depth, or it is
            // passed over below.
            int maxLakes = Mathf.Clamp(
                Mathf.RoundToInt(totalChunks * Mathf.Clamp01(node.lakeChunkPercentage)),
                0, totalChunks);
            if (maxLakes == 0 && node.lakeChunkPercentage > 0f && totalChunks > 0) maxLakes = 1;

            // AN EXPLICIT COUNT IS AN INSTRUCTION, NOT A CEILING TO BE CLAMPED
            // This used to be Min(targetLakeCount, maxLakes), and on any world where the
            // percentage rounded low that silently overrode the number the user typed -
            // "Target Lake Count 5" on a small world came out as zero lakes, the field
            // inert no matter what was put in it. The user's rule is that when the
            // spawner is connected the lake number comes from the spawner. The
            // percentage is the density used INSTEAD of a count, not a cap on one, so a
            // stated count now stands on its own; the world's supply of real basins is
            // what limits it, which is a limit of the terrain rather than of arithmetic.
            int desiredLakes = node.targetLakeCount > 0 ? node.targetLakeCount : maxLakes;

            // Lake outlet rivers are no longer spawned here: rivers are placed by hand.
            // A lake that physically overflows is still shown to the author by the River
            // Mask / carve (a trench leaving the sill), which is where they draw the outlet.

            // Reconcile the procedural set DOWN to the target, keeping the lowest-lying
            // basins (the most natural lakes). Runs before RebuildAll, and OnDisable
            // unregisters each removed body's carve, so no ghost basin is left behind.
            // A body the user saved (placementSaved) is pinned like a hand-placed one:
            // it still counts toward the total but is never a removal candidate, so a
            // saved lake survives even when the target count would otherwise cull it.
            if (proceduralLakes.Count > desiredLakes)
            {
                proceduralLakes.Sort((a, b) => a.transform.position.y.CompareTo(b.transform.position.y));
                int removable = proceduralLakes.Count - desiredLakes;
                for (int i = proceduralLakes.Count - 1; i >= 0 && removable > 0; i--)
                {
                    UCWaterBody extra = proceduralLakes[i];
                    if (extra == null) { proceduralLakes.RemoveAt(i); continue; }
                    if (extra.placementSaved) continue;   // pinned by Save Placement - keep
                    proceduralLakes.RemoveAt(i);
                    DestroyGeneratedBody(extra);
                    removable--;
                }
            }

            // New lakes are numbered after the highest surviving index so a freshly
            // spawned "Lake N" can never collide with a kept one (which would make
            // Unity rename it "Lake N (1)" and slip it past the procedural-name test).
            int highestLakeIndex = 0;
            foreach (UCWaterBody b in proceduralLakes)
            {
                int idx; TryParseTrailingIndex(b.name, "Lake", out idx);
                if (idx > highestLakeIndex) highestLakeIndex = idx;
            }

            int currentLakes = proceduralLakes.Count;
            int lakesToSpawn = desiredLakes - currentLakes;

            // --- Ocean reconcile (T10) ---
            // The generator keeps exactly ONE ocean when the node wants one, tears down
            // any surplus, and removes them all when it no longer does. Destroying needs
            // no terrain sample, so it runs before the early-out below. A saved ocean
            // (placementSaved) is pinned exactly like a saved lake: never destroyed, and
            // being kept it suppresses a fresh spawn.
            bool wantOcean = node.generateOcean;
            UCWaterBody keptOcean = null;
            // A saved ocean pins first - it is user-owned and outranks any unsaved one.
            foreach (UCWaterBody o in proceduralOceans)
                if (o != null && o.placementSaved) { keptOcean = o; break; }
            for (int i = proceduralOceans.Count - 1; i >= 0; i--)
            {
                UCWaterBody o = proceduralOceans[i];
                if (o == null) { proceduralOceans.RemoveAt(i); continue; }
                if (o == keptOcean) continue;                 // the pinned saved ocean
                if (o.placementSaved) continue;               // another saved one - never destroy
                if (wantOcean && keptOcean == null) { keptOcean = o; continue; } // keep one unsaved
                proceduralOceans.RemoveAt(i);
                DestroyGeneratedBody(o);
            }
            bool oceanToSpawn = wantOcean && keptOcean == null;

            // Both silent-nothing exits speak now. A Generate that produced no water and
            // no error was indistinguishable from a broken spawner; these say which of
            // the two it was.
            if (lakesToSpawn <= 0 && !oceanToSpawn)
            {
                string msg = $"[UC Hydrology] node {node.id}: nothing to build - the scene already "
                          + $"holds {currentLakes} lake(s) and {proceduralOceans.Count} ocean(s) "
                          + $"against targets {desiredLakes} lake(s)/"
                          + (wantOcean ? "1" : "0") + " ocean. Rivers are placed by hand. Raise a "
                          + "target, or clear the WaterSystem child to rebuild from scratch.";
                string key = node.id ?? "";
                string prev;
                if (!s_lastNothingToBuild.TryGetValue(key, out prev) || prev != msg)
                {
                    s_lastNothingToBuild[key] = msg;
                    Debug.Log(msg);
                }
                if (onComplete != null) onComplete();
                return;
            }

            Terrain[] allTerrains = Terrain.activeTerrains;
            if (allTerrains == null || allTerrains.Length == 0)
            {
                Debug.LogWarning($"[UC Hydrology] node {node.id}: no active Terrain in the scene, so "
                                 + "there is no ground to read heights from and no water was built. "
                                 + "Generate the terrain first.");
                if (onComplete != null) onComplete();
                return;
            }

            // Terrain.activeTerrains returns a Terrain even when its terrainData asset is
            // null - a real editor state after a data asset is deleted, or a Terrain added
            // without data. Reading terrainData.size on one of those throws, and it is the
            // kind of scene state the synthetic self-test never produces, so it surfaces
            // only in the editor as an unexplained "hydrology node" error. Filter them out
            // once here, and every consumer below (bounds, GetHeightAt) sees only real ground.
            List<Terrain> valid = new List<Terrain>(allTerrains.Length);
            for (int ti = 0; ti < allTerrains.Length; ti++)
                if (allTerrains[ti] != null && allTerrains[ti].terrainData != null) valid.Add(allTerrains[ti]);

            if (valid.Count == 0)
            {
                Debug.LogWarning($"[UC Hydrology] node {node.id}: the scene has Terrain objects but none "
                                 + "has a Terrain Data asset, so there is no ground to read. Assign or "
                                 + "regenerate the terrain data first.");
                if (onComplete != null) onComplete();
                return;
            }

            Terrain[] terrains = valid.ToArray();

            Bounds bounds = new Bounds(terrains[0].transform.position + terrains[0].terrainData.size / 2f, terrains[0].terrainData.size);
            foreach (var t in terrains)
            {
                bounds.Encapsulate(new Bounds(t.transform.position + t.terrainData.size / 2f, t.terrainData.size));
            }

            // --- ONE sea level for the whole generate (T17) ---
            // The ocean's surface is also the elevation the rivers have to reach, so it is
            // resolved once, here, and handed to both. An ocean already standing wins,
            // because the user may have moved it and the water itself is then the truth.
            // Failing that it is the NODE'S OWN DATUM, converted to metres with the
            // expression SyncSeaLevelFromOcean inverts.
            //
            // THE DATUM IS A NUMBER, NOT A MEASUREMENT OF THE LAND
            // This used to measure the world instead: the median terrain height around
            // the perimeter, inset into the world-edge falloff band. That reads whatever
            // the land happens to be doing at the edge, so it landed somewhere other than
            // node.seaLevel - and node.seaLevel is what the graph's own River Mask,
            // Lake Mask, Coastal Erosion and Coastal Shelf Shaper all stamp against. Two
            // numbers for one waterline: the mask ended courses at one height while the
            // object put the sea at another, so a river mouth hung above the sea or ran
            // on under it, and the beach band was cut somewhere the water never reached.
            // A measured median also moves every time the terrain is edited, which meant
            // the whole coast shifted when an unrelated mountain was raised.
            //
            // A WORLD WITH NO OCEAN STILL HAS A WATERLINE
            // Sea level is a datum, not a mesh: it means the same thing with the ocean
            // surface switched off. The River Mask ends every course at it and the Lake
            // Mask refuses to flood at or below it whether or not an ocean object exists,
            // so the object side reads the same figure in all three cases.
            UCWaterBody seaBody = keptOcean != null ? keptOcean : handOcean;
            float seaLevelMetres = seaBody != null
                ? seaBody.transform.position.y + seaBody.surfaceOffsetMetres
                : uc.transform.position.y + Mathf.Clamp01(node.seaLevel) * uc.maxHeightMeters;

            // GetHeightAt stays the single terrain sampler, and every consumer below -
            // the lake contour, the drainage solve, the valley fit - is handed this one
            // delegate rather than a Terrain array, so there is no second copy of the
            // "which terrain owns this point" logic.
            RiverCourseSolver.GroundSampler ground = delegate (float x, float z)
            {
                return GetHeightAt(terrains, x, z);
            };

            // Place lakes in genuine low basins by real-world math - not the lowest
            // chunk centre, and never a random point: SelectLakeBasins ranks enclosed,
            // low-lying bowls (ground that sits below the terrain around it on most
            // bearings, where water actually collects) and spreads the picks into
            // distinct basins so lakes do not clump in one valley. (T22.F/G)
            //
            // More candidates than lakes are asked for, because a candidate is only a
            // lake once its basin is shown to hold at least MinLakeAreaSquareMetres of
            // water: a dimple that holds a puddle is skipped and the next basin down the
            // list is tried, rather than a 4 m^2 pond being called a lake.
            float chunkSize = uc.GetChunkSize();
            List<Vector3> lakeCenters = SelectLakeBasins(terrains, bounds, uc, chunkSize,
                                                         lakesToSpawn * 4);

            // THE DEPTH THE LAKE MASK REJECTS AT, CONVERTED TO METRES ONCE
            // node.minLakeDepth is a fraction of world height, because the mask works in
            // the graph's 0..1 height space; the objects are placed in metres. Without
            // this figure the object side accepted a basin on AREA alone, so a wide
            // ankle-deep dimple that the Lake Mask left dry still received a Lake object:
            // a lake surface with no mask under it, and so no bed carved beneath it
            // either. The spawner's own field decided where water was marked and had no
            // say at all in where water was PLACED - the definition of a stub.
            float minLakeDepthMetres = Mathf.Max(0f, node.minLakeDepth) * uc.maxHeightMeters;

            // Spawn Lakes
            int lakesMade = 0;
            // Rejections are counted BY REASON, not as one "skipped" total. "0 of 8
            // lakes" with no reason is the report that sent this system's own author
            // looking for a code fault when the world genuinely had no basin deep
            // enough - the reason is the answer, so it is what gets printed.
            int lakeNoContour = 0;
            int lakeTooSmall = 0;
            int lakeTooShallow = 0;

            // One line per body actually built, so the report can be checked against the
            // hierarchy without opening it.
            List<string> bodyLines = new List<string>();

            // --- Spawn the ocean (T10) FIRST (author's order: ocean -> lakes -> rivers) ---
            // The sea is the base level of the world's drainage: every lake sits in a basin
            // above it and every river ends at it or in a lake, so it is placed before them.
            // One world-sized, flat sea, standing at the datum resolved above - the same
            // node.seaLevel the graph's masks stamp against, so the coast forms exactly
            // where the graph says the land drops below the waterline. It does NOT
            // dig: BuildLevelBody culls every vertex whose ground is at or above the
            // surface, so the sea shows only where the land is already below sea level and
            // any higher ground reads as shore or island - coastline and islands follow
            // the real landscape, with no rectangle stamped into the terrain.
            if (oceanToSpawn)
            {
                int highestOceanIndex = 0;
                foreach (UCWaterBody b in proceduralOceans)
                {
                    int oi; TryParseTrailingIndex(b.name, "Ocean", out oi);
                    if (oi > highestOceanIndex) highestOceanIndex = oi;
                }

                // The one sea level resolved above - not a second computation.
                float seaLevel = seaLevelMetres;

                GameObject oceanGo = new GameObject($"Ocean {highestOceanIndex + 1}");
                oceanGo.transform.SetParent(waterRoot);
                oceanGo.layer = uc.waterLayer;

                UCWaterBody ocean = oceanGo.AddComponent<UCWaterBody>();
                ocean.SetKindOnce(WaterBodyKind.Ocean);
                // Same object a hand-placed ocean is, then the manager defaults - identical
                // to the create menu (see WaterPresets / T15).
                WaterPresets.ApplyDefaults(ocean);
                WaterPresets.ApplyManagerDefaults(ocean);
                // A pure flood surface: no dig, so no square hole and no shore bevel - the
                // waterline is cut by the depth test alone (T22). Islands emerge from the
                // cull, so no island mask is needed either.
                ocean.digTerrain = false;
                // Hold when the graph regenerates, so a sea the user moved is not refit.
                ocean.onRegenerate = UCWaterBody.ProceduralResponse.HoldPlacement;

                // Cover the whole playable world; the cull trims the rest, so oversizing
                // is harmless while undersizing would leave a dry gap at the world edge.
                ocean.sizeMetres = new Vector2(bounds.size.x, bounds.size.z);
                // surfaceY = transform.position.y + surfaceOffsetMetres, so place the
                // object so the water plane lands exactly on the computed sea level.
                ocean.transform.position = new Vector3(bounds.center.x,
                                                       seaLevel - ocean.surfaceOffsetMetres,
                                                       bounds.center.z);
                ocean.Rebuild();
                ReportIfDry(ocean, oceanGo.name);
                bodyLines.Add("    " + oceanGo.name + " - ocean, surface "
                              + ocean.SurfaceWorldY().ToString("0.##") + " m");
            }

            // Build ONE lake from candidate basin i. The body of the old spawn loop,
            // lifted out unchanged so the synchronous path and the one-per-pump path
            // (spec §15) run the exact same code - there is no second copy of the
            // flood/contour/depth logic to drift. It mutates the counters and body-line
            // list captured above, exactly as the loop did, and each of the old loop's
            // three `continue`s is a `return` here. One basin, one whole lake, one
            // Rebuild - the lake is never itself split into chunks; only WHEN each is
            // built is spread out.
            System.Action<int> TrySpawnLakeAt = delegate (int i)
            {
                Vector3 centerPoint = lakeCenters[i];

                // --- Size and shape from the basin, not from Random (T19's lake half) ---
                // The outline is the edge of the water the basin actually holds: the level
                // is raised until the water spills, tops out, or stops spreading, and the
                // shore is traced around wherever it reached. So the shape is the ground's
                // shape - lobed, bayed, never a polygon - and the size is that basin's own,
                // which is why no two lakes in a world come out alike. It replaces
                // Random.Range(30..150) as a radius, and then a single area target that
                // made every lake the same size.
                float fillLevel;
                float lakeArea;
                Polyline outline = SolveLakeContour(ground, bounds, centerPoint,
                                                    out fillLevel, out lakeArea);

                if (outline == null) { lakeNoContour++; return; }
                if (lakeArea < MinLakeAreaSquareMetres) { lakeTooSmall++; return; }

                // How deep the water stands over the basin floor. FloodBasin seeds its
                // flood at centerPoint and raises the level from there, so the water above
                // that point is the deepest the lake gets. If even that is under the
                // floor, no cell in the basin clears it and the Lake Mask marks nothing
                // here - so placing a Lake object would be placing water the graph does
                // not agree is there.
                if (fillLevel - centerPoint.y < minLakeDepthMetres)
                {
                    lakeTooShallow++;
                    return;
                }

                GameObject lakeGo = new GameObject($"Lake {highestLakeIndex + lakesMade + 1}");
                lakeGo.transform.SetParent(waterRoot);
                lakeGo.transform.position = centerPoint;
                lakeGo.layer = uc.waterLayer;

                UCWaterBody lake = lakeGo.AddComponent<UCWaterBody>();
                lake.SetKindOnce(WaterBodyKind.Lake);
                // Same object a hand-placed lake is (see WaterPresets), then the
                // manager defaults - identical to the create menu.
                WaterPresets.ApplyDefaults(lake);
                WaterPresets.ApplyManagerDefaults(lake);
                // Procedural policy: hold placement when the graph regenerates, so a
                // lake the user nudged is not refit from scratch (see T16).
                lake.onRegenerate = UCWaterBody.ProceduralResponse.HoldPlacement;

                lake.outline = outline;

                // A level body's surface is transform.position.y + surfaceOffsetMetres,
                // so the object is placed to put the water plane exactly on the solved
                // fill level. Set here rather than at construction because the level is
                // not known until the contour is solved.
                lakeGo.transform.position = new Vector3(centerPoint.x,
                                                        fillLevel - lake.surfaceOffsetMetres,
                                                        centerPoint.z);
                lake.Rebuild();
                ReportIfDry(lake, lakeGo.name);

                // Into the same one list the rivers read, so a lake spawned this pass is
                // a river terminus this pass too - no separate "new lakes" collection.
                allLakes.Add(lake);
                lakesMade++;
                bodyLines.Add("    " + lakeGo.name + " - lake, surface "
                              + lake.SurfaceWorldY().ToString("0.##") + " m, "
                              + Mathf.RoundToInt(lakeArea) + " m^2, "
                              + (fillLevel - centerPoint.y).ToString("0.##") + " m deep");
            };

            // Spawn the lakes. Two paths, one lake-builder:
            //  - Synchronous (onComplete == null): the self-test and any direct caller
            //    build every candidate now, in this call, so a lake exists the instant
            //    Generate returns.
            //  - One-per-pump (onComplete != null, spec §15): the editor's settle path.
            //    Each after-settle pump advances through the candidate list until it has
            //    BUILT one more lake (a rejected basin - no contour, too small, too
            //    shallow - is cheap and does not end the pump, so a run of dry dimples
            //    does not stall the world for a frame each), then re-arms itself for the
            //    next pump. The Scene view repaints between lakes instead of freezing
            //    while the whole set floods at once - the "page" that blocked Unity. The
            //    LAST pump emits the report and fires onComplete, so the caller folds the
            //    digs and reconciles the beach only once every body is standing.
            ChunkManager pump = onComplete != null ? uc.Chunks : null;
            if (pump == null)
            {
                for (int i = 0; i < lakeCenters.Count && lakesMade < lakesToSpawn; i++)
                    TrySpawnLakeAt(i);
                EmitReport();
                if (onComplete != null) onComplete();
            }
            else
            {
                int cursor = 0;
                System.Action stepLake = null;
                stepLake = delegate ()
                {
                    int madeBefore = lakesMade;
                    // Advance until this pump has built one more lake or the candidates
                    // run out. Rejections are cheap (no GameObject, no Rebuild), so
                    // skipping past them within one pump costs almost nothing and keeps
                    // "one lake per pump" true rather than "one candidate per pump".
                    while (cursor < lakeCenters.Count && lakesMade < lakesToSpawn
                           && lakesMade == madeBefore)
                    {
                        TrySpawnLakeAt(cursor);
                        cursor++;
                    }

                    bool more = cursor < lakeCenters.Count && lakesMade < lakesToSpawn;
                    if (more)
                    {
                        // Re-arm for the next pump. EnqueueAfterSettle appends WITHOUT
                        // clearing, and the pump re-checks the queue AFTER running this
                        // step, so the settle phase holds and drains one lake per pump.
                        pump.EnqueueAfterSettle(stepLake);
                    }
                    else
                    {
                        // Last lake is standing. Report, then hand back to the caller so
                        // the carve fold-in and beach reconcile run as their own pumps.
                        EmitReport();
                        onComplete();
                    }
                };
                stepLake();
            }
            return;

            // --- One report per Generate ---
            // Everything a "nothing appeared" question needs, in one line-block: which node
            // asked, the datum in BOTH units (the node stores 0..1, the world is metres, and
            // a mismatch between them is invisible unless both are printed), what was asked
            // for against what stands, why each rejected basin was rejected, and a line per
            // body actually built so the result can be checked without opening the hierarchy.
            // A local function (not a delegate) so it can be CALLED above, from either
            // spawn path, before its textual position.
            void EmitReport()
            {
            float seaNormalized = uc.maxHeightMeters > 0.0001f
                ? (seaLevelMetres - uc.transform.position.y) / uc.maxHeightMeters
                : 0f;

            System.Text.StringBuilder report = new System.Text.StringBuilder(512);
            report.Append("[UC Hydrology] node ").Append(node.id).Append('\n');
            report.Append("  sea level  : ").Append(seaLevelMetres.ToString("0.##"))
                  .Append(" m  (").Append(seaNormalized.ToString("0.###"))
                  .Append(" of ").Append(uc.maxHeightMeters.ToString("0.##"))
                  .Append(" m world height)").Append('\n');
            report.Append("  lakes      : asked ").Append(lakesToSpawn)
                  .Append(", built ").Append(lakesMade)
                  .Append(", from ").Append(lakeCenters.Count).Append(" candidate basin(s)")
                  .Append('\n');
            report.Append("    rejected : ").Append(lakeNoContour).Append(" no closed contour, ")
                  .Append(lakeTooSmall).Append(" under ")
                  .Append(Mathf.RoundToInt(MinLakeAreaSquareMetres)).Append(" m^2, ")
                  .Append(lakeTooShallow).Append(" under ")
                  .Append(minLakeDepthMetres.ToString("0.##")).Append(" m deep").Append('\n');
            // Rivers are placed MANUALLY by the author (procedural river placement was
            // removed - it was the one thing this system never did reliably). The spawner
            // now builds only ocean and lakes; the River Mask / carve nodes still guide
            // where the author draws a river by hand, but no river OBJECT is spawned here.
            report.Append("  rivers     : placed manually (procedural placement removed)")
                  .Append('\n');
            report.Append("  ocean      : ").Append(oceanToSpawn ? "built" :
                          (wantOcean ? "already standing" : "not requested"));

            if (bodyLines.Count == 0)
            {
                report.Append('\n').Append("  NO WATER BODIES WERE BUILT. The counts above say "
                              + "which stage stopped: 0 candidate basins means the terrain has no "
                              + "closed depressions, and 0 planned rivers means no cell reached "
                              + "the channel-head drainage area.");
            }
            else
            {
                report.Append('\n').Append("  built:");
                for (int i = 0; i < bodyLines.Count; i++) report.Append('\n').Append(bodyLines[i]);
            }

            Debug.Log(report.ToString());
            }
        }

        // --- River size constants (spec 11.7 / 1.9) ---

        /// <summary>Width of this world's largest river, in metres. The spec's band for a
        /// big river is 40-80 m; this is its top, and every smaller river scales below it
        /// by catchment.</summary>
        /// <remarks>
        /// Internal rather than private because the Hydrology Spawner's River Mask port
        /// has to stamp the channel at the width the river object will actually be. A
        /// mask narrower than its river leaves water standing over an uncarved bank; a
        /// mask wider leaves a dry trench beside the water. One constant, both users.
        /// </remarks>
        internal const float BigRiverWidthMetres = 80f;

        /// <summary>Bottom of the same band, in metres. Applied only to a trunk - a
        /// course that joins no other - so the rivers the node count asks for measure
        /// inside 40-80 m while their tributaries stay honestly smaller.</summary>
        private const float TrunkMinWidthMetres = 40f;

        /// <summary>Nothing narrower than this gets built as a river OBJECT (a whole
        /// course, mask and body). Shared with the spawner's River Mask - see
        /// BigRiverWidthMetres - so it governs whether a course exists at all.</summary>
        internal const float MinRiverWidthMetres = 4f;

        /// <summary>
        /// The width and depth a channel has at its SOURCE - the first-order stream where
        /// water first collects, before any tributary joins it. A real river is not one
        /// width: it begins as a rill you can step across (~0.3 m wide, ~0.2 m deep) and
        /// grows every time more land drains through it. These floor the PER-NODE taper
        /// only - the river object's own width (its mouth, what the mask stamps) still
        /// floors at MinRiverWidthMetres, so the course still exists as a river while its
        /// headwater reach thins honestly to a stream instead of stopping at 4 m.
        /// </summary>
        private const float SourceStreamWidthMetres = 0.3f;
        private const float SourceStreamDepthMetres = 0.2f;

        /// <summary>Alluvial channels are roughly this many times wider than deep.</summary>
        private const float WidthToDepthRatio = 18f;

        /// <summary>
        /// How channel width grows with the land it drains.
        /// </summary>
        /// <remarks>
        /// Downstream hydraulic geometry (Leopold and Maddock, 1953) puts bankfull width
        /// at about the square root of discharge, and Hack's law puts discharge at about
        /// the 0.8 power of drainage area - so width goes as area^0.4.
        ///
        /// Internal because the Hydrology Spawner's River Mask has to stamp the channel
        /// at the width the river object will actually be built at. Two copies of this
        /// exponent would widen the mask and the body at different rates, and the
        /// mismatch shows up as water standing over an uncarved bank near the mouth.
        /// </remarks>
        internal const float WidthAreaExponent = 0.4f;

        /// <summary>Fraction of a river's length that behaves as its estuary.</summary>
        private const float EstuaryFraction = 0.15f;

        /// <summary>How much wider a river is at its mouth than upstream.</summary>
        private const float EstuaryFlare = 2.2f;

        /// <summary>Total width of a delta's arms relative to the trunk's.</summary>
        private const float DeltaArmShare = 1.4f;

        /// <summary>
        /// Lowest Strahler order that earns a delta. With one level of tributaries
        /// traced, order 2 is the top of the achievable range, so this is "at least
        /// two comparable branches join this course" - a real network - rather than a
        /// lone stream that happens to reach the coast. A gate of 3 would be
        /// unreachable and would silently disable every delta.
        /// </summary>
        private const int DeltaMinStreamOrder = 2;

        /// <summary>
        /// How far a body's outline reaches from its own origin, in metres - used as the
        /// distance at which a river counts as having arrived at a lake. Read from the
        /// outline the lake was actually built with, so an edited lake moves its own
        /// join radius with it.
        /// </summary>
        private static float OutlineReach(UCWaterBody body)
        {
            if (body == null) return 0f;

            float reach = 0f;

            if (body.outline != null && body.outline.points != null)
            {
                for (int i = 0; i < body.outline.points.Count; i++)
                {
                    Vector3 p = body.outline.points[i];
                    float d = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                    if (d > reach) reach = d;
                }
            }

            // A box-shaped body has no outline, so fall back to its footprint.
            if (reach <= 0f)
                reach = Mathf.Max(body.sizeMetres.x, body.sizeMetres.y) * 0.5f;

            return reach;
        }

        /// <summary>
        /// Chooses where lakes go: genuine low basins, by real-world math, never a
        /// random point. A basin is a local depression - ground that sits below the
        /// terrain around it on most bearings (an enclosed bowl or a valley floor),
        /// which is where water actually collects. Candidates are sampled on a grid,
        /// each scored by how enclosed it is (how many bearings rise above it) and how
        /// much the surroundings rise, the lowest-lying qualifying basins are taken
        /// first, and every pick excludes a radius around it so the lakes land in
        /// distinct basins rather than clustering in one valley.
        ///
        /// Falls back to the lowest de-clustered points, and then to the lowest points
        /// with no spacing, when the terrain is too flat to offer enough basins - so the
        /// requested count is still placed on featureless ground.
        /// </summary>
        private static List<Vector3> SelectLakeBasins(Terrain[] terrains, Bounds bounds,
                                                      UniverseCreatorObject uc, float chunkSize, int count)
        {
            List<Vector3> chosen = new List<Vector3>();
            if (count <= 0 || terrains == null || terrains.Length == 0) return chosen;

            // ~2 samples per chunk side, clamped so a large world's scan stays bounded.
            int perSide = Mathf.Clamp(uc.chunkCountPerSide * 2, 4, 96);
            float stepX = bounds.size.x / perSide;
            float stepZ = bounds.size.z / perSide;
            if (stepX < 1f || stepZ < 1f) return chosen;

            float margin = chunkSize;                             // keep off the world edge
            float ringRadius = Mathf.Max(chunkSize * 0.5f, 60f);  // bearing sample distance

            // Two picks in the same bowl would flood into one another and give one pond
            // wearing two names, so the picks are kept a basin apart. The lake's own size
            // is NOT part of this figure any more: each lake now stops at its own spill
            // point, so two lakes in two basins cannot merge however close they sit - and
            // spacing them by the largest lake the world allows was what thinned a small
            // world down to two or three lakes.
            float minSeparation = Mathf.Max(chunkSize * 1.5f, ringRadius * 2f);

            List<Vector3> pos = new List<Vector3>();
            List<float> rise = new List<float>();       // mean surrounding rise above centre
            List<float> enclosure = new List<float>();  // fraction of bearings that are higher

            const int Bearings = 8;

            for (float wx = bounds.min.x + margin; wx <= bounds.max.x - margin; wx += stepX)
            {
                for (float wz = bounds.min.z + margin; wz <= bounds.max.z - margin; wz += stepZ)
                {
                    float cy = GetHeightAt(terrains, wx, wz);

                    float sum = 0f;
                    int higher = 0;
                    int valid = 0;

                    for (int a = 0; a < Bearings; a++)
                    {
                        float ang = a * Mathf.PI * 2f / Bearings;
                        float rx = wx + Mathf.Cos(ang) * ringRadius;
                        float rz = wz + Mathf.Sin(ang) * ringRadius;
                        if (rx < bounds.min.x || rx > bounds.max.x || rz < bounds.min.z || rz > bounds.max.z) continue;

                        float ry = GetHeightAt(terrains, rx, rz);
                        sum += ry;
                        valid++;
                        if (ry > cy + 0.5f) higher++;
                    }

                    if (valid == 0) continue;

                    pos.Add(new Vector3(wx, cy, wz));
                    rise.Add(sum / valid - cy);
                    enclosure.Add(higher / (float)valid);
                }
            }

            if (pos.Count == 0) return chosen;

            // Lowest centre first: "lowest basins" among those that qualify.
            List<int> order = new List<int>(pos.Count);
            for (int i = 0; i < pos.Count; i++) order.Add(i);
            order.Sort((a, b) => pos[a].y.CompareTo(pos[b].y));

            // Relaxed 2026-09-19: a hydraulically-eroded world has broad, gently-enclosed bowls, and the
            // old 0.6 / 2 m gate rejected nearly all of them -- part of why no lakes appeared. A little
            // over half the bearings rising, and a metre of relief, is still a basin and not a slope.
            const float MinRelief = 1f;      // metres the surroundings must rise
            const float MinEnclosure = 0.45f; // fraction of bearings that must be higher

            // How far inside its own chunk a basin centre must sit to be preferred. A lake
            // floods out from the centre to its spill point, so a centre hard against a
            // chunk border produces a lake that straddles two chunks - the reported "lakes
            // form at borders" fault. Keeping the centre a band inside the chunk lets the
            // body sit within one chunk. Not a hard rule: if every real basin is near a
            // border, the fallback passes below still place the lake rather than dropping
            // it, because a real basin on genuine low ground beats an interior on a hill.
            float borderMargin = Mathf.Clamp(chunkSize * 0.2f, 8f, chunkSize * 0.4f);

            // Pass 1: genuine enclosed basins that also sit inside a chunk, lowest first.
            for (int k = 0; k < order.Count && chosen.Count < count; k++)
            {
                int idx = order[k];
                if (enclosure[idx] < MinEnclosure || rise[idx] < MinRelief) continue;
                if (ChunkBorderDistance(pos[idx], bounds, chunkSize) < borderMargin) continue;
                if (TooClose(chosen, pos[idx], minSeparation)) continue;
                chosen.Add(pos[idx]);
            }

            // Pass 1b: genuine enclosed basins that fell in the border band. A real basin
            // near a chunk seam is still a better lake than a shallow interior dish, so it
            // is taken before the flat-ground fill - just after the clean-interior ones.
            for (int k = 0; k < order.Count && chosen.Count < count; k++)
            {
                int idx = order[k];
                if (enclosure[idx] < MinEnclosure || rise[idx] < MinRelief) continue;
                if (TooClose(chosen, pos[idx], minSeparation)) continue;
                chosen.Add(pos[idx]);
            }

            // Pass 2: flat terrain offered too few basins - fill with the lowest points,
            // still de-clustered so they spread.
            for (int k = 0; k < order.Count && chosen.Count < count; k++)
            {
                int idx = order[k];
                if (TooClose(chosen, pos[idx], minSeparation)) continue;
                chosen.Add(pos[idx]);
            }

            // Pass 3: guarantee the count when spacing cannot be met (small/flat world),
            // taking the lowest remaining with no spacing rather than placing fewer.
            for (int k = 0; k < order.Count && chosen.Count < count; k++)
            {
                Vector3 p = pos[order[k]];
                if (!chosen.Contains(p)) chosen.Add(p);
            }

            return chosen;
        }

        /// <summary>True when p is within minSep (XZ) of any already-chosen point.</summary>
        private static bool TooClose(List<Vector3> chosen, Vector3 p, float minSep)
        {
            float sq = minSep * minSep;
            for (int i = 0; i < chosen.Count; i++)
            {
                float dx = chosen[i].x - p.x;
                float dz = chosen[i].z - p.z;
                if (dx * dx + dz * dz < sq) return true;
            }
            return false;
        }

        /// <summary>
        /// Distance in metres from a world point to the nearest chunk boundary line. Used
        /// to prefer lake centres that sit inside a chunk rather than on a seam between
        /// two, so a lake floods within one chunk. The chunk grid is anchored at the
        /// world bounds' minimum corner and stepped by chunkSize, matching how the chunk
        /// manager tiles the world.
        /// </summary>
        private static float ChunkBorderDistance(Vector3 p, Bounds bounds, float chunkSize)
        {
            if (chunkSize <= 0.01f) return float.MaxValue;

            // Offset within the current chunk cell, on each axis.
            float ox = Mathf.Repeat(p.x - bounds.min.x, chunkSize);
            float oz = Mathf.Repeat(p.z - bounds.min.z, chunkSize);

            // Nearest of the two borders on each axis, then the nearer axis overall.
            float dx = Mathf.Min(ox, chunkSize - ox);
            float dz = Mathf.Min(oz, chunkSize - oz);
            return Mathf.Min(dx, dz);
        }

        // --- Lake size and shape (spec 11.7 / T19, revised 2026-08-18) --------------
        //
        // WHERE A LAKE'S SIZE AND SHAPE COME FROM
        //
        // Neither is chosen and neither is random. Water is let into the basin floor and
        // the level raised, and the water goes wherever it can reach: the SHAPE is the
        // outline of the ground it covered - lobes around a ridge, a bay, a narrow arm up
        // a side valley - and the SIZE is however much surface that particular basin
        // holds. So two basins give two different lakes from the same code, the way a real
        // landscape does, and the same terrain gives the same lakes on every press.
        //
        // What this replaced, in order: Random.Range(30..150) as a radius (a different
        // world every press), then a 28-bearing star that could only ever be convex and
        // read as a polygon on smooth ground, then filling every basin to one area target,
        // which made every lake the same size.
        //
        // The only figure stated is the floor: under 60 m^2 it is a puddle, not a lake.

        /// <summary>Smallest standing water that counts as a lake: 60 m^2, a patch about
        /// eight metres across. A basin holding less is passed over rather than inflated.
        /// Above it no lake is given a size - each takes what its own basin holds.</summary>
        public const float MinLakeAreaSquareMetres = 60f;

        /// <summary>Largest standing water that counts as a lake: 90,000 m^2, about 340 m
        /// across. The old 720 m^2 ceiling (~30 m across) made every real lake read as a
        /// puddle or be rejected outright - at world scale that is why "no lakes" showed.
        /// Raised to a proper lake, but NOT so far that a gently-sloping dish fills deep and
        /// wide enough to become a shallow sheet the depth mask cannot back (the invariant
        /// TestLakeDepthGateMatchesTheMask guards). A basin that could hold more than a proper
        /// lake is filled to this and no further; water beyond it belongs to the ocean pass,
        /// not here. It also sets how wide a neighbourhood the search looks at.</summary>
        public const float MaxLakeAreaSquareMetres = 90000f;

        /// <summary>How much wider than the ceiling's own radius the searched
        /// neighbourhood is. 2.5 leaves room for a lake of the ceiling area that is long
        /// and thin rather than round - about 38 m out, so 76 m across.</summary>
        private const float LakeSearchReachMultiple = 2.5f;

        /// <summary>Half-width of the neighbourhood searched: the ceiling's own radius
        /// sqrt(A/pi) times the multiple above. Ground that keeps falling past this is a
        /// slope, not a bowl.</summary>
        private static readonly float LakeSearchReachMetres =
            Mathf.Sqrt(MaxLakeAreaSquareMetres / Mathf.PI) * LakeSearchReachMultiple;

        /// <summary>Cells per side of the flood grid. The lake's outline IS the edge of
        /// this grid's flooded region, so this is what buys the shoreline its detail.</summary>
        private const int LakeGridResolution = 80;

        /// <summary>Coarse-to-fine passes. The first finds the basin anywhere in the
        /// neighbourhood; each later one re-solves inside the footprint just found, so a
        /// 60 m^2 tarn and a 720 m^2 pond are each shaped on a grid matched to their own
        /// size instead of on one grid far too coarse for the smaller.</summary>
        private const int LakeRefinePasses = 3;

        /// <summary>Deepest the search fills a basin, and the number of even steps it
        /// takes getting there - so one step is half a metre. Only a shaft narrower than
        /// the ceiling ever gets near 60 m; a wide basin runs out of ceiling long first.</summary>
        private const float MaxLakeFillMetres = 60f;
        private const int LakeFillSteps = 120;

        /// <summary>Halvings used to land the level exactly where the water stops fitting.
        /// Twelve resolves a half-metre step to under a third of a millimetre, which is
        /// finer than the ground is sampled - so the shoreline, not the step size, is what
        /// decides the shape.</summary>
        private const int LakeLevelBisections = 12;

        /// <summary>The water line. When half a metre of rise adds less than this much
        /// surface twice running, the basin's walls have gone vertical and the water has
        /// nothing left to spread over. Filling on past that point is what produces a
        /// column of water standing in a hole instead of a lake lying in a bowl.</summary>
        private const float LakeAreaGainStopFraction = 0.01f;

        /// <summary>Outline vertices kept before the corners are rounded off. Enough for a
        /// lobed shore, few enough that the water mesh is not paying for one vertex per
        /// grid step.</summary>
        private const int LakeOutlineMaxVertices = 72;

        /// <summary>
        /// The outline of the lake a basin holds: the shape of the water, not a radius.
        ///
        /// Water is let in at the basin floor and the level raised half a metre at a time.
        /// At each level the cells the water can REACH from that floor are flooded -
        /// four-connected, so a lower pond on the far side of a ridge stays a different
        /// lake rather than being annexed to this one - and the rise stops when the water
        /// spills out of the neighbourhood, when it reaches the lake/sea ceiling, or when a
        /// rise buys almost no new surface because the walls have gone vertical. The
        /// outline is then traced around the flooded cells and its corners rounded off.
        ///
        /// Because the shore IS the flooded edge, no vertex can stand on ground much above
        /// the water: the lake cannot perch on a hillside and it cannot leak out of the
        /// basin. Leaving the world counts as a wall, so it cannot run off the terrain.
        ///
        /// areaSquareMetres is the surface the outline encloses; below
        /// MinLakeAreaSquareMetres the caller passes the basin over. Returns null when the
        /// ground there cannot hold water at all.
        ///
        /// The polyline is CLOSED and in LOCAL metres about centre. An open one fails the
        /// polygon test in BuildLevelBody and the lake falls back to filling its whole
        /// bounding box - the square hole.
        /// </summary>
        public static Polyline SolveLakeContour(RiverCourseSolver.GroundSampler ground,
                                                Bounds bounds, Vector3 centre,
                                                out float fillLevelMetres,
                                                out float areaSquareMetres)
        {
            fillLevelMetres = centre.y;
            areaSquareMetres = 0f;

            int n = Mathf.Max(16, LakeGridResolution);

            float half = LakeSearchReachMetres;
            float windowX = centre.x;
            float windowZ = centre.z;

            bool[] region = null;
            float[] heights = null;
            float cellSize = 0f, originX = 0f, originZ = 0f;

            for (int pass = 0; pass < Mathf.Max(1, LakeRefinePasses); pass++)
            {
                float cell = half * 2f / n;
                float ox = windowX - half;
                float oz = windowZ - half;

                float[] h = SampleLakeGrid(ground, bounds, ox, oz, cell, n);

                bool[] flooded;
                float level, area;

                // A finer pass that cannot hold water - the water reaches the edge of the
                // tightened window at the very first step - leaves the coarser answer
                // standing rather than throwing the lake away.
                if (!FloodBasin(h, n, cell, ox, oz, centre, out flooded, out level, out area))
                    break;

                region = flooded;
                heights = h;
                cellSize = cell;
                originX = ox;
                originZ = oz;
                fillLevelMetres = level;
                areaSquareMetres = area;

                int minI, minJ, maxI, maxJ;
                RegionBounds(flooded, n, out minI, out minJ, out maxI, out maxJ);

                int spanI = maxI - minI + 1;
                int spanJ = maxJ - minJ + 1;
                int span = Mathf.Max(spanI, spanJ);

                // Already matched to the window: refining would re-sample the same shape
                // at the same relative resolution and only cost terrain samples.
                if (span >= n * 0.34f) break;

                // Otherwise pull the window in around the footprint - centred on the water
                // found, not on the picked point - with room left over so the level can be
                // re-solved on the finer grid without immediately running out of window.
                float widthMetres = Mathf.Max(span * cell, cell * 2f);

                windowX = ox + (minI + maxI + 1) * 0.5f * cell;
                windowZ = oz + (minJ + maxJ + 1) * 0.5f * cell;
                half = widthMetres * 0.875f + cell;
            }

            if (region == null) return null;

            // Two flooded cells touching only at a corner would make the traced loop pass
            // through that one point twice, and the walk would lose an edge there. The
            // lower of the two dry cells between them joins the water - which is where
            // water would actually go - and the loop stays single.
            FillDiagonalPinches(region, heights, n);

            // Recount after the pinch fill so the reported area is the area of the shape
            // that is actually returned.
            int wetCells = 0;
            for (int i = 0; i < region.Length; i++) if (region[i]) wetCells++;
            areaSquareMetres = wetCells * cellSize * cellSize;

            List<Vector3> loop = TraceRegionOutline(region, n, cellSize, originX, originZ, centre);
            if (loop == null || loop.Count < 3) return null;

            loop = DropCollinear(loop);
            loop = LimitVertices(loop, LakeOutlineMaxVertices);
            loop = RoundCorners(loop);

            return new Polyline(loop, true);
        }

        /// <summary>Ground height on a square grid of cell centres. Outside the world
        /// reads as infinitely high, which is the wall the water stops at.</summary>
        private static float[] SampleLakeGrid(RiverCourseSolver.GroundSampler ground,
                                              Bounds bounds,
                                              float originX, float originZ,
                                              float cell, int n)
        {
            float[] h = new float[n * n];

            for (int j = 0; j < n; j++)
            {
                float z = originZ + (j + 0.5f) * cell;
                int row = j * n;

                for (int i = 0; i < n; i++)
                {
                    float x = originX + (i + 0.5f) * cell;

                    h[row + i] = (x >= bounds.min.x && x <= bounds.max.x &&
                                  z >= bounds.min.z && z <= bounds.max.z)
                               ? ground(x, z)
                               : float.PositiveInfinity;
                }
            }

            return h;
        }

        /// <summary>
        /// Floods the basin under centre and reports the water it holds: which cells are
        /// covered, the level they are covered to, and the surface area. False when the
        /// ground there holds no water at all - a slope or a plain, where the first half
        /// metre already runs out of the neighbourhood.
        /// </summary>
        private static bool FloodBasin(float[] h, int n, float cell,
                                       float originX, float originZ, Vector3 centre,
                                       out bool[] region, out float levelMetres,
                                       out float areaSquareMetres)
        {
            region = null;
            levelMetres = centre.y;
            areaSquareMetres = 0f;

            int si = Mathf.Clamp(Mathf.FloorToInt((centre.x - originX) / cell), 0, n - 1);
            int sj = Mathf.Clamp(Mathf.FloorToInt((centre.z - originZ) / cell), 0, n - 1);
            int seed = sj * n + si;

            if (float.IsInfinity(h[seed])) return false;   // the basin floor is off the world

            // Levels are measured from the floor as the grid sampled it, not from the
            // picked point, so the first step always wets the seed even where the cell
            // centre landed a little way up the bank.
            float baseY = Mathf.Min(centre.y, h[seed]);
            float cellArea = cell * cell;

            bool[] wet = new bool[n * n];
            int[] stack = new int[n * n];

            float previous = 0f;
            int quiet = 0;
            float held = 0f;       // rise above the floor of the highest level that fitted

            for (int s = 1; s <= LakeFillSteps; s++)
            {
                float rise = MaxLakeFillMetres * s / LakeFillSteps;

                int count;
                bool escaped = FloodAt(h, n, seed, baseY + rise, wet, stack, out count);
                float area = count * cellArea;

                // Either the water left the neighbourhood or it grew past the size a lake
                // can be. Half a metre of rise is most of a small lake, so the level that
                // just fits is hunted between the last one that did and this one rather
                // than settling for the step below and losing that water.
                if (escaped || area > MaxLakeAreaSquareMetres)
                {
                    float low = held, high = rise;

                    for (int b = 0; b < LakeLevelBisections; b++)
                    {
                        float mid = (low + high) * 0.5f;

                        int midCount;
                        bool midEscaped = FloodAt(h, n, seed, baseY + mid, wet, stack, out midCount);
                        float midArea = midCount * cellArea;

                        if (midEscaped || midArea > MaxLakeAreaSquareMetres) { high = mid; continue; }

                        low = mid;

                        if (region == null) region = new bool[n * n];
                        System.Array.Copy(wet, region, wet.Length);

                        levelMetres = baseY + mid;
                        areaSquareMetres = midArea;
                    }

                    break;
                }

                if (previous > 0f && (area - previous) / previous < LakeAreaGainStopFraction) quiet++;
                else quiet = 0;

                if (region == null) region = new bool[n * n];
                System.Array.Copy(wet, region, wet.Length);

                levelMetres = baseY + rise;
                areaSquareMetres = area;
                previous = area;
                held = rise;

                // Two quiet steps rather than one: on a coarse grid the area moves in
                // whole cells, so a single step can add nothing while the basin is still
                // widening. Two in a row is a wall.
                if (quiet >= 2) break;
            }

            return region != null;
        }

        /// <summary>
        /// Floods outward from seed over everything the water at this level can reach, and
        /// says whether it got out of the neighbourhood. Four-connected on purpose: water
        /// does not cross a ridge diagonally, so a lower pond behind one stays its own lake.
        /// wet and stack are the caller's scratch, reused across every level.
        /// </summary>
        private static bool FloodAt(float[] h, int n, int seed, float level,
                                    bool[] wet, int[] stack, out int count)
        {
            System.Array.Clear(wet, 0, wet.Length);
            count = 0;

            int top = 0;
            wet[seed] = true;
            stack[top++] = seed;

            while (top > 0)
            {
                int c = stack[--top];
                count++;

                int ci = c % n;
                int cj = c / n;

                // Reaching the edge of the neighbourhood means the water has left it:
                // at this level the ground is not a basin, it is a slope draining away.
                if (ci == 0 || cj == 0 || ci == n - 1 || cj == n - 1) return true;

                if (!wet[c - 1] && h[c - 1] <= level) { wet[c - 1] = true; stack[top++] = c - 1; }
                if (!wet[c + 1] && h[c + 1] <= level) { wet[c + 1] = true; stack[top++] = c + 1; }
                if (!wet[c - n] && h[c - n] <= level) { wet[c - n] = true; stack[top++] = c - n; }
                if (!wet[c + n] && h[c + n] <= level) { wet[c + n] = true; stack[top++] = c + n; }
            }

            return false;
        }

        /// <summary>Cell bounds of the flooded region, inclusive. Zeroed when nothing is
        /// flooded, which the caller has already ruled out.</summary>
        private static void RegionBounds(bool[] region, int n,
                                         out int minI, out int minJ, out int maxI, out int maxJ)
        {
            minI = n; minJ = n; maxI = -1; maxJ = -1;

            for (int j = 0; j < n; j++)
            {
                int row = j * n;

                for (int i = 0; i < n; i++)
                {
                    if (!region[row + i]) continue;

                    if (i < minI) minI = i;
                    if (i > maxI) maxI = i;
                    if (j < minJ) minJ = j;
                    if (j > maxJ) maxJ = j;
                }
            }

            if (maxI < 0) { minI = 0; minJ = 0; maxI = 0; maxJ = 0; }
        }

        /// <summary>
        /// Wets the lower of the two dry cells in any checkerboard 2x2, so no two flooded
        /// cells meet at a bare corner. Left in, that corner is the one lattice point with
        /// two ways out of it, and the outline walk drops an edge there.
        /// </summary>
        private static void FillDiagonalPinches(bool[] region, float[] h, int n)
        {
            for (int sweep = 0; sweep < 4; sweep++)
            {
                bool changed = false;

                for (int j = 0; j + 1 < n; j++)
                {
                    for (int i = 0; i + 1 < n; i++)
                    {
                        int a = j * n + i;          // (i,   j)
                        int b = a + 1;              // (i+1, j)
                        int c = a + n;              // (i,   j+1)
                        int d = c + 1;              // (i+1, j+1)

                        if (region[a] && region[d] && !region[b] && !region[c])
                        {
                            region[h[b] <= h[c] ? b : c] = true;
                            changed = true;
                        }
                        else if (region[b] && region[c] && !region[a] && !region[d])
                        {
                            region[h[a] <= h[d] ? a : d] = true;
                            changed = true;
                        }
                    }
                }

                if (!changed) break;
            }
        }

        /// <summary>
        /// Traces the shoreline around the flooded cells: one directed segment per
        /// wet/dry cell edge, wound so the water is on the left, then followed corner to
        /// corner. Islands inside the lake trace the other way round, so the loop
        /// enclosing the most is the shore. Returns LOCAL metres about centre.
        /// </summary>
        private static List<Vector3> TraceRegionOutline(bool[] region, int n, float cell,
                                                        float originX, float originZ, Vector3 centre)
        {
            int stride = n + 1;
            int[] next = new int[stride * stride];
            for (int i = 0; i < next.Length; i++) next[i] = -1;

            for (int j = 0; j < n; j++)
            {
                int row = j * n;

                for (int i = 0; i < n; i++)
                {
                    if (!region[row + i]) continue;

                    if (j == 0 || !region[row - n + i])
                        next[j * stride + i] = j * stride + i + 1;             // south, +x

                    if (i == n - 1 || !region[row + i + 1])
                        next[j * stride + i + 1] = (j + 1) * stride + i + 1;   // east, +z

                    if (j == n - 1 || !region[row + n + i])
                        next[(j + 1) * stride + i + 1] = (j + 1) * stride + i; // north, -x

                    if (i == 0 || !region[row + i - 1])
                        next[(j + 1) * stride + i] = j * stride + i;           // west, -z
                }
            }

            List<Vector3> best = null;
            float bestTwiceArea = 0f;
            bool[] walked = new bool[next.Length];

            for (int start = 0; start < next.Length; start++)
            {
                if (next[start] < 0 || walked[start]) continue;

                List<Vector3> loop = new List<Vector3>();
                int p = start;
                int guard = next.Length + 4;

                while (guard-- > 0)
                {
                    walked[p] = true;

                    int i = p % stride;
                    int j = p / stride;

                    loop.Add(new Vector3(originX + i * cell - centre.x, 0f,
                                         originZ + j * cell - centre.z));

                    p = next[p];
                    if (p < 0 || p == start || walked[p]) break;
                }

                if (loop.Count < 3) continue;

                float twice = 0f;
                for (int k = 0; k < loop.Count; k++)
                {
                    Vector3 u = loop[k];
                    Vector3 v = loop[(k + 1) % loop.Count];
                    twice += u.x * v.z - v.x * u.z;
                }

                if (twice > bestTwiceArea) { bestTwiceArea = twice; best = loop; }
            }

            return best;
        }

        /// <summary>Drops the points that only continue a straight run, which is most of
        /// them along a lattice edge, without moving the shape.</summary>
        private static List<Vector3> DropCollinear(List<Vector3> loop)
        {
            int count = loop.Count;
            if (count < 4) return loop;

            List<Vector3> kept = new List<Vector3>(count);

            for (int i = 0; i < count; i++)
            {
                Vector3 prev = loop[(i + count - 1) % count];
                Vector3 cur = loop[i];
                Vector3 next = loop[(i + 1) % count];

                float cross = (cur.x - prev.x) * (next.z - cur.z)
                            - (cur.z - prev.z) * (next.x - cur.x);

                if (Mathf.Abs(cross) > 1e-4f) kept.Add(cur);
            }

            return kept.Count >= 3 ? kept : loop;
        }

        /// <summary>Resamples the loop at even spacing when it carries more vertices than
        /// the water mesh needs. Even spacing rather than dropping every other point, so a
        /// long straight shore does not keep detail a bay has just lost.</summary>
        private static List<Vector3> LimitVertices(List<Vector3> loop, int max)
        {
            int count = loop.Count;
            if (max < 8 || count <= max) return loop;

            float[] cumulative = new float[count + 1];
            for (int i = 0; i < count; i++)
                cumulative[i + 1] = cumulative[i] + Vector3.Distance(loop[i], loop[(i + 1) % count]);

            float total = cumulative[count];
            if (total <= 1e-4f) return loop;

            List<Vector3> even = new List<Vector3>(max);
            int seg = 0;

            for (int k = 0; k < max; k++)
            {
                float want = total * k / max;
                while (seg < count - 1 && cumulative[seg + 1] < want) seg++;

                float length = cumulative[seg + 1] - cumulative[seg];
                float t = length > 1e-5f ? (want - cumulative[seg]) / length : 0f;

                even.Add(Vector3.Lerp(loop[seg], loop[(seg + 1) % count], t));
            }

            return even;
        }

        /// <summary>One round of corner cutting. The traced loop is made of right angles
        /// because it followed cell edges; cutting the corners is what turns it back into
        /// a shoreline. It also pulls every vertex slightly inward, off the bank.</summary>
        private static List<Vector3> RoundCorners(List<Vector3> loop)
        {
            int count = loop.Count;
            if (count < 4) return loop;

            List<Vector3> rounded = new List<Vector3>(count * 2);

            for (int i = 0; i < count; i++)
            {
                Vector3 a = loop[i];
                Vector3 b = loop[(i + 1) % count];

                rounded.Add(a * 0.75f + b * 0.25f);
                rounded.Add(a * 0.25f + b * 0.75f);
            }

            return rounded;
        }

        // --- O(1) terrain lookup (freeze Fix B) ---
        // GetHeightAt used to scan EVERY Terrain on every sample - O(chunks) per call.
        // The drainage MASK solve and the lake-basin sweep each sample the ground up to
        // 512*512 times, so on a large multi-chunk world that linear scan was the
        // dominant cost of the ~30 s editor freeze. A chunked world's terrains tile on a
        // regular grid, so the terrain that owns a world (x,z) can be found by grid math
        // in O(1) instead of scanned for. The index is cached against the terrains ARRAY
        // reference (a fresh array is built each GenerateFromNode, so a new generate
        // rebuilds it) and only used when the tiles are uniform-size and axis-aligned;
        // any other layout falls back to the exact original linear scan, so there is no
        // behavioural change - only the common case gets faster.
        private static Terrain[] s_indexTerrains;
        private static Dictionary<long, Terrain> s_terrainIndex;
        private static float s_indexOriginX, s_indexOriginZ, s_indexCellX, s_indexCellZ;
        private static bool s_indexUniform;

        private static long TerrainCellKey(int ix, int iz)
        {
            return ((long)ix << 32) ^ (uint)iz;
        }

        private static void EnsureTerrainIndex(Terrain[] terrains)
        {
            if (ReferenceEquals(s_indexTerrains, terrains) && s_terrainIndex != null) return;

            s_indexTerrains = terrains;
            s_terrainIndex = new Dictionary<long, Terrain>(terrains.Length);
            s_indexUniform = true;

            float ox = float.PositiveInfinity, oz = float.PositiveInfinity, cx = 0f, cz = 0f;
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 p = t.transform.position;
                Vector3 size = t.terrainData.size;
                if (p.x < ox) ox = p.x;
                if (p.z < oz) oz = p.z;
                if (cx == 0f) { cx = size.x; cz = size.z; }
                else if (!Mathf.Approximately(cx, size.x) || !Mathf.Approximately(cz, size.z)) s_indexUniform = false;
            }

            s_indexOriginX = ox; s_indexOriginZ = oz; s_indexCellX = cx; s_indexCellZ = cz;

            if (!s_indexUniform || cx <= 0f || cz <= 0f)
            {
                s_indexUniform = false;   // non-uniform layout: GetHeightAt keeps the linear scan
                return;
            }

            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 p = t.transform.position;
                int ix = Mathf.FloorToInt((p.x - ox) / cx + 0.5f);   // +0.5 rounds the tile ORIGIN onto its cell
                int iz = Mathf.FloorToInt((p.z - oz) / cz + 0.5f);
                s_terrainIndex[TerrainCellKey(ix, iz)] = t;
            }
        }

        private static float GetHeightAt(Terrain[] terrains, float x, float z)
        {
            EnsureTerrainIndex(terrains);

            if (s_indexUniform)
            {
                int ix = Mathf.FloorToInt((x - s_indexOriginX) / s_indexCellX);
                int iz = Mathf.FloorToInt((z - s_indexOriginZ) / s_indexCellZ);
                if (s_terrainIndex.TryGetValue(TerrainCellKey(ix, iz), out Terrain t) && t != null && t.terrainData != null)
                {
                    return t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
                }
                return 0f;   // outside the tiled grid - the same answer the old scan gave when no tile owned (x,z)
            }

            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;   // a Terrain with no data samples nothing
                Vector3 local = t.transform.InverseTransformPoint(new Vector3(x, 0, z));
                if (local.x >= 0 && local.x <= t.terrainData.size.x && local.z >= 0 && local.z <= t.terrainData.size.z)
                {
                    return t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
                }
            }
            return 0f;
        }

        // WHY A BODY THAT BUILT NOTHING HAS TO SAY SO
        // UCWaterBody.RebuildSurface asks BuildData for geometry and returns early,
        // without creating its "UC Water Surface" child, whenever the answer is null -
        // which Assemble returns the moment not one quad of the footprint came out wet.
        // That is the reported fault in its exact shape: the object sits in the
        // hierarchy with every field correctly filled in, there is no water and no
        // shader anywhere, and the console says nothing at all. The condition is a real
        // description of the world, not a code error - a water plane with no ground
        // under it, a course too short to raise a mesh - so it is reported rather than
        // thrown, it names the height it used so the figure can be checked against the
        // terrain, and the object is left standing so its settings can be read.
        private static void ReportIfDry(UCWaterBody body, string what)
        {
            if (body == null) return;
            if (body.transform.Find("UC Water Surface") != null) return;

            Debug.LogWarning("[UC Hydrology] " + what + " produced no surface geometry; its water plane "
                             + "stands at " + body.SurfaceWorldY().ToString("0.##") + " m. For a lake or "
                             + "the ocean that means no ground inside its footprint lies below that "
                             + "height, so there is no basin for the water to stand in; for a river it "
                             + "means the course was too short or too narrow to raise a mesh. The object "
                             + "is left in place rather than removed, so its settings can be inspected.");
        }

        // A body created by this generator is named "Lake <n>". A hand-placed body
        // from the menu is "Lake" (or "Lake (1)" when Unity de-dupes a duplicate), so
        // this returns false for it - the cull only ever removes generated lakes.
        private static bool IsGeneratedLakeName(string name)
        {
            int idx;
            return TryParseTrailingIndex(name, "Lake", out idx);
        }

        // A generator ocean is "Ocean <n>". A hand-placed menu ocean is "Ocean" (or
        // "Ocean (1)" de-duped), which this rejects - so the ocean reconcile never
        // destroys the user's own sea, only the one this generator made.
        private static bool IsGeneratedOceanName(string name)
        {
            int idx;
            return TryParseTrailingIndex(name, "Ocean", out idx);
        }

        // True only for exactly "<prefix> <digits>" (one space, digits to the end).
        // Rejects "Lake", "Lake (1)", "Lakebed", "Lake 1 Distributary 2".
        private static bool TryParseTrailingIndex(string name, string prefix, out int index)
        {
            index = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string head = prefix + " ";
            if (!name.StartsWith(head, System.StringComparison.Ordinal)) return false;
            string tail = name.Substring(head.Length);
            if (tail.Length == 0) return false;
            for (int i = 0; i < tail.Length; i++)
                if (tail[i] < '0' || tail[i] > '9') return false;
            return int.TryParse(tail, out index);
        }

        // Removing the GameObject fires UCWaterBody.OnDisable, which unregisters its
        // carve; the subsequent RebuildAll then rebuilds terrain without that basin.
        private static void DestroyGeneratedBody(UCWaterBody body)
        {
            if (body == null) return;
            GameObject go = body.gameObject;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }

        // Graph-reactive teardown. When no Hydrology node reaches an Output (the node was
        // disconnected or deleted), UniverseCreatorObject.RebuildChunks calls this INSTEAD
        // of GenerateFromNode. It destroys every body THIS generator made - "Lake N",
        // "Ocean N", "River N", "Outlet N", and their "... Distributary M" arms - each of
        // which takes its child "UC Water Surface" and its carve down with it (OnDisable
        // unregisters the carve, so the RebuildAll that follows fills the freed basin back
        // in). Hand-placed bodies ("Lake", "River", "Lake (1)", any custom name) fail the
        // name test and survive, and the UniverseCreator root is never touched - the
        // user's rule: the disconnected water must vanish from the scene AND the hierarchy
        // without deleting their own objects. Returns the number removed so the caller can
        // skip the synchronous re-carve when there was nothing to sweep.
        public static int RemoveAllGenerated(UniverseCreatorObject uc)
        {
            if (uc == null) return 0;
            Transform waterRoot = uc.transform.Find("WaterSystem");
            if (waterRoot == null) return 0;

            // Snapshot before destroying - DestroyGeneratedBody mutates the child list.
            List<UCWaterBody> doomed = new List<UCWaterBody>();
            foreach (Transform child in waterRoot)
            {
                UCWaterBody body = child.GetComponent<UCWaterBody>();
                if (body == null) continue;
                // A saved body survives a disconnect: the user pinned it with Save
                // Placement, so it is treated like a hand-placed body even though its
                // name still matches the generated scheme.
                if (body.placementSaved) continue;
                if (IsGeneratedBodyName(child.name)) doomed.Add(body);
            }

            for (int i = 0; i < doomed.Count; i++)
                DestroyGeneratedBody(doomed[i]);

            return doomed.Count;
        }

        // True for any name this generator produces: "Lake N", "Ocean N", "River N",
        // "Tributary N", "Outlet N", or a river or outlet with " Distributary M" after
        // it. Everything else - hand-placed "Lake"/"River"/"Ocean", Unity's "Lake (1)"
        // de-dupe, custom names - is left alone. Public because the water-body inspector
        // uses it to decide whether to offer Save Placement (only a generator-owned body
        // needs protecting; a hand-placed one is already safe).
        public static bool IsGeneratedBodyName(string name)
        {
            int idx;
            if (IsGeneratedLakeName(name)) return true;                         // "Lake N"
            if (IsGeneratedOceanName(name)) return true;                        // "Ocean N"
            if (TryParseTrailingIndex(name, "River", out idx)) return true;     // "River N"
            if (TryParseTrailingIndex(name, "Tributary", out idx)) return true; // "Tributary N"
            if (TryParseTrailingIndex(name, "Outlet", out idx)) return true;    // "Outlet N"
            return IsGeneratedDistributaryName(name);                           // "... Distributary M"
        }

        // Exactly "<River|Outlet> <digits> Distributary <digits>" - the delta-channel
        // scheme GenerateFromNode uses. Matched on its own because TryParseTrailingIndex
        // rejects it (the tail is not all digits), which is what keeps a plain "River N"
        // test from sweeping distributaries by accident and vice-versa.
        private static bool IsGeneratedDistributaryName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            const string mid = " Distributary ";
            int sep = name.IndexOf(mid, System.StringComparison.Ordinal);
            if (sep <= 0) return false;

            if (!IsAllDigits(name.Substring(sep + mid.Length))) return false;

            string trunk = name.Substring(0, sep);
            int idx;
            return TryParseTrailingIndex(trunk, "River", out idx) ||
                   TryParseTrailingIndex(trunk, "Outlet", out idx);
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }
    }
}
