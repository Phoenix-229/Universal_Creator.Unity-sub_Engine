// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    ClimateNodes.cs
//  Purpose: Precipitation, rain shadow, the two standard biome classifiers,
//           seasonality, and the snowline.
//
//  Module 1 shipped Temperature Map and Moisture Map, which are gradients with
//  noise on them. These solve the actual mechanism instead: air is pushed across
//  the terrain, forced up by whatever it meets, drops its water on the way up and
//  arrives dry on the far side. That single march is what puts a rainforest on one
//  coast and a desert 80 km inland, and it is why the deserts land in the right
//  places without anyone painting them.
//
//  The classifiers then read temperature and precipitation the way the published
//  schemes do, so the biome that comes out is defensible rather than invented.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    /// <summary>
    /// Orographic precipitation. Marches parcels of air downwind across the tile,
    /// carrying moisture that condenses as the ground rises and is replenished over
    /// water. Windward slopes get the rain and the lee goes dry.
    /// </summary>
    [System.Serializable]
    public class PrecipitationFieldNode : Generator
    {
        public override string NodeName { get { return "Precipitation Field"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Marches wind across the terrain; windward slopes get the rain."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Wind X", PortType.Matrix, PortDirection.Input),
            new Port("Wind Z", PortType.Matrix, PortDirection.Input),
            new Port("Temperature", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.MaskOut; }

        public int seed = 2024;

        /// <summary>Wind direction in degrees when no wind field is wired. 0 is +X.</summary>
        public float windDirection = 270f;

        /// <summary>Moisture a parcel starts with over open water, 0..1.</summary>
        public float sourceMoisture = 1f;

        /// <summary>How readily rising air sheds its moisture, 0..1.</summary>
        public float condensationRate = 0.55f;

        /// <summary>Rain that falls regardless of relief, 0..1.</summary>
        public float baseRainfall = 0.12f;

        /// <summary>How fast moisture is regained over water and lowland, 0..1.</summary>
        public float evaporationRate = 0.35f;

        /// <summary>Sea level. Anything below it is a moisture source.</summary>
        public float seaLevel = 0.35f;

        /// <summary>How far upwind the march looks, in world metres.</summary>
        public float marchDistance = 4000f;

        /// <summary>Number of march steps. More is smoother and slower.</summary>
        public int marchSteps = 48;

        /// <summary>Cold air holds less water. 0 ignores the temperature input.</summary>
        public float temperatureInfluence = 0.4f;

        /// <summary>Noise on the result so rain bands are not perfectly smooth.</summary>
        public float variation = 0.18f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (height == null) return tile.CreateMatrix();

            Matrix windX = GetInputMatrix(tile, 1, stop);
            Matrix windZ = GetInputMatrix(tile, 2, stop);
            Matrix temperature = GetInputMatrix(tile, 3, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = tile.CreateMatrix();

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = height.PixelSize.x;
            float mppZ = height.PixelSize.y;
            float originX = height.worldPos.x;
            float originZ = height.worldPos.z;

            Noise noise = new Noise(seed);

            int steps = Mathf.Clamp(marchSteps, 4, 256);
            float distance = Mathf.Max(1f, marchDistance);
            float stepLength = distance / steps;

            float sea = Mathf.Clamp01(seaLevel);
            float condense = Mathf.Clamp01(condensationRate);
            float evaporate = Mathf.Clamp01(evaporationRate);
            float source = Mathf.Clamp01(sourceMoisture);
            float tempInf = Mathf.Clamp01(temperatureInfluence);
            float vary = Mathf.Clamp01(variation);

            float defaultRad = windDirection * Mathf.Deg2Rad;
            float defaultX = Mathf.Cos(defaultRad);
            float defaultZ = Mathf.Sin(defaultRad);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float wx = originX + x * mppX;

                    // Wind at the destination sets the march direction. Reading it
                    // per-parcel instead would make the path curl, which is more
                    // physical but costs a sample per step for very little visual gain.
                    float dirX = defaultX;
                    float dirZ = defaultZ;

                    if (windX != null || windZ != null)
                    {
                        float vx = windX != null ? (windX.arr[i] - 0.5f) * 2f : 0f;
                        float vz = windZ != null ? (windZ.arr[i] - 0.5f) * 2f : 0f;
                        float len = Mathf.Sqrt(vx * vx + vz * vz);
                        if (len > 1e-4f) { dirX = vx / len; dirZ = vz / len; }
                    }

                    // Walk from upwind toward this pixel, tracking what the parcel
                    // still carries by the time it arrives.
                    float startX = wx - dirX * distance;
                    float startZ = wz - dirZ * distance;

                    float moisture = source;
                    float rain = 0f;

                    Vector2 prev = height.WorldToPixel(startX, startZ);
                    float prevH = height.GetInterpolated(prev.x, prev.y);

                    for (int s = 1; s <= steps; s++)
                    {
                        float t = s / (float)steps;
                        float sxw = Mathf.Lerp(startX, wx, t);
                        float szw = Mathf.Lerp(startZ, wz, t);

                        Vector2 p = height.WorldToPixel(sxw, szw);
                        float h = height.GetInterpolated(p.x, p.y);

                        if (h <= sea)
                        {
                            // Over water the parcel takes moisture back up.
                            moisture += (source - moisture) * evaporate;
                        }
                        else
                        {
                            float rise = h - prevH;

                            if (rise > 0f)
                            {
                                // Forced ascent: the parcel cools and sheds water in
                                // proportion to how fast it is being lifted.
                                float lift = rise * height.worldHeight / Mathf.Max(1f, stepLength);
                                float shed = moisture * condense * Mathf.Clamp01(lift * 4f);
                                moisture -= shed;
                                rain += shed;
                            }
                            else
                            {
                                // Descending air warms and holds what it has, which
                                // is exactly why the lee side stays dry.
                                moisture += (source - moisture) * evaporate * 0.15f;
                            }
                        }

                        prevH = h;
                    }

                    float value = baseRainfall + rain;

                    if (temperature != null && tempInf > 0.001f)
                    {
                        // Cold air carries less water, so high latitudes and high
                        // ground are drier even where the relief would say otherwise.
                        float warmth = Mathf.Clamp01(temperature.arr[i]);
                        value *= 1f - tempInf + warmth * tempInf * 1.6f;
                    }

                    if (vary > 0.001f)
                    {
                        float n = noise.Fractal(wx, wz, 900f, 3);
                        value *= 1f - vary + n * vary * 2f;
                    }

                    dst.arr[i] = Mathf.Clamp01(value);
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Isolates the dry side of a range as its own mask. Precipitation Field
    /// already produces the shadow implicitly; this measures how much drier a
    /// pixel is than the upwind terrain would suggest, which is what you want to
    /// drive a scrub or desert texture directly.
    /// </summary>
    [System.Serializable]
    public class RainShadowSolverNode : Generator
    {
        public override string NodeName { get { return "Rain Shadow Solver"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Marks lee-side dry zones behind terrain barriers."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Wind X", PortType.Matrix, PortDirection.Input),
            new Port("Wind Z", PortType.Matrix, PortDirection.Input),
            new Port("Precipitation", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Shadow", PortType.Matrix, PortDirection.Output, true),
            new Port("Adjusted Precip", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Wind direction in degrees when no wind field is wired.</summary>
        public float windDirection = 270f;

        /// <summary>How far upwind a barrier can cast a shadow, in world metres.</summary>
        public float shadowDistance = 3000f;

        /// <summary>Barrier height that casts a full shadow, 0..1 of world height.</summary>
        public float barrierHeight = 0.25f;

        /// <summary>How fast the shadow fades with distance downwind, 0..1.</summary>
        public float decay = 0.5f;

        /// <summary>How much of the precipitation the shadow removes, 0..1.</summary>
        public float dryingStrength = 0.75f;

        public int samples = 24;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (height == null) return tile.CreateMatrix();

            Matrix windX = GetInputMatrix(tile, 1, stop);
            Matrix windZ = GetInputMatrix(tile, 2, stop);
            Matrix precip = GetInputMatrix(tile, 3, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = tile.CreateMatrix();

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = height.PixelSize.x;
            float mppZ = height.PixelSize.y;
            float originX = height.worldPos.x;
            float originZ = height.worldPos.z;

            int steps = Mathf.Clamp(samples, 4, 128);
            float distance = Mathf.Max(1f, shadowDistance);
            float barrier = Mathf.Max(1e-4f, barrierHeight);
            float fade = Mathf.Clamp01(decay);
            float drying = Mathf.Clamp01(dryingStrength);

            float defaultRad = windDirection * Mathf.Deg2Rad;
            float defaultX = Mathf.Cos(defaultRad);
            float defaultZ = Mathf.Sin(defaultRad);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float wx = originX + x * mppX;
                    float h = height.arr[i];

                    float dirX = defaultX;
                    float dirZ = defaultZ;

                    if (windX != null || windZ != null)
                    {
                        float vx = windX != null ? (windX.arr[i] - 0.5f) * 2f : 0f;
                        float vz = windZ != null ? (windZ.arr[i] - 0.5f) * 2f : 0f;
                        float len = Mathf.Sqrt(vx * vx + vz * vz);
                        if (len > 1e-4f) { dirX = vx / len; dirZ = vz / len; }
                    }

                    // The tallest thing upwind casts the shadow, weighted by how far
                    // away it is: a ridge on the horizon shelters less than one next
                    // door, even if both are the same height.
                    float shadow = 0f;

                    for (int s = 1; s <= steps; s++)
                    {
                        float t = s / (float)steps;
                        float d = t * distance;

                        Vector2 p = height.WorldToPixel(wx - dirX * d, wz - dirZ * d);
                        float uh = height.GetInterpolated(p.x, p.y);

                        float rise = uh - h;
                        if (rise <= 0f) continue;

                        float strength = Mathf.Clamp01(rise / barrier);
                        strength *= 1f - fade * t;

                        if (strength > shadow) shadow = strength;
                    }

                    shadow = InterpolationMath.SmoothStep(Mathf.Clamp01(shadow));

                    if (outputIndex == 1)
                    {
                        float wet = precip != null ? precip.arr[i] : 0.5f;
                        dst.arr[i] = Mathf.Clamp01(wet * (1f - shadow * drying));
                    }
                    else
                    {
                        dst.arr[i] = shadow;
                    }
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Koeppen-Geiger classification. Ports the published decision tree: the
    /// tropical / dry / temperate / continental / polar split with the aridity
    /// threshold that makes a place a desert relative to how warm it is, not at a
    /// fixed rainfall figure.
    /// </summary>
    [System.Serializable]
    public class KoeppenBiomeClassifierNode : Generator
    {
        public override string NodeName { get { return "Koeppen Biome Classifier"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Koeppen-Geiger climate classes from temperature and precipitation."; } }

        private static readonly Port[] Ins =
        {
            new Port("Temperature", PortType.Matrix, PortDirection.Input),
            new Port("Precipitation", PortType.Matrix, PortDirection.Input),
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Biome Id", PortType.Matrix, PortDirection.Output, true),
            new Port("Class Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Temperature at the 0 end of the input, in Celsius.</summary>
        public float coldestTemperature = -25f;

        /// <summary>Temperature at the 1 end of the input, in Celsius.</summary>
        public float hottestTemperature = 40f;

        /// <summary>Annual rainfall at the 1 end of the input, in millimetres.</summary>
        public float maxAnnualRainfall = 3000f;

        /// <summary>Which class the Class Mask port isolates.</summary>
        public KoeppenClass isolateClass = KoeppenClass.Tropical;

        /// <summary>Softness of the class boundaries in the mask output, 0..1.</summary>
        public float boundarySoftness = 0.08f;

        /// <summary>Lapse rate applied per unit of normalised height, in Celsius.</summary>
        public float lapseRate = 25f;

        public enum KoeppenClass
        {
            Tropical = 0,
            Arid = 1,
            Temperate = 2,
            Continental = 3,
            Polar = 4
        }

        private const int ClassCount = 5;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix temperature = GetInputMatrix(tile, 0, stop);
            Matrix precip = GetInputMatrix(tile, 1, stop);
            Matrix height = GetInputMatrix(tile, 2, stop);

            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            float tMin = Mathf.Min(coldestTemperature, hottestTemperature);
            float tMax = Mathf.Max(coldestTemperature, hottestTemperature);
            float rainScale = Mathf.Max(1f, maxAnnualRainfall);
            float soft = Mathf.Max(1e-3f, Mathf.Clamp01(boundarySoftness));

            int isolate = (int)isolateClass;
            float invClasses = 1f / (ClassCount - 1);

            for (int i = 0; i < dst.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float tNorm = temperature != null ? Mathf.Clamp01(temperature.arr[i]) : 0.5f;
                float celsius = Mathf.Lerp(tMin, tMax, tNorm);

                // Air cools with altitude, which is why a tropical latitude can
                // still have an alpine summit.
                if (height != null) celsius -= height.arr[i] * lapseRate;

                float rain = (precip != null ? Mathf.Clamp01(precip.arr[i]) : 0.4f) * rainScale;

                int classId = Classify(celsius, rain);

                if (outputIndex == 1)
                {
                    // Softened membership rather than a hard 0/1, so the mask can
                    // drive a blend instead of a stencil.
                    float distance = Mathf.Abs(classId - isolate) * invClasses;
                    dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(distance / soft));
                }
                else
                {
                    dst.arr[i] = classId * invClasses;
                }
            }

            return dst;
        }

        /// <summary>
        /// The per-pixel approximation: one annual mean and one annual total, which
        /// is all a matrix element carries. Public so the diagnostic classifier that
        /// works from a full year of monthly normals can be checked against it -
        /// where both have enough information to be sure, the two must agree, and an
        /// assertion that they do is what stops the pair drifting apart.
        /// </summary>
        public static int Classify(float celsius, float rainMm)
        {
            // Polar: the warmest month never gets above 10 C.
            if (celsius < 0f) return (int)KoeppenClass.Polar;

            // Aridity threshold. The published rule is 20T + a seasonality term;
            // without a month-by-month series the annual mean is the honest
            // approximation, and it still puts deserts where warmth outruns rain.
            float aridThreshold = 20f * celsius;
            if (rainMm < aridThreshold) return (int)KoeppenClass.Arid;

            if (celsius >= 18f) return (int)KoeppenClass.Tropical;
            if (celsius >= 8f) return (int)KoeppenClass.Temperate;

            return (int)KoeppenClass.Continental;
        }
    }

    /// <summary>
    /// Whittaker classification. Reads the same two variables as Koeppen but plots
    /// them on the biome diagram, which answers "what grows here" rather than "what
    /// is the climate" - the more useful question when the output drives vegetation.
    /// </summary>
    [System.Serializable]
    public class WhittakerBiomeClassifierNode : Generator
    {
        public override string NodeName { get { return "Whittaker Biome Classifier"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Whittaker-diagram biome lookup from temperature and rainfall."; } }

        private static readonly Port[] Ins =
        {
            new Port("Temperature", PortType.Matrix, PortDirection.Input),
            new Port("Precipitation", PortType.Matrix, PortDirection.Input),
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Biome Id", PortType.Matrix, PortDirection.Output, true),
            new Port("Class Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Vegetation Density", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public float coldestTemperature = -15f;
        public float hottestTemperature = 30f;

        /// <summary>Annual rainfall at the 1 end of the input, in centimetres.</summary>
        public float maxAnnualRainfall = 450f;

        public WhittakerBiome isolateBiome = WhittakerBiome.TemperateForest;

        public float boundarySoftness = 0.1f;

        public float lapseRate = 22f;

        /// <summary>Blurs the classification so biomes meet in bands, not on lines.</summary>
        public float transitionBlur = 2f;

        public enum WhittakerBiome
        {
            Tundra = 0,
            BorealForest = 1,
            TemperateGrassland = 2,
            Woodland = 3,
            TemperateForest = 4,
            TemperateRainforest = 5,
            SubtropicalDesert = 6,
            Savanna = 7,
            TropicalForest = 8,
            TropicalRainforest = 9
        }

        private const int BiomeCount = 10;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix temperature = GetInputMatrix(tile, 0, stop);
            Matrix precip = GetInputMatrix(tile, 1, stop);
            Matrix height = GetInputMatrix(tile, 2, stop);

            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            float tMin = Mathf.Min(coldestTemperature, hottestTemperature);
            float tMax = Mathf.Max(coldestTemperature, hottestTemperature);
            float rainScale = Mathf.Max(1f, maxAnnualRainfall);
            float soft = Mathf.Max(1e-3f, Mathf.Clamp01(boundarySoftness));

            int isolate = (int)isolateBiome;
            float invBiomes = 1f / (BiomeCount - 1);

            for (int i = 0; i < dst.arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return null;

                float tNorm = temperature != null ? Mathf.Clamp01(temperature.arr[i]) : 0.5f;
                float celsius = Mathf.Lerp(tMin, tMax, tNorm);
                if (height != null) celsius -= height.arr[i] * lapseRate;

                float rainCm = (precip != null ? Mathf.Clamp01(precip.arr[i]) : 0.4f) * rainScale;

                int biome = Classify(celsius, rainCm);

                if (outputIndex == 2)
                {
                    dst.arr[i] = VegetationDensity(biome);
                }
                else if (outputIndex == 1)
                {
                    float distance = Mathf.Abs(biome - isolate) * invBiomes;
                    dst.arr[i] = 1f - InterpolationMath.SmoothStep(Mathf.Clamp01(distance / soft));
                }
                else
                {
                    dst.arr[i] = biome * invBiomes;
                }
            }

            if (transitionBlur > 0.01f && !(stop != null && stop.StopRequested))
                dst = MatrixOperations.GaussianBlur(dst, Mathf.Clamp(transitionBlur, 0.1f, 16f));

            return dst;
        }

        // The diagram itself: temperature runs left to right, rainfall bottom to
        // top, and the boundaries below are the standard cut lines.
        private static int Classify(float celsius, float rainCm)
        {
            if (celsius < -5f) return (int)WhittakerBiome.Tundra;

            if (celsius < 3f)
            {
                return rainCm < 25f
                    ? (int)WhittakerBiome.Tundra
                    : (int)WhittakerBiome.BorealForest;
            }

            if (celsius < 12f)
            {
                if (rainCm < 30f) return (int)WhittakerBiome.TemperateGrassland;
                if (rainCm < 100f) return (int)WhittakerBiome.BorealForest;
                return (int)WhittakerBiome.TemperateForest;
            }

            if (celsius < 20f)
            {
                if (rainCm < 30f) return (int)WhittakerBiome.TemperateGrassland;
                if (rainCm < 70f) return (int)WhittakerBiome.Woodland;
                if (rainCm < 200f) return (int)WhittakerBiome.TemperateForest;
                return (int)WhittakerBiome.TemperateRainforest;
            }

            if (rainCm < 40f) return (int)WhittakerBiome.SubtropicalDesert;
            if (rainCm < 130f) return (int)WhittakerBiome.Savanna;
            if (rainCm < 250f) return (int)WhittakerBiome.TropicalForest;

            return (int)WhittakerBiome.TropicalRainforest;
        }

        private static float VegetationDensity(int biome)
        {
            switch (biome)
            {
                case (int)WhittakerBiome.Tundra: return 0.12f;
                case (int)WhittakerBiome.BorealForest: return 0.70f;
                case (int)WhittakerBiome.TemperateGrassland: return 0.30f;
                case (int)WhittakerBiome.Woodland: return 0.50f;
                case (int)WhittakerBiome.TemperateForest: return 0.85f;
                case (int)WhittakerBiome.TemperateRainforest: return 1.00f;
                case (int)WhittakerBiome.SubtropicalDesert: return 0.05f;
                case (int)WhittakerBiome.Savanna: return 0.40f;
                case (int)WhittakerBiome.TropicalForest: return 0.90f;
                default: return 1.00f;
            }
        }
    }

    /// <summary>
    /// Season as a scalar plus the temperature and moisture offsets it implies.
    /// One node owns the phase so snowline, vegetation colour and precipitation
    /// cannot drift into different months.
    /// </summary>
    [System.Serializable]
    public class SeasonalVariationDriverNode : Generator
    {
        public override string NodeName { get { return "Seasonal Variation Driver"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Season phase plus the temperature and moisture shifts it causes."; } }

        private static readonly Port[] Outs =
        {
            new Port("Season", PortType.Scalar, PortDirection.Output, true),
            new Port("Temp Offset", PortType.Matrix, PortDirection.Output, true),
            new Port("Moisture Offset", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Point in the year, 0..1. 0 is midwinter, 0.5 is midsummer.</summary>
        [Range(0f, 1f)]
        public float season;

        /// <summary>Peak-to-trough temperature swing, normalised 0..1.</summary>
        public float temperatureAmplitude = 0.25f;

        /// <summary>Peak-to-trough moisture swing, normalised 0..1.</summary>
        public float moistureAmplitude = 0.2f;

        /// <summary>
        /// Latitude at which seasonality is strongest, 0..1. The tropics barely
        /// have seasons; the swing grows toward the poles.
        /// </summary>
        public float latitudeScaling = 0.7f;

        /// <summary>Shifts the wet season relative to the warm one, in fractions of a year.</summary>
        public float moisturePhaseShift = 0.15f;

        public float range = 60f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex == 0) return Mathf.Clamp01(season);

            Matrix dst = tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppZ = dst.PixelSize.y;
            float originZ = dst.worldPos.z;

            float worldMinZ = tile.fullWorldPos.z;
            float worldSizeZ = Mathf.Max(1f, tile.fullWorldSize.z);
            float safeRange = Mathf.Max(1f, range);

            bool moistureChannel = outputIndex == 2;
            float phase = Mathf.Repeat(season + (moistureChannel ? moisturePhaseShift : 0f), 1f);

            // Cosine, so 0 sits at the winter trough and 0.5 at the summer peak.
            float wave = -Mathf.Cos(phase * Mathf.PI * 2f);
            float amplitude = moistureChannel ? moistureAmplitude : temperatureAmplitude;
            float latScale = Mathf.Clamp01(latitudeScaling);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                float t = (wz - worldMinZ) / worldSizeZ * 2f - 1f;
                float latitude = Mathf.Abs(t * safeRange);

                // Seasonal swing grows away from the equator, and the hemispheres
                // are six months apart.
                float seasonality = 1f - latScale + (latitude / 90f) * latScale * 2f;
                float hemisphere = t >= 0f ? 1f : -1f;

                float offset = wave * amplitude * seasonality * hemisphere;

                // Stored around 0.5 so a consumer adds (value - 0.5) to its field.
                float stored = Mathf.Clamp01(0.5f + offset * 0.5f);

                int row = z * resX;
                for (int x = 0; x < resX; x++) dst.arr[row + x] = stored;
            }

            return dst;
        }
    }

    /// <summary>
    /// Snowline altitude per latitude, with the season and aspect corrections that
    /// stop it reading as a contour line. A north-facing slope holds snow lower
    /// than a sunlit south-facing one at the same height, and that asymmetry is
    /// most of what makes a snow-capped range look real.
    /// </summary>
    [System.Serializable]
    public class SnowlineAltitudeSolverNode : Generator
    {
        public override string NodeName { get { return "Snowline Altitude Solver"; } }
        public override NodeCategory Category { get { return NodeCategory.Biome; } }
        public override string Tooltip { get { return "Snow cover mask from latitude, season, aspect and slope."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Temperature", PortType.Matrix, PortDirection.Input),
            new Port("Season", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs =
        {
            new Port("Snow Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Snowline", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        /// <summary>Snowline at the equator, 0..1 of world height.</summary>
        public float equatorSnowline = 0.85f;

        /// <summary>Snowline at the poles, 0..1. Lower than the equator.</summary>
        public float polarSnowline = 0.15f;

        /// <summary>Softness of the snow edge, 0..1 of world height.</summary>
        public float transition = 0.04f;

        /// <summary>How far a shaded aspect lowers the line, 0..1.</summary>
        public float aspectInfluence = 0.06f;

        /// <summary>Compass bearing of the sun in degrees. 180 is due south.</summary>
        public float sunBearing = 180f;

        /// <summary>Slope above which snow slides off rather than settling, in degrees.</summary>
        public float maxSlope = 55f;

        /// <summary>How much the season raises and lowers the line, 0..1.</summary>
        public float seasonalSwing = 0.12f;

        /// <summary>Irregularity of the snow edge, 0..1.</summary>
        public float edgeNoise = 0.35f;

        public float range = 60f;

        public int seed = 5511;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (height == null) return tile.CreateMatrix();

            Matrix temperature = GetInputMatrix(tile, 1, stop);
            Matrix seasonField = GetInputMatrix(tile, 2, stop);
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = tile.CreateMatrix();

            bool needSurface = outputIndex == 0 && (aspectInfluence > 0.001f || maxSlope < 89f);
            Matrix aspects = needSurface ? MatrixOperations.AspectAngles(height) : null;
            Matrix slopes = needSurface ? MatrixOperations.SlopeAngles(height) : null;
            if (stop != null && stop.StopRequested) return null;

            Noise noise = new Noise(seed);

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            float mppX = dst.PixelSize.x;
            float mppZ = dst.PixelSize.y;
            float originX = dst.worldPos.x;
            float originZ = dst.worldPos.z;

            float worldMinZ = tile.fullWorldPos.z;
            float worldSizeZ = Mathf.Max(1f, tile.fullWorldSize.z);
            float safeRange = Mathf.Max(1f, range);

            float trans = Mathf.Max(1e-4f, transition);
            float aspectInf = Mathf.Clamp01(aspectInfluence);
            float swing = Mathf.Clamp01(seasonalSwing);
            float noiseAmt = Mathf.Clamp01(edgeNoise);
            float slopeLimit = Mathf.Clamp(maxSlope, 1f, 90f);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;

                float t = Mathf.Abs((wz - worldMinZ) / worldSizeZ * 2f - 1f);
                float latitude = Mathf.Clamp01(t * safeRange / 90f);

                // Warm air at the equator pushes the line high; near the poles snow
                // reaches sea level.
                float lineAtLatitude = Mathf.Lerp(equatorSnowline, polarSnowline, latitude);

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float line = lineAtLatitude;

                    if (seasonField != null && swing > 0.001f)
                        line += (seasonField.arr[i] - 0.5f) * 2f * swing;

                    if (temperature != null)
                    {
                        // A warm pixel needs more altitude before snow settles.
                        float warmth = Mathf.Clamp01(temperature.arr[i]) - 0.5f;
                        line += warmth * 0.25f;
                    }

                    if (noiseAmt > 0.001f)
                    {
                        float wx = originX + x * mppX;
                        line += (noise.Fractal(wx, wz, 320f, 3) - 0.5f) * 2f * noiseAmt * trans * 3f;
                    }

                    if (outputIndex == 1) { dst.arr[i] = Mathf.Clamp01(line); continue; }

                    if (aspects != null && aspectInf > 0.001f)
                    {
                        // Aspect is a compass bearing; the cosine of the difference
                        // from the sun bearing is how directly the slope faces it.
                        float delta = (aspects.arr[i] - sunBearing) * Mathf.Deg2Rad;
                        float facing = Mathf.Cos(delta);
                        line += facing * aspectInf;
                    }

                    float cover = InterpolationMath.SmoothStep(
                        Mathf.Clamp01((height.arr[i] - line) / trans + 0.5f));

                    if (slopes != null)
                    {
                        // Too steep and snow avalanches off, leaving bare rock.
                        float shed = 1f - InterpolationMath.SmoothStep(
                            Mathf.Clamp01((slopes.arr[i] - slopeLimit) / 12f));
                        cover *= shed;
                    }

                    dst.arr[i] = Mathf.Clamp01(cover);
                }
            }

            return dst;
        }
    }
}
