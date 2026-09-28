// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: Node Graph
//  File:    HydrologyNode.cs
//  Purpose: The Hydrology Spawner. Two jobs, one node:
//
//           1. It SETTLES THE COUNTS. How many lakes the world gets, and whether
//              it has an ocean, is read off this node by HydrologyGenerator, which
//              then places the actual UCWaterBody objects on the main thread.
//
//           2. It EMITS THE LAKE MASK. The Lake Mask says where that standing
//              water will sit, at chunk resolution, so the height chain can carve
//              a basin for it before it arrives.
//
//           The count and the mask describe the SAME water: both are read from one
//           lake-surface solve of the world, so the basins the mask floods are the
//           basins the lakes are placed in. Rivers are NOT settled here - they are
//           hand-authored with the River Network Trace / River Fill selectors, and
//           this node no longer emits a river mask of its own.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes.Creators
{
    /// <summary>
    /// Spawns the world's lakes and ocean, and emits the Lake Mask that describes
    /// the standing water. Rivers are hand-authored elsewhere.
    /// </summary>
    /// <remarks>
    /// WHY THIS DERIVES FROM Generator AND NOT CreatorNode
    /// CreatorNode.Create builds from nothing - it ignores its inputs. This node
    /// must read the Height it is given to know where the world's basins are, so it
    /// derives from the general Generator base instead. It still reports
    /// NodeCategory.Creator, so it stays where users look for it. (It once had a
    /// second, differing output - a River Mask - which was another reason it could
    /// not sit on CreatorNode; that port is gone with procedural river placement,
    /// but the Height input keeps the base-class choice the same.)
    ///
    /// WHY IT TAKES A HEIGHT INPUT
    /// It used to take none and fill its port with zeros, which left it with
    /// nothing to say and nothing to say it about. Worse, wiring a zero-filled
    /// creator into the height chain flattened the world, so the node could only be
    /// left dangling - and a dangling node reaches no Output, which is what
    /// UCWaterSystemManager.ConnectedHydrology looks for. The spawner was therefore
    /// invisible to the only code that calls it, and no water object was ever built
    /// while the graph carved channels regardless. Reading Height fixes both halves:
    /// the node has real ground to solve, and it is safe to wire.
    /// </remarks>
    [System.Serializable]
    public class HydrologyNode : Generator
    {
        public override string NodeName { get { return "Hydrology Spawner"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }

        public override string Tooltip
        {
            get { return "Spawns Lake and Ocean bodies and outputs the Lake Mask that describes them. Rivers are hand-authored."; }
        }

        // NO RIVER CONTROLS LIVE HERE ANY MORE
        // Rivers are placed by the author, not by a number typed into a spawner.
        // targetRiverCount, minTributaries, enableDistributaries and maxDistributaries
        // were all write-only: the resolver filled them in and nothing downstream ever
        // read one, so the Inspector offered river controls that changed nothing. The
        // channel-network fields (Channel Head Area, Max Channels) went the same way
        // when the spawner stopped emitting a river mask - the drainage network is now
        // decided entirely on the River Network Trace selector that authors it. What
        // this node still owns is the standing water: the lakes and the ocean.

        [Header("Hydrology Counts")]
        // AN EXPLICIT COUNT IS AN INSTRUCTION, NOT A CEILING
        // Zero hands the decision to Lake Coverage below, which sizes the lake set
        // from the world's area. Any positive value replaces that density outright -
        // it is NOT clamped down to what the percentage would have allowed, because
        // HydrologyGenerator used to take Min(count, percentage) and a small world
        // whose percentage rounded to zero silently threw the typed number away.
        // What still limits it is the terrain: every lake has to be a real basin
        // holding MinLakeAreaSquareMetres of water deeper than Min Lake Depth, so a
        // world without that many basins produces fewer lakes than were asked for.
        [Tooltip("Lakes to place. 0 uses Lake Coverage instead; a typed count overrides it.")]
        public int targetLakeCount = 0;

        [Header("Lake Coverage")]
        [Range(0f, 1f)]
        [Tooltip("Fraction of terrain chunks eligible for procedural lakes.")]
        public float lakeChunkPercentage = 0.24f;

        [Header("Lake Outflow")]
        // A lake with an inflow must have an outflow, or it is a sink and not a lake.
        // Counted as a fraction of the world's chunks for the same reason the lakes
        // are: the number of drain-out rivers a landscape supports scales with its
        // area, not with a fixed figure. At 8% against the 24% lake coverage, about
        // one lake in three overflows into a river - so a 5x5 world gets 6 lakes and
        // 2 outlets, and a 10x10 world gets 24 lakes and 8 outlets.
        [Range(0f, 1f)]
        [Tooltip("Fraction of terrain chunks that get a lake drain-out river.")]
        public float lakeOutletChunkPercentage = 0.08f;

        [Header("Ocean")]
        // One world-sized sea whose level sits at the world-edge falloff. It is a flat
        // surface clipped at the shore (it floods wherever the ground is already below
        // sea level), not a rectangle stamped into the terrain, so islands and coastline
        // follow the real landscape. Off leaves the world land-locked.
        [Tooltip("Spawn one world-sized ocean at the world-edge falloff level, clipped at the shore.")]
        public bool generateOcean = true;

        // ------------------------------------------------------------------
        //  Mask solve
        //
        //  These describe the standing water the counts above will produce. They
        //  carry the same names, defaults and meanings as the Lake Fill selector on
        //  purpose: a graph that runs the spawner beside that node must not get two
        //  different answers about where the water is.
        // ------------------------------------------------------------------

        /// <summary>
        /// Cells per side of the world drainage solve. Coarse on purpose: it decides
        /// where the water goes, not how sharp its banks are.
        /// </summary>
        [Header("Mask Solve")]
        [Tooltip("Cells per side of the world drainage solve. Coarse on purpose. The settle-time solve runs "
               + "this grid on the main thread, so a high value is what freezes the editor for tens of "
               + "seconds after a build (the 'busy for 48s' dialog). 160 keeps the basins faithful while "
               + "cutting that solve to roughly a third.")]
        public int drainageResolution = 160;

        /// <summary>
        /// Sea level, 0..1 of world height. Rivers end here, lakes do not form at or
        /// below it, and it is where the ocean surface sits.
        /// </summary>
        /// <remarks>
        /// A datum, not a mesh: it means the same thing on a world with the ocean
        /// switched off, and HydrologyGenerator reads this very field for the waterline
        /// when there is no sea body to measure. Where one IS placed, it is the water
        /// that is the truth and this field follows it - UCWaterSystemManager pushes the
        /// settled ocean surface back over every node that holds a copy of the elevation,
        /// this one included, so what the user types here is a starting position rather
        /// than the last word. Editing it while an ocean stands will be overwritten on
        /// the next sync; move the ocean instead.
        /// </remarks>
        [Range(0f, 1f)]
        [Tooltip("Sea level as a fraction of world height. Rivers end here; lakes do not form below it.")]
        public float seaLevel = 0.35f;

        /// <summary>Basins shallower than this are ignored, 0..1 of world height.</summary>
        [Header("Lake Mask")]
        [Tooltip("Basins shallower than this are not lakes. Fraction of world height.")]
        public float minLakeDepth = 0.002f;

        /// <summary>Deepest a lake reads as full, used to scale the shoreline fade.</summary>
        [Tooltip("Depth at which a lake reads as fully flooded. Fraction of world height.")]
        public float depthNormalise = 0.05f;

        /// <summary>Softness of the shoreline in the mask, 0..1.</summary>
        [Range(0f, 1f)]
        [Tooltip("Softness of the shoreline edge in the Lake Mask.")]
        public float shoreSoftness = 0.15f;

        // ------------------------------------------------------------------
        //  Ports
        // ------------------------------------------------------------------

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Lake Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            return LakeMask(tile, stop);
        }

        /// <summary>
        /// Where standing water ends up: every basin large enough to be a lake, flooded
        /// to its own spill elevation.
        /// </summary>
        /// <remarks>
        /// The flood itself is Lake Fill's, called rather than copied. The basins this
        /// marks are the basins HydrologyGenerator drops Lake objects into, so a second
        /// implementation here would eventually disagree with it and leave a lake
        /// sitting on ground the graph never flooded.
        /// </remarks>
        private object LakeMask(TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();   // Height not connected: no water
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();
            int count = src.arr.Length;
            if (count == 0) return dst;

            float floor = Mathf.Max(0f, minLakeDepth);
            float sea = Mathf.Clamp01(seaLevel);

            // Zero epsilon: a lake surface is genuinely flat, and here that is wanted.
            // No flow needed - only the filled surface decides where water stands.
            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution, 0f, false, stop);
            if (stop != null && stop.StopRequested) return null;
            if (solve == null) return dst;

            float[] worldLevel = LakeFillNode.LakeSurface(solve, floor, sea,
                                                          HydrologyGenerator.MinLakeAreaSquareMetres, stop);
            if (stop != null && stop.StopRequested) return null;

            // Minimum, not bilinear: inside a body all four cells hold the same level so
            // the read is exactly flat, and at the rim it keeps the surface at its spill
            // elevation instead of letting it ramp up the bank.
            float[] level = solve.ReadAtChunk(worldLevel, dst, WorldRead.Minimum);
            if (stop != null && stop.StopRequested) return null;

            float band = Mathf.Max(1e-4f, depthNormalise) * Mathf.Clamp01(shoreSoftness);

            for (int i = 0; i < count; i++)
            {
                float depth = level[i] - src.arr[i];

                if (level[i] <= sea + 1e-5f || depth < floor) { dst.arr[i] = 0f; continue; }

                // Fade in over the first slice of depth so the shoreline is not a hard
                // one-pixel edge where the water meets a shallow gradient.
                float edge = band > 1e-5f ? Mathf.Clamp01((depth - floor) / band) : 1f;
                dst.arr[i] = InterpolationMath.SmoothStep(edge);
            }

            return dst;
        }
    }
}
