// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//
//  File:    RiverFillNode.cs
//  Purpose: Puts WATER in the river. River Network carves the trench; nothing
//           filled it, so a procedurally-routed river was a dry slot in the ground.
//           This is the missing half - Unreal's Water Body River both carves AND
//           fills - and it produces the SAME three outputs Lake Fill does (Water
//           Mask, Depth, Water Level) so a river plugs into the identical bank-seat
//           and water-surface consumers a lake already uses, not a parallel set.
//
//  WHY THE SURFACE CANNOT CLIMB (the author's "water must not come out of the sea")
//  ------------------------------------------------------------------------------
//  The water surface is read from the depression-filled surface `filled`, which
//  priority-flood guarantees never decreases against the flow direction. So the
//  river surface descends from source to mouth BY CONSTRUCTION, and where the
//  channel reaches a lake or the sea the filled surface already equals that body's
//  level - so the river joins it seamlessly instead of standing above it. There is
//  no separate "enforce downhill" pass to get wrong.
//
//  WHY IT REUSES, NOT RE-DERIVES
//  -----------------------------
//    - `filled` / the whole-world solve: WorldDrainage.Solve, read per chunk by
//      ReadAtChunk with a Minimum read (the same flat-surface convention Lake Fill
//      uses), so the surface is seam-free and never steps at a chunk border.
//    - The channel's extent: the River Mask handed in by River Network, already at
//      chunk resolution - the wetted width is the channel, not the floodplain.
//
//  Deterministic: a pure function of the height field and the mask. No RNG, no clock.
//  Managed C# over float[]; no Unity API beyond Mathf. Safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Fills a carved river channel with water: a surface that descends to the mouth,
    /// the depth above the bed, and the wetted mask. Pair it after River Network
    /// (feed its River Mask) exactly as Lake Fill is paired after Lake Basin.
    /// </summary>
    /// <remarks>
    /// OUTPUTS (index order), matching Lake Fill so both feed Water Bank Seat and the
    /// water system through one contract:
    ///   0 Water Mask  - 0..1 wetted channel, feathered at the shallow edge.
    ///   1 Depth       - 0..1 water depth above the bed, normalised by depthNormalise.
    ///   2 Water Level - world-normalised surface elevation; ground where it is dry,
    ///                   so a wired Water Level port is never a hole in the world.
    /// </remarks>
    [System.Serializable]
    public class RiverFillNode : Generator
    {
        public override string NodeName { get { return "River Fill"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }

        public override string Tooltip
        {
            get
            {
                return "Fills a carved river channel with water: a surface that descends " +
                       "to the lake/ocean mouth, plus depth and wetted mask. Feed it River " +
                       "Network's Height and River Mask.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("River Mask", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Water Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Water Level", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Cells per side of the whole-world fill. Coarse on purpose, like Lake Fill.</summary>
        public int drainageResolution = 256;

        /// <summary>
        /// Normalised freeboard: how far the water surface sits BELOW the brim of the
        /// trench (0 fills to the bank top, bankfull). A small value keeps the water a
        /// touch below the lip so the bank reads as a bank, not a brimming canal.
        /// </summary>
        public float bankFreeboard = 0.004f;

        /// <summary>Shallowest water that still counts as wet, 0..1 of world height.</summary>
        public float minDepth = 0.0015f;

        /// <summary>Deepest a channel reads as full, used to normalise the Depth port.</summary>
        public float depthNormalise = 0.03f;

        /// <summary>Channel-mask strength below which a cell is treated as dry bank.</summary>
        public float channelThreshold = 0.02f;

        /// <summary>Softness of the wetted edge in the mask, 0..1 of the depth normalise.</summary>
        public float shoreSoftness = 0.2f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix riverMask = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = src.Blank();
            int count = src.arr.Length;
            if (count == 0) return outputIndex == 2 ? new Matrix(src) : dst;

            // No channel mask wired means no river to fill. Water Level still answers with
            // the ground so a wired port is never a hole; the other ports read dry.
            if (riverMask == null)
                return outputIndex == 2 ? new Matrix(src) : dst;

            // The depression-filled surface is the descending water surface (see header).
            // No flow needed - only `filled` decides how high water stands and that it
            // never climbs against the current.
            WorldDrainage solve = WorldDrainage.Solve(this, 0, tile, drainageResolution,
                                                      0f, false, stop);
            if (stop != null && stop.StopRequested) return null;
            if (solve == null || solve.filled == null)
                return outputIndex == 2 ? new Matrix(src) : dst;

            // Minimum read: a water surface is flat across its width, so where the coarse
            // cell corners straddle a bank the LOWEST keeps the true spill level rather
            // than ramping the surface up the bank (the same reason Lake Fill reads Minimum).
            float[] surfaceW = solve.filled;
            float[] chunkSurface = solve.ReadAtChunk(surfaceW, dst, WorldRead.Minimum, stop); // UC-T03-04: cancel during chunk reads.
            if (stop != null && stop.StopRequested) return null;

            float free = Mathf.Max(0f, bankFreeboard);
            float floor = Mathf.Max(0f, minDepth);
            float norm = 1f / Mathf.Max(1e-4f, depthNormalise);
            float chanMin = Mathf.Clamp01(channelThreshold);
            float soft = Mathf.Clamp01(shoreSoftness);

            for (int i = 0; i < count && i < chunkSurface.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float channel = i < riverMask.arr.Length ? Mathf.Clamp01(riverMask.arr[i]) : 0f;
                float ground = src.arr[i];
                float surface = chunkSurface[i] - free;
                float depth = surface - ground;

                // Dry unless this is actually the channel AND the surface stands above the
                // bed by more than the shallow floor. Outside the channel the river's own
                // water never spreads - the floodplain is damp ground, not open water.
                if (!IsWet(depth, channel, floor, chanMin))
                {
                    dst.arr[i] = outputIndex == 2 ? ground : 0f;
                    continue;
                }

                if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(depth * norm); continue; }
                if (outputIndex == 2) { dst.arr[i] = Mathf.Clamp01(surface); continue; }

                // Mask: fade in over the first slice of depth so the water's edge is not a
                // hard one-pixel line, then weight by the channel strength it was handed.
                float edge = soft > 1e-4f
                    ? Mathf.Clamp01((depth - floor) / (soft * Mathf.Max(1e-4f, depthNormalise)))
                    : 1f;

                dst.arr[i] = Mathf.Clamp01(InterpolationMath.SmoothStep(edge) * channel);
            }

            return dst;
        }

        /// <summary>
        /// The wet/dry decision for one cell, as a pure function so it can be self-tested.
        ///
        /// It is pulled out of the loop for the same reason the scatter water gate is: the
        /// sign of the test is the one thing a compile cannot catch. If "channel" and the
        /// depth floor were read the wrong way the node would fill the dry BANKS and leave
        /// the CHANNEL empty - a river made of its own levees - and only a pinned predicate
        /// catches that. Open water needs BOTH: the cell is actually the channel
        /// (<paramref name="channel"/> at least <paramref name="chanMin"/>), and the surface
        /// stands above the bed by more than the shallow floor.
        /// </summary>
        internal static bool IsWet(float depth, float channel, float floor, float chanMin)
        {
            if (channel < chanMin) return false;   // floodplain / dry bank, not the channel
            return depth >= floor;                 // deep enough to read as open water
        }
    }
}
