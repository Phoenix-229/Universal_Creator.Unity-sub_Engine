// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UCWorldPreset.cs   (CTL-2)
//  Purpose: One data asset that drives the world-level dials, so a world can be
//           handed over, versioned and swapped as a single file instead of as a
//           page of numbers typed onto a component.
//
//  WHY PER-FIELD OVERRIDE TOGGLES AND NOT ONE BIG SWITCH
//  ----------------------------------------------------
//  A preset that forces every value it contains makes the author choose between
//  "use my seed" and "use this preset's terrain shape". With a toggle per field a
//  preset can carry a world SIZE and leave the seed alone, which is how these are
//  actually used - one preset for "a 16 km continent", another for "a test tile",
//  neither of which has an opinion about the seed.
//
//  WHAT THIS DELIBERATELY DOES NOT COVER, AND WHY
//  ---------------------------------------------
//  CTL-2 lists World / Noise / Erosion / River / Biome / Vegetation / Seed as
//  categories. Only the WORLD and SEED dials live on the manager; every noise,
//  erosion, river, biome and vegetation dial lives on a NODE inside the graph. A
//  preset reaching into node fields would give those fields two writers - the graph
//  the author wired and the preset - which is precisely the fault CTL-1 was fixing
//  one layer down (sea level had to be reduced to a single writer). A node's values
//  belong to the graph asset, and a graph is already a swappable file.
//
//  So: this asset owns what the manager owns. Driving node dials needs a different
//  mechanism (a graph variant or a parameter-override list), which is a separate
//  design, not a bigger version of this one.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level authoring preset. Assign one to a <see cref="UniverseCreatorObject"/> and the
    /// fields whose override toggle is on drive the world; clear it and the author's own values
    /// come back exactly as they were.
    /// </summary>
    [CreateAssetMenu(menuName = "Universe Creator/World Preset", fileName = "UCWorldPreset")]
    public class UCWorldPreset : ScriptableObject
    {
        [Header("Seed")]
        [Tooltip("Drive the world seed from this preset.")]
        public bool overrideSeed = false;
        public int seed = 12345;

        [Header("World")]
        [Tooltip("Drive the world's span in kilometres from this preset.")]
        public bool overrideWorldSizeKm = false;

        [Tooltip("World span per side, in kilometres. Clamped by the manager's own OnValidate "
                 + "exactly as a typed value is, so a preset cannot ask for an impossible world.")]
        public float worldSizeKm = 4f;

        [Tooltip("Drive the world's height range from this preset.")]
        public bool overrideMaxHeightMeters = false;

        [Tooltip("Metres from the world's floor to its ceiling. Every normalised height in the "
                 + "system is a fraction of THIS, so changing it rescales the whole relief.")]
        public float maxHeightMeters = 600f;

        [Tooltip("Drive the heightmap resolution from this preset.")]
        public bool overrideResolution = false;

        [Tooltip("Heightmap samples per side. Unity requires 2^n+1; the manager validates it.")]
        public int resolution = 257;

        /// <summary>
        /// True when this preset drives at least one field. A preset with every toggle off is
        /// assigned but inert, which the inspector should say rather than implying it is in charge.
        /// </summary>
        public bool DrivesAnything
        {
            get
            {
                return overrideSeed || overrideWorldSizeKm
                    || overrideMaxHeightMeters || overrideResolution;
            }
        }

        /// <summary>How many fields this preset drives - for the inspector's summary line.</summary>
        public int DrivenFieldCount
        {
            get
            {
                int n = 0;
                if (overrideSeed) n++;
                if (overrideWorldSizeKm) n++;
                if (overrideMaxHeightMeters) n++;
                if (overrideResolution) n++;
                return n;
            }
        }
    }
}
