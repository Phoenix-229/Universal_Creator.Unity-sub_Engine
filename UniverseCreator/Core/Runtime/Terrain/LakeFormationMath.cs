// UC-T08-15: bounded basin-shape models under the existing accepted shoreline; no second water authority.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Saved lake excavation family; zero preserves the existing bed profile in older assets.</summary>
    public enum LakeFormation
    {
        /// <summary>Existing authored bed, shore-distance warp and ruggedness.</summary>
        Legacy = 0,
        /// <summary>Broad downwarping or an elongated fault/rift trough.</summary>
        Tectonic = 1,
        /// <summary>Ice-excavated cirque or trough.</summary>
        GlacialErosion = 2,
        /// <summary>Kettle depression or moraine impoundment.</summary>
        GlacialDeposition = 3,
        /// <summary>Crater/caldera or lava impoundment.</summary>
        Volcanic = 4,
        /// <summary>Abandoned channel or crescent floodplain trough.</summary>
        RiverFloodplain = 5,
        /// <summary>Steep dissolution/collapse bowl.</summary>
        Karst = 6,
        /// <summary>Asymmetric valley upstream of a natural blockage.</summary>
        LandslideDammed = 7,
        /// <summary>Shallow lagoon behind a coastal barrier.</summary>
        CoastalBarrier = 8,
        /// <summary>Broad shallow deflation basin.</summary>
        WindErosion = 9,
        /// <summary>Impact bowl with a submerged central uplift.</summary>
        MeteoriteImpact = 10,
        /// <summary>Small irregular shallow impoundment.</summary>
        BiologicalDamming = 11
    }

    // Tables are built once, then read only. Each body captures metre-space axes once per rebuild.
    // Formation families shape the accepted basin; they do not infer rock type or move an authored waterline.
    internal static class LakeFormationMath
    {
        private const int Samples = 257;
        private static readonly float[] curves = BuildCurves(); // UC-T08-15: one immutable flat table avoids multidimensional indexing per terrain sample.

        internal readonly struct Prepared
        {
            internal readonly LakeFormation Family;
            internal readonly float Sedimentation;
            // Relative bed relief is a bounded artistic profile parameter, not a geological measurement.
            internal readonly float RoughnessMultiplier;
            private readonly Vector2 centre, axis;
            private readonly float inverseLength, inverseWidth;
            private readonly int curveOffset;

            internal Prepared(LakeFormation family, float sedimentation, Vector2 origin, Vector2 direction, float length, float width)
            {
                Family = family; Sedimentation = family == LakeFormation.Legacy ? 0 : sedimentation; centre = origin; axis = direction;
                curveOffset = (int)family * Samples;
                RoughnessMultiplier = family == LakeFormation.Legacy ? 1 : family == LakeFormation.RiverFloodplain ||
                    family == LakeFormation.CoastalBarrier || family == LakeFormation.BiologicalDamming ? 0.3f :
                    family == LakeFormation.WindErosion ? 0.4f : family == LakeFormation.Tectonic ? 0.65f : 1;
                inverseLength = 1 / Mathf.Max(0.5f, length); inverseWidth = 1 / Mathf.Max(0.5f, width);
            }

            // Depth is a fraction of the existing per-node depth. All families meet the exact same shore at zero.
            internal float Fraction(float shoreFraction, float legacyFraction, float worldX, float worldZ)
            {
                if (Family == LakeFormation.Legacy) return legacyFraction;
                float t = Mathf.Clamp01(shoreFraction), index = t * (Samples - 1);
                int lo = Mathf.Min(Samples - 2, (int)index);
                float curve = Mathf.LerpUnclamped(curves[curveOffset + lo], curves[curveOffset + lo + 1], index - lo);
                float dx = worldX - centre.x, dz = worldZ - centre.y;
                float u = (dx * axis.x + dz * axis.y) * inverseLength;
                float v = (-dx * axis.y + dz * axis.x) * inverseWidth;
                float radius2 = u * u + v * v;
                float feature = 1;
                switch (Family)
                {
                    case LakeFormation.Tectonic: feature = 1 - 0.25f * Mathf.Clamp01(v * v); break;
                    case LakeFormation.GlacialErosion: feature = 1 - 0.35f * Mathf.Clamp01(u * u); break;
                    case LakeFormation.GlacialDeposition: feature = 0.75f + 0.25f * Mathf.Clamp01(1 - radius2); break;
                    case LakeFormation.Volcanic: feature = 0.8f + 0.2f * Mathf.Clamp01(1 - radius2); break;
                    case LakeFormation.RiverFloodplain:
                        // A curved abandoned channel is deeper than the inner bar; the open end shelves toward the mouth.
                        float radial = 1 - Mathf.Clamp01(Mathf.Abs(radius2 - 0.45f) / 0.45f);
                        feature = 0.35f + 0.65f * radial * Mathf.Clamp01(0.75f - u * 0.5f); break;
                    case LakeFormation.Karst: feature = 0.65f + 0.35f * Mathf.Clamp01(1 - radius2); break;
                    case LakeFormation.LandslideDammed: feature = 0.6f + 0.4f * Mathf.Clamp01(0.5f + u * 0.5f); break;
                    case LakeFormation.CoastalBarrier: feature = 0.55f + 0.45f * Mathf.Clamp01(1 - v * v); break;
                    case LakeFormation.WindErosion: feature = 0.8f + 0.2f * Mathf.Clamp01(0.5f + u * 0.5f); break;
                    case LakeFormation.MeteoriteImpact:
                        float uplift = Mathf.Clamp01(1 - radius2 * 5); feature = 1 - 0.4f * uplift * uplift; break;
                    case LakeFormation.BiologicalDamming: feature = 0.65f + 0.35f * Mathf.Clamp01(0.5f + u * v); break;
                }
                // Later sedimentation softens geometric relief; it never alters shore ownership or adds per-sample noise layers.
                if (Sedimentation == 0) return Mathf.Clamp01(curve * feature); // UC-T08-15: unsedimented beds need no settling interpolation.
                feature = Mathf.Lerp(feature, 1, Sedimentation);
                float settled = t * t * (3 - 2 * t);
                return Mathf.Clamp01(Mathf.Lerp(curve, settled, Sedimentation * 0.65f) * feature);
            }
        }

        /// <summary>Captures an immutable profile from a finite accepted world-metre ring and saved settings.</summary>
        /// <exception cref="ArgumentException">Unsupported family, nonfinite sedimentation/points or fewer than three points.</exception>
        internal static Prepared Prepare(LakeFormation family, float sedimentation, IList<Vector3> ring)
        {
            if (family < LakeFormation.Legacy || family > LakeFormation.BiologicalDamming ||
                !Finite(sedimentation) || sedimentation < 0 || sedimentation > 1 || ring == null || ring.Count < 3)
                throw new ArgumentException("Lake formation requires a supported family, sedimentation 0..1 and an accepted finite ring.");
            double cx = 0, cz = 0;
            foreach (var p in ring)
            {
                if (!Finite(p.x) || !Finite(p.z)) throw new ArgumentException("Lake formation ring positions must be finite metres.");
                cx += p.x; cz += p.z;
            }
            cx /= ring.Count; cz /= ring.Count;
            double xx = 0, zz = 0, xz = 0;
            foreach (var p in ring) { double x = p.x - cx, z = p.z - cz; xx += x * x; zz += z * z; xz += x * z; }
            float angle = (float)(0.5 * Math.Atan2(2 * xz, xx - zz));
            var axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float length = 0, width = 0;
            foreach (var p in ring)
            {
                float x = (float)(p.x - cx), z = (float)(p.z - cz);
                length = Mathf.Max(length, Mathf.Abs(x * axis.x + z * axis.y));
                width = Mathf.Max(width, Mathf.Abs(-x * axis.y + z * axis.x));
            }
            return new Prepared(family, sedimentation, new Vector2((float)cx, (float)cz), axis, length, width);
        }

        /// <summary>Selects a stable terrain-weighted family without global random state or inferred geology.</summary>
        /// <remarks>Relief is measured metres, area measured square metres, elongation is at least one.
        /// Coastal eligibility must be established from connected ocean; absent evidence excludes that family.
        /// Temperature is degrees Celsius and moisture is an available-water fraction 0..1, not rainfall.
        /// Cold/dry evidence is optional; NaN means unknown and supplies no climate preference.</remarks>
        internal static LakeFormation Select(int seed, Vector3 centre, float relief, float area, float elongation,
            bool coastal, float temperature = float.NaN, float moisture = float.NaN)
        {
            if (!Finite(centre.x) || !Finite(centre.z) || !Finite(relief) || !Finite(area) || !Finite(elongation) ||
                relief < 0 || area <= 0 || elongation < 1) throw new ArgumentException("Lake selection requires finite measured terrain geometry.");
            float Weight(int family)
            {
                bool small = area < 10000, gentle = relief < 8;
                switch ((LakeFormation)family)
                {
                    case LakeFormation.Tectonic: return elongation > 2 ? 3 : 1;
                    case LakeFormation.GlacialErosion: return relief > 12 ? (Finite(temperature) && temperature < 5 ? 4 : 1) : 0.2f;
                    case LakeFormation.GlacialDeposition: return small ? 1 : 0.3f;
                    case LakeFormation.Volcanic: return elongation < 1.5f && relief > 8 ? 1 : 0.1f;
                    case LakeFormation.RiverFloodplain: return gentle && elongation > 1.5f ? 3 : 0.1f;
                    case LakeFormation.Karst: return small && relief > 8 ? 1 : 0.1f;
                    case LakeFormation.LandslideDammed: return elongation > 2 && relief > 8 ? 3 : 0.1f;
                    case LakeFormation.CoastalBarrier: return coastal && gentle ? 4 : 0;
                    case LakeFormation.WindErosion: return gentle ? (Finite(moisture) && moisture < 0.25f ? 3 : 0.5f) : 0.1f;
                    case LakeFormation.MeteoriteImpact: return elongation < 1.5f ? 0.2f : 0.05f;
                    default: return small && gentle ? 2 : 0.05f;
                }
            }
            uint hash = unchecked((uint)seed);
            hash = unchecked((hash ^ (uint)centre.x.GetHashCode()) * 16777619U);
            hash = unchecked((hash ^ (uint)centre.z.GetHashCode()) * 16777619U);
            hash ^= hash >> 16; hash = unchecked(hash * 0x7feb352dU); hash ^= hash >> 15;
            float total = 0; for (int f = 1; f <= 11; f++) total += Weight(f);
            double choice = hash / 4294967296.0 * total;
            for (int f = 1; f <= 11; f++) { choice -= Weight(f); if (choice < 0) return (LakeFormation)f; }
            return LakeFormation.Tectonic;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float[] BuildCurves()
        {
            var result = new float[12 * Samples];
            // Exponents are shaping parameters, not claimed geological measurements.
            float[] exponent = { 1, 1.65f, 0.75f, 1.3f, 0.55f, 1.8f, 0.35f, 0.9f, 1.5f, 2.2f, 0.65f, 1.95f };
            for (int f = 1; f <= 11; f++) for (int i = 0; i < Samples; i++)
            {
                float t = i / (float)(Samples - 1);
                result[f * Samples + i] = Mathf.Pow(t * t * (3 - 2 * t), exponent[f]);
            }
            return result;
        }
    }
}
