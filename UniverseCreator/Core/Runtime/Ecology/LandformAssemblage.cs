// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    LandformAssemblage.cs
//  Purpose: Names the place a site is in - shore, lake margin, riparian gallery,
//           valley floor, hillside, ridge, treeline, scree, bog - from the V-04
//           environment record alone (V-20).
//
//  WHY A NUMERIC MODEL IS NOT ENOUGH ON ITS OWN. Suitability decides what a site
//  admits, and it is right about that. What it cannot express is what makes a place
//  recognisable. A reed belt, a willow carr and a wet meadow may all be admissible
//  on a lake shore; what makes it read as a lake shore is that they occur in that
//  order, in bands, at those distances from the water. The numbers permit the
//  assemblage. They do not compose it. This file names the composition so the rest
//  of section 6.4 can apply the rule that belongs to it.
//
//  NOTHING HERE IS AUTHORED. Every threshold is read off the environment record,
//  which is itself derived from the heightfield, the water probes and the climate
//  model. There is no zone painting, no zone mask and no place for an author to
//  put a lake margin where the water is not.
//
//  EXACTLY ONE ZONE. Sites near a boundary really are in two zones at once, and
//  the honest way to express that would be a weight per zone. It is not what this
//  returns, because the rules that read it - spacing rule, assemblage roster - have
//  to pick one, and blending two rosters produces a place that is neither. Order of
//  test is therefore the whole design: the tests run from the conditions that
//  override everything else (you are under water, or on bare rock, or under
//  permanent snow) down to the ones that only apply when nothing stronger did.
//
//  WHY THE THRESHOLDS LIVE HERE AND NOT IN EcoConstants. They are not laws. There
//  is no measured constant that says a dune stops at 120 m; there is a measured
//  salt-spray decay length, which is in EcoConstants, and a decision that the band
//  worth calling dune is the part where spray still dominates. Keeping the decision
//  next to the reasoning that justifies it is the point.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// The seventeen places of section 6.4, in the order the classifier tests them.
    /// </summary>
    public enum LandformZone
    {
        /// <summary>Under water. Depth decides kelp from seagrass from coral.</summary>
        Submerged = 0,

        /// <summary>Between the tides. Mangrove, saltmarsh, or bare sorted shingle.</summary>
        Intertidal = 1,

        /// <summary>Snow-covered year round. Lichen on exposed rock and nothing else.</summary>
        Nival = 2,

        /// <summary>Too steep for soil to hold. Crevice plants only, where it is concave.</summary>
        CliffFace = 3,

        /// <summary>Loose stone below a cliff, resting at its angle of repose.</summary>
        Talus = 4,

        /// <summary>Salt-sprayed sand within reach of the shore. The three-band dune gradient.</summary>
        CoastalDune = 5,

        /// <summary>Above the treeline, below permanent snow. Cushion plants and lichen.</summary>
        AlpineTundra = 6,

        /// <summary>The Koerner band. Dwarfed wind-flagged scrub rather than absence.</summary>
        Treeline = 7,

        /// <summary>Standing fresh water's edge. Reed, then carr, then wet meadow.</summary>
        LakeMargin = 8,

        /// <summary>The linear forest following a channel through country too dry for forest.</summary>
        RiparianGallery = 9,

        /// <summary>Waterlogged, cold and flat. Sphagnum, cotton-grass, stunted pine on hummocks.</summary>
        Bog = 10,

        /// <summary>The cool forested belt above the valley and below the treeline.</summary>
        MontaneForest = 11,

        /// <summary>Spaced by water, not by light. Cactus, creosote, ephemerals in the swales.</summary>
        AridPlain = 12,

        /// <summary>Grass matrix with scattered trees where the wetness index allows them.</summary>
        Steppe = 13,

        /// <summary>Deep soil, collecting water, sheltered. The tallest stand in the world.</summary>
        ValleyFloor = 14,

        /// <summary>Convex and exposed. Sparse, wind-flagged, stunted, bare at the top.</summary>
        RidgeAndSpur = 15,

        /// <summary>Everything else, and most of the world. Aspect-differentiated mixed forest.</summary>
        Hillside = 16
    }

    public static class LandformAssemblage
    {
        /// <summary>
        /// Number of <see cref="LandformZone"/> members, Submerged through Hillside.
        /// The single source for anything that sizes an array or a ramp by the zone
        /// count - the classifier here, EcologyNode's Zone Id preview, and the V-52
        /// diagnostics histogram - so adding a zone is one edit, not three.
        /// </summary>
        public const int ZoneCount = 17;

        // ---- classification boundaries --------------------------------------

        /// <summary>m. Above the waterline but still worked by the tide, from the shipped tidal range.</summary>
        public const float IntertidalFreeboardM = EcoConstants.TidalRangeMetres;

        /// <summary>deg. Steeper than this and soil does not stay, whatever the rock.</summary>
        public const float CliffSlopeDeg = 50f;

        /// <summary>m. Soil shallower than this cannot root a tree; with a cliff slope it is bare rock.</summary>
        public const float CliffSoilDepthM = 0.05f;

        /// <summary>0-1. Below a cliff the ground is stone rather than soil.</summary>
        public const float TalusRockinessMin = 0.55f;

        /// <summary>deg. Talus rests within this margin of the repose angle - that is what makes its profile straight.</summary>
        public const float TalusSlopeMarginDeg = 12f;

        /// <summary>m. Salt spray dominates the first decay length from the shore; that band is the dune.</summary>
        public const float DuneReachM = EcoConstants.SaltSprayDecayMetres;

        /// <summary>0-1. Enough spray to exclude anything without salt tolerance.</summary>
        public const float DuneSaltMin = 0.25f;

        /// <summary>
        /// degC. Half-width of the Koerner treeline band. Inside it, stems dwarf rather
        /// than stop. Taken from the shared ecotone width (V-25) rather than authored
        /// here: this file and the vegetation gate both need the number, and a treeline
        /// that is a band in one place and a line in the other is worse than either.
        /// </summary>
        public const float TreelineBandC = EcoConstants.EcotoneTreelineBandC;

        /// <summary>m. A water table this shallow means the ground is wet at root depth.</summary>
        public const float MarginWaterTableM = 0.6f;

        /// <summary>m. Distance from standing water over which the margin's banding plays out.</summary>
        public const float LakeMarginReachM = 25f;

        /// <summary>deg. A lake margin is level ground; a steep bank is a hillside that happens to meet water.</summary>
        public const float MarginMaxSlopeDeg = 8f;

        /// <summary>m. Width of the gallery either side of a channel. Roughly two mature crowns.</summary>
        public const float RiparianReachM = 30f;

        /// <summary>Topographic wetness index above which ground is waterlogged rather than merely damp.</summary>
        public const float BogTwiMin = 8f;

        /// <summary>deg. A bog needs flat ground; water runs off anything else.</summary>
        public const float BogMaxSlopeDeg = 3f;

        /// <summary>degC. Above this a peatland decomposes faster than it accumulates.</summary>
        public const float BogWarmestMonthMaxC = 16f;

        /// <summary>degC. Warmest-month ceiling of the montane belt.</summary>
        public const float MontaneWarmestMonthMaxC = 13f;

        /// <summary>P/PET. Below this, water is the binding constraint and spacing follows roots.</summary>
        public const float AridityDesertMax = 0.2f;

        /// <summary>P/PET. Between the desert bound and this, grass wins and trees are scattered.</summary>
        public const float AriditySteppeMax = 0.5f;

        /// <summary>1/m. More concave than this collects water and soil; the valley-floor signature.</summary>
        public const float ValleyConvexityMax = -0.004f;

        /// <summary>Topographic wetness index a valley floor carries.</summary>
        public const float ValleyTwiMin = 6f;

        /// <summary>m. Soil this deep only accumulates where material has been arriving for a long time.</summary>
        public const float ValleySoilDepthM = 0.8f;

        /// <summary>1/m. More convex than this is a nose or a spur rather than a slope.</summary>
        public const float RidgeConvexityMin = 0.004f;

        /// <summary>0-1. Exposure above which a ridge behaves like a ridge.</summary>
        public const float RidgeExposureMin = 0.6f;

        /// <summary>
        /// The one zone this site is in. Tests run from the conditions that override
        /// everything to the ones that only apply when nothing stronger did.
        /// </summary>
        public static LandformZone Classify(in EnvironmentSample site)
        {
            // 1. Under water. Nothing terrestrial applies, and depth alone separates
            //    kelp from seagrass from coral.
            if (site.submergedDepthM > 0f)
                return LandformZone.Submerged;

            // 2. Between the tides. Salt and the waterline together, because either
            //    alone catches ground that is not intertidal - a salt lake margin has
            //    no tide, and a river mouth has no salt.
            if (site.hasWaterInRange
                && site.nearestWaterKind == WaterBodyKind.Ocean
                && site.distanceToWaterM <= IntertidalFreeboardM
                && site.saltFraction > DuneSaltMin)
                return LandformZone.Intertidal;

            // 3. Permanent snow. The warmest month never thaws, so there is no growing
            //    season to score a species against at all.
            if (site.warmestMonthTempC <= EcoConstants.FreezingPointC)
                return LandformZone.Nival;

            // 4. Rock face. Both conditions are required: a steep slope with soil on it
            //    is a steep hillside and carries trees, which is why mountain forest
            //    exists on ground that looks unclimbable.
            if (site.slopeDeg >= CliffSlopeDeg && site.soilDepthM <= CliffSoilDepthM)
                return LandformZone.CliffFace;

            // 5. Talus. Loose stone standing at very nearly its repose angle - the
            //    straight-profiled cone under every cliff. Tested before the vegetated
            //    zones because nothing roots in moving stone.
            if (site.rockiness >= TalusRockinessMin
                && site.slopeDeg >= EcoConstants.AngleOfReposeDegrees - TalusSlopeMarginDeg
                && site.slopeDeg < CliffSlopeDeg)
                return LandformZone.Talus;

            // 6. Dune and strand. Tested before the thermal belts because salt is a
            //    stronger constraint than altitude: a cold coast is still a coast.
            //    The in-range test matters here: with no water found, distanceToWaterM
            //    reports the search radius, and a radius shorter than the dune reach
            //    would put a beach in the middle of a continent.
            if (site.hasWaterInRange
                && site.distanceToWaterM <= DuneReachM
                && site.saltFraction >= DuneSaltMin
                && site.rockiness < TalusRockinessMin)
                return LandformZone.CoastalDune;

            // 7. Above the treeline. Cold enough that no tree completes a season, warm
            //    enough that the ground thaws.
            if (site.warmestMonthTempC
                < EcoConstants.TreelineWarmestMonthC - TreelineBandC)
                return LandformZone.AlpineTundra;

            // 8. The treeline band itself. One degree either side of Koerner's 6.4, and
            //    the reason a mountain has a visible edge to its forest.
            if (site.warmestMonthTempC
                <= EcoConstants.TreelineWarmestMonthC + TreelineBandC)
                return LandformZone.Treeline;

            // 9. Lake margin. Standing water, level ground, and a water table within
            //    root reach - all three, or a dry terrace above a lake would qualify.
            if (site.hasWaterInRange
                && site.nearestWaterKind == WaterBodyKind.Lake
                && site.distanceToWaterM <= LakeMarginReachM
                && site.slopeDeg <= MarginMaxSlopeDeg
                && site.waterTableDepthM <= MarginWaterTableM)
                return LandformZone.LakeMargin;

            // 10. Riparian gallery. Deliberately not conditioned on the surrounding
            //     climate: a gallery forest in wet country is simply the wettest part of
            //     a wood, and in dry country it is the only wood there is. The same rule
            //     produces both, which is what makes it worth having.
            if (site.hasWaterInRange
                && site.nearestWaterKind == WaterBodyKind.River
                && site.distanceToWaterM <= RiparianReachM)
                return LandformZone.RiparianGallery;

            // 11. Peatland. Flat, wet and cool enough that dead material accumulates
            //     faster than it rots. Warm bogs do not persist.
            if (site.slopeDeg <= BogMaxSlopeDeg
                && site.twi >= BogTwiMin
                && site.warmestMonthTempC <= BogWarmestMonthMaxC
                && site.waterTableDepthM <= MarginWaterTableM)
                return LandformZone.Bog;

            // 12. Montane. The cool belt between the valley and the treeline. Density
            //     falls with altitude from the lapse rate alone; nothing else is needed.
            if (site.warmestMonthTempC <= MontaneWarmestMonthMaxC)
                return LandformZone.MontaneForest;

            // 13/14. Regional moisture. Tested after local water, which is the whole
            //        reason a riparian gallery can exist inside a desert.
            if (site.aridity < AridityDesertMax)
                return LandformZone.AridPlain;

            if (site.aridity < AriditySteppeMax)
                return LandformZone.Steppe;

            // 15. Valley floor. Concave, wet and deep-soiled together. Any one of the
            //     three alone is common; all three at once is a floor.
            if (site.curvature <= ValleyConvexityMax
                && site.twi >= ValleyTwiMin
                && site.soilDepthM >= ValleySoilDepthM)
                return LandformZone.ValleyFloor;

            // 16. Ridge and spur. Convex and exposed, because a convex form in shelter
            //     behaves like a hillside and an exposed hollow behaves like a hollow.
            if (site.curvature >= RidgeConvexityMin
                && site.windExposure >= RidgeExposureMin)
                return LandformZone.RidgeAndSpur;

            // 17. Hillside. Most of the world, and the aspect difference of V-09 is what
            //     keeps it from reading as one thing repeated.
            return LandformZone.Hillside;
        }

        /// <summary>
        /// True where nothing is rooted at all, whatever the species roster says.
        /// Kept as a query rather than folded into suitability so the debris pass can
        /// ask the same question without duplicating the test.
        /// </summary>
        public static bool IsUnvegetated(LandformZone zone)
        {
            return zone == LandformZone.CliffFace
                || zone == LandformZone.Nival;
        }

        /// <summary>
        /// True where competition is for water rather than for light, so spacing
        /// follows root-zone radius instead of crown radius (V-21).
        /// </summary>
        public static bool IsWaterCompeted(LandformZone zone)
        {
            return zone == LandformZone.AridPlain
                || zone == LandformZone.Steppe;
        }

        /// <summary>Inspector and self-test label.</summary>
        public static string Name(LandformZone zone)
        {
            switch (zone)
            {
                case LandformZone.Submerged: return "Submerged / benthic";
                case LandformZone.Intertidal: return "Intertidal";
                case LandformZone.Nival: return "Nival / permanent snow";
                case LandformZone.CliffFace: return "Cliff and rock face";
                case LandformZone.Talus: return "Talus / scree";
                case LandformZone.CoastalDune: return "Coastal dune / strand";
                case LandformZone.AlpineTundra: return "Alpine tundra";
                case LandformZone.Treeline: return "Treeline / krummholz";
                case LandformZone.LakeMargin: return "Lake margin";
                case LandformZone.RiparianGallery: return "Riparian gallery";
                case LandformZone.Bog: return "Bog / peatland";
                case LandformZone.MontaneForest: return "Montane forest";
                case LandformZone.AridPlain: return "Arid plain / desert";
                case LandformZone.Steppe: return "Steppe / savanna";
                case LandformZone.ValleyFloor: return "Valley floor";
                case LandformZone.RidgeAndSpur: return "Ridge and spur";
                default: return "Hillside";
            }
        }
    }
}
