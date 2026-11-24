// UC-T08-09: actual shared-clock storage and scoped material recipients, without terrain/scatter work.
using System;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Weather;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestSurfaceWeatherStores()
        {
            var root = new GameObject("UC surface weather stores"); root.SetActive(false);
            UniverseCreatorObject world = null;
            Vector4 incoming = Shader.GetGlobalVector(WetnessCoupling.SurfaceSnowId);
            try
            {
                world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var weather = root.AddComponent<UCWeatherSystemManager>();
                weather.simulateSurfaceResponse = true; weather.timeScale = 1; weather.pauseTime = false;
                weather.temperatureCelsius = 20; weather.diurnalTempRangeCelsius = 0; weather.rainIntensity = 1;
                var cameraObject = new GameObject("UC surface weather camera"); cameraObject.transform.SetParent(root.transform, false);
                var camera = cameraObject.AddComponent<Camera>(); root.SetActive(true);
                weather.SetCalendarTime(4, 172, 12); double start = weather.WorldInstantSeconds;
                WeatherState initial = weather.SampleEnvironmentAt(Vector3.zero).Weather;
                weather.TickWorldClock(60); WeatherState wet = weather.SampleEnvironmentAt(Vector3.zero).Weather;
                Check(initial.wetness == 0 && wet.wetness > 0 && wet.wetness <= 1 && wet.cloudBaseM == initial.cloudBaseM,
                    "UC-T08-09 exposed rain fills a bounded store while retaining atmospheric values.");
                weather.SeekWorldInstant(start); weather.SampleEnvironmentAt(Vector3.zero);
                for (int i = 0; i < 60; i++) { weather.TickWorldClock(1); weather.SampleEnvironmentAt(Vector3.zero); }
                CheckClose(weather.SampleEnvironmentAt(Vector3.zero).Weather.wetness, wet.wetness,
                    "UC-T08-09 constant rain uses shared simulated time across frame partitions.");
                weather.pauseTime = true; weather.TickWorldClock(600);
                CheckClose(weather.SampleEnvironmentAt(Vector3.zero).Weather.wetness, wet.wetness,
                    "UC-T08-09 pausing the shared clock pauses surface accumulation.");
                weather.pauseTime = false; weather.rainIntensity = 0; weather.temperatureCelsius = 35; weather.diurnalTempRangeCelsius = 12;
                weather.TickWorldClock(60); float dry = weather.SampleEnvironmentAt(Vector3.zero).Weather.wetness;
                Check(dry < wet.wetness && dry >= 0, "UC-T08-09 stopped rain dries through existing PET and saved reference dry time.");
                weather.temperatureCelsius = -10; weather.rainIntensity = 1; weather.SeekWorldInstant(start);
                weather.SampleEnvironmentAt(Vector3.zero); weather.TickWorldClock(3600);
                WeatherState snow = weather.SampleEnvironmentAt(Vector3.zero).Weather;
                Check(snow.precipitationType == WeatherPrecipType.PrecipKind.Snow && snow.snowDepthM > 0 && snow.snowDepthM <= 3,
                    "UC-T08-09 wet-bulb-selected snow stores bounded SWE and derives compacted display depth.");
                Shader.SetGlobalVector(WetnessCoupling.SurfaceSnowId, new Vector4(0.3f, 0, 0, 0));
                Check(weather.BeginEnvironmentView(camera) && Shader.GetGlobalVector(WetnessCoupling.SurfaceSnowId).y == 1 &&
                    Shader.GetGlobalVector(WetnessCoupling.SurfaceSnowId).x > 0,
                    "UC-T08-09 captured stored snow reaches actual terrain/vegetation/rock shader bindings.");
                weather.EndEnvironmentView(camera);
                Check(Shader.GetGlobalVector(WetnessCoupling.SurfaceSnowId) == new Vector4(0.3f, 0, 0, 0),
                    "UC-T08-09 ending a camera restores incoming snow without modifying textures or placement.");
                weather.surfaceExposure01 = 0; weather.SeekWorldInstant(start); weather.SampleEnvironmentAt(Vector3.zero); weather.TickWorldClock(600);
                Check(weather.SampleEnvironmentAt(Vector3.zero).Weather.snowDepthM == 0,
                    "UC-T08-09 authored sheltered exposure stops snowfall supply without inferring unavailable shelter geometry.");
                weather.surfaceExposure01 = 1; weather.SeekWorldInstant(start); weather.SampleEnvironmentAt(Vector3.zero); weather.TickWorldClock(3601);
                Check(weather.SampleEnvironmentAt(Vector3.zero).Weather.snowDepthM == 0,
                    "UC-T08-09 long observation gaps reset locally without replaying a fabricated weather archive.");
                for (int i = 0; i < 200; i++) weather.SampleEnvironmentAt(new Vector3(i * 128, 0, 0));
                Check(weather.CachedSurfaceCells == 128, "UC-T08-09 distant observers cannot grow stores past the hard cell cap.");
                weather.SeekWorldInstant(start);
                Check(weather.CachedSurfaceCells == 0, "UC-T08-09 explicit seek retires all stale surface histories.");
                var run = world.GenerationScheduler.BeginRun(); var bounds = new Rect(0, 0, 32, 32);
                var channels = new[] { CommittedProfilePublication(50) }; var heights = new float[33, 33];
                // UC-T08-14: an accepted river needs a real bed below both banks; a flat surface cannot reconcile the fixed water level.
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++)
                    heights[z, x] = 0.5f - 0.1f * Mathf.Clamp01(1 - Mathf.Abs(x - 16) / 4f);
                var ground = new RiverChannelSnapshot(run, "surface-store-water", bounds, channels, heights, 0, 100);
                Check(RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, "surface-store-water", bounds, channels, null, ground, world),
                    "UC-T08-09 water/land fixture uses actual accepted river containment.");
                Vector3 land = new Vector3(0, 50, 16), waterPoint = new Vector3(16, 40, 16);
                Check(RiverChannelRegistry.TrySampleCommitted(world, waterPoint.x, waterPoint.z, 0, out var acceptedWater, out _) &&
                    acceptedWater.SignedBankDistanceMetres <= 0 && waterPoint.y < acceptedWater.WaterSurfaceY &&
                    (!RiverChannelRegistry.TrySampleCommitted(world, land.x, land.z, 0, out var landChannel, out _) || landChannel.SignedBankDistanceMetres > 0),
                    "UC-T08-09 fixture independently confirms reconciled water and dry land inside the same surface cell.");
                weather.SampleEnvironmentAt(land); weather.TickWorldClock(600);
                float retainedSnow = weather.SampleEnvironmentAt(land).Weather.snowDepthM;
                var waterReading = weather.SampleEnvironmentAt(waterPoint).Weather;
                Check(retainedSnow > 0 && waterReading.snowDepthM == 0 && waterReading.wetness == 1 &&
                    Mathf.Approximately(weather.SampleEnvironmentAt(land).Weather.snowDepthM, retainedSnow),
                    "UC-T08-09 an underwater sample cannot erase land snow in the same 64-metre surface cell.");
                RiverChannelRegistry.Withdraw(run.WorldId);
                weather.simulateSurfaceResponse = false; weather.surfaceWetness = 0.25f; weather.rainIntensity = 0;
                CheckClose(weather.SampleEnvironmentAt(Vector3.zero).Weather.wetness, 0.25f,
                    "UC-T08-09 opt-out retains existing authored material behavior.");
            }
            finally
            {
                if (world != null) RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                UnityEngine.Object.DestroyImmediate(root); Shader.SetGlobalVector(WetnessCoupling.SurfaceSnowId, incoming);
            }
        }
    }
}
