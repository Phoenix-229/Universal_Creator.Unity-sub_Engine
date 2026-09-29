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
//    - The reachability walk seeded on the OutputNode base class while the
//      evaluator seeds on the IOutputNode interface. A graph terminating in a
//      Weather, Lighting, Wind, Shader or Scatter output evaluated in full and yet
//      read as "no water asked for", so the caller SWEPT every river, lake and
//      ocean out of the world. TestWaterWalkSeesInterfaceOnlyOutputs.
//
//    - The real-world channel-head area (a quarter of a square kilometre of
//      temperate hillslope) is larger than the entire catchment of a sub-kilometre
//      test world, so no cell ever qualified as a river source and no course was
//      ever planned. TestChannelHeadAreaRescalesToTheWorld.
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
        //  1. The channel-head area rescales to the world it is measuring
        // =====================================================================

        /// <summary>
        /// A permanent channel needs a catchment behind it, and the real-world figure
        /// for temperate hillslope is about a quarter of a square kilometre. A test
        /// world smaller than that has no cell that can ever qualify, so the planner
        /// finds no source, plans no course, and the world gets carved trenches with
        /// no river object in them.
        /// </summary>
        /// <remarks>
        /// WHY RESCALING IS PHYSICS AND NOT A FUDGE
        /// Drainage networks are scale-invariant: Hack's law (L proportional to A^h)
        /// and Horton's ratios are power laws, so the branching of a basin is
        /// statistically the same whatever size the basin is. The only part of the
        /// 250,000 m^2 figure that carries real climate and soil is its CALIBRATION,
        /// and that is precisely what a sub-kilometre world cannot host. Measuring
        /// the threshold against the world's own largest catchment keeps the network
        /// geometry exactly as the law describes it and moves only the calibration -
        /// which is the one part that was never transferable.
        ///
        /// The trunk multiple is the link between the two. A trunk river drains
        /// several channel-heads' worth of land, so the largest accumulation in the
        /// world divided by that multiple is the largest channel-head area the world
        /// can actually afford. Above the authored figure it is no constraint at all
        /// and the authored figure stands untouched - a full-size world is never
        /// quietly re-tuned by this.
        /// </remarks>
        private static void TestChannelHeadAreaRescalesToTheWorld()
        {
            const float cellArea = 100f;                 // a 10 m lattice
            const float requested = RiverNetworkPlanner.ChannelHeadAreaSquareMetres;

            // A world whose trunk drains 4 km^2. Affordable head area is 1 km^2,
            // four times the requested figure, so nothing is rescaled.
            float[] big = new float[] { 1f, 500f, 40000f, 12f };
            float bigAnswer = RiverNetworkPlanner.ResolveChannelHeadArea(big, cellArea, requested);

            Check(Mathf.Abs(bigAnswer - requested) < 1e-3f,
                  "a world whose largest catchment is 4 km^2 had its channel-head area moved from "
                  + requested + " m^2 to " + bigAnswer + " m^2. A world big enough to afford the "
                  + "real-world figure must be left on it - rescaling there would re-tune a "
                  + "full-size world nobody asked to re-tune");

            // A world whose trunk drains 40,000 m^2 - a 200 m square. It cannot
            // afford a quarter of a square kilometre, so the threshold comes down to
            // what its own trunk can support: 40,000 / 4.
            float[] small = new float[] { 1f, 7f, 400f, 3f };
            float smallAnswer = RiverNetworkPlanner.ResolveChannelHeadArea(small, cellArea, requested);
            float affordable = (400f * cellArea) / RiverNetworkPlanner.TrunkCatchmentMultiple;

            Check(Mathf.Abs(smallAnswer - affordable) < 1e-3f,
                  "a world whose largest catchment is 40,000 m^2 kept a channel-head area of "
                  + smallAnswer + " m^2 instead of the " + affordable + " m^2 its own trunk can "
                  + "support. Left on the real-world figure no cell in it ever qualifies as a "
                  + "river source, so the planner returns no course and the world gets trenches "
                  + "with no river in them");

            // Never finer than the lattice. A threshold below one cell marks every
            // cell a channel and paints the whole world wet.
            float[] tiny = new float[] { 1f, 1f };
            float tinyAnswer = RiverNetworkPlanner.ResolveChannelHeadArea(tiny, cellArea, requested);

            Check(tinyAnswer >= cellArea - 1e-3f,
                  "a one-cell catchment produced a channel-head area of " + tinyAnswer
                  + " m^2, under the " + cellArea + " m^2 of a single cell. A threshold finer "
                  + "than the lattice qualifies every cell and floods the world");

            // No drainage solved yet: there is nothing to measure against, so the
            // authored figure is handed back unchanged rather than invented over.
            float noAccum = RiverNetworkPlanner.ResolveChannelHeadArea(null, cellArea, 7777f);

            Check(Mathf.Abs(noAccum - 7777f) < 1e-3f,
                  "with no accumulation to measure, the authored 7777 m^2 came back as "
                  + noAccum + " m^2. With nothing to compare against there is no evidence to "
                  + "overrule the author with");

            // Zero means "use the real-world constant", which is what the node
            // defaults quote - so the default and the fallback cannot drift apart.
            float zeroRequest = RiverNetworkPlanner.ResolveChannelHeadArea(null, cellArea, 0f);

            Check(Mathf.Abs(zeroRequest - RiverNetworkPlanner.ChannelHeadAreaSquareMetres) < 1e-3f,
                  "an unset channel-head area fell back to " + zeroRequest + " m^2 rather than the "
                  + RiverNetworkPlanner.ChannelHeadAreaSquareMetres + " m^2 constant the node "
                  + "defaults quote");
        }

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
        //  3. The water walk sees the same terminals the evaluator does
        // =====================================================================

        /// <summary>
        /// GraphEvaluator seeds its walk on "n is IOutputNode". The water walk used to
        /// seed on the OutputNode base class, which is a strictly narrower set: the
        /// Weather, Lighting and Wind outputs, Shader Output and the Scatter nodes all
        /// implement the interface directly without deriving from that base.
        /// </summary>
        /// <remarks>
        /// The consequence was not a missing feature, it was a demolition. A graph
        /// terminating in one of those nodes evaluated in full - the masks were built,
        /// the ground was carved - and then this walk could not see the terminal, found
        /// no water node reached, and reported that the world asks for no procedural
        /// water. The caller's answer to "no water asked for" is to SWEEP: every river,
        /// lake and ocean object removed, on a graph that had just finished drawing them
        /// a riverbed. Carved channels and no water, which is the reported fault exactly.
        ///
        /// WeatherOutputNode is the witness because it is declared
        /// "class WeatherOutputNode : Generator, IOutputNode" - interface only, no base
        /// class - so if the seed ever narrows back to OutputNode this check fails at
        /// once. The connection is added to graph.connections directly, which bypasses
        /// port-type validation; the walk is about reachability, not about whether a
        /// river mask is a sensible thing to feed a weather output.
        /// </remarks>
        private static void TestWaterWalkSeesInterfaceOnlyOutputs()
        {
            Graph reached = ScriptableObject.CreateInstance<Graph>();
            Graph severed = ScriptableObject.CreateInstance<Graph>();

            try
            {
                RiverNetworkTraceNode trace = new RiverNetworkTraceNode();

                // A fingerprint, not a plausible default. ResolveHydrology copies this
                // across ONLY from a trace the walk actually reached, so finding it on
                // the far side is proof of reachability and not of a shared default.
                // It used to be targetRiverCount that carried that proof, then
                // maxChannels; both are gone from the spawner now - the river count with
                // procedural placement, the channel cap with the spawner's River Mask -
                // so the drainage resolution, which the spawner really does still inherit
                // from a reached trace, carries it instead.
                trace.drainageResolution = 321;
                WeatherOutputNode weather = new WeatherOutputNode();

                reached.nodes.Add(trace);
                reached.nodes.Add(weather);

                Connection link = new Connection();
                link.sourceNodeId = trace.id;
                link.sourcePortIndex = 0;
                link.targetNodeId = weather.id;
                link.targetPortIndex = 0;
                reached.connections.Add(link);

                HydrologyNode native = UCWaterSystemManager.ResolveHydrology(reached, null);

                Check(native != null && native.drainageResolution == 321,
                      "a River Network Trace wired into a Weather Output - which implements "
                      + "IOutputNode without deriving from OutputNode, exactly as the evaluator's "
                      + "own seed allows - resolved to "
                      + (native == null ? "no water at all" : "a spawner that never inherited the trace's settings")
                      + ". The evaluator builds that graph in full, so a walk that cannot see the "
                      + "terminal reports no water on a world that was just carved for it, and "
                      + "the caller sweeps every river, lake and ocean out of the scene");

                // The control: the same two nodes with nothing between them. The walk
                // must report no water here, or the check above proves nothing.
                RiverNetworkTraceNode loose = new RiverNetworkTraceNode();
                WeatherOutputNode idle = new WeatherOutputNode();
                severed.nodes.Add(loose);
                severed.nodes.Add(idle);

                Check(UCWaterSystemManager.ResolveHydrology(severed, null) == null,
                      "a River Network Trace that reaches no Output at all still resolved to "
                      + "procedural water. Its flood is thrown away unread, so the graph has not "
                      + "asked for water and must not be given any");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(reached);
                UnityEngine.Object.DestroyImmediate(severed);
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
            RiverCourseSolver.GroundSampler steep = delegate (float x, float z)
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
            RiverCourseSolver.GroundSampler shallow = delegate (float x, float z)
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

        // =====================================================================
        //  6. Waterfalls form on steep reaches, and only there (J.2)
        // =====================================================================

        /// <summary>
        /// A river that crosses a steep hillside must grow a fall there, and one that
        /// stays on gentle ground must grow none, with no marker node and no authoring.
        /// The detector reads the SOLVED water surface, so this drives it with a surface
        /// built by hand: a flat run, a sustained plunge, and a flat run out. The plunge
        /// is the only thing in the profile steep enough to read as a fall.
        /// </summary>
        /// <remarks>
        /// The three cases are the whole contract. A multi-segment plunge must merge into
        /// ONE reach, not a stack of short ones fighting for the same water. Gentle water
        /// must produce nothing, or every ordinary river would be strung with phantom
        /// foam. And a short steep step under the minimum drop is a riffle, not a fall -
        /// asserting it stays a riffle pins the threshold that keeps a bank edge or a
        /// single steep post from spawning a spray column.
        /// </remarks>
        private static void TestWaterfallsFormOnSteepReaches()
        {
            GameObject go = new GameObject("UC_SelfTest_Waterfall_Probe");
            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();

                // z runs downstream; x is held so the horizontal run of a segment is its
                // z step. A flat head, a three-segment plunge of 4 m over 5 m each
                // (grade 0.8, well past the 0.28 fall threshold), then a flat tail.
                float[] zs   = { 0f,   10f,  20f,  30f,  35f,  40f,  45f,  55f,  65f };
                float[] surf = { 100f, 99.8f,99.6f,99.4f,95.4f,91.4f,87.4f,87.2f,87f };

                List<Vector3> pts = new List<Vector3>();
                List<float> widths = new List<float>();
                for (int i = 0; i < zs.Length; i++)
                {
                    pts.Add(new Vector3(0f, surf[i], zs[i]));
                    widths.Add(6f);
                }

                List<UCWaterBody.WaterfallReach> falls = body.DetectCascadesForTest(pts, surf, widths);

                Check(falls != null && falls.Count == 1,
                      "a course with one sustained 12 m plunge in it produced "
                      + (falls == null ? "a null list" : falls.Count + " fall(s)")
                      + " instead of exactly one. Either the steep reach was missed - a river "
                      + "runs down a cliff with no white water - or the plunge was split into a "
                      + "stack of short falls all emitting foam into the same pool");

                if (falls != null && falls.Count == 1)
                {
                    UCWaterBody.WaterfallReach f = falls[0];

                    Check(Mathf.Abs(f.dropMetres - 12f) < 0.01f,
                          "the merged plunge measured " + f.dropMetres.ToString("0.##")
                          + " m of fall instead of the 12 m the three steep segments add up to. "
                          + "The spray intensity is driven from this drop, so a short reading is a "
                          + "weak fall on a tall cliff");

                    Check(Mathf.Abs(f.crest.z - 30f) < 0.01f && Mathf.Abs(f.foot.z - 45f) < 0.01f,
                          "the fall was placed from z=" + f.crest.z.ToString("0.#") + " to z="
                          + f.foot.z.ToString("0.#") + " instead of z=30 to z=45, the exact span "
                          + "of the steep reach. Foam emitted off the crest or the foot lands in "
                          + "the wrong place");

                    Check(Mathf.Abs(f.widthMetres - 6f) < 0.01f,
                          "the fall took a width of " + f.widthMetres.ToString("0.##")
                          + " m rather than the 6 m channel width at its crest. The foam band is "
                          + "sized from this, so a wrong width is foam narrower or wider than the "
                          + "water it sits on");
                }

                // Gentle everywhere: a 0.02 grade the whole way. Nothing may fire, or
                // every flatland river grows phantom waterfalls.
                float[] gz   = { 0f,   10f,  20f,  30f,  40f };
                float[] gsur = { 100f, 99.8f,99.6f,99.4f,99.2f };
                List<Vector3> gpts = new List<Vector3>();
                List<float> gw = new List<float>();
                for (int i = 0; i < gz.Length; i++) { gpts.Add(new Vector3(0f, gsur[i], gz[i])); gw.Add(4f); }

                List<UCWaterBody.WaterfallReach> none = body.DetectCascadesForTest(gpts, gsur, gw);
                Check(none != null && none.Count == 0,
                      "a course on gentle ground - a 2% grade throughout - grew "
                      + (none == null ? "a null list" : none.Count.ToString()) + " waterfall(s). "
                      + "A fall is an elevation event; on ground this shallow there is none, and "
                      + "spawning one strings every ordinary river with foam it should not have");

                // Steep but tiny: a single 1 m step over a 2 m run. Grade 0.5 clears the
                // angle, but 1 m is under the 1.5 m minimum, so it is a riffle not a fall.
                float[] rz   = { 0f,   2f };
                float[] rsur = { 100f, 99f };
                List<Vector3> rpts = new List<Vector3> { new Vector3(0f, 100f, 0f), new Vector3(0f, 99f, 2f) };
                List<float> rw = new List<float> { 3f, 3f };
                List<UCWaterBody.WaterfallReach> riffle = body.DetectCascadesForTest(rpts, rsur, rw);
                Check(riffle != null && riffle.Count == 0,
                      "a single 1 m step was read as a waterfall. It clears the angle but not the "
                      + "1.5 m minimum drop, and below that a step is fast water, not a fall - "
                      + "without the floor a bank edge or one steep heightmap post spawns spray");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // =====================================================================
        //  6b. Read mode respects a level bank; dig mode still falls (item B)
        // =====================================================================

        /// <summary>
        /// The anti-climb rule must RESPECT a read trench/bank: level where the bank is
        /// level, descending where it descends, never uphill. It must NOT force a river to
        /// fall along a genuinely level reach the way a fresh dig does, or the water is
        /// tilted off the banks it was read to sit inside.
        /// </summary>
        /// <remarks>
        /// One profile, two modes. The input has a LEVEL reach (100 to 100), a DESCENDING
        /// reach (100 to 99), a second LEVEL reach (99 to 99), and finally a RISE (99 to
        /// 101 - the river drawn up over a hump).
        ///
        /// Read Trenches And Banks: the level reaches must survive untouched (no forced
        /// grade), the descent is kept, and only the rise is clamped - down to the upstream
        /// LEVEL, never above it. Dig mode: the same level reach must instead be pulled
        /// below its upstream neighbour by the minimum gradient, because a fresh channel
        /// cut on flat ground has to fall or it stalls into a dam lip. The one profile
        /// proves both halves of the contract and, more importantly, their DIFFERENCE - the
        /// level station stays put in read mode and drops in dig mode. In both modes no
        /// station may end up higher than the one above it: water never runs uphill.
        /// </remarks>
        private static void TestReadModeRespectsBankLevels()
        {
            GameObject go = new GameObject("UC_SelfTest_ReadLevel_Probe");
            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();

                // z runs downstream at a wide 1 km spacing, so the dig-mode minimum grade
                // (0.1 m/km) produces a 0.1 m drop per step - small, but far above the
                // 1e-4 m epsilon these checks use, so "level" and "forced to fall" cannot
                // be confused. x is held, so each run is exactly the z step.
                float[] zs = { 0f, 1000f, 2000f, 3000f, 4000f };
                List<Vector3> pts = new List<Vector3>();
                for (int i = 0; i < zs.Length; i++) pts.Add(new Vector3(0f, 0f, zs[i]));

                const float eps = 1e-4f;

                // ---- Read mode: level survives, descent kept, rise clamped to level ----
                float[] r = { 100f, 100f, 99f, 99f, 101f };
                body.EnforceDownhillForTest(r, pts, true);

                Check(Mathf.Abs(r[1] - 100f) < eps,
                      "read mode forced a LEVEL bank reach downhill: station 1 came out "
                      + r[1].ToString("0.####") + " m instead of staying at 100 m. A river read "
                      + "into its banks must sit level where the bank is level, not be tilted off "
                      + "it by an enforced grade");

                Check(Mathf.Abs(r[2] - 99f) < eps && Mathf.Abs(r[3] - 99f) < eps,
                      "read mode disturbed a reach it should have left alone: the descent to 99 m "
                      + "and the second level reach came out " + r[2].ToString("0.####") + " and "
                      + r[3].ToString("0.####") + " instead of 99 and 99. Read mode only clamps a "
                      + "rise; it may not move a level or descending station");

                Check(Mathf.Abs(r[4] - 99f) < eps,
                      "read mode let the river climb, or over-corrected it: the uphill station "
                      + "(drawn at 101 m over a 99 m neighbour) came out " + r[4].ToString("0.####")
                      + " m instead of being clamped to the upstream LEVEL of 99 m. Uphill is "
                      + "clamped to level - never left climbing, never dragged below the bank");

                for (int i = 1; i < r.Length; i++)
                    Check(r[i] <= r[i - 1] + eps,
                          "read mode left water running uphill at station " + i + ": "
                          + r[i].ToString("0.####") + " m sits above its upstream neighbour "
                          + r[i - 1].ToString("0.####") + " m");

                // ---- Dig mode: the SAME level reach is instead forced to fall ----
                float[] d = { 100f, 100f, 99f, 99f, 101f };
                body.EnforceDownhillForTest(d, pts, false);

                Check(d[1] < 100f - eps,
                      "dig mode did NOT enforce a downhill grade on a level reach: station 1 held "
                      + "at " + d[1].ToString("0.####") + " m instead of being pulled below 100 m. "
                      + "A fresh channel cut on flat ground must fall or it stalls into a still-water "
                      + "dam lip - this is exactly the behaviour read mode must NOT copy");

                for (int i = 1; i < d.Length; i++)
                    Check(d[i] <= d[i - 1] + eps,
                          "dig mode left water running uphill at station " + i + ": "
                          + d[i].ToString("0.####") + " m sits above its upstream neighbour "
                          + d[i - 1].ToString("0.####") + " m");

                // The whole point of item B: same input, same station, different answer.
                Check(r[1] > d[1] + eps,
                      "read mode and dig mode treated a level reach identically (both "
                      + r[1].ToString("0.####") + " m). Read mode must leave it level and dig mode "
                      + "must pull it downhill; if they agree, the read/dig distinction the author "
                      + "asked for is not being made");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // =====================================================================
        //  6c. Manual dig carries per-node depth/width forward (item E)
        // =====================================================================

        /// <summary>
        /// A river dug manually (Read Trenches And Banks off) takes its depth and width
        /// from the node values, and those CARRY FORWARD to every node below until a node
        /// sets a new one. A node left unset (0) is not "zero depth" - it inherits what the
        /// river is already carrying. The run before the first authored node takes the
        /// body-wide default, so a course with nothing typed behaves exactly as before
        /// per-node values existed.
        /// </summary>
        /// <remarks>
        /// Two cases. FIRST, one authored node in the middle proves the carry-forward and
        /// the pre-first-node default cleanly, with no easing to muddy it: default before
        /// it, the authored value at it, and that same value held to the end. SECOND, two
        /// authored nodes with a gap between prove the documented easing - the inherited
        /// stretch transitions from the upstream value to the downstream one rather than
        /// stepping, and stays strictly between the two the whole way.
        /// </remarks>
        private static void TestManualDigCarriesNodeValuesForward()
        {
            GameObject go = new GameObject("UC_SelfTest_NodeCarry_Probe");
            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();

                // Five nodes; positions do not matter to the inherit rule, only the count.
                body.course.points.Clear();
                for (int i = 0; i < 5; i++) body.course.points.Add(new Vector3(i * 10f, 0f, 0f));

                const float eps = 1e-3f;

                // ---- Case 1: one authored node, default before, carried after ----
                // depth authored only at node 2 (= 5 m); body default is 1 m.
                var authored1 = new List<float> { 0f, 0f, 5f, 0f, 0f };
                float[] r1 = body.ResolveNodeSeriesForTest(authored1, 1f);

                Check(r1 != null && r1.Length == 5,
                      "the resolved depth series came back "
                      + (r1 == null ? "null" : r1.Length + " long") + " for a 5-node course");

                Check(Mathf.Abs(r1[0] - 1f) < eps && Mathf.Abs(r1[1] - 1f) < eps,
                      "the run BEFORE the first authored node did not take the body default: "
                      + "nodes 0,1 came out " + r1[0].ToString("0.###") + "," + r1[1].ToString("0.###")
                      + " instead of 1,1. A course with a value set only downstream must still have "
                      + "a sensible depth above it");

                Check(Mathf.Abs(r1[2] - 5f) < eps,
                      "the authored node did not take its own value: node 2 came out "
                      + r1[2].ToString("0.###") + " instead of the authored 5 m");

                Check(Mathf.Abs(r1[3] - 5f) < eps && Mathf.Abs(r1[4] - 5f) < eps,
                      "the authored value did NOT carry forward: nodes 3,4 came out "
                      + r1[3].ToString("0.###") + "," + r1[4].ToString("0.###") + " instead of 5,5. "
                      + "A depth typed at one node must be carried down every node below it until a "
                      + "new one is typed - this is the carry-forward the author asked for");

                // ---- Case 2: two authored nodes, the gap eases between them ----
                var authored2 = new List<float> { 2f, 0f, 0f, 0f, 6f };
                float[] r2 = body.ResolveNodeSeriesForTest(authored2, 10f);

                Check(Mathf.Abs(r2[0] - 2f) < eps && Mathf.Abs(r2[4] - 6f) < eps,
                      "the two authored ends were not held: nodes 0,4 came out "
                      + r2[0].ToString("0.###") + "," + r2[4].ToString("0.###") + " instead of 2,6 "
                      + "(and note the pre-first-node default of 10 must NOT leak into node 0)");

                Check(r2[1] > r2[0] + eps && r2[2] > r2[1] + eps && r2[3] > r2[2] + eps
                      && r2[4] > r2[3] + eps,
                      "the inherited stretch between two authored nodes did not EASE upward "
                      + "monotonically: [" + r2[0].ToString("0.##") + "," + r2[1].ToString("0.##")
                      + "," + r2[2].ToString("0.##") + "," + r2[3].ToString("0.##") + ","
                      + r2[4].ToString("0.##") + "]. A step in the dig shows as a shelf across the "
                      + "channel, so the value must transition, not plateau then jump");

                for (int i = 0; i < 5; i++)
                    Check(r2[i] >= 2f - eps && r2[i] <= 6f + eps,
                          "the eased value at node " + i + " (" + r2[i].ToString("0.###")
                          + ") left the 2..6 m span of the two authored ends it interpolates between");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // =====================================================================
        //  6d. Read mode gap-fills to the next bank, but never gouges open ground (item F)
        // =====================================================================
        // The manual river reads existing trenches, but the procedural pass does not
        // trench every metre. Where the course crosses a stretch the procedural node
        // left flat, and that stretch ENDS at an existing bank (or a joined mouth),
        // the read must DIG a ramp that deepens toward the bank so the two meet
        // continuously - no cliff where the authored trench begins. Where the stretch
        // ends at NEITHER a bank nor a mouth, it must dig nothing and rest on the
        // ground. Both halves are asserted here against the real BuildBankAwareReadChannel.
        private static void TestReadModeGapFillsToBank()
        {
            GameObject go = new GameObject("UC_SelfTest_GapFill_Probe");
            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();
                body.bankFreeboardMetres = 0.10f;   // pin the flat-ground inset the assertions expect

                // Five stations, 10 m apart, so arc length is uniform and the ramp is easy
                // to reason about. widths only feed the mouth case (not exercised here).
                var world  = new List<Vector3>();
                var widths = new List<float>();
                for (int i = 0; i < 5; i++) { world.Add(new Vector3(i * 10f, 0f, 0f)); widths.Add(5f); }

                const float eps = 1e-3f;

                // ---- Case A: a flat gap that ENDS at a downstream bank ----
                // depths[] doubles as the "banked" marker: >5 cm means the ground already
                // holds a trench of that crest. Banks at station 0 and 4 (3 m); 1..3 flat.
                var depthsA = new List<float> { 3f, 0f, 0f, 0f, 3f };
                float[] surfA = new float[5];
                float[] digA  = body.BuildBankAwareReadChannelForTest(world, widths, depthsA, surfA);

                Check(digA != null && digA.Length == 5,
                      "the read-mode dig came back "
                      + (digA == null ? "null" : digA.Length + " long") + " for a 5-station course");

                Check(Mathf.Abs(digA[0]) < eps && Mathf.Abs(digA[4]) < eps,
                      "a banked station was re-dug: stations 0 and 4 (which already hold a "
                      + "trench) came out " + digA[0].ToString("0.###") + "," + digA[4].ToString("0.###")
                      + " instead of 0 - a read bank must be cut-only, never re-carved");

                Check(digA[3] > digA[2] + eps && digA[2] > digA[1] + eps,
                      "the gap dig did NOT ramp UP toward the downstream bank: stations 1..3 "
                      + "came out [" + digA[1].ToString("0.###") + "," + digA[2].ToString("0.###")
                      + "," + digA[3].ToString("0.###") + "]. It must deepen as it approaches the "
                      + "existing trench so the bed meets the bank continuously");

                Check(digA[3] > 0.5f * 3f,
                      "the gap did not deepen ENOUGH to meet the 3 m bank it flows into: the "
                      + "station just above the bank dug only " + digA[3].ToString("0.###")
                      + " m. Where the river reaches an existing trench the dig must approach that "
                      + "trench's depth, or a step is left where they join");

                // ---- Case B: the same flat gap ending at NEITHER a bank nor a mouth ----
                // Only station 0 is banked; 1..4 run to the open end with no join authored.
                var depthsB = new List<float> { 3f, 0f, 0f, 0f, 0f };
                float[] surfB = new float[5];
                float[] digB  = body.BuildBankAwareReadChannelForTest(world, widths, depthsB, surfB);

                Check(Mathf.Abs(digB[1] - digB[2]) < eps && Mathf.Abs(digB[2] - digB[3]) < eps
                      && Mathf.Abs(digB[3] - digB[4]) < eps,
                      "an open run that reaches no bank and no mouth was RAMPED anyway: stations "
                      + "1..4 came out [" + digB[1].ToString("0.###") + "," + digB[2].ToString("0.###")
                      + "," + digB[3].ToString("0.###") + "," + digB[4].ToString("0.###") + "]. With "
                      + "nothing downstream to meet, the read must rest flat on the ground, not dig");

                Check(digB[4] < 1f,
                      "an open run gouged open ground: the far station dug " + digB[4].ToString("0.###")
                      + " m with no bank or mouth ahead of it. Only the freeboard inset (a few cm) is "
                      + "allowed where the river merely rests on flat terrain");

                Check(digA[3] > digB[3] + 1f,
                      "the read did not DISTINGUISH a gap that meets a bank from one that does not: "
                      + "the station above a 3 m bank dug " + digA[3].ToString("0.###")
                      + " m yet the same station over open ground dug " + digB[3].ToString("0.###")
                      + " m. Only the run that reaches a trench should deepen");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // =====================================================================
        //  7. Downstream width grows on its own, with nothing typed (J.4)
        // =====================================================================

        /// <summary>
        /// A procedural river must widen from source to mouth by physics, not because the
        /// user typed a bigger number. When the authored width is flat, the hydraulic-
        /// geometry pass reads arc length as a proxy for the land drained and grows the
        /// width as area^0.4 (Leopold and Maddock), off a narrow-source floor up to the
        /// authored value at the mouth. This drives that pass on a flat-width course and
        /// requires the growth to appear.
        /// </summary>
        /// <remarks>
        /// The confinement trade is turned off here (valleyConfinementStrength = 0) so the
        /// growth law is tested on its own, without reaching into the terrain to sample
        /// valley walls. Depth is required to track width at the base ratio at this stage;
        /// the width-for-depth trade that a gorge or a plain then applies is the separate
        /// terrain-driven pass, exercised end to end by TestSpawnerPlacesRealWaterObjects.
        /// </remarks>
        private static void TestDownstreamWidthGrowsWithoutAuthoring()
        {
            GameObject go = new GameObject("UC_SelfTest_Hydraulic_Growth_Probe");
            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();
                body.autoHydraulicGeometry = true;
                body.valleyConfinementStrength = 0f;   // isolate the growth law from the ground
                body.sourceWidthFraction = 0.15f;

                const int count = 11;
                const float mouthWidth = 10f;
                List<Vector3> pts = new List<Vector3>();
                List<float> widths = new List<float>();
                List<float> depths = new List<float>();
                for (int i = 0; i < count; i++)
                {
                    pts.Add(new Vector3(0f, 0f, i * 100f));   // 100 m spacing, 1 km course
                    widths.Add(mouthWidth);                   // FLAT: nothing authored downstream
                    depths.Add(1f);
                }

                body.ApplyHydraulicGeometryForTest(pts, widths, depths);

                // The source narrows to the fraction; the mouth keeps the authored width.
                Check(Mathf.Abs(widths[0] - mouthWidth * 0.15f) < 0.05f,
                      "with a flat 10 m width authored, the source came out "
                      + widths[0].ToString("0.##") + " m wide instead of 1.5 m (15% of the mouth). "
                      + "A river that is as wide at its spring as at its mouth is the very thing "
                      + "the automatic growth exists to fix");

                Check(Mathf.Abs(widths[count - 1] - mouthWidth) < 0.05f,
                      "the mouth came out " + widths[count - 1].ToString("0.##") + " m wide instead "
                      + "of the authored 10 m. The growth law must reach the authored width at the "
                      + "mouth, not overshoot or fall short of it");

                // Strictly widening the whole way down: monotone, and genuinely bigger by
                // the end. area^0.4 is monotone in arc length, so any dip is a bug.
                bool monotone = true;
                for (int i = 1; i < count; i++)
                    if (widths[i] < widths[i - 1] - 1e-3f) { monotone = false; break; }

                Check(monotone,
                      "the width did not grow monotonically from source to mouth. A river gathers "
                      + "land the whole way down and never narrows for want of it, so a dip in the "
                      + "profile is the growth law misfiring");

                Check(widths[count - 1] > widths[0] * 3f,
                      "the mouth is only " + (widths[count - 1] / Mathf.Max(0.01f, widths[0])).ToString("0.##")
                      + "x the source width. The point of the pass is a river that visibly grows; "
                      + "a nearly flat profile means it did nothing an eye would see");

                // Depth tracks width at the base 18:1 ratio at this stage.
                Check(Mathf.Abs(depths[count - 1] - widths[count - 1] / 18f) < 0.05f
                      && Mathf.Abs(depths[0] - widths[0] / 18f) < 0.05f,
                      "depth did not track width at the 18:1 base ratio after the growth pass. "
                      + "Before the confinement trade reshapes it, a wider reach must carry "
                      + "proportionally more depth, or the channel cross-section is wrong");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // =====================================================================
        //  8. A course that ends inland joins the river it flows into (P4, spec 11.5d)
        // =====================================================================

        /// <summary>
        /// A lake outlet is planned in its own pass and cannot see the trunk rivers'
        /// cells, so one that flows into a river below it used to stop short and read as
        /// two channels climbing over each other. ResolveInlandConfluences fixes that on
        /// the one solved grid: any course still ending inland is walked on down the D8
        /// flow graph, and the first cell owned by ANOTHER course is the confluence -
        /// declared exactly the way a trunk tributary is (mouth=River, joinsPlanIndex=host,
        /// the shared cell appended as the course's end).
        /// </summary>
        /// <remarks>
        /// A real, deterministic grid rather than a solve, so the confluence is a fact of
        /// the flow graph and not of a sampler's noise: a trunk runs south down column
        /// x=2 (cells 2,7,12,17,22) and ends at the world edge; a short outlet sits at
        /// x=0,z=2 (cells 10,11) whose mouth cell flows EAST (D8 dir 4) into the trunk's
        /// cell 12. The outlet must come out joined to the trunk at cell 12; the trunk,
        /// which reaches no other course, must be left exactly as it was - no false join.
        /// A second run with the catchments swapped proves the hierarchy rule: the bigger
        /// water is never made a tributary of the smaller.
        /// </remarks>
        private static void TestInlandCourseJoinsTheRiverItFlowsInto()
        {
            const int res = 5;
            const float cell = 10f;

            RiverNetworkPlanner.DrainageGrid g = new RiverNetworkPlanner.DrainageGrid();
            g.resX = res;
            g.resZ = res;
            g.cellSizeX = cell;
            g.cellSizeZ = cell;
            g.origin = Vector3.zero;
            g.ground = new float[res * res];       // flat; WorldOf's y is irrelevant here
            g.filled = g.ground;
            g.accum = new float[res * res];         // unused by the resolve, sized for safety
            g.dir = new int[res * res];
            for (int i = 0; i < g.dir.Length; i++) g.dir[i] = -1;   // outlet by default

            // The outlet's mouth cell (x=0? no - x=1,z=2 = index 11) drains EAST into the
            // trunk's column at cell 12. Only this one direction matters to the walk.
            g.dir[11] = 4;                          // D8 index 4 = (dx=+1, dz=0), due east

            // Trunk down column x=2. Larger catchment - it is the river, not the branch.
            List<Vector3> trunkCourse = new List<Vector3>
            {
                g.WorldOf(2), g.WorldOf(7), g.WorldOf(12), g.WorldOf(17), g.WorldOf(22)
            };
            RiverNetworkPlanner.PlannedRiver trunk = new RiverNetworkPlanner.PlannedRiver
            {
                course = trunkCourse,
                catchmentSquareMetres = 100000f,
                mouth = RiverNetworkPlanner.MouthKind.Outflow
            };

            // Outlet at x=1,z=2, ending inland one cell short of the trunk.
            List<Vector3> outletCourse = new List<Vector3> { g.WorldOf(10), g.WorldOf(11) };
            RiverNetworkPlanner.PlannedRiver outlet = new RiverNetworkPlanner.PlannedRiver
            {
                course = outletCourse,
                catchmentSquareMetres = 5000f,
                mouth = RiverNetworkPlanner.MouthKind.Outflow
            };

            List<RiverNetworkPlanner.PlannedRiver> plans =
                new List<RiverNetworkPlanner.PlannedRiver> { trunk, outlet };

            RiverNetworkPlanner.ResolveInlandConfluences(g, plans);

            Check(outlet.mouth == RiverNetworkPlanner.MouthKind.River
                  && outlet.joinsPlanIndex == 0,
                  "an outlet whose flow path runs into a trunk river was left ending inland "
                  + "(mouth=" + outlet.mouth + ", joins=" + outlet.joinsPlanIndex + ") instead of "
                  + "joining the trunk at index 0. Unjoined, the outlet and the river cross with no "
                  + "confluence - the 'climb over each other' the author reported");

            Vector3 end = outlet.course[outlet.course.Count - 1];
            Vector3 conf = g.WorldOf(12);
            Check(Mathf.Abs(end.x - conf.x) < 0.01f && Mathf.Abs(end.z - conf.z) < 0.01f,
                  "the outlet joined but did not END on the shared confluence cell: its last point "
                  + "is (" + end.x.ToString("0.#") + ", " + end.z.ToString("0.#") + ") not the trunk "
                  + "cell (" + conf.x.ToString("0.#") + ", " + conf.z.ToString("0.#") + "). A join is a "
                  + "shared point, not two ends that happen to be near each other");

            Check(trunk.mouth == RiverNetworkPlanner.MouthKind.Outflow
                  && trunk.joinsPlanIndex < 0,
                  "the trunk, which reaches no other course, was given a false join (mouth="
                  + trunk.mouth + ", joins=" + trunk.joinsPlanIndex + "). The resolve must only join a "
                  + "course that actually flows into another, or ordinary rivers sprout phantom "
                  + "confluences");

            // Hierarchy rule: swap the catchments so the inland course is now the BIGGER
            // water. It must NOT be made a tributary of the smaller - that is the trunk
            // taking the wrong fork.
            RiverNetworkPlanner.PlannedRiver bigTrunkSmallHost0 = new RiverNetworkPlanner.PlannedRiver
            {
                course = new List<Vector3> { g.WorldOf(2), g.WorldOf(7), g.WorldOf(12), g.WorldOf(17), g.WorldOf(22) },
                catchmentSquareMetres = 3000f,        // now the SMALLER water
                mouth = RiverNetworkPlanner.MouthKind.Outflow
            };
            RiverNetworkPlanner.PlannedRiver bigOutlet = new RiverNetworkPlanner.PlannedRiver
            {
                course = new List<Vector3> { g.WorldOf(10), g.WorldOf(11) },
                catchmentSquareMetres = 90000f,       // now the LARGER water
                mouth = RiverNetworkPlanner.MouthKind.Outflow
            };
            List<RiverNetworkPlanner.PlannedRiver> swapped =
                new List<RiverNetworkPlanner.PlannedRiver> { bigTrunkSmallHost0, bigOutlet };

            RiverNetworkPlanner.ResolveInlandConfluences(g, swapped);

            Check(bigOutlet.joinsPlanIndex < 0
                  && bigOutlet.mouth == RiverNetworkPlanner.MouthKind.Outflow,
                  "a course carrying MORE land than the one it flows into was made a tributary of it "
                  + "(joins=" + bigOutlet.joinsPlanIndex + "). The bigger water is the trunk; joining it "
                  + "to the smaller inverts the hierarchy the whole network is built on");
        }

        // =====================================================================
        //  9. A tributary is bounded to Hack's-law length, not the whole trunk (P5)
        // =====================================================================

        /// <summary>
        /// A tributary that traced nearly the length of the trunk it joins was the
        /// reported "tributary as long as a river" that gouges the terrain. Real
        /// tributaries are shorter than their trunk in proportion to the land they drain
        /// (Hack's law, L proportional to A^h), so TributaryLengthBound caps a branch at
        /// (A_trib / A_trunk)^h of the trunk length, plus a little tolerance, and never
        /// longer than the trunk. This asserts that relation on the pure function.
        /// </summary>
        private static void TestTributaryLengthFollowsHacksLaw()
        {
            const float trunkLen = 4000f;
            const float trunkCatch = 100000f;
            const float h = RiverNetworkPlanner.HackLengthExponent;

            // A branch draining a tenth of the trunk's land: ~0.1^0.57 ~ 0.27 of its
            // length. It must be bounded well below the trunk, and near the Hack estimate
            // (within the tolerance the bound allows), not left trunk-sized.
            float tenth = RiverNetworkPlanner.TributaryLengthBound(trunkLen, trunkCatch, trunkCatch * 0.1f);
            float hackTenth = trunkLen * Mathf.Pow(0.1f, h);

            Check(tenth < trunkLen * 0.5f,
                  "a tributary draining a tenth of its trunk's catchment was bounded to "
                  + tenth.ToString("0") + " m against a " + trunkLen.ToString("0") + " m trunk - more "
                  + "than half its length. Hack's law makes it about a quarter; a branch this long is "
                  + "the reported trunk-sized tributary that gouges the ground");

            Check(tenth >= hackTenth - 1f && tenth <= hackTenth * 1.5f,
                  "the bound (" + tenth.ToString("0") + " m) is not near the Hack estimate ("
                  + hackTenth.ToString("0") + " m) for a one-tenth-catchment branch. The length must "
                  + "track A^h, not be clipped to an arbitrary figure");

            // A branch nearly as large as its trunk stays nearly as long - the law is
            // continuous, so a major tributary is not wrongly cut down.
            float almost = RiverNetworkPlanner.TributaryLengthBound(trunkLen, trunkCatch, trunkCatch * 0.9f);
            Check(almost > tenth && almost <= trunkLen,
                  "a branch draining 90% of the trunk's land was bounded to " + almost.ToString("0")
                  + " m, not between the small branch's " + tenth.ToString("0") + " m and the trunk's "
                  + trunkLen.ToString("0") + " m. The bound must rise with catchment and never exceed "
                  + "the trunk");

            // Never trims a legitimate short branch out of existence: even a vanishing
            // catchment keeps the minimum course length.
            float tiny = RiverNetworkPlanner.TributaryLengthBound(trunkLen, trunkCatch, 1f);
            Check(tiny >= RiverNetworkPlanner.MinCourseMetresForTest - 0.01f,
                  "a tiny-catchment branch was bounded to " + tiny.ToString("0") + " m, under the "
                  + "minimum course length. The bound must never clip a valid branch below the floor a "
                  + "river object needs to exist at all");
        }

        // =====================================================================
        //  10. A connected River Network Trace / River Course Solver actually
        //      carves a trench through River Carve
        // =====================================================================

        private const float TraceCarveWorldMetres = 2000f;

        /// <summary>
        /// Analytic ground for the trace-&gt;carve check: a regional slope with one trunk
        /// valley down the centre, so drainage converges into a channel with a real
        /// catchment and descends monotonically to the sea. Normalised 0..1 height.
        /// </summary>
        private static float TraceCarveHeight(float wx, float wz)
        {
            float metres = 480f - 0.11f * wz;
            float u = (wx - TraceCarveWorldMetres * 0.5f) / 300f;
            metres -= 60f * Mathf.Exp(-u * u);
            return Mathf.Clamp01(metres / 600f);
        }

        /// <summary>Test feed: the analytic converging-valley ground.</summary>
        private class TraceCarveGroundFeed : CreatorNode
        {
            public override string NodeName { get { return "Test Trace-Carve Ground"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            protected override void Create(Matrix m, TileData tile, StopToken stop)
            {
                int sx = m.rect.size.x, sz = m.rect.size.z;
                Vector2 ps = m.PixelSize;
                for (int z = 0; z < sz; z++)
                {
                    float wz = m.worldPos.z + z * ps.y;
                    int row = z * sx;
                    for (int x = 0; x < sx; x++)
                    {
                        float wx = m.worldPos.x + x * ps.x;
                        m.arr[row + x] = TraceCarveHeight(wx, wz);
                    }
                }
            }
        }

        /// <summary>
        /// Builds a single tile that IS the whole world, bound to its own evaluator, so a
        /// world-wide drainage solve and the per-tile stamp both land in the one matrix we read.
        /// </summary>
        private static TileData TraceCarveTile(Graph graph, int res, out GraphEvaluator evaluator)
        {
            float w = TraceCarveWorldMetres;
            TileData tile = new TileData(
                Coord.Zero,
                new CoordRect(new Coord(0, 0), new Coord(res, res)),
                Vector3.zero,
                new Vector3(w, 600f, w),
                600f);
            tile.SetFullWorld(Vector3.zero, new Vector3(w, 600f, w));
            evaluator = new GraphEvaluator(graph);
            tile.Bind(evaluator);
            return tile;
        }

        /// <summary>
        /// Shared checks for a trace-&gt;carve pull: the mask has wet cells, River Carve cut
        /// the ground below where it started at some wet cell, and it left the world corner
        /// (far from any channel) alone.
        /// </summary>
        private static void AssertTraceCarvesTrench(string who, Matrix ground, Matrix mask, Matrix carved)
        {
            Check(ground != null && mask != null && carved != null,
                  who + " -> River Carve returned null somewhere in the chain (ground/mask/carved)");
            if (ground == null || mask == null || carved == null) return;

            int n = mask.arr.Length;
            int wet = 0, lowered = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask.arr[i] <= 0.001f) continue;
                wet++;
                if (carved.arr[i] < ground.arr[i] - 1e-4f) lowered++;
            }

            Check(wet > 0,
                  who + " produced a River Mask with no wet cells, so River Carve had nothing to "
                  + "cut. A connected trace over a draining world must stamp a channel; a blank "
                  + "mask is the trace half of \"no river trenches\"");

            Check(lowered > 0,
                  who + " stamped " + wet + " wet cell(s) but River Carve lowered the ground at "
                  + "none of them. The mask reached the carve and no trench came out - the carve "
                  + "half of \"no river trenches\"");

            int dry = 0;  // world origin corner, far from the centre valley
            Check(mask.arr[dry] <= 0.001f && Mathf.Abs(carved.arr[dry] - ground.arr[dry]) < 1e-4f,
                  who + " -> River Carve changed ground away from any channel, or the mask bled to "
                  + "the world corner - the carve must be confined to the channel it was given");
        }

        /// <summary>
        /// The exact wiring the report is about: River Network Trace (and River Course
        /// Solver) feeding River Carve. Trace's mask -&gt; Carve's River Mask, the ground -&gt;
        /// Carve's Height. If the trace returns a blank mask, or the carve reads it and cuts
        /// nothing, the terrain gets no river trench - which is what "I connected the river
        /// nodes and there are no trenches" is. None of this is arithmetic the compiler
        /// covers: it is whether the graph path end-to-end still puts a channel in the ground.
        /// It is also the ONLY self-test that exercises StampNetwork + BuildValleyRefine (the
        /// WalkDownhill re-route) through the node graph - the spawner path does not touch them.
        /// </summary>
        private static void TestConnectedRiverNodesCarveATrench()
        {
            const int res = 129;

            // ---- River Network Trace -> River Carve ----
            Graph traceGraph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                TraceCarveGroundFeed ground = new TraceCarveGroundFeed();
                RiverNetworkTraceNode trace = new RiverNetworkTraceNode();
                RiverCarveNode carve = new RiverCarveNode();

                traceGraph.nodes.Add(ground);
                traceGraph.nodes.Add(trace);
                traceGraph.nodes.Add(carve);
                traceGraph.connections.Add(new Connection(ground.id, 0, trace.id, 0)); // ground -> Trace.Height
                traceGraph.connections.Add(new Connection(ground.id, 0, carve.id, 0)); // ground -> Carve.Height
                traceGraph.connections.Add(new Connection(trace.id, 0, carve.id, 1));  // River Mask -> Carve.River Mask

                GraphEvaluator eval;
                TileData tile = TraceCarveTile(traceGraph, res, out eval);
                StopToken stop = new StopToken();

                Matrix groundM = eval.PullNode(ground, 0, tile, stop) as Matrix;
                Matrix maskM = eval.PullNode(trace, 0, tile, stop) as Matrix;
                Matrix carvedM = eval.PullNode(carve, 0, tile, stop) as Matrix;

                AssertTraceCarvesTrench("River Network Trace", groundM, maskM, carvedM);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(traceGraph);
            }

            // ---- River Course Solver -> River Carve (one node feeds both carve inputs) ----
            Graph solverGraph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                TraceCarveGroundFeed ground = new TraceCarveGroundFeed();
                RiverCourseSolverNode solver = new RiverCourseSolverNode();
                RiverCarveNode carve = new RiverCarveNode();

                solverGraph.nodes.Add(ground);
                solverGraph.nodes.Add(solver);
                solverGraph.nodes.Add(carve);
                solverGraph.connections.Add(new Connection(ground.id, 0, solver.id, 0)); // ground -> Solver.Height
                solverGraph.connections.Add(new Connection(solver.id, 0, carve.id, 0));  // Height passthrough -> Carve.Height
                solverGraph.connections.Add(new Connection(solver.id, 1, carve.id, 1));  // River Mask -> Carve.River Mask

                GraphEvaluator eval;
                TileData tile = TraceCarveTile(solverGraph, res, out eval);
                StopToken stop = new StopToken();

                Matrix groundM = eval.PullNode(ground, 0, tile, stop) as Matrix;
                Matrix maskM = eval.PullNode(solver, 1, tile, stop) as Matrix;
                Matrix carvedM = eval.PullNode(carve, 0, tile, stop) as Matrix;

                AssertTraceCarvesTrench("River Course Solver", groundM, maskM, carvedM);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(solverGraph);
            }
        }
    }
}
