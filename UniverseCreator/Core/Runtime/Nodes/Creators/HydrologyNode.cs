using UnityEngine;
using UniverseCreator.Nodes;
using System.Collections.Generic;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class HydrologyNode : CreatorNode
    {
        public override string NodeName { get { return "Hydrology Spawner"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Automatically spawns River and Lake water bodies up to a target count."; } }

        [Header("Hydrology Counts")]
        public int targetRiverCount = 3;
        // Zero means use the coverage percentage exactly. A positive value is an
        // explicit upper bound for authored worlds that want fewer lakes.
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

        [Header("River Networks")]
        [Tooltip("Merge this many small rivers to form a Big river.")]
        public int minTributaries = 3;

        [Tooltip("Enable river deltas / distributaries when a big river meets the ocean.")]
        public bool enableDistributaries = true;
        public int maxDistributaries = 4;

        [Header("Ocean")]
        // One world-sized sea whose level sits at the world-edge falloff. It is a flat
        // surface clipped at the shore (it floods wherever the ground is already below
        // sea level), not a rectangle stamped into the terrain, so islands and coastline
        // follow the real landscape. Off leaves the world land-locked.
        [Tooltip("Spawn one world-sized ocean at the world-edge falloff level, clipped at the shore.")]
        public bool generateOcean = true;

        private static readonly Port[] Outs =
        {
            new Port("Lake Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetOutputs() { return Outs; }


        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            // Hydrology generation spawns GameObjects and must run on the main thread.
            // It is executed by UniverseCreatorObject directly, so this method just fills zeros.
            m.Fill(0f);
        }
    }
}
