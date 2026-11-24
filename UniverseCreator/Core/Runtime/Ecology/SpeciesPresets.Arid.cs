// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Arid.cs
//  Purpose: Desert and savanna plants. Several of the extremes in the whole table
//           live in this file, and they are extremes for one reason.
//
//  AN ARIDITY CEILING, NOT JUST A FLOOR. Every plant here states aridityMax, and
//  it is low. A saguaro in a humid climate does not thrive - it rots. These species
//  are excluded from wet ground as firmly as a beech is excluded from dry ground,
//  and without the ceiling a cactus would happily appear in a rainforest that
//  happened to be warm enough.
//
//  SPACING IS THE OTHER STORY. A desert looks the way it does because the plants
//  are evenly spread rather than clustered, and there are two separate mechanisms
//  for that here:
//    creosote  poisons the ground around itself. That is what allelopathyRadiusM
//              is for, and creosote is the reason the field exists.
//    saguaro   is spaced by having almost no plants per hectare at all - the
//              lowest self-thinning figure in the table.
//
//  Leaf area is the number that will look wrong and is not. A cactus has no leaves;
//  an acacia's umbrella crown is thin enough to read a book under. Values of 0.3 to
//  1.8 here against 7 for a rainforest canopy are why grass grows right up to the
//  trunk in a savanna and stops dead at the edge of a wood.
//
//  ONE THING THIS MODEL CANNOT SAY. Savanna trees are drought-deciduous - they
//  flush with the rains, not with warmth. The phenology here runs on growing degree
//  days and day length, which at savanna latitudes barely move, so acacia is left
//  evergreen rather than given a leaf-off season driven by the wrong signal.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyArid(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.SaguaroColumnarCactus: SaguaroColumnarCactus(eco); return true;
                case SpeciesPreset.BarrelCactus: BarrelCactus(eco); return true;
                case SpeciesPreset.CreosoteShrub: CreosoteShrub(eco); return true;
                case SpeciesPreset.Acacia: Acacia(eco); return true;
                case SpeciesPreset.SpinifexTussock: SpinifexTussock(eco); return true;
                default: return false;
            }
        }

        private static void SaguaroColumnarCactus(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Saguaro Columnar Cactus", LifeForm.Succulent);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Canopy;

            // Burns and does not come back. A cactus has no bark, no burl and no
            // seed bank worth the name, which is why fire in a saguaro desert is a
            // permanent loss rather than a reset.
            eco.fireResponse = FireResponse.Killed;

            eco.tempOptimumC = 24f;
            eco.tempToleranceC = 10f;

            // A brief light frost is survivable; a long freeze is not. The real
            // limit is hours below zero rather than the depth of the cold, and this
            // is the closest a single temperature can come to saying that.
            eco.tempLethalMinC = -5f;
            eco.tempLethalMaxC = 50f;
            eco.warmestMonthMinC = 24f;
            eco.frostFreeFractionMin = 0.85f;
            eco.gddMinRequired = 3500f;

            eco.moistureOptimum = 0.1f;
            eco.moistureTolerance = 0.12f;
            eco.precipOptimumMm = 250f;
            eco.precipToleranceMm = 200f;
            eco.aridityMin = 0.03f;
            eco.aridityMax = 0.5f;
            eco.twiOptimum = 4f;
            eco.twiTolerance = 3f;
            eco.floodTolerance = 0.02f;
            eco.droughtTolerance = 0.98f;

            // Convex, well-drained ground: the rocky bajada rather than the flat.
            eco.curvaturePreference = 0.2f;
            eco.slopeOptimumDeg = 10f;
            eco.slopeToleranceDeg = 25f;
            eco.soilDepthMinM = 0.2f;
            eco.soilFertilityOptimum = 0.25f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.8f;

            // Unexpectedly high for a desert plant, and it is the single most
            // important fact about saguaro recruitment: a seedling cannot survive
            // in the open. It establishes in the shade of a nurse shrub and only
            // later outgrows it.
            eco.shadeTolerance = 0.45f;

            // No leaves at all. A saguaro shades almost nothing, so it never closes
            // a canopy however many of them there are.
            eco.laiContribution = 0.6f;

            // Birds and bats take the fruit off the top of a fifteen-metre column
            // and carry it.
            eco.dispersalLambdaM = 100f;

            eco.matureHeightM = new Vector2(6f, 15f);
            eco.dbhRangeM = new Vector2(0.3f, 0.65f);

            // The narrowest crown in the table. A column's crown radius barely
            // exceeds its own stem.
            eco.crownAllometryA = 0.9f;
            eco.crownAllometryB = 0.35f;

            // Ten centimetres deep and five metres across - a plate of roots just
            // under the surface, built to take a whole year's rain out of one
            // storm before it evaporates.
            eco.rootPlateRadiusM = 5f;

            // Rigid. A saguaro does not bend, and a column full of water is heavy
            // enough that wind pushes it over instead.
            eco.trunkFlexibility = 0.05f;
            eco.windExposureMax = 0.75f;

            // The fewest individuals per hectare of anything here, which is most of
            // why a saguaro stand reads as spaced-out rather than as forest.
            eco.selfThinningK = 3000f;

            eco.snowLoadMaxKgPerM2 = 20f;
            eco.browseTolerance = 0.9f;

            eco.springColour = new Color(0.37f, 0.50f, 0.34f, 1f);
            eco.summerColour = new Color(0.35f, 0.48f, 0.34f, 1f);
            eco.autumnColour = new Color(0.35f, 0.47f, 0.33f, 1f);
            eco.winterColour = new Color(0.34f, 0.46f, 0.33f, 1f);
            eco.wetDarkening = 0.2f;
        }

        private static void BarrelCactus(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Barrel Cactus", LifeForm.Succulent);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            eco.tempOptimumC = 25f;
            eco.tempToleranceC = 11f;
            eco.tempLethalMinC = -7f;
            eco.tempLethalMaxC = 52f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.8f;
            eco.gddMinRequired = 3000f;

            eco.moistureOptimum = 0.08f;
            eco.moistureTolerance = 0.1f;
            eco.precipOptimumMm = 180f;
            eco.precipToleranceMm = 160f;
            eco.aridityMin = 0.02f;
            eco.aridityMax = 0.45f;
            eco.twiOptimum = 3.5f;
            eco.twiTolerance = 3f;
            eco.floodTolerance = 0.02f;

            // The highest of any vascular plant here, and it is achieved by
            // holding water rather than by doing without it. Only the rock lichen
            // goes above this, by the opposite trick: it keeps no water at all and
            // simply stops until the next rain.
            eco.droughtTolerance = 0.99f;

            eco.curvaturePreference = 0.3f;
            eco.slopeOptimumDeg = 15f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.1f;
            eco.soilFertilityOptimum = 0.2f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.9f;

            // Unlike the saguaro it needs no nurse plant, so it establishes in the
            // open where the saguaro cannot.
            eco.shadeTolerance = 0.12f;
            eco.laiContribution = 0.3f;
            eco.dispersalLambdaM = 25f;

            // As wide as it is tall, which is what "barrel" means and what the
            // near-equal height and diameter ranges are saying.
            eco.matureHeightM = new Vector2(0.3f, 1.2f);
            eco.dbhRangeM = new Vector2(0.2f, 0.6f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 1.2f;
            eco.trunkFlexibility = 0.05f;
            eco.selfThinningK = 25000f;
            eco.windExposureMax = 0.95f;
            eco.snowLoadMaxKgPerM2 = 25f;
            eco.browseTolerance = 0.9f;

            eco.springColour = new Color(0.36f, 0.50f, 0.30f, 1f);
            eco.summerColour = new Color(0.34f, 0.47f, 0.29f, 1f);
            eco.autumnColour = new Color(0.34f, 0.47f, 0.29f, 1f);
            eco.winterColour = new Color(0.33f, 0.45f, 0.29f, 1f);
            eco.wetDarkening = 0.2f;
        }

        private static void CreosoteShrub(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Creosote Shrub", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Canopy;
            eco.fireResponse = FireResponse.Killed;

            // THE REASON allelopathyRadiusM EXISTS. Creosote poisons the soil
            // around its own roots, so nothing - including another creosote - can
            // establish inside this radius. The famous even spacing of a creosote
            // flat is not a statistical accident and must not be produced by one;
            // it is chemistry, and this is where it is stated.
            eco.allelopathyRadiusM = 2.5f;

            eco.tempOptimumC = 24f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -12f;
            eco.tempLethalMaxC = 52f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.7f;
            eco.gddMinRequired = 2800f;

            eco.moistureOptimum = 0.1f;
            eco.moistureTolerance = 0.12f;
            eco.precipOptimumMm = 200f;
            eco.precipToleranceMm = 180f;
            eco.aridityMin = 0.02f;
            eco.aridityMax = 0.55f;
            eco.twiOptimum = 4f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.03f;
            eco.droughtTolerance = 0.97f;

            // Holds the alkaline flats and caliche pans that nothing else uses.
            eco.saltTolerance = 0.3f;

            eco.slopeToleranceDeg = 25f;
            eco.soilDepthMinM = 0.15f;
            eco.soilFertilityOptimum = 0.2f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.85f;

            eco.shadeTolerance = 0.1f;

            // Small resinous leaves. Enough shade to nurse a cactus seedling,
            // nowhere near enough to close a canopy.
            eco.laiContribution = 1.2f;

            // Short, which together with the allelopathy is why creosote spreads
            // as slowly expanding clonal rings rather than as a wave.
            eco.dispersalLambdaM = 15f;

            eco.matureHeightM = new Vector2(0.6f, 3f);
            eco.dbhRangeM = new Vector2(0.02f, 0.08f);
            eco.crownAllometryA = 2.2f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 2f;
            eco.trunkFlexibility = 0.4f;
            eco.selfThinningK = 30000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 40f;

            // Resinous and foul enough that almost nothing will eat it.
            eco.browseTolerance = 0.85f;

            eco.springColour = new Color(0.34f, 0.42f, 0.26f, 1f);
            eco.summerColour = new Color(0.30f, 0.38f, 0.24f, 1f);
            eco.autumnColour = new Color(0.30f, 0.37f, 0.24f, 1f);
            eco.winterColour = new Color(0.29f, 0.36f, 0.24f, 1f);
        }

        private static void Acacia(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Acacia", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.Canopy;

            // Burns to the ground and comes back from the base. The savanna's
            // grass-fire-tree cycle depends on this, and an acacia that could not
            // resprout would have been eliminated from savanna long ago.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 25f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -4f;
            eco.tempLethalMaxC = 50f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.75f;
            eco.gddMinRequired = 3200f;

            eco.moistureOptimum = 0.25f;
            eco.moistureTolerance = 0.2f;
            eco.precipOptimumMm = 550f;
            eco.precipToleranceMm = 350f;
            eco.aridityMin = 0.15f;
            eco.aridityMax = 1f;
            eco.twiOptimum = 5f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.1f;
            eco.droughtTolerance = 0.88f;

            eco.slopeOptimumDeg = 6f;
            eco.slopeToleranceDeg = 22f;
            eco.soilDepthMinM = 0.4f;

            // Fixes its own nitrogen, so soil fertility is close to irrelevant to
            // it - and an acacia grove leaves the ground richer than it found it.
            eco.soilFertilityOptimum = 0.35f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.6f;

            eco.shadeTolerance = 0.15f;

            // Thin enough to read under. This is why savanna grass runs right up to
            // the trunk instead of stopping at the drip line, and it is the single
            // number that separates a savanna from a woodland.
            eco.laiContribution = 1.8f;

            // Pods eaten whole by antelope and elephant and dropped kilometres
            // away. The distance is an animal's day, not a seed's glide.
            eco.dispersalLambdaM = 70f;

            eco.matureHeightM = new Vector2(4f, 15f);
            eco.dbhRangeM = new Vector2(0.2f, 0.9f);

            // The widest crown in the table relative to its stem. An umbrella thorn
            // is broader than it is tall, and that silhouette is what makes a
            // savanna recognisable at a glance.
            eco.crownAllometryA = 3.4f;
            eco.crownAllometryB = 0.55f;
            eco.rootPlateRadiusM = 3.5f;
            eco.trunkFlexibility = 0.25f;

            // Very few stems per hectare. Savanna is open by definition, and this
            // is what keeps it open.
            eco.selfThinningK = 9000f;
            eco.windExposureMax = 0.9f;
            eco.snowLoadMaxKgPerM2 = 30f;

            // Thorns, and it still gets browsed hard by everything tall enough to
            // reach. It tolerates that; it is not protected from it.
            eco.browseTolerance = 0.75f;

            eco.springColour = new Color(0.40f, 0.48f, 0.26f, 1f);
            eco.summerColour = new Color(0.34f, 0.43f, 0.23f, 1f);
            eco.autumnColour = new Color(0.34f, 0.42f, 0.23f, 1f);
            eco.winterColour = new Color(0.33f, 0.41f, 0.23f, 1f);
        }

        private static void SpinifexTussock(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Spinifex Tussock", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;

            // Resinous and built to burn, then straight back from the base. A
            // spinifex plain burns on a cycle of a decade or two and the same
            // hummocks return.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 26f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -6f;
            eco.tempLethalMaxC = 52f;
            eco.warmestMonthMinC = 20f;
            eco.frostFreeFractionMin = 0.6f;
            eco.gddMinRequired = 2500f;

            eco.moistureOptimum = 0.12f;
            eco.moistureTolerance = 0.13f;
            eco.precipOptimumMm = 250f;
            eco.precipToleranceMm = 200f;
            eco.aridityMin = 0.03f;
            eco.aridityMax = 0.6f;
            eco.twiOptimum = 4f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.05f;
            eco.droughtTolerance = 0.95f;

            eco.slopeOptimumDeg = 6f;
            eco.slopeToleranceDeg = 25f;
            eco.soilDepthMinM = 0.08f;

            // The lowest fertility demand in the whole table. Spinifex grows on
            // bare sand with effectively no nutrients in it, which is why it covers
            // ground nothing else will touch.
            eco.soilFertilityOptimum = 0.15f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.85f;

            eco.shadeTolerance = 0.08f;
            eco.laiContribution = 1.4f;
            eco.dispersalLambdaM = 35f;

            // Height and clump width, not stem diameter. For a tussock the spread
            // of the hummock is what the crown and spacing maths needs.
            eco.matureHeightM = new Vector2(0.3f, 1f);
            eco.dbhRangeM = new Vector2(0.3f, 0.9f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 0.8f;
            eco.trunkFlexibility = 0.55f;
            eco.selfThinningK = 200000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 30f;

            // Needle-sharp and resinous. The most thoroughly inedible plant in the
            // table, which is exactly why it dominates where grazing is heavy.
            eco.browseTolerance = 0.95f;

            eco.springColour = new Color(0.58f, 0.60f, 0.32f, 1f);
            eco.summerColour = new Color(0.55f, 0.55f, 0.30f, 1f);
            eco.autumnColour = new Color(0.58f, 0.54f, 0.28f, 1f);
            eco.winterColour = new Color(0.54f, 0.52f, 0.30f, 1f);
        }
    }
}
