// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    CurvatureSelectorNode.cs
//  Purpose: Marks ridges and valleys. Positive curvature (convex, ridges) outputs
//           1 at positive threshold, negative curvature (concave, valleys) outputs
//           1 at negative threshold. Zero curvature is flat planar surface.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    public enum CurvatureMode
    {
        Ridges = 0,     // Select convex, positive curvature
        Valleys = 1,    // Select concave, negative curvature
        Both = 2        // Select both extremes
    }

    [System.Serializable]
    public class CurvatureSelectorNode : SelectorNode
    {
        public override string NodeName { get { return "Curvature"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Select ridges or valleys by Laplacian curvature."; } }

        public CurvatureMode mode = CurvatureMode.Ridges;
        public float threshold = 0.1f;

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            // Compute Laplacian: centre minus average of four neighbours.
            int w = src.rect.size.x;
            int h = src.rect.size.z;

            for (int z = 1; z < h - 1; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;

                for (int x = 1; x < w - 1; x++)
                {
                    int i = rowStart + x;
                    float c = src.arr[i];
                    float l = src.arr[i - 1];
                    float r = src.arr[i + 1];
                    float u = src.arr[i - w];
                    float d = src.arr[i + w];

                    float avg = (l + r + u + d) * 0.25f;
                    float laplacian = c - avg;

                    float v = 0f;
                    float t = Mathf.Max(0.001f, threshold);

                    if (mode == CurvatureMode.Ridges)
                    {
                        v = Mathf.Clamp01(laplacian / t);
                    }
                    else if (mode == CurvatureMode.Valleys)
                    {
                        v = Mathf.Clamp01(-laplacian / t);
                    }
                    else
                    {
                        v = Mathf.Clamp01(Mathf.Abs(laplacian) / t);
                    }

                    mask.arr[i] = v;
                }
            }
        }
    }
}
