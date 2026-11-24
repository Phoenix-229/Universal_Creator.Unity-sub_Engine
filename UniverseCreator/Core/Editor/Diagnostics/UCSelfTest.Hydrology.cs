// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  Module 2: Terrain / Water
//
//  File:    UCSelfTest.Hydrology.cs
//  Purpose: The Hydrology Spawner half of the self test. Asserts that the spawner
//           is wired, live, and actually places water OBJECTS - not that it
//           compiles, which the compiler already covers.
//
//  WHY A SECOND FILE
//  -----------------
//  UCSelfTest.cs is already past two thousand lines. A partial class shares its
//  counter and its Check helper, so these checks land in the same tally and the
//  same report without that file growing again.
//
//  WHAT THESE CHECKS ARE FOR
//  -------------------------
//  Every one of them pins a fault that was REPORTED from the scene, where each
//  looked identical: a graph that carved riverbeds and lake basins into the ground
//  and then put no water in them, with nothing said in the console.
//
//    - The spawner judged only by mask reachability. Its Height input was fed and
//      its two mask outputs left dangling, which is the ordinary way to wire it,
//      and the walk reported it unconnected. TestSpawnerIsLiveWhenOnlyItsHeightIsWired.
//
//    - The object side accepted a basin on AREA alone while the Lake Mask rejected
//      it on DEPTH, so a wide ankle-deep dimple received a Lake object with no mask
//      and no bed under it. TestLakeDepthGateMatchesTheMask.
//
//    - And the one that covers all of them from the outside: run the generator on
//      real ground and require that every body it placed carries a built surface.
//      TestSpawnerPlacesRealWaterObjects.
//
//  THE STRONG ASSERTION, AND WHY IT IS THE ONE THAT MATTERS
//  --------------------------------------------------------
//  "UC Water Surface" is the child GameObject UCWaterBody.RebuildSurface creates.
//  It is created AFTER the null-body early-out, so its presence is proof that the
//  body produced real geometry and resolved a real material. A body with correct
//  fields and no such child is exactly the reported fault - water in the hierarchy,
//  none in the world - and it is silent, which is why it is asserted rather than
//  eyeballed.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // The child RebuildSurface creates once it has geometry. Named once here
        // because four separate checks below turn on it.
        private const string WaterSurfaceChild = "UC Water Surface";

        // =====================================================================
        //  2. A spawner fed only its Height input is live
        // =====================================================================

        /// <summary>
        /// The Hydrology Spawner's product is not a matrix - it places river, lake and
        /// ocean OBJECTS, reading the finished ground straight off Terrain.activeTerrains.
        /// Its two mask ports are an optional extra service. So judging it by whether
        /// those masks reach an Output is asking the wrong question about the wrong half
        /// of the node, and it is the question that made a fully configured spawner sit
        /// inert with its Height chain wired.
        /// </summary>
        /// <remarks>
        /// Being wired is still required, and that half is asserted too: a spawner
        /// dropped into a graph and left completely unconnected has not been asked for
        /// anything and must not flood a world by its mere presence. Water appears only
        /// where the graph asks for it.
        ///
        /// ConnectedHydrology and ResolveHydrology are checked together on purpose.
        /// They are the count the inspector shows and the node that actually runs; if
        /// they could ever disagree, the inspector would report a live spawner that
        /// produced nothing, which is indistinguishable from this whole class of fault.
        /// </remarks>
        private static void TestSpawnerIsLiveWhenOnlyItsHeightIsWired()
        {
            Graph wired = ScriptableObject.CreateInstance<Graph>();
            Graph orphan = ScriptableObject.CreateInstance<Graph>();

            try
            {
                // Noise -> Height Output is the live chain. Noise -> Spawner.Height
                // feeds the spawner. Both spawner mask outputs are left dangling,
                // which is exactly how the node is ordinarily wired.
                PerlinNoiseNode noise = new PerlinNoiseNode();
                HeightOutputNode output = new HeightOutputNode();
                HydrologyNode spawner = new HydrologyNode();

                wired.nodes.Add(noise);
                wired.nodes.Add(output);
                wired.nodes.Add(spawner);

                Connection toOutput = new Connection();
                toOutput.sourceNodeId = noise.id;
                toOutput.sourcePortIndex = 0;
                toOutput.targetNodeId = output.id;
                toOutput.targetPortIndex = 0;
                wired.connections.Add(toOutput);

                Connection toSpawner = new Connection();
                toSpawner.sourceNodeId = noise.id;
                toSpawner.sourcePortIndex = 0;
                toSpawner.targetNodeId = spawner.id;
                toSpawner.targetPortIndex = 0;
                wired.connections.Add(toSpawner);

                List<HydrologyNode> found = UCWaterSystemManager.ConnectedHydrology(wired);

                Check(found.Count == 1,
                      "a Hydrology Spawner with its Height input fed and its mask outputs left "
                      + "dangling was counted " + found.Count + " time(s) instead of once. That "
                      + "is the ordinary way to wire the node: its masks are optional and there "
                      + "is no wiring that makes a river mask terminate in a Height Output on "
                      + "its own merits, so judged by mask reachability the node can never be live");

                HydrologyNode resolved = UCWaterSystemManager.ResolveHydrology(wired, null);

                Check(resolved == spawner,
                      "the spawner the inspector counts and the node that actually runs "
                      + "disagreed: ResolveHydrology returned "
                      + (resolved == null ? "null" : "a different node")
                      + ". A count that says live while the resolver says none is a world that "
                      + "reports water and generates none");

                // The other half of the rule: presence alone is not a request.
                HydrologyNode alone = new HydrologyNode();
                HeightOutputNode lonely = new HeightOutputNode();
                orphan.nodes.Add(alone);
                orphan.nodes.Add(lonely);

                Check(UCWaterSystemManager.ConnectedHydrology(orphan).Count == 0
                      && UCWaterSystemManager.ResolveHydrology(orphan, null) == null,
                      "a Hydrology Spawner dropped into a graph and left completely unconnected "
                      + "was treated as live. Water appears only where the graph asks for it, so "
                      + "an unwired node must flood nothing");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wired);
                UnityEngine.Object.DestroyImmediate(orphan);
            }
        }

        // =====================================================================
        //  4. The spawner places real water objects on real ground
        // =====================================================================

        /// <summary>
        /// The end-to-end check, and the one that answers the report in its own terms:
        /// run HydrologyGenerator over a real Terrain and require that the bodies it
        /// placed actually hold water.
        /// </summary>
        /// <remarks>
        /// WHY "UC Water Surface" IS THE THING ASSERTED
        /// UCWaterBody.RebuildSurface asks BuildData for geometry and returns early -
        /// before it creates that child - whenever the answer is null, which Assemble
        /// returns the moment not one quad of the footprint came out wet. So a Lake,
        /// Ocean or River GameObject can stand in the hierarchy with every field
        /// correctly filled in, no mesh, no material, no child, and nothing at all in
        /// the console. Checking that the objects EXIST would pass on precisely the
        /// world the report describes. Checking that each one carries a built surface
        /// is the same assertion the eye makes when it looks for water.
        ///
        /// THE GROUND, AND WHY IT IS SHAPED THE WAY IT IS
        /// Nothing here is arbitrary: each feature is the minimum real landform the
        /// piece under test needs.
        ///   - A regional slope falling 300 m to 80 m across 2 km, so water has a
        ///     direction to go and a coastline has somewhere to form. The sea level is
        ///     the median height of the inset perimeter, so about half that perimeter
        ///     stands below it and the ocean has a basin.
        ///   - One trunk valley, a 40 m Gaussian trough along the centre line. D8 flow
        ///     needs convergent topography or every cell drains straight down the fall
        ///     line in parallel, accumulates nothing, and no cell ever reaches
        ///     channel-head area. A valley is what makes a river a river.
        ///   - Two closed bowls, 60 m deep over a 60 m radius, on the valley flanks.
        ///     Closed because a lake needs somewhere the water cannot leave; deeper than
        ///     the surrounding relief so they rank as the lowest enclosed basins and are
        ///     tried first; far enough apart that neither is culled as a duplicate of
        ///     the other.
        ///
        /// The spawner is given targetLakeCount 2 with lakeChunkPercentage 0, which also
        /// pins the rule that a typed count is an instruction and not a ceiling the
        /// density may veto.
        /// </remarks>
        private static void TestSpawnerPlacesRealWaterObjects()
        {
            GameObject terrainGo = new GameObject("UC_SelfTest_Hydrology_Terrain");
            GameObject worldGo = new GameObject("UC_SelfTest_Hydrology_World");
            TerrainData data = BuildHydrologyTestTerrain();

            try
            {
                terrainGo.transform.position = Vector3.zero;
                Terrain terrain = terrainGo.AddComponent<Terrain>();
                terrain.terrainData = data;

                worldGo.transform.position = Vector3.zero;
                UniverseCreatorObject uc = worldGo.AddComponent<UniverseCreatorObject>();
                terrainGo.transform.SetParent(worldGo.transform, true); // UC-T08-15: native ground belongs to the sampled world, as in production chunks.
                uc.worldSizeKm = HydrologyTestWorldMetres / 1000f;
                uc.maxHeightMeters = HydrologyTestHeightMetres;
                // Chunk count is derived from size now: 2000 m world / 400 m tiles = 5x5,
                // the grid this test was written against. Setting the size keeps that grid
                // whether or not the generator re-validates.
                uc.chunkSizeMeters = 400f;
                uc.chunkCountPerSide = 5;
                uc.resolution = HydrologyTestHeightmapRes;
                uc.waterLayer = 0;

                HydrologyNode node = new HydrologyNode();
                node.generateOcean = true;
                node.targetLakeCount = 2;
                node.lakeChunkPercentage = 0f;         // the count must stand on its own
                node.lakeOutletChunkPercentage = 0f;
                node.drainageResolution = 256;

                HydrologyGenerator.GenerateFromNode(uc, node);

                Transform waterRoot = worldGo.transform.Find("WaterSystem");

                Check(waterRoot != null,
                      "the generator ran and left no WaterSystem child under the world. That "
                      + "child is created unconditionally at the top of GenerateFromNode, so "
                      + "its absence means the generator returned before doing anything");

                List<UCWaterBody> placed = new List<UCWaterBody>();
                if (waterRoot != null)
                {
                    foreach (Transform child in waterRoot)
                    {
                        UCWaterBody body = child.GetComponent<UCWaterBody>();
                        if (body != null) placed.Add(body);
                    }
                }

                int lakesWithWater = 0;
                int oceansWithWater = 0;
                string dry = "";

                for (int i = 0; i < placed.Count; i++)
                {
                    UCWaterBody body = placed[i];
                    if (body.transform.Find(WaterSurfaceChild) == null)
                    {
                        if (dry.Length > 0) dry += ", ";
                        dry += body.name;
                        continue;
                    }
                    if (body.kind == WaterBodyKind.Lake) lakesWithWater++;
                    else if (body.kind == WaterBodyKind.Ocean) oceansWithWater++;
                }

                Check(lakesWithWater > 0,
                      "two closed 60 m bowls were put in the terrain and the spawner was told to "
                      + "place two lakes, and " + lakesWithWater + " lake(s) came out holding "
                      + "water. A basin that deep holds water in the world we live in, so this "
                      + "is the spawner's lake half doing nothing");

                Check(oceansWithWater > 0,
                      "Generate Ocean was on, half the world's perimeter stands below the median "
                      + "coastal height the sea level is taken from, and " + oceansWithWater
                      + " ocean(s) came out holding water. There is a basin for it; an ocean "
                      + "object with no surface is water that exists only in the hierarchy");

                Check(dry.Length == 0,
                      "the generator placed water bodies that hold no water: " + dry + ". Each "
                      + "stands in the hierarchy with its fields filled in and no mesh, no "
                      + "material and no " + WaterSurfaceChild + " child, because RebuildSurface "
                      + "returns before creating it when not one quad of the footprint came out "
                      + "wet - silently, which is why the eye finds carved channels and no river");

                // UC-T08-15: raising the target after carving must not turn a kept basin into a second body.
                var retainedLakes = new List<UCWaterBody>();
                foreach (var body in placed)
                    if (body.kind == WaterBodyKind.Lake) { body.placementSaved = true; retainedLakes.Add(body); }
                var overlap = typeof(HydrologyGenerator).GetMethod("LakeFootprintsOverlap",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                for (int retry = 0; retry < 2; retry++)
                {
                    int current = 0;
                    foreach (var body in waterRoot.GetComponentsInChildren<UCWaterBody>(true))
                        if (body.kind == WaterBodyKind.Lake) current++;
                    node.targetLakeCount = current + 1;
                    HydrologyGenerator.GenerateFromNode(uc, node);
                    var shores = new List<(Vector2[] ring, Bounds bounds)>();
                    bool valid = retainedLakes.Count > 0 && overlap != null, unique = true, retained = true;
                    foreach (var body in waterRoot.GetComponentsInChildren<UCWaterBody>(true))
                    {
                        if (body.kind != WaterBodyKind.Lake) continue;
                        if (!body.TryGetWorldFootprint(out Vector2[] ring, out Bounds bounds)) { valid = false; continue; }
                        if (overlap != null)
                            foreach (var shore in shores)
                                unique &= !(bool)overlap.Invoke(null, new object[] { ring, bounds, shore.ring, shore.bounds });
                        shores.Add((ring, bounds));
                    }
                    foreach (var body in retainedLakes)
                        retained &= body != null && body.placementSaved && body.transform.IsChildOf(waterRoot);
                    Check(valid && unique && shores.Count >= retainedLakes.Count,
                        "UC-T08-15 native spawner retry " + retry + " accepts only disjoint lake footprints after terrain carving.");
                    Check(retained, "UC-T08-15 native spawner retry " + retry + " preserves saved lake bodies and their identities.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(worldGo);
                UnityEngine.Object.DestroyImmediate(terrainGo);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        // The test world: 2 km square, 600 m of height range, a 257 heightmap. Small
        // enough to build and solve inside a self test, big enough to hold a trunk
        // valley and two separated basins at real-world proportions.
        private const float HydrologyTestWorldMetres = 2000f;
        private const float HydrologyTestHeightMetres = 600f;
        private const int HydrologyTestHeightmapRes = 257;

        /// <summary>
        /// The ground TestSpawnerPlacesRealWaterObjects runs on: a regional slope, one
        /// trunk valley, and two closed bowls on its flanks. Built rather than loaded so
        /// the test carries its own landscape and cannot be broken by a scene edit.
        /// </summary>
        private static TerrainData BuildHydrologyTestTerrain()
        {
            TerrainData data = new TerrainData();
            data.heightmapResolution = HydrologyTestHeightmapRes;
            data.size = new Vector3(HydrologyTestWorldMetres,
                                    HydrologyTestHeightMetres,
                                    HydrologyTestWorldMetres);

            int n = data.heightmapResolution;
            float[,] h = new float[n, n];

            for (int zi = 0; zi < n; zi++)
            {
                float z = HydrologyTestWorldMetres * zi / (n - 1);
                for (int xi = 0; xi < n; xi++)
                {
                    float x = HydrologyTestWorldMetres * xi / (n - 1);

                    // The regional slope: 300 m down to 80 m across the world, so every
                    // drop of water has one direction to go and the coast has a place
                    // to be. Without a gradient there is no drainage to solve.
                    float metres = 300f - 0.11f * z;

                    // The trunk valley. D8 flow on a plane runs down the fall line in
                    // parallel and never converges, so no cell ever accumulates a
                    // catchment and no cell ever becomes a river source. A trough is
                    // what makes the flow gather, and gathered flow is what a river is.
                    float u = (x - 1000f) / 350f;
                    metres -= 40f * Mathf.Exp(-u * u);

                    // Two closed basins on the valley flanks. Deeper than the relief
                    // around them so they rank as the lowest enclosed bowls, and 1130 m
                    // apart so neither is de-clustered away as the other's neighbour.
                    metres -= HydrologyTestBowl(x, z, 600f, 600f);
                    metres -= HydrologyTestBowl(x, z, 1400f, 1400f);

                    h[zi, xi] = Mathf.Clamp01(metres / HydrologyTestHeightMetres);
                }
            }

            data.SetHeights(0, 0, h);
            return data;
        }

        /// <summary>A conical basin 60 m deep over a 60 m radius - a one-in-one slope,
        /// steep enough that the water surface reaches the lake ceiling well before the
        /// rim, so the basin and not the rim is what decides the lake.</summary>
        private static float HydrologyTestBowl(float x, float z, float cx, float cz)
        {
            float dx = x - cx;
            float dz = z - cz;
            float r = Mathf.Sqrt(dx * dx + dz * dz);
            return r >= 60f ? 0f : 60f * (1f - r / 60f);
        }

        // =====================================================================
        //  5. The object side rejects exactly what the mask leaves dry
        // =====================================================================

        /// <summary>
        /// minLakeDepth is a fraction of world height, because the Lake Mask works in the
        /// graph's 0..1 height space, while lake objects are placed in metres. The object
        /// side used to accept a basin on AREA alone, so a wide ankle-deep dimple that
        /// the mask left completely dry still received a Lake object: a water surface
        /// with no mask under it, and so no bed carved beneath it either.
        /// </summary>
        /// <remarks>
        /// That is a stub in the precise sense the report used. The spawner's own field
        /// decided where water was MARKED and had no say at all in where water was
        /// PLACED, so turning it did nothing an eye could see.
        ///
        /// Both basins here flood past the area floor, which is the point: area alone
        /// would admit either. Depth is the only thing that separates them, so this
        /// check fails the moment the object side stops consulting it.
        /// </remarks>
        private static void TestLakeDepthGateMatchesTheMask()
        {
            const float floorY = 100f;
            Bounds bounds = new Bounds(new Vector3(0f, 300f, 0f), new Vector3(8000f, 600f, 8000f));
            Vector3 seed = new Vector3(0f, floorY, 0f);

            // The same arithmetic HydrologyGenerator does: a fraction of world height,
            // converted to metres once. 0.002 of 600 m is 1.2 m of standing water.
            HydrologyNode node = new HydrologyNode();
            float gateMetres = Mathf.Max(0f, node.minLakeDepth) * HydrologyTestHeightMetres;

            // A steep basin - a one-in-2.5 slope. It reaches the lake area ceiling at
            // about six metres of water, well past the gate.
            HydrologyGenerator.GroundSampler steep = delegate (float x, float z)
            {
                return floorY + 0.40f * Mathf.Sqrt(x * x + z * z);
            };

            float steepLevel, steepArea;
            Polyline steepOutline = HydrologyGenerator.SolveLakeContour(
                steep, bounds, seed, out steepLevel, out steepArea);

            Check(steepOutline != null && steepLevel - floorY >= gateMetres,
                  "a basin that holds " + (steepLevel - floorY).ToString("0.##") + " m of water "
                  + "was measured against a " + gateMetres.ToString("0.##") + " m floor and "
                  + (steepOutline == null ? "produced no contour at all" : "did not clear it")
                  + ". Water this deep is marked wet by the Lake Mask, so refusing it here is "
                  + "the object side and the mask disagreeing about the same basin");

            // A shallow dish - a one-in-167 slope. It floods out to the area ceiling while
            // standing about a metre deep, still under the depth gate, so the Lake Mask
            // leaves it dry. (Slope re-calibrated to the raised MaxLakeAreaSquareMetres: at
            // the 90,000 m^2 ceiling the flood reaches ~169 m radius, so the slope has to be
            // gentle enough that a metre of rise is what fills it, which is the whole point of
            // the gate - a wide sheet the mask marks dry must be refused an object.)
            HydrologyGenerator.GroundSampler shallow = delegate (float x, float z)
            {
                return floorY + 0.006f * Mathf.Sqrt(x * x + z * z);
            };

            float shallowLevel, shallowArea;
            HydrologyGenerator.SolveLakeContour(shallow, bounds, seed,
                                                out shallowLevel, out shallowArea);

            Check(shallowLevel - floorY < gateMetres
                  && shallowArea >= HydrologyGenerator.MinLakeAreaSquareMetres,
                  "a dish holding " + (shallowLevel - floorY).ToString("0.##") + " m of water "
                  + "over " + shallowArea.ToString("F0") + " m^2 was expected to clear the area "
                  + "floor and fail the " + gateMetres.ToString("0.##") + " m depth gate, which "
                  + "is the whole case the gate exists for: wide enough to look like a lake, too "
                  + "shallow for the mask to mark any of it wet. Judged on area alone it receives "
                  + "a Lake object with no mask and no carved bed under it");
        }

    }
}
