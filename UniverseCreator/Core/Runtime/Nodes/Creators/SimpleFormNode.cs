// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    SimpleFormNode.cs
//  Purpose: Analytic primitive shapes (cone, dome, pyramid, plateau, valley,
//           gradient) placed in world space. The building block for hand-authored
//           landmarks that noise alone cannot place deliberately.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    public enum SimpleFormType
    {
        Cone = 0,
        Dome = 1,
        Pyramid = 2,
        Plateau = 3,
        Valley = 4,
        GradientX = 5,
        GradientZ = 6,
        Radial = 7
    }

    [System.Serializable]
    public class SimpleFormNode : CreatorNode
    {
        public override string NodeName { get { return "Simple Form"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Analytic cone, dome, pyramid, plateau, valley, or gradient."; } }

        public SimpleFormType form = SimpleFormType.Dome;

        /// <summary>Centre in world metres.</summary>
        public Vector2 center = Vector2.zero;

        /// <summary>Radius in world metres.</summary>
        public float radius = 500f;

        public float height = 1f;

        /// <summary>Edge softness. 0 is a hard cut, 1 fades across the whole radius.</summary>
        public float falloff = 0.5f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            float safeRadius = Mathf.Max(0.0001f, radius);
            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float dx = wx - center.x;
                    float dz = wz - center.y;

                    float v;
                    switch (form)
                    {
                        case SimpleFormType.GradientX:
                            v = InterpolationMath.Clamp01((dx / safeRadius) * 0.5f + 0.5f);
                            break;

                        case SimpleFormType.GradientZ:
                            v = InterpolationMath.Clamp01((dz / safeRadius) * 0.5f + 0.5f);
                            break;

                        case SimpleFormType.Pyramid:
                        {
                            // Chebyshev distance gives square contours, hence a pyramid.
                            float cheb = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) / safeRadius;
                            v = InterpolationMath.Clamp01(1f - cheb);
                            break;
                        }

                        default:
                        {
                            float dist = Mathf.Sqrt(dx * dx + dz * dz) / safeRadius;
                            float t = InterpolationMath.Clamp01(1f - dist);

                            if (form == SimpleFormType.Cone) v = t;
                            else if (form == SimpleFormType.Dome) v = Mathf.Sqrt(Mathf.Max(0f, 1f - dist * dist));
                            else if (form == SimpleFormType.Radial) v = dist > 1f ? 1f : dist;
                            else if (form == SimpleFormType.Valley) v = 1f - t;
                            else v = t;    // Plateau, shaped by the falloff below
                            break;
                        }
                    }

                    // Falloff only shapes the radial forms; gradients are already linear.
                    if (form == SimpleFormType.Plateau)
                    {
                        float edge = Mathf.Clamp01(falloff);
                        v = edge < 1e-5f
                            ? (v > 0f ? 1f : 0f)
                            : InterpolationMath.SmoothStep(0f, edge, v);
                    }
                    else if (form == SimpleFormType.Cone || form == SimpleFormType.Dome)
                    {
                        if (falloff > 1e-5f) v = Mathf.Pow(v, 1f / Mathf.Max(0.0001f, falloff));
                    }

                    m.arr[rowStart + x] = v * height;
                }
            }
        }
    }
}
