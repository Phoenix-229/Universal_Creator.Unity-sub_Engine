// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: per-type rules implemented
//  File:    RockScatterNode.cs
//  Purpose: Places rock/boulder prefabs, biased toward steep and exposed ground.
//           The base solver handles placement and normal alignment; this node
//           adds the random tilt and the embed offset that stop a boulder from
//           reading as a decal sitting on the surface.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class RockScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Rock Scatter"; } }
        public override string Tooltip { get { return "Places boulders on steep ground, embedded and tilted."; } }
        public override string Channel { get { return "rocks"; } }

        /// <summary>Sink each rock this far into the surface so it reads as embedded.</summary>
        public float embedDepth = 0.2f;

        /// <summary>Random tilt away from surface normal, in degrees.</summary>
        public float randomTilt = 15f;

        /// <summary>Align the rock's up axis to the terrain normal before tilting.</summary>
        public bool alignToNormal = true;

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
