// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    NormalMapOutputNode.cs
//  Purpose: Computes surface normals from the height and writes them into
//           OutputBag.normals for custom mesh/shader uses. Unity's TerrainData
//           auto-computes normals when you set heights, so this output is only
//           needed for special workflows.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class NormalMapOutputNode : OutputNode
    {
        public override string NodeName { get { return "Normal Map Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Write Sobel normals for custom mesh or shader workflows."; } }

        /// <summary>Normal blend strength in 0..1; invalid values use 1. Native commits reconcile this optional output with final ground.</summary>
        public float strength = 1f; // UC-T03-10: preserve the control through post-graph shaping.

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            Vector3[] normals = MatrixOperations.SobelNormals(src);
            if (stop != null && stop.StopRequested) return;

            // Blend toward vertical when strength < 1.
            float resolvedStrength = float.IsNaN(strength) || float.IsInfinity(strength) ? 1 : Mathf.Clamp01(strength); // UC-T03-10.
            if (resolvedStrength < 1f)
            {
                float t = resolvedStrength;
                Vector3 up = Vector3.up;

                for (int i = 0; i < normals.Length; i++)
                {
                    normals[i] = Vector3.Lerp(up, normals[i], t).normalized;
                }
            }

            tile.outputs.normals = normals;
            tile.outputs.normalStrength = resolvedStrength;
        }
    }
}
