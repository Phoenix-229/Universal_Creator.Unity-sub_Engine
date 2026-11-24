// UC-T08-03: author climate in Lighting Graph, publish once, and capture only dependent terrain/biome readers.
using System;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Nodes.Biome
{
    /// <summary>Authors a terrain-independent sea-level climate with worker-safe repeating seasonal curves.</summary>
    [Serializable]
    public class ClimateBaselineNode : Generator
    {
        /// <summary>Sea-level mean temperature in degrees Celsius, -100 through 70.</summary>
        [Range(-100, 70)] public float temperatureC = 15;
        /// <summary>Long-term available moisture, zero through one.</summary>
        [Range(0, 1)] public float moisture01 = 0.5f;
        /// <summary>Bearing wind blows toward in degrees; zero is world +Z, 90 is +X.</summary>
        [Range(0, 360)] public float windBearingDeg = 270;
        /// <summary>Prevailing wind speed in metres per second, zero through 100.</summary>
        [Range(0, 100)] public float windSpeedMetresPerSecond = 4;
        /// <summary>Temperature delta in degrees Celsius by model-year fraction; endpoints repeat.</summary>
        public AnimationCurve seasonalTemperatureC = AnimationCurve.Linear(0, 0, 1, 0);
        /// <summary>Moisture multiplier by model-year fraction, zero through four; endpoints repeat.</summary>
        public AnimationCurve seasonalMoistureScale = AnimationCurve.Linear(0, 1, 1, 1);
        [NonSerialized] private float[] temperatures, moistures;
        private static readonly Port[] ClimateOut = { new Port("Climate", PortType.Climate, PortDirection.Output) };
        public override string NodeName => "Climate Baseline";
        public override NodeCategory Category => NodeCategory.Biome;
        public override UCGraphDomainMask GraphDomains => UCGraphDomainMask.LightingClimateWeather;
        public override string Tooltip => "Long-term sea-level climate before terrain. Curves repeat each 365-day model year; live rain never edits them.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => ClimateOut;
        public override string SettingsDiagnostic =>
            !ClimateBaselineProduct.Finite(temperatureC) || temperatureC < -100 || temperatureC > 70 ||
            !ClimateBaselineProduct.Finite(moisture01) || moisture01 < 0 || moisture01 > 1 ||
            !ClimateBaselineProduct.Finite(windBearingDeg) || windBearingDeg < 0 || windBearingDeg > 360 ||
            !ClimateBaselineProduct.Finite(windSpeedMetresPerSecond) || windSpeedMetresPerSecond < 0 || windSpeedMetresPerSecond > 100 ?
            "Use finite climate temperature (-100..70 C), moisture (0..1), bearing (0..360 degrees) and wind speed (0..100 m/s)." : null;

        /// <summary>Captures 65 seasonal samples on Unity's main thread, rejecting missing/nonfinite curves.</summary>
        /// <exception cref="InvalidOperationException">An authored value or sampled seasonal value is unsupported.</exception>
        public override void PrepareMainThread()
        {
            if (SettingsDiagnostic != null) throw new InvalidOperationException(SettingsDiagnostic);
            temperatures = Bake(seasonalTemperatureC, -100, 100); moistures = Bake(seasonalMoistureScale, 0, 4);
        }
        private static float[] Bake(AnimationCurve curve, float min, float max)
        {
            if (curve == null || curve.length == 0) throw new InvalidOperationException("Climate seasonal curves require at least one finite key.");
            var values = new float[ClimateBaselineProduct.SeasonalSamples];
            for (int i = 0; i < values.Length - 1; i++)
            { float value = curve.Evaluate(i / (float)(values.Length - 1)); if (!ClimateBaselineProduct.Finite(value) || value < min || value > max) throw new InvalidOperationException("Climate seasonal curve exceeds its documented range."); values[i] = value; }
            values[values.Length - 1] = values[0]; // Repeating annual climate remains continuous at the model-year boundary.
            return values;
        }
        internal ClimateBaselineProduct.Band CaptureBand(Rect bounds, float blend, int priority, Matrix mask)
        {
            if (temperatures == null || moistures == null) throw new InvalidOperationException("Capture climate curves before worker evaluation.");
            float angle = windBearingDeg * Mathf.Deg2Rad;
            return new ClimateBaselineProduct.Band(id, priority, bounds, blend, temperatureC, moisture01,
                new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * windSpeedMetresPerSecond, temperatures, moistures, mask);
        }
        /// <summary>Produces an immutable climate recipe, or null on cooperative cancellation.</summary>
        /// <param name="outputIndex">Climate output slot zero.</param>
        /// <param name="tile">Evaluation tile; no terrain input is required.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Typed baseline independent of tile resolution.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The output slot is unsupported.</exception>
        /// <exception cref="InvalidOperationException">Seasonal curves were not captured.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        { if (outputIndex != 0) throw new ArgumentOutOfRangeException(nameof(outputIndex)); return stop != null && stop.StopRequested ? null : new ClimateBaselineProduct(CaptureBand(default, 0, 0, null)); }
    }

    /// <summary>Appends an authored regional climate override to an existing baseline, with metre bounds and optional mask.</summary>
    [Serializable]
    public sealed class ClimateRegionNode : ClimateBaselineNode
    {
        /// <summary>World XZ rectangle in metres; both extents must be positive.</summary>
        public Rect boundsMetres = new Rect(-1000, -1000, 2000, 2000);
        /// <summary>Inward edge blend distance in metres, zero for a hard boundary.</summary>
        [Min(0)] public float blendDistanceMetres = 100;
        /// <summary>Higher priority overrides lower priority; equal priority sorts by stable node ID.</summary>
        public int priority;
        private static readonly Port[] Ins = { new Port("Baseline", PortType.Climate, PortDirection.Input), new Port("Region Mask", PortType.Matrix, PortDirection.Input) };
        public override string NodeName => "Climate Region";
        public override Port[] GetInputs() => Ins;
        public override string SettingsDiagnostic => base.SettingsDiagnostic ??
            (!ClimateBaselineProduct.Finite(boundsMetres.x) || !ClimateBaselineProduct.Finite(boundsMetres.y) ||
            !ClimateBaselineProduct.Finite(boundsMetres.width) || !ClimateBaselineProduct.Finite(boundsMetres.height) ||
            !ClimateBaselineProduct.Finite(boundsMetres.xMax) || !ClimateBaselineProduct.Finite(boundsMetres.yMax) ||
            boundsMetres.width <= 0 || boundsMetres.height <= 0 || !ClimateBaselineProduct.Finite(blendDistanceMetres) || blendDistanceMetres < 0 ?
            "Climate regions require finite positive metre bounds and a nonnegative blend distance." : null);
        /// <summary>Creates a bounded immutable regional recipe without modifying the upstream baseline or mask.</summary>
        /// <param name="outputIndex">Climate output slot zero.</param>
        /// <param name="tile">Tile providing the typed baseline and optional authored mask.</param>
        /// <param name="stop">Optional cancellation propagated to both pulls.</param>
        /// <returns>New climate recipe or null on cancellation.</returns>
        /// <exception cref="InvalidOperationException">Baseline is missing, region limit exceeded or IDs repeat.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex != 0) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            var climate = tile.PullInput(this, 0, stop) as ClimateBaselineProduct;
            Matrix mask = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;
            if (climate == null) throw new InvalidOperationException("Connect Climate Baseline before adding a Climate Region.");
            return climate.WithRegion(CaptureBand(boundsMetres, blendDistanceMetres, priority, mask));
        }
    }

    /// <summary>Produces later environment corrections from the same committed ground/water view used by World Graph Input.</summary>
    /// <remarks>UC-T08-04: Lighting-only and ground-dependent. No terrain graph is evaluated or modified; coastal response is local to the bounded accepted shore query.</remarks>
    [Serializable]
    public sealed class TerrainClimateModifierNode : Generator
    {
        /// <summary>World sea level in metres used to convert committed elevation to altitude.</summary>
        public float seaLevelMetres;
        /// <summary>Environmental temperature lapse in degrees Celsius per kilometre, zero through 20.</summary>
        [Range(0, 20)] public float lapseRateCPerKm = AtmoConstants.LapseRateCPerKm;
        /// <summary>Warm-slope compass bearing in world degrees, zero through 360.</summary>
        [Range(0, 360)] public float warmAspectDeg = 180;
        /// <summary>Temperature amplitude on sun-facing slopes in degrees Celsius, zero through 20; flat ground has no aspect warming.</summary>
        [Range(0, 20)] public float aspectWarmingC = UniverseCreator.Ecology.EcoConstants.AspectWarmingDefaultC;
        /// <summary>Local ocean-shore blend distance in metres, positive and at most 256.</summary>
        [Range(1, 256)] public float coastalReachMetres = 100;
        /// <summary>Authored temperature correction at the accepted ocean shore in degrees Celsius, -20 through 20; zero disables this coastal channel.</summary>
        [Range(-20, 20)] public float coastalTemperatureBiasC;
        /// <summary>Authored precipitation-rate correction at the ocean shore in millimetres per hour, zero through 100; zero disables this channel.</summary>
        [Range(0, 100)] public float coastalPrecipitationMmPerHour;
        private static readonly Port[] Outs = { new Port("Temperature Correction (Celsius)", PortType.Matrix, PortDirection.Output),
            new Port("Coastal Precipitation Correction (mm/hour)", PortType.Matrix, PortDirection.Output) };
        public override string NodeName => "Terrain Climate Modifier";
        public override NodeCategory Category => NodeCategory.Biome;
        public override UCGraphDomainMask GraphDomains => UCGraphDomainMask.LightingClimateWeather;
        public override string Tooltip => "Later environment correction from committed elevation, aspect and local ocean shore. Requires accepted terrain; never feeds terrain generation back.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => Outs;
        public override string SettingsDiagnostic =>
            !ClimateBaselineProduct.Finite(seaLevelMetres) || !Valid(lapseRateCPerKm, 0, 20) || !Valid(warmAspectDeg, 0, 360) ||
            !Valid(aspectWarmingC, 0, 20) || !Valid(coastalReachMetres, 1, 256) || !Valid(coastalTemperatureBiasC, -20, 20) ||
            !Valid(coastalPrecipitationMmPerHour, 0, 100) ? "Use finite terrain climate values within their documented Celsius/metre/rate ranges." : null;
        private static bool Valid(float value, float min, float max) => ClimateBaselineProduct.Finite(value) && value >= min && value <= max;

        /// <summary>Samples committed ground into an environment correction using existing lapse/aspect mathematics.</summary>
        /// <param name="outputIndex">Zero for Celsius correction, one for coastal precipitation mm/hour.</param>
        /// <param name="tile">Lighting tile bound to this world's accepted ground and captured water geometry.</param>
        /// <param name="stop">Optional cancellation checked once per row.</param>
        /// <returns>New bounded correction field, or null on cancellation.</returns>
        /// <exception cref="InvalidOperationException">Accepted ground/water is absent or does not cover the requested region.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The output index is unsupported.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex < 0 || outputIndex >= Outs.Length) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            var ground = tile.worldGraphGround ?? throw new InvalidOperationException("Apply terrain-informed climate only after accepted main-graph ground is available.");
            bool coast = outputIndex == 0 ? coastalTemperatureBiasC != 0 : coastalPrecipitationMmPerHour != 0;
            if (coast && ground.Water == null) throw new InvalidOperationException("Capture accepted water geometry before enabling coastal climate response.");
            Matrix field = tile.CreateMatrix(); Vector2 spacing = field.PixelSize;
            for (int z = 0; z < field.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < field.rect.size.x; x++)
                {
                    float wx = field.worldPos.x + x * spacing.x, wz = field.worldPos.z + z * spacing.y;
                    // Terrain holes carry no terrestrial correction; they are never filled or treated as a slope.
                    if (!ground.TrySample(wx, wz, out float elevation)) continue;
                    float coastalWeight = 0;
                    if (coast)
                    {
                        float distance = Mathf.Max(0, ground.Water.SignedShoreDistance(wx, wz, coastalReachMetres, WaterBodyKind.Ocean));
                        coastalWeight = 1 - Mathf.SmoothStep(0, 1, distance / coastalReachMetres);
                    }
                    float value = coastalWeight * coastalPrecipitationMmPerHour;
                    if (outputIndex == 0)
                    {
                        Vector3 normal = ground.Snapshot.GroundNormal(wx, wz);
                        float horizontal = normal.x * normal.x + normal.z * normal.z;
                        float aspect = horizontal < UniverseCreator.Ecology.EcoConstants.AspectFlatSlopeTangent * UniverseCreator.Ecology.EcoConstants.AspectFlatSlopeTangent ?
                            UniverseCreator.Ecology.EcoConstants.AspectFlatSentinelDegrees : Mathf.Repeat(Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg, 360);
                        value = AtmoMath.LapseAdjustedTempC(0, elevation - seaLevelMetres, lapseRateCPerKm) +
                            UniverseCreator.Ecology.TopographyMath.AspectWarmingC(aspect, warmAspectDeg, aspectWarmingC) + coastalWeight * coastalTemperatureBiasC;
                    }
                    field.arr[z * field.rect.size.x + x] = Mathf.Clamp(value, -100, 100);
                }
            }
            return field;
        }
    }

    /// <summary>Captures accepted early climate into ordinary terrain/biome fields without requiring accepted ground.</summary>
    [Serializable]
    public sealed class ClimateBaselineInputNode : Generator, IGenerationSnapshotDependencies, IPreparedGraphInputHash
    {
        /// <summary>Fixed model-year phase used for an explicit terrain/biome bake, zero through one; live time does not invalidate it.</summary>
        [Range(0, 1)] public float yearFraction;
        /// <summary>True uses the documented 15 C / 0.5 moisture / calm fallback when no baseline has been explicitly applied.</summary>
        public bool allowMissingBaseline = true;
        [NonSerialized] private ClimateBaselineProduct captured;
        [NonSerialized] private ProductMemoryBudget.Lease pin;
        private static readonly Port[] Outs = {
            new Port("Temperature (-20..40 C normalized)", PortType.Matrix, PortDirection.Output),
            new Port("Moisture", PortType.Matrix, PortDirection.Output), new Port("Aridity", PortType.Matrix, PortDirection.Output),
            new Port("Wind X (-100..100 m/s normalized)", PortType.Matrix, PortDirection.Output),
            new Port("Wind Z (-100..100 m/s normalized)", PortType.Matrix, PortDirection.Output) };
        public override string NodeName => "Climate Baseline Input";
        public override NodeCategory Category => NodeCategory.Biome;
        public override UCGraphDomainMask GraphDomains => UCGraphDomainMask.TerrainWater | UCGraphDomainMask.Vegetation;
        public override string Tooltip => "Reads explicitly accepted early climate for terrain and biome classification. Missing baseline uses 15 C, 0.5 moisture and calm unless disabled.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => Outs;
        public override string SettingsDiagnostic => !ClimateBaselineProduct.Finite(yearFraction) || yearFraction < 0 || yearFraction > 1 ? "Climate bake year fraction must be finite and between zero and one." : null;
        /// <summary>Captures only the world's accepted baseline and pins its immutable managed product until snapshot retirement.</summary>
        /// <param name="world">Bound UC world, or null for explicit fallback sampling.</param>
        /// <param name="domains">Parent's admitted execution domains; sampling remains worker-safe.</param>
        /// <exception cref="InvalidOperationException">Baseline is missing and fallback is disabled.</exception>
        public void CaptureDependencies(UniverseCreatorObject world, NodeExecutionDomain domains)
        {
            ReleaseDependencies();
            captured = world != null ? world.GetComponent<UCWorldGraphBindings>()?.CaptureClimateBaseline() : null;
            if (captured == null && !allowMissingBaseline) throw new InvalidOperationException("Apply the Lighting Graph's early climate baseline before this bake.");
            if (captured != null) pin = world.ProductMemory.Acquire(captured, ProductMemoryFootprint.Estimate(captured), 0, "captured-climate-baseline");
        }
        /// <summary>Releases the captured product-memory lease exactly once; a new capture does not mutate other snapshots.</summary>
        public void ReleaseDependencies() { pin?.Dispose(); pin = null; captured = null; }
        /// <summary>Returns exact accepted climate content, or a distinct stable fallback fingerprint; live weather is excluded.</summary>
        public ulong GetPreparedInputHash() => captured != null ? captured.ContentHash : 1UL;
        /// <summary>Samples captured climate into a normalized field for existing terrain/biome consumers.</summary>
        /// <param name="outputIndex">Temperature, moisture, aridity, wind X or wind Z slot, zero through four.</param>
        /// <param name="tile">Tile whose world metre grid is sampled.</param>
        /// <param name="stop">Optional cancellation, checked each row.</param>
        /// <returns>New field or null on cancellation.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Output index is unsupported.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex < 0 || outputIndex >= Outs.Length) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            Matrix result = tile.CreateMatrix(); Vector2 spacing = result.PixelSize;
            for (int z = 0; z < result.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < result.rect.size.x; x++)
                {
                    var position = new Vector2(result.worldPos.x + x * spacing.x, result.worldPos.z + z * spacing.y);
                    var sample = captured != null ? captured.Sample(position, yearFraction) : new ClimateBaselineSample(15, 0.5f, Vector2.zero);
                    result.arr[z * result.rect.size.x + x] = outputIndex == 0 ? Mathf.Clamp01((sample.TemperatureC + 20) / 60) :
                        outputIndex == 1 ? sample.Moisture01 : outputIndex == 2 ? sample.Aridity01 :
                        0.5f + (outputIndex == 3 ? sample.WindMetresPerSecond.x : sample.WindMetresPerSecond.y) / 200;
                }
            }
            return result;
        }
    }
}
