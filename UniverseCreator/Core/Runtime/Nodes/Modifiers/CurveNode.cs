// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CurveNode.cs
//  Purpose: Remaps values through an AnimationCurve. The curve's horizontal axis
//           is the input value, vertical axis is the output. Gives complete control
//           over value distribution, brightness curves, S-curves, and custom falloffs.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class CurveNode : ModifierNode, IPreparedGraphInputHash
    {
        public override string NodeName { get { return "Curve"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Remap values through an AnimationCurve."; } }

        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        private float[] _samples;

        /// <summary>UC-T01-07: fingerprints the actual prepared remap samples, including curve removal.</summary>
        /// <returns>Deterministic fingerprint; call after main-thread preparation.</returns>
        public ulong GetPreparedInputHash() { return PreparedRasterInputs.Hash(256, 1, _samples); }

        public override void PrepareMainThread()
        {
            if (curve == null || curve.length == 0)
            {
                _samples = null;
                return;
            }

            // Bake 256 samples of the curve so Generate() never touches the Unity API.
            _samples = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255f;
                _samples[i] = curve.Evaluate(t);
            }
        }

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            if (_samples == null)
            {
                // Passthrough when no curve is present.
                return;
            }

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float v = InterpolationMath.Clamp01(m.arr[i]);
                    int idx = Mathf.FloorToInt(v * 255f);
                    idx = Mathf.Clamp(idx, 0, 255);
                    m.arr[i] = _samples[idx];
                }
            }
        }
    }
}
