// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Tropical.cs
//  Purpose: Wet tropical plants, and the vertical structure they build.
//
//  THE NARROW TOLERANCE IS THE POINT. Every plant here has a thermal tolerance of
//  about five degrees, against twelve for a pine and fourteen for a juniper. A
//  place that never varies breeds things that cannot cope with variation, and the
//  lethal minima below are all well above freezing - a tropical tree dies of
//  chilling at five degrees, long before ice is involved. That single fact is why
//  these species cannot follow the mountain up and why they stop dead at a
//  latitude rather than fading out.
//
//  THE FOUR LAYERS ARE REAL, and they are built out of two numbers each:
//
//    Emergent    open crowns above everything, LAI 3, shade tolerance 0.55
//    Canopy      the closed layer, LAI 7 - the highest in the table, and what
//                makes the forest floor dark
//    Understory  palms living on a couple of percent of full sun, tolerance 0.98
//    Gap         banana, which needs a hole in the canopy and cannot wait
//
//  Beer-Lambert does the rest: the canopy's LAI is what starves the floor, so an
//  understory palm placed under it survives and a banana placed under it does not.
//  Nothing in the placement pass has to know about layers - the light does it.
//
//  A LIMIT WORTH STATING. The envelope can permit salt but not require it. A
//  coconut palm tolerating a salt fraction of 0.8 is not thereby forced to the
//  sea, so it is held to the shore by a distance-to-water limit instead. Where a
//  plant genuinely cannot live away from the ocean, TidalZone is the field that
//  says so, and it is used in the coastal group rather than here.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        // A token load, not zero. These plants cannot take snow at all, but a hard
        // zero would turn a rounding error in the snow model into a total rejection
        // of the tropics, and one centimetre of wet snow is the honest way to say
        // "essentially none".
        private const float TropicalSnowLoadKgPerM2 = 5f;

        private static bool ApplyTropical(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.RainforestEmergent: RainforestEmergent(eco); return true;
                case SpeciesPreset.RainforestCanopy: RainforestCanopy(eco); return true;
                case SpeciesPreset.UnderstoryPalm: UnderstoryPalm(eco); return true;
                case SpeciesPreset.CoconutPalm: CoconutPalm(eco); return true;
                case SpeciesPreset.BananaHerb: BananaHerb(eco); return true;
                default: return false;
            }
        }

        private static void RainforestEmergent(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Rainforest Emergent", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Emergent;

            eco.tempOptimumC = 26f;
            eco.tempToleranceC = 5f;

            // Chilling, not freezing. Four degrees is fatal to this tree with no
            // ice anywhere near it.
            eco.tempLethalMinC = 4f;
            eco.tempLethalMaxC = 45f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.99f;
            eco.gddMinRequired = 5500f;

            eco.moistureOptimum = 0.75f;
            eco.moistureTolerance = 0.18f;
            eco.precipOptimumMm = 2500f;
            eco.precipToleranceMm = 900f;

            // A floor above one: rainfall must exceed evaporative demand. This is
            // the definition of a wet tropical climate rather than a hot one.
            eco.aridityMin = 1.3f;

            eco.twiOptimum = 6.5f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.1f;
            eco.droughtTolerance = 0.15f;

            // Seventy metres of tree needs somewhere to stand.
            eco.soilDepthMinM = 0.8f;

            // Tropical soils are poor - the nutrients are in the living biomass,
            // not the ground - so a high fertility demand would be wrong.
            eco.soilFertilityOptimum = 0.45f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.35f;
            eco.slopeToleranceDeg = 22f;

            // Seedlings wait in shade for a gap; adults stand in full sun. The
            // number is the seedling's, because that is the stage that decides
            // whether the species establishes at all.
            eco.shadeTolerance = 0.55f;

            // Open, scattered crowns. Emergents do not form a closed layer, which
            // is why their LAI is low and the canopy below them is high.
            eco.laiContribution = 3f;

            // A winged seed that autorotates down from seventy metres travels.
            eco.dispersalLambdaM = 90f;

            eco.matureHeightM = new Vector2(45f, 70f);
            eco.dbhRangeM = new Vector2(1f, 2.5f);
            eco.crownAllometryA = 3.2f;
            eco.crownAllometryB = 0.55f;

            // Buttresses: very wide and very shallow, because there is no deep
            // soil to anchor into. Wide enough to hold, shallow enough that a
            // storm can still take one down - which is how canopy gaps form.
            eco.rootPlateRadiusM = 6f;
            eco.trunkFlexibility = 0.15f;
            eco.selfThinningK = 8000f;
            eco.windExposureMax = 0.7f;

            eco.snowLoadMaxKgPerM2 = TropicalSnowLoadKgPerM2;
            eco.snowCanopyAccumulation = 0.6f;
            eco.browseTolerance = 0.5f;

            eco.springColour = new Color(0.20f, 0.40f, 0.20f, 1f);
            eco.summerColour = new Color(0.17f, 0.37f, 0.19f, 1f);
            eco.autumnColour = new Color(0.17f, 0.37f, 0.19f, 1f);
            eco.winterColour = new Color(0.17f, 0.36f, 0.19f, 1f);
        }

        private static void RainforestCanopy(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Rainforest Canopy", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Canopy;

            eco.tempOptimumC = 26f;
            eco.tempToleranceC = 5f;
            eco.tempLethalMinC = 5f;
            eco.tempLethalMaxC = 44f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.99f;
            eco.gddMinRequired = 5000f;

            eco.moistureOptimum = 0.75f;
            eco.moistureTolerance = 0.2f;
            eco.precipOptimumMm = 2200f;
            eco.precipToleranceMm = 900f;
            eco.aridityMin = 1.2f;
            eco.twiOptimum = 6.5f;
            eco.floodTolerance = 0.12f;
            eco.droughtTolerance = 0.18f;

            eco.soilDepthMinM = 0.5f;
            eco.soilFertilityOptimum = 0.45f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.4f;
            eco.slopeToleranceDeg = 25f;

            eco.shadeTolerance = 0.8f;

            // The highest leaf area in the whole table, and the reason a rainforest
            // floor is dark enough to need a plant adapted to two percent of full
            // sun. Every other layer in this file is downstream of this number.
            eco.laiContribution = 7f;

            // Fruit carried by birds, bats and monkeys rather than wind.
            eco.dispersalLambdaM = 50f;

            eco.matureHeightM = new Vector2(25f, 40f);
            eco.dbhRangeM = new Vector2(0.4f, 1f);
            eco.crownAllometryA = 2.6f;
            eco.crownAllometryB = 0.6f;
            eco.rootPlateRadiusM = 3.5f;
            eco.trunkFlexibility = 0.2f;
            eco.selfThinningK = 20000f;

            // Sheltered beneath the emergents, so it has never had to be windfirm.
            eco.windExposureMax = 0.55f;

            eco.snowLoadMaxKgPerM2 = TropicalSnowLoadKgPerM2;
            eco.snowCanopyAccumulation = 0.7f;
            eco.browseTolerance = 0.45f;

            eco.springColour = new Color(0.18f, 0.38f, 0.19f, 1f);
            eco.summerColour = new Color(0.15f, 0.34f, 0.17f, 1f);
            eco.autumnColour = new Color(0.15f, 0.34f, 0.17f, 1f);
            eco.winterColour = new Color(0.15f, 0.33f, 0.17f, 1f);
        }

        private static void UnderstoryPalm(EcoEnvelope eco)
        {
            ResetBiology(eco);

            Stamp(eco, "Understory Palm", LifeForm.Palm);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Understory;

            eco.tempOptimumC = 25f;
            eco.tempToleranceC = 5f;
            eco.tempLethalMinC = 6f;
            eco.tempLethalMaxC = 42f;
            eco.warmestMonthMinC = 21f;
            eco.frostFreeFractionMin = 0.99f;
            eco.gddMinRequired = 4500f;

            eco.moistureOptimum = 0.8f;
            eco.moistureTolerance = 0.15f;
            eco.precipOptimumMm = 2500f;
            eco.precipToleranceMm = 800f;
            eco.aridityMin = 1.3f;
            eco.twiOptimum = 7.5f;
            eco.twiTolerance = 3f;
            eco.floodTolerance = 0.2f;

            // Lower than any other tree or palm here. This plant has never once
            // dried out and has no equipment for it. Only the true wetland species
            // go below it.
            eco.droughtTolerance = 0.08f;

            eco.soilDepthMinM = 0.25f;
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.4f;
            eco.slopeToleranceDeg = 25f;

            // The highest shade tolerance anywhere in the table, higher even than
            // beech. It lives on what gets through an LAI of seven.
            eco.shadeTolerance = 0.98f;
            eco.laiContribution = 2f;
            eco.dispersalLambdaM = 30f;

            eco.matureHeightM = new Vector2(2f, 8f);

            // A palm does not thicken with age, so the stem stays thin however tall
            // it gets.
            eco.dbhRangeM = new Vector2(0.05f, 0.15f);

            // The low exponent says the crown barely grows as the stem does, which
            // is exactly true of a palm: a fixed rosette of fronds on a rising
            // pole.
            eco.crownAllometryA = 1.8f;
            eco.crownAllometryB = 0.35f;
            eco.rootPlateRadiusM = 1.2f;
            eco.trunkFlexibility = 0.5f;
            eco.selfThinningK = 70000f;

            // The lowest wind exposure of any woody plant here. It has spent its
            // whole evolutionary history in still air under a closed canopy.
            eco.windExposureMax = 0.4f;

            eco.snowLoadMaxKgPerM2 = TropicalSnowLoadKgPerM2;
            eco.browseTolerance = 0.4f;

            eco.springColour = new Color(0.22f, 0.44f, 0.22f, 1f);
            eco.summerColour = new Color(0.19f, 0.40f, 0.21f, 1f);
            eco.autumnColour = new Color(0.19f, 0.40f, 0.21f, 1f);
            eco.winterColour = new Color(0.19f, 0.39f, 0.21f, 1f);
        }

        private static void CoconutPalm(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Coconut Palm", LifeForm.Palm);

            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.Canopy;

            eco.tempOptimumC = 27f;
            eco.tempToleranceC = 5f;
            eco.tempLethalMinC = 7f;
            eco.tempLethalMaxC = 46f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.99f;
            eco.gddMinRequired = 5500f;

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 1800f;
            eco.precipToleranceMm = 900f;

            // Lower than the forest species because a coconut palm does not drink
            // the rain. It drinks the freshwater lens under the sand, which is
            // what the water table depth below is for, and that is how a palm
            // stands on a dry coral island.
            eco.aridityMin = 0.7f;
            eco.waterTableMaxDepthM = 3f;

            eco.twiOptimum = 5f;
            eco.twiTolerance = 4f;
            eco.floodTolerance = 0.4f;
            eco.droughtTolerance = 0.55f;

            // Held to the shore by distance rather than by a tidal zone: coconut
            // grows well back from the beach, it just never grows far inland.
            eco.distanceToWaterMaxM = 300f;
            eco.saltTolerance = 0.8f;

            eco.slopeOptimumDeg = 3f;
            eco.slopeToleranceDeg = 15f;
            eco.soilDepthMinM = 0.3f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.5f;

            eco.shadeTolerance = 0.08f;
            eco.laiContribution = 2.2f;

            // The longest dispersal distance in the table by a factor of two, and
            // it is not an exaggeration: the nut floats, stays viable for months,
            // and crosses oceans. This is why isolated islands have coconut palms
            // and nothing else tall.
            eco.dispersalLambdaM = 400f;

            eco.matureHeightM = new Vector2(12f, 25f);
            eco.dbhRangeM = new Vector2(0.25f, 0.4f);
            eco.crownAllometryA = 2.2f;
            eco.crownAllometryB = 0.3f;

            // A fibrous root mat with no taproot. It leans instead of anchoring,
            // and survives by bending nearly double rather than by standing firm -
            // which is why it holds the single most exposed position any tree
            // occupies.
            eco.rootPlateRadiusM = 2f;
            eco.trunkFlexibility = 0.75f;
            eco.windExposureMax = 1f;
            eco.selfThinningK = 12000f;

            eco.snowLoadMaxKgPerM2 = TropicalSnowLoadKgPerM2;
            eco.browseTolerance = 0.6f;
            eco.avalancheTolerance = 0.2f;

            eco.springColour = new Color(0.32f, 0.48f, 0.24f, 1f);
            eco.summerColour = new Color(0.28f, 0.44f, 0.22f, 1f);
            eco.autumnColour = new Color(0.28f, 0.44f, 0.22f, 1f);
            eco.winterColour = new Color(0.27f, 0.43f, 0.22f, 1f);
        }

        private static void BananaHerb(EcoEnvelope eco)
        {
            ResetBiology(eco);

            // Genuinely a herb: the largest herbaceous plant there is. What looks
            // like a trunk is a column of rolled leaf bases with no wood in it at
            // all, and every number below follows from that.
            Stamp(eco, "Banana Herb", LifeForm.Herb);

            eco.successionStage = SuccessionStage.Early;
            eco.canopyLayer = CanopyLayer.Understory;

            // Burned or cut to the ground and back within months from the corm.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 27f;
            eco.tempToleranceC = 5f;
            eco.tempLethalMinC = 8f;
            eco.tempLethalMaxC = 42f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.99f;
            eco.gddMinRequired = 4500f;

            eco.moistureOptimum = 0.8f;
            eco.moistureTolerance = 0.16f;
            eco.precipOptimumMm = 2200f;
            eco.precipToleranceMm = 800f;
            eco.aridityMin = 1.1f;
            eco.twiOptimum = 8f;
            eco.twiTolerance = 3f;
            eco.waterTableMaxDepthM = 2f;
            eco.floodTolerance = 0.3f;
            eco.droughtTolerance = 0.1f;

            eco.slopeOptimumDeg = 6f;
            eco.slopeToleranceDeg = 18f;
            eco.soilDepthMinM = 0.4f;

            // The heaviest feeder in the table. Banana on poor ground is a small
            // sick banana, and this is the number that says so.
            eco.soilFertilityOptimum = 0.75f;
            eco.soilFertilityTolerance = 0.25f;
            eco.rockinessMax = 0.25f;

            // Needs a gap. Not a deep-shade plant, which is what separates it from
            // the understory palm growing a few metres away.
            eco.shadeTolerance = 0.35f;
            eco.laiContribution = 3.2f;

            // Spreads by suckers off the parent corm and barely at all by seed, so
            // it forms discrete clumps rather than a spread. The short distance is
            // what produces that clumping.
            eco.dispersalLambdaM = 12f;

            eco.matureHeightM = new Vector2(2f, 7f);
            eco.dbhRangeM = new Vector2(0.15f, 0.35f);
            eco.crownAllometryA = 1.6f;
            eco.crownAllometryB = 0.3f;
            eco.rootPlateRadiusM = 1f;
            eco.selfThinningK = 120000f;

            // No wood, so nothing holds it up. The most flexible and the least
            // wind-tolerant plant in the table - a banana in an open gale is
            // shredded leaves on a bent stem, and that is why it lives in
            // sheltered hollows.
            eco.trunkFlexibility = 0.85f;
            eco.windExposureMax = 0.3f;

            eco.snowLoadMaxKgPerM2 = TropicalSnowLoadKgPerM2;

            // Highly palatable, and there is no thorn, resin or toxin anywhere in
            // it.
            eco.browseTolerance = 0.25f;

            eco.springColour = new Color(0.42f, 0.60f, 0.24f, 1f);
            eco.summerColour = new Color(0.36f, 0.55f, 0.22f, 1f);
            eco.autumnColour = new Color(0.36f, 0.54f, 0.22f, 1f);
            eco.winterColour = new Color(0.35f, 0.53f, 0.22f, 1f);
            eco.wetDarkening = 0.45f;
        }
    }
}
