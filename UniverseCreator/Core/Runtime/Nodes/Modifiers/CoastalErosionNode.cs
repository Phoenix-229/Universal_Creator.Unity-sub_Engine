// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CoastalErosionNode.cs
//  Purpose: Cuts a wave-notch terrace in a band around sea level, flattening
//           beaches and undercutting cliffs. Restricted to the band, so inland
//           terrain is untouched.
//
//  TEXTURING THE BEACH THIS CUTS
//  ----------------------------
//  This node shapes the beach; it does not paint it. The mask that says WHERE the
//  beach is already exists and does not need a second one: Ocean Level's third
//  output, "Shoreline", peaks exactly at the waterline and falls off in both
//  directions, which is a beach band and not "everything below sea level".
//
//  So the wiring for a continuous dry-ground -> wet-sand -> shallow transition,
//  with no gap in the splat weights along the waterline, is:
//
//      Ocean Level ("Shoreline") -> Height Blend / Texture Output sand weight
//      Ocean Level ("Depth")     -> the wet/shelf weight beyond it
//      Coastal Shelf Shaper      -> the underwater slope those weights sit on
//
//  Deliberately NOT a second output on this node. A ModifierNode is one height in,
//  one height out - its Generate ignores outputIndex - so a mask port here would
//  mean duplicating the ghost-margin plumbing and then maintaining two beach-band
//  definitions that would drift apart the first time either was tuned.
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
