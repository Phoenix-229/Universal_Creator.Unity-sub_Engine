// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Scatter.cs
//  Purpose: The six non-living objects. Stone, deadfall, driftwood and coral, run
//           through the same envelope as the plants so they land by the same
//           physics instead of by a second placement system.
//
//  WHAT PLACES A STONE IS THE ANGLE OF REPOSE, and the constants file already holds
//  two of them because the physics genuinely differs: angular fragments interlock
//  and rest at 35 degrees, rounded gravel interlocks less and rests at 30. Both are
//  cited below rather than written out, so a stone slope is steep for a stated
//  reason. Scree sits at its repose angle; cobble sits well below its own.
//
//  DENSITY IS WHAT MAKES THESE READ CORRECTLY, and it spans five orders of
//  magnitude across six objects:
//
//    angular scree     2,000,000/ha  a continuous armour of stone, no ground showing
//    rounded cobble      900,000/ha  a paved bar
//    coral head           40,000/ha  a reef
//    driftwood             2,000/ha  dense, but only in a band metres wide
//    deadfall log            300/ha  the real coarse-woody-debris figure for old forest
//    glacial erratic           8/ha  alone, and that is the entire point of it
//
//  An erratic at any higher density stops being a landmark and becomes gravel.
//
//  ELEVEN BIOLOGICAL FIELDS ARE DELIBERATELY MADE INERT HERE. A boulder has no
//  opinion about soil fertility, and the danger is not that it holds a wrong opinion
//  - it is that the Liebig minimum takes the lowest of every factor, so one
//  meaningless term left narrow would silently delete the object from the world with
//  no error anywhere. Moisture, rainfall, wetness index and fertility therefore use
//  the shared inert constants below; light, drought and browsing are set to full
//  indifference; and growth timing is left at the baseline zero.
//
//  FLEXIBILITY IS ZERO ON ALL SIX, and that one is not a nicety. A non-zero value
//  hands the object to the wind animation, and a swaying boulder is the single most
//  obvious defect this file could ship.
//
//  FIRE HAS NO "UNAFFECTED" RESPONSE, so the mapping is stated rather than guessed:
//  wood is Killed, because a log or a driftwood spar genuinely burns and returns only
//  when a new one arrives. Stone and coral are Resprouter, which reads oddly but is
//  behaviourally exact - fire passes over and the object is still there afterwards.
//
//  ONE LIMITATION WORTH KNOWING. There is no field for a landscape's glacial history,
//  which is the only thing that actually explains an erratic. Present-day warmth
//  stands in for it below, and it is a proxy rather than a mechanism.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        // Wide enough that the Gaussian returns essentially one across the whole
        // range of each field. These four terms enter Liebig's minimum, so leaving
        // any of them narrow on an object that has no opinion about it would let the
        // minimum reject a boulder for being on rich soil.
        private const float InertUnitOptimum = 0.5f;
        private const float InertUnitTolerance = 3f;
        private const float InertPrecipOptimumMm = 1200f;
        private const float InertPrecipToleranceMm = 5000f;
        private const float InertTwiOptimum = 8f;
        private const float InertTwiTolerance = 20f;

        // Nothing here grows, decays on the timescale of a season, or dries out in a
        // way the model should care about, and none of these objects is eaten.
        private const float AbioticSnowLoadKgPerM2 = 2000f;

        // Dispersal distance has no meaning for a stone - it did not come from a
        // parent - but the kernel is still evaluated, so a short distance would
        // cluster boulders as if they had bred. Wide enough to be uniform.
        private const float AbioticDispersalM = 500f;

        private static bool ApplyScatter(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.AngularScree: AngularScree(eco); return true;
                case SpeciesPreset.RoundedCobble: RoundedCobble(eco); return true;
                case SpeciesPreset.GlacialErratic: GlacialErratic(eco); return true;
                case SpeciesPreset.DeadfallLog: DeadfallLog(eco); return true;
                case SpeciesPreset.Driftwood: Driftwood(eco); return true;
                case SpeciesPreset.CoralHead: CoralHead(eco); return true;
                default: return false;
            }
        }

        /// <summary>
        /// The fields no non-living object has an opinion about. Called immediately
        /// after ResetBiology so each object below states only the handful of
        /// physical facts that actually place it.
        /// </summary>
        private static void MakeAbiotic(EcoEnvelope eco)
        {
            eco.moistureOptimum = InertUnitOptimum;
            eco.moistureTolerance = InertUnitTolerance;
            eco.precipOptimumMm = InertPrecipOptimumMm;
            eco.precipToleranceMm = InertPrecipToleranceMm;
            eco.twiOptimum = InertTwiOptimum;
            eco.twiTolerance = InertTwiTolerance;
            eco.soilFertilityOptimum = InertUnitOptimum;
            eco.soilFertilityTolerance = InertUnitTolerance;

            // Full indifference to light, water and grazing, so none of the three
            // multiplicative penalties ever touches these objects.
            eco.shadeTolerance = 1f;
            eco.droughtTolerance = 1f;
            eco.browseTolerance = 1f;

            // No leaves, so no contribution to anyone else's shade budget. A boulder
            // does cast a shadow, but leaf area index is not the field that says so.
            eco.laiContribution = 0f;

            // The one that must not be forgotten.
            eco.trunkFlexibility = 0f;

            eco.dispersalLambdaM = AbioticDispersalM;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = AbioticSnowLoadKgPerM2;
            eco.avalancheTolerance = 1f;

            // Sits on the surface. Nothing here needs soil to root into.
            eco.soilDepthMinM = 0f;
            eco.rockinessMax = 1f;
        }

        private static void AngularScree(EcoEnvelope eco)
        {
            ResetBiology(eco);
            MakeAbiotic(eco);
            Stamp(eco, "Angular Scree", LifeForm.Abiotic);

            // First onto ground nothing has colonised yet, which is what a fresh
            // talus cone is.
            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            // Scree rests at its repose angle, so the optimum is the angle itself
            // rather than something near it. The tolerance carries the gentler apron
            // at the foot of a cone and puts the rejection just under sixty degrees,
            // above which loose rock does not stay.
            eco.slopeOptimumDeg = EcoConstants.AngleOfReposeDegrees;
            eco.slopeToleranceDeg = 12f;

            // Chutes and cones are concave: this is the collecting hollow below a
            // crag rather than the crag itself.
            eco.curvaturePreference = -0.3f;

            // No climate gate at all, and that is deliberate. Freeze-thaw shatters
            // rock in the mountains and heating-cooling shatters it in hot deserts,
            // producing the same angular talus by different mechanisms - so slope and
            // rockiness are the honest constraints and temperature is not one. The
            // envelope could not require freeze-thaw anyway: there is a minimum
            // frost-free fraction but no maximum.

            eco.matureHeightM = new Vector2(0.05f, 0.4f);
            eco.dbhRangeM = new Vector2(0.05f, 0.4f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 0.2f;

            // The densest scatter in the table. A scree slope is continuous stone
            // with no ground visible between the fragments.
            eco.selfThinningK = 2000000f;

            eco.springColour = new Color(0.52f, 0.50f, 0.47f, 1f);
            eco.summerColour = new Color(0.54f, 0.52f, 0.49f, 1f);
            eco.autumnColour = new Color(0.52f, 0.50f, 0.47f, 1f);
            eco.winterColour = new Color(0.50f, 0.49f, 0.47f, 1f);

            // Wet rock is markedly darker, and on a scree slope after rain it is the
            // most obvious change in the landscape.
            eco.wetDarkening = 0.6f;
        }

        private static void RoundedCobble(EcoEnvelope eco)
        {
            ResetBiology(eco);
            MakeAbiotic(eco);
            Stamp(eco, "Rounded Cobble", LifeForm.Abiotic);

            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            // THE DEFINING FACT: rounded means water-worked. A cobble is rounded
            // because a river or a surf zone tumbled it for centuries, so a rounded
            // stone far from water is a stone with no history that could have made it.
            // This limit is the whole difference between this object and the scree
            // above, and it is a mechanism rather than a preference.
            eco.distanceToWaterMaxM = 60f;

            // Bars and channel floors are flat. The tolerance is set so rejection
            // lands on the repose angle for rounded gravel, which is where such
            // stones stop resting - rounder grains interlock less and hold a
            // shallower face than angular ones.
            eco.slopeOptimumDeg = 3f;
            eco.slopeToleranceDeg = (EcoConstants.AngleOfReposeRoundedGravelDegrees - 3f) * 0.5f;

            // Channels and bars, mildly concave.
            eco.curvaturePreference = -0.2f;

            eco.matureHeightM = new Vector2(0.05f, 0.3f);
            eco.dbhRangeM = new Vector2(0.08f, 0.35f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 0.18f;

            // A cobble bar is paved. Slightly less than scree because the stones are
            // rounder and pack a little less tightly.
            eco.selfThinningK = 900000f;

            // Greyer and browner than fresh scree: a water-worn stone carries an iron
            // stain and an algal film that a frost-shattered one does not.
            eco.springColour = new Color(0.48f, 0.46f, 0.42f, 1f);
            eco.summerColour = new Color(0.50f, 0.47f, 0.42f, 1f);
            eco.autumnColour = new Color(0.48f, 0.45f, 0.41f, 1f);
            eco.winterColour = new Color(0.46f, 0.44f, 0.41f, 1f);
            eco.wetDarkening = 0.7f;
        }

        private static void GlacialErratic(EcoEnvelope eco)
        {
            ResetBiology(eco);
            MakeAbiotic(eco);
            Stamp(eco, "Glacial Erratic", LifeForm.Abiotic);

            eco.successionStage = SuccessionStage.Pioneer;

            // Eight metres of rock standing clear of everything around it.
            eco.canopyLayer = CanopyLayer.Emergent;
            eco.fireResponse = FireResponse.Resprouter;

            // THE PROXY, and it is worth being plain about: what puts an erratic
            // somewhere is that an ice sheet once stood there and melted, and no
            // field in this envelope carries a landscape's glacial history. A ceiling
            // on warmest-month temperature keeps erratics out of the tropics, which
            // reproduces the right distribution for the wrong reason. Anyone tuning
            // this should know it is a stand-in.
            eco.tempLethalMaxC = 26f;

            // Ice drops its load on lowlands and gentle valley floors, not on faces.
            eco.slopeOptimumDeg = 2f;
            eco.slopeToleranceDeg = 14f;

            // Genuinely indifferent, and this is the one object in the file where
            // indifference is the physically correct answer rather than a gap: an
            // erratic sits precisely where the ice happened to leave it, and no
            // landform preference is involved.
            eco.curvaturePreference = 0f;

            eco.matureHeightM = new Vector2(1f, 8f);
            eco.dbhRangeM = new Vector2(1.5f, 9f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;

            // A large footprint, so the placement pass keeps everything else off it
            // rather than growing a tree out of solid granite.
            eco.rootPlateRadiusM = 4f;

            // Eight per hectare, and the lowest figure in the entire table by three
            // orders of magnitude. An erratic is a landmark because it is alone; at
            // any higher density it is just a boulder field.
            eco.selfThinningK = 8f;

            eco.springColour = new Color(0.46f, 0.45f, 0.44f, 1f);
            eco.summerColour = new Color(0.48f, 0.47f, 0.45f, 1f);
            eco.autumnColour = new Color(0.46f, 0.45f, 0.44f, 1f);
            eco.winterColour = new Color(0.45f, 0.44f, 0.44f, 1f);
            eco.wetDarkening = 0.55f;
        }

        private static void DeadfallLog(EcoEnvelope eco)
        {
            ResetBiology(eco);
            MakeAbiotic(eco);
            Stamp(eco, "Deadfall Log", LifeForm.Abiotic);

            // It appears once a stand is old enough to be losing trees, not on fresh
            // ground.
            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.GroundCover;

            // Wood burns, and a burnt log is gone until another tree falls.
            eco.fireResponse = FireResponse.Killed;

            // THE EXACT CONSTRAINT, and the reason this object needs no proxy: a log
            // is a dead tree, so it can only exist where a tree could have grown. The
            // treeline constant is cited directly, which means deadfall stops at the
            // same line the forest does and cannot appear on alpine turf.
            eco.warmestMonthMinC = EcoConstants.TreelineWarmestMonthC;

            // And not in a desert, where there was never enough wood to fall.
            eco.aridityMin = 0.5f;

            eco.slopeOptimumDeg = 8f;
            eco.slopeToleranceDeg = 25f;

            eco.rockinessMax = 0.8f;

            // Height off the ground, which is the trunk's diameter as it lies. The
            // envelope has no length field, so the mesh carries that; these two
            // numbers are the cross-section.
            eco.matureHeightM = new Vector2(0.15f, 1f);
            eco.dbhRangeM = new Vector2(0.2f, 1.2f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.5f;

            // Three hundred pieces of coarse woody debris per hectare, which is the
            // measured figure for unmanaged temperate forest. A managed wood has
            // almost none, and the difference is one of the clearest signals of
            // whether a forest has been worked.
            eco.selfThinningK = 300f;

            // Grey-brown weathered wood, greener where moss has taken it.
            eco.springColour = new Color(0.36f, 0.31f, 0.24f, 1f);
            eco.summerColour = new Color(0.34f, 0.29f, 0.23f, 1f);
            eco.autumnColour = new Color(0.33f, 0.28f, 0.22f, 1f);
            eco.winterColour = new Color(0.31f, 0.27f, 0.23f, 1f);
            eco.wetDarkening = 0.65f;
        }

        private static void Driftwood(EcoEnvelope eco)
        {
            ResetBiology(eco);
            MakeAbiotic(eco);
            Stamp(eco, "Driftwood", LifeForm.Abiotic);

            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            // Thrown above high water by a storm and left there. This is the strand
            // line, and the supratidal band is exactly the field that describes it -
            // driftwood in the intertidal would float away on the next tide.
            eco.tidalZone = TidalZone.Supratidal;
            eco.saltTolerance = 1f;
            eco.distanceToWaterMaxM = 80f;

            eco.slopeOptimumDeg = 2f;
            eco.slopeToleranceDeg = 12f;

            eco.matureHeightM = new Vector2(0.1f, 0.8f);
            eco.dbhRangeM = new Vector2(0.15f, 1f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.2f;

            // Dense within the band and absent a few metres either side of it, which
            // is what a strand line is. The tidal gate does the narrowing; this
            // number only says how much wood is in the band.
            eco.selfThinningK = 2000f;

            // Silver-grey. Salt and ultraviolet bleach driftwood to a colour no
            // inland deadwood ever reaches, and getting this wrong is what makes a
            // beach look like a forest floor.
            eco.springColour = new Color(0.64f, 0.62f, 0.58f, 1f);
            eco.summerColour = new Color(0.66f, 0.64f, 0.60f, 1f);
            eco.autumnColour = new Color(0.62f, 0.60f, 0.56f, 1f);
            eco.winterColour = new Color(0.60f, 0.58f, 0.55f, 1f);
            eco.wetDarkening = 0.6f;
        }

        private static void CoralHead(EcoEnvelope eco)
        {
            ResetBiology(eco);
            MakeAbiotic(eco);

            // Filed with the stones because it is placed as a hard object on a
            // seabed, not because it is not alive. Everything below it is.
            Stamp(eco, "Coral Head", LifeForm.Abiotic);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Understory;
            eco.fireResponse = FireResponse.Resprouter;

            eco.tidalZone = TidalZone.Subtidal;
            eco.submergedDepthRangeM = new Vector2(1f, 25f);
            eco.saltTolerance = 1f;

            eco.tempOptimumC = 27f;

            // The narrowest thermal window of anything in the table, and the two
            // limits below are the two best-known numbers in reef biogeography.
            eco.tempToleranceC = 4f;

            // Eighteen degrees. The reef-building corals stop at this isotherm
            // worldwide - it is why there is no reef off a cool coast at the same
            // latitude as one that has them.
            eco.tempLethalMinC = 18f;

            // And thirty-one is bleaching. A few weeks above it and the colony
            // expels its algae and dies, which is why a marine heatwave kills a reef
            // that survived the previous ten thousand years.
            eco.tempLethalMaxC = 31f;

            // Under water, so the wetness index and the surface snow load are as
            // meaningless here as in the aquatic group and are neutralised the same
            // way rather than with a second set of numbers.
            eco.twiOptimum = SubmergedTwiOptimum;
            eco.twiTolerance = SubmergedTwiTolerance;
            eco.snowLoadMaxKgPerM2 = SubmergedSnowLoadKgPerM2;
            eco.floodTolerance = 1f;

            // Needs hard substrate, and grows on its own dead skeleton.
            eco.slopeOptimumDeg = 10f;
            eco.slopeToleranceDeg = 25f;

            // Overridden from the abiotic default of full indifference, because this
            // object genuinely needs light: the algae inside it photosynthesise, and
            // silt washing off a cleared hillside kills a reef by shading it. That is
            // the mechanism behind most inshore reef loss.
            eco.shadeTolerance = 0.25f;

            eco.matureHeightM = new Vector2(0.3f, 4f);
            eco.dbhRangeM = new Vector2(0.4f, 5f);
            eco.crownAllometryA = 1.4f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.5f;
            eco.selfThinningK = 40000f;

            // Reef crests take the heaviest surf on earth and are built by it.
            eco.windExposureMax = 1f;

            // Overridden as well. Parrotfish and urchins scrape coral constantly, and
            // bioerosion is a major part of how a reef is shaped.
            eco.browseTolerance = 0.4f;

            // Living colour, and no seasons to change it.
            eco.springColour = new Color(0.74f, 0.54f, 0.44f, 1f);
            eco.summerColour = new Color(0.72f, 0.52f, 0.42f, 1f);
            eco.autumnColour = new Color(0.72f, 0.52f, 0.42f, 1f);
            eco.winterColour = new Color(0.70f, 0.51f, 0.42f, 1f);
            eco.wetDarkening = 0.3f;
        }
    }
}
