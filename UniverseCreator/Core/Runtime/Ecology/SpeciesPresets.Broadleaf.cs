// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Broadleaf.cs
//  Purpose: Temperate broadleaf trees. All seven drop their leaves; everything
//           else about them differs, and the differences are the reason a mixed
//           wood does not look like one tree repeated.
//
//  The two axes that decide which of these wins a given spot:
//
//  LIGHT. Beech at one end (0.95 shade tolerance, and a canopy dense enough to
//  make sure of it), birch at the other (0.10, and dead within a decade of being
//  overtopped). This is why an abandoned field goes birch first and beech last,
//  and why it never happens in the other order.
//
//  WATER. Alder and willow are not simply "wetter oaks" - they carry a water
//  table requirement, so they form the ribbon along a river instead of a patch in
//  a wood. Willow is the most flood-tolerant tree there is; beech dies in ground
//  that is merely damp all winter.
//
//  Leaf timing is set per species rather than shared, because it is visible. Ash
//  is the last into leaf and the first out of it; willow is in leaf while oak is
//  still bare. Giving them all one budburst threshold would throw away a spring
//  that an author can actually watch happen.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyBroadleaf(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.Oak: Oak(eco); return true;
                case SpeciesPreset.Beech: Beech(eco); return true;
                case SpeciesPreset.Birch: Birch(eco); return true;
                case SpeciesPreset.Maple: Maple(eco); return true;
                case SpeciesPreset.Ash: Ash(eco); return true;
                case SpeciesPreset.Alder: Alder(eco); return true;
                case SpeciesPreset.Willow: Willow(eco); return true;
                default: return false;
            }
        }

        private static void Oak(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Oak", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Mid;

            eco.tempOptimumC = 11f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -35f;
            eco.tempLethalMaxC = 42f;
            eco.warmestMonthMinC = 13f;
            eco.frostFreeFractionMin = 0.4f;
            eco.gddMinRequired = 1400f;

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 500f;
            eco.aridityMin = 0.5f;
            eco.twiOptimum = 6f;
            eco.floodTolerance = 0.2f;
            eco.droughtTolerance = 0.55f;

            // A taproot needs somewhere to go, which is why oak is absent from
            // thin soil over rock however good the climate is.
            eco.soilDepthMinM = 0.5f;
            eco.soilFertilityOptimum = 0.6f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.5f;
            eco.slopeToleranceDeg = 25f;

            eco.shadeTolerance = 0.4f;
            eco.laiContribution = 4.5f;

            // An acorn falls where it grew. The distance in the number is the jays
            // and squirrels that carry and forget them, not the wind.
            eco.dispersalLambdaM = 30f;

            eco.matureHeightM = new Vector2(20f, 35f);
            eco.dbhRangeM = new Vector2(0.6f, 1.8f);

            // Broad and spreading, so a hedgerow oak in the open is nearly as wide
            // as it is tall.
            eco.crownAllometryA = 2.6f;
            eco.crownAllometryB = 0.6f;
            eco.selfThinningK = 18000f;

            // Deep and wide: the tree that is still standing after the gale.
            eco.rootPlateRadiusM = 4f;
            eco.trunkFlexibility = 0.2f;
            eco.windExposureMax = 0.8f;

            eco.snowCanopyAccumulation = 0.15f;

            // Acorns and oak seedlings are eaten by everything, which is why oak
            // regeneration fails where deer are dense.
            eco.browseTolerance = 0.3f;

            eco.deciduous = true;
            eco.budburstGdd = 250f;
            eco.senescenceDayLengthH = 11f;
            eco.springColour = new Color(0.52f, 0.63f, 0.28f, 1f);
            eco.summerColour = new Color(0.24f, 0.40f, 0.18f, 1f);
            eco.autumnColour = new Color(0.55f, 0.35f, 0.14f, 1f);
            eco.winterColour = new Color(0.34f, 0.28f, 0.22f, 1f);
            eco.leafOffFraction = 0.85f;
            eco.seasonalHeightScale = new Vector2(0.9f, 1f);
        }

        private static void Beech(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Beech", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;

            // A narrower thermal tolerance than oak, deliberately. Beech is fussy
            // about climate and it shows in where it stops.
            eco.tempOptimumC = 10f;
            eco.tempToleranceC = 7f;
            eco.tempLethalMinC = -30f;
            eco.tempLethalMaxC = 38f;
            eco.warmestMonthMinC = 13f;
            eco.frostFreeFractionMin = 0.45f;
            eco.gddMinRequired = 1300f;

            eco.moistureOptimum = 0.6f;
            eco.moistureTolerance = 0.2f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 350f;
            eco.aridityMin = 0.75f;
            eco.twiOptimum = 6f;
            eco.twiTolerance = 3f;

            // Beech dies in ground that stays wet, and dies in a drought. Both
            // numbers are low and both matter.
            eco.floodTolerance = 0.05f;
            eco.droughtTolerance = 0.2f;

            eco.soilDepthMinM = 0.4f;
            eco.soilFertilityOptimum = 0.6f;
            eco.soilFertilityTolerance = 0.3f;
            eco.rockinessMax = 0.6f;

            // The highest shade tolerance of any tree here, paired with the
            // densest canopy. That combination is the whole story of beech: it can
            // wait in its own shade, and nothing else can.
            eco.shadeTolerance = 0.95f;
            eco.laiContribution = 6f;
            eco.dispersalLambdaM = 20f;

            eco.matureHeightM = new Vector2(25f, 40f);
            eco.dbhRangeM = new Vector2(0.5f, 1.4f);
            eco.crownAllometryA = 2.4f;
            eco.crownAllometryB = 0.6f;
            eco.selfThinningK = 20000f;
            eco.rootPlateRadiusM = 3f;
            eco.trunkFlexibility = 0.22f;
            eco.windExposureMax = 0.6f;

            eco.snowCanopyAccumulation = 0.15f;
            eco.browseTolerance = 0.35f;

            eco.deciduous = true;
            eco.budburstGdd = 200f;
            eco.senescenceDayLengthH = 11.3f;
            eco.springColour = new Color(0.58f, 0.72f, 0.32f, 1f);
            eco.summerColour = new Color(0.22f, 0.38f, 0.17f, 1f);
            eco.autumnColour = new Color(0.72f, 0.40f, 0.16f, 1f);

            // Young beech holds its dead leaves all winter instead of dropping
            // them, so a beech thicket stays coppery when everything else is bare.
            eco.winterColour = new Color(0.52f, 0.36f, 0.22f, 1f);
            eco.leafOffFraction = 0.7f;
        }

        private static void Birch(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Birch", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Pioneer;

            eco.tempOptimumC = 8f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -45f;
            eco.tempLethalMaxC = 36f;

            // Birch reaches further north than any other broadleaf here, and forms
            // the treeline itself where the conifers have run out.
            eco.warmestMonthMinC = 8f;
            eco.frostFreeFractionMin = 0.15f;
            eco.gddMinRequired = 600f;

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 700f;
            eco.precipToleranceMm = 500f;
            eco.aridityMin = 0.4f;
            eco.twiTolerance = 4.5f;
            eco.floodTolerance = 0.3f;
            eco.droughtTolerance = 0.4f;

            eco.soilDepthMinM = 0.1f;
            eco.soilFertilityOptimum = 0.35f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.7f;

            eco.shadeTolerance = 0.1f;
            eco.laiContribution = 3f;

            // The longest seed throw of any tree in this file, and the reason a
            // clear-fell fills with birch before anything else arrives. Seed the
            // size of a speck of dust, with wings.
            eco.dispersalLambdaM = 120f;

            eco.matureHeightM = new Vector2(12f, 25f);
            eco.dbhRangeM = new Vector2(0.2f, 0.6f);
            eco.crownAllometryA = 1.8f;
            eco.crownAllometryB = 0.55f;
            eco.rootPlateRadiusM = 2f;
            eco.selfThinningK = 40000f;

            // Whippy. A birch bends right over under snow or in a gale and stands
            // back up, which is why it survives exposure that snaps a spruce.
            eco.trunkFlexibility = 0.55f;
            eco.windExposureMax = 0.85f;
            eco.snowLoadMaxKgPerM2 = 180f;
            eco.snowCanopyAccumulation = 0.1f;
            eco.browseTolerance = 0.4f;
            eco.krummholzAboveExposure = true;

            eco.deciduous = true;
            eco.budburstGdd = 120f;
            eco.senescenceDayLengthH = 12f;
            eco.springColour = new Color(0.60f, 0.76f, 0.32f, 1f);
            eco.summerColour = new Color(0.32f, 0.50f, 0.20f, 1f);
            eco.autumnColour = new Color(0.88f, 0.75f, 0.25f, 1f);

            // White bark, not brown twigs: a bare birch reads pale in winter.
            eco.winterColour = new Color(0.72f, 0.70f, 0.65f, 1f);
            eco.leafOffFraction = 0.92f;
            eco.seasonalHeightScale = new Vector2(0.9f, 1f);
        }

        private static void Maple(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Maple", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;

            eco.tempOptimumC = 10f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -38f;
            eco.tempLethalMaxC = 38f;
            eco.warmestMonthMinC = 12f;
            eco.frostFreeFractionMin = 0.4f;
            eco.gddMinRequired = 1200f;

            eco.moistureOptimum = 0.6f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 950f;
            eco.precipToleranceMm = 400f;
            eco.aridityMin = 0.8f;
            eco.twiOptimum = 6.5f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.1f;
            eco.droughtTolerance = 0.25f;

            // Into the coves and hollows where the soil is deep and the air stays
            // moist. Maple on an exposed ridge is a maple that is struggling.
            eco.curvaturePreference = -0.2f;
            eco.soilDepthMinM = 0.4f;
            eco.soilFertilityOptimum = 0.65f;
            eco.soilFertilityTolerance = 0.3f;
            eco.rockinessMax = 0.5f;

            eco.shadeTolerance = 0.85f;
            eco.laiContribution = 5.5f;

            // A samara spins as it falls, so it travels further than an acorn and
            // nowhere near as far as a birch seed.
            eco.dispersalLambdaM = 60f;

            eco.matureHeightM = new Vector2(20f, 35f);
            eco.dbhRangeM = new Vector2(0.4f, 1.0f);
            eco.crownAllometryA = 2.3f;
            eco.crownAllometryB = 0.6f;
            eco.selfThinningK = 22000f;
            eco.rootPlateRadiusM = 2.8f;
            eco.trunkFlexibility = 0.28f;
            eco.windExposureMax = 0.65f;
            eco.snowCanopyAccumulation = 0.15f;
            eco.browseTolerance = 0.35f;

            eco.deciduous = true;
            eco.budburstGdd = 180f;
            eco.senescenceDayLengthH = 11.6f;
            eco.springColour = new Color(0.56f, 0.70f, 0.30f, 1f);
            eco.summerColour = new Color(0.23f, 0.42f, 0.18f, 1f);

            // The scarlet that a temperate autumn is famous for. No other tree in
            // this file goes red.
            eco.autumnColour = new Color(0.85f, 0.30f, 0.12f, 1f);
            eco.winterColour = new Color(0.36f, 0.30f, 0.24f, 1f);
            eco.leafOffFraction = 0.88f;
        }

        private static void Ash(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Ash", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Mid;

            eco.tempOptimumC = 10f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -32f;
            eco.tempLethalMaxC = 38f;
            eco.warmestMonthMinC = 13f;
            eco.frostFreeFractionMin = 0.42f;
            eco.gddMinRequired = 1300f;

            eco.moistureOptimum = 0.6f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 850f;
            eco.precipToleranceMm = 400f;
            eco.aridityMin = 0.7f;

            // Ash marks the damp flushes and the base of slopes, a step wetter
            // than oak without being riparian.
            eco.twiOptimum = 7f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.25f;
            eco.droughtTolerance = 0.3f;

            eco.soilDepthMinM = 0.35f;

            // The fussiest of the seven about fertility. Ash growing well is a
            // statement about the soil.
            eco.soilFertilityOptimum = 0.7f;
            eco.soilFertilityTolerance = 0.28f;
            eco.rockinessMax = 0.5f;

            eco.shadeTolerance = 0.35f;

            // An open airy crown, which is why grass and bluebells grow under ash
            // and nothing grows under beech.
            eco.laiContribution = 3.8f;
            eco.dispersalLambdaM = 55f;

            eco.matureHeightM = new Vector2(18f, 32f);
            eco.dbhRangeM = new Vector2(0.35f, 0.9f);
            eco.crownAllometryA = 2f;
            eco.crownAllometryB = 0.58f;
            eco.selfThinningK = 24000f;
            eco.rootPlateRadiusM = 2.6f;
            eco.trunkFlexibility = 0.3f;
            eco.windExposureMax = 0.7f;
            eco.snowCanopyAccumulation = 0.12f;
            eco.browseTolerance = 0.3f;

            eco.deciduous = true;

            // Last into leaf and first out of it: ash carries leaves for a shorter
            // season than anything else here, so it is still bare when the wood
            // around it is green.
            eco.budburstGdd = 320f;
            eco.senescenceDayLengthH = 12.2f;
            eco.springColour = new Color(0.50f, 0.64f, 0.30f, 1f);
            eco.summerColour = new Color(0.28f, 0.45f, 0.20f, 1f);

            // Ash barely colours before it drops - a washed-out yellow-green, not
            // an autumn display.
            eco.autumnColour = new Color(0.72f, 0.72f, 0.35f, 1f);
            eco.winterColour = new Color(0.42f, 0.40f, 0.34f, 1f);
            eco.leafOffFraction = 0.9f;
        }

        private static void Alder(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Alder", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Pioneer;

            eco.tempOptimumC = 10f;
            eco.tempToleranceC = 10f;
            eco.tempLethalMinC = -35f;
            eco.tempLethalMaxC = 38f;
            eco.warmestMonthMinC = 11f;
            eco.frostFreeFractionMin = 0.3f;
            eco.gddMinRequired = 900f;

            eco.moistureOptimum = 0.85f;
            eco.moistureTolerance = 0.18f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 500f;
            eco.aridityMin = 0.6f;

            // The three numbers that make alder a ribbon along the water rather
            // than a patch in a wood: valley-bottom drainage, a water table within
            // reach of the roots, and a hard limit on how far from water it goes.
            eco.twiOptimum = 10f;
            eco.twiTolerance = 3f;
            eco.waterTableMaxDepthM = 0.6f;
            eco.distanceToWaterMaxM = 60f;

            // Survives standing water for weeks. This is the trait that lets it
            // hold ground no other tree here can use.
            eco.floodTolerance = 0.9f;
            eco.droughtTolerance = 0.1f;

            eco.curvaturePreference = -0.5f;
            eco.slopeOptimumDeg = 3f;
            eco.slopeToleranceDeg = 10f;
            eco.soilDepthMinM = 0.3f;

            // Alder fixes its own nitrogen, so it is indifferent to fertility in a
            // way none of the others are. The wide tolerance is that indifference.
            eco.soilFertilityOptimum = 0.45f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.4f;

            eco.shadeTolerance = 0.25f;
            eco.laiContribution = 4f;

            // Alder seed floats. Downstream is a long way, and that is why a river
            // corridor recruits alder along its whole length at once.
            eco.dispersalLambdaM = 150f;

            eco.matureHeightM = new Vector2(12f, 25f);
            eco.dbhRangeM = new Vector2(0.25f, 0.7f);
            eco.crownAllometryA = 1.8f;
            eco.crownAllometryB = 0.55f;
            eco.selfThinningK = 32000f;
            eco.rootPlateRadiusM = 2.2f;
            eco.trunkFlexibility = 0.35f;
            eco.windExposureMax = 0.7f;
            eco.snowCanopyAccumulation = 0.12f;
            eco.browseTolerance = 0.45f;

            eco.deciduous = true;
            eco.budburstGdd = 150f;
            eco.senescenceDayLengthH = 11.8f;
            eco.springColour = new Color(0.44f, 0.58f, 0.26f, 1f);
            eco.summerColour = new Color(0.22f, 0.36f, 0.18f, 1f);

            // Alder goes brown and drops. It does not do gold.
            eco.autumnColour = new Color(0.45f, 0.38f, 0.20f, 1f);
            eco.winterColour = new Color(0.32f, 0.28f, 0.24f, 1f);
            eco.leafOffFraction = 0.9f;
        }

        private static void Willow(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Willow", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Pioneer;

            // Genuinely a resprouter, and one of the few trees for which that is
            // the literal truth: a willow branch snapped off by a flood and driven
            // into a gravel bar grows into a willow.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 11f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -40f;
            eco.tempLethalMaxC = 40f;
            eco.warmestMonthMinC = 10f;
            eco.frostFreeFractionMin = 0.25f;
            eco.gddMinRequired = 800f;

            eco.moistureOptimum = 0.9f;
            eco.moistureTolerance = 0.15f;
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 550f;

            // Lower than alder's on purpose. A willow beside a desert wadi is
            // drinking the river, not the rain, so rainfall is the wrong thing to
            // exclude it on.
            eco.aridityMin = 0.4f;

            eco.twiOptimum = 11f;
            eco.twiTolerance = 3f;
            eco.waterTableMaxDepthM = 1f;
            eco.distanceToWaterMaxM = 40f;

            // The most flood-tolerant tree in the table. Nothing else takes total
            // submergence of the root zone as a normal year.
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.15f;
            eco.saltTolerance = 0.1f;

            eco.curvaturePreference = -0.5f;
            eco.slopeOptimumDeg = 2f;
            eco.slopeToleranceDeg = 8f;
            eco.soilDepthMinM = 0.2f;
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.4f;

            // Willow colonises bare river gravel, so stones are not an obstacle.
            eco.rockinessMax = 0.5f;

            eco.shadeTolerance = 0.1f;
            eco.laiContribution = 3.5f;

            // Cottony seed on both wind and water, viable for only days. Travels
            // furthest of anything here and has to land on wet bare ground.
            eco.dispersalLambdaM = 200f;

            eco.matureHeightM = new Vector2(12f, 25f);
            eco.dbhRangeM = new Vector2(0.3f, 1.2f);
            eco.crownAllometryA = 2.4f;
            eco.crownAllometryB = 0.6f;
            eco.selfThinningK = 35000f;
            eco.rootPlateRadiusM = 2.4f;

            // The flexible tree. It survives being laid flat by a flood by giving
            // instead of resisting, then standing back up.
            eco.trunkFlexibility = 0.7f;
            eco.windExposureMax = 0.75f;
            eco.snowCanopyAccumulation = 0.1f;
            eco.browseTolerance = 0.55f;
            eco.avalancheTolerance = 0.5f;

            eco.deciduous = true;
            eco.budburstGdd = 100f;
            eco.senescenceDayLengthH = 11.5f;
            eco.springColour = new Color(0.62f, 0.74f, 0.36f, 1f);
            eco.summerColour = new Color(0.35f, 0.48f, 0.24f, 1f);
            eco.autumnColour = new Color(0.78f, 0.72f, 0.32f, 1f);
            eco.winterColour = new Color(0.44f, 0.36f, 0.26f, 1f);
            eco.leafOffFraction = 0.9f;
        }
    }
}
