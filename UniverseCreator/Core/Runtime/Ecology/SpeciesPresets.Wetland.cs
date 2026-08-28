// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Wetland.cs
//  Purpose: Freshwater marsh, fen and bog plants.
//
//  THREE GRADIENTS BUILD A WETLAND, and all three are stated here rather than
//  imposed by a placement rule:
//
//  1. DEPTH. Flood tolerance on a land plant is read as metres of standing water it
//     will accept, so it doubles as a depth limit and the group sorts itself into
//     bands out from the shore:
//       water lily   0.3 - 2.5 m of open water, and it is the only aquatic here
//       cattail      up to a metre, and it must have water at the surface
//       reed         up to a metre, but happy on merely saturated ground as well
//       sedge        saturated to about half a metre
//       sphagnum     wet, never flooded. Deep water drowns it.
//
//  2. NUTRIENTS. This is the gradient that decides which wetland you are looking
//     at, and it runs the opposite way to intuition - the poorest ground grows the
//     rarest thing:
//       cattail 0.80  eutrophic. The indicator of nutrient enrichment.
//       reed    0.70  eutrophic.
//       sedge   0.45  mesotrophic fen.
//       sphagnum 0.10 oligotrophic bog, with a narrow tolerance, so fertile ground
//                     excludes it outright. That is not fragility: on rich ground
//                     vascular plants simply outgrow it and shade it out.
//
//  3. CLIMATE. Reed lines irrigation canals in deserts, so its aridity floor is
//     nearly zero. A raised bog cannot exist unless rainfall exceeds evaporation,
//     so sphagnum's floor is above one. That single pair of numbers is why reedbeds
//     are everywhere and bogs are only in cool oceanic places.
//
//  A TRAP WORTH NAMING. Snow load limits must be generous for everything in this
//  file. A water lily thrives in Finland, and it does it by spending the winter as
//  a rhizome under the ice - but the site still reports a heavy snow load, and a
//  low limit here would wrongly exclude water lilies from every snowy region on
//  the map. The same applies to sphagnum, which is buried all winter and does not
//  care.
//
//  Drought tolerance in this file holds the two lowest values anywhere: the water
//  lily at 0.02, which is dead within hours of losing its water, and sphagnum at
//  0.05, which has no roots and no vascular tissue and stops photosynthesising as
//  soon as its surface dries.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyWetland(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.Reed: Reed(eco); return true;
                case SpeciesPreset.Sedge: Sedge(eco); return true;
                case SpeciesPreset.Cattail: Cattail(eco); return true;
                case SpeciesPreset.SphagnumMoss: SphagnumMoss(eco); return true;
                case SpeciesPreset.WaterLily: WaterLily(eco); return true;
                default: return false;
            }
        }

        private static void Reed(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Reed", LifeForm.Graminoid);

            // Early rather than pioneer: reed takes over shallow water that
            // something else has already silted up, and holds it until the ground
            // it built is dry enough for scrub.
            eco.successionStage = SuccessionStage.Early;

            // The tallest thing in a reedbed by a wide margin, so it is the canopy
            // even though it is a grass.
            eco.canopyLayer = CanopyLayer.Canopy;

            // A reedbed burns hot and fast and is back to full height in one season
            // from the rhizome mat, which is why burning is how reedbeds are
            // managed rather than how they are destroyed.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 15f;

            // Among the widest in the table, and genuinely so: this is very nearly
            // a cosmopolitan plant.
            eco.tempToleranceC = 14f;
            eco.tempLethalMinC = -35f;
            eco.tempLethalMaxC = 48f;
            eco.warmestMonthMinC = 10f;
            eco.frostFreeFractionMin = 0.25f;
            eco.gddMinRequired = 1000f;

            eco.moistureOptimum = 0.95f;
            eco.moistureTolerance = 0.12f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 800f;

            // Almost no floor at all - the lowest in this file. Reed lines
            // irrigation canals through open desert, because the water it needs is
            // in the ditch and not in the sky.
            eco.aridityMin = 0.05f;

            eco.twiOptimum = 11f;
            eco.twiTolerance = 4f;
            eco.waterTableMaxDepthM = 0.1f;

            // A metre of standing water, which is about the real limit for reed.
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.15f;
            eco.distanceToWaterMaxM = 150f;

            // Takes brackish water where cattail cannot, which is why the marsh at
            // the head of an estuary is reed and the freshwater ditch behind it is
            // cattail.
            eco.saltTolerance = 0.35f;

            eco.slopeOptimumDeg = 1f;
            eco.slopeToleranceDeg = 4f;
            eco.soilDepthMinM = 0.4f;
            eco.soilFertilityOptimum = 0.7f;
            eco.soilFertilityTolerance = 0.3f;
            eco.rockinessMax = 0.15f;

            eco.shadeTolerance = 0.15f;

            // Dense enough that a reedbed is dark at ground level in midsummer, and
            // dense enough to exclude nearly everything else.
            eco.laiContribution = 4f;

            // Plumed seed on the wind, plus rhizome fragments that float. Either
            // way it reaches a new ditch quickly.
            eco.dispersalLambdaM = 250f;

            eco.matureHeightM = new Vector2(1.5f, 4f);

            // Clump width rather than stem diameter.
            eco.dbhRangeM = new Vector2(0.3f, 0.8f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.5f;
            eco.trunkFlexibility = 0.8f;
            eco.selfThinningK = 400000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 100f;
            eco.browseTolerance = 0.5f;

            eco.springColour = new Color(0.44f, 0.58f, 0.32f, 1f);
            eco.summerColour = new Color(0.38f, 0.52f, 0.28f, 1f);

            // The dead stems stand all winter as pale straw, and that colour is
            // most of what a winter reedbed looks like.
            eco.autumnColour = new Color(0.68f, 0.60f, 0.36f, 1f);
            eco.winterColour = new Color(0.72f, 0.64f, 0.44f, 1f);

            // Keeps most of its height through winter as standing dead straw,
            // unlike a sward that collapses to stubble.
            eco.seasonalHeightScale = new Vector2(0.75f, 1f);
        }

        private static void Sedge(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Sedge", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            // Cooler than reed, and that is the separation: sedge fen replaces
            // reedbed as you go north, and sedge carries on where reed cannot.
            eco.tempOptimumC = 11f;
            eco.tempToleranceC = 13f;
            eco.tempLethalMinC = -45f;
            eco.tempLethalMaxC = 42f;

            // Below the treeline threshold on purpose. Sedge is a dominant of wet
            // tundra, where no tree can stand, and a gate set at the treeline would
            // wrongly strip it out of exactly the ground it owns.
            eco.warmestMonthMinC = 5f;
            eco.frostFreeFractionMin = 0.1f;
            eco.gddMinRequired = 500f;

            eco.moistureOptimum = 0.9f;
            eco.moistureTolerance = 0.15f;
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 500f;

            // Well above reed's floor. A sedge fen needs a genuinely humid climate;
            // it does not line canals through a desert.
            eco.aridityMin = 0.5f;

            eco.twiOptimum = 10f;
            eco.twiTolerance = 4f;
            eco.waterTableMaxDepthM = 0.2f;
            eco.floodTolerance = 0.6f;
            eco.droughtTolerance = 0.2f;
            eco.distanceToWaterMaxM = 250f;

            eco.slopeOptimumDeg = 3f;
            eco.slopeToleranceDeg = 8f;
            eco.soilDepthMinM = 0.25f;

            // Mesotrophic - the middle rung of the three-step fertility gradient
            // this file is built around, and the reason a fen sits between a
            // reedbed and a bog rather than being a version of either.
            eco.soilFertilityOptimum = 0.45f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.25f;

            eco.shadeTolerance = 0.25f;
            eco.laiContribution = 2.8f;
            eco.dispersalLambdaM = 60f;

            eco.matureHeightM = new Vector2(0.2f, 1.2f);
            eco.dbhRangeM = new Vector2(0.2f, 0.7f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 0.5f;
            eco.trunkFlexibility = 0.7f;
            eco.selfThinningK = 350000f;
            eco.windExposureMax = 1f;

            // Spends half the year under snow in the ground it dominates, so the
            // limit has to be generous or the boreal fen empties out.
            eco.snowLoadMaxKgPerM2 = 250f;

            // Grazed extremely hard by geese, deer and reindeer, and it holds.
            eco.browseTolerance = 0.35f;

            eco.springColour = new Color(0.46f, 0.60f, 0.34f, 1f);
            eco.summerColour = new Color(0.38f, 0.52f, 0.30f, 1f);
            eco.autumnColour = new Color(0.62f, 0.54f, 0.30f, 1f);
            eco.winterColour = new Color(0.56f, 0.50f, 0.34f, 1f);
            eco.seasonalHeightScale = new Vector2(0.45f, 1f);
        }

        private static void Cattail(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Cattail", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.Canopy;
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 16f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -30f;
            eco.tempLethalMaxC = 46f;
            eco.warmestMonthMinC = 12f;
            eco.frostFreeFractionMin = 0.35f;
            eco.gddMinRequired = 1200f;

            eco.moistureOptimum = 0.95f;

            // The narrowest moisture tolerance in this file. Cattail does not do dry
            // at any point in its life.
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 700f;
            eco.aridityMin = 0.15f;

            eco.twiOptimum = 11.5f;
            eco.twiTolerance = 3.5f;

            // Water at the surface, not merely near it. This is what keeps cattail
            // in the ditch and the pond edge while reed spreads onto damp ground.
            eco.waterTableMaxDepthM = 0.05f;
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.1f;
            eco.distanceToWaterMaxM = 120f;

            // Far less than reed's. Salt is the boundary between them.
            eco.saltTolerance = 0.15f;

            eco.slopeOptimumDeg = 1f;
            eco.slopeToleranceDeg = 3f;
            eco.soilDepthMinM = 0.4f;

            // The most nutrient-hungry plant in the whole table. A ditch that turns
            // solid cattail is a ditch that is being fertilised, and that is a real
            // diagnostic rather than a stylistic choice.
            eco.soilFertilityOptimum = 0.8f;
            eco.soilFertilityTolerance = 0.25f;
            eco.rockinessMax = 0.1f;

            eco.shadeTolerance = 0.12f;
            eco.laiContribution = 3.6f;

            // A quarter of a million plumed seeds off one head. The longest wind
            // dispersal in this file, and why a new pond has cattail in it within a
            // year of being dug.
            eco.dispersalLambdaM = 350f;

            eco.matureHeightM = new Vector2(1f, 3f);
            eco.dbhRangeM = new Vector2(0.25f, 0.6f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.2f;
            eco.trunkFlexibility = 0.75f;
            eco.selfThinningK = 300000f;

            // Slightly less than reed. Broad flat leaves shred in a gale where a
            // narrow reed leaf simply streams.
            eco.windExposureMax = 0.9f;
            eco.snowLoadMaxKgPerM2 = 80f;

            // Starchy rhizomes and soft leaf bases. Muskrat, geese and deer all
            // work a cattail stand hard.
            eco.browseTolerance = 0.3f;

            eco.springColour = new Color(0.40f, 0.56f, 0.30f, 1f);
            eco.summerColour = new Color(0.34f, 0.50f, 0.27f, 1f);
            eco.autumnColour = new Color(0.64f, 0.56f, 0.32f, 1f);
            eco.winterColour = new Color(0.60f, 0.52f, 0.36f, 1f);
            eco.seasonalHeightScale = new Vector2(0.55f, 1f);
        }

        private static void SphagnumMoss(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Sphagnum Moss", LifeForm.Moss);

            // A raised bog is not on its way to becoming anything else. It builds
            // its own acid waterlogged substrate metre by metre and holds it for
            // thousands of years, which makes it a climax by any honest reading.
            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.GroundCover;

            // Burnt peat does not come back in a human lifetime. There is no
            // resprouting and no seed bank - just bare oxidising peat.
            eco.fireResponse = FireResponse.Killed;

            eco.tempOptimumC = 8f;
            eco.tempToleranceC = 10f;
            eco.tempLethalMinC = -50f;

            // A low upper limit for once. Heat kills sphagnum by drying it, not by
            // cooking it, and thirty-two degrees of air over a bog surface is
            // already lethal.
            eco.tempLethalMaxC = 32f;
            eco.warmestMonthMinC = 5f;
            eco.frostFreeFractionMin = 0.1f;
            eco.gddMinRequired = 350f;

            eco.moistureOptimum = 0.95f;
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 1400f;
            eco.precipToleranceMm = 600f;

            // THE CONSTRAINT THAT DEFINES A BOG. Rain must exceed evaporation, or
            // the peat dries, oxidises and is gone. This one number is why raised
            // and blanket bog occurs in cool oceanic climates and nowhere else,
            // however wet the ground looks.
            eco.aridityMin = 1.2f;

            eco.twiOptimum = 9f;
            eco.twiTolerance = 4f;
            eco.waterTableMaxDepthM = 0.3f;

            // It is not an aquatic. A sphagnum lawn floats on water it holds itself,
            // and standing water over the surface drowns it.
            eco.floodTolerance = 0.3f;

            // No roots and no vascular tissue at all. When the surface dries it
            // stops working within hours, and only the water it holds in its own
            // dead cells keeps it alive between showers.
            eco.droughtTolerance = 0.05f;

            eco.slopeOptimumDeg = 2f;
            eco.slopeToleranceDeg = 6f;
            eco.soilDepthMinM = 0.3f;

            // Oligotrophic, and the narrow tolerance is doing deliberate work: rich
            // ground must exclude sphagnum. Not because it is delicate, but because
            // on fertile ground taller plants simply grow over it. Its answer is to
            // acidify and impoverish its own site until they cannot.
            eco.soilFertilityOptimum = 0.1f;
            eco.soilFertilityTolerance = 0.2f;
            eco.rockinessMax = 0.1f;

            // Bog margins run out under open pine and birch, so it needs to survive
            // half-light without needing it.
            eco.shadeTolerance = 0.4f;
            eco.laiContribution = 1f;
            eco.dispersalLambdaM = 40f;

            eco.matureHeightM = new Vector2(0.05f, 0.4f);

            // Hummock width. There is no stem to measure.
            eco.dbhRangeM = new Vector2(0.3f, 1f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.4f;

            // No roots whatsoever - the figure is a token, not an anchor.
            eco.rootPlateRadiusM = 0.05f;

            // Rigid, and that is the visual point: a moss carpet does not move in
            // the wind, and animating one would look wrong immediately.
            eco.trunkFlexibility = 0.05f;

            // A continuous carpet - the highest density in the table, because there
            // are no individuals to space apart.
            eco.selfThinningK = 800000f;

            // Blanket bog holds the most wind-scoured ground in the north Atlantic.
            // The desiccating wind that ought to kill it is answered by rain that
            // never stops, and the aridity floor above is what states that.
            eco.windExposureMax = 1f;

            // Buried all winter and indifferent to it. A low limit here would strip
            // sphagnum out of every snowy region, which is most of where it lives.
            eco.snowLoadMaxKgPerM2 = 600f;
            eco.avalancheTolerance = 0.2f;

            // Acidic, nutrient-free and close to indigestible.
            eco.browseTolerance = 0.6f;

            // Sphagnum runs from pale yellow-green to deep rust red depending on
            // species and sun, and the red is what makes a bog surface read as a
            // bog rather than as wet grass.
            eco.springColour = new Color(0.52f, 0.62f, 0.38f, 1f);
            eco.summerColour = new Color(0.55f, 0.60f, 0.34f, 1f);
            eco.autumnColour = new Color(0.62f, 0.42f, 0.30f, 1f);
            eco.winterColour = new Color(0.56f, 0.40f, 0.32f, 1f);

            // Permanently saturated, and it shows - a wet bog surface is markedly
            // darker and glossier than a drying one.
            eco.wetDarkening = 0.5f;
        }

        private static void WaterLily(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Water Lily", LifeForm.Aquatic);

            eco.successionStage = SuccessionStage.Mid;

            // Floating on the surface, which is the ground layer as far as anything
            // living in the water is concerned.
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            // THE FIELD THAT MAKES THIS AN AQUATIC. A stated depth range switches
            // the suitability test onto its submerged branch, so this plant is
            // placed by how deep the water is and not by how high the ground is.
            // Below thirty centimetres it is left dry by a normal drawdown; below
            // two and a half metres there is not enough light at the bed for the
            // leaf to reach the surface on stored energy.
            eco.submergedDepthRangeM = new Vector2(0.3f, 2.5f);

            eco.tempOptimumC = 17f;
            eco.tempToleranceC = 12f;

            // The rhizome overwinters in the mud below the ice, so the air
            // temperature above the lake is close to irrelevant to survival. This
            // is why water lilies grow well inside the Arctic Circle.
            eco.tempLethalMinC = -30f;
            eco.tempLethalMaxC = 42f;

            // It does need a real summer, though - enough warmth to push a new set
            // of pads to the surface and flower.
            eco.warmestMonthMinC = 14f;
            eco.frostFreeFractionMin = 0.35f;
            eco.gddMinRequired = 1300f;

            eco.moistureOptimum = 1f;
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 800f;
            eco.aridityMin = 0.1f;
            eco.twiOptimum = 12f;
            eco.twiTolerance = 5f;

            // Stated for honesty rather than effect: the submerged branch of the
            // test never consults it, but a low value here would read as though
            // this plant minded being under water.
            eco.floodTolerance = 1f;

            // The lowest figure anywhere in the table. A water lily out of water is
            // dead the same afternoon.
            eco.droughtTolerance = 0.02f;

            // Near-flat bed. A steep-sided lake has no lily fringe, because the
            // shelf where the depth is right is only centimetres wide.
            eco.slopeOptimumDeg = 1f;
            eco.slopeToleranceDeg = 3f;

            eco.soilDepthMinM = 0.3f;
            eco.soilFertilityOptimum = 0.65f;
            eco.soilFertilityTolerance = 0.3f;

            // Rooted in soft mud. A rocky bed rules it out, which keeps lilies off
            // the stony shore of an upland tarn.
            eco.rockinessMax = 0.1f;

            // It needs open sky over the water. A lily under overhanging trees is a
            // lily that never flowers.
            eco.shadeTolerance = 0.3f;

            // The pads shade the water column beneath them, which is genuinely how
            // a lily bed suppresses the submerged plants under it.
            eco.laiContribution = 2f;

            // The seed floats for a few days before sinking.
            eco.dispersalLambdaM = 80f;

            // Height above the water surface. The plant is essentially flat; only
            // the flower and the newest leaf stand clear.
            eco.matureHeightM = new Vector2(0.05f, 0.25f);

            // Spread of the pad cluster across the surface, which is what the
            // spacing maths needs from something with no stem.
            eco.dbhRangeM = new Vector2(0.4f, 1.5f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 0.8f;

            // Rides the ripple. It is floating, so it should move with the water
            // rather than resist it.
            eco.trunkFlexibility = 0.9f;
            eco.selfThinningK = 60000f;

            // Sheltered water only. A wind-swept lake has no lilies on it, and that
            // is one of the more reliably observable facts about lakes.
            eco.windExposureMax = 0.6f;

            // Generous for the same reason as sphagnum's: the site over a frozen
            // lake reports a full snow load, and the plant is a metre below it,
            // asleep in the mud.
            eco.snowLoadMaxKgPerM2 = 600f;
            eco.browseTolerance = 0.35f;

            // Gone completely over winter and back from the rhizome in spring.
            eco.deciduous = true;
            eco.budburstGdd = 200f;
            eco.senescenceDayLengthH = 12.5f;
            eco.leafOffFraction = 1f;

            // Not zero. Nothing is visible in winter, but a zero scale is a
            // degenerate transform, and shrinking to a fifth says the same thing
            // without handing the renderer a collapsed matrix.
            eco.seasonalHeightScale = new Vector2(0.2f, 1f);

            eco.springColour = new Color(0.36f, 0.52f, 0.30f, 1f);
            eco.summerColour = new Color(0.30f, 0.48f, 0.28f, 1f);
            eco.autumnColour = new Color(0.60f, 0.55f, 0.28f, 1f);
            eco.winterColour = new Color(0.42f, 0.44f, 0.30f, 1f);

            // Always wet, by definition.
            eco.wetDarkening = 0.6f;
        }
    }
}
