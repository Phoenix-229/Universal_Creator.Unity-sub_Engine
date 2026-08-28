// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    EcoEnvelope.cs
//  Purpose: The authored ecology of one species. Ten groups, every field an input
//           to a named law, every field carrying its unit.
//
//  WHAT THIS IS FOR
//  ----------------
//  This is the author's control surface over where a species can live. It is not
//  a set of look-right multipliers: each field feeds a law, and the laws decide
//  placement. Change tempOptimumC and a forest migrates up or down a mountain
//  because the thermal tolerance curve moved, not because a density slider did.
//
//  ONE CLASS, REFERENCED BY FOUR PROTOTYPES
//  ----------------------------------------
//  TreePrototypeDef, ShrubPrototypeDef, DetailPrototypeDef and ObjectPrototypeDef
//  each hold one of these. Their existing rendering and engine fields are
//  untouched: this is ecology, added beside them, never a replacement for them.
//
//  DEFAULTS ARE A TEMPERATE GENERALIST THAT WILL ACTUALLY APPEAR
//  ------------------------------------------------------------
//  An envelope defaulting to "nothing grows" makes every newly added prototype
//  look broken, and the author's first hour goes on finding out why. So a fresh
//  envelope is a mid-succession temperate tree that establishes over a wide band.
//
//  SENTINEL CONVENTION
//  -------------------
//  A value of -1 on a range limit means "ignore this limit", consistently, on
//  every field where it appears. It is never a magnitude.
//
//  SERIALISATION
//  -------------
//  Unity serialises by field name. Fields may be appended; renaming one silently
//  discards every value an author has entered for it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// The authored ecological envelope of one species. Read by SpeciesSuitability
    /// to score a site; filled from an archetype by SpeciesPresets.
    /// </summary>
    [System.Serializable]
    public class EcoEnvelope
    {
        // =====================================================================
        //  A - Identity and abundance
        // =====================================================================

        [Header("A - Identity")]

        [Tooltip("Name shown in the inspector, the debug window and the species picker. " +
                 "A list of eight identical prefab slots is unusable without this.")]
        public string displayName = "";

        [Tooltip("What kind of organism this is. Decides which landform assemblages the species " +
                 "is eligible for at all, and what a collider policy of Auto resolves to.")]
        public LifeForm lifeForm = LifeForm.Tree;

        [Tooltip("Relative abundance among species already found admissible at a site. " +
                 "This is never a placement probability on its own: the environment decides whether " +
                 "a species can be here, and only then does weight decide which of the survivors lands.")]
        [Min(0f)]
        public float weight = 1f;

        [Tooltip("Where in a succession sequence this species belongs. Pioneers colonise disturbed " +
                 "ground; climax species need a site that has been left alone.")]
        public SuccessionStage successionStage = SuccessionStage.Mid;

        [Tooltip("Off removes the species from placement without discarding a minute of tuning.")]
        public bool enabled = true;

        // =====================================================================
        //  B - Thermal envelope        L-04, L-15, L-22, L-24, L-26
        // =====================================================================

        [Header("B - Temperature")]

        [Tooltip("degC. Peak of the thermal tolerance curve: the annual mean temperature this " +
                 "species does best at. (L-15)")]
        public float tempOptimumC = 12f;

        [Tooltip("degC half-width of the thermal curve. Wide is a generalist, narrow is a " +
                 "specialist that appears only in a band. (L-15)")]
        [Min(0f)]
        public float tempToleranceC = 12f;

        [Tooltip("degC. Below this the species is absent, not merely rare. A hard reject, " +
                 "evaluated before any curve.")]
        public float tempLethalMinC = -40f;

        [Tooltip("degC. Upper lethal limit. A hard reject.")]
        public float tempLethalMaxC = 50f;

        [Tooltip("degC. The treeline. Koerner's threshold: trees fail where the warmest month " +
                 "mean falls below about 6.4 degC. This is why a real mountain goes bare at an " +
                 "altitude instead of thinning out. Set 0 or below to disable. (L-22)")]
        public float warmestMonthMinC = 6.4f;

        [Tooltip("0-1 share of the year that must be frost free. Above 0 this gates evergreen " +
                 "broadleaf and palms out of frost climates. (L-26)")]
        [Range(0f, 1f)]
        public float frostFreeFractionMin = 0f;

        [Tooltip("degC base for growing degree days: 5 for temperate and cool-season, 10 for " +
                 "warm-season species. (L-24)")]
        public float gddBaseC = 5f;

        [Tooltip("degC-day of accumulated warmth the species needs to complete a season. " +
                 "A summer too short to leaf out excludes it entirely. 0 disables. (L-24)")]
        [Min(0f)]
        public float gddMinRequired = 0f;

        // =====================================================================
        //  C - Water envelope         L-12, L-13, L-14, L-15
        // =====================================================================

        [Header("C - Water")]

        [Tooltip("0-1 preferred plant-available soil moisture. (L-15)")]
        [Range(0f, 1f)]
        public float moistureOptimum = 0.5f;

        [Tooltip("0-1 half-width of the moisture curve. (L-15)")]
        [Range(0f, 1f)]
        public float moistureTolerance = 0.25f;

        [Tooltip("mm/yr preferred annual precipitation, against the orographic climate field. (L-15)")]
        [Min(0f)]
        public float precipOptimumMm = 800f;

        [Tooltip("mm/yr half-width of the precipitation curve. (L-15)")]
        [Min(0f)]
        public float precipToleranceMm = 500f;

        [Tooltip("Aridity index (precipitation over PET) below which the site is too dry. " +
                 "Under 0.2 is desert, 0.2-0.5 semi-arid, above 0.65 humid. (L-14)")]
        [Min(0f)]
        public float aridityMin = 0.2f;

        [Tooltip("Aridity index above which the site is too wet for this species. (L-14)")]
        [Min(0f)]
        public float aridityMax = 4f;

        [Tooltip("Preferred topographic wetness, ln(a / tan beta). Low prefers ridges that shed " +
                 "water, high prefers hollows that collect it. (L-12)")]
        public float twiOptimum = 6f;

        [Tooltip("Half-width of the wetness curve, in ln units. (L-12, L-15)")]
        [Min(0f)]
        public float twiTolerance = 3f;

        [Tooltip("m. Positive means the species needs groundwater within this depth of the " +
                 "surface: willow, alder, reed. -1 ignores the water table entirely.")]
        public float waterTableMaxDepthM = -1f;

        [Tooltip("0-1 tolerance of seasonal inundation. Low means standing water kills it.")]
        [Range(0f, 1f)]
        public float floodTolerance = 0.1f;

        [Tooltip("0-1 drought tolerance. This raises the floor of the moisture curve rather than " +
                 "moving its peak, because a drought-tolerant species survives drought - it does " +
                 "not prefer it.")]
        [Range(0f, 1f)]
        public float droughtTolerance = 0.3f;

        // =====================================================================
        //  D - Water bodies and coast        L-25
        // =====================================================================

        [Header("D - Shorelines and salt")]

        [Tooltip("m keep-out band from the water's edge. Non-zero is what stops trees standing " +
                 "in the surf.")]
        [Min(0f)]
        public float distanceToWaterMinM = 0f;

        [Tooltip("m. Positive makes a riparian gallery: a species that exists only in a band " +
                 "along rivers and lake margins. -1 ignores distance to water.")]
        public float distanceToWaterMaxM = -1f;

        [Tooltip("0-1 salt tolerance. With the 120 m spray decay this produces the real coastal " +
                 "banding: salt scrub, then pines, then inland forest. (L-25)")]
        [Range(0f, 1f)]
        public float saltTolerance = 0f;

        [Tooltip("Band defined by tide rather than by altitude. Mangrove and saltmarsh are " +
                 "intertidal; salt-pruned scrub and dune grass are supratidal.")]
        public TidalZone tidalZone = TidalZone.None;

        [Tooltip("m below the water surface this species occupies, min to max. Kelp, seagrass and " +
                 "lily. Leave at (0, 0) for anything that lives on dry land.")]
        public Vector2 submergedDepthRangeM = Vector2.zero;

        // =====================================================================
        //  E - Terrain envelope        L-15, L-19, L-23
        // =====================================================================

        [Header("E - Terrain")]

        [Tooltip("On switches the altitude gate from the prototype's normalised 0-1 band to real " +
                 "metres below. Off keeps the shipped behaviour exactly, so an existing world does " +
                 "not change appearance on upgrade.")]
        public bool useAbsoluteAltitude = false;

        [Tooltip("m above sea level, lower limit. Real metres matter because the lapse rate is in " +
                 "degC per km, so an altitude band is a temperature band.")]
        public float minAltitudeM = 0f;

        [Tooltip("m above sea level, upper limit.")]
        public float maxAltitudeM = 3000f;

        [Tooltip("deg. Preferred slope. Most species prefer gentle ground; the prototype's " +
                 "existing maxSlope only rejects, it does not prefer. (L-15)")]
        [Range(0f, 90f)]
        public float slopeOptimumDeg = 8f;

        [Tooltip("deg half-width of the slope curve. (L-15)")]
        [Range(0f, 90f)]
        public float slopeToleranceDeg = 20f;

        [Tooltip("deg compass azimuth this species prefers to face, or -1 for no preference. " +
                 "Hemisphere aware: the warm slope is equator-facing, so the sign flips across " +
                 "the equator and the system handles that for you.")]
        public float aspectPreferenceDeg = -1f;

        [Tooltip("0-1 how strongly aspect biases suitability. 0 is no effect even with a " +
                 "preference set.")]
        [Range(0f, 1f)]
        public float aspectStrength = 0.5f;

        [Tooltip("-1 prefers concave hollows that collect water and soil, +1 prefers convex " +
                 "ridges that shed both, 0 is indifferent.")]
        [Range(-1f, 1f)]
        public float curvaturePreference = 0f;

        [Tooltip("m of soil this species needs to root in. Derived from slope and curvature per " +
                 "cell, never authored per cell.")]
        [Min(0f)]
        public float soilDepthMinM = 0.1f;

        [Tooltip("0-1 preferred soil fertility. (L-15)")]
        [Range(0f, 1f)]
        public float soilFertilityOptimum = 0.5f;

        [Tooltip("0-1 half-width of the fertility curve. (L-15)")]
        [Range(0f, 1f)]
        public float soilFertilityTolerance = 0.35f;

        [Tooltip("0-1 upper limit on exposed rock and loose stone. Above this the site is scree, " +
                 "and the landform rules place stone there instead. (L-23)")]
        [Range(0f, 1f)]
        public float rockinessMax = 0.7f;

        // =====================================================================
        //  F - Light and competition        L-16, L-17, L-21
        // =====================================================================

        [Header("F - Light and competition")]

        [Tooltip("0-1 minimum light fraction this species survives under a canopy. This is the " +
                 "field that builds a layered forest instead of one species mat: a low value can " +
                 "live in shade, a high value cannot. (L-17)")]
        [Range(0f, 1f)]
        public float shadeTolerance = 0.3f;

        [Tooltip("Which vertical layer this species occupies, so it draws from the right light " +
                 "budget and reduces the right one.")]
        public CanopyLayer canopyLayer = CanopyLayer.Canopy;

        [Tooltip("m2/m2 leaf area this species adds above whatever is beneath it. What it does to " +
                 "the light below. (L-17)")]
        [Min(0f)]
        public float laiContribution = 2.5f;

        [Tooltip("m dispersal kernel scale. Small clumps into groves with gaps between; large " +
                 "spreads evenly. The single field that makes a stand read as grown rather than " +
                 "sprinkled. (L-21)")]
        [Min(0f)]
        public float dispersalLambdaM = 25f;

        [Tooltip("m radius in which this species chemically suppresses neighbours, producing a " +
                 "bare halo. 0 for none, which is almost everything.")]
        [Min(0f)]
        public float allelopathyRadiusM = 0f;

        // =====================================================================
        //  G - Structure and allometry        L-18, L-19, L-20
        // =====================================================================

        [Header("G - Structure")]

        [Tooltip("m mature height, min to max. Real metres, and the source of the prototype's " +
                 "height scale rather than a free-floating multiplier.")]
        public Vector2 matureHeightM = new Vector2(12f, 22f);

        [Tooltip("m trunk diameter at breast height, min to max. Drives both crown radius and " +
                 "self-thinning, so it decides spacing. (L-18, L-19)")]
        public Vector2 dbhRangeM = new Vector2(0.25f, 0.6f);

        [Tooltip("Crown radius coefficient a in r = a * DBH^b. About 1.9 for broadleaf, lower for " +
                 "a narrow conifer. Spacing follows crown geometry, not taste. (L-19)")]
        [Min(0f)]
        public float crownAllometryA = 1.9f;

        [Tooltip("Crown radius exponent b in r = a * DBH^b. About 0.6. (L-19)")]
        [Min(0f)]
        public float crownAllometryB = 0.6f;

        [Tooltip("m radius of the root plate. Keeps trees off ground too thin to hold them.")]
        [Min(0f)]
        public float rootPlateRadiusM = 2f;

        [Tooltip("0-1 how much the stem bends in wind. Feeds the existing wind bend factor from a " +
                 "physical quantity instead of a tuned number.")]
        [Range(0f, 1f)]
        public float trunkFlexibility = 0.35f;

        [Tooltip("Reineke coefficient: stems per hectare allowed at 1 m mean diameter. Caps stand " +
                 "density so a mature forest cannot read as a plantation. (L-18)")]
        [Min(0f)]
        public float selfThinningK = 25000f;

        // =====================================================================
        //  H - Disturbance        L-23
        // =====================================================================

        [Header("H - Disturbance")]

        [Tooltip("What fire does to this species. Serotinous species require fire to reproduce, " +
                 "so long-unburnt ground has none of them.")]
        public FireResponse fireResponse = FireResponse.Killed;

        [Tooltip("0-1 topographic wind exposure above which the species is absent. This is the " +
                 "reason ridgelines are bare while the slope below them is wooded.")]
        [Range(0f, 1f)]
        public float windExposureMax = 0.8f;

        [Tooltip("On, exposure past the limit dwarfs the species instead of removing it: the " +
                 "wind-flagged krummholz scrub of a real treeline.")]
        public bool krummholzAboveExposure = false;

        [Tooltip("0-1 height multiplier applied when dwarfed by exposure.")]
        [Range(0f, 1f)]
        public float krummholzHeightScale = 0.3f;

        [Tooltip("kg/m2 peak snow load the crown survives. Why conifers dominate snow country: a " +
                 "broad crown breaks under load that a narrow one sheds.")]
        [Min(0f)]
        public float snowLoadMaxKgPerM2 = 200f;

        [Tooltip("0-1 tolerance of avalanche scour. Low keeps the species out of steep chutes, " +
                 "which is what makes a chute read as a chute.")]
        [Range(0f, 1f)]
        public float avalancheTolerance = 0.2f;

        [Tooltip("0-1 tolerance of grazing and browsing. Low keeps a species out of pressured " +
                 "ground, which is what keeps a meadow open.")]
        [Range(0f, 1f)]
        public float browseTolerance = 0.5f;

        // =====================================================================
        //  I - Phenology and season        L-03, L-24
        // =====================================================================

        [Header("I - Season")]

        [Tooltip("On drops its leaves. Off is evergreen.")]
        public bool deciduous = false;

        [Tooltip("degC-day of accumulated warmth at which leaves open. Budburst fires on warmth, " +
                 "not on a calendar date, which is why a cold spring is genuinely late. (L-24)")]
        [Min(0f)]
        public float budburstGdd = 200f;

        [Tooltip("h. Day length at which autumn colour begins. Photoperiod is the real trigger, " +
                 "which is why autumn is reliable while spring is not. (L-03)")]
        [Range(0f, 24f)]
        public float senescenceDayLengthH = 11.5f;

        [Tooltip("Foliage colour at leaf-out. New leaves are lighter and yellower than mature ones.")]
        public Color springColour = new Color(0.45f, 0.65f, 0.25f, 1f);

        [Tooltip("Foliage colour at full season.")]
        public Color summerColour = new Color(0.25f, 0.45f, 0.16f, 1f);

        [Tooltip("Foliage colour after senescence.")]
        public Color autumnColour = new Color(0.70f, 0.48f, 0.13f, 1f);

        [Tooltip("Foliage colour when dormant.")]
        public Color winterColour = new Color(0.36f, 0.31f, 0.24f, 1f);

        [Tooltip("0-1 how bare it goes out of season. 1 is a bare stick, 0 keeps full foliage.")]
        [Range(0f, 1f)]
        public float leafOffFraction = 0.85f;

        [Tooltip("0-1 how much snow the crown holds before shedding it.")]
        [Range(0f, 1f)]
        public float snowCanopyAccumulation = 0.5f;

        [Tooltip("0-1 how much the foliage darkens when wet, coupling to the shipped surface " +
                 "wetness term.")]
        [Range(0f, 1f)]
        public float wetDarkening = 0.4f;

        [Tooltip("Height multiplier at midwinter (x) and midsummer (y). Grass genuinely dies " +
                 "back; a full grass field in January is the tell that nothing seasonal is wired.")]
        public Vector2 seasonalHeightScale = new Vector2(0.55f, 1f);

        // =====================================================================
        //  J - Rendering and budget
        // =====================================================================

        [Header("J - Rendering and budget")]

        [Tooltip("Per-species override of GPU instancing, above the library's global threshold.")]
        public InstancingPolicy instancingPolicy = InstancingPolicy.WhenDense;

        [Tooltip("What collider a spawned instance carries. Auto resolves from the life form: " +
                 "grass none, shrub trigger, tree and stone solid.")]
        public ColliderPolicy colliderPolicy = ColliderPolicy.Auto;

        [Tooltip("m at which this species crossfades to a billboard or impostor. -1 uses the " +
                 "library default.")]
        public float billboardStartM = -1f;

        [Tooltip("m beyond which this species stops casting shadows. The highest-value mobile " +
                 "dial here: a shadow caster is drawn again per cascade, and distant casters are " +
                 "nearly free to remove visually.")]
        [Min(0f)]
        public float shadowCastDistanceM = 60f;

        [Tooltip("Per-species ceiling on instances in one chunk, under the library's chunk " +
                 "budget. -1 uses the library budget.")]
        public int maxPerChunk = -1;

        [Tooltip("LOD bias for this species. Above 1 holds detail further out.")]
        [Range(0.25f, 4f)]
        public float lodBias = 1f;

        [Tooltip("Density multiplier for this species alone, so one species can be trimmed " +
                 "without retuning the whole field.")]
        [Range(0f, 4f)]
        public float detailDensityScale = 1f;

        // =====================================================================
        //  K - Biome
        // =====================================================================
        //
        //  Everything above answers "can this species survive the conditions here".
        //  This group answers a different question: "does it belong to the community
        //  that lives here". Both are needed, because climate alone puts spruce in
        //  half the temperate world - the envelope is what the plant tolerates, the
        //  biome is who its neighbours are. Left empty, the group says nothing and
        //  only the tolerances decide.

        [Header("K - Biome")]

        [Tooltip("Bitmask of biomes this species is eligible in, tested against the biome " +
                 "field the classifier bakes. 0 means no restriction - so does having every " +
                 "biome ticked. Use the mask to keep mangrove out of the tundra; use the " +
                 "tolerances above for anything gradual.")]
        public int allowedBiomes = 0;

        [Tooltip("Optional relative abundance per biome index, for a species that lives in " +
                 "several but is commoner in one. Indexed by biome; a missing or short array " +
                 "reads as 1. Multiplies the mask, so a weight cannot readmit a vetoed biome.")]
        public float[] perBiomeWeight;

        // =====================================================================
        //  Provenance - inspector bookkeeping, not ecology
        // =====================================================================

        [Header("Provenance")]

        [Tooltip("Archetype this envelope was filled from, if any. Read-only bookkeeping.")]
        public string presetName = "";

        [Tooltip("Checksum of the values at the moment the preset was applied, so the inspector " +
                 "can say (modified) once an author has drifted from the archetype. Read-only.")]
        public int presetChecksum = 0;

        // =====================================================================
        //  Routing - how one instance of this species reaches the engine (V-27)
        // =====================================================================

        /// <summary>
        /// Turns ColliderPolicy.Auto into a concrete kind from the life form. Auto is the
        /// default, so this is the path almost every species takes.
        /// </summary>
        public ColliderPolicy ResolveColliderPolicy()
        {
            if (colliderPolicy != ColliderPolicy.Auto) return colliderPolicy;

            switch (lifeForm)
            {
                // Walked over, not into. Thousands of colliders for something that offers
                // no resistance is the worst trade in scatter.
                case LifeForm.Graminoid:
                case LifeForm.Herb:
                case LifeForm.Fern:
                case LifeForm.Moss:
                case LifeForm.Lichen:
                case LifeForm.Aquatic:
                    return ColliderPolicy.None;

                // Walked through: the plant yields and reports the brush-through. A
                // succulent sits here on stature rather than on texture - it is shrub
                // sized, and a knee-high cactus that hard-stops a character reads as a wall.
                case LifeForm.Shrub:
                case LifeForm.Succulent:
                    return ColliderPolicy.Trigger;

                // Trunks and stone stop a body: Tree, Palm, Abiotic.
                default:
                    return ColliderPolicy.Solid;
            }
        }

        /// <summary>
        /// Decides the two things ScatterApplier needs per instance: what collider it
        /// carries, and whether it is submitted as an instanced draw instead of a
        /// GameObject. One method because the two answers are not independent - an
        /// instanced draw cannot hold a collider, so deciding them apart is how a
        /// species silently loses its collision.
        /// </summary>
        /// <param name="countInChunk">This prototype's own count in the chunk, not the
        /// chunk total: "dense" has to mean dense in this species, or twenty prototypes
        /// of twenty instances each would all batch on the strength of a crowd none of
        /// them is in.</param>
        /// <param name="hierarchyThreshold">The library's gpuInstancingThreshold.</param>
        /// <param name="colliderBudgetRemaining">Whether the channel's collider budget
        /// still has room for one more.</param>
        public void ResolveRouting(int countInChunk, int hierarchyThreshold,
                                   bool colliderBudgetRemaining,
                                   out ColliderPolicy collider, out bool instanced)
        {
            ColliderPolicy wanted = ResolveColliderPolicy();

            // The library threshold is a hierarchy guard, not a preference. Past it a
            // chunk is adding thousands of transforms, and Unity's scene graph is what
            // gives out first - so it overrides the collider policy, which is exactly
            // what the shipped tooltip has always promised ("regardless of the
            // per-prototype flag").
            bool overThreshold = countInChunk > Mathf.Max(1, hierarchyThreshold);

            // An explicit Always is an author instruction, so it may drop a collider the
            // life form merely implied. It may not drop one the author asked for by name:
            // silently removing collision is the one failure here that reaches a player
            // as falling through a boulder.
            bool authorForcedInstancing = instancingPolicy == InstancingPolicy.Always
                                       && colliderPolicy == ColliderPolicy.Auto;

            bool carries = wanted != ColliderPolicy.None
                        && colliderBudgetRemaining
                        && !overThreshold
                        && !authorForcedInstancing;

            collider = carries ? wanted : ColliderPolicy.None;

            if (instancingPolicy == InstancingPolicy.Never)
            {
                instanced = false;
                return;
            }

            // A GameObject earns its transform by carrying a collider. Once it is not
            // carrying one there is nothing left on it that an instanced draw does not
            // do more cheaply - the wind and season response are shader side either way.
            instanced = !carries;
        }

        // ---------------------------------------------------------------------
        //  Draw distance and shadows (V-31, V-32). The other half of group J.
        //  Both resolve against a global budget rather than standing alone,
        //  because a species may only ever ask for *less* than the device can
        //  afford: a per-species value that could raise the global ceiling is a
        //  per-species value that breaks the device preset the moment someone
        //  authors an enthusiastic number.
        // ---------------------------------------------------------------------

        /// <summary>
        /// m at which this species starts crossfading to a billboard. -1 defers to
        /// the world's figure, which is the common case and the reason the field
        /// exists at all: only species whose size argues with the default need one.
        /// </summary>
        public float ResolveBillboardStartM(float worldDefaultM)
        {
            return billboardStartM < 0f ? worldDefaultM : billboardStartM;
        }

        /// <summary>
        /// m beyond which this species stops casting. Zero means never casts, and
        /// a zero global budget means nothing casts - a shadow caster is re-drawn
        /// once per cascade, so this is the largest single dial on mobile.
        /// </summary>
        public float ResolveShadowDistanceM(float globalBudgetM)
        {
            if (globalBudgetM <= 0f) return 0f;
            if (shadowCastDistanceM <= 0f) return 0f;
            return Mathf.Min(shadowCastDistanceM, globalBudgetM);
        }

        /// <summary>
        /// What a spawned or instanced draw of this species does about shadows at
        /// the moment it is submitted. TwoSided is never returned: vegetation
        /// cards want their back faces lit, not their shadows doubled.
        /// </summary>
        public UnityEngine.Rendering.ShadowCastingMode ResolveShadowCasting(
            float distanceFromCameraM, float globalBudgetM)
        {
            float limit = ResolveShadowDistanceM(globalBudgetM);
            if (limit <= 0f) return UnityEngine.Rendering.ShadowCastingMode.Off;

            // Negative distance means "not measured" - the caller has no camera to
            // measure against, so the species keeps its shadow rather than losing
            // it to a missing measurement.
            if (distanceFromCameraM >= 0f && distanceFromCameraM > limit)
                return UnityEngine.Rendering.ShadowCastingMode.Off;

            return UnityEngine.Rendering.ShadowCastingMode.On;
        }
    }
}
