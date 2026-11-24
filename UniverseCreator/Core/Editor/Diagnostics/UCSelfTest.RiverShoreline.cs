// UC-T04-06: physical bank contact and non-overlapping sections, not visual or device acceptance.
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestRiverShorelineContact()
        {
            var course = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(0, 0, 10), new Vector3(0, 0, 20) };
            var bed = new float[3]; var depth = new[] { 1f, 1f, 1f }; var width = new[] { 0.5f, 0.5f, 0.5f };
            bool Banks(float x, float z, out float y) { y = Mathf.Abs(x) * (x < 0 ? 2 : 0.5f); return true; }
            bool fitted = RiverCrossSectionFit.TryFitShoreline(course, bed, depth, width, Banks, out var fit, out var diagnostic);
            Check(fitted && fit.Course.Count == 3 && Mathf.Abs(fit.Left[1] - 2) < 0.001f && Mathf.Abs(fit.Right[1] - 0.5f) < 0.001f && width[1] == 0.5f,
                "UC-T04-06 independent banks both reach actual ground without rewriting authored excavation width. " + diagnostic);

            var bend = new List<Vector3>(); var bendBed = new float[7]; var bendDepth = new float[7]; var bendWidth = new float[7];
            for (int i = 0; i < 7; i++)
            {
                float angle = i * Mathf.PI / 12;
                bend.Add(new Vector3(20 * Mathf.Cos(angle), 0, 20 * Mathf.Sin(angle))); bendDepth[i] = 1; bendWidth[i] = 2;
            }
            bool ArcBanks(float x, float z, out float y) { y = Mathf.Abs(Mathf.Sqrt(x * x + z * z) - 20); return true; }
            fitted = RiverCrossSectionFit.TryFitShoreline(bend, bendBed, bendDepth, bendWidth, ArcBanks, out fit, out diagnostic);
            bool contact = fitted && fit.Course.Count <= RiverCrossSectionFit.MaxStations;
            if (fitted)
            {
                for (int i = 0; i < fit.Course.Count; i++)
                {
                    Vector2 normal = RiverCrossSectionFit.Normal(fit.Course, i);
                    foreach (float lateral in new[] { -fit.Left[i], fit.Right[i] })
                    {
                        Vector3 p = fit.Course[i] + new Vector3(normal.x * lateral, 0, normal.y * lateral);
                        ArcBanks(p.x, p.z, out float ground); contact &= Mathf.Abs(ground - 1) <= 0.01f;
                    }
                }
                contact &= fit.Course[0] == bend[0] && fit.Course[fit.Course.Count - 1] == bend[bend.Count - 1];
            }
            Check(contact, "UC-T04-06 a quarter-circle bend subdivides within budget, contacts both banks and preserves authored endpoints. " + diagnostic);

            bool Missing(float x, float z, out float y) { y = Mathf.Abs(x); return z < 4 || z > 6; }
            Check(!RiverCrossSectionFit.TryFitShoreline(course, bed, depth, width, Missing, out _, out diagnostic) && diagnostic.Length > 0,
                "UC-T04-06 missing ground between sections cannot be bridged by a floating river plane.");
            bool Flat(float x, float z, out float y) { y = 0; return true; }
            Check(!RiverCrossSectionFit.TryFitShoreline(course, bed, depth, width, Flat, out _, out diagnostic) && diagnostic.Length > 0,
                "UC-T04-06 a strip without any bank contact is refused; a junction needs its receiving surface contract.");
            var reverse = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 5), Vector3.zero };
            Check(!RiverCrossSectionFit.TryFitShoreline(reverse, bed, depth, width, Banks, out _, out diagnostic),
                "UC-T04-06 a reversing route cannot produce stacked or folded water sections.");
        }
    }
}
