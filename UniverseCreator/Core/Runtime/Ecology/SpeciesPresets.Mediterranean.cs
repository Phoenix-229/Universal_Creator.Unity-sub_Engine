// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Mediterranean.cs
//  Purpose: Evergreen sclerophyll trees and shrubs of a summer-dry climate.
//
//  What defines this group is not heat. It is that the rain and the growing season
//  arrive at different times of year, so every plant here is built to sit out a
//  hot dry summer with its leaves still on. That shows up as high drought
//  tolerance, near-zero flood tolerance, small tough leaves and a low aridity
//  ceiling: an olive in a wet climate is not a happier olive, it is a dead one.
//
//  FIRE IS THE OTHER AXIS, and it splits the group cleanly:
//    Olive, cork oak, maquis   resprout. Fire removes the top and the plant is
//                              back the same year from a burl or a stump.
//    Juniper, rosemary         are killed by fire and return from seed, which is
//                              slower and needs a gap to land in.
//  A landscape that burns therefore drifts toward the resprouters, and that is a
//  real process rather than a weighting an author has to guess at.
//
//  A NOTE ON ASPECT, since this is the group where it is most tempting. None of
//  these presets sets aspectPreferenceDeg, even though every one of them favours a
//  sunny slope. The reason is that the sampler already folds hemisphere-correct
//  aspect warming into the site's temperature, so a warm slope is warmer and the
//  thermal fields above respond to it on their own. Naming a compass bearing here
//  would state a northern-hemisphere fact as though it were universal, and would
//  count the same sunlight twice.
//
//  THE ONE THING THIS MODEL CANNOT SAY is soil pH. Cork oak wants acid siliceous
//  ground and will not grow on limestone; rosemary is happiest on the limestone
//  cork oak refuses. The envelope has no pH field, so that distinction is carried
//  as far as it can be by fertility and rockiness and no further.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyMediterranean(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.Olive: Olive(eco); return true;
                case SpeciesPreset.CorkOak: CorkOak(eco); return true;
                case SpeciesPreset.Juniper: Juniper(eco); return true;
                case SpeciesPreset.MaquisScrub: MaquisScrub(eco); return true;
                case SpeciesPreset.RosemaryShrub: RosemaryShrub(eco); return true;
                default: return false;
            }
        }

        private static void Olive(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Olive", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 17f;
            eco.tempToleranceC = 8f;

            // The number that draws the map. Where winters go below about -12 the
            // olive stops, and that line is the practical edge of the
            // Mediterranean climate as agriculture has always defined it.
            eco.tempLethalMinC = -12f;
            eco.tempLethalMaxC = 45f;
            eco.warmestMonthMinC = 20f;
            eco.frostFreeFractionMin = 0.75f;
            eco.gddMinRequired = 2200f;

            eco.moistureOptimum = 0.28f;
            eco.moistureTolerance = 0.2f;
            eco.precipOptimumMm = 550f;
            eco.precipToleranceMm = 350f;

            // A ceiling, not just a floor. Too wet is a real way to fail here.
            eco.aridityMin = 0.2f;
            eco.aridityMax = 1.2f;

            eco.twiOptimum = 4.5f;
            eco.twiTolerance = 3f;
            eco.droughtTolerance = 0.9f;
            eco.floodTolerance = 0.03f;

            eco.slopeOptimumDeg = 12f;
            eco.slopeToleranceDeg = 25f;
            eco.soilDepthMinM = 0.3f;
            eco.soilFertilityOptimum = 0.4f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.8f;

            eco.shadeTolerance = 0.25f;
            eco.laiContribution = 2.8f;

            // Birds eat the drupes and drop the stones, so olive spreads further
            // than a fruit that heavy has any right to.
            eco.dispersalLambdaM = 45f;

            eco.matureHeightM = new Vector2(5f, 12f);

            // Short, and thick out of all proportion to its height. An old olive
            // is nearly all trunk.
            eco.dbhRangeM = new Vector2(0.3f, 1.2f);
            eco.crownAllometryA = 2.8f;
            eco.crownAllometryB = 0.55f;
            eco.selfThinningK = 22000f;
            eco.rootPlateRadiusM = 3f;
            eco.trunkFlexibility = 0.2f;
            eco.windExposureMax = 0.85f;

            // Evergreen and broad-crowned in a place that rarely sees snow, so an
            // unusual fall breaks it.
            eco.snowLoadMaxKgPerM2 = 60f;
            eco.snowCanopyAccumulation = 0.5f;
            eco.browseTolerance = 0.5f;

            eco.springColour = new Color(0.46f, 0.52f, 0.36f, 1f);
            eco.summerColour = new Color(0.42f, 0.48f, 0.34f, 1f);
            eco.autumnColour = new Color(0.41f, 0.47f, 0.34f, 1f);
            eco.winterColour = new Color(0.38f, 0.44f, 0.33f, 1f);
            eco.wetDarkening = 0.3f;
        }

        private static void CorkOak(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Cork Oak", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Mid;

            // The bark is the adaptation. Cork is thick enough that a fire passes
            // over the trunk and the tree carries on, which is why cork oak stands
            // in open grazed woodland where everything else has burned out.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 16f;
            eco.tempToleranceC = 8f;
            eco.tempLethalMinC = -10f;
            eco.tempLethalMaxC = 44f;
            eco.warmestMonthMinC = 19f;
            eco.frostFreeFractionMin = 0.7f;
            eco.gddMinRequired = 2000f;

            eco.moistureOptimum = 0.35f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 700f;
            eco.precipToleranceMm = 350f;
            eco.aridityMin = 0.35f;
            eco.aridityMax = 1.5f;
            eco.twiOptimum = 5f;
            eco.twiTolerance = 3.5f;
            eco.droughtTolerance = 0.8f;
            eco.floodTolerance = 0.05f;

            eco.soilDepthMinM = 0.4f;
            eco.soilFertilityOptimum = 0.35f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.6f;

            eco.shadeTolerance = 0.4f;
            eco.laiContribution = 3.8f;
            eco.dispersalLambdaM = 35f;

            eco.matureHeightM = new Vector2(10f, 20f);
            eco.dbhRangeM = new Vector2(0.4f, 1.3f);

            // The widest crown in the file. A cork oak grown in the open spreads
            // rather than climbs, which is what gives grazed dehesa woodland its
            // look: big isolated umbrellas over grass.
            eco.crownAllometryA = 2.9f;
            eco.crownAllometryB = 0.58f;
            eco.selfThinningK = 16000f;
            eco.rootPlateRadiusM = 3.5f;
            eco.trunkFlexibility = 0.2f;
            eco.windExposureMax = 0.85f;
            eco.snowLoadMaxKgPerM2 = 80f;
            eco.snowCanopyAccumulation = 0.45f;
            eco.browseTolerance = 0.4f;

            eco.springColour = new Color(0.30f, 0.42f, 0.22f, 1f);
            eco.summerColour = new Color(0.20f, 0.34f, 0.18f, 1f);
            eco.autumnColour = new Color(0.20f, 0.33f, 0.18f, 1f);
            eco.winterColour = new Color(0.19f, 0.31f, 0.17f, 1f);
        }

        private static void Juniper(EcoEnvelope eco)
        {
            ResetBiology(eco);

            // A shrub here, which is the usual form. Juniper is the widest-ranging
            // conifer on Earth - arctic tundra to Mediterranean hillside - and the
            // very wide thermal tolerance below is that range, not carelessness.
            Stamp(eco, "Juniper", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Mid;

            // Killed by fire and slow to return from seed. In a landscape that
            // burns often, juniper loses ground to the resprouters around it.
            eco.fireResponse = FireResponse.Killed;

            eco.tempOptimumC = 12f;
            eco.tempToleranceC = 14f;
            eco.tempLethalMinC = -40f;
            eco.tempLethalMaxC = 44f;
            eco.warmestMonthMinC = 7f;
            eco.frostFreeFractionMin = 0.15f;
            eco.gddMinRequired = 700f;

            eco.moistureOptimum = 0.3f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 500f;
            eco.precipToleranceMm = 400f;
            eco.aridityMin = 0.15f;
            eco.aridityMax = 3f;
            eco.twiOptimum = 4f;
            eco.twiTolerance = 3.5f;
            eco.droughtTolerance = 0.85f;
            eco.floodTolerance = 0.03f;

            eco.slopeOptimumDeg = 18f;
            eco.slopeToleranceDeg = 28f;
            eco.soilDepthMinM = 0.1f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.4f;

            // Juniper grows straight out of limestone pavement, so almost no
            // amount of bare rock rules it out.
            eco.rockinessMax = 0.9f;

            eco.shadeTolerance = 0.2f;
            eco.canopyLayer = CanopyLayer.Understory;
            eco.laiContribution = 2.5f;

            // The cones look and taste like berries to a bird, so they travel.
            eco.dispersalLambdaM = 80f;

            eco.matureHeightM = new Vector2(1f, 5f);
            eco.dbhRangeM = new Vector2(0.06f, 0.3f);
            eco.crownAllometryA = 1.6f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.6f;
            eco.selfThinningK = 50000f;
            eco.trunkFlexibility = 0.4f;
            eco.windExposureMax = 0.95f;
            eco.snowLoadMaxKgPerM2 = 250f;
            eco.avalancheTolerance = 0.4f;

            // Prickly and resinous, so stock will not touch it. This is why an
            // overgrazed common ends up as juniper and bare ground: everything
            // palatable has already been eaten.
            eco.browseTolerance = 0.75f;

            eco.springColour = new Color(0.34f, 0.46f, 0.38f, 1f);
            eco.summerColour = new Color(0.30f, 0.42f, 0.36f, 1f);
            eco.autumnColour = new Color(0.29f, 0.41f, 0.35f, 1f);
            eco.winterColour = new Color(0.27f, 0.38f, 0.34f, 1f);
        }

        private static void MaquisScrub(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Maquis Scrub", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Mid;

            // Burns readily and is back the same season from underground burls.
            // Maquis is not a stage on the way to forest; where fire returns every
            // decade or two it is the stable end state.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 16f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -12f;
            eco.tempLethalMaxC = 46f;
            eco.warmestMonthMinC = 18f;
            eco.frostFreeFractionMin = 0.65f;
            eco.gddMinRequired = 1800f;

            eco.moistureOptimum = 0.3f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 600f;
            eco.precipToleranceMm = 400f;
            eco.aridityMin = 0.2f;
            eco.aridityMax = 1.6f;
            eco.twiOptimum = 4.5f;
            eco.twiTolerance = 3.5f;
            eco.droughtTolerance = 0.85f;
            eco.floodTolerance = 0.03f;

            eco.slopeOptimumDeg = 18f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.1f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.85f;

            eco.shadeTolerance = 0.25f;

            // The tallest thing present where it grows, so it shades what is under
            // it rather than sheltering under something else.
            eco.canopyLayer = CanopyLayer.Canopy;

            // Dense enough to be genuinely impassable, which is what the word
            // maquis means.
            eco.laiContribution = 3.5f;
            eco.dispersalLambdaM = 40f;

            eco.matureHeightM = new Vector2(1f, 3.5f);
            eco.dbhRangeM = new Vector2(0.03f, 0.12f);
            eco.crownAllometryA = 2.4f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.4f;

            // Many thin stems rather than a few thick ones: a thicket, not a wood.
            eco.selfThinningK = 90000f;
            eco.trunkFlexibility = 0.45f;
            eco.windExposureMax = 0.95f;
            eco.snowLoadMaxKgPerM2 = 120f;
            eco.avalancheTolerance = 0.3f;
            eco.browseTolerance = 0.6f;

            eco.springColour = new Color(0.40f, 0.48f, 0.30f, 1f);
            eco.summerColour = new Color(0.34f, 0.42f, 0.28f, 1f);
            eco.autumnColour = new Color(0.34f, 0.41f, 0.27f, 1f);
            eco.winterColour = new Color(0.31f, 0.39f, 0.27f, 1f);
        }

        private static void RosemaryShrub(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Rosemary Shrub", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Early;

            // Killed outright and returns from seed stored in the soil. Not
            // serotinous - nothing is held in the canopy waiting for heat.
            eco.fireResponse = FireResponse.Killed;

            eco.tempOptimumC = 16f;
            eco.tempToleranceC = 10f;
            eco.tempLethalMinC = -10f;
            eco.tempLethalMaxC = 48f;
            eco.warmestMonthMinC = 18f;
            eco.frostFreeFractionMin = 0.65f;
            eco.gddMinRequired = 1700f;

            eco.moistureOptimum = 0.2f;
            eco.moistureTolerance = 0.18f;
            eco.precipOptimumMm = 400f;
            eco.precipToleranceMm = 300f;
            eco.aridityMin = 0.12f;
            eco.aridityMax = 1f;
            eco.twiOptimum = 3.5f;
            eco.twiTolerance = 3f;

            // Near the top of the whole table. Where this cannot survive, almost
            // nothing green can.
            eco.droughtTolerance = 0.95f;
            eco.floodTolerance = 0.02f;

            // Onto the convex ground - ridge crests, rock edges, the shoulders of
            // slopes - because that is where the water runs off fastest and
            // nothing taller can hold on.
            eco.curvaturePreference = 0.4f;
            eco.slopeOptimumDeg = 20f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.05f;
            eco.soilFertilityOptimum = 0.2f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.95f;

            eco.shadeTolerance = 0.1f;
            eco.canopyLayer = CanopyLayer.Understory;

            // Wiry and open, not a thicket. Garrigue is a scatter of low bushes
            // over stone, and you can see the stone.
            eco.laiContribution = 1.6f;
            eco.dispersalLambdaM = 20f;

            eco.matureHeightM = new Vector2(0.4f, 1.5f);
            eco.dbhRangeM = new Vector2(0.02f, 0.06f);
            eco.crownAllometryA = 2f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 0.8f;
            eco.selfThinningK = 150000f;
            eco.trunkFlexibility = 0.5f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 60f;

            // Aromatic oils make it unpalatable, and that is why a grazed
            // hillside ends up as rosemary and thyme rather than grass.
            eco.browseTolerance = 0.8f;

            eco.springColour = new Color(0.44f, 0.50f, 0.40f, 1f);
            eco.summerColour = new Color(0.40f, 0.46f, 0.38f, 1f);
            eco.autumnColour = new Color(0.39f, 0.45f, 0.37f, 1f);
            eco.winterColour = new Color(0.37f, 0.43f, 0.36f, 1f);
        }
    }
}
