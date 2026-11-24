// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Alpine.cs
//  Purpose: Plants of the ground above the treeline.
//
//  EVERY WARMEST-MONTH MINIMUM IN THIS FILE IS BELOW THE TREELINE CONSTANT, and
//  that is the point of the group rather than an oversight. Korner's threshold is
//  the temperature below which a tree cannot build wood; these species are the ones
//  that live above it. Setting their gate at the treeline figure would have excluded
//  them from precisely the ground they own and left the high slopes bare.
//
//  NO PRESET HERE SETS AN ABSOLUTE ALTITUDE BAND. It is tempting - alpine means
//  high - but altitude only matters through what it does to temperature, and the
//  lapse rate already does that. A stated band in metres would put the alpine zone
//  at the same height on an equatorial volcano and a Scottish hill, which is wrong
//  by three thousand metres. The thermal fields find the zone on their own.
//
//  CURVATURE SPLITS THE GROUP, and this is the most visible thing in the file:
//     cushion plant  +0.30  wind-scoured convex ridge, blown clear of snow
//     lichen         +0.40  bare exposed rock, the most scoured ground there is
//     alpine sedge   -0.15  slight hollows, where snowmelt lasts into summer
//     dwarf willow   -0.40  the snowbed itself, which is what it needs to survive
//  Two of these can grow four metres apart and be separated by nothing except
//  whether the snow lies there. That is how real alpine vegetation is patterned,
//  and it comes out of one number per species.
//
//  ALPINE GROUND IS PHYSIOLOGICALLY DRY. Frozen water is unavailable water, and
//  wind on a ridge takes the rest, so the drought tolerances here are far higher
//  than "cold and wet mountain" suggests - a cushion plant needs more drought
//  resistance than a beech.
//
//  Snow load limits are the highest in the table. These plants spend two thirds of
//  the year buried, and the snow is what keeps them alive: an alpine plant in a
//  snowless winter freeze-dries.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyAlpine(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.CushionPlant: CushionPlant(eco); return true;
                case SpeciesPreset.AlpineSedge: AlpineSedge(eco); return true;
                case SpeciesPreset.DwarfWillow: DwarfWillow(eco); return true;
                case SpeciesPreset.CrustoseLichen: CrustoseLichen(eco); return true;
                default: return false;
            }
        }

        private static void CushionPlant(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Cushion Plant", LifeForm.Herb);

            // Centuries old and still ten centimetres across. On the ground it holds
            // there is nothing that succeeds it.
            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.GroundCover;

            // Too slow to recover. A burnt cushion is a bare patch for a human
            // lifetime.
            eco.fireResponse = FireResponse.Killed;

            // Comfortably below the treeline threshold, which is the whole reason
            // this plant has anywhere to live.
            eco.warmestMonthMinC = 2f;

            eco.tempOptimumC = 4f;
            eco.tempToleranceC = 9f;

            // Insulated under snow for most of the year and exposed to anything for
            // the rest.
            eco.tempLethalMinC = -60f;

            // A low ceiling, and it is heat that actually kills cushion plants -
            // the dome that traps warmth on a summit cooks it at low altitude.
            eco.tempLethalMaxC = 28f;
            eco.frostFreeFractionMin = 0.03f;
            eco.gddMinRequired = 180f;

            eco.moistureOptimum = 0.4f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 700f;
            eco.aridityMin = 0.4f;
            eco.twiOptimum = 4f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.05f;

            // Higher than a beech's, on a wet mountain. Water locked up as ice is
            // water the plant cannot have, and the wind on a ridge takes whatever
            // thaws.
            eco.droughtTolerance = 0.7f;

            // The wind-scoured convex ridge, blown clear of snow. Exposed, and
            // preferred - the dome is its own shelter.
            eco.curvaturePreference = 0.3f;
            eco.slopeOptimumDeg = 15f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.03f;
            eco.soilFertilityOptimum = 0.2f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.9f;

            eco.shadeTolerance = 0.1f;
            eco.laiContribution = 1f;
            eco.dispersalLambdaM = 25f;

            // The smallest plant in the table by height.
            eco.matureHeightM = new Vector2(0.02f, 0.15f);

            // Cushion diameter. A metre-wide Silene cushion is three centuries old,
            // and the spacing maths needs the dome, not a stem.
            eco.dbhRangeM = new Vector2(0.1f, 0.6f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.4f;

            // A taproot that is deep for the size of the plant, and almost nothing
            // laterally.
            eco.rootPlateRadiusM = 0.3f;

            // Rigid. A cushion is a solid dome and does not stir in the wind; the
            // whole architecture exists to refuse the wind rather than yield to it.
            eco.trunkFlexibility = 0.05f;
            eco.selfThinningK = 400000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 700f;
            eco.avalancheTolerance = 0.3f;
            eco.browseTolerance = 0.5f;

            eco.springColour = new Color(0.32f, 0.44f, 0.28f, 1f);
            eco.summerColour = new Color(0.28f, 0.40f, 0.26f, 1f);
            eco.autumnColour = new Color(0.34f, 0.38f, 0.24f, 1f);
            eco.winterColour = new Color(0.30f, 0.32f, 0.24f, 1f);
        }

        private static void AlpineSedge(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Alpine Sedge", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            eco.warmestMonthMinC = 2.5f;
            eco.tempOptimumC = 5f;
            eco.tempToleranceC = 9f;
            eco.tempLethalMinC = -50f;
            eco.tempLethalMaxC = 30f;
            eco.frostFreeFractionMin = 0.05f;
            eco.gddMinRequired = 200f;

            // THE SEPARATION FROM THE WETLAND SEDGE, which shares the name and
            // nothing else. That one wants standing water and a fen; this one forms
            // dry turf on a mountainside. Same genus, opposite ends of the moisture
            // axis, and only these numbers distinguish them.
            eco.moistureOptimum = 0.45f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 1000f;
            eco.precipToleranceMm = 700f;
            eco.aridityMin = 0.5f;
            eco.twiOptimum = 5f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.1f;
            eco.droughtTolerance = 0.6f;

            // Slight hollows, where the snow lies a few weeks longer and the melt
            // water lasts into the growing season.
            eco.curvaturePreference = -0.15f;
            eco.slopeOptimumDeg = 18f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.08f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.8f;

            eco.shadeTolerance = 0.15f;
            eco.laiContribution = 1.8f;
            eco.dispersalLambdaM = 40f;

            eco.matureHeightM = new Vector2(0.03f, 0.25f);
            eco.dbhRangeM = new Vector2(0.1f, 0.4f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 0.3f;
            eco.trunkFlexibility = 0.6f;

            // A closed alpine turf - among the densest cover in the table, because
            // above the treeline this is what continuous vegetation looks like.
            eco.selfThinningK = 600000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 700f;
            eco.avalancheTolerance = 0.5f;

            // Grazed by chamois, ibex and marmot all summer, and it holds. The
            // growing point of a sedge is at ground level, below the bite, which is
            // why the turf survives what would strip a shrub.
            eco.browseTolerance = 0.8f;

            eco.springColour = new Color(0.42f, 0.56f, 0.32f, 1f);
            eco.summerColour = new Color(0.36f, 0.50f, 0.28f, 1f);
            eco.autumnColour = new Color(0.60f, 0.52f, 0.28f, 1f);
            eco.winterColour = new Color(0.54f, 0.48f, 0.32f, 1f);
            eco.seasonalHeightScale = new Vector2(0.5f, 1f);
        }

        private static void DwarfWillow(EcoEnvelope eco)
        {
            ResetBiology(eco);

            // A woody plant three centimetres tall. Filed as a shrub because that is
            // what it is botanically, and the height range below is not a typo.
            Stamp(eco, "Dwarf Willow", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            // The lowest warmest-month requirement of any woody plant here. This is
            // the plant that gets closest to the ice.
            eco.warmestMonthMinC = 1.5f;

            eco.tempOptimumC = 4f;
            eco.tempToleranceC = 8f;
            eco.tempLethalMinC = -55f;

            // The lowest heat ceiling of any land plant in the table. An
            // arctic-alpine species has nowhere to go when the climate warms, and
            // this number is why.
            eco.tempLethalMaxC = 26f;
            eco.frostFreeFractionMin = 0.03f;

            // Tied with the lichen for the shortest growing season anything here
            // will accept. Six weeks between thaw and first frost.
            eco.gddMinRequired = 150f;

            eco.moistureOptimum = 0.6f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 1100f;
            eco.precipToleranceMm = 700f;
            eco.aridityMin = 0.6f;
            eco.twiOptimum = 7f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.2f;
            eco.droughtTolerance = 0.3f;

            // THE SNOWBED. The strongest concave preference in this file, and it is
            // a requirement rather than a taste: without deep late-lying snow this
            // plant freeze-dries in winter. It is also why dwarf willow marks out
            // the hollows on a slope where the cushion plants hold the ridges.
            eco.curvaturePreference = -0.4f;
            eco.slopeOptimumDeg = 12f;
            eco.slopeToleranceDeg = 28f;
            eco.soilDepthMinM = 0.06f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.7f;

            eco.shadeTolerance = 0.25f;
            eco.laiContribution = 1.5f;

            // Plumed willow seed, light enough to cross a corrie. Shorter than the
            // lowland willow's only because there is less of the plant to launch it.
            eco.dispersalLambdaM = 80f;

            eco.matureHeightM = new Vector2(0.02f, 0.1f);

            // Mat width. There is a woody stem in there, but it runs along the
            // ground rather than up.
            eco.dbhRangeM = new Vector2(0.1f, 0.5f);
            eco.crownAllometryA = 1.2f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 0.4f;
            eco.trunkFlexibility = 0.4f;
            eco.selfThinningK = 500000f;
            eco.windExposureMax = 1f;

            // The highest in the table, and it is not endurance - it is what the
            // plant is asking for.
            eco.snowLoadMaxKgPerM2 = 800f;

            // Lives in active avalanche tracks, where anything taller is removed
            // every spring. Being three centimetres tall is the strategy.
            eco.avalancheTolerance = 0.7f;
            eco.browseTolerance = 0.4f;

            eco.deciduous = true;

            // Into leaf almost the moment the snow goes, and out again early. There
            // is no time to be cautious with a season this short.
            eco.budburstGdd = 80f;
            eco.senescenceDayLengthH = 13.5f;
            eco.leafOffFraction = 0.95f;

            eco.springColour = new Color(0.44f, 0.58f, 0.32f, 1f);
            eco.summerColour = new Color(0.34f, 0.48f, 0.28f, 1f);
            eco.autumnColour = new Color(0.80f, 0.70f, 0.30f, 1f);
            eco.winterColour = new Color(0.34f, 0.30f, 0.26f, 1f);
        }

        private static void CrustoseLichen(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Crustose Lichen", LifeForm.Lichen);

            // The first thing onto bare rock, and on rock that stays bare it is also
            // the last.
            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            // No warmest-month requirement and no growing degree days at all, both
            // left at the baseline zero deliberately. This organism grows on
            // Antarctic nunataks in the few hours a year the rock is above freezing,
            // and any thermal season gate would be a fiction.
            eco.tempOptimumC = 2f;

            // The widest thermal tolerance in the table by a distance: sea-level
            // Arctic to six thousand metres, on the same lichen.
            eco.tempToleranceC = 14f;
            eco.tempLethalMinC = -70f;
            eco.tempLethalMaxC = 45f;

            eco.moistureOptimum = 0.3f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 700f;
            eco.aridityMin = 0.15f;
            eco.twiOptimum = 4f;
            eco.twiTolerance = 4f;
            eco.floodTolerance = 0.02f;

            // The highest value in the table, by a mechanism nothing else here uses:
            // it does not resist drying, it simply dries out completely, stops, and
            // starts again when wet. A cactus dies after two rainless years; this
            // survives twenty.
            eco.droughtTolerance = 1f;

            // Exposed rock faces and boulder tops.
            eco.curvaturePreference = 0.4f;
            eco.slopeOptimumDeg = 25f;
            eco.slopeToleranceDeg = 40f;

            // None whatsoever. It is cemented directly to the mineral surface.
            eco.soilDepthMinM = 0f;
            eco.rockinessMax = 1f;

            // A very low optimum with a narrow tolerance, and that pairing is doing
            // real work: there is no "requires rock" field, so this is what keeps a
            // rock lichen off a fertile meadow. It is also true - not because the
            // lichen would die there, but because on soil it is overgrown within a
            // season by anything with roots.
            eco.soilFertilityOptimum = 0.05f;
            eco.soilFertilityTolerance = 0.15f;

            eco.shadeTolerance = 0.5f;

            // Effectively nothing. A crust intercepts almost no light and shades
            // nothing at all.
            eco.laiContribution = 0.2f;

            // Spores and wind-blown fragments travel a long way; the limit on
            // colonising new rock is time, not distance.
            eco.dispersalLambdaM = 200f;

            // Millimetres. This is a crust, not a plant standing on the rock.
            eco.matureHeightM = new Vector2(0.005f, 0.02f);

            // Patch diameter, and it is a clock: a crustose lichen spreads about
            // half a millimetre a year, so a thirty-centimetre patch is six
            // centuries old. This is what makes old scree read as old and fresh
            // rockfall read as fresh.
            eco.dbhRangeM = new Vector2(0.02f, 0.4f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 0.02f;

            // Zero, and it must be zero. A lichen is glued to the stone; any wind
            // animation applied to one would be a visible bug.
            eco.trunkFlexibility = 0f;

            // A continuous crust - the highest cover figure in the table, because at
            // this scale there are no individuals to keep apart.
            eco.selfThinningK = 1000000f;

            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 800f;

            // Deliberately low. An avalanche scrapes rock back to bare mineral, and
            // lichen-free stone in an otherwise lichen-covered slope is exactly how
            // a recent avalanche track is recognised on the ground.
            eco.avalancheTolerance = 0.1f;
            eco.browseTolerance = 0.85f;

            // The map lichen's vivid yellow-green with black margins. Unmistakable,
            // and nothing else in the table is this colour.
            eco.springColour = new Color(0.62f, 0.66f, 0.32f, 1f);
            eco.summerColour = new Color(0.60f, 0.64f, 0.30f, 1f);
            eco.autumnColour = new Color(0.58f, 0.62f, 0.30f, 1f);
            eco.winterColour = new Color(0.52f, 0.56f, 0.30f, 1f);

            // Dry lichen is chalky and pale; wet lichen is saturated and dark. The
            // change is dramatic and happens within seconds of rain starting.
            eco.wetDarkening = 0.5f;
        }
    }
}
