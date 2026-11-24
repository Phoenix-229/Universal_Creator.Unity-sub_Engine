// UC-T09-01: detached PCG configuration evidence on the existing capture owner; no quality or generation mutation.
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Optimization
{
    public partial class AdaptiveQuality
    {
        /// <summary>Optional PCG world to profile. Null selects this component's nearest parent world, including inactive parents; an unowned component records unavailable context.</summary>
        [Tooltip("World whose PCG settings are recorded at capture start. Empty uses this object's parent world; it never selects a global first world. Does not start generation or change quality.")]
        public UniverseCreatorObject captureWorld;

        /// <summary>Caller-supplied build/content revision, retained up to 128 characters; empty means comparison identity is unverified.</summary>
        [Tooltip("Your audited content/build revision for matched runs. Asset names/counts alone cannot verify matching graph settings, meshes or textures. Empty records an explicit evidence gap.")]
        public string captureContentRevision = "";

        /// <summary>Caller-supplied phone variant or test-host notes, retained up to 128 characters; runtime device/OS/GPU values are recorded separately.</summary>
        [Tooltip("Optional device notes, such as retail variant and declared RAM. These are author statements; the report also captures actual runtime SystemInfo. A subengine capture is not whole-game certification.")]
        public string captureDeviceNotes = "";

        /// <summary>Caller-supplied scenario such as Cold, Warm, Edit or Traversal, retained up to 128 characters; it does not change the generation collector's settings.</summary>
        [Tooltip("Label this capture Cold, Warm, Edit or Traversal. Enable and label generation measurements separately in Generation Metrics before generating; this field does not start a run or clear a cache.")]
        public string captureScenario = "Traversal";

        /// <summary>Caller-supplied camera-path identifier, retained up to 128 characters; empty denotes an unspecified path, not a recorded trajectory.</summary>
        [Tooltip("Identifier of your repeatable camera path and speed. This is a label, not an automatic camera recording. Keep it matched alongside content and quality when comparing runs.")]
        public string captureCameraPath = "";

        private UniverseCreatorObject captureMetricsWorld;

        private PerformanceWorldReport CapturePerformanceWorld()
        {
            var report = new PerformanceWorldReport {
                scope = "Universe Creator PCG subengine; total frame timings include the host application",
                contentRevision = BoundedCaptureText(captureContentRevision), deviceNotes = BoundedCaptureText(captureDeviceNotes),
                contentIdentityStatus = "Caller revision, asset names and counts are context only; matching full content is not verified.",
                pipelineAsset = CaptureObjectName(GraphicsSettings.currentRenderPipeline) };
            UniverseCreatorObject world = captureWorld != null ? captureWorld : GetComponentInParent<UniverseCreatorObject>(true);
            captureMetricsWorld = world; // UC-T09-02: subsequent exports stay bound to the world captured at start, even if the inspector selection changes.
            report.worldAvailable = world != null;
            report.worldSelection = world == null ? "Unavailable: no explicit or parent PCG world" : captureWorld != null ? "Explicit" : "Parent hierarchy";
            if (world == null) return report;

            report.worldName = CaptureObjectName(world);
            report.worldFactsHash = world.ComputeWorldFactsHash().ToString("X16"); // World salt only; deliberately not presented as a graph/content fingerprint.
            report.seed = world.seed; report.worldSizeKm = world.worldSizeKm; report.maxHeightMetres = world.maxHeightMeters;
            report.heightmapResolution = world.resolution; report.samplingOffsetMetres = world.worldOriginOffset;
            report.chunkSizeMetres = world.chunkSizeMeters; report.residentWindowPerSide = world.residentWindowPerSide;
            report.streamingEnabled = world.streamingEnabled; report.highLODRingSize = world.highLODRingSize; report.bufferRingWidth = world.bufferRingWidth;
            report.drawDistanceMetres = world.drawDistance; report.lodBias = world.lodBias; report.gpuInstancing = world.enableGPUInstancing;
            report.maximumConcurrentChunks = world.maxConcurrentChunks; report.commitBudgetMs = world.ChunkCommitBudgetMilliseconds;
            report.previewQuality = world.previewQuality.ToString(); report.terrainMaterial = CaptureObjectName(world.terrainMaterial);
            report.terrainShader = world.terrainMaterial != null ? CaptureObjectName(world.terrainMaterial.shader) : "";
            report.mainGraph = CaptureGraph(world.graph);
            UCWorldGraphBindings bindings = world.GetComponent<UCWorldGraphBindings>();
            if (bindings != null)
            {
                report.vegetationGraph = CaptureGraph(bindings.VegetationGraph); report.textureGraph = CaptureGraph(bindings.TextureGraph);
                report.lightingGraph = CaptureGraph(bindings.LightingGraph);
                report.vegetationRevision = bindings.VegetationRevision; report.textureRevision = bindings.TextureRevision;
                report.lightingRevision = bindings.LightingRevision; report.baselineClimateRevision = bindings.BaselineClimateRevision;
            }
            int layers = world.terrainLayers != null ? world.terrainLayers.Length : 0;
            report.terrainLayerCount = layers;
            report.terrainLayers = new PerformanceLayerReport[Math.Min(64, layers)]; // Bounded diagnostic inventory; truncation is explicit.
            report.terrainLayersTruncated = layers > report.terrainLayers.Length;
            for (int i = 0; i < report.terrainLayers.Length; i++)
            {
                TerrainLayer layer = world.terrainLayers[i];
                report.terrainLayers[i] = new PerformanceLayerReport { name = CaptureObjectName(layer), available = layer != null };
                if (layer == null) continue;
                var entry = report.terrainLayers[i]; entry.tileSizeMetres = layer.tileSize;
                entry.diffuse = CaptureTexture(layer.diffuseTexture); entry.normal = CaptureTexture(layer.normalMapTexture); entry.mask = CaptureTexture(layer.maskMapTexture);
            }
            ScatterPrototypeLibrary library = world.scatterLibrary;
            report.scatterLibraryAvailable = library != null;
            if (library != null)
            {
                report.treeRosterSlots = library.trees != null ? library.trees.Count : 0;
                report.shrubRosterSlots = library.shrubs != null ? library.shrubs.Count : 0;
                report.detailRosterSlots = library.details != null ? library.details.Count : 0;
                report.objectRosterSlots = library.objects != null ? library.objects.Count : 0;
                // Roster slots include invalid/empty entries; they are not placed or visible instance counts.
            }
            var vegetation = UCWeatherSystemManager.FindOwnedEnvironmentManager<UCVegetationSystemManager>(world.transform);
            report.vegetationManagerAvailable = vegetation != null;
            if (vegetation != null)
            {
                report.vegetationEnabled = vegetation.vegetationEnabled; report.vegetationDensityScale = vegetation.globalDensityScale;
                report.vegetationSeedOffset = vegetation.seedOffset; report.detailMemoryCeilingMiB = vegetation.detailMemoryCeilingMB;
                report.maximumInstancesPerCell = vegetation.maxInstancesPerCell; report.vegetationQuality = vegetation.qualityPreset.ToString();
                report.vegetationUsesGraphMasks = vegetation.useGraphMasks;
            }
            var lighting = UCWeatherSystemManager.FindOwnedEnvironmentManager<UCLightingSystemManager>(world.transform);
            report.lightingManagerAvailable = lighting != null;
            if (lighting != null)
            {
                report.lightingEnabled = lighting.lightingEnabled; report.lightingFollowsAdaptiveQuality = lighting.followAdaptiveQuality;
                report.lightingAuthoredTier = lighting.lightingTierOverride.ToString(); report.lightingResolvedTier = lighting.ResolvedTier.ToString();
                var tier = lighting.ResolvedTierSettings;
                report.lightingTierSettings = new PerformanceLightingTierReport {
                    cloudLayerCount = tier.cloudLayerCount, cloudNoiseResolution = tier.cloudNoiseResolution,
                    precipitationParticleLimit = tier.precipitationParticleLimit, cloudSelfShadows = tier.cloudSelfShadows,
                    screenSpaceGI = tier.screenSpaceGI, ssgiSampleCount = tier.ssgiSampleCount, contactShadows = tier.contactShadows,
                    screenSpaceReflections = tier.screenSpaceReflections, cascadeCount = tier.cascadeCount,
                    shadowDistanceMetres = tier.shadowDistance, shadowAtlasResolution = tier.shadowAtlasResolution,
                    probeSpacingScale = tier.probeSpacingScale }; // Copy the non-serialized production tier into serializable evidence; no feature execution claim.
            }
            var water = UCWeatherSystemManager.FindOwnedEnvironmentManager<UCWaterSystemManager>(world.transform);
            report.waterManagerAvailable = water != null;
            report.waterAuthoredQualityMode = water != null ? water.qualityMode.ToString() : "";
            var weather = UCWeatherSystemManager.FindOwnedEnvironmentManager<UCWeatherSystemManager>(world.transform);
            report.weatherManagerAvailable = weather != null;
            if (weather != null)
            {
                report.liveWeatherEnabled = weather.enableLiveWeatherSim; report.weatherClockPaused = weather.pauseTime;
                report.simulatedSecondsPerRealSecond = weather.timeScale; report.weatherDrivesWind = weather.weatherDrivesWind;
                report.authoredCloudCoverage = weather.cloudCoverage;
            }
            return report;
        }

        private void CaptureGenerationEvidence(PerformanceReport report, bool atStart)
        {
            bool available = captureMetricsWorld != null;
            UCGenerationMetrics metrics = available ? captureMetricsWorld.GenerationScheduler.Metrics : null;
            UCGenerationMetrics.Report[] records = metrics != null ? metrics.Snapshot() : Array.Empty<UCGenerationMetrics.Report>();
            if (atStart)
            {
                report.generationAtStart = records;
                report.generationCollectionEnabledAtStart = metrics != null && metrics.Enabled;
                report.generationScenarioAtStart = metrics != null ? metrics.Scenario : "";
            }
            else
            {
                report.generationAtEnd = records;
                report.generationCollectionEnabledAtEnd = metrics != null && metrics.Enabled;
                report.generationScenarioAtEnd = metrics != null ? metrics.Scenario : "";
            }
            report.generationEvidenceStatus = !available ? "Unavailable: captured PCG world is missing or destroyed" :
                "Bounded cumulative generation histories at start/end; IDs and counters are not an exact frame-capture interval. Missing or evicted runs cannot imply zero work.";
        }

        private static string BoundedCaptureText(string value) => string.IsNullOrEmpty(value) ? "" : value.Substring(0, Math.Min(128, value.Length));
        private static string CaptureObjectName(UnityEngine.Object value) => value != null ? BoundedCaptureText(value.name) : "";
        private static PerformanceGraphReport CaptureGraph(Graph graph) => new PerformanceGraphReport {
            available = graph != null, name = CaptureObjectName(graph), domain = graph != null ? graph.Domain.ToString() : "",
            nodeSlots = graph != null && graph.nodes != null ? graph.nodes.Count : 0,
            wires = graph != null && graph.connections != null ? graph.connections.Count : 0 };
        private static PerformanceTextureReport CaptureTexture(Texture texture) => new PerformanceTextureReport {
            available = texture != null, name = CaptureObjectName(texture), width = texture != null ? texture.width : 0,
            height = texture != null ? texture.height : 0 }; // Loaded dimensions, not source/import settings or GPU residency.

        [Serializable] private sealed class PerformanceGraphReport { public bool available; public string name, domain; public int nodeSlots, wires; }
        [Serializable] private sealed class PerformanceTextureReport { public bool available; public string name; public int width, height; }
        [Serializable] private sealed class PerformanceLayerReport
        { public bool available; public string name; public Vector2 tileSizeMetres; public PerformanceTextureReport diffuse, normal, mask; }
        [Serializable] private sealed class PerformanceLightingTierReport
        {
            public int cloudLayerCount, cloudNoiseResolution, precipitationParticleLimit, ssgiSampleCount, cascadeCount, shadowAtlasResolution;
            public bool cloudSelfShadows, screenSpaceGI, contactShadows, screenSpaceReflections;
            public float shadowDistanceMetres, probeSpacingScale;
        }
        [Serializable] private sealed class PerformanceWorldReport
        {
            public string scope, contentRevision, deviceNotes, contentIdentityStatus, pipelineAsset, worldSelection, worldName, worldFactsHash;
            public bool worldAvailable, streamingEnabled, gpuInstancing, terrainLayersTruncated, scatterLibraryAvailable;
            public int seed, heightmapResolution, residentWindowPerSide, highLODRingSize, bufferRingWidth, maximumConcurrentChunks, terrainLayerCount;
            public float worldSizeKm, maxHeightMetres, chunkSizeMetres, drawDistanceMetres, lodBias, commitBudgetMs;
            public Vector2 samplingOffsetMetres;
            public string previewQuality, terrainMaterial, terrainShader;
            public PerformanceGraphReport mainGraph, vegetationGraph, textureGraph, lightingGraph;
            public long vegetationRevision, textureRevision, lightingRevision, baselineClimateRevision;
            public PerformanceLayerReport[] terrainLayers;
            public int treeRosterSlots, shrubRosterSlots, detailRosterSlots, objectRosterSlots;
            public bool vegetationManagerAvailable, vegetationEnabled, vegetationUsesGraphMasks, lightingManagerAvailable, lightingEnabled, lightingFollowsAdaptiveQuality;
            public int vegetationSeedOffset, maximumInstancesPerCell;
            public float vegetationDensityScale, detailMemoryCeilingMiB;
            public string vegetationQuality, lightingAuthoredTier, lightingResolvedTier;
            public PerformanceLightingTierReport lightingTierSettings;
            public bool waterManagerAvailable, weatherManagerAvailable, liveWeatherEnabled, weatherClockPaused, weatherDrivesWind;
            public string waterAuthoredQualityMode;
            public float simulatedSecondsPerRealSecond, authoredCloudCoverage;
        }
    }
}
