// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LithologyColumnView.cs
//  Purpose: The presentation model behind the § 9.11 lithology inspector (G-69): a stratigraphic
//           column drawn AS a column. The list is authored oldest-first, so the inspector draws it
//           oldest at the bottom; beside each bed it shows a cumulative-thickness readout and a colour
//           swatch. This file owns the pure numbers those widgets display - the depth of each bed top
//           below the top of the section, the cumulative height above the basement, and the swatch
//           colour - so the drawer only lays them out.
//
//  THE READOUT IS THE SAME NUMBER THE STRUCTURE USES
//  -------------------------------------------------
//  A bed's depth below the section top is the summed thickness of the YOUNGER units above it - exactly
//  the `sumAbove` term `GeologicStructure.BedTopElevationM` places a bed with. That is why reordering
//  two units visibly changes the outcrop: it changes how much section sits above each bed, so the bed
//  tops move and a different unit is exposed. The readout and the ground read the same ordering.
//
//  DEVICE-FREE, DRAWER-FREE
//  ------------------------
//  Pure statics over the profile; no editor, no GUI, no scene. The custom inspector and the live
//  strike/dip scene preview are the editor layer. The self-test pins the cumulative maths, that the
//  youngest bed sits at zero depth, that reordering changes the readout, and that the swatch reads.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Geology
{
    /// <summary>
    /// The pure presentation model for the § 9.11 lithology column inspector (G-69): per-bed cumulative
    /// thickness and swatch colour, drawn oldest-at-the-bottom the way a geologic column is drawn. The depth
    /// readout is the same order-dependent thickness term the structural field uses, so the inspector and the
    /// outcrop agree. Device-free; the drawer and the scene preview are the editor layer.
    /// </summary>
    public static class LithologyColumnView
    {
        /// <summary>Depth in metres of a unit's bed top below the TOP of the section: the summed thickness of the
        /// younger units above it (indices greater than <paramref name="unitIndex"/>). The youngest bed (last index)
        /// sits at zero. This is exactly the term <see cref="GeologicStructure.BedTopElevationM"/> subtracts.</summary>
        public static float DepthBelowTopM(LithologyProfile profile, int unitIndex)
        {
            if (profile == null) return 0f;
            float sumAbove = 0f;
            int count = profile.UnitCount;
            for (int k = unitIndex + 1; k < count; k++)
            {
                LithoUnit u = profile.UnitAt(k);
                if (u != null) sumAbove += u.thicknessM;
            }
            return sumAbove;
        }

        /// <summary>Cumulative thickness in metres from the basement up to and including a unit: the height of its
        /// bed top above the base of the column. The basement (index 0) contributes its own thickness.</summary>
        public static float CumulativeFromBottomM(LithologyProfile profile, int unitIndex)
        {
            if (profile == null) return 0f;
            float sum = 0f;
            int count = profile.UnitCount;
            for (int k = 0; k <= unitIndex && k < count; k++)
            {
                LithoUnit u = profile.UnitAt(k);
                if (u != null) sum += u.thicknessM;
            }
            return sum;
        }

        /// <summary>The whole column's depth-below-top readout, one entry per bed in authored (oldest-first) order,
        /// so the drawer can label each row as it stacks them oldest at the bottom. Empty for a null/empty profile.</summary>
        public static float[] DepthsBelowTop(LithologyProfile profile)
        {
            int count = profile != null ? profile.UnitCount : 0;
            var depths = new float[count];
            for (int i = 0; i < count; i++) depths[i] = DepthBelowTopM(profile, i);
            return depths;
        }

        /// <summary>The colour swatch for a bed: a stable per-rock-class hue, its brightness driven by the unit's
        /// authored dry albedo so a dark basalt reads dark and a pale limestone reads pale. Always defined, even when
        /// the unit assigns no terrain layer, so the column is legible before any splat is wired.</summary>
        public static Color SwatchColor(LithoUnit unit)
        {
            if (unit == null) return Color.grey;
            Color hue = ClassColor(unit.rockClass);
            float v = 0.3f + 0.7f * Mathf.Clamp01(unit.albedo); // brighter with higher reflectance
            return new Color(hue.r * v, hue.g * v, hue.b * v, 1f);
        }

        /// <summary>A distinct reference hue per weathering class, so two beds of different classes never share a
        /// swatch. Brightness is applied separately from the unit's albedo.</summary>
        private static Color ClassColor(RockClass c)
        {
            switch (c)
            {
                case RockClass.Crystalline:    return new Color(0.85f, 0.55f, 0.55f); // pinkish granite
                case RockClass.Carbonate:      return new Color(0.55f, 0.70f, 0.85f); // pale blue limestone
                case RockClass.Clastic:        return new Color(0.80f, 0.72f, 0.45f); // tan sandstone/shale
                case RockClass.Volcanic:       return new Color(0.45f, 0.45f, 0.50f); // dark basalt
                case RockClass.Metamorphic:    return new Color(0.60f, 0.55f, 0.65f); // violet schist
                default:                       return new Color(0.75f, 0.70f, 0.60f); // buff alluvium
            }
        }
    }
}
