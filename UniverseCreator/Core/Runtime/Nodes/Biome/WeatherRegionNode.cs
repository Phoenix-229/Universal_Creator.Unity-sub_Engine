// UC-T08-06: a Lighting Graph recipe for the existing regional weather adapter; no new manager or climate authority.
using System;
using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.Nodes.Biome
{
    /// <summary>Authors a bounded local weather override, optionally chained after other regions through Weather Output port 9.</summary>
    [Serializable]
    public sealed class WeatherRegionNode : Generator
    {
        /// <summary>World XZ bounds at instant zero, in metres; both extents must be positive.</summary>
        public Rect boundsMetres = new Rect(-1000, -1000, 2000, 2000);
        /// <summary>Inward blend in metres, zero for an intentional hard edge.</summary>
        [Min(0)] public float blendDistanceMetres = 100;
        /// <summary>Higher priority overrides lower; equal priorities sort by stable node ID.</summary>
        public int priority;
        /// <summary>World XZ front velocity in metres per simulated second, each component -100 through 100.</summary>
        public Vector2 velocityMetresPerSecond;
        /// <summary>Local temperature in Celsius, -100 through 70.</summary>
        [Range(-100, 70)] public float temperatureC = 15;
        /// <summary>Relative humidity, zero through one.</summary>
        [Range(0, 1)] public float humidity01 = 0.8f;
        /// <summary>Combined cloud cover, zero through one.</summary>
        [Range(0, 1)] public float cloudCover01 = 0.8f;
        /// <summary>Precipitation in mm/hour, zero through 1000; phase is selected from wet-bulb temperature by the existing manager.</summary>
        [Range(0, 1000)] public float precipitationMmPerHour = 5;
        /// <summary>Wind bearing toward world +Z at zero degrees and +X at 90, zero through 360.</summary>
        [Range(0, 360)] public float windBearingDeg = 270;
        /// <summary>Sustained wind speed in m/s, zero through 100.</summary>
        [Range(0, 100)] public float windSpeedMetresPerSecond = 8;
        /// <summary>Existing normalized fog control, zero through one; visibility also constrains camera fog.</summary>
        [Range(0, 1)] public float fogDensity01;
        /// <summary>Authored surface wetness, zero through one; storage and dry-down remain separate surface simulation.</summary>
        [Range(0, 1)] public float wetness01;
        /// <summary>Local visibility ceiling in metres, one through 1000000.</summary>
        [Range(1, 1000000)] public float visibilityMetres = 10000;
        private static readonly Port[] Ins = { new Port("Previous Regions", PortType.Weather, PortDirection.Input) };
        private static readonly Port[] Outs = { new Port("Weather Regions", PortType.Weather, PortDirection.Output) };
        public override string NodeName => "Weather Region";
        public override NodeCategory Category => NodeCategory.Biome;
        public override UCGraphDomainMask GraphDomains => UCGraphDomainMask.LightingClimateWeather;
        public override string Tooltip => "Local weather for this world's existing wind/cloud/rain/fog consumers. Bounds use world metres; motion reads the shared instant. Connect to Weather Output Regions.";
        public override Port[] GetInputs() => Ins;
        public override Port[] GetOutputs() => Outs;
        public override string SettingsDiagnostic =>
            !Valid(boundsMetres.x, -1e9f, 1e9f) || !Valid(boundsMetres.y, -1e9f, 1e9f) ||
            !Valid(boundsMetres.width, 0.001f, 1e9f) || !Valid(boundsMetres.height, 0.001f, 1e9f) ||
            !Valid(boundsMetres.xMax, -1e9f, 1e9f) || !Valid(boundsMetres.yMax, -1e9f, 1e9f) ||
            !Valid(blendDistanceMetres, 0, 1e9f) || !Valid(velocityMetresPerSecond.x, -100, 100) || !Valid(velocityMetresPerSecond.y, -100, 100) ||
            !Valid(temperatureC, -100, 70) || !Valid(humidity01, 0, 1) || !Valid(cloudCover01, 0, 1) ||
            !Valid(precipitationMmPerHour, 0, 1000) || !Valid(windBearingDeg, 0, 360) || !Valid(windSpeedMetresPerSecond, 0, 100) ||
            !Valid(fogDensity01, 0, 1) || !Valid(wetness01, 0, 1) || !Valid(visibilityMetres, 1, 1000000) ?
            "Weather regions require finite metre bounds, nonnegative blend, bounded velocity and weather values within their inspector ranges." : null;
        private static bool Valid(float value, float min, float max) => ClimateBaselineProduct.Finite(value) && value >= min && value <= max;

        /// <summary>Captures validated values into a new immutable region recipe without modifying the upstream product.</summary>
        /// <param name="outputIndex">Weather output slot zero.</param>
        /// <param name="tile">Evaluation tile supplying optional previous regions.</param>
        /// <param name="stop">Optional cooperative cancellation.</param>
        /// <returns>Snapshot-owned recipe, or null on cancellation.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Output slot is unsupported.</exception>
        /// <exception cref="InvalidOperationException">Settings are invalid, IDs repeat or the 64-region budget is exceeded.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex != 0) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            if (SettingsDiagnostic != null) throw new InvalidOperationException(SettingsDiagnostic);
            var previous = tile.PullInput(this, 0, stop) as WeatherRegionProduct;
            if (stop != null && stop.StopRequested) return null;
            float angle = windBearingDeg * Mathf.Deg2Rad;
            var region = new WeatherRegionProduct.Region(id, priority, boundsMetres, blendDistanceMetres, velocityMetresPerSecond,
                new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * windSpeedMetresPerSecond, temperatureC, humidity01, cloudCover01,
                precipitationMmPerHour, fogDensity01, wetness01, visibilityMetres);
            return previous != null ? previous.WithRegion(region) : new WeatherRegionProduct(region);
        }
    }
}
