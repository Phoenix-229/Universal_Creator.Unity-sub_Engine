// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ScatterNodeBase.cs
//  Purpose: Shared base for the six Scatter nodes.
//
//  MODULE 1 SCOPE: these nodes are stubs. The graph wiring, port contract,
//  parameter sets, and OutputBag channel plumbing are real and final — that is
//  what Module 1 owns. The placement solver (Poisson-disk sampling, density
//  weighting, slope/height rejection, collision resolution) belongs to Modules
//  2 and 3 and is marked TODO below.
//
//  A stubbed node reports its stubbed state via WasStubbed rather than quietly
//  emitting zero instances, so a graph author can tell "no instances because the
//  solver is not written yet" apart from "no instances because the mask rejected
//  everything".
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public abstract class ScatterNodeBase : Generator, IOutputNode
    {
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }

        public override Port[] GetInputs() { return SharedPorts.HeightMaskIn; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        /// <summary>
        /// OutputBag channel these instances are filed under. Module 2/3 consumers
        /// read by this key.
        /// </summary>
        public abstract string Channel { get; }

        /// <summary>Instances per square kilometre before mask weighting.</summary>
        public float density = 100f;

        /// <summary>Minimum spacing between instances, in world metres.</summary>
        public float minSpacing = 2f;

        /// <summary>Instances are rejected outside this height band.</summary>
        public float minHeight = 0f;
        public float maxHeight = 1f;

        /// <summary>Instances are rejected outside this slope band, in degrees.</summary>
        public float minSlope = 0f;
        public float maxSlope = 45f;

        /// <summary>Per-node seed so two scatter nodes don't produce identical layouts.</summary>
        public int seed = 1000;

        public Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        public bool randomYaw = true;

        /// <summary>
        /// True while the placement solver is unimplemented. Read by the editor
        /// to badge the node, so an empty result is never mistaken for a working
        /// node that found nothing.
        /// </summary>
        public bool WasStubbed { get { return true; } }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            Matrix mask = GetInputMatrix(tile, 1, stop);

            if (stop != null && stop.StopRequested) return null;

            List<ScatterInstance> instances = new List<ScatterInstance>();

            // TODO [Module 2/3]: implement the placement solver.
            //   1. Poisson-disk sample the tile at minSpacing, seeded by
            //      (seed, tile.chunkCoord) so results are deterministic and
            //      stable across regeneration.
            //   2. Weight candidate acceptance by density * mask value.
            //   3. Reject candidates outside [minHeight, maxHeight] sampled from
            //      the height matrix, and outside [minSlope, maxSlope] from the
            //      slope derived off it.
            //   4. Resolve position to world space, snap Y to the height field,
            //      randomise scale within scaleRange and yaw when randomYaw.
            //   5. Call PlaceInstances below to let each subclass apply its own
            //      prototype selection and per-type rules.
            PlaceInstances(height, mask, tile, instances, stop);

            tile.outputs.AddScatter(Channel, instances);
            return null;
        }

        /// <summary>
        /// Per-type placement rules. Receives the accepted candidate list from the
        /// shared solver and may filter, duplicate, or re-parameterise it.
        /// </summary>
        protected abstract void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop);
    }
}
