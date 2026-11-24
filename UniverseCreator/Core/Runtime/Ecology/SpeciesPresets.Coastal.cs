// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Coastal.cs
//  Purpose: Plants of salt water, salt spray and moving sand.
//
//  TIDALZONE IS WHY THIS GROUP IS SEPARATE. Salt tolerance only permits salt; it
//  cannot require the sea, so a plant given nothing but a high salt tolerance would
//  happily appear beside a freshwater lake. TidalZone is the field that actually
//  binds a species to an ocean shore, and three of the four here use it:
//
//    Mangrove         Intertidal - roots in mud that floods twice a day
//    Saltmarsh grass  Intertidal - the same zone, in a cold climate
//    Marram           Supratidal - above the tide, in the salt spray, on bare sand
//    Sea buckthorn    none. It is a coastal plant, not a shore plant, and it grows
//                     on inland river shingle too, so a distance limit is the
//                     honest constraint rather than a tidal band.
//
//  MANGROVE AND SALTMARSH OCCUPY THE SAME ZONE and never meet, because temperature
//  separates them: the mangrove dies below about eight degrees and the saltmarsh
//  grass survives thirty below. That is the whole reason a tropical estuary is
//  forest and a temperate one is grass, and it needs no rule beyond the two lethal
//  minima below.
//
//  MARRAM IS THE COUNTERINTUITIVE ONE. It lives at the edge of the sea and it is a
//  drought plant - dune sand drains in minutes and the surface is bone dry. It also
//  grows better when buried, which is why it builds dunes rather than merely living
//  on them, and why its wind exposure limit is the maximum.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyCoastal(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.Mangrove: Mangrove(eco); return true;
                case SpeciesPreset.SaltmarshGrass: SaltmarshGrass(eco); return true;
                case SpeciesPreset.MarramDuneGrass: MarramDuneGrass(eco); return true;
                case SpeciesPreset.SeaBuckthorn: SeaBuckthorn(eco); return true;
                default: return false;
            }
        }

        private static void Mangrove(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Mangrove", LifeForm.Tree);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.Canopy;
            eco.fireResponse = FireResponse.Killed;

            eco.tidalZone = TidalZone.Intertidal;

            eco.tempOptimumC = 26f;
            eco.tempToleranceC = 5f;

            // Frost is what stops mangroves, and it stops them abruptly. This one
            // number is why mangrove forest ends at roughly the same latitude on
            // every coast in the world.
            eco.tempLethalMinC = 8f;
            eco.tempLethalMaxC = 46f;
            eco.warmestMonthMinC = 22f;
            eco.frostFreeFractionMin = 0.99f;
            eco.gddMinRequired = 5000f;

            eco.moistureOptimum = 0.95f;
            eco.moistureTolerance = 0.12f;
            eco.precipOptimumMm = 1800f;
            eco.precipToleranceMm = 1000f;

            // Deliberately low, like the willow's. Mangroves line the shores of the
            // Red Sea and the Persian Gulf, where it barely rains at all - the water
            // they stand in is not the water that falls.
            eco.aridityMin = 0.15f;

            eco.twiOptimum = 12f;
            eco.twiTolerance = 4f;
            eco.waterTableMaxDepthM = 0.2f;
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.2f;
            eco.distanceToWaterMaxM = 100f;

            // Full seawater. Nothing else in the table takes this.
            eco.saltTolerance = 1f;

            eco.slopeOptimumDeg = 1f;
            eco.slopeToleranceDeg = 5f;
            eco.soilDepthMinM = 0.5f;
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.4f;

            // Deep mud. Rock is the one substrate a mangrove cannot use.
            eco.rockinessMax = 0.2f;

            eco.shadeTolerance = 0.5f;
            eco.laiContribution = 4.5f;

            // The propagule drops, floats off on the tide and roots where it
            // grounds. Second only to the coconut, and for the same reason.
            eco.dispersalLambdaM = 300f;

            eco.matureHeightM = new Vector2(5f, 25f);
            eco.dbhRangeM = new Vector2(0.15f, 0.6f);
            eco.crownAllometryA = 2.2f;
            eco.crownAllometryB = 0.55f;

            // Prop roots, spread wide across the mud rather than driven into it.
            // Wide enough to hold a tree upright in ground with no bearing strength
            // at all.
            eco.rootPlateRadiusM = 4f;
            eco.trunkFlexibility = 0.35f;
            eco.selfThinningK = 40000f;

            // Takes cyclones on an open coast with nothing in front of it.
            eco.windExposureMax = 0.9f;
            eco.snowLoadMaxKgPerM2 = TropicalSnowLoadKgPerM2;
            eco.browseTolerance = 0.6f;

            eco.springColour = new Color(0.20f, 0.38f, 0.23f, 1f);
            eco.summerColour = new Color(0.18f, 0.36f, 0.22f, 1f);
            eco.autumnColour = new Color(0.18f, 0.36f, 0.22f, 1f);
            eco.winterColour = new Color(0.18f, 0.35f, 0.22f, 1f);
            eco.wetDarkening = 0.3f;
        }

        private static void SaltmarshGrass(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Saltmarsh Grass", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.GroundCover;

            // Rhizomatous, so cutting, burning or grazing it removes only the top.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tidalZone = TidalZone.Intertidal;

            eco.tempOptimumC = 13f;

            // Wide, and the contrast with the mangrove's five degrees is the point:
            // this plant holds the same zone from temperate estuary to subarctic.
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -30f;
            eco.tempLethalMaxC = 42f;
            eco.warmestMonthMinC = 8f;
            eco.frostFreeFractionMin = 0.2f;
            eco.gddMinRequired = 800f;

            eco.moistureOptimum = 0.95f;
            eco.moistureTolerance = 0.15f;

            // A very wide rainfall tolerance because rainfall is nearly irrelevant
            // to a plant standing in the sea.
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 700f;
            eco.aridityMin = 0.1f;

            eco.twiOptimum = 12f;
            eco.twiTolerance = 4f;
            eco.waterTableMaxDepthM = 0.15f;
            eco.floodTolerance = 1f;
            eco.droughtTolerance = 0.25f;
            eco.distanceToWaterMaxM = 80f;
            eco.saltTolerance = 1f;

            eco.slopeOptimumDeg = 1f;
            eco.slopeToleranceDeg = 4f;
            eco.soilDepthMinM = 0.2f;
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.15f;

            eco.shadeTolerance = 0.15f;
            eco.laiContribution = 2.5f;

            // Seed and broken rhizome fragments both float off on the tide.
            eco.dispersalLambdaM = 120f;

            eco.matureHeightM = new Vector2(0.3f, 1.5f);

            // Clump width, not stem diameter - the spacing maths needs the spread of
            // the tussock, and a grass has no trunk to measure.
            eco.dbhRangeM = new Vector2(0.1f, 0.4f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 0.6f;

            // Lies flat under the incoming tide twice a day and stands back up.
            eco.trunkFlexibility = 0.75f;
            eco.selfThinningK = 300000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 100f;

            // Geese strip a saltmarsh bare over a winter, and it comes back.
            eco.browseTolerance = 0.4f;

            eco.springColour = new Color(0.42f, 0.58f, 0.34f, 1f);
            eco.summerColour = new Color(0.36f, 0.52f, 0.30f, 1f);
            eco.autumnColour = new Color(0.58f, 0.54f, 0.32f, 1f);
            eco.winterColour = new Color(0.52f, 0.48f, 0.34f, 1f);

            // Dies back to stubble over winter rather than dropping leaves.
            eco.seasonalHeightScale = new Vector2(0.4f, 1f);
        }

        private static void MarramDuneGrass(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Marram Dune Grass", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Pioneer;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            // Above the tide, in the spray. Marram drowns in salt water and dies
            // where the sea reaches it, so it cannot be intertidal - but it needs
            // to be close enough that the wind is still carrying sand.
            eco.tidalZone = TidalZone.Supratidal;

            eco.tempOptimumC = 12f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -25f;
            eco.tempLethalMaxC = 42f;
            eco.warmestMonthMinC = 9f;
            eco.frostFreeFractionMin = 0.25f;
            eco.gddMinRequired = 900f;

            // A drought plant at the edge of the ocean. Dune sand drains in minutes
            // and the top half-metre is dry within hours of rain, so marram lives
            // wet-footed and dry-leaved at once.
            eco.moistureOptimum = 0.25f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 700f;
            eco.precipToleranceMm = 600f;
            eco.aridityMin = 0.25f;
            eco.twiOptimum = 3.5f;
            eco.twiTolerance = 3f;
            eco.floodTolerance = 0.1f;
            eco.droughtTolerance = 0.85f;
            eco.distanceToWaterMaxM = 500f;

            // Spray, not immersion. High, but not the full seawater the marsh takes.
            eco.saltTolerance = 0.75f;

            // Onto the convex ground it built itself. Marram grows up through
            // accumulating sand faster than the sand can bury it, and the dune is
            // the by-product.
            eco.curvaturePreference = 0.35f;
            eco.slopeOptimumDeg = 15f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.3f;
            eco.soilFertilityOptimum = 0.15f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.2f;

            eco.shadeTolerance = 0.08f;
            eco.laiContribution = 1.8f;

            // Mostly by rhizome, which runs metres through the sand, so it advances
            // as a front rather than appearing in isolated tufts.
            eco.dispersalLambdaM = 60f;

            eco.matureHeightM = new Vector2(0.4f, 1.2f);
            eco.dbhRangeM = new Vector2(0.1f, 0.35f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1f;
            eco.trunkFlexibility = 0.7f;
            eco.selfThinningK = 250000f;

            // The maximum, and it is not tolerance - it is a requirement. Marram in
            // still air is marram being shaded out by everything else.
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 120f;
            eco.browseTolerance = 0.55f;

            // Distinctly grey-blue rather than green: a waxy cuticle against the
            // salt and the drying wind.
            eco.springColour = new Color(0.52f, 0.60f, 0.46f, 1f);
            eco.summerColour = new Color(0.48f, 0.55f, 0.42f, 1f);
            eco.autumnColour = new Color(0.60f, 0.58f, 0.40f, 1f);
            eco.winterColour = new Color(0.55f, 0.52f, 0.40f, 1f);
            eco.seasonalHeightScale = new Vector2(0.6f, 1f);
        }

        private static void SeaBuckthorn(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Sea Buckthorn", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Early;
            eco.canopyLayer = CanopyLayer.Canopy;

            // Suckers hard from the roots, so removing the top produces a thicket
            // rather than a gap.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 11f;

            // Very wide, and genuinely so: this is a plant of North Sea dunes and
            // also of Himalayan and Siberian river gravels.
            eco.tempToleranceC = 13f;
            eco.tempLethalMinC = -40f;
            eco.tempLethalMaxC = 40f;
            eco.warmestMonthMinC = 9f;
            eco.frostFreeFractionMin = 0.25f;
            eco.gddMinRequired = 900f;

            eco.moistureOptimum = 0.35f;
            eco.moistureTolerance = 0.28f;
            eco.precipOptimumMm = 600f;
            eco.precipToleranceMm = 500f;
            eco.aridityMin = 0.25f;
            eco.twiOptimum = 4.5f;
            eco.twiTolerance = 4f;
            eco.floodTolerance = 0.15f;
            eco.droughtTolerance = 0.75f;

            // A generous limit rather than a tidal band, because sea buckthorn is
            // not confined to the shore - it colonises inland river shingle by the
            // same mechanism and for the same reasons.
            eco.distanceToWaterMaxM = 1000f;
            eco.saltTolerance = 0.6f;

            eco.slopeOptimumDeg = 12f;
            eco.slopeToleranceDeg = 28f;
            eco.soilDepthMinM = 0.2f;

            // Fixes its own nitrogen, which is how it lives on bare sand and
            // shingle with nothing in it - and how it makes the ground good enough
            // for the shrubs that shade it out later.
            eco.soilFertilityOptimum = 0.2f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.75f;

            // The trap it sets for itself: it enriches the soil, other plants move
            // in, and it cannot survive their shade.
            eco.shadeTolerance = 0.12f;
            eco.laiContribution = 2.8f;

            // Birds strip the orange berries and drop the seed well inland.
            eco.dispersalLambdaM = 90f;

            eco.matureHeightM = new Vector2(1f, 4f);
            eco.dbhRangeM = new Vector2(0.04f, 0.15f);
            eco.crownAllometryA = 2.3f;
            eco.crownAllometryB = 0.5f;
            eco.rootPlateRadiusM = 1.8f;
            eco.trunkFlexibility = 0.4f;
            eco.selfThinningK = 70000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 150f;

            // Thorny enough to be left alone by most grazers.
            eco.browseTolerance = 0.7f;

            eco.deciduous = true;
            eco.budburstGdd = 150f;
            eco.senescenceDayLengthH = 11.8f;

            // Silver-grey rather than green, from a dense covering of scales.
            eco.springColour = new Color(0.54f, 0.58f, 0.46f, 1f);
            eco.summerColour = new Color(0.50f, 0.55f, 0.44f, 1f);

            // It barely colours in autumn. The orange that makes it recognisable is
            // the berries, not the leaves.
            eco.autumnColour = new Color(0.62f, 0.60f, 0.36f, 1f);
            eco.winterColour = new Color(0.40f, 0.38f, 0.32f, 1f);
            eco.leafOffFraction = 0.9f;
        }
    }
}
