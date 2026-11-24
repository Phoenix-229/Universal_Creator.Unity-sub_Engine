// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    HydrologyNodes.cs
//  Purpose: River tracing and carving, lakes, ocean level, bank seating, watershed
//           basins, wetlands, and submarine terrain.
//
//  They all sit on the shared Hydrology solver, so they agree with each other:
//  the lake surface a river drains into is the same spill elevation Lake Fill
//  floods to, and the basin a wetland sits in is the one Watershed segments.
//  Deriving each independently is how you get rivers that miss their lakes.
//
//  ONE NAME PER SIGNAL
//  -------------------
//  A port pair that does not read as one connection is a graph nobody can wire.
//  So the traced course is called "River Mask" from the node that emits it all the
//  way to the nodes that consume it, and a flooded footprint - which is a different
//  thing, the water's own outline rather than the channel's - is called "Water Mask"
//  by every node that emits one, whether the water is a river, a lake or the sea.
//  Water Bank Seat therefore takes a "Water Mask" from any of them without the user
//  having to work out which name means which.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.TerrainSystem; // UC-T03-08: lake criteria and reports share the captured world owner.

namespace UniverseCreator.Nodes.Selectors
{

    /// <summary>
    /// Cuts a lake BASIN into the heightfield: a rugged, non-constant-depth bed with a
    /// wave-cut shelf near the shore, a dish falling to the deep, and low-amplitude bed
    /// ruggedness. The lake counterpart of River Carve - it authors the terrain the
    /// water sits in, so a lake placed on this bed is flush to its shore and never
    /// floats. Reads the Lake Mask (where water stands) and the Water Level (the flat
    /// surface it fills to); the depth profile runs from 0 at the waterline to full in
    /// the deep, shaped by the shared WaterBedMath.LakeBasin profile.
    /// </summary>
    /// <remarks>
    /// WHY THE PROFILE IS A DISTANCE FIELD, NOT THE MASK
    /// The Lake Mask is 1 across the whole interior and only softens at the very edge,
    /// so it cannot say how far from shore a cell is. A shore-to-deep fraction has to
    /// come from an actual distance to the waterline. SpreadLinear on the INVERTED mask
    /// gives distance inward from the dry bank, from which t (0 at the shore, 1 in the
    /// deep) falls straight out - the same t the runtime WaterCarveLayer feeds
    /// WaterBedMath.DepthFraction, so the graph guide and the placed lake read the same
    /// bed shape.
    ///
    /// DEEPENING ONLY. Like River Carve, the bed is a Min against the ground: a basin
    /// is a hole, and lifting ground to meet a shallow bed would fill a valley the lake
    /// happens to sit in.
    /// </remarks>
    [System.Serializable]
    public class LakeBasinNode : Generator
    {
        public override string NodeName { get { return "Lake Basin"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts a rugged, shelved lake basin into the terrain under a lake mask."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Lake Mask", PortType.Matrix, PortDirection.Input),
            new Port("Water Level", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>Deepest the basin digs below its surface, as a fraction of world height.</summary>
        public float maxDepth = 0.03f;

        /// <summary>How far from shore the dish reaches full depth, in metres.</summary>
        public float shoreRunMetres = 120f;

        /// <summary>
        /// How much of the shore-to-deep run is the wave-cut shelf (shallow bench).
        /// Read by the LakeBasin profile - see WaterBedMath.DepthFraction.
        /// </summary>
        public float shelfFraction = 0.25f;

        /// <summary>Bed roughness amplitude, as a fraction of world height. 0 is a smooth dish.</summary>
        public float ruggedness = 0.004f;

        /// <summary>Feature size of the bed roughness, in metres.</summary>
        public float ruggednessFeatureMetres = 40f;

        /// <summary>Octaves of the bed roughness.</summary>
        public int ruggednessOctaves = 3;

        /// <summary>Roughness persistence, 0..1.</summary>
        public float ruggednessPersistence = 0.5f;

        /// <summary>Seed for the bed roughness, so a basin is identical on every rebuild.</summary>
        public int seed = 12345;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix lake = GetInputMatrix(tile, 1, stop);
            if (lake == null) return new Matrix(src);
            if (stop != null && stop.StopRequested) return null;

            Matrix waterLevel = GetInputMatrix(tile, 2, stop);

            Matrix dst = new Matrix(src);
            // UC-T03-03: lake excavation belongs in both ground versions; it must not
            // become a river exemption merely because this branch already carries a river.
            if (src.trenchReference != null) dst.trenchReference = new Matrix(src.trenchReference);

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            Vector2 ps = src.PixelSize;
            float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);

            // Distance INWARD from the shore, in pixels, capped at the shore run. On the
            // inverted mask the dry bank is the seed, so the field is 1 at the waterline
            // falling to 0 in the deep; t is its complement, matching WaterBedMath.
            float runPixels = Mathf.Max(1f, shoreRunMetres / mpp);
            Matrix inv = src.Blank();
            for (int i = 0; i < inv.arr.Length; i++)
                inv.arr[i] = 1f - Mathf.Clamp01(lake.arr[i]);

            Matrix shoreField = MatrixOperations.SpreadLinear(inv, 0.5f, runPixels);
            if (stop != null && stop.StopRequested) return null;

            float depth = Mathf.Max(0f, maxDepth);
            float shelf = Mathf.Clamp(shelfFraction, 0.02f, 0.9f);
            float rug = Mathf.Max(0f, ruggedness);
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float wet = Mathf.Clamp01(lake.arr[i]);
                    if (wet <= 0.001f) continue;

                    // t = 0 at the waterline, 1 in the deep. shoreField is 1 at the bank.
                    float t = Mathf.Clamp01(1f - shoreField.arr[i]);

                    // Shared lake profile: shallow shelf, then a dish. bankExponent is
                    // unused by the LakeBasin case, so any value passes. Fully qualified:
                    // WaterBedMath lives in the terrain layer, reached by its namespace.
                    float frac = UniverseCreator.TerrainSystem.WaterBedMath.DepthFraction(
                        UniverseCreator.TerrainSystem.BedProfile.LakeBasin, t, shelf, 2f);

                    // Surface the basin is measured DOWN from: the flat fill level where
                    // wired, else the local ground (a self-consistent hollow).
                    float surface = waterLevel != null
                        ? Mathf.Clamp01(waterLevel.arr[i])
                        : src.arr[i];

                    float bed = surface - depth * frac;

                    // Bed ruggedness, faded out toward the shore so it never breaks the
                    // waterline into a dashed line of islands (t^2 fade, as the runtime
                    // carve does).
                    if (rug > 1e-6f)
                    {
                        float fade = t * t;
                        float worldX = originX + x * ps.x;
                        float worldZ = originZ + z * ps.y;
                        float n = UniverseCreator.TerrainSystem.WaterBedMath.Ruggedness(
                            worldX, worldZ, ruggednessFeatureMetres,
                            ruggednessOctaves, ruggednessPersistence, seed);
                        bed += n * rug * fade;
                    }

                    // Never above its own surface, and deepening only.
                    if (bed > surface) bed = surface;
                    dst.arr[i] = Mathf.Clamp01(Mathf.Min(dst.arr[i], bed));
                    if (dst.trenchReference != null)
                    {
                        float referenceBed = bed + (waterLevel == null ? src.trenchReference.arr[i] - src.arr[i] : 0f);
                        dst.trenchReference.arr[i] = Mathf.Clamp01(Mathf.Min(dst.trenchReference.arr[i], referenceBed));
                    }
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Floods every closed basin to its spill elevation. Port 0 is the water mask,
    /// port 1 the depth, port 2 the surface elevation, so the texture graph can fade
    /// a lake from shallow shore to deep centre without a second solve.
    /// </summary>
    /// <remarks>
    /// SOLVED FOR THE WORLD, SHORELINE CUT PER CHUNK
    /// A lake's level is a property of its whole rim: the water rises until it finds
    /// the lowest point it can escape through, and that point may be in another
    /// chunk. Filled chunk by chunk, a lake spanning a seam gets a different level on
    /// each side - a step straight across open water. So the fill runs once over the
    /// world (see WorldDrainage) and each chunk reads the surface back.
    ///
    /// The surface is read as the LOWEST of the four surrounding solve cells. Inside
    /// the lake all four hold the same spill elevation, so the surface is exactly
    /// flat; at the rim the land cells stand higher, so the lowest is still the spill
    /// elevation and the water never ramps up the bank. Where two lakes at different
    /// levels touch, the rim of the higher one reads the lower level for one solve
    /// cell - shallower, never wider.
    ///
    /// The GROUND it is subtracted from stays at chunk resolution, so the shoreline
    /// is as sharp as the heightmap: a coarse solve does not give a coarse coast.
    ///
    /// A MINIMUM AREA, BECAUSE PUDDLES ARE NOT LAKES
    /// Every pit in a noise field is a closed basin, and flooding all of them
    /// speckles the terrain with pond-sized discs that read as artefacts. Each body
    /// is measured as a whole - half a lake is not a lake - and anything under the
    /// minimum is left as dry ground.
    /// </remarks>
    [System.Serializable]
    public class LakeFillNode : Generator
    {
        public override string NodeName { get { return "Lake Fill"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        // Role: LAKE SURFACE LEVEL. It computes the flat elevation each basin fills to
        // (plus the wet mask and depth that fall out of it). It does NOT place or shape a
        // basin - Lake Basin cuts the rugged bed, and the water body reads this level to
        // know how high to fill. Kept separate so the surface and the bed have one author
        // each and cannot disagree about the waterline.
        public override string Tooltip { get { return "Lake surface level: floods closed basins to their spill elevation. Outputs mask, depth and level."; } }

        // Same split as River Fill: the input mask RESTRICTS where lakes may form, the
        // first output is the water that ended up there. One name for both made the
        // feed and the result indistinguishable in the graph.
        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Water Level", PortType.Matrix, PortDirection.Output, true)
        };

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Lake Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Cells per side of the world fill. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>
        /// Water a body must cover before it counts as a lake, in square metres.
        /// Sixty square metres - a patch about eight metres across - is the floor:
        /// anything smaller is a rain puddle in a heightmap dimple and is left dry.
        /// Above that the SIZE IS NOT SET HERE. Each basin fills to its own spill
        /// elevation, so one world gives a tarn and a broad lake from the same
        /// number, which is what a real landscape does. Raise it to keep only the
        /// big water; lowering it below 60 starts reading noise as lakes.
        /// </summary>
        public float minAreaSquareMetres = 60f;
        /// <summary>UC-T03-08: optional world-area count/area scaling; defaults retain all eligible basins and the absolute minimum above.</summary>
        public LakeDistributionSettings lakeDistribution = new LakeDistributionSettings();

        /// <summary>Basins shallower than this are ignored, 0..1 of world height.</summary>
        public float minDepth = 0.002f;

        /// <summary>Deepest a lake reads as full, used to normalise the depth port.</summary>
        public float depthNormalise = 0.05f;

        /// <summary>Sea level, 0..1 of world height. Lakes do not form at or below it.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Softness of the shoreline in the mask, 0..1.</summary>
        public float shoreSoftness = 0.15f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            Matrix inputMask = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int count = src.arr.Length;
            if (count == 0) return dst;

            float floor = Mathf.Max(0f, minDepth);
            float sea = Mathf.Clamp01(seaLevel);

            // Zero epsilon: a lake surface is genuinely flat, and here that is wanted.
            // No flow needed - only the filled surface decides where water stands.
            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                     0f, false, stop);
            if (stop != null && stop.StopRequested) return null;

            // No solve means no standing water anywhere. Water Level still answers with
            // the ground so a wired port is never a hole in the world.
            if (solve == null)
            {
                // UC-T03-03: even a ground-valued water-level fallback is a water product,
                // not terrain carrying a river excavation reference.
                if (outputIndex == 2) System.Array.Copy(src.arr, dst.arr, dst.arr.Length);
                return dst;
            }

            // UC-T03-08: resolve once in world units, never per chunk or by changing the fill elevation.
            var distribution = (lakeDistribution ?? new LakeDistributionSettings()).Resolve(
                (double)tile.fullWorldSize.x * tile.fullWorldSize.z, 0, 0, 0, Mathf.Max(0f, minAreaSquareMetres));
            using var lakeSelectionMemory = tile.ReserveProductMemory(4096L + (long)solve.resX * solve.resZ * 64L); // UC-T03-08: selection scratch remains admitted through sampling.
            float[] worldLevel = LakeSurface(solve, floor, sea, distribution.MinimumAreaM2, distribution.MaximumAreaM2,
                lakeDistribution != null && lakeDistribution.scaleToWorld ? distribution.TargetCount : -1, out var report, stop);
            if (stop != null && stop.StopRequested) return null;
            tile.AddPublication(new LakeDistributionGraphPublication(id + ":lakes", report));

            float[] level = solve.ReadAtChunk(worldLevel, dst, WorldRead.Minimum, stop); // UC-T03-04: cancel during chunk reads.
            if (stop != null && stop.StopRequested) return null;

            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);
            float soft = Mathf.Clamp01(shoreSoftness);

            for (int i = 0; i < count; i++)
            {
                float ground = src.arr[i];
                float surface = level[i];
                float d = surface - ground;

                if (surface <= sea + 1e-5f || d < floor)
                {
                    dst.arr[i] = outputIndex == 2 ? ground : 0f;
                    continue;
                }

                if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(d * norm); continue; }
                if (outputIndex == 2) { dst.arr[i] = Mathf.Clamp01(surface); continue; }

                // Mask: fade in over the first slice of depth so the shoreline is not
                // a hard one-pixel edge where it meets a shallow gradient.
                float edge = soft > 1e-4f
                    ? Mathf.Clamp01((d - floor) / (soft * Mathf.Max(1e-4f, depthNormalise)))
                    : 1f;

                float finalMask = InterpolationMath.SmoothStep(edge);
                if (inputMask != null) finalMask *= Mathf.Clamp01(inputMask.arr[i]);

                dst.arr[i] = finalMask;
            }

            return dst;
        }

        /// <summary>
        /// World-scale water surface: the spill elevation of every basin large enough
        /// to be a lake, and bare ground everywhere else.
        /// </summary>
        /// <remarks>
        /// The flood fill is iterative over an explicit stack rather than recursive: a
        /// single basin can cover most of the grid, and recursion would overflow the
        /// worker thread's stack long before it finished.
        ///
        /// Shared with the Hydrology Spawner for the same reason River Network Trace's
        /// stamp is: the basins this finds are the basins the spawner places Lake
        /// objects in. A second copy of the fill would drift from this one and put a
        /// lake where no basin was flooded.
        /// </remarks>
        internal static float[] LakeSurface(WorldDrainage solve, float floor, float sea,
                                            float minArea, StopToken stop)
        { return LakeSurface(solve, floor, sea, minArea, float.PositiveInfinity, -1, out _, stop); } // UC-T03-08: preserve the original method group.

        // UC-T03-08: select only already eligible connected pools. Count limits never alter filled heights.
        // Largest area ranks first, lowest solve cell breaks ties; one membership scan bounds retained work.
        internal static float[] LakeSurface(WorldDrainage solve, float floor, float sea, float minArea,
            float maxArea, int maximumCount, out LakeDistributionReport report, StopToken stop)
        {
            report = null;
            if (solve == null || solve.height == null || solve.height.arr == null || solve.filled == null ||
                maximumCount < -1 || float.IsNaN(minArea) || float.IsInfinity(minArea) || minArea < 0 ||
                float.IsNaN(maxArea) || maxArea < minArea || float.IsNaN(floor) || float.IsInfinity(floor) || floor < 0 ||
                float.IsNaN(sea) || float.IsInfinity(sea))
                throw new System.ArgumentException("Invalid lake selection area/count limits.");
            if (stop != null && stop.StopRequested) return null;
            int resX = solve.resX;
            int resZ = solve.resZ;
            int count = resX * resZ;

            float[] ground = solve.height.arr;
            float[] filled = solve.filled;
            if (ground.Length != count || filled.Length != count)
                throw new System.ArgumentException("Lake selection lattice dimensions do not match the supplied fields."); // UC-T03-08.

            float[] level = new float[count];
            bool[] wet = new bool[count];

            for (int i = 0; i < count; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) return null;
                if (float.IsNaN(ground[i]) || float.IsInfinity(ground[i]) || float.IsNaN(filled[i]) || float.IsInfinity(filled[i]))
                    throw new System.ArgumentException("Lake selection requires finite ground and filled samples.");
                level[i] = ground[i];
                wet[i] = filled[i] > sea + 1e-5f && filled[i] - ground[i] > floor;
            }

            bool[] seen = new bool[count];
            int[] stack = new int[count];
            int[] body = new int[count];
            int[] membership = new int[count];
            for (int i = 0; i < count; i++) membership[i] = -1;
            var eligible = new System.Collections.Generic.List<(int start, float area)>();

            for (int start = 0; start < count; start++)
            {
                if (!wet[start] || seen[start]) continue;
                if (stop != null && stop.StopRequested) return null;

                // One standing body: every wet cell reachable from this one.
                int top = 0;
                int size = 0;
                stack[top++] = start;
                seen[start] = true;

                while (top > 0)
                {
                    if (stop != null && (size & 1023) == 0 && stop.StopRequested) return null; // UC-T03-08: large connected pools remain cancellable.
                    int i = stack[--top];
                    body[size++] = i;

                    int x = i % resX;
                    int z = i / resX;

                    if (x > 0) { int n = i - 1; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (x < resX - 1) { int n = i + 1; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (z > 0) { int n = i - resX; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                    if (z < resZ - 1) { int n = i + resX; if (wet[n] && !seen[n]) { seen[n] = true; stack[top++] = n; } }
                }

                float area = solve.AreaOfCells(size);
                if (area < minArea || area > maxArea) continue;

                for (int k = 0; k < size; k++)
                    membership[body[k]] = eligible.Count;
                eligible.Add((start, area));
            }

            System.Collections.Generic.List<int> ranked = null;
            if (maximumCount >= 0)
            {
                ranked = new System.Collections.Generic.List<int>(eligible.Count);
                for (int i = 0; i < eligible.Count; i++) ranked.Add(i);
                ranked.Sort((a, b) => { int order = eligible[b].area.CompareTo(eligible[a].area);
                    return order != 0 ? order : eligible[a].start.CompareTo(eligible[b].start); });
            }
            int selectedCount = maximumCount < 0 ? eligible.Count : Mathf.Min(maximumCount, eligible.Count);
            var selected = new bool[eligible.Count];
            report = new LakeDistributionReport { RequestedCount = maximumCount, EligibleCount = eligible.Count, SelectedCount = selectedCount };
            for (int i = 0; i < selectedCount; i++)
            {
                int bodyIndex = ranked != null ? ranked[i] : i; selected[bodyIndex] = true; float area = eligible[bodyIndex].area;
                report.SelectedAreaM2 += area;
                report.SmallestAreaM2 = i == 0 ? area : Mathf.Min(report.SmallestAreaM2, area);
                report.LargestAreaM2 = Mathf.Max(report.LargestAreaM2, area);
            }
            for (int i = 0; i < count; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) { report = null; return null; }
                if (membership[i] >= 0 && selected[membership[i]]) level[i] = filled[i];
            }
            return level;
        }
    }

    /// <summary>
    /// Reshapes the bank so a water surface actually meets the ground it sits in.
    /// Takes the terrain plus the Water Level and mask of any water body - lake,
    /// river or ocean - and returns the terrain with a shore cut into it.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NOT OPTIONAL DRESSING
    /// The fill nodes decide where the water is and how high it stands. Nothing in
    /// that decides where the ground is, and a water surface at one elevation
    /// crossing a bank of arbitrary steepness does not meet it: the water's boundary
    /// ends at whatever height the mask faded out at, which is not the ground's
    /// height there. That is the floating edge and the rectangular notch. Seating
    /// has to happen on the GROUND side, which is why it is a height node and not
    /// another output port on Lake Fill.
    ///
    /// WHY A SEPARATE NODE RATHER THAN BUILT INTO EACH FILL
    /// The seat needs a ghost ring as wide as its bench plus skirt - wider than
    /// either fill node needs for itself - and the arithmetic is identical for a
    /// lake, a river and a coast. One node reached by all three costs one padded
    /// pull and keeps one implementation; a port on each fill node would mean three
    /// copies and three different margins. Chain one per body, or combine the masks
    /// first and seat them together.
    ///
    /// Feed it the SAME Water Level and mask that drive the water surface. Seating
    /// against a different level is worse than not seating at all: the ground would
    /// be cut to meet water that is not there.
    /// </remarks>
    [System.Serializable]
    public class WaterBankSeatNode : Generator
    {
        public override string NodeName { get { return "Water Bank Seat"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cuts a shore so the water surface meets the ground exactly."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Water Level", PortType.Matrix, PortDirection.Input),
            new Port("Water Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>
        /// Width of the shelf held level with the water, in metres. The waterline
        /// lands inside this band, which is what makes the meeting exact rather than
        /// approximate. Zero still works - the skirt alone puts ground and water
        /// together at the mask edge - but a few metres absorbs the mask's own fade.
        /// </summary>
        public float benchWidth = 3f;

        /// <summary>Steepest bank left beside the water, rise over run. 0.5 = about 27 degrees.
        /// Lower gives a wider, gentler valley side; the cliff around a lake was this being unlimited.</summary>
        [Range(0.1f, 2f)] public float bankSlope = 0.5f;

        /// <summary>Most a dry bank may be raised to meet the water, in metres. Stops the seat
        /// building a flat levee around a lake whose surface sits above the nearby ground.</summary>
        public float maxRaiseMetres = 1.5f;

        /// <summary>
        /// Distance over which the bench eases back to the natural terrain, in
        /// metres. Too small reads as a moat; roughly a bank's own width reads as a
        /// bank.
        /// </summary>
        public float skirtWidth = 90f;

        /// <summary>
        /// Clearance kept between the bed and the surface, 0-1 of world height. Stops
        /// a bump in the bed from poking back through its own water.
        /// </summary>
        public float minDepth = 0.001f;

        /// <summary>0 leaves the terrain untouched, 1 applies the full seat.</summary>
        public float strength = 1f;

        // Past this the padded pull re-evaluates the whole upstream graph over a much
        // larger rect for a blend the eye cannot follow anyway.
        private const int MaxMargin = 96;
        private const float MinSkirtMetres = 60f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            // Water just outside the chunk still owns bank inside it, and the sweep
            // that measures distance to water is a bounded neighbourhood - so a ghost
            // ring the width of the bench plus skirt is the complete fix. See
            // TileWindows for why drainage needs something else entirely.
            float mpp = MetresPerPixel(tile);
            // A skirt shorter than this cannot hold a hillside to a valley slope, which is what
            // made the cliffs. Applied here so graphs saved with the old 30 m value are fixed too.
            float skirt = Mathf.Max(skirtWidth, MinSkirtMetres);
            int margin = WaterBankSeat.MarginPixels(benchWidth, skirt, mpp, MaxMargin);

            TileData view = TileWindows.Expand(tile, margin);
            if (view == null) { view = tile; margin = 0; }

            Matrix src = view.PullInput(this, 0, stop) as Matrix;
            if (src == null) return tile.CreateMatrix();

            Matrix level = view.PullInput(this, 1, stop) as Matrix;
            Matrix mask = view.PullInput(this, 2, stop) as Matrix;
            if (stop != null && stop.StopRequested) return null;

            // No water described means no bank to cut. Passing the terrain through is
            // the honest answer - a half-wired seat must not deform anything.
            if (level == null || mask == null)
                return margin > 0 ? TileWindows.Crop(src, tile, margin) : new Matrix(src);

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            float[] seated = WaterBankSeat.Apply(src.arr, level.arr, mask.arr,
                                                 resX, resZ, src.PixelSize.x,
                                                 benchWidth, skirt,
                                                 minDepth, strength,
                                                 src.worldHeight > 1e-3f ? src.worldHeight : 600f,
                                                 bankSlope, maxRaiseMetres, cancel);

            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();
            System.Array.Copy(seated, dst.arr, dst.arr.Length);
            // UC-T03-03: seat the same declared water on reference ground before cropping;
            // otherwise this specialized writer discards river relief downstream of a seat.
            if (src.trenchReference != null)
            {
                float[] referenceSeated = WaterBankSeat.Apply(src.trenchReference.arr, level.arr, mask.arr,
                    resX, resZ, src.PixelSize.x, benchWidth, skirt, minDepth, strength,
                    src.worldHeight > 1e-3f ? src.worldHeight : 600f, bankSlope, maxRaiseMetres, cancel);
                if (stop != null && stop.StopRequested) return null;
                dst.trenchReference = new Matrix(src.rect, referenceSeated) {
                    worldPos = src.worldPos, worldSize = src.worldSize, worldHeight = src.worldHeight };
            }

            return margin > 0 ? TileWindows.Crop(dst, tile, margin) : dst;
        }

        /// <summary>Metres one heightmap pixel of this chunk covers.</summary>
        private static float MetresPerPixel(TileData tile)
        {
            if (tile == null) return 1f;
            return tile.rect.size.x > 1
                ? tile.worldSize.x / (tile.rect.size.x - 1)
                : tile.worldSize.x;
        }
    }

    /// <summary>
    /// Sea level as a mask, with depth and a shoreline band. Trivially a threshold,
    /// but every downstream node wants the same sea level and the same softened
    /// edge, so it belongs in one node rather than six copies of a Height Select.
    /// </summary>
    [System.Serializable]
    public class OceanLevelNode : Generator
    {
        public override string NodeName { get { return "Ocean Level"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Sea level to ocean mask, depth, and a shoreline band."; } }

        // "Water Mask" rather than "Ocean Mask" so the sea's footprint plugs into Water
        // Bank Seat's Water Mask input by name, exactly as a lake's or a river's does.
        // The Shoreline output is a different signal - the beach band ABOVE the water -
        // so it keeps its own name.
        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Shoreline", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Sea level, 0..1 of world height.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Vertical width of the shoreline band, 0..1.</summary>
        public float shorelineBand = 0.015f;

        /// <summary>Softness of the ocean mask edge, 0..1.</summary>
        public float edgeSoftness = 0.004f;

        /// <summary>Depth at which the depth port saturates.</summary>
        public float depthNormalise = 0.2f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            float sea = Mathf.Clamp01(seaLevel);
            float band = Mathf.Max(1e-5f, shorelineBand);
            float soft = Mathf.Max(1e-5f, edgeSoftness);
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);

            for (int i = 0; i < src.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float h = src.arr[i];

                if (outputIndex == 1)
                {
                    dst.arr[i] = Mathf.Clamp01((sea - h) * norm);
                    continue;
                }

                if (outputIndex == 2)
                {
                    // Peaks at the waterline and falls off both ways: this is the
                    // beach, not "everything below sea level".
                    float dist = Mathf.Abs(h - sea) / band;
                    dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(dist));
                    continue;
                }

                dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01((h - sea) / soft + 0.5f));
            }

            return dst;
        }
    }

    /// <summary>
    /// Segments the world into drainage basins. Port 0 is a basin id for texturing or
    /// biome variation, port 1 marks the divides - the ridge lines that separate one
    /// catchment from the next - and port 2 how large each basin is.
    /// </summary>
    /// <remarks>
    /// A basin id is only meaningful if every chunk agrees on the numbering, and a
    /// basin's extent is decided by where its ridges run, which is not a chunk-local
    /// question. Labelled per chunk, basin 3 in one chunk is a different catchment
    /// from basin 3 next door, so any shading driven from this port changes at every
    /// tile border. So the labelling runs once over the world (see WorldDrainage) and
    /// each chunk reads its own labels back.
    ///
    /// The divides are then found at CHUNK resolution from those labels, which gives a
    /// one-pixel line rather than a band a whole solve cell wide. The line's PATH is
    /// still only as precise as the solve grid; a ridge is a broad feature, so that is
    /// the right trade.
    /// </remarks>
    [System.Serializable]
    public class WatershedBasinSegmentNode : Generator
    {
        public override string NodeName { get { return "Watershed Basin Segment"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Segments the world into drainage basins; also emits the divides."; } }

        private static readonly Port[] Outs =
        {
            new Port("Basin Id", PortType.Matrix, PortDirection.Output, true),
            new Port("Divides", PortType.Matrix, PortDirection.Output, true),
            new Port("Basin Size", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Cells per side of the world solve. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>Basins smaller than this fraction of the WORLD are left unlabelled.</summary>
        public float minBasinFraction = 0.002f;

        /// <summary>Randomises the emitted ids so adjacent basins contrast.</summary>
        public bool scrambleIds = true;

        public int seed = 616;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int outX = dst.rect.size.x;
            int outZ = dst.rect.size.z;
            int outCount = outX * outZ;
            if (outCount == 0) return dst;

            System.Func<bool> cancel = null;
            if (stop != null) cancel = delegate { return stop.StopRequested; };

            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      1e-6f, true, stop);
            if (solve == null) return dst;
            if (stop != null && stop.StopRequested) return null;

            int worldCount = solve.resX * solve.resZ;

            int basinCount;
            int[] label = Hydrology.Basins(solve.dir, solve.resX, solve.resZ,
                                           out basinCount, cancel);
            if (stop != null && stop.StopRequested) return null;
            if (basinCount <= 0) return dst;

            int[] sizes = new int[basinCount];
            for (int i = 0; i < worldCount; i++)
                if (label[i] >= 0) sizes[label[i]]++;

            int[] chunkLabel = solve.ReadLabelsAtChunk(label, dst);
            if (stop != null && stop.StopRequested) return null;

            if (outputIndex == 2)
            {
                // Scaled against the world's largest basin, so the port means the same
                // thing in every chunk.
                int largest = 0;
                for (int b = 0; b < basinCount; b++)
                    if (sizes[b] > largest) largest = sizes[b];

                float invLargest = largest > 0 ? 1f / largest : 0f;
                for (int i = 0; i < outCount; i++)
                {
                    int b = chunkLabel[i];
                    dst.arr[i] = b >= 0 ? Mathf.Clamp01(sizes[b] * invLargest) : 0f;
                }

                return dst;
            }

            if (outputIndex == 1)
            {
                // A divide is where two different basins touch.
                for (int z = 0; z < outZ; z++)
                {
                    if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                    int row = z * outX;
                    for (int x = 0; x < outX; x++)
                    {
                        int i = row + x;
                        int mine = chunkLabel[i];
                        bool divide = false;

                        for (int n = 0; n < 4 && !divide; n++)
                        {
                            int nx = x + (n == 0 ? -1 : n == 1 ? 1 : 0);
                            int nz = z + (n == 2 ? -1 : n == 3 ? 1 : 0);
                            if (nx < 0 || nz < 0 || nx >= outX || nz >= outZ) continue;
                            if (chunkLabel[nz * outX + nx] != mine) divide = true;
                        }

                        dst.arr[i] = divide ? 1f : 0f;
                    }
                }

                return dst;
            }

            // Basin id, optionally scrambled so neighbouring ids are not adjacent
            // greys - two basins that share a divide should look different.
            int minSize = Mathf.RoundToInt(Mathf.Clamp01(minBasinFraction) * worldCount);
            Noise noise = new Noise(seed);
            float invBasins = basinCount > 1 ? 1f / (basinCount - 1) : 0f;

            for (int i = 0; i < outCount; i++)
            {
                int b = chunkLabel[i];
                if (b < 0 || sizes[b] < minSize) { dst.arr[i] = 0f; continue; }

                dst.arr[i] = scrambleIds ? noise.Random(b) : b * invBasins;
            }

            return dst;
        }
    }

    /// <summary>
    /// Wetlands: flat ground, high throughflow, close to standing water. All three
    /// have to hold at once - a flat hilltop is not a marsh and a steep stream bank
    /// does not hold water.
    /// </summary>
    /// <remarks>
    /// Throughflow and ponding both come from the world drainage solve rather than a
    /// chunk-local one, because the land draining through a marsh usually starts well
    /// outside the chunk it sits in. Solved per chunk the marshes appear as a stripe
    /// down each tile instead of in the valley floors. Slope and the noise that breaks
    /// up the edges stay at chunk resolution, where the detail is.
    /// </remarks>
    [System.Serializable]
    public class GroundwaterWetlandMaskNode : Generator
    {
        public override string NodeName { get { return "Groundwater Wetland Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Marks marsh ground: low slope, high flow, near standing water."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Water Mask", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        /// <summary>Cells per side of the world drainage solve. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>Slope above which ground drains too fast to stay wet, in degrees.</summary>
        public float maxSlope = 4f;

        /// <summary>
        /// Land that must drain through before ground stays saturated, in square
        /// metres. Five hectares of hillside keeps a valley floor wet in a temperate
        /// climate. An area rather than a cell count, so the answer does not change
        /// when the solve resolution does.
        /// </summary>
        public float saturationArea = 50000f;

        /// <summary>How far the influence of standing water reaches, in metres.</summary>
        public float waterProximity = 90f;

        /// <summary>Elevation above local water that still counts as wet, 0..1.</summary>
        public float tableDepth = 0.006f;

        /// <summary>Breaks the edges up so the marsh is not an obvious contour.</summary>
        public float edgeNoise = 0.3f;

        public int seed = 4242;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix water = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      1e-6f, true, stop);
            if (solve == null) return dst;
            if (stop != null && stop.StopRequested) return null;

            // Throughflow is a smooth quantity, so it blends. The ponded surface is a
            // water level, so it takes the lowest of its neighbours - see WorldDrainage.
            float[] accum = solve.ReadAtChunk(solve.accum, dst, WorldRead.Bilinear, stop);
            float[] filled = solve.ReadAtChunk(solve.filled, dst, WorldRead.Minimum, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix slopes = MatrixOperations.SlopeAngles(src);
            if (stop != null && stop.StopRequested) return null;

            // Proximity to standing water: blurring the water mask is a cheap and
            // stable distance proxy, and it stays smooth across tile edges.
            Matrix nearWater = null;
            if (water != null)
            {
                Vector2 wps = src.PixelSize;
                float wmpp = Mathf.Max(1e-4f, (wps.x + wps.y) * 0.5f);
                float sigma = Mathf.Clamp(waterProximity / wmpp, 0.5f, 48f);
                nearWater = MatrixOperations.GaussianBlur(water, sigma);
            }

            Noise noise = new Noise(seed);
            float slopeLimit = Mathf.Max(0.5f, maxSlope);
            float flowLimit = solve.CellsForArea(saturationArea);
            float table = Mathf.Max(1e-5f, tableDepth);
            float noiseAmt = Mathf.Clamp01(edgeNoise);

            float mppX = src.PixelSize.x;
            float mppZ = src.PixelSize.y;
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    // SlopeAngles already reports degrees, 0 flat to 90 vertical.
                    float angle = slopes.arr[i];
                    float flat = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(angle / slopeLimit));
                    if (flat <= 0.001f) continue;

                    float wet = Mathf.Clamp01(accum[i] / flowLimit);

                    // Ponded depth from the fill: ground the water table is at or
                    // just below is saturated even without visible surface water.
                    float ponded = Mathf.Clamp01((filled[i] - src.arr[i]) / table);

                    float near = nearWater != null ? Mathf.Clamp01(nearWater.arr[i] * 2f) : 0f;

                    float v = flat * Mathf.Clamp01(Mathf.Max(Mathf.Max(wet, ponded), near));

                    if (noiseAmt > 0.001f)
                    {
                        float wx = originX + x * mppX;
                        float n = noise.Fractal(wx, wz, 140f, 3);
                        v *= 1f - noiseAmt + n * noiseAmt * 2f;
                    }

                    dst.arr[i] = Mathf.Clamp01(v);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Shapes the sea bed below sea level: submarine canyons cut along the drainage
    /// the land already established, plus seamounts. Coastal Shelf Shaper sets the
    /// broad gradient; this puts detail on it.
    /// </summary>
    [System.Serializable]
    public class UnderwaterTerrainExtendNode : ModifierNode
    {
        public override string NodeName { get { return "Underwater Terrain Extend"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Adds submarine canyons and seamounts below sea level."; } }

        public int seed = 1919;

        /// <summary>Cells per side of the world drainage solve. Coarse on purpose.</summary>
        public int drainageResolution = 256;

        /// <summary>Sea level, 0..1. Nothing above it is touched.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Depth of submarine canyons, 0..1 of world height.</summary>
        public float canyonDepth = 0.05f;

        /// <summary>
        /// Land a canyon needs draining into it before it cuts at full depth, in
        /// square metres. Matching River Network Trace's channel head area is what
        /// lines a submarine canyon up with the river mouth that fed it.
        /// </summary>
        public float canyonCatchmentArea = 250000f;

        /// <summary>How far offshore canyons persist, 0..1 of the submarine range.</summary>
        public float canyonReach = 0.7f;

        /// <summary>Height of seamounts, 0..1.</summary>
        public float seamountHeight = 0.06f;

        /// <summary>Seamount spacing in world metres.</summary>
        public float seamountSpacing = 2200f;

        /// <summary>Fine sediment ripple amplitude, 0..1.</summary>
        public float sedimentDetail = 0.004f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float sea = Mathf.Clamp01(seaLevel);
            if (sea <= 1e-5f) return;

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;

            // Canyons follow the drainage the LAND already carved, which is why a
            // submarine canyon lines up with the river mouth that fed it - and why the
            // drainage has to be the world's, not this chunk's. Solved only when
            // canyons are actually wanted; it is the expensive part of this node.
            float[] accum = null;
            float canyonCells = 1f;

            if (canyonDepth > 1e-5f)
            {
                WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                         1e-6f, true, stop);
                if (stop != null && stop.StopRequested) return;

                if (solve != null)
                {
                    accum = solve.ReadAtChunk(solve.accum, m, WorldRead.Bilinear, stop);
                    canyonCells = solve.CellsForArea(canyonCatchmentArea);
                    if (stop != null && stop.StopRequested) return;
                }
            }

            Noise noise = new Noise(seed);
            Noise mountNoise = noise.WithSubSeed(77);

            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSpacing = Mathf.Max(1f, seamountSpacing);
            float reach = Mathf.Clamp01(canyonReach);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float h = m.arr[i];
                    if (h >= sea) continue;

                    float wx = originX + x * mppX;

                    // 0 at the shoreline, 1 at the deepest point.
                    float submarine = 1f - Mathf.Clamp01(h / sea);
                    float value = h;

                    if (accum != null)
                    {
                        float channel = Mathf.Clamp01(accum[i] / canyonCells);
                        float fade = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(submarine / Mathf.Max(1e-4f, reach)));
                        value -= canyonDepth * channel * fade;
                    }

                    if (seamountHeight > 1e-5f)
                    {
                        // Cellular distance inverted: a cone rising from each cell
                        // centre, only offshore where the abyssal plain is.
                        float d = mountNoise.Voronoi(wx / safeSpacing, wz / safeSpacing, 0f, VoronoiBlend.Cellular);
                        float cone = Mathf.Clamp01(1f - d * 3.5f);
                        value += seamountHeight * cone * cone * submarine;
                    }

                    if (sedimentDetail > 1e-6f)
                    {
                        float ripple = noise.Fractal(wx, wz, 60f, 3) - 0.5f;
                        value += ripple * 2f * sedimentDetail * submarine;
                    }

                    // Never push the sea bed above the waterline.
                    m.arr[i] = Mathf.Clamp(value, 0f, sea - 1e-5f);
                }
            }
        }
    }

    /// <summary>
    /// Shapes a swamp: a low-energy, flat depression where a static water table sits at or
    /// just above a hummocky floor (§25). It does NOT trench like a river - it flattens a
    /// low-lying, gently-sloped region toward a swamp floor and lays low-amplitude hummock
    /// micro-relief over it, then emits a Swamp Mask whose value is the water-table depth
    /// zone (0 = dry mound, ~0.5 = saturated shallows, 1 = deep pool) for the splat and
    /// scatter to read. The swamp region is either hand-painted (wire a mask into Swamp Mask
    /// in) or detected procedurally (flat AND lying near the water table). Placement is the
    /// graph author's or a painted mask's; a Swamp water body puts the visible water in.
    /// </summary>
    [System.Serializable]
    public class SwampBasinNode : Generator
    {
        public override string NodeName { get { return "Swamp Basin"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Flattens a low-lying basin into a hummocky swamp floor and emits a water-table depth mask."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Swamp Mask", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("Swamp Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Water-table height, 0..1 of world height. The swamp forms around this.</summary>
        public float swampLevel = 0.4f;

        /// <summary>How far below the water table the flattened floor sits, 0..1. Sets pool depth.</summary>
        public float floorDrop = 0.02f;

        /// <summary>How strongly the swamp floor is flattened toward the level, 0..1.</summary>
        public float flatten = 0.85f;

        /// <summary>Procedural detection: max per-pixel height gradient still counted as "flat".</summary>
        public float detectMaxSlope = 0.02f;

        /// <summary>Detection band below the water table, 0..1 (a basin sits at/below the table).</summary>
        public float bandBelow = 0.03f;

        /// <summary>Detection band above the water table, 0..1 (micro-islands rise just above).</summary>
        public float bandAbove = 0.01f;

        /// <summary>Hummock micro-relief amplitude, 0..1 (keep tiny - real hummocks are ~0.3 m).</summary>
        public float hummockAmp = 0.0015f;

        /// <summary>Hummock feature size in metres.</summary>
        public float hummockFeatureMetres = 25f;

        /// <summary>Seed, so a swamp is identical on every rebuild.</summary>
        public int seed = 4242;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix paint = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            // UC-T03-03: the swamp's detection/flattening must also transform reference
            // ground, so only the remaining river relief survives terminal composition.
            Matrix result = GenerateSwamp(src, paint, outputIndex, stop);
            if (result != null && outputIndex != 1 && src.trenchReference != null &&
                (stop == null || !stop.StopRequested))
                result.trenchReference = GenerateSwamp(src.trenchReference, paint, 0, stop);
            return result;
        }

        private Matrix GenerateSwamp(Matrix src, Matrix paint, int outputIndex, StopToken stop)
        {
            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;

            Matrix dst = new Matrix(src);
            Matrix maskOut = src.Blank();

            Vector2 ps = src.PixelSize;
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;
            Noise noise = new Noise(seed);

            float level = Mathf.Clamp01(swampLevel);
            float drop = Mathf.Max(0f, floorDrop);
            float floor = Mathf.Clamp01(level - drop);
            float flat = Mathf.Clamp01(flatten);
            float amp = Mathf.Max(0f, hummockAmp);
            float feat = Mathf.Max(1f, hummockFeatureMetres);
            float maxSlope = Mathf.Max(1e-5f, detectMaxSlope);
            float below = Mathf.Max(1e-4f, bandBelow);
            float above = Mathf.Max(1e-4f, bandAbove);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float h = src.arr[i];

                    // Swamp presence 0..1: painted where a mask is wired, else detected as
                    // FLAT ground lying near the water table (a low-energy depression). A
                    // steep or high cell scores 0, so a swamp never climbs a hillside.
                    float weight;
                    if (paint != null)
                    {
                        weight = Mathf.Clamp01(paint.arr[i]);
                    }
                    else
                    {
                        float band = h > level
                            ? 1f - Mathf.Clamp01((h - level) / above)
                            : 1f - Mathf.Clamp01((level - h) / below);

                        int xl = Mathf.Max(0, x - 1), xr = Mathf.Min(resX - 1, x + 1);
                        int zl = Mathf.Max(0, z - 1), zr = Mathf.Min(resZ - 1, z + 1);
                        float gx = src.arr[row + xr] - src.arr[row + xl];
                        float gz = src.arr[zr * resX + x] - src.arr[zl * resX + x];
                        float grad = Mathf.Sqrt(gx * gx + gz * gz) * 0.5f;
                        float flatF = 1f - Mathf.Clamp01(grad / maxSlope);

                        weight = Mathf.Clamp01(band) * flatF;
                    }

                    float outH = h;
                    if (weight > 0.001f)
                    {
                        // Flatten toward the floor, then lay low hummock mounds/hollows over
                        // it. The mound relief is what makes the shallows read as organic mud
                        // rather than a flat pond.
                        outH = Mathf.Lerp(h, floor, flat * weight);
                        if (amp > 1e-6f)
                        {
                            float wx = originX + x * ps.x;
                            float wz = originZ + z * ps.y;
                            float n = noise.Fractal(wx, wz, feat, 3) - 0.5f;
                            outH += n * 2f * amp * weight;
                        }
                        outH = Mathf.Clamp01(outH);
                    }

                    dst.arr[i] = outH;

                    // Water-table depth zone: dz = level - floor-adjusted ground. 0 at a dry
                    // mound (dz <= 0), rising to 1 at pool depth, scaled by presence so the
                    // mask is 0 outside the swamp.
                    float dz = level - outH;
                    maskOut.arr[i] = weight * Mathf.Clamp01(dz / Mathf.Max(1e-4f, drop));
                }
            }

            return outputIndex == 1 ? maskOut : dst;
        }
    }

    /// <summary>
    /// Baked ambient / sky occlusion (§27a — the mobile-safe capture of Lumen's diffuse
    /// indirect-shadowing intent). For each cell it measures how much of the sky the
    /// heightfield hides — the maximum upward horizon slope in several directions — and
    /// outputs a 0..1 sky-visibility mask (1 = open sky, lower = a shadowed crease, valley or
    /// cliff base). Computed ONCE at generation and read cheaply by the splat/shader, so
    /// hollows get believable soft indirect darkening at ZERO per-frame cost — the Lumen
    /// LOOK a phone can afford, with no ray-march, no SDF volume and no runtime GI. Self-
    /// consistent in the heightfield's own units, so it needs no world vertical scale.
    /// </summary>
    [System.Serializable]
    public class SkyOcclusionNode : Generator
    {
        public override string NodeName { get { return "Sky Occlusion"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Baked horizon ambient occlusion from the heightfield: soft indirect darkening in valleys, mobile-cheap."; } }

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        /// <summary>Directions sampled around each cell. 8 balances quality and phone cost.</summary>
        public int directions = 8;

        /// <summary>How far, in pixels, to look for occluding terrain along each direction.</summary>
        public int reachPixels = 16;

        /// <summary>Scales the measured horizon slope into occlusion. Higher = deeper shadows.</summary>
        public float strength = 6f;

        /// <summary>Floor on sky visibility, so a deep crease never goes pure black.</summary>
        public float minVisibility = 0.25f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            Matrix outAo = src.Blank();

            int dirs = Mathf.Clamp(directions, 1, 16);
            int reach = Mathf.Clamp(reachPixels, 1, 128);
            float k = Mathf.Max(0f, strength);
            float floor = Mathf.Clamp01(minVisibility);

            float[] dcx = new float[dirs];
            float[] dcz = new float[dirs];
            for (int d = 0; d < dirs; d++)
            {
                float a = (Mathf.PI * 2f) * d / dirs;
                dcx[d] = Mathf.Cos(a);
                dcz[d] = Mathf.Sin(a);
            }

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 15) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float h0 = src.arr[i];

                    // Average over directions of the maximum UPWARD horizon slope (normalised
                    // rise per pixel). More terrain rising around a cell = less visible sky =
                    // more occlusion. This is horizon-based AO on the heightfield, in its own
                    // units - no metre scale required, and fully baked so runtime cost is nil.
                    float horizonSum = 0f;
                    for (int d = 0; d < dirs; d++)
                    {
                        float maxSlope = 0f;
                        for (int s = 1; s <= reach; s++)
                        {
                            int sx = Mathf.Clamp(x + Mathf.RoundToInt(dcx[d] * s), 0, resX - 1);
                            int sz = Mathf.Clamp(z + Mathf.RoundToInt(dcz[d] * s), 0, resZ - 1);
                            float rise = src.arr[sz * resX + sx] - h0;
                            if (rise <= 0f) continue;
                            float slope = rise / s;
                            if (slope > maxSlope) maxSlope = slope;
                        }
                        horizonSum += maxSlope;
                    }

                    float occlusion = Mathf.Clamp01((horizonSum / dirs) * k);
                    outAo.arr[i] = Mathf.Max(floor, 1f - occlusion);
                }
            }

            return outAo;
        }
    }
}
