// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: per-type rules implemented
//                     Module 3: routed through the one deposit model (V-44)
//  File:    RockScatterNode.cs
//  Purpose: Places rock and boulder prefabs. With ecology present the deposit model
//           decides where stone rests, how it grades and how deep it sits; without
//           it, the authored tilt and embed depth below do.
//
//  THIS NODE NO LONGER HAS ITS OWN OPINION ABOUT HOW STONE SITS. It used to: a
//  hand-authored randomTilt and embedDepth applied to every rock equally, which is a
//  second implementation of what DebrisShaping now does by process - and a worse one,
//  because a constant tilt cannot know that talus lies with the slope while a
//  frost-split block does not, and a constant embed depth cannot know that a boulder
//  weathered in place sits deeper than one that fell last winter. Both fields are
//  kept because they are serialised graph data and they remain the answer for a world
//  with no ecology context, but they are the fallback now and not the model (C-15).
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class RockScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Rock Scatter"; } }
        public override string Tooltip { get { return "Places boulders by deposit process, embedded and tilted."; } }
        public override string Channel { get { return "rocks"; } }

        /// <summary>Fallback sink depth, used only when no ecology context is present.</summary>
        public float embedDepth = 0.2f;

        /// <summary>Fallback random tilt away from surface normal, in degrees.</summary>
        public float randomTilt = 15f;

        /// <summary>Align the rock's up axis to the terrain normal before tilting.</summary>
        public bool alignToNormal = true;

        /// <summary>
        /// Which prototype represents each DebrisKind, indexed by the enum; -1 means
        /// unbound and nothing is placed for that kind. Asset binding, not placement
        /// authoring - see DebrisShaping.
        /// </summary>
        public int[] debrisPrototypes;

        // Feeds the existing serialized bool into the base solver's 0-1 alignment
        // so the base builds the aligned rotation directly, instead of this node
        // rebuilding every rotation from scratch afterwards.
        protected override float NormalAlignment
        {
            get { return alignToNormal ? 1f : normalAlignment; }
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            if (instances.Count == 0) return;

            // The one stone path. Null binding is passed deliberately when the author
            // has not bound kinds: the deposit model still decides where rock rests and
            // how it grades and lies, and each instance keeps whatever prototype the
            // solver gave it. That is strictly better than the authored constants, and
            // it needs no binding to be an improvement.
            if (DebrisShaping.Apply(tile, height, seed, instances,
                                    HasBinding() ? debrisPrototypes : null, stop))
                return;

            ApplyAuthoredTiltAndEmbed(instances, stop);
        }

        private bool HasBinding()
        {
            return debrisPrototypes != null && debrisPrototypes.Length > 0;
        }

        // The pre-Module-3 behaviour, reached only without an ecology context.
        private void ApplyAuthoredTiltAndEmbed(List<ScatterInstance> instances, StopToken stop)
        {
            // Own hash stream, offset from the base solver's so tilt does not
            // correlate with the scale and yaw it already assigned.
            Noise tiltNoise = new Noise(seed + 32452843);

            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];

                if (randomTilt > 0.01f)
                {
                    int hx = Mathf.RoundToInt(inst.position.x * 100f);
                    int hz = Mathf.RoundToInt(inst.position.z * 100f);

                    // Tilt axis and amount from two decorrelated hashes: a fixed
                    // axis would lean every rock in the same direction.
                    float axisAngle = tiltNoise.Random(hx, hz) * 360f;
                    float amount = (tiltNoise.Random(hz + 15485863, hx + 49979687) * 2f - 1f) * randomTilt;

                    Vector3 axis = new Vector3(Mathf.Cos(axisAngle * Mathf.Deg2Rad), 0f,
                                               Mathf.Sin(axisAngle * Mathf.Deg2Rad));

                    inst.rotation = Quaternion.AngleAxis(amount, axis) * inst.rotation;
                }

                // Sinking straight down rather than along the normal. On a steep
                // face a normal-aligned offset slides the rock sideways out of the
                // hollow it was placed in, which reads as floating beside the slope.
                if (embedDepth > 0f)
                    inst.position = new Vector3(inst.position.x,
                                                inst.position.y - embedDepth * inst.scale.y,
                                                inst.position.z);

                instances[i] = inst;
            }
        }
    }
}
