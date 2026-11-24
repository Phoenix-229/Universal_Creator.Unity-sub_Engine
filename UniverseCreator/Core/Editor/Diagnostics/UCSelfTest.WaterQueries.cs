// UC-T04-08/09: accepted triangle footprint, local bed/flow and displaced-mesh queries.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestAcceptedWaterQueries()
        {
            var root = new GameObject("UC accepted water query"); root.SetActive(false);
            try
            {
                var volume = root.AddComponent<UCWaterVolume>();
                volume.worldBounds = new Bounds(Vector3.zero, new Vector3(100, 100, 100));
                volume.maxDepth = 100; volume.surface.waveHeight = 0;
                var data = JunctionQuad(-1, 1, 0, 4, false); data.maxFlowSpeed = 4;
                data.triangles = new[] { 0, 1, 2 };
                for (int i = 0; i < 4; i++) { data.uv[i] = new Vector2(0, 0.2f); data.colors[i] = new Color(0.5f, 1, (i + 1) * 0.25f, 1); }
                volume.SetAcceptedSurface(data, Matrix4x4.identity);
                Check(!volume.SampleLocal(new Vector3(-0.8f, -0.1f, 3.8f), 0, out _),
                    "UC-T04-09 an empty triangle/island inside broad bounds stays dry.");
                var centre = (data.vertices[0] + data.vertices[1] + data.vertices[2]) / 3;
                Check(volume.SampleLocal(centre + Vector3.down * 0.1f, 0, out var sample) && sample.submerged &&
                    Mathf.Abs(sample.waterDepth - 0.2f) < 1e-5f && Mathf.Abs(sample.bedY + 0.2f) < 1e-5f && sample.velocity.z > 0,
                    "UC-T04-09 accepted local depth/bed/downstream flow replace body-wide depth and current.");
                Check(!volume.SampleLocal(centre + Vector3.down * 0.3f, 0, out _),
                    "UC-T04-09 rock below a shallow local bed is dry despite a 100-metre body maximum.");
                data.vertices[0] = new Vector3(500, 500, 500); data.uv[1] = Vector2.zero;
                Check(volume.SampleLocal(centre + Vector3.down * 0.1f, 0, out sample) && sample.submerged,
                    "UC-T04-09 query capture isolates later edits to borrowed mesh arrays.");
                bool rejected = false;
                try { volume.SetAcceptedSurface(new WaterBodyData(), Matrix4x4.identity); } catch (ArgumentException) { rejected = true; }
                Check(rejected && volume.SampleLocal(centre, 0, out _),
                    "UC-T04-09 invalid replacement query streams preserve the accepted lookup.");

                var moving = JunctionQuad(-5, 5, -5, 5, false); moving.maxFlowSpeed = 2;
                for (int i = 0; i < 4; i++) { moving.uv[i] = new Vector2(0, 4); moving.colors[i] = new Color(1, 0.5f, 0.5f, 1); }
                var p = WaterSurfaceParams.Default(0); p.waveHeight = 0.5f; p.waveLength = 8;
                p.flow = Vector2.right; p.flowAdvection = 1; p.horizontalDisplacement = true;
                volume.surface = p; volume.SetAcceptedSurface(moving, Matrix4x4.identity);
                const float time = 2;
                Vector3 displacedCentre = Vector3.zero; float expectedY = 0;
                foreach (int i in new[] { 0, 1, 2 })
                {
                    Vector3 v = moving.vertices[i]; Vector2 xz = new Vector2(v.x, v.z);
                    Vector2 offset = UCWaterSurface.HorizontalOffset(xz, time, p);
                    float h = UCWaterSurface.WaveOffset(xz, time, p);
                    displacedCentre += v + new Vector3(offset.x, h, offset.y); expectedY += h;
                }
                displacedCentre /= 3; expectedY /= 3;
                Check(volume.SampleLocal(displacedCentre, time, out sample) && Mathf.Abs(sample.surfaceY - expectedY) < 1e-4f,
                    "UC-T04-09 query height is the displaced triangle plane rather than a different analytic wave at its shifted position.");
                Check(UCWaterSurface.HorizontalOffset(Vector2.zero, time, p, 0) == Vector2.zero && UCWaterSurface.WaveOffset(Vector2.zero, time, p, 0) == 0,
                    "UC-T04-09 zero-weight shores retain both horizontal and vertical contact.");
                for (int i = 0; i < 64; i++)
                {
                    Vector2 xz = new Vector2(i * 0.41f, i * 0.23f); const float e = 0.001f;
                    Vector2 dx = (UCWaterSurface.HorizontalOffset(xz + Vector2.right * e, time, p) - UCWaterSurface.HorizontalOffset(xz - Vector2.right * e, time, p)) / (2 * e);
                    Vector2 dz = (UCWaterSurface.HorizontalOffset(xz + Vector2.up * e, time, p) - UCWaterSurface.HorizontalOffset(xz - Vector2.up * e, time, p)) / (2 * e);
                    Check((1 + dx.x) * (1 + dz.y) - dx.y * dz.x > 0.4f,
                        "UC-T04-09 conservative spectral steepness preserves a positive constant-weight horizontal Jacobian.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
