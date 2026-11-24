// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    ZoneSpacing.cs
//  Purpose: Decides WHAT sets the distance between two plants, which is not the
//           same question everywhere (V-21).
//
//  THE MISTAKE THIS FILE EXISTS TO PREVENT. V-14 spaces stems by crown radius,
//  because in a closed forest the thing being competed for is light and a crown is
//  the organ that collects it. Apply that rule to a desert and you get a thinned
//  forest: shrubs at forest spacing, just fewer of them. That is wrong, and it is
//  wrong in a way anyone recognises instantly without being able to say why - real
//  desert shrubs are spaced much FURTHER apart than their crowns require, and far
//  more evenly than chance would put them.
//
//  WHY DESERT SPACING IS REGULAR. Below about half a metre of rain, plants compete
//  for water, and water is collected by roots over an area that has nothing to do
//  with the size of the canopy above it. A creosote bush 1 m across drains 5 m of
//  ground in every direction. Two of them cannot overlap that catchment and both
//  live, so the survivors end up almost evenly spaced - the famous near-regular
//  desert pattern. It is competition producing order, not an author placing a grid.
//
//  THE DERIVATION, AND WHY IT NEEDS NO NEW CONSTANT. A plant must gather as much
//  water as it transpires. The ground area it needs is therefore its demand divided
//  by what falls per unit area, and the aridity index (L-14, AI = P/PET) is exactly
//  that ratio already:
//
//        rootArea = crownArea / AI          radius = sqrt(rootArea / pi)
//
//  Read what that does at the ends. At AI = 1 - supply meets demand - root area
//  equals crown area and the rule becomes V-14's crown rule EXACTLY, with no seam
//  and no blend between two systems. At AI = 0.1, a true desert, root area is ten
//  times crown area and the radius is sqrt(10), about 3.2x the crown radius. Both
//  numbers fall out of one division. There is no desert-spacing multiplier to tune
//  because there is nothing to tune: the climate already said it.
//
//  WETLANDS GO THE OTHER WAY. Reed, sedge and sphagnum are not competing for
//  ground at all - they are clonal, they spread by rhizome, and they form a
//  continuous mat with no gaps to measure. Their separation is zero and the density
//  ceiling from V-13 is what limits them. Spacing a reed bed by crown radius
//  produces isolated tufts on mud, which reads as a failed lawn.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// What the plants at a site are actually competing for. The regime picks the
    /// rule; it is not a style setting.
    /// </summary>
    public enum SpacingRegime
    {
        /// <summary>Light. Separation is crown-driven (V-14). Closed forest, most of the world.</summary>
        CrownLight = 0,

        /// <summary>Water. Separation follows root catchment, which is far wider than the crown.</summary>
        RootWater = 1,

        /// <summary>Nothing. Clonal mats close over completely; density alone limits them.</summary>
        MatContinuous = 2,

        /// <summary>Neither - the ground itself is the limit. Stones, ice, moving scree.</summary>
        Unrooted = 3
    }

    public static class ZoneSpacing
    {
        /// <summary>
        /// Numerical floor on the aridity index. At AI this low the root radius is
        /// already about 4.5 crown radii, which is as sparse as any real vegetated
        /// surface gets - past it the ground is bare and V-24 handles it, so letting
        /// the division run to infinity would only produce a spacing no stem can pass.
        /// </summary>
        public const float AridityFloor = 0.05f;

        /// <summary>
        /// P/PET below which water rather than light is the binding constraint. Set
        /// to the steppe bound, so the regime changes where the vegetation structure
        /// changes: closed canopy above it, open matrix below.
        /// </summary>
        public const float WaterCompetitionBelowAridity = LandformAssemblage.AriditySteppeMax;

        /// <summary>What the plants at this site are competing for.</summary>
        public static SpacingRegime RegimeFor(LandformZone zone, float aridity)
        {
            // Bare by nature. Asked for a separation, these want the floor and nothing
            // else - there is no stand to structure.
            if (LandformAssemblage.IsUnvegetated(zone) || zone == LandformZone.Talus)
                return SpacingRegime.Unrooted;

            // Clonal mats. Standing water or permanently saturated peat, where the
            // limit is how much leaf the light supports, never how much ground each
            // stem holds.
            if (zone == LandformZone.Bog
                || zone == LandformZone.LakeMargin
                || zone == LandformZone.Intertidal
                || zone == LandformZone.Submerged)
                return SpacingRegime.MatContinuous;

            // Water-limited. Taken from the zone where it is structural, and from the
            // aridity index otherwise, so a dry hillside inside a wet region still
            // spaces by water without needing its own zone.
            if (LandformAssemblage.IsWaterCompeted(zone)
                || aridity < WaterCompetitionBelowAridity)
                return SpacingRegime.RootWater;

            return SpacingRegime.CrownLight;
        }

        /// <summary>
        /// Radius of the ground area one plant must drain to meet its own demand.
        /// Equals the crown radius wherever supply meets demand, which is what makes
        /// this continuous with V-14 rather than a second system bolted beside it.
        /// </summary>
        public static float RootZoneRadiusM(float crownRadiusM, float aridity)
        {
            float crown = Mathf.Max(0f, crownRadiusM);
            if (crown <= 0f) return 0f;

            // Above unity the plant has more water than it can use, and a root system
            // does not shrink below the crown it has to supply. Clamping here rather
            // than letting the ratio invert is the difference between "wet country
            // spaces by crowns" and "wet country packs stems tighter than their
            // canopies", which is not a thing that happens.
            float ai = Mathf.Clamp(aridity, AridityFloor, 1f);

            // area scales as 1/AI, so radius scales as 1/sqrt(AI).
            return crown / Mathf.Sqrt(ai);
        }

        /// <summary>
        /// The separation these two stems require, in metres, under the regime that
        /// governs their site. This is the value V-14's crown separation pass uses;
        /// <see cref="StandStructure.RequiredSeparation"/> stays the light-limited
        /// case and this selects between it and the others.
        /// </summary>
        public static float RequiredSeparation(SpacingRegime regime,
                                                float radiusA, float radiusB,
                                                float aridity, float floorM)
        {
            switch (regime)
            {
                case SpacingRegime.MatContinuous:
                    // No separation of its own. The floor still applies, because two
                    // instances of the same mesh in the same square centimetre is a
                    // rendering artefact rather than a dense sward.
                    return Mathf.Max(0f, floorM);

                case SpacingRegime.RootWater:
                {
                    // Root catchments may not overlap - that is the whole competition.
                    // No interlace allowance either: two crowns can share a little
                    // airspace and both live, but two root systems cannot share the
                    // same water twice.
                    float ra = RootZoneRadiusM(radiusA, aridity);
                    float rb = RootZoneRadiusM(radiusB, aridity);

                    return Mathf.Max(ra + rb, Mathf.Max(0f, floorM));
                }

                case SpacingRegime.Unrooted:
                    return Mathf.Max(0f, floorM);

                default:
                    return StandStructure.RequiredSeparation(radiusA, radiusB, floorM);
            }
        }

        /// <summary>
        /// Whether the Reineke self-thinning cap (V-15) applies. It is a rule about
        /// stem diameter in a closed even-aged stand, and applying it to a reed bed
        /// or a desert would cap by the wrong constraint entirely - those are already
        /// limited by water and by light on the mat, which the spacing above did.
        /// </summary>
        public static bool AppliesReinekeCap(SpacingRegime regime)
        {
            return regime == SpacingRegime.CrownLight;
        }

        /// <summary>Inspector and self-test label.</summary>
        public static string Name(SpacingRegime regime)
        {
            switch (regime)
            {
                case SpacingRegime.RootWater: return "Water-competed (root radius)";
                case SpacingRegime.MatContinuous: return "Continuous mat (density only)";
                case SpacingRegime.Unrooted: return "Unrooted ground";
                default: return "Light-competed (crown radius)";
            }
        }
    }
}
