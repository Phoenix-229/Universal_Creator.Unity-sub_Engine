// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    GeometrySettings.cs
//  Purpose: The authored geometry dials, in one serialized block (§ 9.11 G-68, C-16). These are
//           the numbers a world author is allowed to turn - density and acuity, the § 9.5 overlay,
//           § 9.4 lithology, § 9.6 impostors, the § 9.7 splat cache, § 9.8 painting, § 9.9 streaming
//           and the § 9.10 budget and thermal governor. Every field carries its UNIT (in the name or
//           the tooltip), a default, and a note saying which law consumes it, so no dial in the
//           inspector is a mystery number.
//
//  DERIVED DISTANCES RIDE ON ACUITY, THEY ARE NOT AUTHORED TWICE
//  ------------------------------------------------------------
//  Tree, detail and basemap draw distances are NOT separate dials - they are derived from the one
//  acuity budget through the angular-error law (L-49), evaluated against the fixed reference view.
//  The read-only `Derived*` accessors here compute them so the inspector can show each distance right
//  next to the acuity value that produced it; raise acuity (coarser tolerance) and every derived
//  distance pulls in together. Serialisable, device-free; the custom inspector drawer is the editor
//  layer. Field names and order are stable so a saved setup deserialises with every value intact.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// The authored geometry settings block (§ 9.11 G-68). One serialised object, grouped by subsystem, every
    /// field carrying a unit, a default and the law it feeds. Derived draw distances are exposed read-only off the
    /// single acuity budget (L-49) rather than authored separately. Device-free; the inspector renders it.
    /// </summary>
    [System.Serializable]
    public class GeometrySettings
    {
        // -- Density and acuity (L-49, G-07) -----------------------------------

        [Tooltip("px. The visual-acuity budget: the terrain silhouette error tolerated on screen, and the one " +
                 "dial the tree/detail/basemap draw distances are derived from (L-49). 1 = Desktop/Ultra, 2 = " +
                 "Mobile High, 4 = Mobile Low. The tier may RAISE this at runtime; it is never lowered below the " +
                 "source heightfield's Nyquist floor (L-50).")]
        [Range(1f, 8f)]
        public float acuityPixels = 1f;

        [Tooltip("m. The on-screen feature size a tree is treated as, for deriving its draw distance from acuity.")]
        [Range(0.5f, 40f)]
        public float treeFeatureSizeM = 8f;

        [Tooltip("m. The feature size a ground-detail (grass) clump is treated as, for deriving its draw distance.")]
        [Range(0.05f, 4f)]
        public float detailFeatureSizeM = 0.5f;

        [Tooltip("m. The feature size the terrain basemap is treated as, for deriving the basemap fade distance.")]
        [Range(1f, 200f)]
        public float basemapFeatureSizeM = 40f;

        // -- Overlay ring (§ 9.5) ----------------------------------------------

        [Tooltip("Whether the sub-heightmap detail overlay is drawn at all. Off on Mobile Low (§ 9.12).")]
        public bool overlayEnabled = true;

        [Tooltip("cells/side. The overlay ring size around the camera (odd, e.g. 7 = 7×7). Larger rings cover " +
                 "more ground at higher cost; the § 9.10 ladder shrinks it a row at a time under budget.")]
        [Range(1, 11)]
        public int overlayRingCells = 7;

        [Tooltip("×. The overlay subdivision factor: how many overlay quads per terrain quad. 4 or 8 (§ 9.12).")]
        [Range(1, 16)]
        public int overlaySubdivisionFactor = 8;

        [Tooltip("Whether the overlay uses hardware tessellation; when off it falls back to the G-24 " +
                 "pre-subdivided mesh. The ladder disables tessellation before disabling the overlay (G-63).")]
        public bool overlayTessellationEnabled = true;

        // -- Lithology (§ 9.4, G-14, G-28) -------------------------------------

        [Tooltip("Whether lithology drives splat choice and micro-relief. Off still yields a coherent world (G-70).")]
        public bool lithologyEnabled = true;

        [Tooltip("Whether the opt-in lithology-driven erosion pass runs (G-14). Off by default: it is a choice, " +
                 "never a silent default.")]
        public bool lithologyErosionEnabled = false;

        [Tooltip("mm. The maximum micro-relief height offset lithology may add, consumed by § 6's scatter (G-28).")]
        [Range(0f, 200f)]
        public float microReliefMaxOffsetMm = 40f;

        // -- Impostors (§ 9.6, G-31, G-36) -------------------------------------

        [Tooltip("m. The distance at which a prop swaps from mesh LODs to its billboard impostor. The § 9.10 " +
                 "ladder pulls this in under budget (G-63); never writes the world.")]
        [Range(20f, 400f)]
        public float impostorSwitchDistanceM = 120f;

        [Tooltip("px. The impostor atlas tile size per view (64/96/256 by class, § 9.12).")]
        [Range(32, 512)]
        public int impostorTilePixels = 256;

        [Tooltip("MB. The impostor atlas memory ceiling; residency (G-36) keeps only the hottest species resident.")]
        [Range(4f, 128f)]
        public float impostorAtlasCeilingMB = 48f;

        [Tooltip("species. How many impostor species stay resident at once (6 Desktop, 12 Ultra, G-36) - not the " +
                 "whole library. This is what makes a fifty-species world fit the atlas ceiling.")]
        [Range(1, 32)]
        public int impostorResidentSpecies = 6;

        // -- Splat cache (§ 9.7) -----------------------------------------------

        [Tooltip("Whether the virtual splat cache is on (Desktop/Ultra only, § 9.12).")]
        public bool splatCacheEnabled = true;

        [Tooltip("rings. The splat cache resident range around the camera; the § 9.10 ladder reduces it a ring " +
                 "at a time under budget (G-63).")]
        [Range(1, 8)]
        public int splatCacheRangeRings = 3;

        [Tooltip("MB. The splat cache memory budget; pages evict least-recently-used beyond it.")]
        [Range(16f, 512f)]
        public float splatCacheBudgetMB = 128f;

        // -- Painting (§ 9.8) --------------------------------------------------

        [Tooltip("MB. The non-destructive paint raster memory ceiling (32/64/256/512 by class, § 9.12).")]
        [Range(16f, 512f)]
        public float paintRasterCeilingMB = 256f;

        [Tooltip("m. The default brush radius a new paint stroke starts at (scroll adjusts it, G-55).")]
        [Range(0.5f, 128f)]
        public float brushDefaultRadiusM = 8f;

        [Tooltip("0..1. The default brush strength a new paint stroke starts at.")]
        [Range(0f, 1f)]
        public float brushDefaultStrength = 0.5f;

        // -- Streaming (§ 9.9) -------------------------------------------------

        [Tooltip("m. The radius within which chunks load. Non-resident chunks inside it are queued (G-59).")]
        [Range(32f, 2000f)]
        public float loadRadiusM = 400f;

        [Tooltip("m. The extra distance beyond loadRadius a resident chunk must exceed before it unloads - the " +
                 "dead band that kills boundary thrash (G-59).")]
        [Range(0f, 500f)]
        public float unloadHysteresisM = 64f;

        [Tooltip("chunks. How many chunk builds may run at once (1/2/4/6 by class, § 9.12, G-61).")]
        [Range(1, 8)]
        public int chunkBuildsInFlight = 4;

        [Tooltip("s. How far ahead the velocity predictor projects the camera to prefetch chunks along the path " +
                 "(G-57). Rotation-only movement predicts no travel, so it prefetches nothing.")]
        [Range(0f, 5f)]
        public float prefetchPredictionSeconds = 1.5f;

        // -- Budget and thermal governor (§ 9.10) ------------------------------

        [Tooltip("ms. The geometry CPU budget per frame (1.5/2.5/4/6 by class, § 9.12). The median frame time " +
                 "over this ceiling triggers ordered degradation (G-62, G-63).")]
        [Range(0.5f, 16f)]
        public float frameBudgetMs = 4f;

        [Tooltip("tris. The terrain + prop triangle ceiling (300k/600k/2.5M/6M by class, § 9.12).")]
        [Min(1f)]
        public float triangleCeiling = 2_500_000f;

        [Tooltip("calls. The draw-call ceiling (120/200/800/2000 by class, § 9.12).")]
        [Min(1f)]
        public float drawCallCeiling = 800f;

        [Tooltip("MB. The geometry memory ceiling the governor watches alongside frame time (G-62).")]
        [Min(1f)]
        public float memoryCeilingMB = 512f;

        [Tooltip("fraction. The frame-time drift at matched complexity that signals SoC throttling and a " +
                 "pre-emptive tier step-down (G-65). 0.15 = a 15 % rise at the same triangles/draw-calls.")]
        [Range(0.02f, 0.5f)]
        public float thermalDriftThreshold = 0.15f;

        [Tooltip("s. The minimum interval between two degradation steps, so the ladder descends one step at a " +
                 "time rather than collapsing several at once (G-63).")]
        [Range(0.25f, 10f)]
        public float degradeIntervalSeconds = 1f;

        [Tooltip("s. How long the median must hold under the recovery margin before a degradation step is undone " +
                 "(G-64). The margin + hold form the dead band that kills recovery sawtooth.")]
        [Range(1f, 30f)]
        public float recoverHoldSeconds = 5f;

        // -- Derived draw distances (read-only, L-49) --------------------------
        //  Not authored: these ride on `acuityPixels` through the angular-error law, evaluated against the
        //  fixed reference view. The inspector shows each next to the acuity value that produced it (G-68);
        //  raising acuity pulls all three in together.

        /// <summary>m. The tree draw distance derived from the acuity budget (L-49).</summary>
        public float DerivedTreeDrawDistanceM => AngularError.FeatureCullDistanceReference(treeFeatureSizeM, acuityPixels);

        /// <summary>m. The ground-detail draw distance derived from the acuity budget (L-49).</summary>
        public float DerivedDetailDrawDistanceM => AngularError.FeatureCullDistanceReference(detailFeatureSizeM, acuityPixels);

        /// <summary>m. The basemap fade distance derived from the acuity budget (L-49).</summary>
        public float DerivedBasemapDistanceM => AngularError.FeatureCullDistanceReference(basemapFeatureSizeM, acuityPixels);

        /// <summary>m. The unload radius the streaming layer actually uses: load radius plus its hysteresis (G-59).</summary>
        public float DerivedUnloadRadiusM => loadRadiusM + Mathf.Max(0f, unloadHysteresisM);
    }
}
