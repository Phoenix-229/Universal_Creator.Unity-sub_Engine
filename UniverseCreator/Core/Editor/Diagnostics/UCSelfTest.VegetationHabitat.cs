// UC-T06-04: known environments and poisoned numeric channels must not become accidental dense planting.
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.Ecology;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static EnvironmentSample HabitatReferenceSite(EcoEnvelope species)
        {
            return new EnvironmentSample {
                worldPos = new Vector3(0, 100, 0), normal = Vector3.up, altitudeM = 100,
                aspectDeg = -1, slopeDeg = species.slopeOptimumDeg,
                meanAnnualTempC = species.tempOptimumC, coldestMonthTempC = 0, warmestMonthTempC = 25,
                frostFreeFraction = 1, gdd = 10000, annualPrecipMm = species.precipOptimumMm, petMm = 1000,
                aridity = 1, soilMoisture = species.moistureOptimum, twi = species.twiOptimum,
                soilDepthM = 1, soilFertility = species.soilFertilityOptimum, waterTableDepthM = 10000,
                distanceToWaterM = 10000, subcanopyLightFraction = 1, canopyLightFraction = 1,
                floorLightFraction = 1, biomeIndex = -1, biomeIndexAlt = -1, biomeBlend = 1
            };
        }

        private static void TestVegetationHabitat()
        {
            var species = new EcoEnvelope { displayName = "habitat-reference", useAbsoluteAltitude = true,
                minAltitudeM = 0, maxAltitudeM = 1000, slopeOptimumDeg = 0, slopeToleranceDeg = 10,
                droughtTolerance = 0, gddMinRequired = 0 };
            var site = HabitatReferenceSite(species);
            Check(site.IsValidForHabitat() && SpeciesSuitability.Evaluate(species, in site) > 0.99f,
                "UC-T06-04 independently constructed optimum scores full suitability.");
            var changed = site; changed.slopeDeg = 10;
            Check(Math.Abs(SpeciesSuitability.Evaluate(species, in changed) - Math.Exp(-2)) < 0.0001,
                "UC-T06-04 one slope tolerance follows the known Gaussian rather than a hard plateau.");
            foreach (ActionEnvironment mutate in new ActionEnvironment[] {
                (ref EnvironmentSample s) => s.coldestMonthTempC = species.tempLethalMinC - 1,
                (ref EnvironmentSample s) => s.warmestMonthTempC = species.tempLethalMaxC + 1,
                (ref EnvironmentSample s) => s.altitudeM = 1001,
                (ref EnvironmentSample s) => s.slopeDeg = 21,
                (ref EnvironmentSample s) => s.soilDepthM = 0,
                (ref EnvironmentSample s) => s.rockiness = 1,
                (ref EnvironmentSample s) => s.saltFraction = 1,
                (ref EnvironmentSample s) => s.submergedDepthM = 2
            })
            {
                changed = site; mutate(ref changed);
                Check(SpeciesSuitability.Evaluate(species, in changed) == 0,
                    "UC-T06-04 independent climate/altitude/slope/substrate/salt/submergence hard exclusion.");
            }
            var desert = new EcoEnvelope { moistureOptimum = 0.1f, moistureTolerance = 0.2f, droughtTolerance = 0 };
            var wetland = new EcoEnvelope { moistureOptimum = 0.9f, moistureTolerance = 0.2f, droughtTolerance = 0 };
            changed = site; changed.soilMoisture = 0.1f;
            Check(SpeciesSuitability.Evaluate(desert, in changed) > SpeciesSuitability.Evaluate(wetland, in changed),
                "UC-T06-04 dry moisture independently favors desert habitat.");
            changed.soilMoisture = 0.9f;
            Check(SpeciesSuitability.Evaluate(wetland, in changed) > SpeciesSuitability.Evaluate(desert, in changed),
                "UC-T06-04 wet moisture independently favors wetland habitat.");
            species.distanceToWaterMinM = 5; species.distanceToWaterMaxM = 20;
            changed = site; changed.hasWaterInRange = true; changed.distanceToWaterM = 4;
            Check(SpeciesSuitability.Evaluate(species, in changed) == 0, "UC-T06-04 shoreline clearance excludes the inner band.");
            changed.distanceToWaterM = 10;
            Check(SpeciesSuitability.Evaluate(species, in changed) > 0, "UC-T06-04 riparian habitat admits its authored metre band.");
            changed.distanceToWaterM = 21;
            Check(SpeciesSuitability.Evaluate(species, in changed) == 0, "UC-T06-04 riparian habitat excludes the outer band.");
            species.distanceToWaterMinM = 0; species.distanceToWaterMaxM = -1;
            species.submergedDepthRangeM = new Vector2(0.5f, 2);
            changed = site; changed.submergedDepthM = 1; changed.hasWaterInRange = true;
            Check(SpeciesSuitability.Evaluate(species, in site) == 0 && SpeciesSuitability.Evaluate(species, in changed) > 0,
                "UC-T06-04 aquatic depth habitat excludes dry land and admits its bounded water depth.");
            species.submergedDepthRangeM = Vector2.zero;
            species.allowedBiomes = WhittakerBiome.MaskOf(WhittakerBiome.TemperateForest);
            changed = site; changed.biomeIndex = (sbyte)WhittakerBiome.Desert;
            var biomeWeights = new float[1];
            Check(SpeciesSelector.BuildWeights(new[] { species }, null, null, 1, in changed, 0, 1, biomeWeights) == 0,
                "UC-T06-04 a classified desert excludes a forest-only species despite suitable climate.");
            changed.biomeIndex = (sbyte)WhittakerBiome.TemperateForest; changed.biomeIndexAlt = (sbyte)WhittakerBiome.Desert;
            changed.biomeBlend = 0;
            Check(Math.Abs(BiomeAffinity.Of(species, in changed) - 0.5f) < 0.0001f,
                "UC-T06-04 forest/desert boundary has the independently known half affinity.");
            species.allowedBiomes = 0;

            foreach (FieldInfo field in typeof(EnvironmentSample).GetFields().Where(f => f.FieldType == typeof(float)))
                foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                {
                    object boxed = site; field.SetValue(boxed, invalid); changed = (EnvironmentSample)boxed;
                    var weights = new[] { 7f }; var scores = new[] { 7f };
                    Check(SpeciesSuitability.Evaluate(species, in changed) == 0 &&
                        SpeciesSelector.BuildWeights(new[] { species }, null, null, 1, in changed, 0, 1, weights, scores) == 0 &&
                        weights[0] == 0 && scores[0] == 0,
                        "UC-T06-04 invalid environment rejects and clears prior selection: " + field.Name + "=" + invalid);
                }
            foreach (string name in new[] { "tempLethalMinC", "rockinessMax", "slopeToleranceDeg", "distanceToWaterMaxM", "tempOptimumC" })
            {
                FieldInfo field = typeof(EcoEnvelope).GetField(name); object original = field.GetValue(species);
                field.SetValue(species, float.NaN);
                Check(SpeciesSuitability.Evaluate(species, in site) == 0, "UC-T06-04 poisoned authored hard gate/curve rejects: " + name);
                field.SetValue(species, original);
            }
            var selected = new[] { 5f };
            Check(SpeciesSelector.BuildWeights(new[] { species }, new[] { float.NaN }, null, 1, in site, 0, 1, selected) == 0 && selected[0] == 0,
                "UC-T06-04 nonfinite authored weight vetoes placement.");
            Check(SpeciesSelector.BuildWeights(new[] { species }, null, new[] { float.PositiveInfinity }, 1, in site, 0, 1, selected) == 0 &&
                SpeciesSelector.BuildWeights(new[] { species }, null, null, 1, in site, float.NaN, 1, selected) == 0 && selected[0] == 0,
                "UC-T06-04 invalid dispersal and selection controls cannot revive prior weights.");
            Check(SpeciesSelector.SelectWeighted(new[] { 1f, float.NaN }, 2, 0.5f) == 0 &&
                SpeciesSelector.SelectBest(new[] { float.NaN, 1f }, 2) == 1 &&
                SpeciesSelector.SelectWeighted(new[] { 1f }, 1, float.NaN) == -1 &&
                SpeciesSelector.SelectWeighted(new[] { float.MaxValue, float.MaxValue }, 2, 0.25f) == 0 &&
                SpeciesSelector.SelectWeighted(new[] { float.MaxValue, float.MaxValue }, 2, 0.75f) == 1,
                "UC-T06-04 weighted selection skips poisoned entries/draws and preserves proportions without sum overflow.");
            TestVegetationHabitatMask(species);
        }

        private delegate void ActionEnvironment(ref EnvironmentSample site);

        private static void TestVegetationHabitatMask(EcoEnvelope species)
        {
            var graph = ScriptableObject.CreateInstance<Graph>();
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 9, 9), Vector3.zero, new Vector3(8, 100, 8), 100);
            try
            {
                graph.SetDomain(UCGraphDomain.Vegetation);
                var ground = new ConstantNode { id = "habitat-ground", value = 0.2f };
                var node = new VegetationHabitatNode { id = "habitat-mask", speciesName = " HABITAT-REFERENCE " };
                graph.nodes.Add(ground); graph.nodes.Add(node);
                graph.connections.Add(new Connection(ground.id, 0, node.id, 0));
                tile.ecology = new EcoScatterContext { trees = new SpeciesTable { count = 1, envelopes = new[] { species } } };
                // Bind a known, finite context explicitly: no live manager or guessed climate defaults are needed.
                Matrix height = tile.CreateMatrix(); Array.Fill(height.arr, 0.2f);
                var settings = new EcoSampleSettings { soilDepthMaxM = 1, soilAngleOfReposeDeg = 45,
                    seaLevelMeanTempC = species.tempOptimumC, annualTempHalfAmplitudeC = 10,
                    annualPrecipMm = species.precipOptimumMm, radiationMjPerM2Day = 15, diurnalTempRangeC = 10, waterSearchRadiusM = 100 };
                tile.ecology.settings = settings;
                tile.ecology.sampler = new EnvironmentSampler();
                tile.ecology.sampler.Bind(in settings, height, null, null, null, 0, null);
                var evaluator = new GraphEvaluator(graph); evaluator.Evaluate(tile, new StopToken());
                Matrix mask = (Matrix)node.Generate(0, tile, new StopToken());
                Check(mask != null && mask.arr.Any(v => v > 0) && mask.arr.All(v => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0 && v <= 1),
                    "UC-T06-04 named species habitat executes through the graph and produces a bounded lattice mask.");
                node.requiredChannels |= EnvironmentChannel.Moisture;
                bool rejected = false;
                try { node.Generate(0, tile, null); } catch (InvalidOperationException error) { rejected = error.Message.Contains("Moisture"); }
                Check(rejected, "UC-T06-04 missing required environment channel reports its actual name.");
                node.requiredChannels = EnvironmentChannel.Height; node.speciesName = "not-assigned"; rejected = false;
                try { node.Generate(0, tile, null); } catch (InvalidOperationException error) { rejected = error.Message.Contains("not-assigned"); }
                Check(rejected, "UC-T06-04 missing named prototype reports an error instead of scattering slot zero.");
                var stop = new StopToken(); stop.RequestStop();
                Check(node.Generate(0, tile, stop) == null, "UC-T06-04 cancelled habitat work returns no partial mask.");
            }
            catch (Exception error) { Check(false, "UC-T06-04 habitat graph exception: " + error); }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
