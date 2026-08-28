// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Aquatic.cs
//  Purpose: Fully submerged plants. Placed by the depth of the water above them
//           rather than by the height of the ground beneath them.
//
//  THE ASYMMETRY THAT SHAPES THIS FILE. Salt excludes but cannot require. A high
//  salt fraction rejects any species whose tolerance is lower, so the freshwater
//  weed below keeps itself out of the sea with no extra machinery at all - but the
//  reverse does not hold, and a kelp given nothing but a tolerance for salt was
//  admissible in a deep freshwater lake. TidalZone.Subtidal exists to close that,
//  and kelp and seagrass both state it.
//
//  KELP AND SEAGRASS DIVIDE THE SEA BETWEEN THEM on two numbers each:
//
//    substrate  kelp grips bare rock with a holdfast and needs no soil at all, so
//               its rockiness ceiling is total and its soil depth requirement is
//               zero. Seagrass is a flowering plant with real roots and must have
//               sediment, so its rockiness ceiling is low. Kelp on the reef,
//               seagrass on the sand flat, and neither in the other's place.
//    temperature kelp dies above twenty-two degrees - the lowest upper limit
//               anywhere in the table, and the reason kelp forest stops dead where
//               a warm current starts. Seagrass runs to thirty-five and takes the
//               warm water kelp cannot.
//
//  THREE FIELDS ARE DELIBERATELY MADE TOOTHLESS HERE, because they measure things
//  that do not exist under water:
//    topographic wetness  meaningless on a seabed, so the tolerance is set wide
//                         enough that the term is always about one rather than
//                         being left to reject the species on noise.
//    rainfall             the same, and for the same reason.
//    soil fertility       kelp feeds from the water column, not the substrate.
//  Growing degree days are left at the baseline of zero: air-temperature heat sums
//  say nothing about a plant that never touches the air.
//
//  And snow load is generous throughout, as in the wetland file - a plant three
//  metres under a frozen lake or a winter sea is unaffected by the load the site
//  reports at the surface.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        // Wide enough to make the wetness term inert. Under water the index has no
        // meaning, and a plant that never dries out must not be rejected because
        // the seabed sample happened to report a ridge.
        private const float SubmergedTwiOptimum = 8f;
        private const float SubmergedTwiTolerance = 12f;

        // The site over a frozen lake or a winter sea reports the full surface load.
        // The plant is metres beneath it.
        private const float SubmergedSnowLoadKgPerM2 = 600f;

        private static bool ApplyAquatic(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.Kelp: Kelp(eco); return true;
                case SpeciesPreset.Seagrass: Seagrass(eco); return true;
                case SpeciesPreset.FreshwaterWeed: FreshwaterWeed(eco); return true;
                default: return false;
            }
        }

        private static void Kelp(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Kelp", LifeForm.Aquatic);

            // A kelp forest on a cold rocky reef is the end state, and it holds
            // until something eats it.
            eco.successionStage = SuccessionStage.Climax;

            // It really is a canopy. Macrocystis reaches the surface and lays a
            // floating layer over everything below it.
            eco.canopyLayer = CanopyLayer.Canopy;
            eco.fireResponse = FireResponse.Killed;

            eco.tidalZone = TidalZone.Subtidal;

            // From just below low water to the depth where the light gives out.
            eco.submergedDepthRangeM = new Vector2(2f, 30f);

            eco.tempOptimumC = 11f;

            // Narrow, and cold. Kelp is a cold-water organism and there is no warm
            // kelp anywhere.
            eco.tempToleranceC = 6f;

            // Sea water does not go below about minus two, so cold never kills it.
            eco.tempLethalMinC = -3f;

            // THE LOWEST UPPER LIMIT IN THE TABLE, and the most consequential
            // number in this file. Above roughly twenty-two degrees kelp dies, and
            // that is why kelp forest ends where a warm current begins and why a
            // marine heatwave strips a coastline of it in a single season.
            eco.tempLethalMaxC = 22f;

            eco.moistureOptimum = 1f;
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 1000f;
            eco.precipToleranceMm = 900f;
            eco.twiOptimum = SubmergedTwiOptimum;
            eco.twiTolerance = SubmergedTwiTolerance;
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.02f;

            // Full sea water.
            eco.saltTolerance = 1f;

            // A holdfast is a claw, not a root. Bare vertical rock is the preferred
            // substrate, so the rockiness ceiling is total and the soil requirement
            // is zero - the one place in the table where no soil at all is the
            // right answer rather than a marginal one.
            eco.rockinessMax = 1f;
            eco.soilDepthMinM = 0f;
            eco.slopeOptimumDeg = 20f;
            eco.slopeToleranceDeg = 30f;

            // Fed from the water column, so what the substrate contains is beside
            // the point. The wide tolerance says exactly that.
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.5f;

            // Dense forest, and the understory species live on very little.
            eco.shadeTolerance = 0.6f;
            eco.laiContribution = 4.5f;

            // Spores travel metres, not kilometres. This is why a cleared reef
            // takes years to refill and why kelp beds have hard edges.
            eco.dispersalLambdaM = 15f;

            eco.matureHeightM = new Vector2(3f, 25f);

            // Stipe and holdfast width.
            eco.dbhRangeM = new Vector2(0.05f, 0.3f);
            eco.crownAllometryA = 1.2f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 0.4f;

            // The most flexible thing in the table. Kelp has no rigid tissue at all
            // and streams flat in a current, which is the whole of how it survives
            // surf that would break any stem.
            eco.trunkFlexibility = 0.95f;
            eco.selfThinningK = 30000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = SubmergedSnowLoadKgPerM2;

            // The lowest browse tolerance in the table, and it is not an
            // exaggeration: sea urchins clear-cut kelp forest to bare rock and hold
            // it there for decades. Grazing is the main thing that destroys kelp.
            eco.browseTolerance = 0.15f;

            // Brown algae, and genuinely brown - olive to chocolate rather than
            // green. Getting this wrong would make a reef look like a lawn.
            eco.springColour = new Color(0.32f, 0.26f, 0.12f, 1f);
            eco.summerColour = new Color(0.28f, 0.22f, 0.10f, 1f);
            eco.autumnColour = new Color(0.26f, 0.20f, 0.10f, 1f);
            eco.winterColour = new Color(0.24f, 0.19f, 0.10f, 1f);
            eco.wetDarkening = 0.6f;
        }

        private static void Seagrass(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Seagrass", LifeForm.Aquatic);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            eco.tidalZone = TidalZone.Subtidal;

            // Shallower than kelp, because it is a flowering plant running on
            // photosynthesis at the bed rather than at the surface.
            eco.submergedDepthRangeM = new Vector2(0.5f, 12f);

            eco.tempOptimumC = 18f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -2f;

            // Thirteen degrees more headroom than kelp, which is the whole reason
            // there is anything green on a tropical seabed.
            eco.tempLethalMaxC = 35f;

            eco.moistureOptimum = 1f;
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 1200f;
            eco.precipToleranceMm = 1000f;
            eco.twiOptimum = SubmergedTwiOptimum;
            eco.twiTolerance = SubmergedTwiTolerance;
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.03f;
            eco.saltTolerance = 1f;

            // A real rooted plant, so it must have sediment. This single ceiling is
            // what puts seagrass on the sand flat and keeps it off the reef kelp
            // occupies a few metres away.
            eco.rockinessMax = 0.2f;
            eco.soilDepthMinM = 0.3f;
            eco.slopeOptimumDeg = 1f;
            eco.slopeToleranceDeg = 4f;
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.4f;

            // It needs clear water. Turbidity, not depth, is what kills a seagrass
            // meadow, and a low shade tolerance is how that is said here.
            eco.shadeTolerance = 0.35f;
            eco.laiContribution = 2.5f;

            // Seed floats, and torn rhizome mats raft a long way.
            eco.dispersalLambdaM = 100f;

            eco.matureHeightM = new Vector2(0.15f, 1.2f);

            // Patch width.
            eco.dbhRangeM = new Vector2(0.2f, 0.8f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 0.5f;
            eco.trunkFlexibility = 0.9f;

            // A continuous meadow.
            eco.selfThinningK = 400000f;

            // Rather less than kelp. Seagrass grows in sheltered bays and lagoons;
            // surf tears the rhizome mat out of the sand.
            eco.windExposureMax = 0.85f;
            eco.snowLoadMaxKgPerM2 = SubmergedSnowLoadKgPerM2;

            // Turtles, dugongs, manatees and wintering geese all graze it hard.
            eco.browseTolerance = 0.2f;

            // Green, and the contrast with kelp's brown is worth keeping - these
            // two are the same size, in the same water, and look nothing alike.
            eco.springColour = new Color(0.34f, 0.56f, 0.34f, 1f);
            eco.summerColour = new Color(0.30f, 0.52f, 0.32f, 1f);
            eco.autumnColour = new Color(0.34f, 0.50f, 0.30f, 1f);
            eco.winterColour = new Color(0.30f, 0.46f, 0.30f, 1f);
            eco.wetDarkening = 0.55f;
        }

        private static void FreshwaterWeed(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Freshwater Weed", LifeForm.Aquatic);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            // No tidal zone, and that omission is the definition of this species.
            // Stated rather than inherited because it is the field that matters
            // most here: the salt rejection keeps it out of the sea by itself, so
            // it needs no marine gate and must not have one.
            eco.saltTolerance = 0.05f;

            eco.submergedDepthRangeM = new Vector2(0.2f, 6f);

            eco.tempOptimumC = 16f;

            // Wide. Pondweeds are as close to cosmopolitan as fresh water gets.
            eco.tempToleranceC = 13f;

            // Overwinters as a rhizome or a turion in the silt below the ice, so
            // the air above the lake barely matters.
            eco.tempLethalMinC = -30f;
            eco.tempLethalMaxC = 40f;
            eco.warmestMonthMinC = 11f;
            eco.gddMinRequired = 700f;

            eco.moistureOptimum = 1f;
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 800f;
            eco.twiOptimum = SubmergedTwiOptimum;
            eco.twiTolerance = SubmergedTwiTolerance;
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.03f;

            eco.slopeOptimumDeg = 2f;
            eco.slopeToleranceDeg = 6f;

            // Rooted in silt.
            eco.rockinessMax = 0.3f;
            eco.soilDepthMinM = 0.2f;

            // Enriched water is where pondweed becomes a nuisance, and a high
            // optimum is the honest way to say that a choked ditch is a fertilised
            // one.
            eco.soilFertilityOptimum = 0.7f;
            eco.soilFertilityTolerance = 0.3f;

            eco.shadeTolerance = 0.4f;
            eco.laiContribution = 1.5f;

            // Waterfowl carry fragments between water bodies on their feet and
            // feathers. That is the actual mechanism, and it is why two ponds with
            // no channel between them hold the same weed.
            eco.dispersalLambdaM = 60f;

            eco.matureHeightM = new Vector2(0.1f, 0.6f);
            eco.dbhRangeM = new Vector2(0.15f, 0.6f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 0.4f;
            eco.trunkFlexibility = 0.9f;
            eco.selfThinningK = 200000f;
            eco.windExposureMax = 0.7f;
            eco.snowLoadMaxKgPerM2 = SubmergedSnowLoadKgPerM2;
            eco.browseTolerance = 0.3f;

            // Collapses to almost nothing over winter and regrows from the bed.
            eco.deciduous = true;
            eco.budburstGdd = 180f;
            eco.senescenceDayLengthH = 12.5f;
            eco.leafOffFraction = 0.9f;
            eco.seasonalHeightScale = new Vector2(0.25f, 1f);

            eco.springColour = new Color(0.38f, 0.58f, 0.32f, 1f);
            eco.summerColour = new Color(0.32f, 0.52f, 0.28f, 1f);
            eco.autumnColour = new Color(0.40f, 0.48f, 0.26f, 1f);
            eco.winterColour = new Color(0.34f, 0.42f, 0.28f, 1f);
            eco.wetDarkening = 0.55f;
        }
    }
}
