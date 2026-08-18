// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    WorldShapeNodes.cs
//  Purpose: Planet-scale context sources: continental outline, latitude, wind
//           belts, river sources, and the shared random seed.
//
//  These are the inputs a climate model needs before any of it can be physical.
//  All of them read ABSOLUTE world coordinates, so two chunks solved on separate
//  workers agree about where the equator and the trade winds are.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    /// <summary>
    /// Continental outline: low-frequency noise gated by a radial falloff from the
    /// world centre, so land occupies the middle and the outer band goes to sea.
    /// </summary>
    [System.Serializable]
    public class ContinentalShapeMaskNode : CreatorNode
    {
        public override string NodeName { get { return "Continental Shape Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Landmass outline: low-frequency noise inside a radial falloff."; } }

        public int seed = 777;

        /// <summary>Coastline detail size in world metres.</summary>
        public float size = 6000f;

        /// <summary>Fraction of the world radius that stays fully land.</summary>
        public float landRadius = 0.55f;

        /// <summary>Falloff curve exponent. Higher gives a tighter, steeper coast.</summary>
        public float falloff = 2.5f;

        /// <summary>How far the coastline wanders from the ideal circle, 0..1.</summary>
        public float coastNoise = 0.45f;

        /// <summary>Land above this, sea below. Shifts total land area.</summary>
        public float seaLevel = 0.5f;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            // Radial falloff is measured against the FULL world, not this chunk.
            // Using the chunk would put a coastline around every tile.
            float centreX = tile.fullWorldPos.x + tile.fullWorldSize.x * 0.5f;
            float centreZ = tile.fullWorldPos.z + tile.fullWorldSize.z * 0.5f;
            float halfX = Mathf.Max(1f, tile.fullWorldSize.x * 0.5f);
            float halfZ = Mathf.Max(1f, tile.fullWorldSize.z * 0.5f);

            float safeSize = Mathf.Max(1f, size);
            float safeLand = Mathf.Clamp(landRadius, 0.05f, 0.99f);
            float safeFall = Mathf.Max(0.1f, falloff);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                float nz = (wz - centreZ) / halfZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;
                    float nx = (wx - centreX) / halfX;

                    // Elliptical normalised distance: 0 at centre, 1 at world edge.
                    float dist = Mathf.Sqrt(nx * nx + nz * nz);

                    float shelf = 1f - Mathf.Clamp01((dist - safeLand) / Mathf.Max(1e-4f, 1f - safeLand));
                    shelf = Mathf.Pow(shelf, safeFall);

                    float n = noise.Fractal(wx, wz, safeSize, 5, 0.5f);

                    // Noise perturbs the falloff rather than being added on top, so
                    // it makes the coast irregular without also raising the sea bed.
                    float landness = shelf * (1f - coastNoise) + shelf * n * 2f * coastNoise;

                    m.arr[rowStart + x] = Mathf.Clamp01(
                        InterpolationMath.SmoothStep(Mathf.Clamp01(landness - (seaLevel - 0.5f))));
                }
            }
        }
    }

    /// <summary>
    /// World Z mapped to latitude and normalised to 0..1. Every climate node reads
    /// this rather than deriving its own, so temperature, wind and snowline all
    /// agree about where the poles are.
    /// </summary>
    [System.Serializable]
    public class LatitudeGradientNode : CreatorNode
    {
        public override string NodeName { get { return "Latitude Gradient"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "World Z mapped to -90..90 latitude, normalised 0..1."; } }

        /// <summary>Latitude at the world's north edge. South edge is its negation.</summary>
        public float range = 60f;

        /// <summary>Shifts the equator north or south, in degrees.</summary>
        public float equatorOffset;

        /// <summary>Output 1 at the equator and 0 at the poles, rather than raw latitude.</summary>
        public bool absolute = true;

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppZ = m.PixelSize.y;
            float originZ = m.worldPos.z;

            float worldMinZ = tile.fullWorldPos.z;
            float worldSizeZ = Mathf.Max(1f, tile.fullWorldSize.z);
            float safeRange = Mathf.Max(1f, range);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;

                // -1 at the south edge, +1 at the north edge.
                float t = (wz - worldMinZ) / worldSizeZ * 2f - 1f;
                float latitude = t * safeRange + equatorOffset;

                float value = absolute
                    ? 1f - Mathf.Clamp01(Mathf.Abs(latitude) / 90f)
                    : Mathf.Clamp01((latitude + 90f) / 180f);

                int rowStart = z * resX;
                for (int x = 0; x < resX; x++) m.arr[rowStart + x] = value;
            }
        }
    }

    /// <summary>
    /// Prevailing wind from the three-cell circulation model: polar easterlies,
    /// mid-latitude westerlies, tropical trade winds. Port 0 is the eastward
    /// component and port 1 the northward, both centred on 0.5.
    /// </summary>
    [System.Serializable]
    public class PrevailingWindNode : Generator
    {
        public override string NodeName { get { return "Prevailing Wind"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Wind vector field from the three-cell circulation model."; } }

        private static readonly Port[] Outs =
        {
            new Port("Wind X", PortType.Matrix, PortDirection.Output, true),
            new Port("Wind Z", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Number of circulation cells per hemisphere. 3 is Earth.</summary>
        public int belts = 3;

        public float range = 60f;

        /// <summary>Wind speed at the belt cores, metres per second.</summary>
        public float strength = 12f;

        /// <summary>Turbulence added to the idealised belts, 0..1.</summary>
        public float variance = 0.25f;

        public int seed = 3131;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix m = tile.CreateMatrix();
            Noise noise = new Noise(seed).WithSubSeed(outputIndex);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float worldMinZ = tile.fullWorldPos.z;
            float worldSizeZ = Mathf.Max(1f, tile.fullWorldSize.z);
            float safeRange = Mathf.Max(1f, range);
            int safeBelts = Mathf.Clamp(belts, 1, 6);
            float safeStrength = Mathf.Max(0.01f, strength);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                float latitude = ((wz - worldMinZ) / worldSizeZ * 2f - 1f) * safeRange;

                // Each cell reverses direction: sin of latitude scaled by belt count
                // alternates sign at every cell boundary, which is the physical
                // easterly / westerly / easterly banding.
                float phase = latitude / 90f * safeBelts * Mathf.PI;
                float eastward = Mathf.Sin(phase);

                // Coriolis: meridional flow deflects with the sine of latitude.
                float northward = Mathf.Cos(phase) * Mathf.Sin(latitude * Mathf.Deg2Rad) * 0.5f;

                float baseValue = outputIndex == 0 ? eastward : northward;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float v = baseValue;
                    if (variance > 0.001f)
                    {
                        float turb = noise.Fractal(wx, wz, 3000f, 4) - 0.5f;
                        v += turb * 2f * variance;
                    }

                    // Normalised to 0..1 around a 0.5 rest point; consumers recentre.
                    m.arr[rowStart + x] = Mathf.Clamp01(0.5f + v * 0.5f);
                }
            }

            return m;
        }

        /// <summary>Recovers a signed wind component from a stored channel value.</summary>
        public float ToSigned(float stored) { return (stored - 0.5f) * 2f * Mathf.Max(0.01f, strength); }
    }

    /// <summary>
    /// Places river headwaters. Blue-noise points weighted by elevation and
    /// precipitation, because rivers begin where it rains on high ground.
    /// Emits a sparse mask; River Network Trace turns the points into channels.
    /// </summary>
    [System.Serializable]
    public class RiverSourceSeedNode : Generator
    {
        public override string NodeName { get { return "River Source Seed"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Headwater points, Poisson-distributed and weighted by elevation and rainfall."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Precipitation", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        public int seed = 5150;

        /// <summary>Sources per square kilometre before weighting.</summary>
        public float density = 4f;

        /// <summary>Minimum elevation a source can appear at, 0..1.</summary>
        public float minElevation = 0.55f;

        /// <summary>Minimum spacing between sources, in world metres.</summary>
        public float minSpacing = 250f;

        /// <summary>How strongly rainfall biases placement, 0..1.</summary>
        public float rainWeight = 0.7f;

        /// <summary>Radius each source paints into the mask, in world metres.</summary>
        public float markRadius = 12f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            Matrix rain = GetInputMatrix(tile, 1, stop);
            Matrix mask = tile.CreateMatrix();

            if (height == null) return mask;
            if (stop != null && stop.StopRequested) return null;

            PoissonSettings settings = PoissonSettings.Default;
            settings.minSpacing = Mathf.Max(
                Mathf.Max(1f, minSpacing), PoissonDisc.SpacingForDensity(Mathf.Max(0.01f, density)));
            settings.seed = seed;
            settings.maxPoints = 4096;

            System.Func<bool> isCancelled = null;
            if (stop != null) isCancelled = delegate { return stop.StopRequested; };

            float safeMin = Mathf.Clamp01(minElevation);
            float invSpan = 1f / Mathf.Max(1e-4f, 1f - safeMin);

            // The weight function is evaluated per dart, so it samples rather than
            // pre-building a probability field over the whole tile.
            System.Func<float, float, float> weight = delegate (float wx, float wz)
            {
                Vector2 p = height.WorldToPixel(wx, wz);
                float h = height.GetInterpolated(p.x, p.y);
                if (h < safeMin) return 0f;

                float w = (h - safeMin) * invSpan;

                if (rain != null && rainWeight > 0.001f)
                {
                    Vector2 rp = rain.WorldToPixel(wx, wz);
                    float r = rain.GetInterpolated(rp.x, rp.y);
                    w = w * (1f - rainWeight) + w * r * rainWeight;
                }

                return Mathf.Clamp01(w);
            };

            List<ScatterCandidate> sources = PoissonDisc.Solve(
                settings,
                tile.worldPos.x, tile.worldPos.z,
                tile.worldSize.x, tile.worldSize.z,
                weight, isCancelled);

            if (stop != null && stop.StopRequested) return null;

            PaintSources(mask, sources);
            return mask;
        }

        private void PaintSources(Matrix mask, List<ScatterCandidate> sources)
        {
            if (sources == null || sources.Count == 0) return;

            Vector2 ps = mask.PixelSize;
            if (ps.x <= 1e-9f || ps.y <= 1e-9f) return;

            float radiusPxX = Mathf.Max(1f, markRadius / ps.x);
            float radiusPxZ = Mathf.Max(1f, markRadius / ps.y);
            int resX = mask.rect.size.x;
            int resZ = mask.rect.size.z;

            for (int i = 0; i < sources.Count; i++)
            {
                Vector2 p = mask.WorldToPixel(sources[i].worldX, sources[i].worldZ);

                int x0 = Mathf.Max(0, Mathf.FloorToInt(p.x - radiusPxX));
                int x1 = Mathf.Min(resX - 1, Mathf.CeilToInt(p.x + radiusPxX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt(p.y - radiusPxZ));
                int z1 = Mathf.Min(resZ - 1, Mathf.CeilToInt(p.y + radiusPxZ));

                for (int z = z0; z <= z1; z++)
                {
                    float dz = (z - p.y) / radiusPxZ;
                    int row = z * resX;

                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = (x - p.x) / radiusPxX;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > 1f) continue;

                        float v = 1f - InterpolationMath.SmoothStep(d);
                        if (v > mask.arr[row + x]) mask.arr[row + x] = v;
                    }
                }
            }
        }
    }

    /// <summary>
    /// One integer other nodes can share, so a whole world re-rolls from a single
    /// field instead of the user hunting every seed box in the graph.
    /// </summary>
    [System.Serializable]
    public class RandomSeedNode : Generator
    {
        public override string NodeName { get { return "Random Seed"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "A single seed value downstream stochastic nodes can share."; } }

        private static readonly Port[] Outs =
        {
            new Port("Seed", PortType.Scalar, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return Outs; }

        public int seed = 20260810;

        /// <summary>Offsets the emitted value without disturbing the stored seed.</summary>
        public int variant;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            return (float)Noise.Pair(seed, variant);
        }
    }
}
