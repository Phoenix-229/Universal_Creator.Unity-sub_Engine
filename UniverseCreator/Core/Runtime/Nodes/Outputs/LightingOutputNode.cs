// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    LightingOutputNode.cs
//  Purpose: Terminates a lighting branch. Takes up to five authored bias fields -
//           sky tint, ambient scale, turbidity, cloud cover and exposure - and
//           records them where the lighting driver can read them.
//
//  BIASES, NOT OVERRIDES
//  ---------------------
//  Every port here nudges a value the atmosphere physics already computed; it never
//  replaces it. The driver adds or scales the field onto the sky the sun's position,
//  the air mass and the day cycle already produced, so a wired bias shifts the look
//  of the air without cutting it loose from the physics that lit it. A port that
//  overrode the physics outright would let an author paint noon light at midnight,
//  which is the guesswork the whole system exists to leave behind.
//
//  WHY FIVE MATRIX INPUTS
//  ----------------------
//  A bias is a field, not a number: an author wires it from a gradient to make one
//  valley hazier than the ridge, or from noise for patchy exposure. Each port is
//  optional - an unwired port is no bias at all, and the driver reads a null field
//  as "leave this one alone", so a graph that wires none behaves exactly as one with
//  no lighting branch. The first three ports keep the names the retired M4 lighting
//  output used, so a graph saved against it reconnects unchanged; the last two are
//  appended after them, never inserted.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class LightingOutputNode : Generator, IOutputNode
    {
        public override string NodeName { get { return "Lighting Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }

        public override string Tooltip
        {
            get
            {
                return "Sends authored bias fields to the lighting driver. Each port shifts a "
                     + "computed value up or down - it never replaces the atmosphere physics.";
            }
        }

        // The three original ports come first and keep their names so a saved graph
        // reconnects; the last two are appended after them, never inserted.
        private static readonly Port[] Ins =
        {
            new Port("In Sky Tint",         PortType.Matrix, PortDirection.Input),
            new Port("In Ambient Scale",    PortType.Matrix, PortDirection.Input),
            new Port("In Turbidity",        PortType.Matrix, PortDirection.Input),
            new Port("In Cloud Cover Bias", PortType.Matrix, PortDirection.Input),
            new Port("In Exposure Bias",    PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (tile == null) return null;

            Matrix skyTint = GetInputMatrix(tile, 0, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix ambientScale = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix turbidity = GetInputMatrix(tile, 2, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix cloudCover = GetInputMatrix(tile, 3, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix exposure = GetInputMatrix(tile, 4, stop);
            if (stop != null && stop.StopRequested) return null;

            // The sample grid comes from the first wired port; a node with nothing
            // wired has no lighting branch to record and leaves the bag as it found it.
            Matrix shape = FirstNonNull(skyTint, ambientScale, turbidity, cloudCover, exposure);
            if (shape == null) return null;

            int w = shape.rect.size.x;
            int h = shape.rect.size.z;
            if (w < 2 || h < 2) return null;
            int n = w * h;

            tile.outputs.lightingSkyTintBias      = CopyBias(skyTint, n);
            tile.outputs.lightingAmbientScaleBias = CopyBias(ambientScale, n);
            tile.outputs.lightingTurbidityBias    = CopyBias(turbidity, n);
            tile.outputs.lightingCloudCoverBias   = CopyBias(cloudCover, n);
            tile.outputs.lightingExposureBias     = CopyBias(exposure, n);

            tile.outputs.lightingBiasSize      = new Vector2Int(w, h);
            tile.outputs.lightingBiasWorldMin  = new Vector2(tile.worldPos.x, tile.worldPos.z);
            tile.outputs.lightingBiasWorldSize = new Vector2(tile.worldSize.x, tile.worldSize.z);

            return null;
        }

        /// <summary>
        /// Copies one bias field to a flat array, or null when the port is unwired or
        /// its field does not fit the shared grid. Non-finite samples become zero so a
        /// stray NaN upstream cannot poison the whole chunk's lighting.
        /// </summary>
        private static float[] CopyBias(Matrix m, int n)
        {
            if (m == null || m.arr == null || m.arr.Length < n) return null;

            float[] o = new float[n];
            for (int i = 0; i < n; i++)
            {
                float v = m.arr[i];
                o[i] = (float.IsNaN(v) || float.IsInfinity(v)) ? 0f : v;
            }
            return o;
        }

        /// <summary>The first non-null matrix, or null when every port is unwired.</summary>
        private static Matrix FirstNonNull(params Matrix[] ms)
        {
            for (int i = 0; i < ms.Length; i++)
                if (ms[i] != null) return ms[i];
            return null;
        }
    }
}
