// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCVegetationSystemManager.cs
//  Purpose: The VegetationSystem child's control surface. DECLARED ONLY - no
//           field here is read by anything yet.
//
//  WHY A FILE THAT DOES NOTHING
//  ----------------------------
//  The vegetation module is not written. This component exists so that when it is
//  written it has an agreed shape to fill: which settings live at world level,
//  what they are called, what units they are in, and which of them the terrain
//  side already constrains.
//
//  That last part is the real reason. Vegetation cannot be designed in isolation
//  from the terrain it stands on, and the constraints are already fixed:
//
//   - Placement must be a pure function of absolute world position and seed.
//     A cell that streams out and back must return the same trees in the same
//     places. Anything seeded from spawn order or frame time breaks that.
//   - Density and mask sampling read from the graph's surface outputs, which are
//     already produced per cell. Vegetation reads them; it does not re-run noise.
//   - Detail and tree instances belong to the native TerrainData of each cell, so
//     they are placed per cell and rebuilt when that cell rebuilds.
//   - Wind comes from the weather system's single wind manager. Vegetation reads
//     it. It must not add a second wind source.
//
//  A later pass that starts from a blank file will invent its own answers to all
//  four and end up with a parallel system. The fields below are the answers.
//
//  Nothing acts on them. A stub that half-worked would be worse than one that
//  does nothing: it would look implemented.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level vegetation control. Declared for a later module; no field here
    /// is read by anything yet.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Vegetation System Manager")]
    [DisallowMultipleComponent]
    public class UCVegetationSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  GLOBAL
        // =====================================================================

        [Header("Global  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Master switch for every vegetation pass.")]
        public bool vegetationEnabled = true;

        [Tooltip("PLACEHOLDER. Offset from the world seed. Placement stays a pure " +
                 "function of world position and this value, so a cell that streams " +
                 "out and back returns identical vegetation.")]
        public int seedOffset;

        [Tooltip("PLACEHOLDER. Scales every density below at once, for quickly " +
                 "trading fidelity for frame rate without retuning each layer.")]
        [Range(0f, 2f)]
        public float globalDensityScale = 1f;

        // =====================================================================
        //  GRASS AND DETAIL
        // =====================================================================

        [Header("Grass and Detail  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Grass instances per square metre at full density.")]
        public float grassPerSquareMetre = 4f;

        [Tooltip("PLACEHOLDER. Metres from the camera at which detail stops drawing. " +
                 "This is the single largest vegetation cost.")]
        public float detailDrawDistanceMetres = 120f;

        [Tooltip("PLACEHOLDER. Detail resolution per cell. Higher places grass more " +
                 "precisely and costs more memory per terrain cell.")]
        public int detailResolution = 512;

        [Tooltip("PLACEHOLDER. Samples per detail patch. Unity's own patch size " +
                 "control; 8 or 16 are the usual answers.")]
        public int detailResolutionPerPatch = 16;

        [Tooltip("PLACEHOLDER. Detail sways with the weather system's wind rather " +
                 "than with its own oscillator, so grass and trees agree.")]
        public bool detailUsesWorldWind = true;

        // =====================================================================
        //  TREES
        // =====================================================================

        [Header("Trees  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Trees per hectare at full density.")]
        public float treesPerHectare = 60f;

        [Tooltip("PLACEHOLDER. Metres from the camera at which trees stop drawing.")]
        public float treeDrawDistanceMetres = 1500f;

        [Tooltip("PLACEHOLDER. Metres over which a tree crossfades to its billboard.")]
        public float billboardStartMetres = 90f;

        [Tooltip("PLACEHOLDER. Metres over which the billboard crossfade happens. " +
                 "Zero pops.")]
        public float billboardFadeLengthMetres = 20f;

        [Tooltip("PLACEHOLDER. Minimum metres between two tree instances. Stops the " +
                 "clumping that a pure density figure produces on its own.")]
        public float treeSpacingMetres = 3.5f;

        // =====================================================================
        //  PLACEMENT RULES
        // =====================================================================

        [Header("Placement Rules  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Steepest ground, in degrees, that vegetation will " +
                 "stand on. Past this the slope reads as rock.")]
        [Range(0f, 90f)]
        public float maxSlopeDegrees = 38f;

        [Tooltip("PLACEHOLDER. Metres above sea level below which nothing grows.")]
        public float minAltitudeMetres;

        [Tooltip("PLACEHOLDER. Metres above sea level above which nothing grows - " +
                 "the tree line.")]
        public float maxAltitudeMetres = 1800f;

        [Tooltip("PLACEHOLDER. Metres of clearance kept around water edges, roads " +
                 "and other cut regions.")]
        public float clearanceMetres = 1.5f;

        [Tooltip("PLACEHOLDER. Vegetation is kept out of water bodies rather than " +
                 "drowned in them.")]
        public bool avoidWater = true;

        [Tooltip("PLACEHOLDER. Vegetation is kept out of regions the road bridge has " +
                 "cut.")]
        public bool avoidCutRegions = true;

        // =====================================================================
        //  BIOME AND MASKING
        // =====================================================================

        [Header("Biome and Masking  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Density is read from the graph's surface output masks " +
                 "rather than from a single global figure - which is what makes a " +
                 "forest follow the terrain instead of covering it uniformly.")]
        public bool useGraphMasks = true;

        [Tooltip("PLACEHOLDER. Mask value below which nothing is placed. Stops a " +
                 "faint mask edge from scattering isolated instances.")]
        [Range(0f, 1f)]
        public float maskThreshold = 0.15f;

        [Tooltip("PLACEHOLDER. Metres over which a biome boundary blends rather than " +
                 "switching in one step.")]
        public float biomeBlendMetres = 12f;

        // =====================================================================
        //  BUILD
        // =====================================================================

        [Header("Build  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Vegetation rebuilds with the terrain cell it stands " +
                 "on. Off means it is only built when asked for.")]
        public bool rebuildWithChunks = true;

        [Tooltip("PLACEHOLDER. Cells whose vegetation may be built at once. Matches " +
                 "the terrain side's concurrency so the two do not compete.")]
        public int maxConcurrentCells = 2;

        /// <summary>The world this vegetation belongs to, or null when loose in a scene.</summary>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        private void OnValidate()
        {
            globalDensityScale = Mathf.Clamp(globalDensityScale, 0f, 2f);
            grassPerSquareMetre = Mathf.Max(0f, grassPerSquareMetre);
            detailDrawDistanceMetres = Mathf.Max(0f, detailDrawDistanceMetres);

            // Unity requires the detail resolution to divide evenly into patches, so
            // the two are clamped together rather than independently.
            detailResolutionPerPatch = Mathf.Clamp(detailResolutionPerPatch, 8, 128);
            detailResolution = Mathf.Clamp(detailResolution, 0, 4096);

            treesPerHectare = Mathf.Max(0f, treesPerHectare);
            treeDrawDistanceMetres = Mathf.Max(0f, treeDrawDistanceMetres);
            billboardStartMetres = Mathf.Max(0f, billboardStartMetres);
            billboardFadeLengthMetres = Mathf.Max(0f, billboardFadeLengthMetres);
            treeSpacingMetres = Mathf.Max(0f, treeSpacingMetres);

            minAltitudeMetres = Mathf.Min(minAltitudeMetres, maxAltitudeMetres);
            clearanceMetres = Mathf.Max(0f, clearanceMetres);
            biomeBlendMetres = Mathf.Max(0f, biomeBlendMetres);
            maxConcurrentCells = Mathf.Clamp(maxConcurrentCells, 1, 16);
        }
    }
}
