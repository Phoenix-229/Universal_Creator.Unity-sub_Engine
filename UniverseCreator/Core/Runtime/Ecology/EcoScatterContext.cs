// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    EcoScatterContext.cs
//  Purpose: The read-only bundle a scatter node needs to choose species: the
//           sample settings, a bound environment sampler, and one species table
//           per prototype kind.
//
//  WHY THIS EXISTS AT ALL. A scatter node runs on a worker thread and must never
//  read a MonoBehaviour, so it cannot ask the vegetation manager anything. The
//  manager's answers are therefore snapshotted on the main thread into plain
//  arrays and structs before evaluation starts, and this is the object that
//  carries them across. It is the same reason EcoSampleSettings and WaterProbe[]
//  exist, applied one level up.
//
//  BUILT ON THE MAIN THREAD, READ FROM WORKERS. The species tables are built once
//  per bake and never written again, so every chunk shares them safely. The
//  sampler is the one part that is per chunk, because it points at that chunk's
//  height field - which is why ForChunk exists, and why two chunks must never be
//  handed the same context object. Sharing one would bind the sampler to
//  whichever chunk reached it first and then score both against that chunk's
//  terrain, placing the second chunk's forest according to the first one's
//  mountains. ForChunk is cheap precisely because the expensive part - the
//  tables - is shared rather than copied.
//
//  Any scratch buffer a node needs is the node's own, allocated per chunk. A
//  shared scratch array here would be the one thing in the design that could race.
//
//  ONE TABLE PER KIND, NOT ONE TABLE. The four prototype kinds are indexed
//  separately on the terrain: a tree instance's prototypeIndex indexes the tree
//  prototype array, a detail's indexes the detail array. Flattening all four into
//  one list would produce indices that point at the wrong prototype - a tree
//  emitted as detail layer three. The tables stay apart for that reason alone.
//
//  ABSENT IS NOT EMPTY. A null context means "this graph was evaluated without
//  ecology", and a node seeing one must behave exactly as it did before Module 3
//  rather than placing nothing. That distinction is what keeps every graph
//  authored against Modules 1 and 2 working unchanged, and it is why IsUsable
//  reports on the envelopes rather than on the array merely existing: a library
//  whose prototypes have never been given a species has a table full of default
//  envelopes, and treating those as authoritative would empty a world that used
//  to have trees in it.
//
//  ONE FACTOR IN THE SELECTION FORMULA IS STILL LEFT NULL. Dispersal - whether a
//  seed source has reached a site at all - is its own model with its own task, and
//  SpeciesSelector reads a null factor array as one. It is named here so it is clear
//  it is outstanding rather than forgotten, and so wiring it later touches this file
//  and nothing else.
//
//  BIOME AFFINITY IS NOT A TABLE COLUMN. It was one, and could not be: a per-slot
//  float can only say how much a species likes biomes in general, while the question
//  is how much it likes the biome under this particular tree. It now resolves per
//  sample from the envelope's own mask against the classifier's field, which is why
//  a biome matrix appears among the sampler inputs below and the column is gone.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// One prototype kind's species list, flattened to parallel arrays a worker
    /// thread can read. Index i corresponds to prototype slot i on the terrain.
    /// </summary>
    public sealed class SpeciesTable
    {
        public EcoEnvelope[] envelopes;

        /// <summary>Authored abundance per slot: the prototype's own weight times its envelope's.</summary>
        public float[] weights;

        public int count;

        /// <summary>
        /// Per-slot chance that a seed source has reached a site. Null until the
        /// dispersal model is wired, which SpeciesSelector reads as "no restriction".
        /// </summary>
        public float[] dispersal;

        /// <summary>
        /// True when this table can actually decide anything. A table of nulls is a
        /// library nobody has assigned species to, and the caller must fall back to
        /// its own prototype index rather than place nothing.
        /// </summary>
        public bool IsUsable()
        {
            if (envelopes == null || count <= 0) return false;

            int n = count < envelopes.Length ? count : envelopes.Length;

            for (int i = 0; i < n; i++)
            {
                EcoEnvelope eco = envelopes[i];

                // A default envelope has no species name. Assigning a preset or typing
                // a name is what marks a slot as deliberately described, and until one
                // slot is, the whole table is treated as unauthored.
                if (eco != null && !string.IsNullOrEmpty(eco.displayName)) return true;
            }

            return false;
        }
    }

    public sealed class EcoScatterContext
    {
        public EcoSampleSettings settings;

        /// <summary>Bound to this chunk's graph products. Sampling is safe from any thread.</summary>
        public EnvironmentSampler sampler;

        public SpeciesTable trees = new SpeciesTable();
        public SpeciesTable shrubs = new SpeciesTable();
        public SpeciesTable details = new SpeciesTable();
        public SpeciesTable objects = new SpeciesTable();

        // ---- Sampler inputs ---------------------------------------------------
        //
        // Supplied by whoever has them, and null is an honest answer for each: the
        // sampler records every absent channel in MissingOutputs and substitutes a
        // stated default rather than failing. A site scored with only a height field
        // still has real altitude, slope, curvature, aspect and temperature, which is
        // enough for thermal zonation to work; what it does not yet have is the
        // moisture axis, and the sampler says so rather than implying otherwise.

        /// <summary>Moisture mask for this chunk, or null when the graph routes none.</summary>
        public Matrix moisture;

        /// <summary>
        /// The classifier's biome index field for this chunk, or null when the graph
        /// does not wire one. Absent means every species' biome preference is withdrawn
        /// rather than failed, so an unwired graph places what it always placed.
        /// </summary>
        public Matrix biome;

        public WaterProbe[] water;
        public int waterCount;

        public TerrainDerivatives derivatives;

        /// <summary>
        /// This chunk's climate normals from the weather branch, or null when the graph
        /// grew none. Null falls the sampler's annual-precip baseline back to the
        /// resolved settings, so a world with no weather branch places what it always
        /// placed. Supplied per chunk from the tile's output bag, like the inputs above.
        /// </summary>
        public UniverseCreator.Weather.ClimateNormals climateNormal;

        /// <summary>
        /// Snapshot of the road cuts (V-23). World scope, like the water probes, and
        /// for the same reason: a road crosses chunk boundaries, so a per-chunk copy
        /// would clear vegetation on one side of a seam and not the other.
        /// </summary>
        public CutRegion[] cutRegions;
        public int cutRegionCount;

        /// <summary>
        /// This chunk's accumulated canopy shade (V-16). Per chunk and never shared:
        /// it is written during the bake, so two chunks sharing one would be two
        /// workers writing the same floats.
        /// </summary>
        public CanopyLightGrid canopy;

        /// <summary>
        /// The parent lattice (V-17). Read-only once bound and derived from the world
        /// seed alone, so it is shared - and must be, since a grove that stopped at a
        /// chunk boundary would be worse than no grove at all.
        /// </summary>
        public DispersalField dispersal;

        /// <summary>
        /// The whole-world hydrology field (W1). World scope, like the dispersal lattice
        /// and the cut list, and for the same reason: "where is the nearest water" is a
        /// map-sized question a chunk cannot answer from its own tile. Resolved lazily by
        /// <see cref="EnsureHydrology"/> from the shared drainage solve and memoised, so
        /// every chunk of a bake reads one derivation. Null until then, and null on a
        /// world with no water - both of which the sampler reads as "no hydrology field".
        /// </summary>
        public HydroField hydro;

        /// <summary>
        /// A context for one chunk that shares this one's species tables. The tables
        /// are the same objects rather than copies - they are read-only for the whole
        /// bake - while the sampler and the per-chunk products are fresh, so binding
        /// one chunk cannot affect another.
        ///
        /// <paramref name="northFraction01"/> is where this chunk sits between the
        /// world's south edge (0) and north edge (1). In a world that spans latitude
        /// it rebases the settings onto this chunk's own latitude; in a world that
        /// does not - the default - it costs nothing and changes nothing. (W-05)
        ///
        /// The fraction is required rather than defaulted on purpose: a caller that
        /// forgot it would silently place every chunk at the world's centre latitude,
        /// and a gradient that quietly does nothing is the hardest kind of bug to
        /// see. Callers that genuinely cover the whole world pass 0.5 and say why.
        /// </summary>
        public EcoScatterContext ForChunk(float northFraction01)
        {
            EcoScatterContext c = new EcoScatterContext();

            // The one field that is not shared verbatim. RebasedToLatitude returns
            // this snapshot untouched when the world has no latitude span, so worlds
            // that do not use the gradient are bit-identical to before W-05.
            c.settings = settings.RebasedToLatitude(northFraction01);

            c.trees = trees;
            c.shrubs = shrubs;
            c.details = details;
            c.objects = objects;

            // Shared on purpose. Both are read-only for the whole bake and both
            // describe the world rather than a chunk: the cut list because a road runs
            // across chunks, the dispersal lattice because a grove does. Copying
            // either per chunk would reintroduce the seam at every boundary.
            c.cutRegions = cutRegions;
            c.cutRegionCount = cutRegionCount;
            c.dispersal = dispersal;

            // Deliberately not carried over: sampler, moisture, biome, water,
            // derivatives, canopy, climateNormal. Those describe one chunk's terrain
            // and inheriting them is the exact mistake this method exists to prevent.
            c.sampler = new EnvironmentSampler();

            return c;
        }

        /// <summary>
        /// Binds the sampler on first use, from the height field the calling node
        /// already holds. Lazy because the height matrix is a graph product and does
        /// not exist when the context is built; guarded because several scatter nodes
        /// in one chunk would otherwise each rebuild the same derivatives.
        ///
        /// Call from the evaluating thread. Chunks do not share a context, and the
        /// nodes within one chunk run in sequence, so no lock is needed here.
        /// </summary>
        public void EnsureBound(Matrix height)
        {
            if (sampler == null) sampler = new EnvironmentSampler();
            if (sampler.IsBound) return;

            sampler.Bind(in settings, height, moisture, biome, water, waterCount, derivatives);

            // V-16. The grid is attached rather than passed to Bind because it is
            // filled progressively as species are placed, so it has to exist before
            // it has anything in it. Empty reads as full daylight, which is the right
            // answer before any canopy has been deposited.
            if (canopy == null) canopy = new CanopyLightGrid();
            sampler.AttachCanopy(canopy);

            // The chunk climate, when a weather branch produced one. Null is benign:
            // the sampler reads it as "use the resolved settings' annual precip", which
            // is exactly the pre-weather behaviour, so this attach is safe every bake.
            sampler.AttachClimate(climateNormal);

            // W1. The whole-world hydrology field, when EnsureHydrology has resolved it
            // (a scatter node calls that before selection). Null is benign: with no
            // field, or an all-dry world, the sampler stays on the empty water probes,
            // which is exactly the pre-W1 behaviour. When present it clears the Water
            // missing bit and becomes the real source of shore distance and salt.
            sampler.AttachHydrology(hydro);

            // V-17. Bound off the world seed, never off a chunk index - a grove that
            // restarted at each chunk boundary is the artefact this whole file guards
            // against.
            if (dispersal == null)
            {
                dispersal = new DispersalField();
                dispersal.Bind(settings.seed);
            }
        }

        /// <summary>
        /// Resolves the whole-world hydrology field (W1) on first use, from the shared
        /// drainage solve of the calling node's height branch. Lazy and guarded for the
        /// same reason as <see cref="EnsureBound"/>: the solve is a graph product that
        /// does not exist when the context is built, and several scatter nodes in one
        /// chunk must not each rebuild it.
        ///
        /// Call from the evaluating thread, from a node that has a consumer, tile and
        /// height (every scatter node does). The per-bake drainage cache and the
        /// HydroField memo together make this one derivation per bake, shared across
        /// every chunk - the field itself is world scope, so a chunk cannot see a coast
        /// two chunks away without it.
        /// </summary>
        public void EnsureHydrology(UniverseCreator.Nodes.Generator consumer,
                                    UniverseCreator.Nodes.TileData tile,
                                    UniverseCreator.Nodes.StopToken stop)
        {
            if (hydro != null) return;
            if (consumer == null || tile == null) return;

            // The SAME whole-world drainage solve the terrain post-pass runs (the one
            // SeaLevelFloor and CoastalShelf grade on), at the same resolution, so
            // ecology and terrain can never drift about where the water is. needFlow is
            // false: HydroField reads only the ocean mask, the filled surface and the
            // height field - not flow direction or accumulation.
            UniverseCreator.Nodes.WorldDrainage wd = UniverseCreator.Nodes.WorldDrainage.Solve(
                consumer, 0, tile,
                UniverseCreator.Nodes.SeaLevelFloor.DefaultResolution, 0f, false, stop);
            if (wd == null) return;

            hydro = HydroField.For(wd, settings.seaLevelY);

            // If the sampler already bound before hydrology was resolved, attach now so
            // this chunk still reads real water rather than the empty-probe fallback.
            // When it binds afterwards, EnsureBound does the attach instead.
            if (hydro != null && sampler != null && sampler.IsBound)
                sampler.AttachHydrology(hydro);
        }

        /// <summary>
        /// The species table for a scatter node's output channel, or null when that
        /// channel chooses species by some other route or by none.
        /// </summary>
        /// <remarks>
        /// Keyed on the channel string the node already declares, so a node does not
        /// have to name a table as well as a channel and then risk the two disagreeing -
        /// a tree node that reached for the detail table would emit tree indices into the
        /// grass layer array.
        ///
        /// Three channels are answered and the rest are not, which is a statement about
        /// how each one is indexed rather than a list of what has been got round to:
        ///
        ///   trees, shrubs   Their own prototype arrays, one index each, chosen per
        ///                   instance. These are the per-candidate pick.
        ///   objects         The object prototype array. Rocks, debris and underwater
        ///                   props index that same array, but they are placed by geology
        ///                   and depth rather than by climate, so they keep their own
        ///                   index instead of asking here. An author who does describe a
        ///                   species on an object prototype gets it through this channel.
        ///   grass           Deliberately absent. Detail layers are a per-cell density
        ///                   grid with no instances in it, so there is no candidate to
        ///                   choose a species for - the detail table is reached through
        ///                   density rather than through selection.
        /// </remarks>
        public SpeciesTable TableFor(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return null;

            if (channel == "trees") return trees;
            if (channel == "shrubs") return shrubs;
            if (channel == "objects") return objects;

            // V-45. Aquatic plants share the OBJECT roster rather than getting a
            // fifth one, and that is deliberate on two counts.
            //
            // Index space: ScatterApplier already routes the "underwater" channel
            // through ApplyObjects, so prototypeIndex on an underwater instance
            // already addresses lib.objects. A separate aquatic prototype list would
            // create a second index space feeding the same applier array - the exact
            // mismatch the ObjectScatterNode header warns about.
            //
            // Correctness: nothing needs separating by hand, because the envelope
            // already separates them. SubmergenceAdmits passes a species with a
            // stated submergedDepthRangeM only where the site is that deep under
            // water, and a species without one only where it is dry. So kelp in this
            // table cannot land on a beach and a fence post cannot land on the seabed.
            if (channel == "underwater") return objects;

            return null;
        }

        /// <summary>The score below which a species is absent rather than sparse.</summary>
        public float SuitabilityFloor { get { return settings.suitabilityThreshold; } }

        /// <summary>Exponent on the score. Above one, a better-suited species wins more decisively.</summary>
        public float Sharpness { get { return settings.suitabilitySharpness; } }

        /// <summary>Off means every site takes its single best species, which is a plantation.</summary>
        public bool MixedStands { get { return settings.allowMixedStands; } }

        /// <summary>How many species may compete at one site once mixing is on.</summary>
        public int MaxSpeciesPerSite { get { return settings.maxSpeciesPerSite; } }

        /// <summary>
        /// True when a node can ask this context for a species at all. A context whose
        /// sampler never bound has no environment to score against, and scoring
        /// against stated defaults would place the same species everywhere - which
        /// looks like a working system and is not one.
        /// </summary>
        public bool CanSelect()
        {
            return sampler != null && sampler.IsBound;
        }
    }
}
