// UC-T08-06: production graph acceptance, bounded sampling, overlap rules and deterministic shared-clock gusts.
#if UNITY_EDITOR
using System;
using System.Threading;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Biome;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWeatherRegionTransitions()
        {
            var root = new GameObject("UC bounded weather regions"); root.SetActive(false);
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater);
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.LightingClimateWeather);
            var context = new SnapshotRetirementContext(); SynchronizationContext previous = SynchronizationContext.Current;
            TileData tile = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var world = root.AddComponent<UniverseCreatorObject>(); world.graph = main; world.enabled = false; world.autoRegenerate = false;
                world.worldSizeKm = 0.032f; world.chunkCountPerSide = 1;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false; weather.SeekWorldInstant(0);
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = graph;
                var low = new WeatherRegionNode { id = "weather-low", priority = 1, boundsMetres = new Rect(-100, -100, 200, 200), temperatureC = 5, blendDistanceMetres = 20 };
                var high = new WeatherRegionNode { id = "weather-high", priority = 2, boundsMetres = low.boundsMetres, temperatureC = 25, blendDistanceMetres = 20 };
                var output = new WeatherOutputNode(); graph.nodes.Add(high); graph.nodes.Add(low); graph.nodes.Add(output);
                graph.connections.Add(new Connection(high.id, 0, low.id, 0)); graph.connections.Add(new Connection(low.id, 0, output.id, 9));
                var run = world.GenerationScheduler.CurrentRun;
                Check(WaitVegetationRun(bindings.RunLightingAsync(false), context) && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T08-06 weather-only typed graph publishes through the existing dynamic acceptance without rebuilding terrain.");
                CheckClose(weather.SampleEnvironmentAt(Vector3.zero).Weather.temperature, 25,
                    "UC-T08-06 priority beats connection order in overlapping regions.");
                high.temperatureC = -10;
                CheckClose(weather.SampleEnvironmentAt(Vector3.zero).Weather.temperature, 25,
                    "UC-T08-06 pending graph edits cannot mutate the accepted immutable weather recipe.");
                var outside = weather.SampleEnvironmentAt(new Vector3(-100.01f, 0, 0)).Weather;
                var inside = weather.SampleEnvironmentAt(new Vector3(-99.99f, 0, 0)).Weather;
                Check(Mathf.Abs(outside.temperature - inside.temperature) < 0.01f && Mathf.Abs(outside.cloudCover - inside.cloudCover) < 0.001f,
                    "UC-T08-06 inward metre blending is continuous across the actual region edge.");
                high.blendDistanceMetres = low.blendDistanceMetres = 0;
                Check(WaitVegetationRun(bindings.RunLightingAsync(false), context), "UC-T08-06 zero-blend replacement is accepted explicitly.");
                CheckClose(weather.SampleEnvironmentAt(new Vector3(-100, 0, 0)).Weather.temperature, -10,
                    "UC-T08-06 zero blend includes the exact boundary as an intentional hard edge.");
                CheckClose(weather.SampleEnvironmentAt(new Vector3(-100.01f, 0, 0)).Weather.temperature, weather.temperatureCelsius,
                    "UC-T08-06 outside a hard region the authored environment is retained.");
                high.velocityMetresPerSecond = new Vector2(10, 0); high.blendDistanceMetres = 20;
                Check(WaitVegetationRun(bindings.RunLightingAsync(false), context), "UC-T08-06 moving authored fronts use the accepted recipe.");
                weather.SeekWorldInstant(30);
                CheckClose(weather.SampleEnvironmentAt(new Vector3(300, 0, 0)).Weather.temperature, -10,
                    "UC-T08-06 the authored front arrives at its shared-clock position without frame integration.");
                weather.SeekWorldInstant(0); weather.SeekWorldInstant(30);
                CheckClose(weather.SampleEnvironmentAt(new Vector3(300, 0, 0)).Weather.temperature, -10,
                    "UC-T08-06 backwards seeks and repeated jumps reproduce moving region weather.");
                weather.enableRegionalWeather = true; weather.SeekWorldInstant(123.25);
                Vector3 p = new Vector3(15001, 0, 15001); var a = weather.SampleEnvironmentAt(p).Weather;
                long samples = weather.RegionalEndpointSamples;
                for (int i = 0; i < 128; i++) weather.SampleEnvironmentAt(p + Vector3.right * (i * 0.1f));
                Check(samples == 8 && weather.RegionalEndpointSamples == samples,
                    "UC-T08-06 repeated moving local queries reuse eight endpoints without rescanning the pressure field.");
                weather.SeekWorldInstant(299.99); var before = weather.SampleEnvironmentAt(p).Weather;
                weather.SeekWorldInstant(300.01); var after = weather.SampleEnvironmentAt(p).Weather;
                Check(Mathf.Abs(before.pressureHpa - after.pressureHpa) < 0.02f && Mathf.Abs(before.windSpeed - after.windSpeed) < 0.02f,
                    "UC-T08-06 time-grid endpoints interpolate continuously across a simulation tick.");
                var edgeA = weather.SampleEnvironmentAt(new Vector3(19999.99f, 0, 15001)).Weather;
                var edgeB = weather.SampleEnvironmentAt(new Vector3(20000.01f, 0, 15001)).Weather;
                Check(Mathf.Abs(edgeA.pressureHpa - edgeB.pressureHpa) < 0.02f && Mathf.Abs(edgeA.windSpeed - edgeB.windSpeed) < 0.02f,
                    "UC-T08-06 movement across coarse spatial cell edges remains continuous.");
                weather.SeekWorldInstant(123.25); var repeated = weather.SampleEnvironmentAt(p).Weather;
                CheckClose(repeated.pressureHpa, a.pressureHpa, "UC-T08-06 cached pressure reproduces after distant and backwards queries.");
                weather.SetCalendarTime(9999, 365, 23.5f);
                var extreme = weather.SampleEnvironmentAt(p).Weather;
                Check(ClimateBaselineProduct.Finite(extreme.pressureHpa) && ClimateBaselineProduct.Finite(extreme.windSpeed),
                    "UC-T08-06 distant-year pressure/front samples remain finite without catch-up loops.");
                var wind = root.AddComponent<UCWindManager>(); wind.enabled = false; wind.gustiness = 1;
                float gust = wind.GustAtWorldInstant(weather.WorldInstantSeconds);
                Check(gust >= 0 && gust <= 2 && gust == wind.GustAtWorldInstant(weather.WorldInstantSeconds) &&
                    Mathf.Abs(gust - wind.GustAtWorldInstant(weather.WorldInstantSeconds + 0.5)) > 1e-5f,
                    "UC-T08-06 shared-clock gusts reproduce while paused and retain sub-second phase in year 9999.");
                bool invalid = false; weather.regionalSampleIntervalSeconds = float.NaN;
                try { weather.SampleEnvironmentAt(p); } catch (ArgumentOutOfRangeException) { invalid = true; }
                Check(invalid, "UC-T08-06 nonfinite scripted simulation cadence is rejected before field evaluation.");
                tile = MemoryTestTile(world.ProductMemory);
                low.priority = high.priority = 0; low.id = "a-weather"; high.id = "z-weather";
                var r0 = (WeatherRegionProduct)low.Generate(0, tile, null);
                var r1 = (WeatherRegionProduct)high.Generate(0, tile, null);
                // Capture order is deliberately reversed; ordinal ID is the tie rule independently of graph traversal.
                var bounds = new Rect(-100, -100, 200, 200);
                var product = new WeatherRegionProduct(WeatherRegionFixture("z", 0, bounds, 25)).WithRegion(WeatherRegionFixture("a", 0, bounds, 5));
                WeatherState.Builder air = default; product.Apply(ref air, Vector2.zero, 0);
                CheckClose(air.temperature, 25, "UC-T08-06 equal-priority overlap is ordered by stable ordinal node ID.");
                for (int i = 2; i < 64; i++) product = product.WithRegion(WeatherRegionFixture("bounded-" + i, i, bounds, 15));
                bool bounded = false; try { product.WithRegion(WeatherRegionFixture("overflow", 65, bounds, 15)); } catch (InvalidOperationException) { bounded = true; }
                Check(bounded && r0 != null && r1 != null && output.GetInputs()[8].type == PortType.Climate && output.GetInputs()[9].type == PortType.Weather,
                    "UC-T08-06 64-region cap and appended typed port preserve earlier climate wire indices.");
                bool duplicate = false; try { new WeatherRegionProduct(WeatherRegionFixture("a", 0, bounds, 15)).WithRegion(WeatherRegionFixture("a", 1, bounds, 15)); } catch (InvalidOperationException) { duplicate = true; }
                Check(duplicate && new WeatherRegionNode { blendDistanceMetres = -1 }.SettingsDiagnostic != null,
                    "UC-T08-06 invalid bounds/blends and duplicate recipe IDs fail explicitly.");
                WeatherState.Builder raw = default; raw.cloudCover = 0; var clear = raw.Build(); raw.cloudCover = 1; var cloudy = raw.Build();
                var machine = new WeatherMachine(20, 30); double instant = 3e11;
                machine.Tick(clear, instant); var halfway = machine.Tick(cloudy, instant + 10);
                Check(halfway.cloudCover > 0 && halfway.cloudCover < 1 && machine.Tick(clear, 0).cloudCover == 0,
                    "UC-T08-06 existing weather transitions retain double-time precision and reset on backwards stepping.");
            }
            catch (Exception ex) { Check(false, "UC-T08-06 regional transition exception: " + ex); }
            finally
            {
                tile?.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(main);
                context.Drain(); // UC-T08-06: consume queued snapshot retirement before restoring Unity's synchronization context.
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
        private static WeatherRegionProduct.Region WeatherRegionFixture(string id, int priority, Rect bounds, float temperature) =>
            new WeatherRegionProduct.Region(id, priority, bounds, 0, Vector2.zero, Vector2.zero, temperature, 0.5f, 0, 0, 0, 0, 10000);
    }
}
#endif
