using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem
{
    public static class HydrologyGenerator
    {
        public static void GenerateFromNode(UniverseCreatorObject uc, HydrologyNode node)
        {
            if (uc == null || node == null) return;

            Transform waterRoot = uc.transform.Find("WaterSystem");
            if (waterRoot == null)
            {
                GameObject rootGo = new GameObject("WaterSystem");
                rootGo.transform.SetParent(uc.transform);
                rootGo.transform.localPosition = Vector3.zero;
                waterRoot = rootGo.transform;
            }

            int currentRivers = 0;
            // Lake drain-out rivers are counted apart from the trunk rivers because they
            // come out of their own budget (a fraction of the chunk count, not
            // targetRiverCount). Counted together, a spawned outlet would eat next run's
            // trunk allowance and the world would lose a big river every Generate.
            int currentOutlets = 0;
            int highestOutletIndex = 0;
            // Tributaries need no count of their own: they are spawned only alongside a
            // new trunk, so their numbering is all that has to survive between runs.
            int highestTributaryIndex = 0;
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
                else if (body.shape == UCWaterBody.ShapeMode.RiverSpline)
                {
                    // Only a trunk river counts against targetRiverCount. A delta arm, a
                    // tributary and a lake outlet are rivers too, but each is spawned
                    // from its own budget, so counting them here would starve the trunks.
                    int idx;
                    if (TryParseTrailingIndex(child.name, "Outlet", out idx))
                    {
                        currentOutlets++;
                        if (idx > highestOutletIndex) highestOutletIndex = idx;
                    }
                    else if (TryParseTrailingIndex(child.name, "Tributary", out idx))
                    {
                        if (idx > highestTributaryIndex) highestTributaryIndex = idx;
                    }
                    else if (!IsGeneratedDistributaryName(child.name))
                    {
                        currentRivers++;
                    }
                }
                else
                {
                    if (body.kind == WaterBodyKind.Lake) allLakes.Add(body);
                    if (body.shape == UCWaterBody.ShapeMode.Polygon && IsGeneratedLakeName(child.name))
                        proceduralLakes.Add(body);
                }
            }

            int riversToSpawn = node.targetRiverCount - currentRivers;
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

            // Drain-out rivers, on the same "fraction of the world's chunks" basis as the
            // lakes and rounded the same way. There can never be more outlets than lakes,
            // because each one is a lake's overflow.
            int desiredOutlets = Mathf.Clamp(
                Mathf.RoundToInt(totalChunks * Mathf.Clamp01(node.lakeOutletChunkPercentage)),
                0, desiredLakes);
            int outletsToSpawn = desiredOutlets - currentOutlets;

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
            if (riversToSpawn <= 0 && outletsToSpawn <= 0 && lakesToSpawn <= 0 && !oceanToSpawn)
            {
                Debug.Log($"[UC Hydrology] node {node.id}: nothing to build - the scene already "
                          + $"holds {currentRivers} river(s), {currentOutlets} outlet(s), "
                          + $"{currentLakes} lake(s) and {proceduralOceans.Count} ocean(s) against "
                          + $"targets {node.targetRiverCount}/{desiredOutlets}/{desiredLakes}/"
                          + (wantOcean ? "1" : "0") + ". Raise a target, or clear the WaterSystem "
                          + "child to rebuild from scratch.");
                return;
            }

            Terrain[] allTerrains = Terrain.activeTerrains;
            if (allTerrains == null || allTerrains.Length == 0)
            {
                Debug.LogWarning($"[UC Hydrology] node {node.id}: no active Terrain in the scene, so "
                                 + "there is no ground to read heights from and no water was built. "
                                 + "Generate the terrain first.");
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

            for (int i = 0; i < lakeCenters.Count && lakesMade < lakesToSpawn; i++)
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

                if (outline == null) { lakeNoContour++; continue; }
                if (lakeArea < MinLakeAreaSquareMetres) { lakeTooSmall++; continue; }

                // How deep the water stands over the basin floor. FloodBasin seeds its
                // flood at centerPoint and raises the level from there, so the water above
                // that point is the deepest the lake gets. If even that is under the
                // floor, no cell in the basin clears it and the Lake Mask marks nothing
                // here - so placing a Lake object would be placing water the graph does
                // not agree is there.
                if (fillLevel - centerPoint.y < minLakeDepthMetres)
                {
                    lakeTooShallow++;
                    continue;
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
            }

            // --- Spawn the ocean (T10) ---
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

            // --- Rivers (T17) and lake drain-out rivers (spec 2.2) ---
            // Built LAST on purpose. A river's downstream end has to be real water, and
            // UCWaterBody's auto-connect scans the LIVE scene for something to join, so
            // the sea and the lakes must already exist by the time a river is built - or
            // its first-pass join finds nothing and the course ends in mid-air. That is
            // why this moved below the lake and ocean spawns rather than above them.
            //
            // Trunk rivers and lake outlets share this whole block: one drainage solve,
            // one spawn loop. They differ only in where the planner starts the course, so
            // giving them separate loops would be two copies of the same river object.
            int plannedTrunks = 0;
            int plannedTributaries = 0;
            int plannedOutlets = 0;
            int distributariesMade = 0;
            int riversMade = 0;

            if (riversToSpawn > 0 || outletsToSpawn > 0)
            {
                // Where the lakes actually are, read back from the scene so generated,
                // hand-placed and just-spawned lakes are ONE list instead of three.
                List<Vector3> lakeCentres = new List<Vector3>();
                // Index-aligned with lakeCentres, because that list skips nulls: a
                // planner index read back against allLakes would slide by one per null
                // and join an outlet to the wrong lake.
                List<UCWaterBody> lakeBodies = new List<UCWaterBody>();
                // Index-aligned too. Each lake carries ITS OWN reach: a single shared
                // maximum handed a hillside pond the span of the largest lake in the
                // world, so the planner marked hundreds of metres of dry ground as that
                // pond's water and stopped rivers short of anything they could join.
                List<float> lakeRadii = new List<float>();
                for (int i = 0; i < allLakes.Count; i++)
                {
                    UCWaterBody lake = allLakes[i];
                    if (lake == null) continue;
                    lakeCentres.Add(lake.transform.position);
                    lakeBodies.Add(lake);
                    lakeRadii.Add(OutlineReach(lake));
                }

                // THE LATTICE THE MASK WAS SOLVED ON, NOT ONE OF ITS OWN
                // The node's River Mask traces the network on a grid of
                // node.drainageResolution cells per side; this plans the river OBJECTS
                // that have to sit in the trench that mask carved. Solved on a different
                // grid the two disagree about where the valley floor is - D8 turns on
                // whole cells, so a coarser or finer lattice picks a different cell and
                // the course drifts from the trench by up to a cell's width. One
                // setting, one lattice, so the object lands in its own channel.
                //
                // Floored at 64 because a course cannot be traced across a world only a
                // few dozen cells wide; RiverNetworkPlanner.Solve caps the top at 512.
                int gridPerSide = Mathf.Max(64, node.drainageResolution);

                RiverNetworkPlanner.DrainageGrid drainage =
                    RiverNetworkPlanner.Solve(ground, bounds, gridPerSide);

                // Editable nodes every few cells: dense enough to hold the course's shape,
                // sparse enough that a user can grab one and move it.
                float seedSpacing = drainage != null
                    ? Mathf.Max(drainage.CellSizeMetres * 3f, 40f)
                    : 60f;

                // Main stems plus the branches that join them. minTributaries is the
                // node's own "how many small rivers merge to form a big one", so the
                // network's shape is the user's setting rather than a hidden constant.
                // channelHeadArea is the node's too, and for a sharper reason: it is the
                // figure its River Mask stamped at, so passing anything else would plan
                // courses for a network the graph did not draw - or, on a small world
                // where the user lowered it, plan none at all.
                List<RiverNetworkPlanner.PlannedRiver> plans =
                    RiverNetworkPlanner.PlanTrunkRivers(drainage, riversToSpawn,
                                                       Mathf.Max(0, node.minTributaries),
                                                       seaLevelMetres,
                                                       lakeCentres, lakeRadii, seedSpacing,
                                                       node.channelHeadArea);

                // Each plan carries the name its object will take, so the three budgets
                // stay tellable apart on the next Generate without re-reading any course:
                // "River N" is a main stem, "Tributary N" joins one, "Outlet N" drains a
                // lake. Numbering continues past whatever already stands in the scene.
                List<string> planNames = new List<string>(plans.Count);
                int nextRiver = currentRivers;
                int nextTributary = highestTributaryIndex;

                for (int i = 0; i < plans.Count; i++)
                {
                    bool isTributary = plans[i].joinsPlanIndex >= 0;
                    if (isTributary) plannedTributaries++; else plannedTrunks++;

                    planNames.Add(isTributary
                        ? $"Tributary {++nextTributary}"
                        : $"River {++nextRiver}");
                }

                // Lake overflow, from the same solved drainage. Appended to the same list
                // so both kinds are sized, shaped and spawned by the one loop below - and
                // so the size scale is anchored by the largest catchment in the world,
                // whichever kind of river happens to hold it. Appending leaves every
                // joinsPlanIndex above pointing at the same course it did.
                List<RiverNetworkPlanner.PlannedRiver> outlets =
                    RiverNetworkPlanner.PlanLakeOutlets(drainage, outletsToSpawn, seaLevelMetres,
                                                        lakeCentres, lakeRadii, seedSpacing);

                for (int i = 0; i < outlets.Count; i++)
                {
                    plans.Add(outlets[i]);
                    planNames.Add($"Outlet {highestOutletIndex + i + 1}");
                    plannedOutlets++;
                }

                // The largest catchment in THIS world anchors the size scale below.
                float largestCatchment = 1f;
                for (int i = 0; i < plans.Count; i++)
                    largestCatchment = Mathf.Max(largestCatchment, plans[i].catchmentSquareMetres);

                // Index-aligned with plans, so a tributary can be handed the actual trunk
                // object it joins. Filled at creation rather than at the end of the loop
                // body, because the delta section below can continue out of the iteration
                // and a missed slot would shift every later index.
                UCWaterBody[] spawnedBodies = new UCWaterBody[plans.Count];

                for (int i = 0; i < plans.Count; i++)
                {
                    RiverNetworkPlanner.PlannedRiver plan = plans[i];
                    if (plan == null || plan.course == null || plan.course.Count < 2) continue;

                    Vector3 head = plan.course[0];

                    GameObject riverGo = new GameObject(planNames[i]);
                    riverGo.transform.SetParent(waterRoot);
                    riverGo.transform.position = head;
                    riverGo.layer = uc.waterLayer;

                    UCWaterBody river = riverGo.AddComponent<UCWaterBody>();
                    spawnedBodies[i] = river;
                    river.SetKindOnce(WaterBodyKind.River);
                    // Same object a hand-placed river is: shape, width, spacing and look
                    // come from the one shared table, then the manager's own defaults -
                    // exactly what the create menu does. No second field-set to drift.
                    WaterPresets.ApplyDefaults(river);
                    WaterPresets.ApplyManagerDefaults(river);
                    // Procedural policy the menu leaves off: a generated river carves its
                    // bed (digTerrain is on by default). It does NOT re-fit its course -
                    // SeatCourse above already did that once, bounded to the corridor.
                    // Leaving these on ran a second, 2 km-wide solve on live terrain and
                    // slid the water off the trench that had just been cut for it.
                    river.fitToValley = false;
                    river.routeAroundHighGround = false;

                    // The course is authored in the body's own space, head at the origin.
                    List<Vector3> local = new List<Vector3>(plan.course.Count);
                    for (int p = 0; p < plan.course.Count; p++) local.Add(plan.course[p] - head);
                    river.course = new Polyline(local);

                    // Per-node size, graded along the course by the land each point
                    // actually drains. Filled below from the drainage solve; a node left
                    // at zero means "inherit the body's", which is what WorldCourse
                    // resolves before it tessellates.
                    river.nodeDepthMetres = new List<float>(new float[local.Count]);
                    river.nodeWidthMetres = new List<float>(new float[local.Count]);

                    // --- Size from catchment, not from Random (T19's river half) ---
                    // Downstream hydraulic geometry (Leopold & Maddock): bankfull width
                    // rises with the square root of discharge, and discharge with about
                    // the 0.8 power of drainage area, so width goes as area^0.4.
                    //
                    // Applied as a RATIO against the biggest catchment in this world, not
                    // as absolute physics - because absolute physics on a 5 km world gives
                    // a 9 m trunk river, and the spec calls for a visible 40-80 m one. So
                    // the exponent is real and sets the relative sizes; the spec's own
                    // BigRiverWidthMetres sets the absolute scale. Both are honoured, and
                    // no number is guessed.
                    float ratio = Mathf.Clamp01(plan.catchmentSquareMetres / largestCatchment);
                    float width = BigRiverWidthMetres * Mathf.Pow(ratio, WidthAreaExponent);

                    // A trunk - a course that joins no other course, which is exactly
                    // what the node's river count sets - is one of the spec's "bigger
                    // rivers" and must measure 40-80 m. A tributary is not: forcing a
                    // headwater brook up to 40 m would flatten the very hierarchy the
                    // power law above builds, so a joining course keeps the continuous
                    // law all the way down to MinRiverWidthMetres.
                    float floorWidth = plan.joinsPlanIndex < 0
                        ? TrunkMinWidthMetres
                        : MinRiverWidthMetres;
                    river.widthMetres = Mathf.Clamp(width, floorWidth, BigRiverWidthMetres);

                    // Alluvial channels run about 18 times wider than they are deep. That
                    // ratio is why the old 10-20 m depths read as slots rather than rivers.
                    river.channelDepthMetres =
                        Mathf.Clamp(river.widthMetres / WidthToDepthRatio, 0.4f, 8f);

                    // --- Source to mouth, from the land each node actually drains ---
                    // The widths above size the river as ONE object, at its mouth. A real
                    // river is not one width: it starts where a channel first forms and
                    // grows every time more land drains through it. So each node is sized
                    // by its own catchment, read from the same solved grid that planned
                    // the course, under the same area^0.4 law - which makes the last node
                    // land back on river.widthMetres by construction, because the plan's
                    // own catchment was read from this grid at this course's mouth.
                    //
                    // The accumulation is taken as a running maximum down the course. D8
                    // flow directions turn on whole cells, so a course point can round to
                    // a cell just off the thread of the channel and read a catchment
                    // smaller than the reach above it. Water does not lose its watershed
                    // halfway down, and honouring such a dip would pinch the channel to a
                    // thread and then reopen it - the discontinuous, zigzag width the
                    // spec calls out. A maximum lets the river widen and never narrow.
                    float mouthCatchment = Mathf.Max(1f, plan.catchmentSquareMetres);
                    float cellArea = drainage != null ? drainage.CellAreaSquareMetres : 0f;
                    float drained = 0f;

                    for (int p = 0; drainage != null && p < local.Count; p++)
                    {
                        Vector3 world = plan.course[p];
                        int cell = drainage.CellAt(world.x, world.z);

                        drained = Mathf.Max(drained, drainage.accum[cell] * cellArea);

                        float share = Mathf.Clamp01(drained / mouthCatchment);
                        float nodeWidth = river.widthMetres * Mathf.Pow(share, WidthAreaExponent);

                        // Floored at the narrowest channel that gets built at all, and
                        // capped at the body's own width: the mouth is the widest the
                        // river gets, and the estuary flare below is the one exception.
                        river.nodeWidthMetres[p] =
                            Mathf.Clamp(nodeWidth, MinRiverWidthMetres, river.widthMetres);

                        // Same width-to-depth ratio as the body, so a headwater reach is
                        // shallow as well as narrow instead of a narrow slot cut deep.
                        river.nodeDepthMetres[p] =
                            Mathf.Clamp(river.nodeWidthMetres[p] / WidthToDepthRatio, 0.4f, 8f);
                    }

                    // A river widens where it meets the sea - the estuary. Over the last
                    // stretch only: halfway up its length is not a delta.
                    if (plan.mouth == RiverNetworkPlanner.MouthKind.Ocean)
                    {
                        int estuaryStart = Mathf.Max(1, local.Count - 1 -
                                                     Mathf.Max(1, Mathf.RoundToInt(local.Count * EstuaryFraction)));
                        int span = Mathf.Max(1, local.Count - 1 - estuaryStart);

                        for (int p = estuaryStart; p < local.Count; p++)
                        {
                            float t = (p - estuaryStart) / (float)span;

                            // Flares the GRADED width where there is one. A zero means the
                            // grading did not run - the same "unset, inherit the body"
                            // that ResolveNodeSeries reads it as - so the body's width is
                            // the thing flared instead of multiplying the zero away.
                            float at = river.nodeWidthMetres[p] > 1e-4f
                                ? river.nodeWidthMetres[p]
                                : river.widthMetres;

                            river.nodeWidthMetres[p] = at * (1f + t * (EstuaryFlare - 1f));
                        }
                    }

                    // --- Junctions (spec 11.5d) ---
                    // Auto-connect deliberately guesses only ONE case: a mouth arriving
                    // at an ocean or a lake. It will not guess a river-to-river
                    // confluence, and it never guesses a head at all - see
                    // UCWaterBody.ResolveJoin. Those are exactly the two the planner
                    // already knows, so they are stated here instead of re-derived:
                    // without them a tributary stops beside its trunk at its own surface
                    // level and the confluence reads as a step between two water planes.
                    //
                    // The joining body is the one that ENDS at the junction, which is the
                    // side that carries the reference; the trunk keeps the shape its own
                    // drainage gave it. Plans are ordered parent-before-child and each is
                    // rebuilt in turn, so the trunk is already built when the tributary
                    // asks it for the edge point to snap to.
                    if (plan.joinsPlanIndex >= 0 && plan.joinsPlanIndex < spawnedBodies.Length)
                    {
                        UCWaterBody trunk = spawnedBodies[plan.joinsPlanIndex];
                        if (river.CanJoin(trunk)) river.joinDownstreamTo = trunk;
                    }

                    // A lake outlet starts IN its lake. SnapConfluences does not touch a
                    // head, so without this the channel begins at the sill with a gap
                    // back to the water it drains.
                    if (plan.fromLakeIndex >= 0 && plan.fromLakeIndex < lakeBodies.Count)
                    {
                        UCWaterBody source = lakeBodies[plan.fromLakeIndex];
                        if (river.CanJoin(source)) river.joinUpstreamFrom = source;
                    }

                    river.Rebuild();
                    ReportIfDry(river, riverGo.name);
                    riversMade++;

                    // Head and mouth elevations rather than one surface height: a river's
                    // surface is a profile, and printing both ends is what shows at a
                    // glance that it descends instead of climbing.
                    bodyLines.Add("    " + riverGo.name + " - river, order "
                                  + plan.streamOrder + ", into " + plan.mouth + ", "
                                  + Mathf.RoundToInt(plan.lengthMetres) + " m long, "
                                  + river.widthMetres.ToString("0.##") + " m wide, "
                                  + plan.course[0].y.ToString("0.##") + " m -> "
                                  + plan.course[plan.course.Count - 1].y.ToString("0.##") + " m");

                    // --- Delta distributaries (spec 1.10) ---
                    // Arms leave the trunk near its mouth and each finds its own way to the
                    // sea by following the low ground beside it, which is what a delta is.
                    //
                    // A delta belongs to a MAJOR river - one with a real network behind
                    // it, not a single stream that happens to reach the coast. Strahler
                    // order is the standard measure of that: with one level of tributaries
                    // traced, order 2 is the top of the achievable range and means at
                    // least two comparable branches join this course. The catchment ratio
                    // then decides how many arms such a river gets, so the biggest river
                    // gets the fullest delta and a small one gets none - no dice roll.
                    int arms = node.enableDistributaries && plan.streamOrder >= DeltaMinStreamOrder
                        ? Mathf.Clamp(Mathf.RoundToInt(ratio * node.maxDistributaries), 0, node.maxDistributaries)
                        : 0;

                    if (arms <= 0 || drainage == null ||
                        plan.mouth != RiverNetworkPlanner.MouthKind.Ocean ||
                        plan.course.Count < 6) continue;

                    int splitIndex = Mathf.Clamp(Mathf.RoundToInt(plan.course.Count * 0.85f),
                                                 1, plan.course.Count - 2);
                    Vector3 splitPoint = plan.course[splitIndex];

                    Vector3 forward = plan.course[plan.course.Count - 1] - splitPoint;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 1e-4f) continue;
                    forward.Normalize();
                    Vector3 side = new Vector3(-forward.z, 0f, forward.x);

                    int spawnedArms = 0;
                    for (int d = 0; d < arms; d++)
                    {
                        // Spread the arms across the delta front, symmetrically about the
                        // trunk, then let the terrain decide where each one actually goes.
                        float offset = arms == 1 ? 0f : ((d / (float)(arms - 1)) * 2f - 1f);
                        Vector3 from = splitPoint + side * (offset * river.widthMetres * 3f);

                        List<Vector3> armCourse = RiverNetworkPlanner.PlanDownstream(
                            drainage, from, seaLevelMetres, river.widthMetres * 4f, seedSpacing);

                        if (armCourse == null || armCourse.Count < 2) continue;

                        spawnedArms++;

                        GameObject distGo = new GameObject(
                            $"{planNames[i]} Distributary {spawnedArms}");
                        distGo.transform.SetParent(waterRoot);
                        distGo.transform.position = armCourse[0];
                        distGo.layer = uc.waterLayer;

                        UCWaterBody distRiver = distGo.AddComponent<UCWaterBody>();
                        distRiver.SetKindOnce(WaterBodyKind.River);
                        WaterPresets.ApplyDefaults(distRiver);
                        WaterPresets.ApplyManagerDefaults(distRiver);
                        distRiver.fitToValley = false;
                        distRiver.routeAroundHighGround = false;

                        List<Vector3> armLocal = new List<Vector3>(armCourse.Count);
                        for (int p = 0; p < armCourse.Count; p++)
                            armLocal.Add(armCourse[p] - armCourse[0]);
                        distRiver.course = new Polyline(armLocal);

                        distRiver.nodeDepthMetres = new List<float>(new float[armLocal.Count]);
                        distRiver.nodeWidthMetres = new List<float>(new float[armLocal.Count]);

                        // A delta's arms together carry rather more width than the single
                        // trunk did, because the flow spreads and shallows as it splits.
                        distRiver.widthMetres = Mathf.Max(MinRiverWidthMetres,
                                                          river.widthMetres * DeltaArmShare / arms);
                        distRiver.channelDepthMetres =
                            Mathf.Clamp(distRiver.widthMetres / WidthToDepthRatio, 0.3f, 8f);

                        // An arm leaves the trunk, so the trunk is its upstream body - the
                        // same statement a tributary makes at the other end. The trunk was
                        // rebuilt above, so its edge is there to snap the arm's head onto.
                        if (distRiver.CanJoin(river)) distRiver.joinUpstreamFrom = river;

                        distRiver.Rebuild();
                        distributariesMade++;
                    }
                }
            }

            // --- One report per Generate ---
            // Everything a "nothing appeared" question needs, in one line-block: which node
            // asked, the datum in BOTH units (the node stores 0..1, the world is metres, and
            // a mismatch between them is invisible unless both are printed), what was asked
            // for against what stands, why each rejected basin was rejected, and a line per
            // body actually built so the result can be checked without opening the hierarchy.
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
            report.Append("  rivers     : asked ").Append(riversToSpawn)
                  .Append(", planned ").Append(plannedTrunks).Append(" trunk(s) + ")
                  .Append(plannedTributaries).Append(" tributary(ies), outlets asked ")
                  .Append(outletsToSpawn).Append(" planned ").Append(plannedOutlets)
                  .Append(", built ").Append(riversMade)
                  .Append(", distributaries ").Append(distributariesMade).Append('\n');
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

        /// <summary>Nothing narrower than this gets built as a river object.</summary>
        /// <remarks>Shared with the spawner's River Mask - see BigRiverWidthMetres.</remarks>
        internal const float MinRiverWidthMetres = 4f;

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

            const float MinRelief = 2f;      // metres the surroundings must rise
            const float MinEnclosure = 0.6f; // fraction of bearings that must be higher

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

        /// <summary>Largest standing water that counts as a lake: 720 m^2, a patch about
        /// thirty metres across. A basin that could hold more is filled to this and no
        /// further - the water beyond it belongs to the ocean pass, not here. It also sets
        /// how wide a neighbourhood the search looks at.</summary>
        public const float MaxLakeAreaSquareMetres = 720f;

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

        private static float GetHeightAt(Terrain[] terrains, float x, float z)
        {
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
