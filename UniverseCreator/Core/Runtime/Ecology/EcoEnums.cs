// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    EcoEnums.cs
//  Purpose: The classification enums an authored species envelope uses.
//
//  SERIALISATION RULE
//  ------------------
//  Every one of these is serialised into a saved world as an integer. Members may
//  be appended; they may never be reordered, renumbered or removed, because doing
//  so silently repoints every species an author already tuned. Values are written
//  explicitly so that rule is enforced by reading the file rather than by memory.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// What kind of organism this is. Decides which landform assemblages the
    /// species is eligible for at all, and supplies the sensible defaults a
    /// preset starts from.
    /// </summary>
    public enum LifeForm
    {
        /// <summary>Single woody stem, above about 3 m. Subject to the treeline.</summary>
        Tree = 0,
        /// <summary>Multi-stemmed woody, below about 3 m. Survives above the treeline where trees cannot.</summary>
        Shrub = 1,
        /// <summary>Non-woody broadleaf. Dies back to the ground each winter.</summary>
        Herb = 2,
        /// <summary>Grass and grass-like. The detail-layer workhorse.</summary>
        Graminoid = 3,
        /// <summary>Fern. Shade and humidity specialist; the reason a temperate forest floor is not bare.</summary>
        Fern = 4,
        /// <summary>Moss. No vascular tissue, so it needs standing humidity rather than soil water.</summary>
        Moss = 5,
        /// <summary>Lichen. Colonises bare rock, which is what makes high scree read as old rather than new.</summary>
        Lichen = 6,
        /// <summary>Water-storing stem or leaf. Defines the arid assemblages.</summary>
        Succulent = 7,
        /// <summary>Palm. Monocot, no true bark, so frost is an absolute limit rather than a stress.</summary>
        Palm = 8,
        /// <summary>Rooted or floating aquatic. Placed by submerged depth, not by altitude.</summary>
        Aquatic = 9,
        /// <summary>Not alive: stone, deadfall, driftwood, coral. Uses the same envelope so it lands by the same physics.</summary>
        Abiotic = 10
    }

    /// <summary>
    /// Where in a succession sequence this species belongs. Authored per region as
    /// a state, never simulated over centuries - a convergence loop would make the
    /// world depend on how many passes had run.
    /// </summary>
    public enum SuccessionStage
    {
        /// <summary>First onto bare ground: burn scars, landslides, river gravel, fresh ash.</summary>
        Pioneer = 0,
        /// <summary>Fast, light-demanding, short-lived. Follows the pioneers and is then shaded out.</summary>
        Early = 1,
        /// <summary>The general case, and the default a new species should start as.</summary>
        Mid = 2,
        /// <summary>Slow, shade-tolerant, long-lived. Needs a site that has been undisturbed for a long time.</summary>
        Climax = 3
    }

    /// <summary>
    /// Position relative to the tide, for species whose band is set by water level
    /// rather than by altitude.
    /// </summary>
    public enum TidalZone
    {
        /// <summary>Not tidal. Anything inland or freshwater.</summary>
        None = 0,
        /// <summary>Between low and high water: submerged and exposed twice a day. Mangrove, saltmarsh, rockweed.</summary>
        Intertidal = 1,
        /// <summary>Above high water but inside the splash zone. Salt-pruned scrub and dune grass.</summary>
        Supratidal = 2,
        /// <summary>Permanently below low water. Kelp and seagrass. Appended because nothing else in the envelope can require the sea at depth: a submerged depth range says how deep, salt tolerance only permits salt, and the intertidal band caps altitude at the tidal range - so without this a kelp bed was admissible in a deep freshwater lake.</summary>
        Subtidal = 3
    }

    /// <summary>
    /// Which vertical layer of a stand this species occupies. Decides whose light
    /// budget it draws from and whose it reduces, and is what turns a forest into
    /// layers instead of one species mat.
    /// </summary>
    public enum CanopyLayer
    {
        /// <summary>Stands clear above the canopy. Full sun, full wind, and it shades everything.</summary>
        Emergent = 0,
        /// <summary>The closed layer that intercepts most of the light.</summary>
        Canopy = 1,
        /// <summary>Below the canopy, living on transmitted light.</summary>
        Understory = 2,
        /// <summary>At the ground. Takes what the two layers above have left.</summary>
        GroundCover = 3
    }

    /// <summary>
    /// What fire does to this species. A fire field with no response model is
    /// decoration, and serotinous species genuinely require fire to reproduce.
    /// </summary>
    public enum FireResponse
    {
        /// <summary>Killed outright. Returns only by dispersal from outside the burn.</summary>
        Killed = 0,
        /// <summary>Resprouts from a protected base or root. Present again within a year.</summary>
        Resprouter = 1,
        /// <summary>Seed is released by heat. Fire is required, so long-unburnt ground has none of it.</summary>
        Serotinous = 2
    }

    /// <summary>Per-species override of how instances are submitted to the GPU.</summary>
    public enum InstancingPolicy
    {
        /// <summary>Always individual draws. For a species with per-instance behaviour that instancing would break.</summary>
        Never = 0,
        /// <summary>Batch once the count in a chunk passes the library threshold. The right answer for almost everything.</summary>
        WhenDense = 1,
        /// <summary>Batch from the first instance. For a species that is always dense.</summary>
        Always = 2
    }

    /// <summary>What kind of collider a spawned instance carries, if any.</summary>
    public enum ColliderPolicy
    {
        /// <summary>Derive from the life form: grass none, shrub trigger, tree and stone solid.</summary>
        Auto = 0,
        /// <summary>No collider. Grass and moss - thousands of colliders for something a player walks through.</summary>
        None = 1,
        /// <summary>Trigger only, so the instance can report a brush-through without blocking movement.</summary>
        Trigger = 2,
        /// <summary>Solid. Trunks, boulders, deadfall.</summary>
        Solid = 3
    }
}
