// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCVegetationSystemManager.cs
//  Purpose: The VegetationSystem child's control surface. Field declarations only;
//           the read path that turns these into placement numbers lives beside it
//           in UCVegetationSystemManager.Resolve.cs.
//
//  WHAT CHANGED
//  ------------
//  This component used to be declaration-only. It is now live: every field below
//  is read, either by the resolve path in the partial file or by the ecology
//  sampler it feeds. The word PLACEHOLDER has been removed from the tooltips
//  because the tooltips are now true.
//
//  The original twenty-four fields keep their exact names, order, types and
//  defaults. Unity serialises by field name and by nothing else, so renaming one
//  would silently discard every value an author has already entered for it.
//  Everything ecological is appended below them.
//
//  THE CONSTRAINTS THAT SHAPED THIS
//  --------------------------------
//   - Placement is a pure function of absolute world position and seed. A cell
//     that streams out and back returns the same trees in the same places.
//     Nothing here may be seeded from spawn order or frame time.
//   - Density and mask sampling read the graph's surface outputs, already produced
//     per cell. Vegetation reads them; it does not re-run noise.
//   - Detail and tree instances belong to the native TerrainData of each cell, so
//     they are placed per cell and rebuilt when that cell rebuilds.
//   - Wind comes from the weather system's single wind manager. Vegetation reads
//     it and never adds a second wind source.
//
//  WHAT THIS COMPONENT DELIBERATELY DOES NOT OWN
//  ---------------------------------------------
//  Latitude and sea level are world facts, and each already has exactly one
//  owner: latitude on the lighting manager, sea level on the ocean water volume.
//  Climate - the temperature and precipitation fields - belongs to the weather
//  manager. This component resolves all three by reading their owners, because a
//  second copy of a world fact is a second answer to the same question, and the
//  two will disagree the first time an author changes one of them.
//
//  ECOLOGY IS OPT-IN
//  -----------------
//  useEcology defaults off. An existing world keeps the shipped placement path,
//  byte for byte, and opts in when its author is ready. A default that silently
//  rearranged every tree in every saved world would not be an improvement.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Which class of device this world's vegetation budget is aimed at. Three and
    /// not five: the two mobile tiers differ by roughly a factor of two in fill
    /// rate and memory, desktop differs from both by an order of magnitude, and a
    /// tier nobody can describe in one sentence is a tier nobody tunes. (V-33)
    /// </summary>
    public enum VegetationQuality
    {
        MidRangeMobile = 0,
        FlagshipMobile = 1,
        Desktop = 2
    }

    /// <summary>
    /// World-level vegetation control. Partial: the resolve path is in
    /// UCVegetationSystemManager.Resolve.cs.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Vegetation System Manager")]
    [DisallowMultipleComponent]
    public partial class UCVegetationSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  GLOBAL
        // =====================================================================

        [Header("Global")]

        [Tooltip("Master switch for every vegetation pass.")]
        public bool vegetationEnabled = true;

        [Tooltip("Offset from the world seed. Placement stays a pure function of " +
                 "world position and this value, so a cell that streams out and " +
                 "back returns identical vegetation.")]
        public int seedOffset;

        [Tooltip("Scales every density below at once, for quickly trading fidelity " +
                 "for frame rate without retuning each layer.")]
        [Range(0f, 2f)]
        public float globalDensityScale = 1f;

        // =====================================================================
        //  GRASS AND DETAIL
        // =====================================================================

        [Header("Grass and Detail")]

        [Tooltip("Grass instances per square metre at full density.")]
        public float grassPerSquareMetre = 4f;

        // Detail draw distance is not authored here. The acuity budget on
        // UniverseCreatorObject derives detailObjectDistance (and the other three
        // native distances) from one dial; a field here would be a second answer.
        [Tooltip("Detail resolution per cell. Higher places grass more precisely and " +
                 "costs more memory per terrain cell.")]
        public int detailResolution = 512;

        [Tooltip("Samples per detail patch. Unity's own patch size control; 8 or 16 " +
                 "are the usual answers.")]
        public int detailResolutionPerPatch = 16;

        // Detail layers are the one vegetation cost paid in RAM before a single
        // frame is drawn, and it is paid per resident chunk whether or not the
        // grass is on screen: TerrainData keeps an int per detail cell per layer.
        // The arithmetic is in the resolve path and the figure is in the
        // inspector, because 512 with eight species reads as a harmless default
        // right up to the point the build dies on device. (V-29)
        [Tooltip("MB. Warn above this much memory held in terrain detail layers. " +
                 "The figure is res squared x 4 B x layers x resident chunks, and " +
                 "the inspector shows it before you build.")]
        [Min(1f)]
        public float detailMemoryCeilingMB = 96f;

        [Tooltip("Detail sways with the weather system's wind rather than with its own " +
                 "oscillator, so grass and trees agree.")]
        public bool detailUsesWorldWind = true;

        // =====================================================================
        //  TREES
        // =====================================================================

        [Header("Trees")]

        [Tooltip("Trees per hectare at full density. With ecology on this becomes a " +
                 "ceiling rather than a target: Reineke's self-thinning law can only " +
                 "lower it, because a stand denser than the law allows does not exist.")]
        public float treesPerHectare = 60f;

        // Tree draw distance and the billboard swap distance are not authored here.
        // Both come off UniverseCreatorObject's acuity budget (treeDistance and
        // treeBillboardDistance); this manager only pushes the billboard swap OUTWARD
        // for a species that must stay a mesh longer (see ResolvedBillboardStartM).
        [Tooltip("Metres over which the billboard crossfade happens. Zero pops.")]
        public float billboardFadeLengthMetres = 20f;

        // PERF-2. The instances this world has had taken out of it - harvested, chopped,
        // mined, erased by a brush. Serialized with the manager, so a removal survives the
        // chunk unloading and streaming back in, and survives a save and reload.
        //
        // IT LIVES HERE, NOT ON THE CHUNK, because a chunk is the thing that gets thrown
        // away and rebuilt; a delta kept on one would be lost exactly when it is needed.
        // Keyed on VEG-13's stable, position-derived ScatterInstance.instanceId rather than
        // on an array index - an index names a different plant after almost any edit.
        //
        // Empty by default and empty for any world nobody has removed anything from, which
        // is the case the per-instance check short-circuits on, so the scatter path pays
        // nothing until something is actually removed.
        [SerializeField, HideInInspector]
        private ScatterRemovalStore removedInstances = new ScatterRemovalStore();

        /// <summary>
        /// PERF-2. Instances removed from this world, so they stay removed across a chunk
        /// reload and a save. UC-T06-06: call <c>Remove(instance.identity)</c> for captured
        /// keyed placement and <c>Restore(instance.identity)</c> to put it back. The legacy
        /// integer API remains for unstamped placement; migrate its edits explicitly before
        /// applying keyed candidates. Ambiguous matches require selecting the intended instance.
        /// </summary>
        public ScatterRemovalStore RemovedInstances
        {
            get
            {
                // A scene saved before this field existed deserializes it as null.
                if (removedInstances == null) removedInstances = new ScatterRemovalStore();
                return removedInstances;
            }
        }

        [Tooltip("Minimum metres between two tree instances. Stops the clumping that " +
                 "a pure density figure produces on its own. With ecology on, crown " +
                 "geometry may widen this but never narrows it.")]
        public float treeSpacingMetres = 3.5f;

        // =====================================================================
        //  PLACEMENT RULES
        // =====================================================================

        [Header("Placement Rules")]

        [Tooltip("Steepest ground, in degrees, that vegetation will stand on. Past " +
                 "this the slope reads as rock.")]
        [Range(0f, 90f)]
        public float maxSlopeDegrees = 38f;

        [Tooltip("Metres above sea level below which nothing grows.")]
        public float minAltitudeMetres;

        [Tooltip("Metres above sea level above which nothing grows. With ecology on " +
                 "this is a backstop only: the real treeline comes from the warmest " +
                 "month temperature, so it follows latitude and aspect instead of " +
                 "sitting at one altitude everywhere.")]
        public float maxAltitudeMetres = 1800f;

        [Tooltip("Metres of clearance kept around water edges, roads and other cut " +
                 "regions.")]
        public float clearanceMetres = 1.5f;

        [Tooltip("Vegetation is kept out of water bodies rather than drowned in them.")]
        public bool avoidWater = true;

        [Tooltip("Vegetation is kept out of regions the road bridge has cut.")]
        public bool avoidCutRegions = true;

        // =====================================================================
        //  BIOME AND MASKING
        // =====================================================================

        [Header("Biome and Masking")]

        [Tooltip("Density is read from the graph's surface output masks rather than " +
                 "from a single global figure, which is what makes a forest follow " +
                 "the terrain instead of covering it uniformly.")]
        public bool useGraphMasks = true;

        [Tooltip("Mask value below which nothing is placed. Stops a faint mask edge " +
                 "from scattering isolated instances.")]
        [Range(0f, 1f)]
        public float maskThreshold = 0.15f;

        [Tooltip("Metres over which a biome boundary blends rather than switching in " +
                 "one step.")]
        public float biomeBlendMetres = 12f;

        // =====================================================================
        //  BUILD
        // =====================================================================

        [Header("Build")]

        [Tooltip("Vegetation rebuilds with the terrain cell it stands on. Off means " +
                 "it is only built when asked for.")]
        [HideInInspector] // UC-T08-15: retained serialization; accepted graph replacement owns rebuilding, not this unused switch.
        public bool rebuildWithChunks = true;

        [Tooltip("Cells whose vegetation may be built at once. Matches the terrain " +
                 "side's concurrency so the two do not compete.")]
        [HideInInspector] // UC-T08-15: generation scheduler owns concurrency; this field has no execution consumer.
        public int maxConcurrentCells = 2;

        // =====================================================================
        //  ECOLOGY
        //
        //  Everything below this line is appended. Off by default, so an existing
        //  world is untouched until its author asks for it.
        // =====================================================================

        [Header("Ecology")]

        [Tooltip("On, placement is decided by the environment: temperature, water " +
                 "balance, soil, light and disturbance, combined by Liebig's law of " +
                 "the minimum. Off keeps the shipped mask-and-density path exactly, " +
                 "so a saved world does not change appearance on upgrade.")]
        public bool useEcology = false;

        [Tooltip("Suitability below which a species is absent from a site rather than " +
                 "merely sparse. Raise it for sharper biome edges, lower it to let " +
                 "species trail off gradually.")]
        [Range(0f, 1f)]
        public float suitabilityThreshold = 0.25f;

        [Tooltip("Exponent applied to the combined suitability. Above 1 crisps up " +
                 "boundaries by punishing mediocre sites; below 1 softens them. 1 is " +
                 "the unmodified law.")]
        [Range(0.25f, 4f)]
        public float suitabilitySharpness = 1f;

        [Tooltip("On, a site's second and third best species can also appear there, " +
                 "in proportion to their suitability. Off places only the winner, " +
                 "which reads as monoculture blocks.")]
        public bool allowMixedStands = true;

        [Tooltip("How many species may share one site when mixed stands are on. Real " +
                 "temperate forests are two to four species deep.")]
        [Range(1, 8)]
        public int maxSpeciesPerSite = 3;

        // =====================================================================
        //  ENVIRONMENT SAMPLING
        // =====================================================================

        [Header("Environment Sampling")]

        [Tooltip("Metres out to which the sampler looks for a water body. Beyond this " +
                 "a site simply reports no water, so a riparian species fails its " +
                 "distance band rather than needing a special case. Larger costs " +
                 "search time on every candidate.")]
        public float waterSearchRadiusMetres = 300f;

        [Tooltip("Metres of soil on level ground. Soil depth is derived from slope and " +
                 "curvature, never authored per cell: material creeps downhill, so " +
                 "ridges are thin and hollows are deep.")]
        public float soilDepthMaxMetres = 2.5f;

        [Tooltip("Degrees. The angle of repose: the slope at which loose material can " +
                 "no longer rest, so soil depth reaches zero and the site is bare " +
                 "rock. 35 for angular scree, 30 for rounded gravel, 45 for wet sand.")]
        [Range(0f, 90f)]
        public float soilAngleOfReposeDegrees = 35f;

        [Tooltip("How strongly curvature enriches soil. 0 makes fertility flat; 1 " +
                 "makes hollows markedly richer than ridges, which is what puts the " +
                 "big trees in the valleys.")]
        [Range(0f, 1f)]
        public float fertilityFromCurvature = 0.5f;

        [Tooltip("Metres of upslope radius used to estimate contributing area for the " +
                 "topographic wetness index. Larger finds broader drainage lines and " +
                 "costs more per sample.")]
        [HideInInspector] // UC-T08-15: wetness comes from the accepted derivative field; no radius-based solve is installed.
        public float wetnessCatchmentRadiusMetres = 48f;

        [Tooltip("Metres of e-folding distance for salt spray inland from a shore. " +
                 "The measured figure is about 120 m, and it is what produces the real " +
                 "coastal banding of salt scrub, then pine, then inland forest.")]
        public float saltSprayDecayMetres = 120f;

        // =====================================================================
        //  COMPETITION AND CANOPY
        // =====================================================================

        [Header("Competition and Canopy")]

        /// <summary>Enables sampled canopy shade, cross-layer exclusion and retained crown deposits during ecology generation; false gives full sunlight and performs no canopy-grid work.</summary>
        [Tooltip("On, taller species shade shorter ones through Beer-Lambert " +
                 "extinction, so a forest gains an understory instead of being one " +
                 "species repeated. Off gives every layer full sun.")]
        public bool enableCanopyCompetition = true;

        [Tooltip("Vertical layers resolved per site, tallest first: emergent, canopy, " +
                 "understory, ground cover. Fewer layers is cheaper and flatter.")]
        [Range(1, 4)]
        [HideInInspector] // UC-T08-15: existing species layers are fixed; this copied count never controlled passes.
        public int canopyLayerPasses = 4;

        [Tooltip("Light fraction the ground layer is guaranteed regardless of canopy " +
                 "above it. Zero is physically honest but leaves bare dirt under dense " +
                 "forest; a small floor keeps a sparse floor alive.")]
        [Range(0f, 1f)]
        public float understoryLightFloor = 0.05f;

        [Tooltip("On, stand density is capped by Reineke's self-thinning law from mean " +
                 "trunk diameter, so a mature forest cannot read as a plantation. This " +
                 "can only reduce the trees-per-hectare figure above.")]
        [HideInInspector] // UC-T08-15: no stand-density consumer; retain old saved intent without presenting a nonworking control.
        public bool enableSelfThinning = true;

        [Tooltip("On, spacing follows crown radius from trunk diameter, so large trees " +
                 "stand apart and saplings stand close without either being tuned.")]
        [HideInInspector] // UC-T08-15: spacing is authored by existing scatter nodes and species rules, not this unused switch.
        public bool enableCrownSpacing = true;

        // =====================================================================
        //  DISPERSAL AND CLUSTERING
        // =====================================================================

        [Header("Dispersal and Clustering")]

        [Tooltip("On, sites near an existing stand of the same species are favoured " +
                 "through an exponential seed-dispersal kernel. This is the single " +
                 "switch that makes a forest read as grown rather than sprinkled.")]
        public bool enableDispersalClustering = true;

        [Tooltip("Parent sites per hectare that seed the dispersal field. Few gives " +
                 "large distinct groves; many approaches an even scatter.")]
        [HideInInspector] // UC-T08-15: the seeded dispersal owner has no parent-density input.
        public float dispersalSeedsPerHectare = 8f;

        [Tooltip("How strongly dispersal biases placement. 0 ignores it; 1 lets it " +
                 "dominate, producing tight groves with genuinely open gaps between.")]
        [Range(0f, 1f)]
        public float clusterContrast = 0.6f;

        // =====================================================================
        //  DISTURBANCE
        // =====================================================================

        [Header("Disturbance")]

        [Tooltip("On, fire history shapes the stand: species killed by fire are absent " +
                 "from recently burnt ground, and serotinous species need fire to be " +
                 "present at all.")]
        public bool enableFire = false;

        [Tooltip("Years between fires on typical ground. Mediterranean and boreal " +
                 "systems run 20 to 60; temperate rainforest runs into the thousands. " +
                 "Aridity shortens it locally.")]
        public float fireReturnBaseYears = 120f;

        [Tooltip("On, grazing pressure keeps meadows open by suppressing the species " +
                 "that cannot tolerate being browsed.")]
        public bool enableBrowse = false;

        [Tooltip("Baseline grazing and browsing pressure. Productive lowland carries " +
                 "more herbivores than bare high ground, and the sampler varies this " +
                 "with productivity.")]
        [Range(0f, 1f)]
        public float browsePressureBase = 0.25f;

        [Tooltip("On, snow load limits which crowns survive. This is why conifers " +
                 "dominate snow country: a broad crown breaks under a load a narrow " +
                 "one sheds.")]
        public bool enableSnowLoad = true;

        [Tooltip("Kilograms per square metre of peak snow load at the treeline, " +
                 "scaled down to zero at the altitude where snow no longer settles.")]
        public float snowLoadAtTreelineKgPerM2 = 250f;

        [Tooltip("On, steep chutes are scoured clear of anything that cannot tolerate " +
                 "avalanche, which is what makes a chute read as a chute rather than " +
                 "as an ordinary steep slope.")]
        public bool enableAvalanche = true;

        [Tooltip("Degrees. Slope above which avalanche scour is possible. Below about " +
                 "25 the snowpack holds; above about 50 it sheds before it can build.")]
        [Range(0f, 90f)]
        [HideInInspector] // UC-T08-15: current avalanche field uses its verified slope contract, not this copied threshold.
        public float avalancheMinSlopeDegrees = 28f;

        [Tooltip("On, exposure past a species' wind limit dwarfs it rather than " +
                 "removing it, giving the wind-flagged krummholz scrub of a real " +
                 "treeline instead of a hard line with nothing above it.")]
        public bool enableKrummholz = true;

        [Tooltip("0 leaves every plant at its mature size (one age, the synthetic look). " +
                 "Above 0, each stand gets a spread of ages - many saplings, fewer mature " +
                 "trees (a reverse-J age structure, as a real forest has) - applied as a size " +
                 "multiplier so a wood reads as generations, not a single planting. This is the " +
                 "cheap, single-pass stand-in for Unreal's multi-step growth simulation.")]
        [Range(0f, 1f)]
        public float ageSizeVariation = 0f;

        [Tooltip("Size of the youngest seedling as a fraction of its mature size, when Age " +
                 "Size Variation is on. 0.2 means saplings start at a fifth of full size and " +
                 "grow toward 1.")]
        [Range(0.05f, 1f)]
        public float seedlingSizeFraction = 0.2f;

        [Tooltip("VEG-8 anti-repeat radius, metres. Above 0 (with mixed stands on) a site's species " +
                 "is biased against whatever was just placed within this radius, so neighbouring " +
                 "plants vary instead of repeating the same slot. 0 (default) keeps the independent " +
                 "per-site pick, so existing worlds are unchanged.")]
        [Min(0f)]
        public float antiRepeatRadiusM = 0f;

        [Tooltip("How hard a just-placed neighbour's species is penalised at the next site, 0-1.")]
        [Range(0f, 1f)]
        public float antiRepeatStrength = 0.6f;

        [Tooltip("Assert bare ground where a named cause removes it - permanent snow, scree " +
                 "above the slope limit, a saline pan, a scoured channel or crest, a burn scar - " +
                 "instead of letting vegetation merely fade out. This is what gives a mountaintop " +
                 "a snowline you can point at rather than a soft thinning. Off (the default) leaves " +
                 "placement exactly as before.")]
        public bool assertBareGround = false;

        // =====================================================================
        //  SEASON
        // =====================================================================

        [Header("Season")]

        [Tooltip("On, foliage colour, leaf cover and grass height follow the season. " +
                 "Budburst fires on accumulated warmth and autumn on day length, " +
                 "which is why a cold spring is genuinely late while autumn is not.")]
        public bool enableSeasonalPhenology = false;

        [Tooltip("On, the season is read from the weather manager's clock, so the whole " +
                 "world agrees on what month it is. Off uses the day of year below, " +
                 "for authoring a fixed season.")]
        public bool seasonFollowsWeather = true;

        [Tooltip("Day of year used when the season does not follow the weather clock. " +
                 "1 is January 1st in the northern hemisphere, and the southern " +
                 "hemisphere is handled from latitude rather than authored separately.")]
        [Range(1, 366)]
        public int manualDayOfYear = 172;

        [Tooltip("Seconds between ecology evaluations. Phenology and climate are far too " +
                 "slow to be worth solving every frame, so they run on this tick and " +
                 "interpolate between ticks. 4 s is the shipped default.")]
        [Min(0.1f)]
        public float ecologyTickSeconds = 4f;

        [Tooltip("UC-T06-09: simulated days between native vegetation colour refreshes; placement stays fixed. " +
                 "In days rather than ticks, so the same value " +
                 "behaves the same way at any year length.")]
        [Min(0.5f)]
        public float seasonRefreshDays = 5f;

        // =====================================================================
        //  BUDGET
        // =====================================================================

        [Header("Budget")]

        [Tooltip("Hard ceiling on instances placed in one cell, across every species " +
                 "and layer. The backstop that keeps a rich envelope from becoming a " +
                 "frame-time cliff on a mid-range phone. -1 removes the ceiling.")]
        public int maxInstancesPerCell = 20000;

        /// <summary>T7: the per-cell ceiling the resolver caps to on a mobile platform,
        /// so an authored 20000 (or -1 "unlimited") can never OOM the minimum target
        /// device. Desktop keeps the authored value.</summary>
        public const int MobileMaxInstancesPerCell = 4000;

        /// <summary>Caps an authored per-cell instance ceiling to the mobile floor:
        /// -1 (unlimited) becomes the cap, any larger value is clamped down, a smaller
        /// one is kept. Pure so the mobile guard is unit-testable off-device.</summary>
        public static int MobileCappedInstancesPerCell(int authored)
        {
            return authored < 0 ? MobileMaxInstancesPerCell : Mathf.Min(authored, MobileMaxInstancesPerCell);
        }

        [Tooltip("Metres beyond which vegetation stops casting shadows. The highest " +
                 "value dial on this component for mobile: a shadow caster is drawn " +
                 "again per cascade, and distant casters cost most to keep.")]
        public float shadowCasterDistanceMetres = 60f;

        [Tooltip("Candidate sites evaluated per square metre before suitability " +
                 "rejects any of them. Higher finds better sites and costs linearly " +
                 "more; this is the sampler's cost dial.")]
        [Range(0.01f, 4f)]
        public float candidatesPerSquareMetre = 0.25f;

        // =====================================================================
        //  DEVICE PRESET (V-33)
        // =====================================================================

        [Header("Device Preset")]

        // A preset is a starting point, not a lock. It writes the dials once and
        // then gets out of the way: every field it touched stays editable, and the
        // inspector says "(modified)" once one of them diverges. The alternative -
        // a preset that keeps reasserting itself - is the thing authors hate,
        // because it silently undoes the tuning they just did.
        [Tooltip("Which device this world's vegetation budget is aimed at. Choose one " +
                 "and press Apply; the numbers become ordinary editable fields.")]
        public VegetationQuality qualityPreset = VegetationQuality.Desktop;

        [Tooltip("Checksum of the dials as the preset last wrote them. Divergence is " +
                 "what makes the inspector say (modified). Not authored by hand.")]
        [HideInInspector]
        public int appliedPresetChecksum;

        [Tooltip("Whether a preset has ever been applied. Without this a fresh " +
                 "component would read as (modified) against a preset nobody chose.")]
        [HideInInspector]
        public bool hasAppliedPreset;

        // The Apply itself, the dial table it writes, the checksum and the
        // "(modified)" test all live together in the Resolve partial of this same
        // class (UCVegetationSystemManager.Resolve.cs, "DEVICE PRESETS (V-33)"):
        // DialsFor, ApplyQualityPreset, PresetDialChecksum, PresetModified and
        // MatchesPreset. They are deliberately NOT repeated here.
        //
        // DEF-5 note, kept as a warning rather than deleted. A second copy of that
        // whole mechanism was written here, on the strength of a grep of THIS FILE
        // that found qualityPreset with no Apply anywhere near it. The class is
        // PARTIAL, so "no Apply in this file" proved nothing. The compiler caught it
        // only because one of the five names happened to collide; the other four
        // would have compiled. And the duplicate wrote a DIFFERENT set of dials from
        // the set PresetDialChecksum hashes, so "(modified)" would have answered for
        // the wrong fields from the first time either one ran. Grep the TYPE, not the
        // file, before concluding a member does not exist.

        // =====================================================================
        //  HIERARCHY
        // =====================================================================

        [Header("Hierarchy")]

        /// <summary>UC-T06-12: selects the generated objects' hierarchy parent; exact region parking/retirement applies to either parent.</summary>
        [Tooltip("Parent scattered props under this component instead of their terrain cell. " +
                 "Each prop keeps its exact world/region ownership: unloading still hides its rendering and collision, " +
                 "and recycling releases it. Off retains the per-terrain hierarchy.")]
        public bool parentUnderVegetationSystem = false;

        // =====================================================================
        //  DEBUG
        // =====================================================================

        [Header("Debug")]

        [Tooltip("On, the scene view shades the ground by the suitability of the " +
                 "species selected below, which is how an author sees why a species " +
                 "is absent rather than guessing at it.")]
        [HideInInspector] // UC-T08-15: no scene-overlay consumer; actual density/rejection diagnostics remain in the inspector.
        public bool debugDrawSuitability = false;

        [Tooltip("Index into the species library of the species the suitability " +
                 "overlay draws. -1 draws the winning species at each site instead.")]
        [HideInInspector]
        public int debugSpeciesIndex = -1;

        [Tooltip("On, the overlay reports which single factor is limiting each site " +
                 "rather than the combined score. This answers the only question an " +
                 "author actually has, which is what is stopping it growing here.")]
        [HideInInspector]
        public bool debugShowLimitingFactor = false;

        /// <summary>The world this vegetation belongs to, or null when loose in a scene.</summary>
        /// <remarks>UC-T06-12/18: includes inactive/staged ancestors so parking and authoring retain the same explicit owner.</remarks>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(true); }
        }

        private void OnValidate()
        {
            globalDensityScale = Mathf.Clamp(globalDensityScale, 0f, 2f);
            grassPerSquareMetre = Mathf.Max(0f, grassPerSquareMetre);

            // Unity requires the detail resolution to divide evenly into patches, so
            // the two are clamped together rather than independently.
            detailResolutionPerPatch = Mathf.Clamp(detailResolutionPerPatch, 8, 128);
            detailResolution = Mathf.Clamp(detailResolution, 0, 4096);

            treesPerHectare = Mathf.Max(0f, treesPerHectare);
            billboardFadeLengthMetres = Mathf.Max(0f, billboardFadeLengthMetres);
            treeSpacingMetres = Mathf.Max(0f, treeSpacingMetres);

            minAltitudeMetres = Mathf.Min(minAltitudeMetres, maxAltitudeMetres);
            clearanceMetres = Mathf.Max(0f, clearanceMetres);
            biomeBlendMetres = Mathf.Max(0f, biomeBlendMetres);
            maxConcurrentCells = Mathf.Clamp(maxConcurrentCells, 1, 16);

            // ---- Ecology ----------------------------------------------------

            suitabilityThreshold = Mathf.Clamp01(suitabilityThreshold);
            suitabilitySharpness = Mathf.Clamp(suitabilitySharpness, 0.25f, 4f);
            maxSpeciesPerSite = Mathf.Clamp(maxSpeciesPerSite, 1, 8);

            // A search radius of zero would report every site as coastal, because
            // "no body found" reports the radius itself as the distance.
            waterSearchRadiusMetres = Mathf.Max(1f, waterSearchRadiusMetres);

            soilDepthMaxMetres = Mathf.Max(0f, soilDepthMaxMetres);
            soilAngleOfReposeDegrees = Mathf.Clamp(soilAngleOfReposeDegrees, 1f, 89f);
            fertilityFromCurvature = Mathf.Clamp01(fertilityFromCurvature);
            wetnessCatchmentRadiusMetres = Mathf.Max(1f, wetnessCatchmentRadiusMetres);
            saltSprayDecayMetres = Mathf.Max(1f, saltSprayDecayMetres);

            canopyLayerPasses = Mathf.Clamp(canopyLayerPasses, 1, 4);
            understoryLightFloor = Mathf.Clamp01(understoryLightFloor);

            dispersalSeedsPerHectare = Mathf.Max(0f, dispersalSeedsPerHectare);
            clusterContrast = Mathf.Clamp01(clusterContrast);

            // One year is the shortest interval that still means "a fire history"
            // rather than permanent burning.
            fireReturnBaseYears = Mathf.Max(1f, fireReturnBaseYears);
            browsePressureBase = Mathf.Clamp01(browsePressureBase);
            snowLoadAtTreelineKgPerM2 = Mathf.Max(0f, snowLoadAtTreelineKgPerM2);
            avalancheMinSlopeDegrees = Mathf.Clamp(avalancheMinSlopeDegrees, 0f, 90f);

            manualDayOfYear = Mathf.Clamp(manualDayOfYear, 1, 366);
            ecologyTickSeconds = Mathf.Max(0.1f, ecologyTickSeconds);
            seasonRefreshDays = Mathf.Max(0.5f, seasonRefreshDays);

            // -1 is the documented "no ceiling" sentinel, so it must survive the
            // clamp that otherwise forces a positive count.
            if (maxInstancesPerCell != -1)
                maxInstancesPerCell = Mathf.Max(0, maxInstancesPerCell);

            shadowCasterDistanceMetres = Mathf.Max(0f, shadowCasterDistanceMetres);
            candidatesPerSquareMetre = Mathf.Clamp(candidatesPerSquareMetre, 0.01f, 4f);
            detailMemoryCeilingMB = Mathf.Max(1f, detailMemoryCeilingMB);

            // Every field above feeds the resolved snapshot, which is computed once
            // and reused per cell. Clamping a value without dropping that snapshot
            // would leave the scatter pass building from the number the author just
            // corrected.
            MarkSettingsDirty();
            MarkAppearanceDirty(); // UC-T06-09: inspector appearance changes reach existing consumers without graph execution.

            // The four LOD figures are only inert until something writes them
            // through to the one place the Terrain reads. Doing it here means an
            // edit reaches the terrain on the next rebuild rather than needing a
            // second action nobody would guess at. (V-31)
            ApplyLodToWorld();

            // Update() no longer publishes the season shader globals every frame in the
            // scene view (§36), so republish them once here when an inspector value moves
            // and once in OnEnable (Season.cs) after a reload - enough to keep the
            // edit-mode tint correct without a per-frame heartbeat.
            if (isActiveAndEnabled && vegetationEnabled) PublishShaderGlobals();
        }
    }
}
