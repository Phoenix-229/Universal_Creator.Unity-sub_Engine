// UC-T08-03: exercise actual early graph publication and detached terrain readers, not a second climate simulation.
using System;
using System.Threading;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Biome;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestClimateBaselineContracts()
        {
            var root = new GameObject("UC early climate acceptance"); root.SetActive(false);
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater);
            var environment = ScriptableObject.CreateInstance<Graph>(); environment.SetDomain(UCGraphDomain.LightingClimateWeather);
            var context = new SnapshotRetirementContext(); SynchronizationContext previous = SynchronizationContext.Current;
            GenerationSnapshot first = null, second = null;
            TileData tile = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = main; world.worldSizeKm = 0.032f; world.chunkCountPerSide = 1;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = environment;
                var vegetation = root.AddComponent<UCVegetationSystemManager>(); vegetation.enabled = false; // UC-T08-04: placement and appearance have distinct revisions.
                var baseline = new ClimateBaselineNode { id = "baseline", temperatureC = 10, moisture01 = 0.4f, windBearingDeg = 90, windSpeedMetresPerSecond = 8 };
                var output = new WeatherOutputNode { id = "early-weather" };
                environment.nodes.Add(baseline); environment.nodes.Add(output);
                environment.connections.Add(new Connection(baseline.id, 0, output.id, 8));
                var reader = new ClimateBaselineInputNode { id = "early-climate-reader" };
                var unrelated = new ConstantNode { id = "unrelated-height", value = 0.7f };
                var heightOutput = new HeightOutputNode { id = "climate-dependent-output" };
                main.nodes.Add(reader); main.nodes.Add(unrelated); main.nodes.Add(heightOutput);
                main.connections.Add(new Connection(reader.id, 0, heightOutput.id, 0));
                reader.CaptureDependencies(null, NodeExecutionDomain.Worker);
                tile = MemoryTestTile(world.ProductMemory);
                var missing = (Matrix)reader.Generate(1, tile, null);
                Check(missing.arr[0] == 0.5f && bindings.BaselineClimateRevision == 0,
                    "UC-T08-03 missing baseline has an explicit stable fallback without generating terrain or climate implicitly.");
                reader.allowMissingBaseline = false; bool refused = false;
                try { reader.CaptureDependencies(null, NodeExecutionDomain.Worker); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "UC-T08-03 strict early climate readers report missing applied baseline.");
                reader.allowMissingBaseline = true;
                GenerationRunId run = world.GenerationScheduler.CurrentRun;
                Check(WaitVegetationRun(bindings.RunLightingAsync(true), context) && bindings.BaselineClimateRevision == 1 &&
                    bindings.LightingRevision == 0 && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T08-03 climate-only Weather Output publishes before any accepted ground without restarting terrain.");
                first = GenerationSnapshot.Capture(main, world, true);
                var firstReader = (ClimateBaselineInputNode)first.Graph.GetNodeById(reader.id);
                var temperature = (Matrix)firstReader.Generate(0, tile, null);
                Check(Mathf.Abs(temperature.arr[0] - 0.5f) < 1e-6f,
                    "UC-T08-03 the real terrain snapshot reads accepted Celsius climate through its declared normalized field.");
                ulong originalReaderHash = firstReader.contentHash, unrelatedHash = first.Graph.GetNodeById(unrelated.id).contentHash;
                ulong originalOutputHash = first.Graph.GetNodeById(heightOutput.id).contentHash;
                baseline.temperatureC = 25;
                Check(Mathf.Abs(((Matrix)firstReader.Generate(0, tile, null)).arr[0] - 0.5f) < 1e-6f,
                    "UC-T08-03 pending authored climate edits cannot mutate a captured terrain worker.");
                Check(WaitVegetationRun(bindings.RunLightingAsync(true), context), "UC-T08-03 explicit baseline replacement succeeds while an older terrain snapshot is retained.");
                second = GenerationSnapshot.Capture(main, world, true);
                Check(second.Graph.GetNodeById(reader.id).contentHash != originalReaderHash &&
                    second.Graph.GetNodeById(heightOutput.id).contentHash != originalOutputHash &&
                    second.Graph.GetNodeById(unrelated.id).contentHash == unrelatedHash,
                    "UC-T08-03 accepted baseline edits invalidate the actual reader and downstream only.");
                Check(Mathf.Abs(((Matrix)firstReader.Generate(0, tile, null)).arr[0] - 0.5f) < 1e-6f &&
                    Mathf.Abs(((Matrix)second.Graph.GetNodeById(reader.id).Generate(0, tile, null)).arr[0] - 0.75f) < 1e-6f,
                    "UC-T08-03 old and replacement snapshots keep independent immutable climate and product-memory leases.");
                ulong stable = ((ClimateBaselineInputNode)second.Graph.GetNodeById(reader.id)).GetPreparedInputHash();
                weather.cloudCoverage = 1; weather.temperatureCelsius = -50; weather.Advance(10000);
                Check(bindings.CaptureClimateBaseline().ContentHash == stable && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T08-03 live weather and clock edits never rewrite long-term climate or restart terrain.");

                long placementRevision = vegetation.PlacementRevision, appearanceRevision = vegetation.AppearanceRevision;
                Check(WaitVegetationRun(bindings.RunLightingAsync(), context) && vegetation.PlacementRevision == placementRevision &&
                    vegetation.AppearanceRevision > appearanceRevision && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T08-04 dynamic environment application refreshes appearance without invalidating stable placement or terrain.");
                weather.SetCalendarTime(2, 120, 12);
                Check(vegetation.PlacementRevision == placementRevision,
                    "UC-T08-04 time seeks refresh the standing world without changing placement inputs.");
                // UC-T08-04/05: Unity SendMessage does not dispatch to this intentionally inactive fixture; invoke the real validation callback.
                var validateWeather = typeof(UCWeatherSystemManager).GetMethod("OnValidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (validateWeather == null) throw new MissingMethodException(nameof(UCWeatherSystemManager), "OnValidate");
                validateWeather.Invoke(weather, null);
                long inspectorPlacement = vegetation.PlacementRevision, inspectorAppearance = vegetation.AppearanceRevision;
                weather.rainIntensity = 0.7f; weather.fogDensity = 0.3f; weather.cloudCoverage = 0.4f;
                validateWeather.Invoke(weather, null);
                Check(vegetation.PlacementRevision == inspectorPlacement && vegetation.AppearanceRevision > inspectorAppearance,
                    "UC-T08-04/05 live weather inspector validation changes appearance without incrementing placement invalidation.");
                placementRevision = inspectorPlacement;
                var modifier = new TerrainClimateModifierNode { id = "accepted-topography", aspectWarmingC = 0 };
                environment.nodes.Add(modifier); environment.connections.Add(new Connection(modifier.id, 0, output.id, 1));
                Check(!WaitVegetationRun(bindings.RunLightingAsync(true), context) && bindings.BaselineClimateRevision == 2 &&
                    bindings.LightingStatus.Contains("baseline climate cannot depend on final ground"),
                    "UC-T08-04 compiled early output dependencies reject terrain-informed climate without replacing accepted baseline.");
                environment.connections.RemoveAll(edge => edge.sourceNodeId == modifier.id); environment.nodes.Remove(modifier);
                var groundTile = new TileData(new Coord(0, 0), new CoordRect(0, 0, 33, 33), new Vector3(-16, 0, -16), new Vector3(32, 100, 32), 100);
                try
                {
                    groundTile.AttachMemory(world.ProductMemory); groundTile.AssignRun(run);
                    var heights = new float[33, 33]; for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.5f;
                    var ground = new RiverChannelSnapshot(run, "climate-topography", new Rect(-16, -16, 32, 32), Array.Empty<ChannelGraphPublication>(), heights, 0, 100);
                    groundTile.BindWorldGraphInputs(main, ground, WorldGraphWaterInput.Capture(world), null);
                    var correction = (Matrix)modifier.Generate(0, groundTile, null);
                    Check(Mathf.Abs(correction.arr[0] + 0.325f) < 1e-6f && vegetation.PlacementRevision == placementRevision &&
                        world.GenerationScheduler.CurrentRun.Equals(run),
                        "UC-T08-04 committed altitude uses the existing Celsius/km lapse law without editing terrain or placement.");
                    modifier.coastalTemperatureBiasC = 5; modifier.coastalPrecipitationMmPerHour = 3;
                    Check(Mathf.Abs(((Matrix)modifier.Generate(0, groundTile, null)).arr[0] + 0.325f) < 1e-6f &&
                        ((Matrix)modifier.Generate(1, groundTile, null)).arr[0] == 0,
                        "UC-T08-04 absent accepted ocean geometry cannot invent a coastal temperature or rainfall correction.");
                }
                finally { groundTile.ReleaseProductMemory(); }

                var climate = bindings.CaptureClimateBaseline();
                var low = new ClimateRegionNode { id = "low-region", priority = 1, boundsMetres = new Rect(-100, -100, 200, 200), blendDistanceMetres = 20, temperatureC = 0 };
                var high = new ClimateRegionNode { id = "high-region", priority = 2, boundsMetres = low.boundsMetres, blendDistanceMetres = 20, temperatureC = -10 };
                low.PrepareMainThread(); high.PrepareMainThread();
                var a = low.CaptureBand(low.boundsMetres, 20, low.priority, null);
                var b = high.CaptureBand(high.boundsMetres, 20, high.priority, null);
                var regions = climate.WithRegion(a).WithRegion(b);
                var reverse = climate.WithRegion(b).WithRegion(a);
                Check(regions.ContentHash == reverse.ContentHash && regions.Sample(Vector2.zero, 0).TemperatureC == -10 &&
                    regions.Sample(new Vector2(200, 0), 0).TemperatureC == 25,
                    "UC-T08-03 overlapping region priorities are order-independent and outside regions retains baseline.");
                Check(regions.Sample(new Vector2(-100, 0), 0).TemperatureC == 25 &&
                    Mathf.Abs(regions.Sample(new Vector2(-99.999f, 0), 0).TemperatureC - 25) < 0.001f,
                    "UC-T08-03 inward metre blending remains continuous at regional boundaries.");
                Matrix mask = tile.CreateMatrix(); for (int i = 0; i < mask.arr.Length; i++) mask.arr[i] = 0;
                var masked = climate.WithRegion(low.CaptureBand(low.boundsMetres, 0, low.priority, mask));
                for (int i = 0; i < mask.arr.Length; i++) mask.arr[i] = 1;
                Check(masked.Sample(new Vector2(mask.worldPos.x, mask.worldPos.z), 0).TemperatureC == 25,
                    "UC-T08-03 regional masks are frozen and never alias mutable upstream fields.");
                var seasonal = new ClimateBaselineNode { id = "seasonal", seasonalTemperatureC = AnimationCurve.Linear(0, 0, 1, 10) };
                seasonal.PrepareMainThread(); var seasonProduct = (ClimateBaselineProduct)seasonal.Generate(0, tile, null);
                seasonal.seasonalTemperatureC = AnimationCurve.Linear(0, 100, 1, 100);
                Check(Mathf.Abs(seasonProduct.Sample(Vector2.zero, 0.5).TemperatureC - 20) < 1e-6f &&
                    seasonProduct.Sample(Vector2.zero, 0).TemperatureC == seasonProduct.Sample(Vector2.zero, 1).TemperatureC,
                    "UC-T08-03 captured seasonal curves survive authored replacement and repeat at model-year boundaries.");
                foreach (float extent in new[] { 1f, 1000f, 1000000f })
                    Check(climate.Sample(new Vector2(-extent, extent), 0).TemperatureC == 25,
                        "UC-T08-03 sea-level climate remains independent of terrain resolution and world scale " + extent + " m.");
                bool nonfinite = false; try { climate.Sample(new Vector2(float.NaN, 0), 0); } catch (ArgumentOutOfRangeException) { nonfinite = true; }
                Check(nonfinite && new ClimateBaselineNode { temperatureC = float.NaN }.SettingsDiagnostic != null &&
                    new ClimateRegionNode { blendDistanceMetres = -1 }.SettingsDiagnostic != null,
                    "UC-T08-03 nonfinite climate and invalid region inputs fail at their boundary.");
                var legacy = new TemperatureMapNode { equatorTemperature = 0.73f, poleTemperature = 0.19f, lapseRate = 0.4f };
                var loaded = JsonUtility.FromJson<TemperatureMapNode>(JsonUtility.ToJson(legacy));
                Check(loaded.equatorTemperature == legacy.equatorTemperature && loaded.poleTemperature == legacy.poleTemperature &&
                    loaded.lapseRate == legacy.lapseRate && output.GetInputs()[0].type == PortType.Matrix && output.GetInputs()[7].type == PortType.Matrix &&
                    output.GetInputs()[8].type == PortType.Climate && (int)PortType.Vector == 11,
                    "UC-T08-03 existing climate node values and weather wire indices survive additive typed climate migration.");
                tile.outputs.climateBaseline = climate; tile.outputs.Clear();
                Check(tile.outputs.climateBaseline == null, "UC-T08-03 recycled output bags clear typed climate ownership.");
            }
            finally
            {
                first?.Release(); second?.Release(); tile?.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(root); context.Drain();
                UnityEngine.Object.DestroyImmediate(main); UnityEngine.Object.DestroyImmediate(environment);
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        // UC-T08-05: use the existing pressure and native wind/particle owners in two independent worlds.
        private static void TestRegionalEnvironmentConsumers()
        {
            var first = new GameObject("UC regional weather world A"); first.SetActive(false);
            var second = new GameObject("UC regional weather world B"); second.SetActive(false);
            var mainA = ScriptableObject.CreateInstance<Graph>(); mainA.SetDomain(UCGraphDomain.TerrainWater);
            var mainB = ScriptableObject.CreateInstance<Graph>(); mainB.SetDomain(UCGraphDomain.TerrainWater);
            try
            {
                var worldA = first.AddComponent<UniverseCreatorObject>(); worldA.graph = mainA; worldA.enabled = false;
                var worldB = second.AddComponent<UniverseCreatorObject>(); worldB.graph = mainB; worldB.enabled = false;
                var bindingsA = first.AddComponent<UCWorldGraphBindings>(); var bindingsB = second.AddComponent<UCWorldGraphBindings>();
                var weatherA = first.AddComponent<UCWeatherSystemManager>(); weatherA.enabled = false;
                var weatherB = second.AddComponent<UCWeatherSystemManager>(); weatherB.enabled = false;
                weatherA.BindEnvironmentGraph(bindingsA); weatherB.BindEnvironmentGraph(bindingsB);
                weatherA.enableRegionalWeather = true; weatherA.regionalWeatherSeed = 17;
                weatherB.enableRegionalWeather = true; weatherB.regionalWeatherSeed = 91;
                weatherA.SetTime(172, 12); weatherB.SetTime(172, 12);
                var runA = worldA.GenerationScheduler.CurrentRun; var runB = worldB.GenerationScheduler.CurrentRun;
                EnvironmentSampleProduct a = weatherA.SampleEnvironmentAt(Vector3.zero), b = weatherB.SampleEnvironmentAt(Vector3.zero);
                Check(a.WorldId == runA.WorldId && b.WorldId == runB.WorldId && a.WorldId != b.WorldId &&
                    a.WorldInstantSeconds == b.WorldInstantSeconds && a.ClimateRevision == 0,
                    "UC-T08-05 regional samples carry each actual world identity and shared instant without a global weather singleton.");
                bool differs = false;
                foreach (float offset in new[] { 0f, 75000f, 200000f })
                {
                    var position = new Vector3(offset, 0, -offset);
                    var local = weatherA.SampleEnvironmentAt(position).Weather;
                    var other = weatherB.SampleEnvironmentAt(position).Weather;
                    differs |= Mathf.Abs(local.pressureHpa - other.pressureHpa) > 0.00001f || Mathf.Abs(local.windSpeed - other.windSpeed) > 0.00001f;
                    Check(!float.IsNaN(local.pressureHpa) && local.windSpeed >= 0 && local.windSpeed <= 100 && local.cloudCover >= 0 &&
                        local.cloudCover <= 1 && local.precipitationMmPerHour >= 0 && local.precipitationMmPerHour <= 1000 && local.visibility >= 1,
                        "UC-T08-05 actual regional pressure/wind/cloud/rain/visibility consumers remain finite at " + offset + " metres.");
                }
                Check(differs && worldA.GenerationScheduler.CurrentRun.Equals(runA) && worldB.GenerationScheduler.CurrentRun.Equals(runB),
                    "UC-T08-05 seeded regional differences leave both terrain runs unchanged.");
                weatherA.enableRegionalWeather = false; weatherA.temperatureCelsius = 20; weatherA.humidity = 0.5f;
                weatherA.rainIntensity = 1; weatherA.fogDensity = 0.8f;
                WeatherState rain = weatherA.SampleEnvironmentAt(Vector3.zero).Weather;
                Check(rain.precipitationMmPerHour == AtmoConstants.ConvectiveRainReferenceMmPerH && rain.precipitationType == WeatherPrecipType.PrecipKind.Rain &&
                    rain.visibility > 0 && rain.visibility < weatherA.visibilityMetres,
                    "UC-T08-05 authored rain/fog controls execute through the same local precipitation and visibility sample.");
                var wind = first.AddComponent<UCWindManager>(); wind.enabled = false; wind.gustiness = 0;
                wind.directionDegrees = 35; wind.speedMetresPerSecond = 3;
                wind.SetRuntimeWeatherWind(new Vector2(1, 0), 12);
                Check(wind.directionDegrees == 35 && wind.speedMetresPerSecond == 3 && wind.DirectionXZ == new Vector2(1, 0) && wind.CurrentSpeed == 12,
                    "UC-T08-05 runtime weather wind reaches existing consumers while preserving saved authored wind.");
                wind.ClearRuntimeWeatherWind();
                Check(Mathf.Abs(wind.DirectionXZ.x - Mathf.Sin(35 * Mathf.Deg2Rad)) < 1e-6f && wind.CurrentSpeed == 3,
                    "UC-T08-05 retiring runtime weather wind restores the existing authored owner.");
                wind.SetRuntimeWeatherWind(new Vector2(float.MaxValue, float.MaxValue), 10);
                Check(Mathf.Abs(wind.DirectionXZ.sqrMagnitude - 1) < 1e-5f,
                    "UC-T08-05 finite extreme runtime wind direction is normalized without overflow.");
                var particles = new GameObject("UC local precipitation consumer"); particles.transform.SetParent(first.transform, false);
                var emitter = particles.AddComponent<PrecipitationEmitter>();
                emitter.UpdatePrecipitation(rain.precipitationMmPerHour, false, Vector3.right * 12);
                var system = particles.GetComponent<ParticleSystem>();
                Check(system.emission.rateOverTime.constant > 0 && system.velocityOverLifetime.x.constant == 12,
                    "UC-T08-05 the existing native precipitation system receives local rainfall and world wind.");
                emitter.UpdatePrecipitation(0, false, Vector3.zero);
                Check(system.emission.rateOverTime.constant == 0, "UC-T08-05 dry local weather stops obsolete particle emission.");
                bool nonfinite = false; try { weatherA.SampleEnvironmentAt(new Vector3(float.NaN, 0, 0)); } catch (ArgumentOutOfRangeException) { nonfinite = true; }
                Check(nonfinite, "UC-T08-05 local environment sampling rejects a nonfinite observer position.");
                bool crossWorld = false; try { weatherA.BindEnvironmentGraph(bindingsB); } catch (ArgumentException) { crossWorld = true; }
                Check(crossWorld && weatherA.SampleEnvironmentAt(Vector3.zero).WorldId == runA.WorldId,
                    "UC-T08-05 a foreign environment binding is refused before replacing this world's sample owner.");

                weatherA.temperatureCelsius = -10; weatherA.humidity = 1;
                WeatherState cold = weatherA.SampleEnvironmentAt(Vector3.zero).Weather;
                Check(cold.wetBulbC < 0 && cold.precipitationType == WeatherPrecipType.PrecipKind.Snow,
                    "UC-T08-05 direct authored and live-smoother paths receive the same wet-bulb snow classification.");
                weatherA.temperatureCelsius = 20; weatherA.rainIntensity = 1; weatherA.surfaceWetness = 0.8f;
                weatherB.enableRegionalWeather = false; weatherB.temperatureCelsius = -10; weatherB.rainIntensity = 0; weatherB.fogDensity = 0;
                weatherB.surfaceWetness = 0.2f;
                var cameraObjectA = new GameObject("UC environment camera A"); cameraObjectA.transform.SetParent(first.transform, false);
                var cameraObjectB = new GameObject("UC environment camera B"); cameraObjectB.transform.SetParent(second.transform, false);
                var cameraA = cameraObjectA.AddComponent<Camera>(); var cameraB = cameraObjectB.AddComponent<Camera>();
                bool originalFog = RenderSettings.fog; float originalDensity = RenderSettings.fogDensity;
                Vector4 originalWet = Shader.GetGlobalVector(WetnessCoupling.WetParamsId);
                try
                {
                    Check(!weatherA.BeginEnvironmentView(cameraB), "UC-T08-05 a foreign world's camera never receives this owner's render state.");
                    Check(weatherA.BeginEnvironmentView(cameraA) && RenderSettings.fog && RenderSettings.fogDensity > 0 &&
                        Shader.GetGlobalVector(WetnessCoupling.WetParamsId).x == 1,
                        "UC-T08-05 the actual owned view consumes sampled visibility and wetness through URP fog and shader recipients.");
                    weatherA.EndEnvironmentView(cameraA);
                    Check(weatherB.BeginEnvironmentView(cameraB) && !RenderSettings.fog &&
                        Shader.GetGlobalVector(WetnessCoupling.WetParamsId).x == 0.2f,
                        "UC-T08-05 a second world's dry camera receives its own independent environment.");
                    weatherB.EndEnvironmentView(cameraB);
                    Check(RenderSettings.fog == originalFog && RenderSettings.fogDensity == originalDensity &&
                        Shader.GetGlobalVector(WetnessCoupling.WetParamsId) == originalWet,
                        "UC-T08-05 ending camera views restores incoming fog and shader state without persistent cross-world writes.");
                }
                finally { weatherA.EndEnvironmentView(cameraA); weatherB.EndEnvironmentView(cameraB); }
                emitter.BindWeatherSource(weatherA); emitter.RefreshLocalWeather();
                Check(system.emission.rateOverTime.constant > 0, "UC-T08-05 the emitter's production adapter consumes its bound world's local rain.");
                emitter.BindWeatherSource(weatherB); emitter.RefreshLocalWeather();
                Check(system.emission.rateOverTime.constant == 0, "UC-T08-05 rebinding the emitter to a dry world clears the earlier world's rain.");
                emitter.BindWeatherSource(weatherA); emitter.RefreshLocalWeather(); emitter.BindWeatherSource(null);
                Check(system.emission.rateOverTime.constant == 0, "UC-T08-05 retiring a bound weather owner clears obsolete emission immediately.");
                bool invalidPrecipitation = false;
                try { emitter.UpdatePrecipitation(float.NaN, false, Vector3.zero); } catch (ArgumentOutOfRangeException) { invalidPrecipitation = true; }
                Check(invalidPrecipitation && system.emission.rateOverTime.constant == 0,
                    "UC-T08-05 nonfinite external precipitation is rejected before changing native particle modules.");

                // The existing volume registry establishes ownership on enable; neither disabled world generator is started.
                first.SetActive(true); second.SetActive(true);
                var lakeObjectA = new GameObject("UC local lake A"); lakeObjectA.transform.SetParent(first.transform, false);
                var lakeObjectB = new GameObject("UC local lake B"); lakeObjectB.transform.SetParent(second.transform, false);
                var lakeA = lakeObjectA.AddComponent<UCWaterVolume>(); var lakeB = lakeObjectB.AddComponent<UCWaterVolume>();
                lakeA.kind = UCWaterVolume.BodyKind.Lake; lakeB.kind = UCWaterVolume.BodyKind.Lake;
                lakeObjectA.AddComponent<MeshRenderer>(); lakeObjectB.AddComponent<MeshRenderer>();
                UCWaterWeatherCoupling.Apply(worldB, weatherB);
                var block = new MaterialPropertyBlock(); lakeObjectB.GetComponent<Renderer>().GetPropertyBlock(block);
                float frozenB = block.GetFloat(Shader.PropertyToID(UCWaterProps.Ice));
                UCWaterWeatherCoupling.Apply(worldA, weatherA); UCWaterWeatherCoupling.Calm(worldA);
                lakeObjectB.GetComponent<Renderer>().GetPropertyBlock(block);
                Check(frozenB == 1 && block.GetFloat(Shader.PropertyToID(UCWaterProps.Ice)) == frozenB,
                    "UC-T08-05 another world's apply/retirement cannot overwrite the actual per-body frozen-water shader state.");
                var emptyObject = new GameObject("UC empty environment world"); emptyObject.SetActive(false); emptyObject.transform.SetParent(first.transform, false);
                var emptyWorld = emptyObject.AddComponent<UniverseCreatorObject>(); emptyWorld.enabled = false;
                var nestedObject = new GameObject("UC nested weather world"); nestedObject.transform.SetParent(emptyObject.transform, false);
                var nestedWorld = nestedObject.AddComponent<UniverseCreatorObject>(); nestedWorld.enabled = false;
                nestedObject.AddComponent<UCWeatherSystemManager>().enabled = false;
                Check(UCWeatherSystemManager.FindForWorld(emptyObject.transform) == null,
                    "UC-T08-05 a world without weather cannot inherit a nested world's clock or environment.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(mainA); UnityEngine.Object.DestroyImmediate(mainB);
            }
        }
    }
}
