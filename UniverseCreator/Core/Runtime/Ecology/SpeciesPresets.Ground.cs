// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    SpeciesPresets.Ground.cs
//  Purpose: The ground layer. What covers the space between the trees, and what
//           the player's camera is closest to for most of a game.
//
//  ONE NUMBER DECIDES WHAT A GRASSLAND LOOKS LIKE, and it is the self-thinning
//  constant rather than anything about the plant itself:
//
//    meadow fescue     800,000/ha   a closed sward, no ground visible
//    prairie tallgrass 200,000/ha   tall, dense, but with structure inside it
//    bunchgrass        120,000/ha   discrete tussocks with bare earth between
//
//  That last figure is the whole visual signature of steppe. A steppe rendered as a
//  continuous lawn is wrong in a way no texture can fix, and it is wrong by this one
//  value. Nothing in the placement pass needs to know the difference.
//
//  AN UPPER ARIDITY BOUND IS WHAT KEEPS DRY GRASSLAND DRY. Bunchgrass and tallgrass
//  both state one, and it is not because they would die in a wet climate - they
//  would be shaded out by something taller. This is the only field here that can
//  express "outcompeted rather than killed", so it carries that meaning, and the
//  alternative was a steppe grass carpeting a rainforest floor.
//
//  BRACKEN AND HEATHER BOTH WIN BY BEING INEDIBLE, on the same overgrazed hillside,
//  and their browse tolerances say so. Bracken is toxic and takes ground that sheep
//  have stripped of everything palatable; heather is merely tough and loses to grass
//  when the ground is fertilised. Both are real management outcomes, and both fall
//  out of two numbers rather than a rule.
//
//  THERE IS NO SOIL pH FIELD, which matters more in this group than anywhere else -
//  heather, bracken and the moss carpet are all acid-soil plants and that is central
//  to where they grow. Fertility stands in for it: all three take a low optimum with
//  a narrow tolerance, so rich ground rejects them. The effect is right and the
//  reason is approximate, and that is worth knowing before someone tunes it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    public static partial class SpeciesPresets
    {
        private static bool ApplyGround(EcoEnvelope eco, SpeciesPreset preset)
        {
            switch (preset)
            {
                case SpeciesPreset.MeadowFescue: MeadowFescue(eco); return true;
                case SpeciesPreset.Bunchgrass: Bunchgrass(eco); return true;
                case SpeciesPreset.PrairieTallgrass: PrairieTallgrass(eco); return true;
                case SpeciesPreset.BrackenFern: BrackenFern(eco); return true;
                case SpeciesPreset.Heather: Heather(eco); return true;
                case SpeciesPreset.MossMat: MossMat(eco); return true;
                default: return false;
            }
        }

        private static void MeadowFescue(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Meadow Fescue", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 13f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -35f;
            eco.tempLethalMaxC = 38f;
            eco.warmestMonthMinC = 8f;
            eco.frostFreeFractionMin = 0.4f;
            eco.gddMinRequired = 900f;

            eco.moistureOptimum = 0.55f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 800f;
            eco.precipToleranceMm = 500f;
            eco.aridityMin = 0.5f;
            eco.twiOptimum = 6f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.15f;
            eco.droughtTolerance = 0.45f;

            eco.slopeOptimumDeg = 5f;
            eco.slopeToleranceDeg = 25f;
            eco.soilDepthMinM = 0.15f;

            // Fertile ground, and this is the separation from bunchgrass: the same
            // hillside, one on the rich lower slope and one on the thin upper.
            eco.soilFertilityOptimum = 0.6f;
            eco.soilFertilityTolerance = 0.3f;
            eco.rockinessMax = 0.35f;

            // Enough to persist under scattered trees, which is what makes wood
            // pasture read as pasture rather than as forest floor.
            eco.shadeTolerance = 0.35f;
            eco.laiContribution = 2.2f;
            eco.dispersalLambdaM = 30f;

            eco.matureHeightM = new Vector2(0.1f, 0.7f);
            eco.dbhRangeM = new Vector2(0.05f, 0.2f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 0.2f;
            eco.trunkFlexibility = 0.8f;

            // A closed sward. The highest figure among the grasses, and the reason
            // no bare ground shows through a meadow.
            eco.selfThinningK = 800000f;
            eco.windExposureMax = 0.95f;
            eco.snowLoadMaxKgPerM2 = 400f;

            // The highest browse tolerance of any plant in the table, and it is the
            // single fact that makes grassland grassland: the growing point sits at
            // the base of the plant, below the height a grazer can reach. Grasses
            // and grazers made each other, and pasture is the result.
            eco.browseTolerance = 0.9f;

            eco.springColour = new Color(0.44f, 0.62f, 0.30f, 1f);
            eco.summerColour = new Color(0.38f, 0.56f, 0.28f, 1f);
            eco.autumnColour = new Color(0.52f, 0.52f, 0.30f, 1f);
            eco.winterColour = new Color(0.48f, 0.46f, 0.32f, 1f);
            eco.seasonalHeightScale = new Vector2(0.4f, 1f);
        }

        private static void Bunchgrass(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Bunchgrass", LifeForm.Graminoid);

            eco.successionStage = SuccessionStage.Mid;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 12f;
            eco.tempToleranceC = 13f;
            eco.tempLethalMinC = -40f;
            eco.tempLethalMaxC = 44f;
            eco.warmestMonthMinC = 12f;
            eco.gddMinRequired = 1000f;

            eco.moistureOptimum = 0.3f;
            eco.moistureTolerance = 0.2f;
            eco.precipOptimumMm = 350f;
            eco.precipToleranceMm = 250f;
            eco.aridityMin = 0.2f;

            // The ceiling matters more than the floor. Bunchgrass does not die in a
            // wet climate - it is simply overgrown by taller grass and then by trees,
            // and this is the only field that can say that.
            eco.aridityMax = 0.75f;

            eco.twiOptimum = 4f;
            eco.twiTolerance = 3f;
            eco.floodTolerance = 0.05f;
            eco.droughtTolerance = 0.85f;

            eco.slopeOptimumDeg = 8f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.1f;
            eco.soilFertilityOptimum = 0.25f;
            eco.soilFertilityTolerance = 0.3f;
            eco.rockinessMax = 0.6f;

            // Full sun. There is no shade on a steppe and no tolerance for it here.
            eco.shadeTolerance = 0.1f;
            eco.laiContribution = 1.2f;
            eco.dispersalLambdaM = 40f;

            eco.matureHeightM = new Vector2(0.15f, 0.8f);

            // Tussock width. A bunchgrass is a discrete clump with a hard edge, not
            // a spreading turf, and this is the dimension the spacing maths needs.
            eco.dbhRangeM = new Vector2(0.1f, 0.4f);
            eco.crownAllometryA = 1.1f;
            eco.crownAllometryB = 0.4f;

            // Deep fibrous roots for the size of the plant - most of a steppe grass
            // is underground.
            eco.rootPlateRadiusM = 0.4f;
            eco.trunkFlexibility = 0.7f;

            // Deliberately low, and the most important number in this file: a sixth
            // of the meadow figure, so tussocks sit apart with bare earth between
            // them. That gap is what a steppe actually looks like.
            eco.selfThinningK = 120000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 300f;

            // Grazing-adapted, but less forgiving than pasture grass. Overgrazed
            // steppe does not recover on a human timescale, and a tussock killed
            // outright leaves a gap nothing closes.
            eco.browseTolerance = 0.6f;

            // Glaucous grey-green rather than fresh green. Steppe grass is never the
            // colour of a lawn, even in spring.
            eco.springColour = new Color(0.58f, 0.60f, 0.38f, 1f);
            eco.summerColour = new Color(0.55f, 0.52f, 0.34f, 1f);
            eco.autumnColour = new Color(0.66f, 0.58f, 0.38f, 1f);
            eco.winterColour = new Color(0.68f, 0.60f, 0.40f, 1f);

            // Stands as dead straw through winter rather than collapsing.
            eco.seasonalHeightScale = new Vector2(0.7f, 1f);
        }

        private static void PrairieTallgrass(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Prairie Tallgrass", LifeForm.Graminoid);

            // On ground that burns often enough, this is the end state. Stop the fire
            // and trees take it within thirty years.
            eco.successionStage = SuccessionStage.Climax;

            // Genuinely the canopy. Three metres of grass with nothing above it, and
            // filing it as ground cover would let short grasses grow in full sun
            // beneath a closed stand. Beer-Lambert does the shading correctly once
            // the layer is stated honestly.
            eco.canopyLayer = CanopyLayer.Canopy;

            // Fire is not a hazard to this plant, it is the maintenance regime. The
            // growing points and the whole root mass are below ground when it burns.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 16f;
            eco.tempToleranceC = 11f;
            eco.tempLethalMinC = -40f;
            eco.tempLethalMaxC = 45f;

            // A hot summer is a requirement, not a preference. This is a C4 grass and
            // its photosynthesis barely functions in a cool one, which is why
            // tallgrass prairie has a northern edge.
            eco.warmestMonthMinC = 16f;
            eco.gddMinRequired = 2200f;

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 350f;
            eco.aridityMin = 0.55f;

            // Bounded above for the same reason as bunchgrass, at a wetter figure.
            // Beyond this, forest wins.
            eco.aridityMax = 1.2f;

            eco.twiOptimum = 6f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.2f;

            // Two metres of root. It goes to water that a shallow-rooted grass on the
            // same ground cannot reach, which is why prairie stays green through a
            // drought that browns a lawn.
            eco.droughtTolerance = 0.7f;

            eco.slopeOptimumDeg = 3f;
            eco.slopeToleranceDeg = 18f;

            // The deepest soil requirement of any grass here. A tallgrass prairie
            // builds a metre of black mollisol and then needs it.
            eco.soilDepthMinM = 0.6f;
            eco.soilFertilityOptimum = 0.7f;
            eco.soilFertilityTolerance = 0.3f;
            eco.rockinessMax = 0.2f;

            eco.shadeTolerance = 0.15f;

            // The highest leaf area of any grass in the table. A closed stand in
            // August is dark at ground level.
            eco.laiContribution = 3.5f;
            eco.dispersalLambdaM = 50f;

            eco.matureHeightM = new Vector2(1f, 3f);
            eco.dbhRangeM = new Vector2(0.1f, 0.5f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.45f;

            // The sod that broke steel ploughs. Wide and dense rather than deep in
            // the sense a tree is deep.
            eco.rootPlateRadiusM = 1.5f;
            eco.trunkFlexibility = 0.8f;
            eco.selfThinningK = 200000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 350f;

            // Made by and for bison. Heavy grazing is the condition it evolved in.
            eco.browseTolerance = 0.75f;

            eco.springColour = new Color(0.40f, 0.58f, 0.30f, 1f);
            eco.summerColour = new Color(0.36f, 0.52f, 0.28f, 1f);

            // Big bluestem turns copper. It is one of the most distinctive seasonal
            // colours on the continent and nothing else in this table is near it.
            eco.autumnColour = new Color(0.72f, 0.42f, 0.26f, 1f);
            eco.winterColour = new Color(0.66f, 0.52f, 0.36f, 1f);
            eco.seasonalHeightScale = new Vector2(0.35f, 1f);
        }

        private static void BrackenFern(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Bracken Fern", LifeForm.Fern);

            eco.successionStage = SuccessionStage.Early;
            eco.canopyLayer = CanopyLayer.Understory;

            // The rhizome is half a metre down and untouched by anything at the
            // surface.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 13f;
            eco.tempToleranceC = 11f;

            // The rhizome takes hard frost; the frond takes none at all. The pair of
            // numbers is honest because only the rhizome overwinters.
            eco.tempLethalMinC = -30f;
            eco.tempLethalMaxC = 40f;
            eco.warmestMonthMinC = 10f;
            eco.frostFreeFractionMin = 0.35f;
            eco.gddMinRequired = 1100f;

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 1100f;
            eco.precipToleranceMm = 600f;
            eco.aridityMin = 0.65f;
            eco.twiOptimum = 6f;
            eco.twiTolerance = 3.5f;

            // Unlike almost every other fern, this one will not have wet feet. It
            // wants a humid climate and a drained soil, and the pairing is what puts
            // bracken on the slope and not in the flush at the bottom of it.
            eco.floodTolerance = 0.05f;
            eco.droughtTolerance = 0.5f;

            eco.slopeOptimumDeg = 10f;
            eco.slopeToleranceDeg = 28f;
            eco.soilDepthMinM = 0.4f;

            // Acid and poor. Standing in for a pH the envelope does not carry.
            eco.soilFertilityOptimum = 0.35f;
            eco.soilFertilityTolerance = 0.35f;
            eco.rockinessMax = 0.3f;

            eco.shadeTolerance = 0.6f;

            // Very high for something a metre tall, and it is the mechanism behind
            // bracken monoculture: a closed stand puts almost nothing through to the
            // ground, so nothing germinates under it.
            eco.laiContribution = 3.8f;

            // Spores travel for kilometres and almost never establish. What actually
            // spreads bracken is the rhizome, creeping outward a metre or so a year,
            // so the effective distance is short and the patches have hard edges.
            eco.dispersalLambdaM = 60f;

            eco.matureHeightM = new Vector2(0.4f, 2f);

            // Clone patch width. A single bracken clone can be centuries old and
            // tens of metres across; this is the practical spacing unit.
            eco.dbhRangeM = new Vector2(0.2f, 1f);
            eco.crownAllometryA = 1.2f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 1f;
            eco.trunkFlexibility = 0.85f;
            eco.selfThinningK = 400000f;
            eco.windExposureMax = 0.8f;
            eco.snowLoadMaxKgPerM2 = 400f;

            // Toxic and carcinogenic, and effectively nothing eats it. This is why
            // bracken takes over a hillside that sheep have grazed bare of
            // everything else - it is not a better competitor, it is the only thing
            // left standing.
            eco.browseTolerance = 0.85f;

            eco.deciduous = true;

            // The latest budburst in the table by a wide margin. A bracken frond has
            // no frost protection whatever, so it waits weeks past the grass and
            // then unrolls all at once.
            eco.budburstGdd = 400f;
            eco.senescenceDayLengthH = 13f;
            eco.leafOffFraction = 1f;

            eco.springColour = new Color(0.42f, 0.58f, 0.26f, 1f);
            eco.summerColour = new Color(0.30f, 0.46f, 0.22f, 1f);

            // Rust-brown, and it stands dead all winter, which is what makes an
            // abandoned hillside read as abandoned.
            eco.autumnColour = new Color(0.62f, 0.40f, 0.22f, 1f);
            eco.winterColour = new Color(0.52f, 0.36f, 0.22f, 1f);
            eco.seasonalHeightScale = new Vector2(0.15f, 1f);
            eco.wetDarkening = 0.45f;
        }

        private static void Heather(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Heather", LifeForm.Shrub);

            eco.successionStage = SuccessionStage.Mid;

            // The layer above the moss carpet and below nothing at all. Stating it
            // is what lets the moss beneath it live in shade rather than in full sun.
            eco.canopyLayer = CanopyLayer.Understory;

            // Young heather resprouts from the base after a burn and old heather does
            // not, which is the whole basis of rotational muirburn.
            eco.fireResponse = FireResponse.Resprouter;

            eco.tempOptimumC = 10f;
            eco.tempToleranceC = 11f;
            eco.tempLethalMinC = -35f;
            eco.tempLethalMaxC = 36f;
            eco.warmestMonthMinC = 7f;
            eco.frostFreeFractionMin = 0.3f;
            eco.gddMinRequired = 800f;

            eco.moistureOptimum = 0.5f;
            eco.moistureTolerance = 0.22f;
            eco.precipOptimumMm = 1000f;
            eco.precipToleranceMm = 500f;

            // A high floor, because heather moorland is an oceanic thing. Continental
            // interiors at the same latitude and temperature have none of it.
            eco.aridityMin = 0.8f;

            eco.twiOptimum = 6f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.1f;
            eco.droughtTolerance = 0.55f;

            eco.slopeOptimumDeg = 10f;
            eco.slopeToleranceDeg = 30f;
            eco.soilDepthMinM = 0.15f;

            // Very low, and narrow on purpose. Fertilising a moor converts heather to
            // grass within a few seasons - a documented management outcome - and a
            // narrow tolerance is what reproduces it. Acid peat, again standing in
            // for a pH field that does not exist.
            eco.soilFertilityOptimum = 0.12f;
            eco.soilFertilityTolerance = 0.25f;
            eco.rockinessMax = 0.5f;

            // Needs the light. Heather under a closed canopy dies, which is why
            // moorland exists only where trees do not, and why abandoned moor turns
            // to birch scrub and then loses its heather.
            eco.shadeTolerance = 0.15f;
            eco.laiContribution = 2.4f;

            // Dust-fine seed carried on the wind.
            eco.dispersalLambdaM = 70f;

            eco.matureHeightM = new Vector2(0.15f, 0.7f);

            // Bush spread rather than a stem diameter.
            eco.dbhRangeM = new Vector2(0.1f, 0.6f);
            eco.crownAllometryA = 1.3f;
            eco.crownAllometryB = 0.45f;
            eco.rootPlateRadiusM = 0.3f;

            // Woody and stiff. Heather does not sway - it is the ground texture that
            // moves in the wind on a moor, not the plants.
            eco.trunkFlexibility = 0.35f;
            eco.selfThinningK = 300000f;
            eco.windExposureMax = 1f;
            eco.snowLoadMaxKgPerM2 = 500f;
            eco.browseTolerance = 0.5f;

            // Evergreen. The leaves are tiny rolled needles, which is a drought
            // adaptation on ground that is never short of rain - the water in peat is
            // acidic and hard to take up, so the plant is built as if it were dry.
            eco.deciduous = false;

            eco.springColour = new Color(0.30f, 0.36f, 0.24f, 1f);
            eco.summerColour = new Color(0.28f, 0.34f, 0.22f, 1f);

            // Not senescence. This is the August flowering flush, and on a moor in
            // late summer it is genuinely the colour of the ground for a hundred
            // square kilometres, so the autumn slot is the honest place to put it.
            eco.autumnColour = new Color(0.46f, 0.30f, 0.42f, 1f);
            eco.winterColour = new Color(0.32f, 0.28f, 0.24f, 1f);
        }

        private static void MossMat(EcoEnvelope eco)
        {
            ResetBiology(eco);
            Stamp(eco, "Moss Mat", LifeForm.Moss);

            eco.successionStage = SuccessionStage.Climax;
            eco.canopyLayer = CanopyLayer.GroundCover;
            eco.fireResponse = FireResponse.Killed;

            eco.tempOptimumC = 9f;
            eco.tempToleranceC = 12f;
            eco.tempLethalMinC = -50f;

            // Heat kills by drying, as with the bog moss, and the ceiling is low for
            // the same reason.
            eco.tempLethalMaxC = 34f;
            eco.warmestMonthMinC = 4f;
            eco.gddMinRequired = 400f;

            eco.moistureOptimum = 0.6f;
            eco.moistureTolerance = 0.25f;
            eco.precipOptimumMm = 900f;
            eco.precipToleranceMm = 500f;

            // THE SEPARATION FROM SPHAGNUM, which is the other moss in this table and
            // occupies completely different ground. That one demands an aridity above
            // 1.2 and a topographic wetness of 9 - a waterlogged bog. This one wants
            // a humid forest floor, which is a much lower bar on both counts, and
            // will not grow in standing water at all.
            eco.aridityMin = 0.7f;
            eco.twiOptimum = 6.5f;
            eco.twiTolerance = 3.5f;
            eco.floodTolerance = 0.05f;
            eco.droughtTolerance = 0.15f;

            eco.slopeOptimumDeg = 8f;
            eco.slopeToleranceDeg = 30f;

            // Almost none. A feather moss grows on needle litter, on rotting logs and
            // straight onto rock, so what is underneath barely matters.
            eco.soilDepthMinM = 0.02f;
            eco.soilFertilityOptimum = 0.3f;
            eco.soilFertilityTolerance = 0.4f;
            eco.rockinessMax = 0.7f;

            // The second highest in the table, behind only the understory palm. This
            // is the number that puts a moss carpet on a dense conifer floor where
            // grass cannot survive - the reason a spruce forest floor is green and
            // soft rather than bare.
            eco.shadeTolerance = 0.9f;
            eco.laiContribution = 1f;

            // Spores are light and go a long way.
            eco.dispersalLambdaM = 100f;

            eco.matureHeightM = new Vector2(0.02f, 0.12f);

            // Carpet patch width.
            eco.dbhRangeM = new Vector2(0.2f, 1.5f);
            eco.crownAllometryA = 1f;
            eco.crownAllometryB = 0.4f;
            eco.rootPlateRadiusM = 0.03f;

            // A moss carpet does not move in wind.
            eco.trunkFlexibility = 0.05f;

            // Continuous cover, second only to the rock crust.
            eco.selfThinningK = 900000f;

            // Low, and it is the one real constraint on this species: a moss needs
            // still, humid air at ground level. Exposed ground dries the carpet out
            // within days, which is why the moss stops at the forest edge.
            eco.windExposureMax = 0.7f;
            eco.snowLoadMaxKgPerM2 = 600f;
            eco.browseTolerance = 0.65f;

            eco.springColour = new Color(0.36f, 0.50f, 0.30f, 1f);
            eco.summerColour = new Color(0.34f, 0.46f, 0.28f, 1f);
            eco.autumnColour = new Color(0.34f, 0.45f, 0.28f, 1f);
            eco.winterColour = new Color(0.32f, 0.42f, 0.28f, 1f);

            // Dry moss is pale and grey; wet moss is vivid. The swing is large and it
            // is the fastest visible response to rain of anything in the table.
            eco.wetDarkening = 0.55f;
        }
    }
}
