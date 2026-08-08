// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ThermalErosionNode.cs
//  Purpose: Thermal weathering / talus relaxation (Musgrave). Slopes steeper than
//           the angle of repose shed material downhill until they relax back to
//           that angle, which is what builds scree fans below cliffs.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class ThermalErosionNode : ModifierNode
    {
        public override string NodeName { get { return "Thermal Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Talus relaxation. Builds scree fans below cliffs."; } }

        public int iterations = 8;

        /// <summary>Angle of repose in degrees. Sand sits near 34, most scree 30-45.</summary>
        public float talusAngle = 35f;

        /// <summary>0-1. Fraction of the excess moved per pass.</summary>
        public float amount = 0.5f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Erosion.ThermalSettings settings = new Erosion.ThermalSettings
            {
                iterations = Mathf.Clamp(iterations, 1, 128),
                talusAngle = Mathf.Clamp(talusAngle, 1f, 89f),
                amount = Mathf.Clamp01(amount)
            };

            Erosion.Thermal(m, settings, null,
                            stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);
        }
    }
}
