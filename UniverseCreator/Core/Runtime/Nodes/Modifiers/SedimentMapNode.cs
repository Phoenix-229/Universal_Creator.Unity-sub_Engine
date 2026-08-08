// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SedimentMapNode.cs
//  Purpose: Runs a hydraulic pass purely to harvest its deposition map, then
//           outputs that instead of the eroded height. Feeds texture nodes that
//           want to paint silt in the valleys.
//
//  The height result of the internal pass is discarded on purpose. If you want
//  both the eroded terrain and its sediment, run HydraulicErosionNode for the
//  height and this node for the mask with matching settings.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class SedimentMapNode : ModifierNode
    {
        public override string NodeName { get { return "Sediment Map"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Deposition mask from a hydraulic pass. Silt in the valleys."; } }

        public int iterations = 3;
        public float durability = 0.9f;
        public float erosionAmount = 1f;
        public float sedimentAmount = 0.5f;
        public int fluidityIterations = 3;

        /// <summary>Scales the harvested sediment before output.</summary>
        public float intensity = 1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Erosion.HydraulicSettings settings = new Erosion.HydraulicSettings
            {
                iterations = Mathf.Clamp(iterations, 1, 32),
                durability = Mathf.Clamp01(durability),
                erosionAmount = Mathf.Max(0f, erosionAmount),
                sedimentAmount = Mathf.Clamp01(sedimentAmount),
                fluidityIterations = Mathf.Clamp(fluidityIterations, 1, 16),
                relax = 0f
            };

            Matrix sediment = m.Blank();

            Erosion.Hydraulic(m, settings, sediment, null, null,
                              stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);

            if (stop != null && stop.StopRequested) return;

            // Replace the height with the deposition map.
            float scale = Mathf.Max(0f, intensity);
            for (int i = 0; i < m.arr.Length; i++)
            {
                m.arr[i] = sediment.arr[i] * scale;
            }
        }
    }
}
