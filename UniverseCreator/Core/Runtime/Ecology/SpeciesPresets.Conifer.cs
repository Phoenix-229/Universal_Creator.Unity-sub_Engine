// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Conifer.cs
//  Purpose: Boreal and montane conifers - the trees that make a treeline.
//
//  These five are not five flavours of "conifer". They differ in the ways that
//  decide where each one actually grows, and the numbers below are what produce
//  that difference:
//
//    Spruce   shade-tolerant, shallow-rooted, sheds snow off a spire crown, and is
//             the tree that forms the treeline in most of the world.
//    Fir      the most shade-tolerant of the group and the least drought-tolerant.
//             Its dense crown holds snow instead of shedding it, so it wants
//             shelter, and deer eat it, so it wants somewhere they are not.
//    Pine     the opposite of fir on every axis: needs full light, tolerates
//             drought, thrives on poor sand, throws seed a long way, first back
//             after a burn.
//    Larch    a deciduous conifer, which is why it is here. Bare in winter, so it
//             carries almost no snow load, and the most cold-hardy tree there is.
//    Krummholz dwarf pine  a shrub, not a tree, and therefore not subject to the
//             treeline at all. It spends winter under the snowpack, which is both
//             why it survives above the trees and why it is shaped the way it is.
//
//  A NOTE ON GROWING DEGREE DAYS. The site's GDD is accumulated at the shipped
//  temperate base, so every gddMinRequired here is stated on that same scale.
//  Changing a species' gddBaseC without changing how the site accumulates would
//  compare two numbers measured against different baselines, which is why no
//  preset moves it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyConifer(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.Spruce: Spruce(eco); return true;
                case SpeciesPreset.Fir: Fir(eco); return true;
                case SpeciesPreset.Pine: Pine(eco); return true;
                case SpeciesPreset.Larch: Larch(eco); return true;
                case SpeciesPreset.KrummholzDwarfPine: KrummholzDwarfPine(eco); return true;
                default: return false;
            }
        }

        private static void Spruce(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Spruce", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;

            eco.tempOptimumC = 3f;
            eco.tempToleranceC = 8f;
            eco.tempLethalMinC = -50f;
            eco.tempLethalMaxC = 32f;
            eco.warmestMonthMinC = EcoConstants.TreelineWarmestMonthC;
            eco.frostFreeFractionMin = 0.15f;
            eco.gddMinRequired = 400f;

            eco.moistureOptimum = 0.6f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 700f;
            eco.precipToleranceMm = 400f;
            eco.aridityMin = 0.65f;
            eco.twiOptimum = 6.5f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.15f;
            eco.droughtTolerance = 0.2f;

            // Slightly into the hollows, where the snowmelt collects.
            eco.curvaturePreference = -0.2f;
            eco.slopeOptimumDeg = 10f;
            eco.slopeToleranceDeg = 22f;
            eco.soilDepthMinM = 0.2f;
            eco.soilFertilityOptimum = 0.45f;
            eco.rockinessMax = 0.6f;

            eco.shadeTolerance = 0.75f;
            eco.laiContribution = 5.5f;
            eco.dispersalLambdaM = 30f;

            eco.matureHeightM = new Vector2(25f, 40f);
            eco.dbhRangeM = new Vector2(0.4f, 1.0f);
            eco.crownAllometryA = 1.2f;
            eco.crownAllometryB = 0.55f;
            eco.selfThinningK = 30000f;

            // A shallow root plate on a tall stem: spruce is the tree that comes
            // down in a gale, which is what windExposureMax is saying.
            eco.rootPlateRadiusM = 2.5f;
            eco.trunkFlexibility = 0.25f;
            eco.windExposureMax = 0.75f;

            // The spire sheds snow, so it carries a heavy load without breaking.
            eco.snowLoadMaxKgPerM2 = 300f;
            eco.snowCanopyAccumulation = 0.7f;
            eco.avalancheTolerance = 0.15f;
            eco.browseTolerance = 0.6f;

            eco.krummholzAboveExposure = true;
            eco.krummholzHeightScale = 0.25f;

            eco.springColour = new Color(0.30f, 0.46f, 0.28f, 1f);
            eco.summerColour = new Color(0.16f, 0.31f, 0.22f, 1f);
            eco.autumnColour = new Color(0.16f, 0.30f, 0.22f, 1f);
            eco.winterColour = new Color(0.13f, 0.25f, 0.20f, 1f);
            eco.wetDarkening = 0.35f;
        }

        private static void Fir(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Fir", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;

            eco.tempOptimumC = 5f;
            eco.tempToleranceC = 8f;
            eco.tempLethalMinC = -45f;
            eco.tempLethalMaxC = 33f;
            eco.warmestMonthMinC = 7f;
            eco.frostFreeFractionMin = 0.2f;
            eco.gddMinRequired = 500f;

            eco.moistureOptimum = 0.65f;
            eco.moistureTolerance = 0.2f;
            eco.precipOptimumMm = 1000f;
            eco.precipToleranceMm = 400f;
            eco.aridityMin = 0.8f;
            eco.twiOptimum = 6.5f;
            eco.twiTolerance = 3f;
            eco.droughtTolerance = 0.12f;

            eco.curvaturePreference = -0.3f;
            eco.slopeOptimumDeg = 12f;
            eco.slopeToleranceDeg = 22f;
            eco.soilDepthMinM = 0.25f;
            eco.soilFertilityOptimum = 0.55f;
            eco.rockinessMax = 0.5f;

            // The most shade-tolerant of the group: fir seedlings wait in deep
            // shade for decades, which is why fir replaces pine on undisturbed
            // ground and never the other way round.
            eco.shadeTolerance = 0.9f;
            eco.laiContribution = 6.5f;
            eco.dispersalLambdaM = 25f;

            eco.matureHeightM = new Vector2(25f, 45f);
            eco.dbhRangeM = new Vector2(0.4f, 1.2f);
            eco.crownAllometryA = 1.4f;
            eco.crownAllometryB = 0.55f;
            eco.selfThinningK = 28000f;
            eco.rootPlateRadiusM = 2.8f;
            eco.trunkFlexibility = 0.28f;

            // A dense crown catches snow rather than dropping it, so it needs
            // shelter and it breaks under a load a spruce would shrug off.
            eco.windExposureMax = 0.6f;
            eco.snowLoadMaxKgPerM2 = 260f;
            eco.snowCanopyAccumulation = 0.85f;
            eco.avalancheTolerance = 0.15f;

            // Deer strip fir before they touch spruce.
            eco.browseTolerance = 0.35f;

            eco.springColour = new Color(0.33f, 0.50f, 0.28f, 1f);
            eco.summerColour = new Color(0.17f, 0.34f, 0.21f, 1f);
            eco.autumnColour = new Color(0.17f, 0.33f, 0.21f, 1f);
            eco.winterColour = new Color(0.14f, 0.27f, 0.19f, 1f);
        }

        private static void Pine(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Pine", LifeForm.Tree);

            // First onto a burn scar or a sand blow, then shaded out by the fir
            // and spruce that follow it. Its real fire advantage is thick bark on
            // a mature stem, which this three-state model has no term for; what it
            // does capture is the part that decides the picture - pioneer status
            // and a long seed throw, so a burn refills with pine.
            eco.successionStage = SuccessionStage.Pioneer;

            eco.tempOptimumC = 7f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -50f;
            eco.tempLethalMaxC = 38f;
            eco.warmestMonthMinC = 6.8f;
            eco.frostFreeFractionMin = 0.15f;
            eco.gddMinRequired = 450f;

            eco.moistureOptimum = 0.4f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 600f;
            eco.precipToleranceMm = 500f;
            eco.aridityMin = 0.35f;

            // Drier than the others want: pine holds the well-drained ridges and
            // sands that fir cannot use.
            eco.twiOptimum = 4.5f;
            eco.twiTolerance = 3.5f;
            eco.droughtTolerance = 0.7f;

            eco.curvaturePreference = 0.25f;
            eco.slopeOptimumDeg = 12f;
            eco.slopeToleranceDeg = 28f;
            eco.soilDepthMinM = 0.15f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.8f;

            eco.shadeTolerance = 0.15f;
            eco.laiContribution = 3f;
            eco.dispersalLambdaM = 60f;

            eco.matureHeightM = new Vector2(20f, 35f);
            eco.dbhRangeM = new Vector2(0.35f, 1.0f);
            eco.crownAllometryA = 1.7f;
            eco.crownAllometryB = 0.58f;
            eco.selfThinningK = 24000f;

            // A taproot into sand, so it stands where spruce falls over.
            eco.rootPlateRadiusM = 3f;
            eco.trunkFlexibility = 0.3f;
            eco.windExposureMax = 0.85f;

            eco.snowLoadMaxKgPerM2 = 220f;
            eco.snowCanopyAccumulation = 0.45f;
            eco.browseTolerance = 0.5f;

            eco.krummholzAboveExposure = true;
            eco.krummholzHeightScale = 0.3f;

            eco.springColour = new Color(0.40f, 0.52f, 0.26f, 1f);
            eco.summerColour = new Color(0.28f, 0.40f, 0.20f, 1f);
            eco.autumnColour = new Color(0.29f, 0.39f, 0.20f, 1f);
            eco.winterColour = new Color(0.25f, 0.34f, 0.19f, 1f);
        }

        private static void Larch(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Larch", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Early;

            eco.tempOptimumC = 1f;
            eco.tempToleranceC = 10f;

            // The most cold-hardy tree on Earth. Larch is what stands on
            // permafrost where nothing else will, and this number is why.
            eco.tempLethalMinC = -70f;
            eco.tempLethalMaxC = 32f;
            eco.warmestMonthMinC = 6f;
            eco.frostFreeFractionMin = 0.08f;
            eco.gddMinRequired = 300f;

            eco.moistureOptimum = 0.55f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 450f;
            eco.precipToleranceMm = 350f;
            eco.aridityMin = 0.4f;
            eco.twiOptimum = 6f;
            eco.floodTolerance = 0.25f;
            eco.droughtTolerance = 0.45f;

            eco.slopeOptimumDeg = 12f;
            eco.slopeToleranceDeg = 26f;
            eco.soilDepthMinM = 0.15f;
            eco.soilFertilityOptimum = 0.35f;
            eco.rockinessMax = 0.7f;

            // Even more light-demanding than pine: nothing grows under a larch,
            // including larch.
            eco.shadeTolerance = 0.05f;
            eco.laiContribution = 2.2f;
            eco.dispersalLambdaM = 55f;

            eco.matureHeightM = new Vector2(20f, 40f);
            eco.dbhRangeM = new Vector2(0.3f, 0.9f);
            eco.crownAllometryA = 1.3f;
            eco.crownAllometryB = 0.55f;
            eco.selfThinningK = 26000f;
            eco.rootPlateRadiusM = 2.6f;
            eco.trunkFlexibility = 0.32f;
            eco.windExposureMax = 0.8f;

            // Bare through the winter, so the snow goes straight past it. This is
            // the whole advantage of dropping needles in a place with two metres
            // of snow.
            eco.snowLoadMaxKgPerM2 = 320f;
            eco.snowCanopyAccumulation = 0.1f;
            eco.avalancheTolerance = 0.2f;
            eco.browseTolerance = 0.45f;

            eco.krummholzAboveExposure = true;

            eco.deciduous = true;
            eco.budburstGdd = 120f;
            eco.senescenceDayLengthH = 12.5f;
            eco.leafOffFraction = 0.95f;
            eco.seasonalHeightScale = new Vector2(0.95f, 1f);

            eco.springColour = new Color(0.55f, 0.72f, 0.30f, 1f);
            eco.summerColour = new Color(0.32f, 0.50f, 0.22f, 1f);

            // The gold that gives a Siberian or alpine autumn its colour.
            eco.autumnColour = new Color(0.85f, 0.68f, 0.15f, 1f);
            eco.winterColour = new Color(0.34f, 0.29f, 0.24f, 1f);
        }

        private static void KrummholzDwarfPine(EcoEnvelope eco)
        {
            ResetBiology(eco);

            // A shrub, and that is the point. The treeline is a limit on trees;
            // a plant that spends winter buried in snow is insulated from the
            // cold that stops them, so its warmest-month requirement sits well
            // below the treeline value.
            Stamp(eco, "Krummholz Dwarf Pine", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Climax;

            eco.tempOptimumC = 2f;
            eco.tempToleranceC = 8f;
            eco.tempLethalMinC = -50f;
            eco.tempLethalMaxC = 30f;
            eco.warmestMonthMinC = 3.5f;
            eco.frostFreeFractionMin = 0.05f;
            eco.gddMinRequired = 150f;

            eco.moistureOptimum = 0.55f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 1200f;
            eco.precipToleranceMm = 600f;
            eco.aridityMin = 0.6f;
            eco.twiOptimum = 5.5f;
            eco.droughtTolerance = 0.4f;

            // Steep, stony and shallow: the ground above the trees.
            eco.slopeOptimumDeg = 25f;
            eco.slopeToleranceDeg = 25f;
            eco.soilDepthMinM = 0.05f;
            eco.soilFertilityOptimum = 0.25f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.9f;

            eco.shadeTolerance = 0.2f;
            eco.canopyLayer = CanopyLayer.Canopy;
            eco.laiContribution = 3.5f;

            // Short seed throw, so it forms the dense discrete thickets an alpine
            // slope actually carries rather than an even sprinkle.
            eco.dispersalLambdaM = 15f;

            eco.matureHeightM = new Vector2(1f, 3.5f);
            eco.dbhRangeM = new Vector2(0.05f, 0.15f);

            // Wide for its stem thickness. Krummholz sprawls sideways because
            // upright growth gets killed back by wind and ice.
            eco.crownAllometryA = 2.6f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.5f;
            eco.selfThinningK = 60000f;

            // Built to be bent flat and buried, then to stand back up.
            eco.trunkFlexibility = 0.6f;
            eco.windExposureMax = 1f;
            eco.krummholzAboveExposure = true;
            eco.krummholzHeightScale = 0.35f;
            eco.snowLoadMaxKgPerM2 = 600f;
            eco.avalancheTolerance = 0.6f;

            eco.browseTolerance = 0.7f;

            eco.springColour = new Color(0.32f, 0.47f, 0.27f, 1f);
            eco.summerColour = new Color(0.19f, 0.33f, 0.20f, 1f);
            eco.autumnColour = new Color(0.19f, 0.32f, 0.20f, 1f);
            eco.winterColour = new Color(0.16f, 0.27f, 0.18f, 1f);
        }
    }
}
