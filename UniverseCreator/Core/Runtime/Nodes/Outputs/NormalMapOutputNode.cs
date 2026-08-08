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

        public float strength = 1f;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            Vector3[] normals = MatrixOperations.SobelNormals(src);
            if (stop != null && stop.StopRequested) return;

            // Blend toward vertical when strength < 1.
            if (strength < 0.99f)
            {
                float t = Mathf.Clamp01(strength);
                Vector3 up = Vector3.up;

                for (int i = 0; i < normals.Length; i++)
                {
                    normals[i] = Vector3.Lerp(up, normals[i], t).normalized;
                }
            }

            tile.outputs.normals = normals;
        }
    }
}
