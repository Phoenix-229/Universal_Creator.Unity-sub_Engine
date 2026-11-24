// UC-T07-11: preserve the actual water/bed crossing before the dry skirt buries it; do not use the final mesh's expanded outer edge.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    internal static class WaterShorelineBuilder
    {
        private const int MaxSegments = 32768;

        internal static Vector3[] Grid(float[] rawDepth, bool[] wet, int width, int height, Vector2 origin, float spacing,
            float waterLevel, Matrix4x4 localToWorld)
        {
            if (rawDepth == null || wet == null || rawDepth.Length != wet.Length || (long)width * height != wet.Length || width < 2 || height < 2 ||
                width > 512 || height > 512 || !RiverChannelSnapshot.Finite(spacing) || spacing <= 0)
                throw new ArgumentException("Accepted shore source exceeds its 512-sample lattice envelope or has invalid metre spacing.");
            var segments = new List<Vector3>(); var crossings = new Vector2[4];
            Matrix4x4 toLocal = localToWorld.inverse;
            Vector3 Local(Vector2 p)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(new Vector3(p.x, 0, p.y)); world.y = waterLevel;
                return toLocal.MultiplyPoint3x4(world);
            }
            void Add(Vector2 a, Vector2 b, Vector2 cellOrigin, int ca, int cb, int cc, int cd)
            {
                if (a.x == b.x && a.y == b.y) return;
                if (segments.Count == MaxSegments * 2) throw new ArgumentException("Accepted shore source exceeds 32,768 contour segments; simplify the physical source or use smaller water regions.");
                Vector2 left = new Vector2(a.y - b.y, b.x - a.x).normalized;
                Vector2 probe = ((a + b) * 0.5f + left * (spacing * 0.001f) - cellOrigin) / spacing;
                double Signed(int i) => wet[i] ? Math.Max(1e-12, rawDepth[i]) : -Math.Max(1e-12, Math.Abs(rawDepth[i]));
                double bottom = Signed(ca) * (1 - probe.x) + Signed(cb) * probe.x;
                double top = Signed(cd) * (1 - probe.x) + Signed(cc) * probe.x;
                bool wetOnLeft = bottom * (1 - probe.y) + top * probe.y > 0;
                bool mirrored = localToWorld.m00 * localToWorld.m22 - localToWorld.m02 * localToWorld.m20 < 0;
                if (wetOnLeft == mirrored) { Vector2 swap = a; a = b; b = swap; }
                segments.Add(Local(a)); segments.Add(Local(b));
            }
            for (int z = 0; z < height - 1; z++) for (int x = 0; x < width - 1; x++)
            {
                int a = z * width + x, b = a + 1, c = a + width + 1, d = a + width, count = 0;
                Vector2 p = origin + new Vector2(x * spacing, z * spacing);
                void Edge(int first, int second, Vector2 start, Vector2 end)
                {
                    if (wet[first] == wet[second]) return;
                    float da = rawDepth[first], db = rawDepth[second];
                    if (!RiverChannelSnapshot.Finite(da) || !RiverChannelSnapshot.Finite(db)) throw new ArgumentException("Accepted shore source contains nonfinite bed depth.");
                    // A true depth sign change locates the water/ground intersection. A polygon/dam/connectivity mask
                    // with no depth sign change has only sampled membership; its midpoint retains that source's half-cell uncertainty.
                    double fraction = (da > 0) != (db > 0) && da != db ? (double)da / ((double)da - db) : 0.5;
                    crossings[count++] = Vector2.Lerp(start, end, Mathf.Clamp01((float)fraction));
                }
                Edge(a, b, p, p + new Vector2(spacing, 0));
                Edge(b, c, p + new Vector2(spacing, 0), p + new Vector2(spacing, spacing));
                Edge(c, d, p + new Vector2(spacing, spacing), p + new Vector2(0, spacing));
                Edge(d, a, p + new Vector2(0, spacing), p);
                if (count == 2) Add(crossings[0], crossings[1], p, a, b, c, d);
                else if (count == 4)
                {
                    // Resolve the sampled saddle using the physical cell-centre depth, never interpolation of category IDs.
                    double centre = ((double)rawDepth[a] + rawDepth[b] + rawDepth[c] + rawDepth[d]) * 0.25;
                    if (wet[a] == (centre > 0)) { Add(crossings[0], crossings[1], p, a, b, c, d); Add(crossings[2], crossings[3], p, a, b, c, d); }
                    else { Add(crossings[0], crossings[3], p, a, b, c, d); Add(crossings[1], crossings[2], p, a, b, c, d); }
                }
            }
            return segments.ToArray();
        }
    }
}
