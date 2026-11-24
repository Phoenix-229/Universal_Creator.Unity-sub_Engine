// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    Allometry.cs
//  Purpose: Turns a species description into a stem with real dimensions - trunk
//           diameter, height, crown radius - so that spacing, self-thinning and
//           scale all read the same numbers instead of three separate guesses.
//
//  WHY A STEM AND NOT A SCALE FACTOR. Modules 1 and 2 gave every instance a
//  uniform random scale between two authored multipliers. That produces variety
//  but not a forest: the 1.2x tree is not taller because it grew better, it is
//  taller because a hash said so, and nothing else in the world knows how big it
//  is. Spacing could not follow crown width because no crown width existed.
//
//  So the order is inverted here. The stem's diameter is drawn first, height and
//  crown follow from it by measured allometry, and the render scale is derived
//  from the height in metres at the very end. Every later question - how far is
//  the next tree, how many stems does this hectare allow, how much light reaches
//  the ground - is answered from the same stem.
//
//  DIAMETER IS DRAWN, EVERYTHING ELSE IS COMPUTED. One hash per instance decides
//  where in its species' diameter range this individual sits. That single number
//  then propagates: height by the height-diameter curve, crown by L-19, light
//  interception by crown area. A second hash for height would let a stem be
//  simultaneously thick and short, which does not happen to trees.
//
//  SUITABILITY SHRINKS THE STEM, IT DOES NOT ONLY THIN THE STAND. A pine at the
//  edge of what it can tolerate is a smaller pine. Modelling marginal ground as
//  "fewer full-size trees" alone gives a thinned plantation; modelling it as
//  "fewer, and those smaller" gives a treeline. Both happen, and V-13 already
//  does the thinning, so this file does the shrinking.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// One individual's dimensions, in metres. Produced once per instance and
    /// read by spacing, self-thinning, canopy light and render scale.
    /// </summary>
    public struct StemForm
    {
        /// <summary>Trunk diameter at breast height, metres.</summary>
        public float dbhM;

        /// <summary>Height from ground to leader, metres.</summary>
        public float heightM;

        /// <summary>Crown radius, metres. L-19 from the diameter.</summary>
        public float crownRadiusM;

        /// <summary>Trunk flexibility passed through to the wind bend.</summary>
        public float bendFactor;

        /// <summary>
        /// Where this individual sits in its species' size range, 0-1. Kept
        /// because self-thinning drops the smallest stems and needs to rank them
        /// without recomputing the curve.
        /// </summary>
        public float vigour;

        /// <summary>True when wind exposure has dwarfed this stem into krummholz.</summary>
        public bool krummholz;
    }

    public static class Allometry
    {
        // A diameter of exactly zero would divide by zero in the Reineke cap and
        // give an infinite crown in L-19. A centimetre is a seedling, which is a
        // real thing for a stem to be.
        private const float MinDbhM = 0.01f;

        private const float MinHeightM = 0.05f;

        /// <summary>
        /// The stem an individual of this species grows into at this site.
        /// </summary>
        /// <param name="hash01">
        /// The individual's own 0-1 draw. Must come from a position hash, never a
        /// sequential random, or the same tree changes size on rebuild (C-06).
        /// </param>
        public static StemForm Resolve(EcoEnvelope eco, float hash01, float suitability,
                                       float windExposure)
        {
            StemForm form = new StemForm();

            if (eco == null)
            {
                form.dbhM = MinDbhM;
                form.heightM = MinHeightM;
                form.crownRadiusM = EcoMath.CrownRadiusM(MinDbhM);
                form.bendFactor = 0f;
                form.vigour = 0f;
                return form;
            }

            float dMin = Mathf.Max(MinDbhM, Mathf.Min(eco.dbhRangeM.x, eco.dbhRangeM.y));
            float dMax = Mathf.Max(dMin, Mathf.Max(eco.dbhRangeM.x, eco.dbhRangeM.y));

            // The draw is squeezed toward the small end of the range by how poorly
            // the site suits the species. A site at suitability 1 can grow anything
            // in the range; a site at 0.3 tops out at 30 % of the way up it. This is
            // the whole of "a marginal tree is a small tree".
            float ceiling = Mathf.Clamp01(suitability);
            float draw = Mathf.Clamp01(hash01) * ceiling;

            form.vigour = draw;
            form.dbhM = Mathf.Lerp(dMin, dMax, draw);

            float hMin = Mathf.Max(MinHeightM, Mathf.Min(eco.matureHeightM.x, eco.matureHeightM.y));
            float hMax = Mathf.Max(hMin, Mathf.Max(eco.matureHeightM.x, eco.matureHeightM.y));

            // Height saturates before girth does - the exponent is below one - so a
            // stem two thirds of the way up the diameter range is already most of the
            // way up the height range. Interpolating both linearly would give tall
            // thin poles at mid size and stout towers at the top, and neither is a
            // shape a tree takes.
            float shaped = Mathf.Pow(draw, EcoConstants.HeightDiameterExponent);
            form.heightM = Mathf.Lerp(hMin, hMax, shaped);

            form.crownRadiusM = EcoMath.CrownRadiusM(
                form.dbhM, eco.crownAllometryA, eco.crownAllometryB);

            form.bendFactor = Mathf.Clamp01(eco.trunkFlexibility);

            float kHeight, kWidth, kBend;
            if (Krummholz(eco, windExposure, out kHeight, out kWidth, out kBend))
            {
                form.heightM *= kHeight;
                form.crownRadiusM *= kWidth;
                form.bendFactor *= kBend;
                form.krummholz = true;
            }

            return form;
        }

        /// <summary>
        /// V-39. The dwarfing a wind-flagged stem takes above its exposure limit.
        /// Returns false and three multipliers of 1 where it does not apply.
        ///
        /// Krummholz. A species flagged for it does not vanish where wind exceeds
        /// its limit - it lies down and stays. That band is what makes a mountain
        /// read as a mountain (L-22), and the alternative, a hard absence, gives
        /// the bare ring above a forest that looks like a masking bug.
        ///
        /// Split out of <see cref="Resolve"/> rather than restated in the scatter
        /// path: the placement pass has to dwarf an instance without knowing the
        /// prefab's native height, and this rule must exist in exactly one place.
        /// </summary>
        public static bool Krummholz(EcoEnvelope eco, float windExposure,
                                     out float heightMul, out float widthMul,
                                     out float bendMul)
        {
            heightMul = 1f;
            widthMul = 1f;
            bendMul = 1f;

            if (eco == null) return false;
            if (!eco.krummholzAboveExposure) return false;
            if (windExposure <= eco.windExposureMax) return false;

            float dwarf = Mathf.Clamp(eco.krummholzHeightScale, 0.05f, 1f);

            heightMul = dwarf;

            // The crown spreads rather than shrinking with the height: a wind-
            // flagged stem is wider than it is tall, which is the shape that
            // reads as krummholz instead of as a small ordinary tree.
            widthMul = Mathf.Lerp(1.4f, 1f, dwarf);

            // A stem that has already been bent permanently does not bend again
            // in the wind. Flexibility is spent.
            bendMul = dwarf;

            return true;
        }

        /// <summary>
        /// Render scale for a prototype whose prefab measures <paramref name="nativeHeightM"/>
        /// tall. Returns 1 when the native size is unknown, which leaves the authored
        /// multiplier in charge rather than collapsing every tree to nothing.
        /// </summary>
        public static float HeightScaleFor(float targetHeightM, float nativeHeightM)
        {
            if (nativeHeightM <= 1e-3f) return 1f;
            if (targetHeightM <= 1e-3f) return 1f;

            return targetHeightM / nativeHeightM;
        }

        /// <summary>
        /// Width scale from the crown the stem actually carries, against the prefab's
        /// own crown width. Kept separate from height because a real stem's slenderness
        /// changes with size - Unity's tree instances carry the two scales separately
        /// for exactly this reason.
        /// </summary>
        public static float WidthScaleFor(float crownRadiusM, float nativeCrownRadiusM)
        {
            if (nativeCrownRadiusM <= 1e-3f) return 1f;
            if (crownRadiusM <= 1e-3f) return 1f;

            return crownRadiusM / nativeCrownRadiusM;
        }

        /// <summary>
        /// Crown area in square metres. The figure the canopy light grid deposits
        /// leaf area over, and the one the interlace test measures against.
        /// </summary>
        public static float CrownAreaM2(float crownRadiusM)
        {
            float r = Mathf.Max(0f, crownRadiusM);
            return Mathf.PI * r * r;
        }

        /// <summary>
        /// Basal area of one stem, square metres. Reineke's law is a statement about
        /// how much basal area a hectare can carry, so the cap needs this.
        /// </summary>
        public static float BasalAreaM2(float dbhM)
        {
            float r = Mathf.Max(MinDbhM, dbhM) * 0.5f;
            return Mathf.PI * r * r;
        }
    }
}
