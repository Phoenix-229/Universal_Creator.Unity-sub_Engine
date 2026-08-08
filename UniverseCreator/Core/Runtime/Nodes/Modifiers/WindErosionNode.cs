// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    WindErosionNode.cs
//  Purpose: Aeolian erosion. Strips material from windward faces, carries it
//           downwind, and drops it where the surface turns away from the wind.
//           That asymmetry is what gives dunes their shallow windward slope and
//           steep lee face.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class WindErosionNode : ModifierNode
    {
        public override string NodeName { get { return "Wind Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Aeolian erosion. Dunes and wind-scoured faces."; } }

        /// <summary>Compass bearing the wind blows toward, in degrees.</summary>
        public float direction = 270f;

        /// <summary>Wind speed. Pickup scales with its square.</summary>
        public float windSpeed = 1f;

        public float strength = 0.5f;

        /// <summary>How many pixels downwind material is carried before settling.</summary>
        public float carryDistance = 8f;

        public int iterations = 2;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Erosion.WindSettings settings = new Erosion.WindSettings
            {
                direction = direction,
                windSpeed = Mathf.Max(0f, windSpeed),
                strength = Mathf.Max(0f, strength),
                carryDistance = Mathf.Max(1f, carryDistance),
                iterations = Mathf.Clamp(iterations, 1, 32)
            };

            Erosion.Wind(m, settings, null,
                         stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);
        }
    }
}
