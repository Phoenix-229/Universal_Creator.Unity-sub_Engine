// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Baseline.cs
//  Purpose: The neutral organism every preset starts from.
//
//  WHY THIS EXISTS, and it is not tidiness.
//
//  Applying a second preset over a first has to leave nothing of the first behind.
//  If Mangrove sets a tidal zone and Spruce does not mention one, then an author
//  who applies Mangrove and then changes their mind to Spruce would get a spruce
//  that still insists on living in the intertidal - and would have no way of
//  seeing why. Resetting first makes every preset a complete statement rather than
//  a patch on whatever happened to be there.
//
//  The baseline is a generalist with no opinions: it survives an ordinary
//  temperate range, wants middling everything, and states no requirement it does
//  not have. So a field a preset does not mention means "this organism has no
//  preference about that", which is the truth for most fields of most species and
//  is exactly how a real trait database records a missing measurement.
//
//  It never touches the author's machine settings - instancing, colliders, LOD,
//  billboard distance, shadow distance, per-chunk budget - or the enabled flag.
//  Those are not facts about a plant.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        // A temperate generalist's range: comfortable around 12 degrees, alive
        // between roughly -40 and 50, which is the span the envelope's own
        // defaults describe and is wide enough to constrain nothing.
        private const float NeutralTempOptimumC = 12f;
        private const float NeutralTempToleranceC = 12f;

        private static void ResetBiology(EcoEnvelope eco)
        {
            if (eco == null) return;

            // ---- identity ---------------------------------------------------

            eco.displayName = "";
            eco.lifeForm = LifeForm.Tree;
            eco.weight = 1f;
            eco.successionStage = SuccessionStage.Mid;

            // ---- thermal ----------------------------------------------------

            eco.tempOptimumC = NeutralTempOptimumC;
            eco.tempToleranceC = NeutralTempToleranceC;
            eco.tempLethalMinC = -40f;
            eco.tempLethalMaxC = 50f;

            // Zero, not Korner's 6.4: the treeline gate belongs to the species
            // that actually has one, and a grass does not.
            eco.warmestMonthMinC = 0f;
            eco.frostFreeFractionMin = 0f;

            eco.gddBaseC = EcoConstants.GddBaseTemperateC;
            eco.gddMinRequired = 0f;

            // ---- water ------------------------------------------------------

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.3f;
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 600f;
            eco.aridityMin = 0f;
            eco.aridityMax = 100f;
            eco.twiOptimum = 6f;
            eco.twiTolerance = 4f;
            eco.waterTableMaxDepthM = -1f;
            eco.floodTolerance = 0.05f;
            eco.droughtTolerance = 0.3f;

            // ---- shore and salt ---------------------------------------------

            eco.distanceToWaterMinM = 0f;
            eco.distanceToWaterMaxM = -1f;
            eco.saltTolerance = 0.05f;
            eco.tidalZone = TidalZone.None;
            eco.submergedDepthRangeM = Vector2.zero;

            // ---- ground -----------------------------------------------------

            // Left relative on purpose. An absolute altitude band is an author's
            // override; where a species stops is a thermal question, and the
            // thermal fields above are what answer it.
            eco.useAbsoluteAltitude = false;
            eco.minAltitudeM = 0f;
            eco.maxAltitudeM = 3000f;

            eco.slopeOptimumDeg = 8f;
            eco.slopeToleranceDeg = 25f;
            eco.aspectPreferenceDeg = -1f;
            eco.aspectStrength = 0.3f;
            eco.curvaturePreference = 0f;
            eco.soilDepthMinM = 0.1f;
            eco.soilFertilityOptimum = 0.5f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.7f;

            // ---- light and neighbours ---------------------------------------

            eco.shadeTolerance = 0.35f;
            eco.canopyLayer = CanopyLayer.Canopy;
            eco.laiContribution = 2.5f;
            eco.dispersalLambdaM = 40f;
            eco.allelopathyRadiusM = 0f;

            // ---- form -------------------------------------------------------

            eco.matureHeightM = new Vector2(12f, 22f);
            eco.dbhRangeM = new Vector2(0.25f, 0.6f);
            eco.crownAllometryA = EcoConstants.CrownAllometryDefaultA;
            eco.crownAllometryB = EcoConstants.CrownAllometryDefaultB;
            eco.rootPlateRadiusM = 2f;
            eco.trunkFlexibility = 0.35f;
            eco.selfThinningK = EcoConstants.ReinekeDefaultK;

            // ---- disturbance ------------------------------------------------

            eco.fireResponse = FireResponse.Killed;
            eco.windExposureMax = 0.85f;
            eco.krummholzAboveExposure = false;
            eco.krummholzHeightScale = 0.3f;
            eco.snowLoadMaxKgPerM2 = 200f;
            eco.avalancheTolerance = 0.2f;
            eco.browseTolerance = 0.5f;

            // ---- season -----------------------------------------------------

            eco.deciduous = false;
            eco.budburstGdd = 200f;
            eco.senescenceDayLengthH = 11.5f;
            eco.springColour = new Color(0.45f, 0.65f, 0.25f, 1f);
            eco.summerColour = new Color(0.25f, 0.45f, 0.16f, 1f);
            eco.autumnColour = new Color(0.70f, 0.48f, 0.13f, 1f);
            eco.winterColour = new Color(0.36f, 0.31f, 0.24f, 1f);
            eco.leafOffFraction = 0f;
            eco.snowCanopyAccumulation = 0.5f;
            eco.wetDarkening = 0.4f;
            eco.seasonalHeightScale = Vector2.one;
        }
    }
}
