// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SurfaceForm.cs
//  Purpose: The one pure reader that turns a height Matrix into the local SHAPE of
//           the ground at a pixel - slope, the compass direction it faces downhill,
//           whether it bulges out or scoops in, and the surface normal. Two data
//           nodes need this (the lithology field needs slope to pick a surficial
//           cover; the micro-relief field needs slope, aspect, curvature AND normal
//           to assemble a process context), so the maths lives here ONCE instead of
//           being copied - and copied slightly differently - into each node.
//
//  ALL FOUR COME FROM THE SAME NEIGHBOURS
//  --------------------------------------
//  Every quantity is read from the four axis neighbours of a pixel by central
//  differences, in METRES, using the Matrix's own pixel spacing and world-height
//  scale - so a slope is a real angle, not a heightmap-value ratio. At a tile edge
//  the difference is taken one-sided (the neighbour clamps back onto the centre),
//  which is the same edge rule the existing environment slope reader uses, so the
//  border reports a slightly gentler but never a wild value.
//
//  SIGN CONVENTIONS, FIXED HERE SO DOWNSTREAM CANNOT DISAGREE
//  ---------------------------------------------------------
//   - aspectDeg is the DOWNSLOPE compass bearing: 0 = faces +Z (north), 90 = +X
//     (east), matching EnvironmentSample.aspectDeg. It is left at 0 on a flat where
//     "which way it faces" is undefined, rather than reading noise from rounding.
//   - curvature is -(hxx + hzz): POSITIVE on a convex bulge (a ridge/mound top) and
//     NEGATIVE in a concave hollow, matching EnvironmentSample.curvature, so a node
//     that favours drained convex sites reads the sign it expects.
//   - normal is normalize(-gx, 1, -gz): points up out of the surface, tilting toward
//     the downhill side, the standard heightfield normal.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Static, allocation-free per call, no UnityEngine scene or GPU access (only Mathf,
//  which is pure maths and legal off the main thread), so the node Generate() path
//  may call it on a worker and a self-test may pin it on a hand-built Matrix.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// The local shape of the ground at one pixel, read from a height <see cref="Matrix"/>:
    /// slope angle, downslope compass aspect, convex/concave curvature, and the surface normal.
    /// A plain value struct so a node can pull all four in one call without allocating.
    /// </summary>
    public struct SurfaceForm
    {
        /// <summary>Slope in degrees from horizontal (0 = flat, 90 = vertical).</summary>
        public float slopeDeg;

        /// <summary>Downslope compass bearing in degrees: 0 = +Z (north), 90 = +X (east). 0 on a flat.</summary>
        public float aspectDeg;

        /// <summary>Curvature: positive on a convex bulge (ridge/mound), negative in a concave hollow.</summary>
        public float curvature;

        /// <summary>Unit surface normal, pointing up out of the ground and tilting toward downhill.</summary>
        public Vector3 normal;
    }

    /// <summary>
    /// Pure reader of <see cref="SurfaceForm"/> from a height <see cref="Matrix"/>. The lithology
    /// and micro-relief data nodes both call <see cref="Sample"/>; keeping it here means they read
    /// one definition of slope/aspect/curvature/normal rather than two that could drift apart.
    /// </summary>
    public static class MatrixSurfaceForm
    {
        /// <summary>
        /// The local shape at pixel (<paramref name="lx"/>, <paramref name="lz"/>) - LOCAL array indices
        /// into <paramref name="m"/>, 0-based within its rect - read by edge-clamped central differences in
        /// metres. Returns a flat, up-facing form (slope 0, aspect 0, curvature 0, normal +Y) for a null,
        /// empty or degenerate (single-row/column) matrix, so a caller never has to guard the tiny cases.
        /// </summary>
        public static SurfaceForm Sample(Matrix m, int lx, int lz)
        {
            SurfaceForm f = default;
            f.normal = Vector3.up;

            if (m == null || m.arr == null) return f;
            int w = m.rect.size.x;
            int h = m.rect.size.z;
            if (w < 2 || h < 2) return f;

            // Clamp the neighbour indices at the tile border (one-sided difference there).
            int xm = lx > 0 ? lx - 1 : lx;
            int xp = lx < w - 1 ? lx + 1 : lx;
            int zm = lz > 0 ? lz - 1 : lz;
            int zp = lz < h - 1 ? lz + 1 : lz;
            int cx = lx < 0 ? 0 : (lx > w - 1 ? w - 1 : lx);
            int cz = lz < 0 ? 0 : (lz > h - 1 ? h - 1 : lz);

            float hUnit = m.worldHeight; // a Matrix value of 1.0 is this many metres of elevation

            float[] a = m.arr;
            float hxm = a[cz * w + xm] * hUnit;
            float hxp = a[cz * w + xp] * hUnit;
            float hzm = a[zm * w + cx] * hUnit;
            float hzp = a[zp * w + cx] * hUnit;
            float hc = a[cz * w + cx] * hUnit;

            Vector2 ps = m.PixelSize; // metres between adjacent pixels on X (.x) and Z (.y)
            float dx = ps.x > 1e-6f ? ps.x : 1e-6f;
            float dz = ps.y > 1e-6f ? ps.y : 1e-6f;

            // Gradient (rise over run in metres). (xp - xm) is 2 in the interior, 1 at an edge.
            float runX = (xp - xm) * dx; if (runX < 1e-6f) runX = 1e-6f;
            float runZ = (zp - zm) * dz; if (runZ < 1e-6f) runZ = 1e-6f;
            float gx = (hxp - hxm) / runX;
            float gz = (hzp - hzm) / runZ;

            float g2 = gx * gx + gz * gz;
            f.slopeDeg = Mathf.Atan(Mathf.Sqrt(g2)) * Mathf.Rad2Deg;

            // Downslope facing direction is -(gx, gz); as a compass bearing 0 = +Z, 90 = +X.
            // Undefined on a flat, so report 0 there rather than the arctangent of noise.
            f.aspectDeg = g2 < 1e-12f
                ? 0f
                : (Mathf.Atan2(-gx, -gz) * Mathf.Rad2Deg + 360f) % 360f;

            // Curvature: -(second derivative sum). Convex (local max) reads positive, hollow negative.
            float hxx = (hxp - 2f * hc + hxm) / (dx * dx);
            float hzz = (hzp - 2f * hc + hzm) / (dz * dz);
            f.curvature = -(hxx + hzz);

            f.normal = new Vector3(-gx, 1f, -gz).normalized;
            return f;
        }
    }
}
