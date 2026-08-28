// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LithologyProfile.cs
//  Purpose: The § 9.4 answer to the one question every close-up relief process
//           asks and retired M5 could not: WHAT IS THIS GROUND MADE OF? Authors a
//           stratigraphic column - an ordered list of rock units, oldest first,
//           exactly as a geologic column is drawn - so the weathering processes of
//           § 9.5, the splat choice of § 9.7, § 5's soil depth and § 6's rooting
//           all read the same material identity instead of each inventing one.
//
//  A COLUMN, NOT A ROCK-TYPE LOOKUP
//  --------------------------------
//  Real geology is layered and ordered in time. A single "rock type per pixel"
//  field cannot express a hard bench of sandstone standing over a soft slope of
//  shale, because that landform IS the ordering - the resistant bed on top of the
//  weak one. So the unit of authorship here is the COLUMN (this asset), and each
//  entry is a bed with the physical properties downstream systems actually consume,
//  every one in a named real-world unit. Nothing here is a noise setting.
//
//  WHAT THIS FILE OWNS AND WHAT IT DOES NOT
//  ----------------------------------------
//  It owns the data and its physical sanity (ranges, per-class defaults, ordering).
//  It does NOT place the beds in space (that is the fold/dip structural field) nor
//  decide which unit is exposed at a point (that is outcrop resolution). Those read
//  this profile; they do not live in it. Keeping the material identity in one asset
//  is what lets every later process agree about the same rock.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Geology
{
    /// <summary>
    /// The weathering family a bed belongs to. This is the master switch every § 9.5
    /// process reads first: it decides which weathering shape is even ELIGIBLE before
    /// any rate is considered - karst can only touch carbonate, columnar joints only
    /// volcanic, and so on. Named for the process, not the hand sample.
    /// </summary>
    public enum RockClass
    {
        Crystalline,    // granite, gabbro - exfoliates in sheets, grus to coarse sand
        Carbonate,      // limestone, dolomite - the only class that dissolves (karst)
        Clastic,        // sandstone, shale - bedded, cross-beds, tafoni near salt
        Volcanic,       // basalt, tuff - columnar joints, blocky talus
        Metamorphic,    // schist, gneiss, slate - foliated, platy to blocky
        Unconsolidated  // alluvium, till, sand - no coherent rock, already sediment
    }

    /// <summary>
    /// One bed in the column. Every field is a property a downstream process consumes,
    /// in a named real-world unit; there are no tuning knobs here that do not mean
    /// something physical. Serializable so the whole column authors inline on the asset.
    /// </summary>
    [System.Serializable]
    public class LithoUnit
    {
        [Tooltip("The unit as a geologist would name it, so the debug view is readable.")]
        public string displayName = "Unnamed unit";

        [Tooltip("The weathering family. Selects which § 9.5 process is even eligible " +
                 "before any rate is considered.")]
        public RockClass rockClass = RockClass.Clastic;

        [Tooltip("Bed thickness in metres. Sets joint spacing (L-38) and therefore facet " +
                 "size, and is what an outcrop measures top to bottom.")]
        public float thicknessM = 20f;

        [Tooltip("Relative resistance to erosion (Klsu, dimensionless). Hard beds stand as " +
                 "benches, soft beds retreat into slopes. Higher = more resistant.")]
        public float erodibilityKlsu = 1f;

        [Range(0f, 1f)]
        [Tooltip("Pore fraction 0-1. Gates salt weathering (haloclasty) and tafoni (L-42): " +
                 "no pores, no salt crystallisation damage.")]
        public float porosity = 0.1f;

        [Range(0f, 1f)]
        [Tooltip("Dissolution susceptibility 0-1. Gates karst (L-40). Non-zero for carbonate " +
                 "only - silicate rock does not dissolve at surface conditions.")]
        public float solubility = 0f;

        [Tooltip("Grain size this unit weathers INTO, on the Udden-Wentworth φ scale (L-31). " +
                 "Lower φ is coarser: gravel < -1, sand 0-4, silt 4-8, clay > 8.")]
        public float grainSizePhi = 2f;

        [Range(1, 6)]
        [Tooltip("Joint sets that cut the rock. 1 = bedding only (platy), 2-3 = blocky, " +
                 "6 = columnar (basalt). Decides block shape, not just size.")]
        public int jointSetCount = 2;

        [Range(0f, 60f)]
        [Tooltip("Angle of repose in degrees for the talus this unit sheds below an outcrop " +
                 "(L-34). Angular blocks stand steeper than rounded sand.")]
        public float angleOfReposeDeg = 34f;

        [Tooltip("Soil production rate in mm per thousand years. Feeds § 5's soil depth: fast " +
                 "on weak porous rock, near zero on fresh crystalline rock.")]
        public float soilFormationRate = 40f;

        [Range(0f, 1f)]
        [Tooltip("Dry surface reflectance 0-1, so the § 9.7 splat and § 8 lighting agree about " +
                 "the same rock. Dark basalt ~0.07, pale limestone ~0.4.")]
        public float albedo = 0.25f;

        [Range(0f, 1f)]
        [Tooltip("How much darker the surface reads when wet, 0-1. Porous rock darkens more.")]
        public float wetDarkening = 0.3f;

        [Tooltip("The terrain layer this unit paints as, when the column is exposed as splat. " +
                 "Optional: left empty, the unit still drives relief and soil, just not colour.")]
        public TerrainLayer terrainLayer;

        /// <summary>
        /// Clamps every field into a physically meaningful range and enforces the one
        /// cross-field rule that matters: only carbonate dissolves, so solubility is
        /// forced to zero on every other class. Called by the profile's OnValidate so a
        /// hand-typed silicate can never quietly grow a karst budget.
        /// </summary>
        public void Sanitize()
        {
            thicknessM        = Mathf.Max(0.01f, thicknessM);
            erodibilityKlsu   = Mathf.Max(0.001f, erodibilityKlsu);
            porosity          = Mathf.Clamp01(porosity);
            solubility        = rockClass == RockClass.Carbonate ? Mathf.Clamp01(solubility) : 0f;
            grainSizePhi      = Mathf.Clamp(grainSizePhi, -8f, 14f);
            jointSetCount     = Mathf.Clamp(jointSetCount, 1, 6);
            angleOfReposeDeg  = Mathf.Clamp(angleOfReposeDeg, 0f, 60f);
            soilFormationRate = Mathf.Max(0f, soilFormationRate);
            albedo            = Mathf.Clamp01(albedo);
            wetDarkening      = Mathf.Clamp01(wetDarkening);
        }

        /// <summary>
        /// A physically sensible bed for a rock class - the real-world defaults a geologist
        /// would reach for, so an author builds a believable column by picking classes rather
        /// than by hand-tuning ten numbers. These are typical field values, not one true
        /// answer; the author edits from here. Grain size is what the rock weathers INTO.
        /// </summary>
        public static LithoUnit Preset(RockClass rockClass)
        {
            var u = new LithoUnit { rockClass = rockClass };
            switch (rockClass)
            {
                case RockClass.Crystalline: // granite: hard, low pore, sheds coarse grus
                    u.displayName = "Granite"; u.thicknessM = 200f; u.erodibilityKlsu = 4f;
                    u.porosity = 0.02f; u.grainSizePhi = -1f; u.jointSetCount = 3;
                    u.angleOfReposeDeg = 37f; u.soilFormationRate = 8f;
                    u.albedo = 0.35f; u.wetDarkening = 0.25f; break;
                case RockClass.Carbonate: // limestone: the one that dissolves
                    u.displayName = "Limestone"; u.thicknessM = 40f; u.erodibilityKlsu = 2f;
                    u.porosity = 0.15f; u.solubility = 0.8f; u.grainSizePhi = 3f; u.jointSetCount = 2;
                    u.angleOfReposeDeg = 34f; u.soilFormationRate = 25f;
                    u.albedo = 0.42f; u.wetDarkening = 0.3f; break;
                case RockClass.Clastic: // shale: soft, bedded, retreats into slopes
                    u.displayName = "Shale"; u.thicknessM = 15f; u.erodibilityKlsu = 0.6f;
                    u.porosity = 0.2f; u.grainSizePhi = 8f; u.jointSetCount = 1;
                    u.angleOfReposeDeg = 28f; u.soilFormationRate = 60f;
                    u.albedo = 0.22f; u.wetDarkening = 0.45f; break;
                case RockClass.Volcanic: // basalt: columnar, dark, blocky talus
                    u.displayName = "Basalt"; u.thicknessM = 30f; u.erodibilityKlsu = 3f;
                    u.porosity = 0.08f; u.grainSizePhi = 0f; u.jointSetCount = 6;
                    u.angleOfReposeDeg = 38f; u.soilFormationRate = 30f;
                    u.albedo = 0.08f; u.wetDarkening = 0.5f; break;
                case RockClass.Metamorphic: // schist: foliated, platy
                    u.displayName = "Schist"; u.thicknessM = 60f; u.erodibilityKlsu = 2.5f;
                    u.porosity = 0.04f; u.grainSizePhi = 4f; u.jointSetCount = 2;
                    u.angleOfReposeDeg = 33f; u.soilFormationRate = 20f;
                    u.albedo = 0.28f; u.wetDarkening = 0.35f; break;
                default: // Unconsolidated alluvium: already sediment
                    u.displayName = "Alluvium"; u.thicknessM = 5f; u.erodibilityKlsu = 0.2f;
                    u.porosity = 0.35f; u.grainSizePhi = 5f; u.jointSetCount = 1;
                    u.angleOfReposeDeg = 32f; u.soilFormationRate = 120f;
                    u.albedo = 0.3f; u.wetDarkening = 0.5f; break;
            }
            u.Sanitize();
            return u;
        }
    }

    /// <summary>
    /// The stratigraphic column, authored as a project asset so many worlds can share a
    /// geology. The list is ordered OLDEST FIRST - index 0 is the basement, the last entry
    /// is the youngest bed at the top - exactly as a geologic column is drawn, so an author
    /// reading the list top to bottom is reading it bottom to top on the ground. The
    /// structural field (fold/dip) and outcrop resolution read this ordering; they do not
    /// redefine it.
    /// </summary>
    [CreateAssetMenu(fileName = "LithologyProfile",
                     menuName = "Universe Creator/Lithology Profile", order = 120)]
    public class LithologyProfile : ScriptableObject
    {
        [Tooltip("The beds of this column, OLDEST FIRST: element 0 is the basement, the last " +
                 "element is the youngest bed exposed at the top of the section.")]
        public List<LithoUnit> units = new List<LithoUnit>();

        [Tooltip("How this column is folded, tilted and faulted in space. The units say what the " +
                 "rock is and in what order; the structure says where each bed top sits at a point.")]
        public GeologicStructure structure = new GeologicStructure();


        /// <summary>Number of beds in the column.</summary>
        public int UnitCount => units != null ? units.Count : 0;

        /// <summary>
        /// The basement - the oldest unit, exposed wherever erosion has cut through
        /// everything above it. This is the fallback outcrop when a point sits below the
        /// whole column, so it must always exist for a non-empty profile.
        /// </summary>
        public LithoUnit Basement => UnitCount > 0 ? units[0] : null;

        /// <summary>Bounds-checked accessor; null outside the column rather than throwing.</summary>
        public LithoUnit UnitAt(int index)
        {
            return (units != null && index >= 0 && index < units.Count) ? units[index] : null;
        }

        /// <summary>
        /// Total authored thickness of the column in metres - the structural datum's worth of
        /// section. The fold/dip field sums thicknesses above a unit to place its bed top, so
        /// this is the ceiling of that sum.
        /// </summary>
        public float TotalThicknessM()
        {
            float sum = 0f;
            for (int i = 0; i < UnitCount; i++) sum += units[i].thicknessM;
            return sum;
        }

        /// <summary>
        /// Builds the three-bed column the § 9.4 done-when asks for - a hard bench over a soft
        /// slope over a resistant basement, three visibly different surfaces - so an author has
        /// a believable starting column without hand-entering one. Sandstone is authored here
        /// via the Clastic preset then stiffened, since the shared Clastic default is shale.
        /// </summary>
        public void PopulateExampleColumn()
        {
            units = new List<LithoUnit>
            {
                LithoUnit.Preset(RockClass.Crystalline), // basement, oldest
                LithoUnit.Preset(RockClass.Clastic),     // soft shale slope
                LithoUnit.Preset(RockClass.Carbonate)    // resistant limestone bench on top
            };
            for (int i = 0; i < units.Count; i++) units[i].Sanitize();
        }

        // Keeps every bed physically sane the moment it is edited in the inspector, and is
        // where the carbonate-only solubility rule is enforced across the whole column.
        private void OnValidate()
        {
            if (structure != null) structure.Sanitize();
            if (units == null) return;
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null) units[i].Sanitize();
        }
    }
}

