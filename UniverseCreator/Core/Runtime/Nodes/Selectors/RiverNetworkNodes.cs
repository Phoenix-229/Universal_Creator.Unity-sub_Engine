// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverNetworkNodes.cs
//  Purpose: The GRAPH NODE that places rivers procedurally - the trenches, the
//           network, the confluences where trenches join, the drain into a lake
//           or the sea, and the floodplain.
//
//  WHY THIS NODE EXISTS (author question 2026-10-06: "did you create the node in
//  the node graph or not")
//  ------------------------------------------------------------------------------
//  The answer was no. RiverNetworkExtractor.cs held the whole network solve but
//  had ZERO callers and was a plain static class, so nothing in the graph could
//  reach it and there was nothing to find in the add-node menu. This file is that
//  missing seam: NodeCatalog scans the runtime assembly for Generator subclasses,
//  so declaring this class is what makes "River Network" appear in the menu.
//
//  WHAT IS PROCEDURAL AND WHAT IS MANUAL - both, deliberately
//  ---------------------------------------------------------
//  Procedural (this node): WHERE channels begin, which way they run, where they
//  JOIN, where they end, how wide and deep each station is, and the floodplain.
//  Manual (UCRiverNode + UCWaterBodyInspector): hand-laid or hand-edited courses.
//  They are not alternatives - this node shapes the LAND (it carves the trench
//  network into the heightfield), exactly as Unreal's water system lets a
//  landscape be deformed procedurally while individual splines stay editable.
//
//  HOW IT AVOIDS DUPLICATING WORK ALREADY IN THE PROJECT
//  ----------------------------------------------------
//    - Flow directions / accumulation / depression fill: WorldDrainage.Solve
//      (needFlow: true). Not re-derived.
//    - Channel initiation, confluences, Strahler order, discharge:
//      RiverNetworkExtractor. Not re-derived.
//    - Channel width / depth: RiverCourseSolver.HydraulicGeometry (Leopold +
//      Shields). Not re-derived, so one set of coefficients serves both the
//      manual river and this one.
//    - Ocean classification: SeaLevelFloor.FloodOceanFromEdges - the SAME
//      classification the terrain floor, the coastal shelf and the continental
//      rise use, so "where the sea is" has one definition, not five.
//    - Coarse-to-chunk reading: wd.ReadAtChunk, the established seam-free pattern.
//      A per-chunk network would restart at every border and step at the seams.
//
//  WHY THE BED CANNOT CLIMB (and why no "enforce downhill" pass is needed)
//  ----------------------------------------------------------------------
//  The trench is cut below the DEPRESSION-FILLED surface, and priority-flood
//  guarantees `filled` never decreases against the flow direction. So a bed of
//  filled - depth is monotonically descending BY CONSTRUCTION: an uphill reach is
//  not representable, which is the same guarantee step 4 gives the manual solver.
//
//  Deterministic: a pure function of the height field. No RNG, no clock.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Carves a procedurally-routed river network into the terrain and reports where
    /// the channels and floodplains are. See the file header for the division of
    /// labour with the manual river and for what it reuses rather than re-derives.
    /// </summary>
    /// <remarks>
    /// OUTPUTS
    ///   Height     - the input height with the trench network cut into it.
    ///   River Mask - 1 in the channel, falling to 0 across the floodplain, for
    ///                texture and scatter to read (wet banks, no trees in the bed).
    ///
    /// DEEPENING ONLY. Like Lake Basin and River Carve, the bed is a Min against the
    /// ground: a channel is a cut, and raising ground to meet a shallow bed would
    /// fill the valley the river runs through.
    /// </remarks>
    [System.Serializable]
    public class RiverNetworkNode : Generator, IPreparedGraphInputs, IGenerationSnapshotInputs
    {
        public override string NodeName { get { return "River Network"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }

        public override string Tooltip
        {
            get
            {
                return "Procedurally routes and carves a whole river network: channel " +
                       "heads from the slope-area law, trenches that join at confluences, " +
                       "draining into lakes and the sea, sized by Leopold hydraulic geometry.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        // -- Where the network is solved ------------------------------------------
        /// <summary>
        /// Whole-world solve cells per side. The network is a world-scale question
        /// (a river's catchment is not inside one chunk), so it is solved once at this
        /// resolution and read onto each chunk - which is also what makes it seam-free.
        /// </summary>
        public int solveResolution = 256;

        /// <summary>Normalised sea height, for finding where rivers reach the ocean.</summary>
        public float seaLevel = 0.35f;

        /// <summary>
        /// Pooled depth (normalised) above which a depression counts as a lake, so a
        /// river entering it ends as a mouth. Matches the terrain passes' basinDepth.
        /// </summary>
        public float lakeBasinDepth = 0.01f;

        // UC-T03-07: valid lake spills bypass initiation thresholds, never protected lake cells.
        /// <summary>Generate dry downhill channels from existing open lakes; false retains threshold-only extraction.</summary>
        public bool generateLakeOutlets = true;
        /// <summary>Maximum valid lake-source channels, -1 for all or 0 for none; highest discharge is admitted first.
        /// Only active when generateLakeOutlets is true; closed lakes never count towards this cap.</summary>
        public int maximumLakeOutletSources = -1;

        // -- World scaling (author directive 2026-10-15) ---------------------------
        //
        //  WHY A VIRTUAL WORLD CANNOT USE REAL ABSOLUTE NUMBERS
        //  ---------------------------------------------------
        //  The two numbers below this block - a 1 km2 channel threshold and a runoff
        //  of 1e-8 m/s - are correct for Earth. They are wrong for a game world, and
        //  the author put it exactly right: a real river can run 50 km, but you do
        //  not copy that into a 4 km world, you SCALE IT DOWN so it still reads as a
        //  river. Used literally on a 4 km world they ask for 1 km2 of catchment
        //  before a channel may start (~4,100 solve cells, which a small island never
        //  reaches) and then yield a discharge of ~0.01 m3/s, which Leopold turns into
        //  a channel a few CENTIMETRES deep - computed, written, and invisible.
        //
        //  WHAT IS SCALED, AND WHAT IS DELIBERATELY NOT
        //  --------------------------------------------
        //  Only the SCALE moves. The physical law does not. Leopold's exponents are
        //  what make a hierarchy - a headwater stream small, a trunk river large, and
        //  every tributary in proportion - so they are left exactly alone and a single
        //  runoff coefficient is solved for instead. Streams stay streams, big rivers
        //  stay big, and the whole family shrinks together. Rescaling the exponents
        //  would flatten the hierarchy into one uniform ditch, which is the thing that
        //  stops a river network looking real.
        //
        //  HOW THE COEFFICIENT IS FOUND
        //  ----------------------------
        //  Leopold is w = C * q^E, so it inverts in closed form: the discharge that
        //  produces the target trunk width is q = (w / C)^(1/E), and the runoff that
        //  produces that discharge on THIS world's largest catchment is q / area.
        //  No search, no tuning table, and it re-solves itself when the world resizes.

        [Tooltip("Scale the river network to this world instead of using Earth's absolute "
               + "numbers. ON: the channel threshold is a fraction of world area and the "
               + "runoff is solved so the biggest river reaches the target width below, with "
               + "every tributary in proportion. OFF: the raw Min Drainage Area and Runoff "
               + "fields below are used as authored, which is correct only at real-world scale.")]
        public bool scaleToWorld = true;

        /// <summary>UC-T03-05: optional counts of continuous trunks, side rivers and streams per river.
        /// Disabled preserves the full thresholded network; enabled selects connected courses without
        /// changing authored initiation/runoff values. Results accompany the generated output bag.</summary>
        public RiverHierarchyTargets hierarchyTargets = new RiverHierarchyTargets();

        [Tooltip("Metres. How wide the LARGEST river in this world should be. Everything else "
               + "follows from it by hydraulic geometry, so this is the one dial for river "
               + "scale. Clamped to the channel-width limits the solver supports.")]
        public float trunkChannelWidthMetres = 18f;

        [Tooltip("Fraction of the WORLD's area a catchment needs before a channel starts. "
               + "This is what sets how dense the river network looks, and because it is a "
               + "fraction it looks the same on a 4 km world and a 100 km one. Smaller = more, "
               + "finer streams.")]
        [Range(0.0002f, 0.2f)]
        public float channelInitiationFraction = 0.004f;

        // -- Channel initiation: Montgomery and Dietrich (1992) -------------------
        /// <summary>Minimum catchment before a channel may start, in SQUARE METRES.
        /// Used as authored only when <see cref="scaleToWorld"/> is off.</summary>
        public float minDrainageAreaM2 = 1e6f;

        /// <summary>Slope exponent theta in A * S^theta >= C. 0 = pure support area.</summary>
        public float slopeExponent = 1f;

        /// <summary>
        /// Threshold C in A * S^theta >= C, in SQUARE METRES (an area at unit slope).
        /// Larger = fewer, longer rivers. Used as authored only when
        /// <see cref="scaleToWorld"/> is off; with it on, C is re-derived from the
        /// world's own mean slope so a small world is not held to an Earth-sized
        /// catchment (see RiverNetworkOptions.scaleThresholdToWorld).
        /// </summary>
        [Tooltip("Threshold C of the slope-area law A*S^theta >= C, in square metres. "
               + "Larger = fewer, longer rivers. Only used as authored when Scale To World "
               + "is OFF; with it ON, C is re-derived from this world's mean slope so a "
               + "small world still grows a network.")]
        public float slopeAreaThreshold = 1e5f;

        /// <summary>Reaches shorter than this many solve cells are discarded as noise.</summary>
        public int minReachCells = 3;

        // -- Discharge: the rational method Q = runoff * A -------------------------
        /// <summary>
        /// Runoff depth shed by the catchment, METRES PER SECOND. 1e-8 is about
        /// 315 mm/year of effective runoff - temperate. Arid worlds use less.
        /// </summary>
        public float runoffMetresPerSecond = 1e-8f;

        // -- Shape of the cut ------------------------------------------------------
        /// <summary>Floodplain half-width as a MULTIPLE of bankfull width (Leopold ~10-20x).</summary>
        public float floodplainWidthMultiple = 12f;

        /// <summary>
        /// How deep the floodplain sits relative to the channel, 0..1. 0 leaves the
        /// floodplain at ground level; 1 would drop it to the thalweg. A real
        /// floodplain is a shallow bench well above the bed, so this is small.
        /// </summary>
        public float floodplainDepthFraction = 0.15f;

        /// <summary>Multiplies the computed channel depth. 1 = exactly Leopold + Shields.</summary>
        public float depthScale = 1f;

        /// <summary>Lowest Strahler order that is carved. 2 drops the finest headwaters.</summary>
        public int minOrderToCarve = 1;

        [Tooltip("A catchment this many times the channel-initiation area is a river even on flat " +
                 "ground. Keeps rivers alive across lowland so they reach the lake or sea instead of " +
                 "dying at the foothills. 0 = pure slope-area law.")]
        public float trunkAreaMultiple = 3f;

        [Tooltip("Shallowest trench cut anywhere on the network, in METRES. Leopold depth for a " +
                 "headwater on a big world can be a few centimetres - computed, written, invisible. " +
                 "This floor makes every carved reach readable. 0 = pure hydraulic depth.")]
        public float minTrenchDepthMetres = 1.2f;

        [Tooltip("Size the trunk river from the WORLD: a bigger world gets a wider biggest river, so a " +
                 "small world makes streams and small rivers and a large one grows real big rivers. " +
                 "ON overrides Trunk Channel Width. OFF uses the number you typed.")]
        public bool autoTrunkWidth = true;

        [System.NonSerialized] private System.Collections.Generic.List<HeightOutputNode.LakeFootprint> _lakeFootprints;
        // UC-T03-LAKES / UC-T01-07: full content, not quantized bounds that miss changed shorelines.
        [System.NonSerialized] private string _lakeSignature;
        [System.NonSerialized] private HeightOutputNode.PreparedLakeFootprints preparedLakeState;

        /// <summary>Hands the scene's placed lakes/ponds in, so a trench treats them as WATER: it ends at
        /// a lake's shore and never runs through it.</summary>
        /// <param name="footprints">World-metre rings/bounds; null or empty clears exclusions.</param>
        /// <returns>True when complete footprint content changed, including in-place geometry edits.</returns>
        /// <remarks>Main-thread only, before hashing/dispatch. Keep the supplied data stable during the bake.</remarks>
        public bool SetLakeFootprints(System.Collections.Generic.List<HeightOutputNode.LakeFootprint> footprints)
        {
            _lakeFootprints = footprints;
            object prepared = GetPreparedInputsForHash();
            string sig = prepared != null ? JsonUtility.ToJson(prepared) : null;
            bool changed = !string.Equals(sig, _lakeSignature, System.StringComparison.Ordinal);
            _lakeSignature = sig;
            return changed;
        }

        /// <summary>UC-T01-06: deep-copies pushed lake exclusions into a detached river node.</summary>
        /// <param name="snapshot">Same-type generation node; another type is rejected. Main thread only.</param>
        public void CopyInputsTo(Generator snapshot)
        {
            RiverNetworkNode target = snapshot as RiverNetworkNode;
            if (target == null) throw new System.ArgumentException("Expected a River Network snapshot.", "snapshot");
            HeightOutputNode.PreparedLakeFootprints data = new HeightOutputNode.PreparedLakeFootprints { footprints = _lakeFootprints };
            target.SetLakeFootprints(JsonUtility.FromJson<HeightOutputNode.PreparedLakeFootprints>(JsonUtility.ToJson(data)).footprints);
        }

        /// <summary>UC-T03-LAKES: includes scene lake geometry in retained height and mask dependencies.</summary>
        /// <returns>A reusable serializable wrapper, or null when no lake exclusions apply.</returns>
        /// <remarks>Main-thread only, before worker dispatch; graph assets remain scene-independent.</remarks>
        public object GetPreparedInputsForHash()
        {
            if (_lakeFootprints == null || _lakeFootprints.Count == 0)
            {
                if (preparedLakeState != null) preparedLakeState.footprints = null;
                return null;
            }
            if (preparedLakeState == null) preparedLakeState = new HeightOutputNode.PreparedLakeFootprints();
            preparedLakeState.footprints = _lakeFootprints;
            return preparedLakeState;
        }

        [Range(0f, 2f)]
        [Tooltip("How natural the procedural channel is: pools and riffles in the bed, a wandering bank " +
                 "line and an uneven flood plain. 0 = the clean computed channel, 1 = natural, 2 = rugged. " +
                 "Only ever varies the depth of the cut (never raises ground) and is zero away from the river.")]
        public float naturalRuggedness = 1f;

        [Tooltip("Seed for the natural variation. The same seed always gives the same river bed.")]
        public int naturalSeed = 0;

        [Tooltip("Smallest drop between neighbouring solve cells used when routing across filled " +
                 "flats, normalised height. Must be above zero: with zero a filled lake or flat has " +
                 "no flow direction, every river that reaches one stops dead, and the network breaks.")]
        public float flatRoutingEpsilon = 1e-5f;

        private static bool PointInRing(Vector2[] ring, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                float zi = ring[i].y, zj = ring[j].y;
                if ((zi > z) != (zj > z) &&
                    x < (ring[j].x - ring[i].x) * (z - zi) / (zj - zi + 1e-9f) + ring[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private const int MinLakeAreaCells = 4;

        /// <summary>Connected pools of depression-filled land: depth >= minDepth and at least
        /// MinLakeAreaCells cells. Ocean cells are excluded (they are water already).</summary>
        private static bool[] ClassifyLakes(WorldDrainage wd, bool[] ocean, float minDepth,
                                            int resX, int resZ, int count, StopToken stop)
        {
            bool[] pooled = new bool[count];
            for (int i = 0; i < count; i++)
            {
                if (ocean != null && ocean[i]) continue;
                if (wd.filled[i] - wd.height.arr[i] >= minDepth && minDepth > 0f) pooled[i] = true;
            }

            bool[] lake = new bool[count];
            bool[] seen = new bool[count];
            int[] stack = new int[count];
            int[] body = new int[count];

            for (int start = 0; start < count; start++)
            {
                if (!pooled[start] || seen[start]) continue;
                if (stop != null && (start & 8191) == 0 && stop.StopRequested) return lake;

                int top = 0, size = 0;
                stack[top++] = start; seen[start] = true;
                while (top > 0)
                {
                    int i = stack[--top];
                    body[size++] = i;
                    int x = i % resX, z = i / resX;
                    if (x > 0)        { int n = i - 1;    if (pooled[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (x < resX - 1) { int n = i + 1;    if (pooled[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (z > 0)        { int n = i - resX; if (pooled[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (z < resZ - 1) { int n = i + resX; if (pooled[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                }

                if (size < MinLakeAreaCells) continue;
                for (int k = 0; k < size; k++) lake[body[k]] = true;
            }
            return lake;
        }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            // needFlow: true - this node is the reason the flow pass exists. Without it
            // `dir` and `accum` are null and there is no network to trace.
            WorldDrainage wd = WorldDrainage.Solve(this, 0, tile,
                                                   Mathf.Clamp(solveResolution, 16, 512),
                                                   Mathf.Max(0f, flatRoutingEpsilon), true, stop);
            if (stop != null && stop.StopRequested) return null;

            // No solve (single tile, nothing connected) means no network. Pass the height
            // through untouched rather than inventing rivers from a world we cannot see.
            if (wd == null || wd.dir == null || wd.accum == null)
                return outputIndex == 1 ? src.Blank() : new Matrix(src);

            int resX = wd.resX, resZ = wd.resZ;
            int count = resX * resZ;
            if (count <= 0 || wd.height == null || wd.height.arr == null || wd.filled == null)
                return outputIndex == 1 ? src.Blank() : new Matrix(src);
            if (wd.height.arr.Length < count || wd.filled.Length < count)
                return outputIndex == 1 ? src.Blank() : new Matrix(src);

            using var outletMemory = tile.ReserveProductMemory(4096L + count * 384L); // UC-T03-06: classification/record scratch remains admitted through publication.

            float sea = Mathf.Clamp01(seaLevel);

            // WHERE THE WATER ALREADY IS, so a reach ends at a mouth instead of running
            // on through a lake, and so a lake gets an outflow river below it.
            //
            // The lake test is NOT a bare pooled-depth check. It reuses
            // ContinentalRise.BuildBasinLift's classification, which also requires a basin
            // to span at least ContinentalRise.MinLakeAreaM2 before it counts as a lake -
            // the author's rule "where there is no lake is not a basin". Calling the same
            // routine the terrain pass calls is deliberate: if the river used a looser
            // definition it would treat dry hollows as lakes, end reaches in them, and
            // leave the trench stopping in the middle of dry ground. One definition of
            // "lake", shared, not two that can drift.
            bool[] ocean = SeaLevelFloor.FloodOceanFromEdges(wd.height.arr, resX, resZ, sea);
            if (stop != null && stop.StopRequested) return null;

            // FIX 2026-10-19: the lake test is now LOCAL to this node. It used to call
            // ContinentalRise.BuildBasinLift, a class outside the river system whose definition
            // of "lake" could flood large dry areas as water - and WATER IS NEVER CHANNEL, so every
            // cell it claimed silently lost its river. Here a lake is a connected pool of land cells
            // whose depression-fill depth is at least lakeBasinDepth and whose area is at least
            // MinLakeAreaCells solve cells. Nothing else is water but the sea.
            // UC-T03-07: epsilon is only for routing, never an artificial standing-water depth.
            WorldDrainage flat = WorldDrainage.Solve(this, 0, tile, Mathf.Clamp(solveResolution, 16, 512), 0f, false, stop);
            if (stop != null && stop.StopRequested) return null;
            if (flat == null) return outputIndex == 1 ? src.Blank() : new Matrix(src);
            bool[] isLake = ClassifyLakes(flat, ocean, Mathf.Max(0f, lakeBasinDepth), resX, resZ, count, stop);
            if (stop != null && stop.StopRequested) return null;

            // Lakes the author PLACED in the scene are water too, wherever the terrain says nothing about
            // a basin: rasterise their rings so no trench is cut through them.
            string[] namedLakeIds = new string[count]; // UC-T03-06: carry body identity alongside the existing water exclusion.
            var authoredLakeLevels = new System.Collections.Generic.Dictionary<string, float>(System.StringComparer.Ordinal); // UC-T03-07.
            System.Collections.Generic.List<HeightOutputNode.LakeFootprint> placedLakes = _lakeFootprints;
            if (placedLakes != null && placedLakes.Count > 0)
            {
                Vector2 cp0 = wd.height.PixelSize;
                float ox0 = wd.height.worldPos.x, oz0 = wd.height.worldPos.z;
                for (int f = 0; f < placedLakes.Count; f++)
                {
                    HeightOutputNode.LakeFootprint fp = placedLakes[f];
                    if (fp == null || fp.ringXZ == null || fp.ringXZ.Length < 3) continue;
                    // UC-T03-07: authored surface is captured with the same geometry/identity hash.
                    if (fp.hasSurfaceLevel && !string.IsNullOrEmpty(fp.bodyId))
                    {
                        if (authoredLakeLevels.TryGetValue(fp.bodyId, out float level) && level != fp.surfaceMetres)
                            throw new System.InvalidOperationException("Placed lake identity has inconsistent surface levels.");
                        authoredLakeLevels[fp.bodyId] = fp.surfaceMetres;
                    }
                    Bounds pb = fp.planBounds;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((pb.min.x - ox0) / cp0.x) - 1);
                    int x1 = Mathf.Min(resX - 1, Mathf.CeilToInt((pb.max.x - ox0) / cp0.x) + 1);
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((pb.min.z - oz0) / cp0.y) - 1);
                    int z1 = Mathf.Min(resZ - 1, Mathf.CeilToInt((pb.max.z - oz0) / cp0.y) + 1);
                    for (int zz = z0; zz <= z1; zz++)
                        for (int xx = x0; xx <= x1; xx++)
                            if (PointInRing(fp.ringXZ, ox0 + xx * cp0.x, oz0 + zz * cp0.y))
                            {
                                int cell = zz * resX + xx;
                                isLake[cell] = true;
                                ocean[cell] = false; // UC-T03-07: captured placed water owns its footprint over terrain-derived ocean classification.
                                // UC-T03-06: overlapping named bodies need a resolved owner, not whichever list entry ran last.
                                if (string.IsNullOrEmpty(fp.bodyId)) continue;
                                if (namedLakeIds[cell] != null && namedLakeIds[cell] != fp.bodyId)
                                    throw new System.InvalidOperationException("Placed lake footprints have ambiguous overlapping body identities.");
                                namedLakeIds[cell] = fp.bodyId;
                            }
                }
            }

            bool[] water = new bool[count];
            for (int i = 0; i < count; i++)
                water[i] = ocean[i] || (isLake != null && isLake[i]);

            // -----------------------------------------------------------------
            // WORLD SCALING. Resolve the two absolute numbers into values this world
            // can actually produce a visible, proportioned river network from. See the
            // field block for why only the scale moves and the law does not.
            // -----------------------------------------------------------------
            float cellAreaM2 = wd.cellArea > 1e-6f ? wd.cellArea : 1f;
            float effectiveMinAreaM2 = Mathf.Max(0f, minDrainageAreaM2);
            float effectiveRunoff = Mathf.Max(0f, runoffMetresPerSecond);

            if (scaleToWorld)
            {
                // Channel initiation as a FRACTION of the world, so the network reads at
                // the same density whatever the world measures. Floored at one solve cell:
                // a threshold below the grid cannot be resolved and would claim every cell.
                float worldAreaM2 = Mathf.Max(cellAreaM2, count * cellAreaM2);
                effectiveMinAreaM2 = Mathf.Max(cellAreaM2,
                                               Mathf.Clamp(channelInitiationFraction, 0.0002f, 0.2f)
                                               * worldAreaM2);

                // The largest catchment THIS world actually has. Water cells are skipped:
                // accumulation keeps growing across a lake or the sea, and sizing the trunk
                // from an ocean cell would scale every river off the whole map's runoff.
                float maxAccumCells = 0f;
                for (int i = 0; i < count; i++)
                {
                    if (water[i]) continue;
                    if (wd.accum[i] > maxAccumCells) maxAccumCells = wd.accum[i];
                }

                float maxCatchmentM2 = Mathf.Max(cellAreaM2, maxAccumCells * cellAreaM2);

                // Invert Leopold for the target width: w = C * q^E  =>  q = (w / C)^(1/E).
                // Clamped to the solver's own channel-width limits so the target cannot ask
                // for a river the geometry refuses to produce - which would silently scale
                // every tributary against a width the trunk never reaches.
                float worldSizeM = Mathf.Max(wd.height.PixelSize.x * resX, wd.height.PixelSize.y * resZ);
                float trunkWanted = autoTrunkWidth ? Mathf.Clamp(worldSizeM * 0.015f, 8f, 100f) : trunkChannelWidthMetres;
                float widthTarget = Mathf.Clamp(trunkWanted,
                                                RiverCourseSolver.MinChannelWidthM,
                                                RiverCourseSolver.MaxChannelWidthM);

                float qTrunk = Mathf.Pow(widthTarget / RiverCourseSolver.WidthDischargeCoeff,
                                         1f / RiverCourseSolver.WidthDischargeExponent);

                effectiveRunoff = qTrunk / maxCatchmentM2;
            }

            RiverNetworkOptions opt = new RiverNetworkOptions();
            opt.minDrainageAreaM2       = effectiveMinAreaM2;
            opt.slopeExponent           = Mathf.Max(0f, slopeExponent);
            opt.slopeAreaThreshold      = Mathf.Max(0f, slopeAreaThreshold);
            opt.runoffMetresPerSecond   = effectiveRunoff;
            opt.floodplainWidthMultiple = Mathf.Max(1f, floodplainWidthMultiple);
            opt.minReachCells           = Mathf.Max(1, minReachCells);
            // Third absolute number to move with the scale: C lives in square metres, so
            // the same Earth figure applied to a 4 km world demands an eighth of the map
            // for one channel head and grows nothing. The extractor re-derives it from
            // the world's own mean slope in this mode.
            opt.scaleThresholdToWorld   = scaleToWorld;
            opt.trunkAreaMultiple       = Mathf.Max(0f, trunkAreaMultiple);

            // UC-T03-07: classification, flat fill and routing share the existing world solve/cache owners.
            RiverOutletWater outletWater = RiverOutletWater.Capture(tile.RunId.WorldId, id, resX, resZ, ocean, isLake, namedLakeIds, stop);
            if (stop != null && stop.StopRequested) return null;
            var lakeSources = RiverNetworkExtractor.ResolveLakeOutlets(wd, flat, outletWater, authoredLakeLevels,
                sea, effectiveRunoff, generateLakeOutlets ? Mathf.Max(-1, maximumLakeOutletSources) : 0, stop);
            if (stop != null && stop.StopRequested) return null;
            RiverNetwork net = RiverNetworkExtractor.Extract(wd, opt, water, lakeSources, stop);
            if (stop != null && stop.StopRequested) return null;
            // UC-T03-05: select once from the solved tree; a cached product replays the same immutable report.
            if (net != null && hierarchyTargets != null && hierarchyTargets.enabled)
            {
                // UC climate/memory repair: selection allocates by actual reach count, not every solve pixel.
                // Copied reaches borrow their cell lists; this scratch retires before raster publication.
                using var hierarchyMemory = tile.ReserveProductMemory(4096L + net.reaches.Count * 512L);
                net = RiverNetworkExtractor.SelectHierarchy(wd, net, hierarchyTargets, scaleToWorld, minOrderToCarve, stop);
                if (stop != null && stop.StopRequested) return null;
                if (net != null) tile.AddPublication(new RiverHierarchyGraphPublication(id + ":hierarchy", net.hierarchy));
            }
            LakeOutletSnapshot lakeOutlets = RiverNetworkExtractor.BindLakeOutlets(wd, net, lakeSources,
                tile.RunId.WorldId, id, Mathf.Max(1, minOrderToCarve), stop);
            if (stop != null && stop.StopRequested) return null;
            if (net == null || net.reaches.Count == 0)
            {
                // UC-T03-07: closed/disabled lakes retain diagnostics even when no channel exists.
                if (lakeOutlets.Lakes.Count > 0)
                    tile.AddPublication(new ChannelGraphPublication(id + ":channels", new float[count], new float[count], resX, resZ,
                        wd.height.worldPos.x, wd.height.worldPos.z, wd.height.PixelSize.x, wd.height.PixelSize.y, null, lakeOutlets));
                return outputIndex == 1 ? src.Blank() : new Matrix(src);
            }

            // -----------------------------------------------------------------
            // Rasterise the network onto the COARSE grid: how deep to cut, and a
            // channel strength. Reading a coarse field onto the chunk (below) is the
            // established seam-free pattern; a per-chunk trace would restart at
            // every border.
            //
            // Depth is computed PER CELL from that cell's own accumulated area, not
            // from the reach outlet, because downstream hydraulic geometry is the
            // whole point: a channel grows as its catchment grows.
            // -----------------------------------------------------------------
            float worldHeightM = wd.height.worldHeight > 1e-3f ? wd.height.worldHeight : 600f;
            float cellArea = cellAreaM2;
            // The SAME coefficient the extractor was given. Reading the raw field here
            // instead would size the trench from Earth's runoff while the network was
            // traced from the world-scaled one - the mask and the cut would disagree.
            float runoff = effectiveRunoff;
            float dScale = Mathf.Max(0f, depthScale);
            int minOrder = Mathf.Max(1, minOrderToCarve);

            Vector2 cpix = wd.height.PixelSize;
            float mppX = Mathf.Max(1e-3f, cpix.x);
            float mppZ = Mathf.Max(1e-3f, cpix.y);
            float mppAvg = (mppX + mppZ) * 0.5f;

            // Normalised depth to cut at each coarse cell, the shallower floodplain
            // bench, and the channel strength that becomes the River Mask. All zero
            // away from the network.
            float[] cutNorm = new float[count];
            float[] plainNorm = new float[count];
            float[] strength = new float[count];
            float[] pubHalfW = new float[count];   // published to RiverChannelRegistry (metres)
            float[] pubDepth = new float[count];
            int[] pubDownstream = new int[count]; // UC-T03-09: final profiles use the same directed stations as the carve.
            string[] pubReachIds = new string[count];
            float[] pubSurface = new float[count]; // UC-T03-09: source-lake levels are captured, never reconstructed from routing epsilon.
            for (int c = 0; c < count; c++) pubDownstream[c] = -1;
            for (int c = 0; c < count; c++) pubSurface[c] = float.NaN;
            foreach (var lake in lakeOutlets.Lakes)
                if (lake.Selected && lake.SourceCell >= 0) pubSurface[lake.SourceCell] = lake.SurfaceMetres;

            // UC-T04-06: only true headwaters taper; confluence/lake-fed sections retain clearance.
            var hasIncoming = new bool[net.reaches.Count];
            foreach (var link in net.reaches)
                if (link.downstream >= 0 && link.downstream < hasIncoming.Length) hasIncoming[link.downstream] = true;
            for (int r = 0; r < net.reaches.Count; r++)
            {
                if (stop != null && stop.StopRequested) return null;

                RiverReach reach = net.reaches[r];
                if (reach.order < minOrder) continue;

                float sourceArcM = 0;
                bool spring = !hasIncoming[r] && reach.cells.Count > 0 &&
                    reach.cells[0] >= 0 && reach.cells[0] < count && float.IsNaN(pubSurface[reach.cells[0]]);
                string reachId = reach.cells.Count > 0 ? id + ":reach:" + reach.cells[0] : null; // UC memory repair: one shared immutable identity per reach.

                for (int k = 0; k < reach.cells.Count; k++)
                {
                    int c = reach.cells[k];
                    if (c < 0 || c >= count) continue;
                    if (water[c]) continue;        // inside a lake/sea: its own bed owns it

                    // Area -> discharge -> Leopold width/depth with the Shields floor.
                    float areaM2 = wd.accum[c] * cellArea;
                    float q = runoff * areaM2;

                    // Along-flow slope from the filled surface, in metres per metre: the
                    // slope the hydraulic geometry is written against.
                    float s = 0f;
                    // `wd.dir` is a D8 direction CODE 0..7, not a cell index - resolve it
                    // through Hydrology, never by indexing `filled` with it directly.
                    int d = Hydrology.DownstreamCell(wd.dir, c, resX, resZ);
                    pubDownstream[c] = d; // UC-T03-09: shared junction identity follows its downstream reach.
                    pubReachIds[c] = reachId;
                    if (d >= 0)
                    {
                        float dz = wd.filled[c] - wd.filled[d];
                        if (dz < 0f) dz = 0f;
                        float rx = ((d % resX) - (c % resX)) * mppX;
                        float rz = ((d / resX) - (c / resX)) * mppZ;
                        float run = Mathf.Sqrt(rx * rx + rz * rz);
                        if (run > 1e-4f) s = (dz * worldHeightM) / run;
                    }

                    float halfW, depthM;
                    RiverCourseSolver.HydraulicGeometry(q, s, out halfW, out depthM);

                    // Depth floor: a reach must be deep enough to SEE. Scaled with the width so a
                    // trunk is still deeper than a brook, but never below the floor.
                    // Floor proportional to width: a 1.5 m stream gets ~0.6 m, never a 1.2 m slot; only a real river reaches the full floor.
                    float depthWithFloor = Mathf.Max(depthM * dScale, Mathf.Min(Mathf.Max(0f, minTrenchDepthMetres), 0.4f * halfW * 2f));
                    if (spring)
                    {
                        if (k > 0)
                        {
                            int previous = reach.cells[k - 1];
                            float dx = (c % resX - previous % resX) * mppX, dz = (c / resX - previous / resX) * mppZ;
                            sourceArcM += Mathf.Sqrt(dx * dx + dz * dz);
                        }
                        float onset = Mathf.SmoothStep(0, 1, sourceArcM / Mathf.Max(2 * mppAvg, halfW * 4));
                        depthWithFloor = Mathf.Lerp(Mathf.Min(0.02f, depthWithFloor), depthWithFloor, onset);
                        halfW = Mathf.Lerp(Mathf.Min(halfW, mppAvg * 0.5f), halfW, onset);
                    }
                    float dNorm = depthWithFloor / worldHeightM;
                    if (dNorm > cutNorm[c]) cutNorm[c] = dNorm;
                    if (halfW > pubHalfW[c]) pubHalfW[c] = halfW;
                    if (depthWithFloor > pubDepth[c]) pubDepth[c] = depthWithFloor;

                    // Channel strength: 1 where the channel is at least a cell wide, and
                    // proportional below that, so a stream narrower than the solve grid
                    // reads as a partial channel instead of vanishing or over-claiming.
                    float occupancy = Mathf.Clamp01((halfW * 2f) / mppAvg);
                    if (occupancy > strength[c]) strength[c] = occupancy;

                    // Floodplain: a shallow bench beside the channel. Recorded as a much
                    // smaller cut so it eases the valley rather than trenching it.
                    float plain = dNorm * Mathf.Clamp01(floodplainDepthFraction);
                    if (plain > plainNorm[c]) plainNorm[c] = plain;
                }
            }
            if (stop != null && stop.StopRequested) return null;

            // UC-T02-07: workers return channel metadata with their field; only the accepted terrain commit publishes it.
            // UC-T03-06: lake/sea identity and directed junctions use that same captured product and commit owner.
            RiverOutletTopology outlets = RiverNetworkExtractor.BuildOutlets(wd, net, outletWater, tile.RunId.WorldId, id,
                pubHalfW, pubDepth, minOrder, stop);
            if (stop != null && stop.StopRequested) return null;
            tile.AddPublication(new ChannelGraphPublication(id + ":channels", pubHalfW, pubDepth, resX, resZ,
                wd.height.worldPos.x, wd.height.worldPos.z, mppX, mppZ, outlets, lakeOutlets,
                pubDownstream, pubReachIds, wd.height.arr, wd.height.worldPos.y, wd.height.worldHeight, pubSurface)); // UC-T03-09.

            // -----------------------------------------------------------------
            // Read the coarse fields onto this chunk and cut. Bilinear so the trench
            // walls are smooth rather than stepping cell to cell - the facet source
            // called out in the water-smoothness report.
            // -----------------------------------------------------------------
            if (outputIndex == 1)
            {
                float[] chunkMask = wd.ReadAtChunk(strength, src, WorldRead.Bilinear, stop); // UC-T03-04: cancel during chunk reads.
                if (stop != null && stop.StopRequested) return null;

                Matrix mask = src.Blank();
                int mn = mask.arr.Length;
                for (int i = 0; i < mn && i < chunkMask.Length; i++)
                    mask.arr[i] = Mathf.Clamp01(chunkMask[i]);
                return mask;
            }

            float[] chunkCut   = wd.ReadAtChunk(cutNorm,   src, WorldRead.Bilinear, stop);
            float[] chunkPlain = wd.ReadAtChunk(plainNorm, src, WorldRead.Bilinear, stop);
            if (stop != null && stop.StopRequested) return null;

            // Where the coarse grid says WATER (sea, a basin lake, a placed lake), read it onto the chunk and
            // switch the cut off there: a trench runs DOWN TO a shore and stops, never through the water. The
            // bilinear read is what makes the stop line smooth rather than cell-stepped.
            float[] waterCoarse = new float[count];
            for (int i = 0; i < count; i++) waterCoarse[i] = water[i] ? 1f : 0f;
            float[] chunkWater = wd.ReadAtChunk(waterCoarse, src, WorldRead.Bilinear, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = new Matrix(src);
            // UC-T03-02: retain pre-trench ground for terminal guards; published inputs stay immutable.
            dst.trenchReference = src.trenchReference ?? src;
            int n = dst.arr.Length;

            // Natural variation is evaluated at CHUNK resolution from WORLD coordinates, so it is
            // identical on both sides of a chunk border (no seams) and far finer than the coarse grid
            // the network is solved on. It scales the cut (never adds ground) and is zero where
            // there is no river, so nothing appears away from the channel.
            Vector2 px = dst.PixelSize;
            float ox = dst.worldPos.x, oz = dst.worldPos.z;
            int sx = Mathf.Max(1, dst.rect.size.x);
            float rug = Mathf.Max(0f, naturalRuggedness);
            float sd = naturalSeed * 0.713f;

            for (int i = 0; i < n && i < chunkCut.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float chan  = chunkCut[i];
                float plain = i < chunkPlain.Length ? chunkPlain[i] : 0f;
                if (chan <= 0f && plain <= 0f) continue;

                if (chunkWater != null && i < chunkWater.Length && chunkWater[i] > 0.02f)
                {
                    float keep = 1f - Mathf.SmoothStep(0.02f, 0.45f, chunkWater[i]);
                    chan *= keep; plain *= keep;
                    if (chan <= 0f && plain <= 0f) continue;
                }

                if (rug > 0f)
                {
                    float wx = ox + (i % sx) * px.x;
                    float wz = oz + (i / sx) * px.y;

                    // Pools and riffles: long-wavelength deepening/shallowing along the channel.
                    float pool   = Mathf.PerlinNoise(wx * 0.020f + sd, wz * 0.020f - sd) - 0.5f;
                    // Bank wander: medium scale, moves where the trench wall falls off.
                    float wander = Mathf.PerlinNoise(wx * 0.060f - sd, wz * 0.060f + sd + 17f) - 0.5f;
                    // Gravel: fine-scale roughness of the bed.
                    float gravel = Mathf.PerlinNoise(wx * 0.350f + sd, wz * 0.350f + 31f) - 0.5f;

                    float chanScale  = 1f + rug * (pool * 0.55f + wander * 0.40f + gravel * 0.18f);
                    // The flood plain is looser: benches and low spots at a different scale.
                    float plainScale = 1f + rug * (wander * 0.90f + pool * 0.50f);

                    chan  *= Mathf.Max(0.35f, chanScale);
                    plain *= Mathf.Max(0.20f, plainScale);
                }

                // DEEPENING ONLY: the bed is a Min against the ground. Cutting below the
                // existing surface can only remove material, so the trench cannot build a
                // levee wall and cannot raise a valley floor.
                dst.arr[i] = dst.arr[i] - (chan + plain);
            }

            return dst;
        }
    }

    /// <summary>
    /// Shapes the BANKS of a river and reports the riparian bands beside it. Second half
    /// of the procedural river pair: River Network routes and trenches, this dresses the
    /// edges so a trench reads as a river valley instead of a slot cut in the ground.
    /// </summary>
    /// <remarks>
    /// WHY A SEPARATE NODE, AND WHY IT TAKES THE MASK RATHER THAN RE-SOLVING
    /// The bank bands are tens of metres wide. The network is solved on a whole-world
    /// coarse grid (one cell can be 100 m+ on a large world), which physically cannot
    /// resolve a 20 m bank - so banks must be computed at CHUNK resolution. Feeding this
    /// node the network's River Mask output does exactly that: the mask arrives already
    /// sampled onto the chunk lattice, and the lateral distance is then solved here at
    /// full fidelity. It also means the author can see and re-use the mask.
    ///
    /// The lateral distance comes from MatrixOperations.SpreadLinear - the SAME routine
    /// LakeBasinNode uses for its shore-to-deep field - rather than a new distance
    /// transform. Band classification is RiverBankZones.Classify, already the single
    /// definition of Channel / Waterline / Bank / Floodplain / Dry.
    ///
    /// LOWER-ONLY on the height output, like every other water carve in the project: a
    /// bank is eased by removing material, never by raising ground into a levee.
    /// </remarks>
    [System.Serializable]
    public class RiverBanksNode : Generator
    {
        public override string NodeName { get { return "River Banks"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }

        public override string Tooltip
        {
            get
            {
                return "Eases the banks beside a river trench and outputs the riparian " +
                       "bands (waterline, bank, floodplain) for texture and scatter.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("River Mask", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("Bank Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Metres of BANK beyond the water's edge - rock, grass, bank cover.</summary>
        public float bankBandMetres = 12f;

        /// <summary>Metres of FLOODPLAIN beyond the bank - larger riparian plants.</summary>
        public float floodplainMetres = 60f;

        /// <summary>
        /// Representative channel half-width in metres, for band classification. The
        /// network sizes the channel itself; this only tells the band maths where the
        /// water's edge sits relative to the mask it was handed.
        /// </summary>
        public float channelHalfWidthMetres = 8f;

        /// <summary>
        /// How much the bank band is eased DOWN toward the channel, as a fraction of
        /// world height. Small on purpose: a bank is a slope, not a second trench. 0
        /// leaves the terrain alone and outputs only the mask.
        /// </summary>
        public float bankEase = 0.004f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix river = GetInputMatrix(tile, 1, stop);
            if (river == null)
                return outputIndex == 1 ? src.Blank() : new Matrix(src);
            if (stop != null && stop.StopRequested) return null;

            Vector2 ps = src.PixelSize;
            float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);

            float half  = Mathf.Max(0f, channelHalfWidthMetres);
            float bankM = Mathf.Max(0f, bankBandMetres);
            float plainM = Mathf.Max(0f, floodplainMetres);

            // Total metres the river's influence reaches beyond its centreline, converted
            // to the pixel run SpreadLinear works in.
            float reachM = half + bankM + plainM;
            float reachPixels = Mathf.Max(1f, reachM / mpp);

            // Distance outward from the channel: 1 inside the river, falling to 0 at the
            // edge of the river's influence. Same routine, same convention, as the lake
            // shore field in LakeBasinNode.
            Matrix spread = MatrixOperations.SpreadLinear(river, 0.5f, reachPixels);
            if (stop != null && stop.StopRequested) return null;

            Matrix outM = outputIndex == 1 ? src.Blank() : new Matrix(src);
            // UC-T03-02: the bank cut belongs to river relief, including when driven by an authored mask.
            if (outputIndex != 1) outM.trenchReference = src.trenchReference ?? src;
            int n = outM.arr.Length;
            float ease = Mathf.Max(0f, bankEase);

            for (int i = 0; i < n && i < spread.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                // Spread 1 -> at the channel, 0 -> at the far edge of the influence, so
                // lateral metres from the centreline is the complement scaled by reach.
                float s = Mathf.Clamp01(spread.arr[i]);
                if (s <= 0f) continue;                        // beyond the river entirely

                float lateralM = (1f - s) * reachM;

                RiverBankZone zone = RiverBankZones.Classify(lateralM, half, bankM, plainM);

                if (outputIndex == 1)
                {
                    // Riparian influence for texture and scatter. Graded rather than a
                    // hard band id so a shader or a density curve can read it directly:
                    // strongest at the water's edge, fading out across the floodplain.
                    float v;
                    switch (zone)
                    {
                        case RiverBankZone.Channel:    v = 0f;   break;   // open water, not bank
                        case RiverBankZone.Waterline:  v = 1f;   break;
                        case RiverBankZone.Bank:       v = 0.75f; break;
                        case RiverBankZone.Floodplain: v = 0.35f; break;
                        default:                       v = 0f;   break;
                    }

                    // Feather within the band by the same lateral fraction, so the mask has
                    // no stair step where two bands meet.
                    if (v > 0f && reachM > 1e-3f)
                        v *= Mathf.Clamp01(1f - (lateralM - half) / Mathf.Max(1e-3f, bankM + plainM));

                    outM.arr[i] = Mathf.Clamp01(v);
                    continue;
                }

                if (ease <= 0f) continue;

                // Terrain: ease the BANK band down toward the channel so the trench lip is
                // a slope instead of a wall. Strongest just outside the water, releasing
                // across the floodplain. Lower-only.
                if (zone == RiverBankZone.Bank || zone == RiverBankZone.Waterline ||
                    zone == RiverBankZone.Floodplain)
                {
                    float outside = Mathf.Max(0f, lateralM - half);
                    float span = Mathf.Max(1e-3f, bankM + plainM);
                    float t = Mathf.Clamp01(1f - outside / span);
                    float w = t * t * (3f - 2f * t);            // smoothstep, no crease
                    outM.arr[i] = outM.arr[i] - ease * w;
                }
            }

            return outM;
        }
    }
}
