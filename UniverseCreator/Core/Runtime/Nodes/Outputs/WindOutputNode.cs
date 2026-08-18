// -----------------------------------------------------------------------------
//  Universe Creator
//  File:    WindOutputNode.cs
//  Purpose: Terminates a wind branch. Takes the amplitude and phase maps from
//           Wind Sway Parameter Bake and puts them where the runtime can upload
//           them, which is what finally makes those maps visible on screen.
//
//  Before this node existed, Wind Sway Parameter Bake produced correct maps that
//  nothing consumed - the variation it described never reached a shader. This is
//  the terminal that closes that gap.
//
//  Two inputs rather than one, so it does not fit OutputNode's single-matrix
//  shape and derives from Generator directly. Amplitude alone is enough to be
//  useful; phase is optional and defaults to zero, which just means neighbouring
//  patches swing together.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class WindOutputNode : Generator, IOutputNode
    {
        public override string NodeName { get { return "Wind Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }

        public override string Tooltip
        {
            get
            {
                return "Sends baked sway amplitude and phase to the wind manager, so grass and "
                     + "bushes using the WindDetail shader vary with shelter.";
            }
        }

        private static readonly Port[] Ins =
        {
            new Port("Sway Amplitude", PortType.Matrix, PortDirection.Input),
            new Port("Sway Phase", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        /// <summary>
        /// Scales the baked amplitude on the way out. Use it to dial the whole
        /// world's sway down without re-authoring the bake node.
        /// </summary>
        [Tooltip("Multiplies the baked amplitude. 1 uses it as authored.")]
        [Range(0f, 2f)]
        public float amplitudeScale = 1f;

        /// <summary>
        /// Lowest amplitude any point is allowed. Zero means fully sheltered
        /// ground freezes solid, which reads as broken rather than as calm, so a
        /// small floor is the better default.
        /// </summary>
        [Tooltip("Minimum sway anywhere. Stops sheltered ground looking frozen.")]
        [Range(0f, 0.5f)]
        public float minimumAmplitude = 0.08f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix amplitude = GetInputMatrix(tile, 0, stop);
            if (amplitude == null) return null;
            if (stop != null && stop.StopRequested) return null;

            Matrix phase = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;

            int w = amplitude.rect.size.x;
            int h = amplitude.rect.size.z;
            if (w < 2 || h < 2) return null;

            int n = w * h;

            float[] amp = new float[n];
            float scale = Mathf.Max(0f, amplitudeScale);
            float floor = Mathf.Clamp01(minimumAmplitude);

            for (int i = 0; i < n; i++)
            {
                float v = InterpolationMath.Clamp01(amplitude.arr[i] * scale);
                amp[i] = v < floor ? floor : v;
            }

            float[] ph = null;
            if (phase != null && phase.arr != null && phase.arr.Length >= n)
            {
                ph = new float[n];
                for (int i = 0; i < n; i++)
                    ph[i] = InterpolationMath.Clamp01(phase.arr[i]);
            }

            tile.outputs.windSwayAmplitude = amp;
            tile.outputs.windSwayPhase = ph;
            tile.outputs.windSwaySize = new Vector2Int(w, h);
            tile.outputs.windSwayWorldMin = new Vector2(tile.worldPos.x, tile.worldPos.z);
            tile.outputs.windSwayWorldSize = new Vector2(tile.worldSize.x, tile.worldSize.z);

            return null;
        }
    }
}
