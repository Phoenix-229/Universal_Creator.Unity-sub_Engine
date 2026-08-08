// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CustomMeshOutputNode.cs
//  Purpose: Builds a mesh from the height matrix and writes it into
//           OutputBag.meshes for custom renderers, collider baking, or export.
//           Not wired to Unity's TerrainData — the user instantiates a MeshFilter
//           and reads the MeshBuildData themselves.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class CustomMeshOutputNode : OutputNode
    {
        public override string NodeName { get { return "Custom Mesh Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Build a mesh from the heightfield. Not applied to TerrainData."; } }

        /// <summary>Channel name this mesh is filed under.</summary>
        public string meshChannel = "terrain";

        public float heightScale = 600f;

        /// <summary>Generate UVs spanning [0, 1] across the tile.</summary>
        public bool generateUVs = true;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            MeshBuildData mesh = new MeshBuildData();

            int vertCount = w * h;
            mesh.vertices = new Vector3[vertCount];
            if (generateUVs) mesh.uv = new Vector2[vertCount];

            float invW = 1f / Mathf.Max(1, w - 1);
            float invH = 1f / Mathf.Max(1, h - 1);

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 0; x < w; x++)
                {
                    int i = rowStart + x;
                    // FINAL SAFETY NET: Strict [0.0, 1.0] mathematical wall.
                    float clampedVal = Mathf.Clamp01(src.arr[i]);
                    float height = clampedVal * heightScale;

                    mesh.vertices[i] = new Vector3(x, height, z);
                    if (generateUVs) mesh.uv[i] = new Vector2(x * invW, z * invH);
                }
            }

            // Build triangle indices: two triangles per quad.
            int quadCount = (w - 1) * (h - 1);
            mesh.triangles = new int[quadCount * 6];

            int idx = 0;
            for (int z = 0; z < h - 1; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 0; x < w - 1; x++)
                {
                    int i0 = rowStart + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + w;
                    int i3 = i2 + 1;

                    mesh.triangles[idx++] = i0;
                    mesh.triangles[idx++] = i2;
                    mesh.triangles[idx++] = i1;

                    mesh.triangles[idx++] = i1;
                    mesh.triangles[idx++] = i2;
                    mesh.triangles[idx++] = i3;
                }
            }

            // Compute normals.
            mesh.normals = MatrixOperations.SobelNormals(src);

            tile.outputs.meshes[meshChannel] = mesh;
        }
    }
}
