// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    ScatterPipelineNodes.cs
//  Purpose: The scatter pipeline: build a density field, cut it back with real
//           constraints, clump it, then emit instances from it.
//
//  The six Module 1 scatter nodes each solve placement from Height + one Mask.
//  That works but gives the user one multiplier for everything. These eight split
//  the decision into stages a designer can inspect one at a time:
//
//     BiomeDensityTable -> SlopeAltitudeExclusion -> ExclusionMask
//         -> ClusterGroveBias -> PoissonDiscScatter (emits)
//
//  Every stage in that chain is a plain Matrix, so it previews in the graph like
//  any other mask, and any stage can be skipped by not wiring it.
//
//  Wind Sway and Ground Cover Tint produce MAPS rather than per-instance records,
//  because that is how Unity actually drives detail rendering - waving grass reads
//  terrain-wide parameters and the shader samples a tint map.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>
    /// Base planting density per biome. The curve is indexed by normalised biome
    /// id, which keeps it one inspector control instead of a list that grows every
    /// time the classifier gains a class.
    /// </summary>
    [System.Serializable]
    public class BiomeDensityTableNode : Generator, IPreparedGraphInputHash
    {
        public override string NodeName { get { return "Biome Density Table"; } }
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }
        public override string Tooltip { get { return "Turns a biome id map into a planting density field."; } }

        private static readonly Port[] Ins =
        {
            new Port("Biome Id", PortType.Matrix, PortDirection.Input),
            new Port("Moisture", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        /// <summary>Density multiplier against normalised biome id, 0..1 on both axes.</summary>
        public AnimationCurve densityByBiome = AnimationCurve.Linear(0f, 0.2f, 1f, 1f);

        /// <summary>Scales the whole field. 1 leaves the curve as authored.</summary>
        public float baseDensity = 1f;

        /// <summary>How much moisture raises density where it is wired, 0..1.</summary>
        public float moistureInfluence = 0.5f;

        /// <summary>Breaks up the hard edges between biome classes.</summary>
        public float variation = 0.25f;

        public int seed = 8123;

        // AnimationCurve.Evaluate is main-thread only, so it is baked here and the
        // worker reads the table. Same pattern as Slope Remap Curve.
        [System.NonSerialized] private float[] lut;

        /// <summary>UC-T01-07: fingerprints the prepared biome-density lookup used by workers.</summary>
        /// <returns>Deterministic fingerprint; call after main-thread preparation.</returns>
        public ulong GetPreparedInputHash() { return PreparedRasterInputs.Hash(256, 1, lut); }

        public override void PrepareMainThread()
        {
            base.PrepareMainThread();

            const int Steps = 256;
            float[] table = new float[Steps];

            if (densityByBiome != null && densityByBiome.length > 0)
            {
                for (int i = 0; i < Steps; i++)
                    table[i] = densityByBiome.Evaluate(i / (float)(Steps - 1));
            }
            else
            {
                for (int i = 0; i < Steps; i++) table[i] = 1f;
            }

            lut = table;
        }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix biome = GetInputMatrix(tile, 0, stop);
            Matrix moisture = GetInputMatrix(tile, 1, stop);

            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            float[] table = lut;
            int steps = table != null ? table.Length : 0;

            Noise noise = new Noise(seed);

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = dst.PixelSize.x;
            float mppZ = dst.PixelSize.y;
            float originX = dst.worldPos.x;
            float originZ = dst.worldPos.z;

            float vary = Mathf.Clamp01(variation);
            float moist = Mathf.Clamp01(moistureInfluence);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    float id = biome != null ? Mathf.Clamp01(biome.arr[i]) : 0.5f;

                    float d = baseDensity;
                    if (steps > 0)
                        d *= table[Mathf.Clamp(Mathf.RoundToInt(id * (steps - 1)), 0, steps - 1)];

                    if (moisture != null && moist > 0.001f)
                        d *= 1f - moist + Mathf.Clamp01(moisture.arr[i]) * moist * 2f;

                    if (vary > 0.001f)
                    {
                        float wx = originX + x * mppX;
                        float n = noise.Fractal(wx, wz, 220f, 3);
                        d *= 1f - vary + n * vary * 2f;
                    }

                    dst.arr[i] = Mathf.Clamp01(d);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Zeroes density outside the bands a species can actually live in. Separate
    /// from the emitter's own height/slope range so the exclusion is visible as a
    /// field and can be shared by several emitters.
    /// </summary>
    [System.Serializable]
    public class SlopeAltitudeExclusionNode : Generator
    {
        public override string NodeName { get { return "Slope Altitude Exclusion"; } }
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }
        public override string Tooltip { get { return "Cuts density outside the survivable slope and altitude bands."; } }

        private static readonly Port[] Ins =
        {
            new Port("Density", PortType.Matrix, PortDirection.Input),
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        public float minAltitude = 0f;
        public float maxAltitude = 1f;

        /// <summary>Altitude falloff width, 0..1. Avoids a hard contour edge.</summary>
        public float altitudeFalloff = 0.03f;

        public float minSlope = 0f;
        public float maxSlope = 35f;

        /// <summary>Slope falloff width in degrees.</summary>
        public float slopeFalloff = 6f;

        /// <summary>Keeps this fraction of density outside the bands rather than zero.</summary>
        public float residual;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix density = GetInputMatrix(tile, 0, stop);
            Matrix height = GetInputMatrix(tile, 1, stop);

            if (height == null) return density != null ? new Matrix(density) : tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = tile.CreateMatrix();
            Matrix slopes = MatrixOperations.SlopeAngles(height);
            if (stop != null && stop.StopRequested) return null;

            float aFall = Mathf.Max(1e-4f, altitudeFalloff);
            float sFall = Mathf.Max(1e-3f, slopeFalloff);
            float keep = Mathf.Clamp01(residual);

            for (int i = 0; i < dst.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float d = density != null ? density.arr[i] : 1f;
                if (d <= 0f) { dst.arr[i] = 0f; continue; }

                float h = height.arr[i];
                float angle = slopes.arr[i];

                // Two-sided smooth band: 1 well inside, 0 well outside, with the
                // falloff spread over the given width on each edge.
                float aGate = Band(h, minAltitude, maxAltitude, aFall);
                float sGate = Band(angle, minSlope, maxSlope, sFall);

                float gate = aGate * sGate;
                dst.arr[i] = Mathf.Clamp01(d * (keep + (1f - keep) * gate));
            }

            return dst;
        }

        private static float Band(float value, float min, float max, float falloff)
        {
            if (max < min) { float t = min; min = max; max = t; }

            float lower = InterpolationMath.SmoothStep(Mathf.Clamp01((value - min) / falloff));
            float upper = InterpolationMath.SmoothStep(Mathf.Clamp01((max - value) / falloff));
            return lower * upper;
        }
    }

    /// <summary>
    /// Multiplies density down inside an exclusion mask - roads, building plots,
    /// clearings. Inverted rather than additive so it composes with whatever
    /// produced the mask.
    /// </summary>
    [System.Serializable]
    public class ExclusionMaskNode : Generator
    {
        public override string NodeName { get { return "Exclusion Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }
        public override string Tooltip { get { return "Removes density inside a mask, with a soft margin."; } }

        private static readonly Port[] Ins =
        {
            new Port("Density", PortType.Matrix, PortDirection.Input),
            new Port("Exclusion", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        /// <summary>How completely the mask clears density, 0..1.</summary>
        public float strength = 1f;

        /// <summary>Soft margin around the mask in world metres.</summary>
        public float feather = 8f;

        /// <summary>Treats the mask as a keep-region instead of a remove-region.</summary>
        public bool invert;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix density = GetInputMatrix(tile, 0, stop);
            Matrix exclusion = GetInputMatrix(tile, 1, stop);

            if (density == null) return tile.CreateMatrix();
            if (exclusion == null) return new Matrix(density);
            if (stop != null && stop.StopRequested) return null;

            Matrix blocked = exclusion;

            if (feather > 0.01f)
            {
                Vector2 ps = density.PixelSize;
                float mpp = Mathf.Max(1e-4f, (ps.x + ps.y) * 0.5f);
                float sigma = Mathf.Clamp(feather / mpp, 0.5f, 32f);
                blocked = MatrixOperations.GaussianBlur(exclusion, sigma);
            }

            if (stop != null && stop.StopRequested) return null;

            Matrix dst = tile.CreateMatrix();
            float s = Mathf.Clamp01(strength);

            for (int i = 0; i < dst.arr.Length; i++)
            {
                float e = Mathf.Clamp01(blocked.arr[i]);
                if (invert) e = 1f - e;

                dst.arr[i] = Mathf.Clamp01(density.arr[i] * (1f - e * s));
            }

            return dst;
        }
    }

    /// <summary>
    /// Biases a flat density field into groves. Real vegetation is patchy because
    /// seed dispersal is local, so this multiplies by a clumping field rather than
    /// thinning uniformly - the gaps between groves go to near zero while the
    /// grove centres keep full density.
    /// </summary>
    [System.Serializable]
    public class ClusterGroveBiasNode : Generator
    {
        public override string NodeName { get { return "Cluster Grove Bias"; } }
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }
        public override string Tooltip { get { return "Clumps a density field into groves and clearings."; } }

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        public int seed = 3377;

        /// <summary>Average grove diameter in world metres.</summary>
        public float groveSize = 90f;

        /// <summary>How strongly groves are separated, 0..1. 0 leaves the field flat.</summary>
        public float strength = 0.7f;

        /// <summary>Fraction of ground that stays clear between groves, 0..1.</summary>
        public float clearingFraction = 0.45f;

        /// <summary>Softness of the grove edge, 0..1.</summary>
        public float edgeSoftness = 0.35f;

        /// <summary>Adds a second, larger scale so groves themselves cluster.</summary>
        public bool twoScale = true;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix density = GetInputMatrix(tile, 0, stop);
            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Noise noise = new Noise(seed);
            Noise coarse = noise.WithSubSeed(51);

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = dst.PixelSize.x;
            float mppZ = dst.PixelSize.y;
            float originX = dst.worldPos.x;
            float originZ = dst.worldPos.z;

            float safeSize = Mathf.Max(1f, groveSize);
            float s = Mathf.Clamp01(strength);
            float threshold = Mathf.Clamp01(clearingFraction);
            float soft = Mathf.Max(1e-3f, Mathf.Clamp01(edgeSoftness));

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float d = density != null ? density.arr[i] : 1f;
                    if (d <= 0f) { dst.arr[i] = 0f; continue; }

                    float wx = originX + x * mppX;

                    float n = noise.Fractal(wx, wz, safeSize, 3, 0.55f);

                    if (twoScale)
                    {
                        // Groves cluster into stands: the coarse field decides where
                        // woodland is possible at all, the fine one shapes each grove.
                        float c = coarse.Fractal(wx, wz, safeSize * 6f, 2);
                        n = n * 0.6f + c * 0.4f;
                    }

                    // Threshold with a soft shoulder: above it is grove, below it is
                    // clearing, and the shoulder width is the edge softness.
                    float grove = InterpolationMath.SmoothStep(Mathf.Clamp01((n - threshold) / soft + 0.5f));

                    dst.arr[i] = Mathf.Clamp01(d * (1f - s + grove * s));
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Blue-noise emitter driven by a density field. This is the generic scatter
    /// the pipeline above feeds; the Module 1 nodes stay as the quick path for
    /// trees, rocks, grass and debris.
    /// </summary>
    [System.Serializable]
    public class PoissonDiscScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Poisson Disc Scatter"; } }
        public override string Tooltip { get { return "Blue-noise instance placement from a density field."; } }

        /// <summary>OutputBag channel these instances are filed under.</summary>
        public string channelName = "custom";

        public override string Channel { get { return string.IsNullOrEmpty(channelName) ? "custom" : channelName; } }

        /// <summary>Number of prototype variants to spread instances across.</summary>
        public int prototypeCount = 1;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            int variants = Mathf.Max(1, prototypeCount);
            if (variants == 1 || instances.Count == 0) return;

            // Spread across prototypes by hashing the position, so the assignment is
            // stable when the chunk is regenerated.
            Noise picker = new Noise(seed + 15486071);

            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];

                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);

                int index = Mathf.Clamp((int)(picker.Random(hx, hz) * variants), 0, variants - 1);
                inst.prototypeIndex = index;
                instances[i] = inst;
            }
        }
    }

    /// <summary>
    /// Picks a species per point from a weighted set whose weights vary with
    /// altitude. Emits one channel per species so each can carry its own prefab
    /// list downstream.
    /// </summary>
    [System.Serializable]
    public class SpeciesInstanceResolverNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Species Instance Resolver"; } }
        public override string Tooltip { get { return "Assigns a species variant per point, weighted by altitude."; } }

        public string channelName = "species";

        public override string Channel { get { return string.IsNullOrEmpty(channelName) ? "species" : channelName; } }

        /// <summary>Number of species in the mix.</summary>
        public int speciesCount = 3;

        /// <summary>
        /// Altitude at which each successive species takes over, as a fraction of
        /// the node's own height band. 0 mixes them evenly at all altitudes.
        /// </summary>
        public float altitudeZoning = 0.6f;

        /// <summary>Width of the transition between neighbouring species, 0..1.</summary>
        public float blendWidth = 0.25f;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            int species = Mathf.Max(1, speciesCount);
            if (species == 1 || instances.Count == 0) return;

            Noise picker = new Noise(seed + 32452843);

            float lo = Mathf.Min(minHeight, maxHeight);
            float hi = Mathf.Max(minHeight, maxHeight);
            float span = Mathf.Max(1e-4f, hi - lo);

            float zoning = Mathf.Clamp01(altitudeZoning);
            float blend = Mathf.Max(1e-3f, Mathf.Clamp01(blendWidth));
            float worldHeight = Mathf.Max(1e-4f, tile.worldHeight);

            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterInstance inst = instances[i];

                float normalised = (inst.position.y - tile.worldPos.y) / worldHeight;
                float t = Mathf.Clamp01((normalised - lo) / span);

                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);
                float r = picker.Random(hx, hz);

                // Blend the altitude preference with a random draw. At zoning 1 the
                // altitude decides outright; at 0 it is a uniform random mix.
                float biased = t * zoning + r * (1f - zoning);

                // Jitter by the blend width so the boundary between two species is a
                // mixed band rather than a clean line across the hillside.
                biased += (r - 0.5f) * blend;

                int index = Mathf.Clamp((int)(Mathf.Clamp01(biased) * species), 0, species - 1);
                inst.prototypeIndex = index;
                instances[i] = inst;
            }
        }
    }

    /// <summary>
    /// Bakes wind sway parameters as maps. Sway amplitude is wind speed modulated
    /// by shelter: a hollow or a lee slope moves far less than an exposed ridge,
    /// which is the difference between grass that looks placed and grass that looks
    /// grown. Port 1 carries a phase offset so neighbouring patches do not all
    /// swing in unison.
    /// </summary>
    [System.Serializable]
    public class WindSwayParameterBakeNode : Generator
    {
        public override string NodeName { get { return "Wind Sway Parameter Bake"; } }
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }
        public override string Tooltip { get { return "Bakes wind sway amplitude and phase maps for detail rendering."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Wind X", PortType.Matrix, PortDirection.Input),
            new Port("Wind Z", PortType.Matrix, PortDirection.Input),
            new Port("Vegetation", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Sway Amplitude", PortType.Matrix, PortDirection.Output, true),
            new Port("Sway Phase", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public int seed = 6161;

        /// <summary>Sway at full exposure with no shelter, 0..1.</summary>
        public float baseAmplitude = 0.5f;

        /// <summary>How much terrain shelter reduces sway, 0..1.</summary>
        public float shelterInfluence = 0.7f;

        /// <summary>Distance over which shelter is measured, in world metres.</summary>
        public float shelterRadius = 120f;

        /// <summary>Size of the gust cells that vary phase, in world metres.</summary>
        public float gustSize = 250f;

        /// <summary>Taller vegetation sways more. 0 ignores the vegetation input.</summary>
        public float vegetationInfluence = 0.4f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Noise noise = new Noise(seed);

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = dst.PixelSize.x;
            float mppZ = dst.PixelSize.y;
            float originX = dst.worldPos.x;
            float originZ = dst.worldPos.z;

            float safeGust = Mathf.Max(1f, gustSize);

            if (outputIndex == 1)
            {
                // Phase only needs to decorrelate neighbouring patches.
                for (int z = 0; z < resZ; z++)
                {
                    if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                    float wz = originZ + z * mppZ;
                    int row = z * resX;

                    for (int x = 0; x < resX; x++)
                        dst.arr[row + x] = Mathf.Clamp01(noise.Fractal(originX + x * mppX, wz, safeGust, 2));
                }

                return dst;
            }

            Matrix windX = GetInputMatrix(tile, 1, stop);
            Matrix windZ = GetInputMatrix(tile, 2, stop);
            Matrix vegetation = GetInputMatrix(tile, 3, stop);
            if (stop != null && stop.StopRequested) return null;

            // Shelter: ground below its own neighbourhood mean is in a hollow. A
            // blurred height field is that mean, so the difference is exposure.
            Matrix smoothed = null;
            if (height != null && shelterInfluence > 0.001f)
            {
                float mpp = Mathf.Max(1e-4f, (mppX + mppZ) * 0.5f);
                float sigma = Mathf.Clamp(shelterRadius / mpp, 0.5f, 48f);
                smoothed = MatrixOperations.GaussianBlur(height, sigma);
            }

            float shelter = Mathf.Clamp01(shelterInfluence);
            float vegInf = Mathf.Clamp01(vegetationInfluence);
            float amp = Mathf.Clamp01(baseAmplitude);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    float speed = 1f;
                    if (windX != null || windZ != null)
                    {
                        float wxv = windX != null ? (windX.arr[i] - 0.5f) * 2f : 0f;
                        float wzv = windZ != null ? (windZ.arr[i] - 0.5f) * 2f : 0f;
                        speed = Mathf.Clamp01(Mathf.Sqrt(wxv * wxv + wzv * wzv));
                    }

                    float exposure = 1f;
                    if (smoothed != null && height != null)
                    {
                        // Positive where the ground stands above its surroundings.
                        float relief = (height.arr[i] - smoothed.arr[i]) * 20f;
                        exposure = Mathf.Clamp01(0.5f + relief);
                        exposure = 1f - shelter + exposure * shelter;
                    }

                    float veg = 1f;
                    if (vegetation != null && vegInf > 0.001f)
                        veg = 1f - vegInf + Mathf.Clamp01(vegetation.arr[i]) * vegInf * 2f;

                    // A slow gust field so the whole field breathes rather than
                    // holding one constant amplitude.
                    float gust = 0.75f + noise.Fractal(originX + x * mppX, wz, safeGust * 3f, 2) * 0.5f;

                    dst.arr[i] = Mathf.Clamp01(amp * speed * exposure * veg * gust);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Ground-cover tint as three channels. Grass that ignores the soil under it
    /// reads as decals; sampling the surrounding ground and drying it out on
    /// exposed, low-moisture slopes is what ties the two together.
    /// </summary>
    [System.Serializable]
    public class GroundCoverColourTintNode : Generator
    {
        public override string NodeName { get { return "Ground Cover Colour Tint"; } }
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }
        public override string Tooltip { get { return "Tint channels for ground cover, driven by soil, moisture and season."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Moisture", PortType.Matrix, PortDirection.Input),
            new Port("Biome Id", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Tint R", PortType.Matrix, PortDirection.Output, true),
            new Port("Tint G", PortType.Matrix, PortDirection.Output, true),
            new Port("Tint B", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public int seed = 9091;

        /// <summary>Colour of well-watered ground cover.</summary>
        public Color healthyColor = new Color(0.35f, 0.52f, 0.22f);

        /// <summary>Colour where the ground is dry.</summary>
        public Color dryColor = new Color(0.62f, 0.56f, 0.30f);

        /// <summary>How strongly moisture drives the blend, 0..1.</summary>
        public float moistureInfluence = 0.8f;

        /// <summary>Altitude above which cover thins and pales, 0..1.</summary>
        public float paleAltitude = 0.75f;

        /// <summary>Per-patch colour variation, 0..1.</summary>
        public float variation = 0.15f;

        /// <summary>Size of the colour patches in world metres.</summary>
        public float patchSize = 180f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            Matrix moisture = GetInputMatrix(tile, 1, stop);
            Matrix biome = GetInputMatrix(tile, 2, stop);

            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            int channel = Mathf.Clamp(outputIndex, 0, 2);
            float healthy = channel == 0 ? healthyColor.r : channel == 1 ? healthyColor.g : healthyColor.b;
            float dry = channel == 0 ? dryColor.r : channel == 1 ? dryColor.g : dryColor.b;

            Noise noise = new Noise(seed).WithSubSeed(channel);

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = dst.PixelSize.x;
            float mppZ = dst.PixelSize.y;
            float originX = dst.worldPos.x;
            float originZ = dst.worldPos.z;

            float moist = Mathf.Clamp01(moistureInfluence);
            float vary = Mathf.Clamp01(variation);
            float safePatch = Mathf.Max(1f, patchSize);
            float pale = Mathf.Clamp01(paleAltitude);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float wx = originX + x * mppX;

                    float wet = moisture != null ? Mathf.Clamp01(moisture.arr[i]) : 0.5f;
                    float t = 1f - (moist * wet + (1f - moist) * 0.5f);

                    float value = Mathf.Lerp(healthy, dry, Mathf.Clamp01(t));

                    if (height != null)
                    {
                        // Cover pales toward bare ground with altitude.
                        float h = height.arr[i];
                        float alpine = InterpolationMath.SmoothStep(
                            Mathf.Clamp01((h - pale) / Mathf.Max(1e-4f, 1f - pale)));
                        value = Mathf.Lerp(value, value * 0.75f + 0.25f, alpine);
                    }

                    if (biome != null)
                    {
                        // Nudge per biome so two biomes never share an exact tint.
                        float shift = (noise.Random(Mathf.RoundToInt(biome.arr[i] * 255f)) - 0.5f) * 0.12f;
                        value += shift;
                    }

                    if (vary > 0.001f)
                    {
                        float n = noise.Fractal(wx, wz, safePatch, 3) - 0.5f;
                        value += n * 2f * vary * 0.25f;
                    }

                    dst.arr[i] = Mathf.Clamp01(value);
                }
            }

            return dst;
        }
    }
}
