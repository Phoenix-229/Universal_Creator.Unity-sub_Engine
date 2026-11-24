// UC-T05-04: explicit baseline/dynamic environment stages share the world worker and immutable region inputs.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Outputs;

namespace UniverseCreator.TerrainSystem
{
    internal struct EnvironmentGraphBias
    {
        internal float TemperatureC, PrecipitationMmPerHour, WindMetresPerSecond, FogDensity, CloudCover, SnowDepthMetres, StormActivity;
        internal float Turbidity, SkyTint, AmbientScale, LightingCloudCover, ExposureEv;
    }

    public sealed partial class UCWorldGraphBindings
    {
        [SerializeField, Tooltip("Lighting, weather and climate graph. Apply baseline climate before terrain; apply dynamic fields after accepted ground.")]
        private Graph lightingGraph;
        [SerializeField, Tooltip("Optional camera/player transform for spatial environment fields. Empty samples this UC world's origin.")]
        private Transform environmentSampleTarget;
        private GenerationSnapshot baselineLighting, acceptedLighting;
        private List<GraphVegetationResult> baselineFields, lightingFields;
        private GenerationRunId baselineRun, lightingRun;
        private Graph lightingMain;
        private bool lightingUsesGround;
        private bool lightingGroundStale;
        private List<ProductMemoryBudget.Lease> lightingGroundPins;
        private StopToken pendingLighting;
        private Task<bool> lightingTask;
        private long lightingRevision, baselineRevision;
        private static readonly Func<Generator, bool> BaselineRoots = node => node is WeatherOutputNode;
        private static readonly Func<Generator, bool> DynamicRoots = node => node is WeatherOutputNode || node is LightingOutputNode;

        /// <summary>Saved Lighting/Climate/Weather graph; null unbinds, incompatible metadata is rejected without losing the current reference.</summary>
        /// <exception cref="ArgumentException">A non-null asset is outside the supported environment domain.</exception>
        public Graph LightingGraph
        {
            get => lightingGraph;
            set
            {
                if (value != null && (!value.HasSupportedDomainMetadata || value.Domain != UCGraphDomain.LightingClimateWeather))
                    throw new ArgumentException("Select an explicit Lighting Graph asset.", nameof(value));
                if (!ReferenceEquals(lightingGraph, value)) CancelLighting();
                lightingGraph = value;
            }
        }
        /// <summary>Optional scene transform sampled for dynamic environment fields; null uses the bound world's origin.</summary>
        public Transform EnvironmentSampleTarget { get => environmentSampleTarget; set => environmentSampleTarget = value; }
        /// <summary>Last accepted dynamic graph revision; zero means none in this runtime lifetime.</summary>
        public long LightingRevision => lightingRevision;
        /// <summary>Last accepted terrain-independent baseline climate revision; zero means no baseline has been applied.</summary>
        public long BaselineClimateRevision => baselineRevision;
        /// <summary>True while an explicitly requested environment candidate evaluates.</summary>
        public bool IsLightingRunning => lightingTask != null && !lightingTask.IsCompleted;
        /// <summary>Pending, running, accepted or failed environment execution status; authored edits remain unapplied.</summary>
        public string LightingStatus { get; private set; } = "Pending: select a Lighting Graph and run its baseline or dynamic stage.";

        /// <summary>Captures and applies the selected environment stage without restarting the world's terrain generation or clock.</summary>
        /// <param name="baselineClimate">True selects terrain-independent Weather Output roots for early climate; false applies dynamic lighting/weather roots.</param>
        /// <returns>True after current-world acceptance; false on missing inputs, cancellation, foreign/stale ground or a reported failure.</returns>
        /// <remarks>Main-thread entry; CPU work shares the existing admitted worker pool. Baseline climate never reads final ground.</remarks>
        public Task<bool> RunLightingAsync(bool baselineClimate = false)
        {
            GraphExecutionGuard.RequireMainThread("Lighting Graph Run");
            CancelLighting(); pendingLighting = new StopToken();
            lightingTask = EvaluateLightingAsync(pendingLighting, baselineClimate);
            return lightingTask;
        }

        /// <summary>Cooperatively cancels the pending environment candidate while retaining accepted fields and the running world clock.</summary>
        public void CancelLighting() => pendingLighting?.RequestStop();

        private async Task<bool> EvaluateLightingAsync(StopToken stop, bool baseline)
        {
            GenerationSnapshot candidate = null;
            var results = new List<GraphVegetationResult>();
            var pins = new List<ProductMemoryBudget.Lease>();
            try
            {
                var world = Owner;
                Graph authored = lightingGraph, main = world != null ? world.graph : null;
                if (schemaVersion != 1 || world == null || authored == null || !authored.HasSupportedDomainMetadata ||
                    authored.Domain != UCGraphDomain.LightingClimateWeather || main == null || !main.HasSupportedDomainMetadata || main.Domain != UCGraphDomain.TerrainWater)
                { LightingStatus = "Pending: bind explicit Lighting and main TerrainWater graphs to this world."; return false; }
                bool hasWeather = authored.nodes.Exists(node => node is WeatherOutputNode && !node.disabled);
                bool hasLighting = !baseline && authored.nodes.Exists(node => node is LightingOutputNode && !node.disabled);
                var weather = UCWeatherSystemManager.FindForWorld(world.transform);
                var light = world.GetComponentInChildren<UCLightingSystemManager>(true);
                if ((!hasWeather && !hasLighting) || weather == null || (hasLighting && light == null))
                { LightingStatus = "Pending: enable an environment output and provide its existing world weather/lighting manager."; return false; }
                Func<Generator, bool> filter = baseline ? BaselineRoots : DynamicRoots;
                bool usesGround = EnvironmentNeedsGround(authored, filter);
                if (baseline && usesGround)
                { LightingStatus = "Pending: baseline climate cannot depend on final ground; connect terrain-dependent fields to the dynamic stage."; return false; }
                ValidateWorldInputs(authored, main);
                GenerationRunId run = world.GenerationScheduler.CaptureRun();
                candidate = GenerationSnapshot.Capture(authored, world, requireOutput: true);
                var chunks = world.Chunks;
                var regions = usesGround && chunks != null ? chunks.CaptureGraphVegetationRegions(run) : new List<GraphVegetationRegion>();
                if (usesGround && regions.Count == 0)
                { LightingStatus = "Pending: build accepted terrain before applying ground-dependent environment fields."; return false; }
                if (!usesGround) regions.Add(new GraphVegetationRegion { Position = candidate.Settings.worldPos, Size = candidate.Settings.worldSize });
                foreach (var region in regions) if (region.Ground != null)
                    pins.Add(world.ProductMemory.Acquire(region.Ground, ProductMemoryFootprint.Estimate(region.Ground), 0, "environment-accepted-ground"));
                var water = usesGround ? WorldGraphWaterInput.Capture(world) : null;
                var environment = CaptureEnvironmentInputs(false); // UC-T05-06: environment graphs can reuse baseline fields without feeding back their dynamic candidate.
                water?.Pin(world.ProductMemory, pins); environment?.Pin(world.ProductMemory, pins);
                LightingStatus = baseline ? "Running terrain-independent baseline climate." : "Running dynamic environment fields.";
                foreach (var region in regions)
                    results.Add(await EvaluateRegionAsync(world, main, candidate, region, run, stop, filter, water, environment));
                if (stop.StopRequested || !ReferenceEquals(stop, pendingLighting) || !ReferenceEquals(authored, lightingGraph) ||
                    !ReferenceEquals(main, world.graph) || (usesGround && !chunks.AreGraphVegetationRegionsCurrent(results, run)) ||
                    (water != null && !water.IsCurrent()) || (environment != null && !environment.IsCurrent()))
                    throw new OperationCanceledException();
                long next = checked((baseline ? baselineRevision : lightingRevision) + 1);
                if (!world.GenerationScheduler.TryPublish(run, () =>
                {
                    if (baseline)
                    {
                        DisposeEnvironmentFields(baselineFields); baselineLighting?.Release();
                        baselineFields = results; baselineLighting = candidate; baselineRun = run; baselineRevision = next;
                    }
                    else
                    {
                        DisposeEnvironmentFields(lightingFields); acceptedLighting?.Release();
                        if (lightingGroundPins != null) foreach (var pin in lightingGroundPins) pin.Dispose();
                        lightingGroundPins = pins; pins = null;
                        lightingFields = results; acceptedLighting = candidate; lightingRun = run; lightingMain = main;
                        lightingRevision = next; lightingUsesGround = usesGround; lightingGroundStale = false;
                    }
                    results = null; candidate = null;
                    weather?.BindEnvironmentGraph(this); light?.BindEnvironmentGraph(this);
                    // UC-T08-04: accepted baseline edits affect placement; live/dynamic weather refreshes appearance only.
                    if (baseline) UCVegetationSystemManager.InvalidateForWorld(world.transform);
                })) throw new OperationCanceledException();
                if (!baseline) UCVegetationSystemManager.ForWorld(world.transform)?.MarkAppearanceDirty(); // UC-T08-04: native appearance uploads stay outside the publication lock.
                light?.RefreshAcceptedGraphEnvironment(); // UC-T05-04: native lighting work runs outside the short managed publication lock.
                LightingStatus = baseline ? "Accepted baseline climate " + next + "; build terrain explicitly to use the new baseline." :
                    "Accepted environment " + next + "; the existing clock and weather/light drivers continue.";
                return true;
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(stop, pendingLighting)) LightingStatus = "Cancelled or stale inputs; the previous accepted environment is retained.";
                return false;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(stop, pendingLighting)) { LightingStatus = "Environment Run failed: " + ex.Message; Debug.LogError(LightingStatus, this); }
                return false;
            }
            finally
            {
                DisposeEnvironmentFields(results); candidate?.Release();
                if (pins != null) foreach (var pin in pins) pin.Dispose();
            }
        }

        // Traverse only selected output dependencies, including portal fallbacks, through the shared effective graph contract.
        private static bool EnvironmentNeedsGround(Graph graph, Func<Generator, bool> filter)
        {
            var pending = new Stack<Generator>(); var seen = new HashSet<Generator>();
            foreach (var node in graph.nodes) if (node != null && !node.disabled && filter(node)) pending.Push(node);
            while (pending.Count > 0)
            {
                var node = pending.Pop(); if (!seen.Add(node)) continue;
                if (node is WorldGraphInputNode || node is Nodes.Biome.TerrainClimateModifierNode) return true; // UC-T08-04: admit only the later accepted-ground stage.
                foreach (var connection in graph.connections)
                    if (connection.targetNodeId == node.id)
                    { var source = graph.GetNodeById(connection.sourceNodeId); if (source != null) pending.Push(source); }
                if (node is IPortalIn portal)
                { var sender = portal.ResolveSender(graph); if (sender != null) pending.Push(sender); }
            }
            return false;
        }

        internal EnvironmentGraphBias SampleEnvironmentBias(bool baselineOnly = false)
        {
            Vector3 p = environmentSampleTarget != null && !baselineOnly ? environmentSampleTarget.position : Owner.transform.position;
            return SampleEnvironmentBiasAt(p, baselineOnly); // UC-T08-05: camera/observer samples share accepted fields, never a global weather singleton.
        }

        internal EnvironmentGraphBias SampleEnvironmentBiasAt(Vector3 p, bool baselineOnly = false)
        {
            EnvironmentGraphBias bias = default;
            if (baselineRun.WorldId == Owner.GenerationScheduler.CurrentRun.WorldId) SampleEnvironmentFields(baselineFields, p, ref bias);
            if (!baselineOnly && acceptedLighting != null && (!lightingUsesGround ||
                (!lightingGroundStale && Owner.GenerationScheduler.IsCurrent(lightingRun) && ReferenceEquals(Owner.graph, lightingMain))))
                SampleEnvironmentFields(lightingFields, p, ref bias);
            else if (!baselineOnly && acceptedLighting != null) LightingStatus = "Stale: Run dynamic environment against the new accepted ground.";
            return bias;
        }

        // UC-T08-03: capture only early climate; dynamic fields and the running clock never enter terrain cache fingerprints.
        internal Weather.ClimateBaselineProduct CaptureClimateBaseline()
        {
            GraphExecutionGuard.RequireMainThread("Early climate baseline capture");
            if (baselineFields == null || baselineRun.WorldId != Owner.GenerationScheduler.CurrentRun.WorldId) return null;
            foreach (var field in baselineFields) if (field.Outputs.climateBaseline != null) return field.Outputs.climateBaseline;
            return null;
        }

        private void InvalidateLightingGround()
        {
            if (acceptedLighting == null || !lightingUsesGround) return;
            lightingGroundStale = true;
            LightingStatus = "Stale: Run dynamic environment against the new accepted ground.";
        }

        // UC-T08-06: use accepted recipes only; a stale terrain-dependent dynamic run cannot override the baseline.
        internal Weather.WeatherRegionProduct CaptureWeatherRegions()
        {
            GraphExecutionGuard.RequireMainThread("Weather region capture");
            if (acceptedLighting != null && (!lightingUsesGround || (!lightingGroundStale &&
                Owner.GenerationScheduler.IsCurrent(lightingRun) && ReferenceEquals(Owner.graph, lightingMain))))
                if (lightingFields != null) foreach (var field in lightingFields)
                    if (field.Outputs.weatherRegions != null) return field.Outputs.weatherRegions;
            if (baselineRun.WorldId == Owner.GenerationScheduler.CurrentRun.WorldId && baselineFields != null)
                foreach (var field in baselineFields) if (field.Outputs.weatherRegions != null) return field.Outputs.weatherRegions;
            return null;
        }

        private static void SampleEnvironmentFields(List<GraphVegetationResult> fields, Vector3 p, ref EnvironmentGraphBias b)
        {
            if (fields == null) return;
            foreach (var field in fields)
            {
                var r = field.Region;
                if (p.x < r.Position.x || p.z < r.Position.z || p.x > r.Position.x + r.Size.x || p.z > r.Position.z + r.Size.z) continue;
                OutputBag o = field.Outputs;
                // A dynamic port replaces its baseline port once; unwired ports retain the baseline instead of doubling the same graph's bias.
                if (o.weatherTemperatureBias != null) b.TemperatureC = SampleBias(o.weatherTemperatureBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherPrecipBias != null) b.PrecipitationMmPerHour = SampleBias(o.weatherPrecipBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherWindBias != null) b.WindMetresPerSecond = SampleBias(o.weatherWindBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherFogBias != null) b.FogDensity = SampleBias(o.weatherFogBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherCloudBias != null) b.CloudCover = SampleBias(o.weatherCloudBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherSnowBias != null) b.SnowDepthMetres = SampleBias(o.weatherSnowBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherStormBias != null) b.StormActivity = SampleBias(o.weatherStormBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                if (o.weatherTurbidityBias != null) b.Turbidity = SampleBias(o.weatherTurbidityBias, o.weatherBiasSize, o.weatherBiasWorldMin, o.weatherBiasWorldSize, p);
                b.SkyTint += SampleBias(o.lightingSkyTintBias, o.lightingBiasSize, o.lightingBiasWorldMin, o.lightingBiasWorldSize, p);
                b.AmbientScale += SampleBias(o.lightingAmbientScaleBias, o.lightingBiasSize, o.lightingBiasWorldMin, o.lightingBiasWorldSize, p);
                b.Turbidity += SampleBias(o.lightingTurbidityBias, o.lightingBiasSize, o.lightingBiasWorldMin, o.lightingBiasWorldSize, p);
                b.LightingCloudCover += SampleBias(o.lightingCloudCoverBias, o.lightingBiasSize, o.lightingBiasWorldMin, o.lightingBiasWorldSize, p);
                b.ExposureEv += SampleBias(o.lightingExposureBias, o.lightingBiasSize, o.lightingBiasWorldMin, o.lightingBiasWorldSize, p);
                return; // Shared cell boundaries resolve once; neighbouring fields must not double a bias.
            }
        }

        internal static float SampleBias(float[] values, Vector2Int size, Vector2 min, Vector2 extent, Vector3 p)
        {
            if (values == null || size.x < 2 || size.y < 2 || extent.x <= 0 || extent.y <= 0 || values.LongLength < (long)size.x * size.y) return 0;
            float x = Mathf.Clamp01((p.x - min.x) / extent.x) * (size.x - 1), z = Mathf.Clamp01((p.z - min.y) / extent.y) * (size.y - 1);
            int ix = Math.Min(size.x - 2, (int)x), iz = Math.Min(size.y - 2, (int)z), at = iz * size.x + ix;
            float value = Mathf.Lerp(Mathf.Lerp(values[at], values[at + 1], x - ix),
                Mathf.Lerp(values[at + size.x], values[at + size.x + 1], x - ix), z - iz);
            return RiverChannelSnapshot.Finite(value) ? value : 0;
        }

        private static void DisposeEnvironmentFields(List<GraphVegetationResult> fields)
        { if (fields != null) foreach (var field in fields) field.Dispose(); }

        private void RetireLightingGraphs()
        {
            CancelLighting(); DisposeEnvironmentFields(baselineFields); DisposeEnvironmentFields(lightingFields);
            baselineFields = null; lightingFields = null;
            baselineLighting?.Release(); acceptedLighting?.Release(); baselineLighting = null; acceptedLighting = null;
            if (lightingGroundPins != null) foreach (var pin in lightingGroundPins) pin.Dispose();
            lightingGroundPins = null;
        }
    }
}
