// UC-T06-11: accepted lake/river/ocean observations, shared species/habitat laws and final aquatic contact.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators; // UC-T06-11: use the existing constant field producer in the registered habitat fixture.
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationAquaticHabitat()
        {
            var worldRoot = new GameObject("UC aquatic habitat world");
            var waterRoot = new GameObject("UC accepted aquatic water"); waterRoot.transform.SetParent(worldRoot.transform, false);
            var foreignRoot = new GameObject("UC foreign aquatic water");
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.Vegetation);
            var main = ScriptableObject.CreateInstance<Graph>(); main.SetDomain(UCGraphDomain.TerrainWater);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(8, 100, 8) };
            var terrainRoot = Terrain.CreateTerrainGameObject(data);
            TileData tile = null;
            try
            {
                var world = worldRoot.AddComponent<UniverseCreatorObject>(); world.graph = main;
                var run = world.GenerationScheduler.CaptureRun();
                var body = JunctionQuad(0, 8, 0, 8, false);
                for (int i = 0; i < body.vertices.Length; i++) body.vertices[i].y = 10f;
                var volume = waterRoot.AddComponent<UCWaterVolume>(); volume.kind = UCWaterVolume.BodyKind.Lake;
                volume.SetAcceptedSurface(body, Matrix4x4.identity, false, new Vector2(0, 0.75f));
                var foreign = foreignRoot.AddComponent<UCWaterVolume>(); foreign.kind = UCWaterVolume.BodyKind.Ocean;
                foreign.SetAcceptedSurface(body, Matrix4x4.Translate(Vector3.up * 100), false);
                var water = WorldGraphWaterInput.Capture(world);
                Check(water.TryStillObservation(4, 4, out float level, out Vector2 flow, out WaterBodyKind kind) && level == 10 &&
                    Mathf.Abs(flow.magnitude - 0.75f) < 1e-5f && kind == WaterBodyKind.Lake,
                    "UC-T06-11 accepted wave-free lake level and m/s current exclude another world's overlapping water.");
                var height = new Matrix(new CoordRect(0, 0, 9, 9)) { worldPos = Vector3.zero, worldSize = new Vector3(8, 100, 8), worldHeight = 100f };
                Array.Fill(height.arr, 0.09f);
                var species = new EcoEnvelope { displayName = "aquatic-reference", useAbsoluteAltitude = true, minAltitudeM = 0, maxAltitudeM = 1000,
                    slopeOptimumDeg = 0, slopeToleranceDeg = 10, droughtTolerance = 0, gddMinRequired = 0,
                    submergedDepthRangeM = new Vector2(0.25f, 2), saltTolerance = 1,
                    moistureOptimum = 1, soilFertilityOptimum = 1 }; // UC-T06-11: synthetic pool envelope matches the observed saturated/fertile reference; terrestrial defaults would correctly thin its 64 sites to zero.
                var settings = new EcoSampleSettings { soilDepthMaxM = 1, soilAngleOfReposeDeg = 45,
                    seaLevelMeanTempC = species.tempOptimumC, annualTempHalfAmplitudeC = 10, annualPrecipMm = species.precipOptimumMm,
                    radiationMjPerM2Day = 15, diurnalTempRangeC = 10, waterSearchRadiusM = 100, avoidWater = true };
                var sampler = new EnvironmentSampler(); sampler.Bind(in settings, height, null, null, null, 0, null); sampler.AttachAcceptedWater(water);
                var site = sampler.Sample(4, 4);
                Check(Mathf.Abs(site.submergedDepthM - 1f) < 1e-5f && Mathf.Abs(site.saltFraction - EcoMath.BodySalinityFraction(WaterBodyKind.Lake)) < 1e-5f &&
                    (sampler.MissingOutputs & EnvironmentChannel.Water) == 0 && sampler.TryCurrentMetresPerSecond(4, 4, out float speed) && Mathf.Abs(speed - 0.75f) < 1e-5f,
                    "UC-T06-11 actual ecology samples accepted root depth/current and labels body-kind salinity through the existing estimate.");
                var adjacentHeight = new Matrix(height) { worldPos = new Vector3(4, 0, 0) };
                var adjacentSampler = new EnvironmentSampler(); adjacentSampler.Bind(in settings, adjacentHeight, null, null, null, 0, null); adjacentSampler.AttachAcceptedWater(water);
                var shared = adjacentSampler.Sample(5, 4); var original = sampler.Sample(5, 4);
                Check(shared.submergedDepthM == original.submergedDepthM && shared.saltFraction == original.saltFraction,
                    "UC-T06-11 overlapping/adjacent chunk samplers agree on absolute-world accepted aquatic observations.");
                Check(!PlacementClearance.WaterAdmits(species, in site, true, 0) && PlacementClearance.WaterAdmits(species, in site, true, 0, true),
                    "UC-T06-11 explicit aquatic production bypasses the global land-water switch while retaining the shared species depth band.");
                var land = new EcoEnvelope { submergedDepthRangeM = Vector2.zero };
                Check(!PlacementClearance.WaterAdmits(land, in site, false, 0, true),
                    "UC-T06-11 aquatic opt-in cannot turn a land-only species into an underwater plant.");
                var dry = site; dry.submergedDepthM = 0; dry.distanceToWaterM = 2;
                Check(VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.Submerged, in site, new Vector2(0.5f, 2), 5, 2) &&
                    !VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.Submerged, in dry, new Vector2(0.5f, 2), 5, 2) &&
                    VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.Emergent, in site, new Vector2(0.5f, 2), 5, 2) &&
                    !VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.Emergent, in site, new Vector2(0.5f, 2), 5, 0.5f) &&
                    VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.WetBank, in dry, Vector2.zero, 3, 2) &&
                    !VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.WetBank, in site, Vector2.zero, 3, 2) &&
                    VegetationHabitatNode.WaterHabitatAdmits(VegetationWaterHabitat.DryLand, in dry, Vector2.zero, 0, 0),
                    "UC-T06-11 submerged, emergent, wet-bank and dry-land restrictions compose distinct root-depth/proximity/stature rules.");
                var groundSnapshot = new RiverChannelSnapshot(run, "aquatic-ground", new Rect(0, 0, 8, 8), Array.Empty<ChannelGraphPublication>(),
                    new float[,] { { 0.09f, 0.09f }, { 0.09f, 0.09f } }, 0, 100);
                tile = new TileData(Coord.Zero, height.rect, height.worldPos, height.worldSize, height.worldHeight);
                tile.AssignRun(run); tile.worldGraphGround = new WorldGraphGround(main, groundSnapshot, water);
                tile.ecology = new EcoScatterContext { settings = settings, objects = new SpeciesTable { count = 1, envelopes = new[] { species } } };
                var constant = new ConstantNode { id = "aquatic-ground-node", value = 0.09f };
                var habitat = new VegetationHabitatNode { id = "aquatic-habitat", family = VegetationHabitatFamily.Objects, speciesName = species.displayName,
                    waterHabitat = VegetationWaterHabitat.Submerged, waterDepthRangeMetres = new Vector2(0.5f, 2), requireAcceptedCurrent = true,
                    currentSpeedRangeMetresPerSecond = new Vector2(0.5f, 1) };
                graph.nodes.Add(constant); graph.nodes.Add(habitat); graph.connections.Add(new Connection(constant.id, 0, habitat.id, 0));
                new GraphEvaluator(graph).Evaluate(tile, new StopToken());
                Matrix mask = (Matrix)habitat.Generate(0, tile, null);
                Check(mask.arr.All(v => !float.IsNaN(v) && v >= 0 && v <= 1) && mask.arr.Any(v => v > 0),
                    "UC-T06-11 real named-species habitat execution reads accepted water/current rather than rerunning an inferred water world.");
                var placements = new List<ScatterInstance>();
                for (int i = 0; i < 64; i++) placements.Add(IdentityInstance("aquatic-world", "underwater:fixture", "aquatic-prototype", 0.5f + i % 8, 0.5f + i / 8));
                bool assigned = SpeciesAssignment.TryAssign(tile, height, "underwater", 1011, placements, null);
                Check(assigned && placements.Count > 0 && placements.All(p => p.prototypeIndex == 0),
                    "UC-T06-11 actual underwater species assignment retains aquatic candidates under the shared land-water guard. " +
                    string.Join("; ", tile.outputs.vegetationPlacementReports.Values.Select(report => report.Stage + " " + report.Inputs + " -> " + report.Outputs)) +
                    "; sampled moisture=" + site.soilMoisture + ", TWI=" + site.twi + ", fertility=" + site.soilFertility +
                    ", suitability=" + SpeciesSuitability.Evaluate(species, in site)); // UC-T06-11/17: report actual rejection stages and reference-site suitability if this real fixture empties.
                var cancelled = new StopToken(); cancelled.RequestStop();
                Check(habitat.Generate(0, tile, cancelled) == null, "UC-T06-11 aquatic habitat cancellation publishes no partial mask.");

                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.09f;
                data.SetHeights(0, 0, heights);
                var instance = IdentityInstance("aquatic-world", "underwater:fixture", "aquatic-prototype", 4, 4);
                instance.ground = new ScatterGroundBinding(0, 0, Quaternion.identity, Vector3.up, 0) {
                    requiresAquaticWater = true, requiresAcceptedWater = true, aquaticDepthRangeMetres = new Vector2(0.5f, 2), fallbackWaterY = 10 };
                var native = new ScatterApplier.FinalGroundContext(terrainRoot.GetComponent<Terrain>(), data, water);
                Check(native.TrySeat(instance, null, Vector3.one, 0, 0, out Vector3 seated, out _) && Mathf.Abs(seated.y - data.GetInterpolatedHeight(0.5f, 0.5f)) < 1e-5f, // UC-T06-11: compare against quantized native ground, not the pre-upload float height.
                    "UC-T06-11 final native aquatic root contact resamples terrain depth while preserving the procedural position key.");
                volume.kind = UCWaterVolume.BodyKind.River;
                volume.SetAcceptedSurface(body, Matrix4x4.Translate(Vector3.up), false, new Vector2(0, 1.25f));
                var river = WorldGraphWaterInput.Capture(world);
                Check(!water.IsCurrent() && river.TryStillObservation(4, 4, out level, out flow, out kind) && level == 11 && kind == WaterBodyKind.River &&
                    Mathf.Abs(flow.magnitude - 1.25f) < 1e-5f && water.TryStillObservation(4, 4, out float oldLevel, out _, out _) && oldLevel == 10,
                    "UC-T06-11 accepted river replacement changes revision/observations without mutating the prior worker capture.");
                volume.kind = UCWaterVolume.BodyKind.Ocean; volume.SetAcceptedSurface(body, Matrix4x4.identity, false);
                var ocean = WorldGraphWaterInput.Capture(world); sampler.AttachAcceptedWater(ocean);
                Check(sampler.Sample(4, 4).saltFraction == EcoMath.BodySalinityFraction(WaterBodyKind.Ocean),
                    "UC-T06-11 ocean body salinity remains an explicit category estimate separate from freshwater and current observations.");
                body.triangles = new[] { 0, 1, 2 }; volume.SetAcceptedSurface(body, Matrix4x4.identity, false);
                sampler.AttachAcceptedWater(WorldGraphWaterInput.Capture(world));
                Check(sampler.Sample(0.5f, 7.5f).submergedDepthM == 0,
                    "UC-T06-11 an exposed island inside broad accepted body bounds remains dry through actual triangle coverage.");
                volume.SetAcceptedSurface(null, Matrix4x4.identity);
                var empty = WorldGraphWaterInput.Capture(world); sampler.AttachAcceptedWater(empty);
                Check(sampler.Sample(4, 4).submergedDepthM == 0 && (sampler.MissingOutputs & EnvironmentChannel.Water) != 0 &&
                    !new ScatterApplier.FinalGroundContext(terrainRoot.GetComponent<Terrain>(), data, empty).TrySeat(instance, null, Vector3.one, 0, 0, out _, out _),
                    "UC-T06-11 retired accepted water reports missing observations and cannot strand an observed aquatic contact onto the legacy plane.");
                Check(UCWorldGraphBindings.WaterChangeAffectsRegion(new Bounds(new Vector3(4, 10, 4), new Vector3(8, 0, 8)), new Rect(8, 0, 8, 8), 1) &&
                    !UCWorldGraphBindings.WaterChangeAffectsRegion(new Bounds(new Vector3(4, 10, 4), new Vector3(8, 0, 8)), new Rect(64, 0, 8, 8), 1),
                    "UC-T06-11 water-change proximity selection includes affected adjacent regions and preserves a distant resident region.");
                tile.ecology.sampler.AttachAcceptedWater(empty); tile.worldGraphGround = new WorldGraphGround(main, groundSnapshot, empty);
                bool missing = false;
                try { habitat.Generate(0, tile, null); } catch (InvalidOperationException error) { missing = error.Message.Contains("current"); }
                Check(missing, "UC-T06-11 missing required current is reported rather than guessed from dimensionless flow accumulation.");
            }
            finally
            {
                tile?.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(terrainRoot); UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(worldRoot); UnityEngine.Object.DestroyImmediate(foreignRoot);
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(main);
            }
        }
    }
}
