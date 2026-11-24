// UC-T05-04: baseline/dynamic staging reaches the existing weather, climate and native sun consumers.
using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Runs the resumed graph-domain acceptance without unrelated product assets or device/visual claims.</summary>
        public static void RunGraphDomainsBatch()
        {
            checks = 0; failures.Clear();
            var groups = new (string id, Action run)[] { // UC-T05-11: every group reports its real failures instead of hiding later groups after an exception.
                ("UC-T05-01", TestGraphDomainMetadata), ("UC-T05-02", TestGraphWindowServices),
                ("UC-T05-03/04/06", TestVegetationGraphBinding), ("UC-T05-05", TestDomainWindowAuthoring),
                ("UC-T05-07", TestSharedGraphEditing), ("UC-T05-08", TestGraphPreviews),
                ("UC-T05-09", TestGraphMigrationAnalysis), ("UC-T05-10", TestGraphMigrationExecution),
                ("UC-T05-11 widgets", TestGraphSerializedWidgets), ("UC-T05-11 sample", TestFourGraphSample) };
            foreach (var group in groups)
                try { group.run(); } catch (Exception error) { Check(false, group.id + " graph acceptance exception: " + error); }
            bool ok = failures.Count == 0; Debug.Log(Report(ok)); Console.WriteLine(Report(ok));
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static void TestLightingGraphBinding(UniverseCreatorObject world)
        {
            var context = new SnapshotRetirementContext();
            var originalContext = System.Threading.SynchronizationContext.Current;
            var graph = ScriptableObject.CreateInstance<Graph>();
            var weatherObject = new GameObject("Environment graph weather fixture"); weatherObject.transform.SetParent(world.transform, false);
            var lightingObject = new GameObject("Environment graph lighting fixture"); lightingObject.transform.SetParent(world.transform, false);
            try
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                var weather = weatherObject.AddComponent<UCWeatherSystemManager>(); weather.enabled = false; weather.SetTime(172, 12);
                var light = lightingObject.AddComponent<UCLightingSystemManager>(); light.enabled = false;
                // UC-T08-14: inactive fixtures never run OnEnable; provide the native recipient without permitting acceptance to create objects.
                var sunObject = new GameObject("Authored graph sun"); sunObject.transform.SetParent(lightingObject.transform, false);
                light.sun = sunObject.AddComponent<Light>(); light.sun.type = LightType.Directional;
                var bindings = world.GetComponent<UCWorldGraphBindings>();
                graph.SetDomain(UCGraphDomain.LightingClimateWeather);
                var temperature = new ConstantNode { value = 4, id = "environment-temperature" };
                var cloud = new ConstantNode { value = 0.2f, id = "environment-cloud" };
                var ground = new WorldGraphInputNode { id = "environment-ground" };
                var weatherOutput = new WeatherOutputNode { id = "environment-weather-output" };
                var lightOutput = new LightingOutputNode { id = "environment-light-output" };
                graph.nodes.Add(temperature); graph.nodes.Add(cloud); graph.nodes.Add(ground); graph.nodes.Add(weatherOutput); graph.nodes.Add(lightOutput);
                graph.connections.Add(new Connection(temperature.id, 0, weatherOutput.id, 1));
                graph.connections.Add(new Connection(cloud.id, 0, weatherOutput.id, 4));
                graph.connections.Add(new Connection(ground.id, 0, lightOutput.id, 4));
                // UC-T05-04: absent/disabled authoring is pending and cannot implicitly create a second environment or terrain run.
                Check(!WaitVegetationRun(bindings.RunLightingAsync(), context) && bindings.LightingRevision == 0,
                    "UC-T05-04 an empty Lighting Graph binding reports pending without applying an environment.");
                bindings.LightingGraph = graph;
                GenerationRunId run = world.GenerationScheduler.CurrentRun;
                weatherOutput.disabled = true; lightOutput.disabled = true;
                Check(!WaitVegetationRun(bindings.RunLightingAsync(), context) && bindings.LightingRevision == 0 &&
                    world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T05-04 disabled environment outputs preserve the world run and leave the candidate unapplied.");
                weatherOutput.disabled = false; lightOutput.disabled = false;
                bool wrongDomain = false;
                try { bindings.LightingGraph = world.graph; } catch (ArgumentException) { wrongDomain = true; }
                Check(wrongDomain && ReferenceEquals(bindings.LightingGraph, graph),
                    "UC-T05-04 incompatible Lighting Graph assignment preserves the valid saved environment binding.");
                float authoredTemperature = weather.temperatureCelsius, authoredCloud = weather.cloudCoverage;
                Check(WaitVegetationRun(bindings.RunLightingAsync(true), context) && bindings.BaselineClimateRevision == 1 && bindings.LightingRevision == 0,
                    "UC-T05-04 baseline climate executes only independent weather roots while dynamic final-ground lighting remains separate.");
                Check(Mathf.Abs(weather.MeanTempAtAltitudeC(0) - (authoredTemperature + 4)) < 1e-5f &&
                    weather.temperatureCelsius == authoredTemperature && weather.cloudCoverage == authoredCloud && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T05-04 baseline reaches the real climate consumer without editing authored settings or restarting terrain.");
                float sunlight = light.sun != null ? light.sun.intensity : 0;
                Check(WaitVegetationRun(bindings.RunLightingAsync(), context) && bindings.LightingRevision == 1 &&
                    light.sun != null && sunlight > 0 && light.sun.intensity < sunlight,
                    "UC-T05-04 accepted ground exposure changes the actual native sun through its existing physical driver.");
                Check(Mathf.Abs(weather.ResolveWeather().temperature - (authoredTemperature + 4)) < 1e-5f &&
                    Mathf.Abs(weather.ResolveWeather().cloudCover - Mathf.Clamp01(authoredCloud + 0.2f)) < 1e-5f,
                    "UC-T05-04 dynamic weather reaches the existing consumer and replaces its baseline port once without double bias.");
                temperature.value = 9;
                Check(Mathf.Abs(weather.ResolveWeather().temperature - (authoredTemperature + 4)) < 1e-5f,
                    "UC-T05-04 pending authored environment edits cannot change accepted weather.");
                var cancelled = bindings.RunLightingAsync(); bindings.CancelLighting();
                Check(!WaitVegetationRun(cancelled, context) && bindings.LightingRevision == 1,
                    "UC-T05-04 cancellation keeps accepted environment fields and their revision.");
                double beforeTime = weather.ElapsedSimSeconds; weather.Advance(60); // UC-T05-04: verify clock progress through its existing public diagnostic contract.
                Check(weather.ElapsedSimSeconds == beforeTime + 60 && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T05-04 the same world clock continues after graph application without a terrain rebuild.");
                graph.connections.Add(new Connection(ground.id, 0, weatherOutput.id, 0));
                Check(!WaitVegetationRun(bindings.RunLightingAsync(true), context) && bindings.BaselineClimateRevision == 1 &&
                    bindings.LightingStatus.Contains("baseline climate cannot depend on final ground"),
                    "UC-T05-04 final-ground dependencies are rejected in the early baseline stage without replacing valid climate.");
                bindings.LightingGraph = null;
            }
            finally
            {
                world.GetComponent<UCWorldGraphBindings>()?.CancelLighting();
                UnityEngine.Object.DestroyImmediate(weatherObject); UnityEngine.Object.DestroyImmediate(lightingObject);
                context.Drain(); UnityEngine.Object.DestroyImmediate(graph);
                System.Threading.SynchronizationContext.SetSynchronizationContext(originalContext);
            }
        }
    }
}
