// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWeatherSystemManager.Weather.cs   (partial of UCWeatherSystemManager)
//  Purpose: The single WeatherState source the lighting driver reads, mirroring
//           ResolveClockFor/DefaultClock exactly (W-04). Until the full pressure-
//           field weather sim publishes a live snapshot, the sky must still be driven
//           by the world's AUTHORED air - cloud cover, humidity, temperature - not by
//           a guess. This resolves those authored scalars into a WeatherState so
//           AtmosphereState.From has real inputs, and hands back a documented clear-sky
//           default when a world has no weather system at all.
//
//  WHY HERE, AND WHY ONE OWNER
//  ---------------------------
//  UCLightingSystemManager already reads the clock through ResolveClockFor so it never
//  declares a latitude of its own. The weather it needs must come the same way, from
//  the same owner, or the sky and the phenology would read two different skies. This is
//  that accessor: one place the authored air becomes a snapshot, so nothing downstream
//  re-derives it a second way. (Matches the ResolveClockFor / DefaultClock pattern in
//  UCWeatherSystemManager.cs.)
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        // The single owned stateful smoother. Null until the first live tick; one
        // instance for the manager's whole life, so no per-frame allocation. (W-04)
        private WeatherMachine _weatherMachine;
        private bool _weatherMachineReady;
        private UCWorldGraphBindings graphBindings; // UC-T05-04: resolved once on enable/explicit acceptance, never searched in a frame loop.
        private ClimateBaselineProduct earlyClimate; // UC-T08-05: accepted immutable baseline, never the live authored graph.
        private readonly System.Guid looseEnvironmentId = System.Guid.NewGuid();
        private WeatherCellField regionalField;
        private int regionalSeed;
        private float regionalNorth;

        // UC-T08-04/05: exact authored values distinguish climate edits from live appearance edits without a competing revision owner.
        private ClimateAuthoringState cachedClimateAuthoring;
        private bool hasClimateAuthoring;
        private struct ClimateAuthoringState
        {
            internal Vector4 Thermal, Hydrology, Season, Geography;
            internal float LongitudeSpan;
            internal bool DeriveTemperature;
            internal bool Equals(ClimateAuthoringState other) => Thermal.Equals(other.Thermal) && Hydrology.Equals(other.Hydrology) &&
                Season.Equals(other.Season) && Geography.Equals(other.Geography) && LongitudeSpan == other.LongitudeSpan && DeriveTemperature == other.DeriveTemperature;
        }
        private ClimateAuthoringState CaptureClimateAuthoring() => new ClimateAuthoringState {
            Thermal = new Vector4(temperatureCelsius, equatorTemperatureCelsius, lapseRatePerKm, annualTempHalfAmplitudeCelsius),
            Hydrology = new Vector4(diurnalTempRangeCelsius, continentality, annualPrecipitationMm, precipitationSeasonality),
            Season = new Vector4(wettestSeasonPhase, latitudeDeg, latitudeSpanDeg, humidity),
            Geography = new Vector4(longitudeDeg, clockReferenceMeridianDeg, axialTiltDeg, northBearingDeg),
            LongitudeSpan = longitudeSpanDeg, // UC-T08-10: civil display edits never invalidate climate or placement.
            DeriveTemperature = deriveTemperatureFromLatitude
        };

        /// <summary>UC-T08-05: enables the existing seeded regional pressure/front/cloud model; off preserves authored weather.</summary>
        [Tooltip("Use this world's seeded regional pressure, fronts, cloud decks and wind. No terrain or placement rebuild is scheduled.")]
        public bool enableRegionalWeather;
        /// <summary>Seed for this world's regional pressure lattice; independent of other worlds and static random state.</summary>
        [Tooltip("Deterministic regional weather seed for this UC world.")]
        public int regionalWeatherSeed = 2024;

        internal void BindEnvironmentGraph(UCWorldGraphBindings source)
        {
            if (source != null && !ReferenceEquals(source.Owner, GetComponentInParent<UniverseCreatorObject>(true)))
                throw new System.ArgumentException("Environment graph belongs to another UC world.", nameof(source));
            graphBindings = source;
            earlyClimate = source != null ? source.CaptureClimateBaseline() : null;
            ResetRegionalSampling(); // UC-T08-06: accepted replacement never reuses endpoints from previous geography/settings.
            _weatherMachineReady = false; // UC-T08-05: explicit graph acceptance cannot retain a smoother from a previous environment.
            _weatherMachine = null;
            ResetSurfaceStores(); // UC-T08-09: accepted replacements cannot retain a previous climate's surface history.
            EnvironmentHistoryRevision++; // UC-T08-11: explicit environment replacement invalidates owned camera history.
        }

        /// <summary>Degrees Celsius added by the explicitly applied, terrain-independent baseline graph; zero when absent.</summary>
        /// <remarks>Dynamic weather never changes the climate input captured for terrain/ecology generation.</remarks>
        public float BaselineTemperatureBiasC => graphBindings != null ? graphBindings.SampleEnvironmentBias(true).TemperatureC : 0;

        /// <summary>Annual millimetres after the baseline graph's uniform rate bias (mm/hour) is integrated over a tropical year; clamped nonnegative.</summary>
        public float ResolvedAnnualPrecipitationMm => Mathf.Max(0, annualPrecipitationMm + (graphBindings != null ?
            graphBindings.SampleEnvironmentBias(true).PrecipitationMmPerHour * 24f * UniverseCreator.Ecology.EcoConstants.DaysPerTropicalYear : 0));

        /// <summary>
        /// The shared authored-air builder. Both the live and the non-live paths run
        /// through it so a snapshot the machine smooths can never drift from the
        /// snapshot the off-path returns - they start from the identical authored sky.
        /// </summary>
        private WeatherState.Builder AuthoredWeatherBuilder()
            => AuthoredWeatherBuilder(EnvironmentPosition());

        private Vector3 EnvironmentPosition() => graphBindings != null && graphBindings.EnvironmentSampleTarget != null ?
            graphBindings.EnvironmentSampleTarget.position : transform.position;

        private WeatherState.Builder AuthoredWeatherBuilder(Vector3 position)
        {
            if (!ClimateBaselineProduct.Finite(position.x) || !ClimateBaselineProduct.Finite(position.y) || !ClimateBaselineProduct.Finite(position.z) ||
                !ClimateBaselineProduct.Finite(temperatureCelsius) || !ClimateBaselineProduct.Finite(cloudCoverage) || !ClimateBaselineProduct.Finite(humidity) ||
                !ClimateBaselineProduct.Finite(rainIntensity) || !ClimateBaselineProduct.Finite(snowIntensity) || !ClimateBaselineProduct.Finite(fogDensity) ||
                !ClimateBaselineProduct.Finite(surfaceWetness) || !ClimateBaselineProduct.Finite(visibilityMetres) ||
                !ClimateBaselineProduct.Finite(latitudeDeg) || !ClimateBaselineProduct.Finite(northBearingDeg) || !ClimateBaselineProduct.Finite(continentality))
                throw new System.ArgumentOutOfRangeException(nameof(position), "Weather position and authored air inputs must be finite.");
            float cover = Mathf.Clamp01(cloudCoverage);
            float rh = Mathf.Clamp01(humidity);

            WeatherState.Builder b = new WeatherState.Builder();
            b.cloudCover = cover;
            b.cloudCoverLow = cover;   // one authored deck; combined cover == cloudCoverage
            b.cloudCoverMid = 0f;
            b.cloudCoverHigh = 0f;
            b.humidity = rh;
            b.relativeHumidity01 = rh;
            b.visibility = Mathf.Max(1, visibilityMetres); // UC-T08-05: a dry, unfogged authored sample still has a usable visibility ceiling.
            EnvironmentGraphBias bias = graphBindings != null ? graphBindings.SampleEnvironmentBiasAt(position) : default;
            double phase = ((DayOfYear - 1) + SecondsOfDay / ClockState.SecondsPerDay) / 365d;
            ClimateBaselineSample climate = earlyClimate != null ? earlyClimate.Sample(new Vector2(position.x, position.z), phase) :
                new ClimateBaselineSample(temperatureCelsius, Mathf.Clamp01(humidity), Vector2.zero);
            b.temperature = Mathf.Clamp(climate.TemperatureC + bias.TemperatureC, -100, 70);
            b.cloudCover = b.cloudCoverLow = Mathf.Clamp01(cover + bias.CloudCover + bias.LightingCloudCover);
            b.precipitationMmPerHour = Mathf.Max(0, bias.PrecipitationMmPerHour) + Mathf.Clamp01(rainIntensity + snowIntensity) * AtmoConstants.ConvectiveRainReferenceMmPerH;
            b.precipitation = b.precipitationMmPerHour;
            UCWindManager authoredWind = WindOrNull;
            b.windSpeed = Mathf.Clamp((earlyClimate != null ? climate.WindMetresPerSecond.magnitude :
                authoredWind != null ? authoredWind.speedMetresPerSecond : 0) + bias.WindMetresPerSecond, 0, 100);
            if (earlyClimate == null && authoredWind != null) b.windDirectionDeg = Mathf.Repeat(authoredWind.directionDegrees, 360);
            if (climate.WindMetresPerSecond.sqrMagnitude > 0) b.windDirectionDeg = Mathf.Repeat(Mathf.Atan2(climate.WindMetresPerSecond.x, climate.WindMetresPerSecond.y) * Mathf.Rad2Deg, 360);
            b.fogDensity = Mathf.Clamp01(fogDensity + bias.FogDensity);
            b.wetness = Mathf.Clamp01(surfaceWetness);
            b.snowDepthM = Mathf.Max(0, bias.SnowDepthMetres);
            b.stormActivity = Mathf.Clamp01(bias.StormActivity);
            if (enableRegionalWeather) ApplyRegionalWeather(ref b, position, climate, bias.WindMetresPerSecond);
            activeWeatherRegions = graphBindings != null ? graphBindings.CaptureWeatherRegions() : null;
            activeWeatherRegions?.Apply(ref b, new Vector2(position.x, position.z), WorldInstantSeconds); // UC-T08-06: ordered local overrides never write long-term climate.
            b.precipitation = b.precipitationMmPerHour = Mathf.Clamp(b.precipitationMmPerHour, 0, 1000);
            b.dewPointC = AtmoMath.DewPointC(b.temperature, b.relativeHumidity01);
            b.wetBulbC = AtmoMath.WetBulbC(b.temperature, b.relativeHumidity01);
            // UC-T08-08: cloud planes use this finite world's fixed datum, never the moving camera's altitude.
            float cloudDatum = graphBindings != null ? graphBindings.Owner.transform.position.y : renderWorld != null ? renderWorld.transform.position.y : transform.position.y;
            if (!ClimateBaselineProduct.Finite(cloudDatum)) throw new System.ArgumentOutOfRangeException(nameof(position), "Cloud world datum must be finite.");
            b.cloudBaseM = cloudDatum + AtmoMath.LiftingCondensationLevelM(b.temperature, b.dewPointC);
            b.cloudBaseMidM = cloudDatum + AtmoConstants.CloudMidLayerAltitudeM;
            b.cloudBaseHighM = cloudDatum + AtmoConstants.CloudHighLayerAltitudeM;
            if (b.precipitationMmPerHour > 0)
                b.precipitationType = WeatherPrecipType.Classify(b.wetBulbC);
            b.gustSpeed = b.windSpeed * (authoredWind != null ? authoredWind.GustAtWorldInstant(WorldInstantSeconds) : 1); // UC-T08-06: physical, particle and view wind read the same gust phase.
            // UC-T08-05: exposed rain has an immediate material film; stored accumulation/dry-down is the separate UC-T08-09 task.
            if (b.precipitationType != WeatherPrecipType.PrecipKind.Snow)
                b.wetness = Mathf.Max(b.wetness, Mathf.Clamp01(b.precipitationMmPerHour / AtmoConstants.WetnessReferenceRainMmPerH));
            if (enableRegionalWeather || b.fogDensity > 0 || b.precipitationMmPerHour > 0)
                b.visibility = Mathf.Min(b.visibility, WeatherVisibility.VisibilityMetres(b.fogDensity, b.precipitationMmPerHour,
                    b.precipitationType == WeatherPrecipType.PrecipKind.Snow ? 1 : 0));
            return b;
        }

        // UC-T08-05: reuse existing regional mathematics; the rain conversion is a bounded visual/simulation parameterization, not hydrological certification.
        private void ApplyRegionalWeather(ref WeatherState.Builder b, Vector3 position, ClimateBaselineSample climate, float windBias)
        {
            RegionalReading reading = SampleRegionalReading(position); // UC-T08-06: at most eight cached local endpoints, independent of world extent.
            WeatherState regional = reading.State;
            b.pressureHpa = regional.pressureHpa; b.pressureTrendHpaPerHour = regional.pressureTrendHpaPerHour;
            b.frontKind = regional.frontKind; b.stabilityLapseCPerKm = regional.stabilityLapseCPerKm;
            float dew = Mathf.Min(b.temperature, WeatherHumidity.AirMassDewPointC(continentality, reading.MoistureBoost));
            b.relativeHumidity01 = b.humidity = Mathf.Clamp01(AtmoMath.RelativeHumidity01(b.temperature, dew) * (earlyClimate != null ? climate.Moisture01 * 2 : 1));
            b.cloudCoverLow = Mathf.Clamp01(b.cloudCoverLow + regional.cloudCoverLow + regional.cloudCover);
            b.cloudCoverMid = regional.cloudCoverMid; b.cloudCoverHigh = regional.cloudCoverHigh;
            b.cloudCover = CloudCover.CombinedCover01(b.cloudCoverLow, b.cloudCoverMid, b.cloudCoverHigh);
            b.cloudType = regional.cloudType;
            Vector2 velocity = regionalField.CompassToWorldXZ(WeatherFront.SurfaceFromBalancedCompassMPerS(
                WeatherFront.BalancedWindFromGradientCompassMPerS(reading.Wind, latitudeDeg,
                    AtmoMath.AirDensityKgPerM3(b.temperature, regional.pressureHpa)), latitudeDeg)) + climate.WindMetresPerSecond;
            b.windSpeed = Mathf.Clamp(velocity.magnitude + windBias, 0, 100);
            if (velocity.sqrMagnitude > 0) b.windDirectionDeg = Mathf.Repeat(Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg, 360);
            b.stormActivity = Mathf.Clamp01(b.stormActivity + WeatherStorm.Activity01(regional.cloudCover, reading.Depth));
            b.precipitationMmPerHour = Mathf.Min(1000, b.precipitationMmPerHour + AtmoConstants.ConvectiveRainReferenceMmPerH *
                b.relativeHumidity01 * Mathf.Clamp01(Mathf.Max(regional.cloudCover, reading.Depth / 20)) * b.cloudCover);
            b.precipitation = b.precipitationMmPerHour;
        }

        /// <summary>Samples this world's local environment without moving its shared observer or mutating terrain/placement.</summary>
        /// <param name="positionMetres">Finite world position in metres.</param>
        /// <returns>Immutable local weather, clock astronomy and lighting biases with world identity, shared instant and accepted graph revisions;
        /// the shared observer uses its current smoothed weather sample. Later seeks/graph replacements do not mutate this value.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">The world position is nonfinite.</exception>
        /// <remarks>Main thread; regional field ownership stays with this manager. Additional cameras never replace another world's sample.</remarks>
        public EnvironmentSampleProduct SampleEnvironmentAt(Vector3 positionMetres)
        {
            if (!ClimateBaselineProduct.Finite(positionMetres.x) || !ClimateBaselineProduct.Finite(positionMetres.y) || !ClimateBaselineProduct.Finite(positionMetres.z))
                throw new System.ArgumentOutOfRangeException(nameof(positionMetres));
            // UC-T08-07: capture all consumers' astronomy and artistic biases in the same main-thread sampling call.
            ClockState clock = ResolveClockAt(positionMetres);
            EnvironmentGraphBias lightingBias = graphBindings != null ? graphBindings.SampleEnvironmentBiasAt(positionMetres) : default;
            WeatherState local = positionMetres == EnvironmentPosition() ? ResolveWeather() : AuthoredWeatherBuilder(positionMetres).Build();
            local = ApplySurfaceResponse(local, positionMetres, clock); // UC-T08-09: one captured result feeds all material/simulation recipients.
            return new EnvironmentSampleProduct(graphBindings != null ? graphBindings.Owner.GenerationScheduler.CurrentRun.WorldId : looseEnvironmentId,
                positionMetres, WorldInstantSeconds, graphBindings != null ? graphBindings.BaselineClimateRevision : 0,
                graphBindings != null ? graphBindings.LightingRevision : 0,
                local,
                clock, lightingBias.Turbidity, lightingBias.ExposureEv, lightingBias.SkyTint, lightingBias.AmbientScale);
        }

        // UC-T08-05: the existing wind manager remains the only upload owner; authored saved wind survives runtime control.
        private void RefreshWeatherWind()
        {
            UCWindManager target = WindOrNull;
            if (target == null) return;
            if (!windEnabled || !weatherDrivesWind) { target.ClearRuntimeWeatherWind(); return; }
            WeatherState state = ResolveWeather(); float angle = state.windDirectionDeg * Mathf.Deg2Rad;
            target.SetRuntimeWeatherWind(new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)), state.windSpeed);
        }

        /// <summary>
        /// The weather snapshot governing a transform's world: this manager's authored
        /// air when there is a weather system, otherwise the documented clear-sky
        /// default. What <c>UCLightingSystemManager</c>'s driver reads so it never
        /// invents its own cloud cover or humidity. Mirrors <see cref="ResolveClockFor"/>.
        /// </summary>
        public static WeatherState ResolveWeatherFor(Transform context)
        {
            UCWeatherSystemManager weather = FindForWorld(context);
            return weather != null ? weather.ResolveWeather() : DefaultWeather();
        }

        /// <summary>
        /// This world's authored/accepted local weather, optionally driven by the existing regional pressure model.
        /// With enableLiveWeatherSim, the existing WeatherMachine smooths the target at the shared observer.
        /// Unbound graphs and disabled regional simulation retain authored values; no terrain generation is invoked.
        /// </summary>
        public WeatherState ResolveWeather()
        {
            if (enableLiveWeatherSim && _weatherMachineReady)
                return _weatherMachine.Current;

            return AuthoredWeatherBuilder().Build();
        }

        /// <summary>
        /// The same accepted/authored/regional target the direct path reads, with shared dew-point and wet-bulb mathematics.
        /// </summary>
        private WeatherState BuildRawWeatherState()
        {
            WeatherState.Builder b = AuthoredWeatherBuilder();
            b.dewPointC = AtmoMath.DewPointC(b.temperature, b.relativeHumidity01);
            b.wetBulbC = AtmoMath.WetBulbC(b.temperature, b.relativeHumidity01);
            return b.Build();
        }

        /// <summary>
        /// Advances the one owned machine against the freshest authored snapshot on the
        /// world clock. Called once per clock tick from <c>Update()</c> while the live sim
        /// is on; ticked with simulated seconds, never frame time, so a saved world
        /// reopened at the same time lands on the same sky (C-06). Toggling the sim off
        /// drops the ready flag so a later re-enable re-seeds from the current authored sky
        /// rather than easing out of a stale state. Public so a deterministic test (and,
        /// later, an explicit external driver) can step it; the manager calls it itself
        /// each tick from <c>Update()</c>.
        /// </summary>
        /// <param name="simSeconds">Finite nonnegative absolute simulated seconds; backwards stepping resets the existing smoother.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Time is nonfinite or negative.</exception>
        public void TickLiveWeather(double simSeconds)
        {
            if (double.IsNaN(simSeconds) || double.IsInfinity(simSeconds) || simSeconds < 0) throw new System.ArgumentOutOfRangeException(nameof(simSeconds));
            if (!enableLiveWeatherSim)
            {
                _weatherMachineReady = false;
                return;
            }

            if (_weatherMachine == null) _weatherMachine = new WeatherMachine();

            WeatherState raw = BuildRawWeatherState();
            _weatherMachine.Tick(in raw, simSeconds);
            _weatherMachineReady = true;
        }


        /// <summary>
        /// The documented fallback weather when a world has no weather system, built the
        /// same way as <see cref="ResolveWeather"/> so it cannot drift from it: a clear,
        /// lightly-humid, calm day. Pairs with <see cref="DefaultClock"/> (latitude 45,
        /// midsummer noon), so a lighting rig dropped loose into a scene still lights up
        /// against a real sky instead of a black one.
        /// </summary>
        public static WeatherState DefaultWeather()
        {
            WeatherState.Builder b = new WeatherState.Builder();
            b.cloudCover = 0f;
            b.cloudCoverLow = 0f;
            b.cloudCoverMid = 0f;
            b.cloudCoverHigh = 0f;
            b.humidity = 0.5f;
            b.relativeHumidity01 = 0.5f;
            b.temperature = 15f;
            return b.Build();
        }
    }
}
