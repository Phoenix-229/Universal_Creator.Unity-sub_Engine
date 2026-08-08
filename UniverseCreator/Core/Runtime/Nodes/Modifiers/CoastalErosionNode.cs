// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CoastalErosionNode.cs
//  Purpose: Cuts a wave-notch terrace in a band around sea level, flattening
//           beaches and undercutting cliffs. Restricted to the band, so inland
//           terrain is untouched.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class CoastalErosionNode : ModifierNode
    {
        public override string NodeName { get { return "Coastal Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Cut a wave-notch terrace around sea level."; } }

        /// <summary>Water plane, in normalised height.</summary>
        public float seaLevel = 0.3f;

        /// <summary>Half-width of the affected band.</summary>
        public float waveHeight = 0.02f;

        /// <summary>0-1. How flat the beach terrace is cut.</summary>
        public float strength = 0.5f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Erosion.Coastal(m, seaLevel, Mathf.Max(0f, waveHeight), Mathf.Clamp01(strength));
        }
    }
}
