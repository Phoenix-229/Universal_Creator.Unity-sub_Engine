// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TunnelShellBuilder.cs
//  Purpose: Builds the geometry that closes a terrain hole - wall skirts around the
//           cut edge and a floor underneath it.
//
//  WHY THIS EXISTS
//  ---------------
//  TerrainData.SetHoles removes surface. It does not build anything in its place.
//  A bare hole therefore has two problems:
//
//    1. You see straight through the cut edge into the terrain's underside, which
//       reads as the hill having been sliced open.
//    2. There is no collider where the surface used to be, so anything standing
//       there falls through the world.
//
//  So the hole needs a shell: a skirt that hangs from the cut edge down to the
//  tunnel floor, and a floor the road can sit on. This file generates both.
//
//  NO-GAP GUARANTEE
//  ----------------
//  The terrain holes map is per-quad, so the real cut boundary is quantised to that
//  grid and can land up to one quad outside the requested box. Building the skirt
//  exactly on the requested bounds would therefore leave a hairline crack along
//  most of the perimeter.
//
//  The fix is overlap, not precision: the skirt is pushed outward by `oversize` and
//  its top edge follows the sampled terrain height, so the wall top always sits
//  *under* the surviving surface. Overlapping geometry cannot crack; matched
//  geometry can, because floating point says so.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Which horizontal faces of the cut are left open for the road.</summary>
    [Flags]
    public enum TunnelFaces
    {
        None = 0,
        MinX = 1,
        MaxX = 2,
        MinZ = 4,
        MaxZ = 8,

        /// <summary>Bore running along X: both X ends open, Z sides walled.</summary>
        AlongX = MinX | MaxX,

        /// <summary>Bore running along Z: both Z ends open, X sides walled.</summary>
        AlongZ = MinZ | MaxZ,

        All = MinX | MaxX | MinZ | MaxZ
    }

    [Serializable]
    public struct TunnelShellSettings
    {
        /// <summary>Faces left open so the road can pass through. Others get a wall.</summary>
        public TunnelFaces openFaces;

        /// <summary>World Y of the tunnel floor. Vertical extent of the cut box by default.</summary>
        public float floorY;

        /// <summary>Lay a floor across the footprint so nothing falls through.</summary>
        public bool buildFloor;

        /// <summary>Metres the skirt is pushed outward to tuck under the terrain.</summary>
        public float oversize;

        /// <summary>Samples per wall run. More follows an uneven cut edge more closely.</summary>
        public int segments;

        /// <summary>Extra metres the skirt continues below the floor, hiding any Z-fight.</summary>
        public float skirtBelowFloor;

        /// <summary>Emit both winding orders so the wall is visible from either side.</summary>
        public bool doubleSided;

        /// <summary>World metres per UV unit, so tiling materials keep a constant scale.</summary>
        public float uvScale;

        public static TunnelShellSettings Default(Bounds b)
        {
            TunnelShellSettings s = new TunnelShellSettings();
            s.openFaces = b.size.x >= b.size.z ? TunnelFaces.AlongX : TunnelFaces.AlongZ;
            s.floorY = b.min.y;
            s.buildFloor = true;
            s.oversize = 0.5f;
            s.segments = 16;
            s.skirtBelowFloor = 1f;
            s.doubleSided = true;
            s.uvScale = 0.25f;
            return s;
        }
    }

    public static class TunnelShellBuilder
    {
        /// <summary>
        /// Fills <paramref name="target"/> with the shell for one carve volume.
        /// <paramref name="sampleHeight"/> returns world terrain height at (x, z) and
        /// is called on the main thread, so it may touch Unity API.
        /// </summary>
        public static void Build(Mesh target, Bounds bounds, TunnelShellSettings s,
                                 Func<float, float, float> sampleHeight)
        {
            if (target == null) return;

            target.Clear();

            int segments = Mathf.Clamp(s.segments, 1, 256);
            float oversize = Mathf.Max(0f, s.oversize);

            // The skirt footprint is the cut box grown outward, so the wall tops sit
            // beneath surviving terrain instead of level with the cut edge.
            float xMin = bounds.min.x - oversize;
            float xMax = bounds.max.x + oversize;
            float zMin = bounds.min.z - oversize;
            float zMax = bounds.max.z + oversize;

            float floorY = s.floorY;
            float skirtBottom = floorY - Mathf.Max(0f, s.skirtBelowFloor);
            float uvScale = s.uvScale > 0f ? s.uvScale : 0.25f;

            List<Vector3> verts = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> tris = new List<int>();

            // Four walls. A face named in openFaces is the road's way in, so it is
            // skipped - walling it would brick up the tunnel mouth.
            if ((s.openFaces & TunnelFaces.MinZ) == 0)
                AddWall(verts, normals, uvs, tris, s,
                        new Vector2(xMin, zMin), new Vector2(xMax, zMin),
                        Vector3.back, segments, skirtBottom, uvScale, sampleHeight);

            if ((s.openFaces & TunnelFaces.MaxZ) == 0)
                AddWall(verts, normals, uvs, tris, s,
                        new Vector2(xMax, zMax), new Vector2(xMin, zMax),
                        Vector3.forward, segments, skirtBottom, uvScale, sampleHeight);

            if ((s.openFaces & TunnelFaces.MinX) == 0)
                AddWall(verts, normals, uvs, tris, s,
                        new Vector2(xMin, zMax), new Vector2(xMin, zMin),
                        Vector3.left, segments, skirtBottom, uvScale, sampleHeight);

            if ((s.openFaces & TunnelFaces.MaxX) == 0)
                AddWall(verts, normals, uvs, tris, s,
                        new Vector2(xMax, zMin), new Vector2(xMax, zMax),
                        Vector3.right, segments, skirtBottom, uvScale, sampleHeight);

            if (s.buildFloor)
                AddFloor(verts, normals, uvs, tris, s,
                         xMin, xMax, zMin, zMax, floorY, uvScale);

            // Vertex counts stay in the low thousands, but a 16-bit buffer caps at
            // 65535 and a high segment count on four walls can pass that.
            target.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            target.SetVertices(verts);
            target.SetNormals(normals);
            target.SetUVs(0, uvs);
            target.SetTriangles(tris, 0);
            target.RecalculateBounds();
        }

        // One wall run from `from` to `to` in the XZ plane. The top edge follows the
        // terrain surface so the seal holds over uneven ground; the bottom is flat.
        private static void AddWall(List<Vector3> verts, List<Vector3> normals,
                                    List<Vector2> uvs, List<int> tris,
                                    TunnelShellSettings s,
                                    Vector2 from, Vector2 to, Vector3 normal,
                                    int segments, float bottomY, float uvScale,
                                    Func<float, float, float> sampleHeight)
        {
            float runLength = Vector2.Distance(from, to);
            if (runLength < 0.001f) return;

            int baseIndex = verts.Count;

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                Vector2 p = Vector2.Lerp(from, to, t);

                float topY = sampleHeight != null ? sampleHeight(p.x, p.y) : bottomY;

                // A cut that reaches above the surface would otherwise produce an
                // inverted quad; clamping keeps the wall degenerate-but-valid.
                if (topY < bottomY) topY = bottomY;

                float u = t * runLength * uvScale;

                verts.Add(new Vector3(p.x, topY, p.y));
                verts.Add(new Vector3(p.x, bottomY, p.y));

                normals.Add(normal);
                normals.Add(normal);

                uvs.Add(new Vector2(u, topY * uvScale));
                uvs.Add(new Vector2(u, bottomY * uvScale));
            }

            for (int i = 0; i < segments; i++)
            {
                int a = baseIndex + i * 2;      // top of this column
                int b = a + 1;                  // bottom of this column
                int c = a + 2;                  // top of next column
                int d = a + 3;                  // bottom of next column

                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(c); tris.Add(b); tris.Add(d);

                if (s.doubleSided)
                {
                    tris.Add(c); tris.Add(b); tris.Add(a);
                    tris.Add(d); tris.Add(b); tris.Add(c);
                }
            }
        }

        // A single upward-facing quad. This is the collider the road and anything on
        // it actually rests on, so it is never optional when buildFloor is set.
        private static void AddFloor(List<Vector3> verts, List<Vector3> normals,
                                     List<Vector2> uvs, List<int> tris,
                                     TunnelShellSettings s,
                                     float xMin, float xMax, float zMin, float zMax,
                                     float y, float uvScale)
        {
            int baseIndex = verts.Count;

            verts.Add(new Vector3(xMin, y, zMin));
            verts.Add(new Vector3(xMax, y, zMin));
            verts.Add(new Vector3(xMax, y, zMax));
            verts.Add(new Vector3(xMin, y, zMax));

            for (int i = 0; i < 4; i++) normals.Add(Vector3.up);

            uvs.Add(new Vector2(xMin * uvScale, zMin * uvScale));
            uvs.Add(new Vector2(xMax * uvScale, zMin * uvScale));
            uvs.Add(new Vector2(xMax * uvScale, zMax * uvScale));
            uvs.Add(new Vector2(xMin * uvScale, zMax * uvScale));

            tris.Add(baseIndex); tris.Add(baseIndex + 3); tris.Add(baseIndex + 1);
            tris.Add(baseIndex + 1); tris.Add(baseIndex + 3); tris.Add(baseIndex + 2);

            if (s.doubleSided)
            {
                tris.Add(baseIndex + 1); tris.Add(baseIndex + 3); tris.Add(baseIndex);
                tris.Add(baseIndex + 2); tris.Add(baseIndex + 3); tris.Add(baseIndex + 1);
            }
        }
    }
}
