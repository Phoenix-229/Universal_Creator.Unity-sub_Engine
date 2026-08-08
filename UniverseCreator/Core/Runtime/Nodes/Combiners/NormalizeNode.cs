// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    NormalizeNode.cs
//  Purpose: Rescales the value range so the field spans exactly [newMin, newMax].
//           Use after combines that overshoot, or to bring imported data into
//           the 0-1 range the output nodes expect.
//
//  NOTE ON SHAPE: the spec files this under Combiners, but normalisation is a
//  single-input operation — there is no second field to combine with. It is
//  therefore built on ModifierNode (one Height in, one Height out) while keeping
//  the Combiner category so it groups with its siblings in the add-node menu.
//
//  CHUNKING CAVEAT: min/max are measured per chunk. Two neighbouring chunks with
//  different local extremes will normalise to different scales and will not match
//  at the seam. Use fixed source bounds when generating a chunked world.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Combiners
{
    [System.Serializable]
    public class NormalizeNode : ModifierNode
    {
        public override string NodeName { get { return "Normalize"; } }
        public override NodeCategory Category { get { return NodeCategory.Combiner; } }
        public override string Tooltip { get { return "Rescale the value range to [min, max]."; } }

        public float newMin = 0f;
        public float newMax = 1f;

        /// <summary>
        /// When true the source range is taken from sourceMin/sourceMax instead of
        /// the chunk's measured extremes. Required for seamless chunked worlds.
        /// </summary>
        public bool useFixedSourceRange;
        public float sourceMin = 0f;
        public float sourceMax = 1f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float srcLo, srcHi;

            if (useFixedSourceRange)
            {
                srcLo = Mathf.Min(sourceMin, sourceMax);
                srcHi = Mathf.Max(sourceMin, sourceMax);
            }
            else
            {
                srcLo = float.MaxValue;
                srcHi = float.MinValue;

                for (int i = 0; i < m.arr.Length; i++)
                {
                    float v = m.arr[i];
                    if (v < srcLo) srcLo = v;
                    if (v > srcHi) srcHi = v;
                }

                if (srcLo > srcHi) return;      // empty matrix
            }

            float srcRange = srcHi - srcLo;
            if (srcRange < 1e-7f)
            {
                // Flat field: collapse to the midpoint of the target range rather
                // than dividing by zero.
                m.Fill((newMin + newMax) * 0.5f);
                return;
            }

            float invRange = 1f / srcRange;
            float dstLo = newMin;
            float dstRange = newMax - newMin;

            for (int z = 0; z < m.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * m.rect.size.x;
                for (int x = 0; x < m.rect.size.x; x++)
                {
                    int i = rowStart + x;
                    float t = (m.arr[i] - srcLo) * invRange;
                    m.arr[i] = dstLo + t * dstRange;
                }
            }
        }
    }
}
