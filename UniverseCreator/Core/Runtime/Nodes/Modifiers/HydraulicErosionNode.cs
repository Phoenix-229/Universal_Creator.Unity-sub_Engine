// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HydraulicErosionNode.cs
//  Purpose: Water erosion. Wraps the five-stage solver in Erosion.Hydraulic:
//           height ordering, torrent (flow accumulation), cutting, mudflow
//           transport, and settling.
//
//  This is the single most expensive node in the library and the one that does
//  the most for realism, because it carves drainage networks that are globally
//  consistent rather than locally noisy.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class HydraulicErosionNode : ModifierNode
    {
        public override string NodeName { get { return "Hydraulic Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Water erosion. Carves drainage networks and valleys."; } }

        /// <summary>Full erode/settle passes.</summary>
        public int iterations = 3;

        /// <summary>0-1. Rock resistance. Higher erodes less.</summary>
        public float durability = 0.9f;

        /// <summary>Scales how much material each pass removes.</summary>
        public float erosionAmount = 1f;

        /// <summary>Fraction of removed material that becomes transportable mudflow.</summary>
        public float sedimentAmount = 0.5f;

        /// <summary>Mudflow redistribution sub-passes.</summary>
        public int fluidityIterations = 3;

        /// <summary>Post-pass blur sigma in pixels.</summary>
        public float relax = 0.5f;


        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Erosion.HydraulicSettings settings = new Erosion.HydraulicSettings
            {
                iterations = Mathf.Clamp(iterations, 1, 32),
                durability = Mathf.Clamp01(durability),
                erosionAmount = Mathf.Max(0f, erosionAmount),
                sedimentAmount = Mathf.Clamp01(sedimentAmount),
                fluidityIterations = Mathf.Clamp(fluidityIterations, 1, 16),
                relax = Mathf.Max(0f, relax)
            };

            // The solver probes cancellation once per iteration, so a stop request
            // leaves the field partially eroded but still valid.
            Erosion.Hydraulic(m, settings, null, null, null,
                              stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);
        }
    }
}
