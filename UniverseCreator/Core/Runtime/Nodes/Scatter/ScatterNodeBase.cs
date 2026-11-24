// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: placement solver implemented
//  File:    ScatterNodeBase.cs
//  Purpose: Shared base for the six Scatter nodes.
//
//  Module 1 owned the graph wiring, port contract, parameter sets, and OutputBag
//  channel plumbing. Module 2 fills in the placement solver that was stubbed:
//  blue-noise sampling at minSpacing, density and mask weighting, height and
//  slope rejection, Y snapping, and randomised scale and yaw.
//
//  Per-type prototype selection and species rules remain the subclass's job via
//  PlaceInstances, which now receives a populated list instead of an empty one.
//
//  SEAMLESSNESS
//  ------------
//  The solver is driven from ABSOLUTE world coordinates, not from the tile's
//  index, exactly as the noise creators are. Two neighbouring chunks solved on
//  separate workers therefore agree about the instances near their shared edge,
//  and regenerating a chunk reproduces its layout precisely.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>VEG-10 placement pattern for a scatter node.</summary>
    public enum ScatterPlacementMode
    {
        /// <summary>Organic blue-noise Poisson field (the default and only Module 1-3 behaviour).</summary>
        BlueNoise = 0,
        /// <summary>Regular square lattice - orchards, plantations, formal rows.</summary>
        Grid = 1,
        /// <summary>Concentric rings about the chunk centre - formal/radial planting.</summary>
        Ring = 2
    }

    [System.Serializable]
    public abstract class ScatterNodeBase : Generator, IOutputNode, IPreparedGraphInputHash
    {
        [System.NonSerialized] private TerrainSystem.ScatterIdentitySnapshot preparedIdentity; // UC-T06-06: immutable worker input, never a live world/prototype.
        internal void BindIdentity(TerrainSystem.ScatterIdentitySnapshot identity) { preparedIdentity = identity; }
        /// <summary>UC-T06-06: invalidates retained placement when the captured persistent world/prototype roster changes.</summary>
        /// <returns>Captured identity hash, or zero for an unowned standalone evaluation.</returns>
        public ulong GetPreparedInputHash() => preparedIdentity != null ? preparedIdentity.Hash : 0;
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }

        // UC-T01-06: platform capabilities are captured before dispatch, never queried through Unity on a worker.
        [System.NonSerialized] private int preparedProcessorCount = System.Environment.ProcessorCount;
        /// <summary>Captures available CPU count on the main thread for this node's parallel-build policy.</summary>
        public override void PrepareMainThread() { base.PrepareMainThread(); preparedProcessorCount = Mathf.Max(1, SystemInfo.processorCount); }

        // BIO-9: scatter gains an optional third input, Biome. HeightMaskBiomeIn's own contract is
        // that an unwired Biome port withdraws every biome preference, so a graph that wires only
        // Height+Mask (every existing graph) scatters exactly as before - the port is additive and
        // migration-safe. When wired, onlyInBiome below can hard-gate this node to one biome.
        public override Port[] GetInputs() { return SharedPorts.HeightMaskBiomeIn; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        /// <summary>
        /// VEG-6. A freshly-dropped scatter node grades its density across the height/slope
        /// bands by terrain (the Gaussian bell, not a flat plateau) out of the box, so a NEW
        /// world does not read "plants placed evenly, everywhere." Set here, on first creation
        /// only, rather than as the serialized field default: that keeps
        /// <see cref="gradeDensityByTerrain"/> = false for every node already saved in a graph,
        /// so existing worlds deserialize and place byte-for-byte unchanged. Opt-out remains the
        /// same single toggle in the inspector.
        /// </summary>
        public override void OnCreatedFresh()
        {
            base.OnCreatedFresh();
            gradeDensityByTerrain = true;
            stableWorldCandidates = true; // UC-T06-05: new nodes keep a world lattice independent of demand-region and point caps.

            // CTL-1: a NEWLY dropped scatter node detects the waterline rather than trusting a
            // typed 0.35 that may have nothing to do with where this world's ocean actually sits.
            // Set here, not as the field default, so nodes already saved in a graph keep their
            // authored value - the same reason gradeDensityByTerrain is set here.
            autoSeaLevel = true;

            // VEG-GND1. A new node seats its plants IN the ground instead of balancing them on a
            // single height sample. Both dials ship at 0 so no saved graph moves, but a node the
            // author drops today should not produce a forest with daylight under the trunks - that
            // was the reported fault, and "off by default" was why nobody ever saw the fix.
            //
            // 0.35 m footprint: a trunk-and-root-flare radius for a mid-size tree, which is what
            // this base class is most often used for. 3 cm sink: the low end of the author's
            // "2 to 5 cm or more", chosen because it is enough to hide sampling error at typical
            // heightmap resolutions while staying far below anything an eye reads as sunken.
            groundConformRadiusM = 0.35f;
            groundEmbedMetres = 0.03f;
        }

        /// <summary>
        /// OutputBag channel these instances are filed under. Module 2/3 consumers
        /// read by this key.
        /// </summary>
        public abstract string Channel { get; }

        /// <summary>
        /// Instances per square kilometre before mask weighting. The fallback figure
        /// that places when the ecology pass is off: with Use Ecology enabled on a
        /// vegetation manager, tree and grass density come from its shipped
        /// per-hectare fields instead (V-13); with ecology off (the shipped default)
        /// this node value is what actually places. See <see cref="CeilingDensityPerSqKm"/>.
        /// </summary>
        public float density = 100f;

        /// <summary>UC-T06-05: enables cap-independent candidates and local-priority cross-region spacing. False preserves saved legacy placement.</summary>
        /// <remarks>Changing the mode changes placement deliberately; persistent-edit identity migration is owned by UC-T06-06. One candidate per world cell; dense requests beyond the bounded work limit fail explicitly.</remarks>
        [Tooltip("Stable world candidates: caps retain a ranked subset instead of changing the grid. New nodes enable it. Changing a saved legacy node deliberately changes its distribution; preserve/migrate persistent edits before changing mode.")]
        public bool stableWorldCandidates;

        /// <summary>Minimum spacing between instances, in world metres.</summary>
        public float minSpacing = 2f;

        /// <summary>Instances are rejected outside this height band.</summary>
        public float minHeight = 0f;
        public float maxHeight = 1f;

        /// <summary>Instances are rejected outside this slope band, in degrees.</summary>
        public float minSlope = 0f;
        public float maxSlope = 45f;

        /// <summary>
        /// Grades density by the terrain bands instead of filling them flat. With this
        /// off (the default) a height or slope band is a plateau: full density anywhere
        /// inside it, cut at the edge - which is the "plants placed too far, evenly,
        /// everywhere" the author reported, because a marginal site and an ideal one
        /// inside the same band get identical spacing. With it on, each constrained band
        /// becomes a continuous bell (RangeFilter.GradedBand): thickest at the band's
        /// centre, thinning smoothly toward its bounds. The solver already honours a
        /// fractional weight by dropping darts, so no new pass is added - the darts just
        /// land densest where the ground suits best.
        ///
        /// This is the terrain-only half of 4O.2. The full ecology path (Use Ecology on)
        /// already grades density by habitat suitability per species (V-13, EcoDensity);
        /// this gives a world that is NOT running the climate ecology the same natural
        /// falloff from the height and slope bands it does author. Off by default so every
        /// existing graph places exactly as before.
        /// </summary>
        public bool gradeDensityByTerrain = false;

        /// <summary>Per-node seed so two scatter nodes don't produce identical layouts.</summary>
        public int seed = 1000;

        public Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        public bool randomYaw = true;

        /// <summary>
        /// §4O item 6. Radius in metres, scaled by the instance, over which the base is
        /// seated into the ground. 0 (the default) snaps the origin to a SINGLE sample,
        /// exactly as before - so every existing graph is unchanged until an author sets
        /// a footprint. Above 0, the Y is conformed DOWN to the lowest ground within the
        /// footprint, so a trunk on a slope sits into the hill instead of balancing on
        /// its centre and floating its downhill edge - the author's "tree bottom has a
        /// gap in slope". See <see cref="ConformedGroundY"/>.
        /// </summary>
        public float groundConformRadiusM = 0f;

        /// <summary>
        /// VEG-GND1. How far the instance's base is pushed INTO the ground, in metres, after the
        /// footprint conform above. The author's complaint this exists for: "the tree base must be
        /// in the ground 2 to 5 cm or more so it does not look like one side is hanging."
        ///
        /// WHY CONFORMING ALONE IS NOT ENOUGH. <see cref="ConformedGroundY"/> takes the LOWEST of
        /// four ring samples plus the centre, which removes the gap on the downhill side of a
        /// slope. It does not bury anything. Two things still leave daylight under a trunk:
        ///   - The heightmap is SAMPLED. Between samples the real drawn surface can dip below
        ///     every sample the conform looked at, and the mesh base skims the air there.
        ///   - A convex break - a ridge or the crown of a mound - curves away from the base on
        ///     every side at once, so there is no "lowest neighbour" low enough to sit on.
        /// A small constant sink absorbs both, and is what Unreal's foliage does with its own
        /// z-offset rather than trusting a single ground query.
        ///
        /// Kept SEPARATE from the conform radius because they answer different questions: the
        /// radius is "how wide is this plant's base", the sink is "how deep do I bury it". A tree
        /// wants a wide footprint and a few centimetres; a grass blade wants no footprint and a
        /// centimetre or two.
        ///
        /// 0 (the serialized default) changes nothing, so existing graphs are byte-identical.
        /// <see cref="OnCreatedFresh"/> gives a NEW node a real footprint and sink, because a tree
        /// that floats out of the box is the fault, not the safe default.
        /// </summary>
        public float groundEmbedMetres = 0f;

        /// <summary>
        /// VEG-2: when two placed instances' footprints overlap, keep the higher-scored one
        /// (bigger footprint × better suitability) and drop the loser, iteratively, over the
        /// shared bounded-radius spatial index. OFF (the default) prunes nothing, so every
        /// existing graph is byte-unchanged. Uses each instance's stamped
        /// <see cref="ScatterInstance.footprintRadius"/> (VEG-4), falling back to
        /// <see cref="overlapFallbackRadiusM"/> where none is stamped.
        /// </summary>
        public bool resolveOverlaps = false;

        /// <summary>
        /// Footprint radius in metres used for overlap/spacing when an instance carries no
        /// stamped footprint. Doubling as a minimum spacing: two fallback-radius instances
        /// may sit no closer than 2× this. 0 with nothing stamped = no pruning.
        /// </summary>
        public float overlapFallbackRadiusM = 0f;

        /// <summary>
        /// VEG-5: thin placement with a SMOOTH noise band so a field reads organic (broad clumps
        /// and clearings) instead of Poisson-even. A candidate is kept only where a tileable fBm
        /// of its world position falls inside [<see cref="noiseBandKeepLow"/>,
        /// <see cref="noiseBandKeepHigh"/>]. OFF (the default) keeps the even Poisson field, so
        /// nothing changes until an author turns it on; enabling it only ever removes points.
        /// </summary>
        public bool useNoiseBandThinning = false;

        /// <summary>Feature size in metres of the thinning noise. Larger = broader clumps/gaps.</summary>
        public float noiseBandScaleM = 40f;

        /// <summary>Keep band on the 0-1 noise. Narrower = sparser, patchier.</summary>
        public float noiseBandKeepLow = 0.35f;
        public float noiseBandKeepHigh = 1f;

        /// <summary>
        /// VEG-3: reject any candidate within <see cref="avoidRadiusM"/> metres of an instance
        /// already placed on another scatter channel this bake (e.g. "no tree within 6 m of a
        /// placed rock"). Reads whatever that channel has deposited into the tile's output bag, so
        /// the avoided node must evaluate first. Empty channel or radius 0 = no feature spacing.
        /// </summary>
        public string avoidChannel = "";
        public float avoidRadiusM = 0f;

        /// <summary>
        /// VEG-10: placement pattern. BlueNoise (the default) is the organic Poisson field; Grid
        /// and Ring lay candidates on a regular lattice / concentric rings for orchards, plantations
        /// and formal planting. Non-default modes still pass through every filter (height/slope/mask,
        /// submerged, noise, feature) so a grove stays inside the admissible area.
        /// </summary>
        public ScatterPlacementMode placementMode = ScatterPlacementMode.BlueNoise;

        /// <summary>Spacing in metres between grid rows/columns, or between concentric rings.</summary>
        public float patternSpacingM = 8f;

        /// <summary>
        /// GEO-3: orient each instance to the terrain FLOW direction (steepest descent) instead of
        /// a random yaw - driftwood, debris and grass combed downslope. Reads the height gradient
        /// already available to the solver, so it needs no extra input. Opt-in; off keeps the
        /// random/!randomYaw behaviour. On flat ground there is no flow direction, so the instance
        /// keeps its existing yaw.
        /// </summary>
        public bool alignToFlow = false;

        /// <summary>0-1 how strongly the instance turns to face downslope.</summary>
        public float alignToFlowStrength = 1f;

        /// <summary>
        /// RIV-4 W3: yaw each instance ALONG the river instead of downslope - reeds, driftwood and
        /// bank grass lying with the current rather than across it. Reads the direction from the
        /// MASK input's gradient: a River Mask falls off ACROSS the channel, so its gradient points
        /// across the water and the course runs at right angles to it. That means this needs no
        /// scene access, no pushed course and no extra port - just the River Mask the author has
        /// already wired to place bank vegetation.
        ///
        /// Opt-in; off keeps the random/flow behaviour exactly. Where the mask is flat - deep
        /// inside the channel, or well away from any river - there is no direction to read and the
        /// instance keeps the yaw it already had, so this is never wrong, only sometimes silent.
        /// Applied AFTER alignToFlow so that with both on, the river wins on a bank (which is the
        /// author's evident intent) while flow still combs everything away from the water.
        /// </summary>
        public bool alignToRiverBank = false;

        /// <summary>0-1 how strongly the instance turns to lie along the river course.</summary>
        public float alignToRiverBankStrength = 1f;

        /// <summary>
        /// VEG-9: a composable filter stack (AND / OR) of typed predicates on each candidate's
        /// height, slope, mask or noise. Richer than the single height-band + slope-band the solver
        /// ships with - "high AND steep", "near water OR south-facing". Empty / off = unchanged.
        /// </summary>
        public bool useFilterStack = false;
        public FilterGroupOp filterGroupOp = FilterGroupOp.And;
        public List<ScatterPredicate> filterStack = new List<ScatterPredicate>();

        /// <summary>
        /// VEG-11: decorate another channel's instances instead of solving fresh positions - drop
        /// nests, mushrooms or debris onto a FRACTION of the trees/rocks already placed this bake.
        /// Empty channel = normal placement. The decorated channel must have evaluated first.
        /// </summary>
        public string decorateChannel = "";

        /// <summary>0-1 fraction of the source channel's instances that receive a decorator.</summary>
        public float decorateKeepFraction = 0.3f;

        /// <summary>
        /// BIO-9: hard-gate this whole scatter node to a single biome class (a BiomeType index).
        /// -1 (default) = no gate (any biome), so existing graphs are unchanged. When >= 0 and the
        /// Biome input is wired, a candidate is rejected unless the classified biome at its position
        /// matches - e.g. a mangrove node that only fires in the Mangrove/wetland class.
        /// </summary>
        public int onlyInBiome = -1;

        /// <summary>
        /// Softens the height and slope cutoffs so a field thins out over the last
        /// few degrees rather than stopping along a contour line. Slope units are
        /// degrees; the height band uses a fixed small fraction of its own range.
        /// </summary>
        public float slopeFalloff = 5f;

        /// <summary>
        /// Tilts instances toward the surface normal. 0 keeps everything upright,
        /// which is right for trees; 1 lays them flat against the slope, which is
        /// right for rocks and debris.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT named alignToNormal: RockScatterNode and
        /// ObjectScatterNode already ship a serialized bool by that name, and a
        /// same-named base field would hide theirs (CS0108) so their inspector
        /// setting would silently stop reaching the solver. They route their bool
        /// in through <see cref="NormalAlignment"/> instead.
        /// </remarks>
        public float normalAlignment;

        /// <summary>
        /// Effective 0-1 normal alignment the solver applies. Subclasses with their
        /// own alignment control override this to feed it in.
        /// </summary>
        protected virtual float NormalAlignment { get { return normalAlignment; } }

        /// <summary>
        /// Above 0, thins sparse areas so the remaining instances read as groves
        /// and thickets rather than an even field.
        /// </summary>
        public float clusterStrength;

        /// <summary>Neighbourhood radius the cluster test measures density over.</summary>
        public float clusterRadius = 25f;

        /// <summary>
        /// Safety ceiling on instances emitted per chunk. A misconfigured density
        /// on a 1 km chunk can ask for millions, which would exhaust memory before
        /// anything reached the terrain.
        /// </summary>
        public int maxInstancesPerChunk = 50000;

        /// <summary>
        /// PERF-3: fill this node's instances with a parallel-for instead of the serial loop.
        ///
        /// Serialized default is FALSE, so every graph already saved keeps the path it had and
        /// a bake is byte-for-byte unchanged until an author ticks it. The two paths run the
        /// SAME per-candidate maths through the same helper, so turning this on must not move a
        /// single instance - the PERF-JOB1 gate asserts that for a fixed seed.
        ///
        /// Capability-checked rather than trusted: the solve only goes parallel when this is on,
        /// the candidate count is at least <see cref="ParallelBuildMinCandidates"/>, and the
        /// machine reports more than one core. Below that the serial loop wins anyway, because
        /// the dispatch itself costs more than the work.
        ///
        /// NOT Burst/Jobs. The per-candidate maths samples a managed <see cref="Matrix"/> and a
        /// managed noise table; making it a Burst job would mean copying the whole height field
        /// into NativeArrays per chunk, which costs more than the scatter it would accelerate.
        /// Parallel.For over pure managed maths is what this path actually is.
        ///
        /// No frame budget either: <see cref="BuildInstances"/> already runs off the main thread
        /// inside the chunk bake, so a long fill cannot stall a frame. Time-slicing it would only
        /// mean returning a half-built channel, which is wrong in a way a slower frame is not.
        /// </summary>
        public bool parallelBuild = false;

        // -- Water exclusion -------------------------------------------------------
        // The guard the author asked for ("no trees in the ocean unless marine or lake
        // vegetation"). Authored here, on the shared base, so every scatter type gets it
        // from one place - see RejectSubmergedCandidates for why it was missing.

        /// <summary>
        /// Keep this scatter type out of water. Left ON by default: a land plant standing
        /// in the sea is never wanted, so the safe state must not depend on the author
        /// remembering to tick it.
        /// </summary>
        public bool avoidWater = true;

        /// <summary>
        /// Metres of dry shore ALSO left clear, measured horizontally from the waterline.
        /// 0 allows planting right down to the water's edge. Separate from the submerged
        /// test on purpose: a beach is not a lake, so refusing to plant IN water must not
        /// force a bare strip around every shore unless the author asks for one.
        /// </summary>
        public float shoreClearanceMetres = 0f;

        /// <summary>
        /// Normalised waterline this node measures against, matching the convention the
        /// terrain passes and the texture bands use.
        ///
        /// CTL-1 (2026-10-14): this is no longer typed-only. When <see cref="autoSeaLevel"/> is
        /// on, the water system OVERWRITES this field from the ocean body's real surface through
        /// the same single-writer push that already feeds Ocean Level, Lake Fill, Coastal Erosion
        /// and the rest (`UCWaterSystemManager.ApplyToGraph` → `WalkSeaLevelReaders`). It stays a
        /// serialized field on purpose: the push writes it, so the value the author sees in the
        /// inspector is the value the bake used, instead of the real number living somewhere the
        /// inspector cannot show.
        ///
        /// The other two facts the original TODO asked for need no detection: `minHeight`/
        /// `maxHeight` are already NORMALISED fractions of the height range rather than metres,
        /// and the world size arrives on the tile. Sea level was the only one being re-asked.
        /// </summary>
        public float seaLevel = 0.35f;

        /// <summary>
        /// CTL-1: let the water system detect the waterline instead of trusting the typed
        /// <see cref="seaLevel"/> above.
        ///
        /// Serialized default is FALSE so every node already saved in a graph keeps the value its
        /// author chose and places byte-for-byte unchanged - an authored override always wins.
        /// <see cref="OnCreatedFresh"/> turns it ON for newly dropped nodes, which is the same
        /// first-creation-only trick <see cref="gradeDensityByTerrain"/> uses, so "auto" is the
        /// new default without rewriting anyone's existing world.
        /// </summary>
        public bool autoSeaLevel = false;

        /// <summary>
        /// True for a scatter type that BELONGS in water - marine or lake planting, or the
        /// underwater scatter path. Aquatic types opt IN; everything else is excluded.
        /// Overridden rather than authored so a land node cannot be mis-ticked into the sea.
        /// </summary>
        protected virtual bool IsAquaticScatter { get { return false; } }
        /// <summary>
        /// False since Module 2: the placement solver is implemented. Subclasses
        /// whose per-type rules are still outstanding override this back to true
        /// so the editor keeps badging them.
        /// </summary>
        public virtual bool WasStubbed { get { return false; } }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            Matrix mask = GetInputMatrix(tile, 1, stop);

            if (stop != null && stop.StopRequested) return null;

            List<ScatterInstance> instances = new List<ScatterInstance>();

            // No height field means nothing to stand on. Emitting an empty channel
            // rather than returning early keeps consumers from distinguishing
            // "unwired" from "never ran".
            if (height == null)
            {
                VegetationPlacementReport.Publish(tile, "missing ground", 0, 0, guidance: "Connect accepted world ground to Height; no candidate can be seated."); // UC-T06-17: explain dependency emptiness.
                tile.outputs.AddScatter(Channel, instances);
                return null;
            }

            // W1. With ecology active for this tile, resolve the whole-world hydrology
            // field before any species is scored, so the environment sampler reads real
            // shore distance, submersion and salt instead of the empty water probes that
            // no builder ever fills. Cheap after the first chunk - both the drainage
            // solve and the derived field are cached and shared across the bake.
            if (tile.ecology != null)
                tile.ecology.EnsureHydrology(this, tile, stop);

            List<ScatterCandidate> candidates = SolvePlacement(height, mask, tile, stop);

            if (stop != null && stop.StopRequested) return null;

            BuildInstances(candidates, height, tile, instances, stop);
            int beforeSpecies = instances.Count; // UC-T06-17: shaping remains separate from sampling evidence.

            // Subclass hook. Runs last so per-type rules can filter, duplicate, or
            // re-parameterise what the shared solver produced.
            PlaceInstances(height, mask, tile, instances, stop);
            if (stop == null || !stop.StopRequested) VegetationPlacementReport.Publish(tile, "species and family shaping", beforeSpecies, instances.Count);

            // VEG-2: optional weighted overlap pruning, after every instance (and any the
            // subclass added) exists and carries its footprint. Opt-in; a no-op when off.
            ResolveInstanceOverlaps(instances, tile, stop);
            int visibleScale = 0;
            for (int i = 0; i < instances.Count; i++) if (instances[i].scale.x > 0 && instances[i].scale.y > 0 && instances[i].scale.z > 0) visibleScale++;
            if (stop == null || !stop.StopRequested) VegetationPlacementReport.Publish(tile, "overlap and family thinning", instances.Count, visibleScale,
                guidance: "Native prototype/contact/collider/quality admission follows; compare with the committed registry.");

            // UC-T06-06: species selection must finish before identity is stamped; legacy IDs remain available for migration.
            if (stop != null && stop.StopRequested) return null;
            // UC-T06-07: retain authored embedding/attitude after species and deposit shaping;
            // final native ground can differ from the worker heightfield without moving XZ or keys.
            for (int i = 0; i < instances.Count; i++)
            {
                if ((i & 255) == 0 && stop != null && stop.StopRequested) return null;
                ScatterInstance inst = instances[i];
                float radius = Mathf.Max(0f, groundConformRadiusM * inst.scale.x);
                float referenceY = ConformedGroundY(height, inst.position.x, inst.position.z, radius,
                    height.GetWorldHeight(inst.position.x, inst.position.z));
                if (!inst.ground.followsGround)
                    inst.ground = new TerrainSystem.ScatterGroundBinding(0f, 0f, inst.rotation,
                        SampleNormal(height, inst.position.x, inst.position.z), NormalAlignment);
                inst.ground.offsetMetres = inst.position.y - referenceY;
                inst.ground.radiusMetres = radius;
                BindWaterContact(ref inst, tile); // UC-T06-11: final-ground water constraints use the producer's accepted species/depth contract.
                instances[i] = inst;
            }
            preparedIdentity?.Stamp(Channel, id, instances);
            tile.outputs.AddScatter(Channel, instances);
            return null;
        }

        /// <summary>UC-T06-11: retains this producer's final-ground water policy after placement/species selection.</summary>
        /// <param name="instance">Prepared instance with its ground binding; updated in place without changing identity or pose.</param>
        /// <param name="tile">Worker tile supplying the captured world-metre vertical frame.</param>
        protected virtual void BindWaterContact(ref ScatterInstance instance, TileData tile)
        {
            instance.ground.excludesWater = avoidWater && !IsAquaticScatter;
            instance.ground.fallbackWaterY = tile.worldPos.y + seaLevel * tile.worldHeight;
        }

        /// <summary>
        /// Runs the blue-noise solve and returns accepted candidates with their
        /// height, slope, and mask values resolved.
        /// </summary>
        protected virtual List<ScatterCandidate> SolvePlacement(
            Matrix height, Matrix mask, TileData tile, StopToken stop)
        {
            ScatterFilter filter = new ScatterFilter();

            // Only enable a filter that actually excludes something. An enabled
            // slope filter costs a full Sobel pass over the tile, so a node left
            // at the full 0-90 range should not pay for it.
            //
            // When density grading is on, the SAME constrained bands become bells
            // instead of plateaus (GradedBand), so the field thins toward each bound
            // rather than passing flat across it. The enable test is unchanged, so a
            // node at the full range still pays for no filter and no Sobel pass - the
            // author narrows a band to ask for grading on it.
            if (minHeight > 0f || maxHeight < 1f)
                filter.height = gradeDensityByTerrain
                    ? RangeFilter.GradedBand(minHeight, maxHeight)
                    : RangeFilter.Band(minHeight, maxHeight, 0.02f);

            if (minSlope > 0f || maxSlope < 90f)
                filter.slope = gradeDensityByTerrain
                    ? RangeFilter.GradedBand(minSlope, maxSlope)
                    : RangeFilter.Band(minSlope, maxSlope, Mathf.Max(0f, slopeFalloff));

            filter.Bind(height, mask);

            PoissonSettings settings = PoissonSettings.Default;

            // Spacing is whichever constraint binds harder: the explicit minimum,
            // or the spacing implied by the requested density. Ignoring the second
            // would let a low density still emit a dense field that is then thinned
            // probabilistically - far more darts thrown for the same result.
            float densitySpacing = PoissonDisc.SpacingForDensity(CeilingDensityPerSqKm(tile));
            settings.minSpacing = Mathf.Max(Mathf.Max(0.01f, minSpacing), densitySpacing);

            // Chunk coordinate does NOT enter the seed. The world cell hash already
            // makes placement position-dependent, and folding the chunk index in
            // would make the same world position hash differently depending on
            // which chunk asked - which is exactly the seam this design avoids.
            settings.seed = seed;
            settings.maxPoints = Mathf.Max(1, maxInstancesPerChunk);
            settings.stableWorldGrid = stableWorldCandidates; // UC-T06-05: serialized legacy nodes retain their original solver.

            // StopToken is the node-graph cancellation type; the math layer takes a
            // plain predicate so it stays free of graph types.
            System.Func<bool> isCancelled = null;
            if (stop != null) isCancelled = delegate { return stop.StopRequested; };

            List<ScatterCandidate> candidates;
            if (!string.IsNullOrEmpty(decorateChannel))
            {
                // VEG-11: decorate a fraction of another channel's instances instead of solving
                // fresh positions. The decorated channel must have run first; an empty/missing one
                // yields nothing (nothing to decorate), which is the honest result.
                candidates = BuildDecoratorCandidates(tile);
            }
            else if (placementMode == ScatterPlacementMode.BlueNoise)
            {
                candidates = PoissonDisc.Solve(
                    settings,
                    tile.worldPos.x, tile.worldPos.z,
                    tile.worldSize.x, tile.worldSize.z,
                    filter.EvaluateWorld,
                    isCancelled, report => tile.AddPublication(new ScatterSamplingGraphPublication(id + ":sampling", report))); // UC-T06-05: report exact sampler rejection stages through existing cache publication ownership.

                if (clusterStrength > 0.001f && clusterRadius > 0f)
                    candidates = PoissonDisc.ApplyClusterBias(
                        candidates, clusterRadius, clusterStrength, isCancelled, seed, stableWorldCandidates); // UC-T06-05: capped regions share one clump field.
            }
            else
            {
                // VEG-10: a regular orchard/plantation pattern, kept to the admissible band and the
                // chunk's instance cap. Cluster bias is deliberately NOT applied - a formal planting
                // is regular by definition.
                candidates = GeneratePatternCandidates(tile, filter, settings.maxPoints, isCancelled);
            }

            // Resolve fills in height, slope, and mask for the survivors only, so
            // rejected darts never pay for the sampling.
            filter.ResolveAll(candidates, isCancelled);
            filter.Unbind();

            // VEG-9: optional composable filter stack over the resolved candidates. Opt-in; a
            // no-op when off or empty.
            int previousCount = candidates.Count; // UC-T06-17: completed stages report their observed count difference.
            candidates = ApplyFilterStack(candidates, stop);
            Stage("composed filters");

            candidates = RejectSubmergedCandidates(candidates, tile, stop);
            Stage("water exclusion");

            // VEG-5: optional smooth noise-band thinning, so the field clumps instead of
            // reading Poisson-even. Opt-in; a no-op when off.
            candidates = ApplyNoiseBandThinning(candidates, stop);
            Stage("noise density");

            // VEG-3: optional feature-distance filter - reject candidates too close to instances
            // already placed on another channel. Opt-in; a no-op when off.
            candidates = ApplyFeatureDistanceFilter(candidates, tile, stop);
            Stage("feature distance");

            // BIO-9: optional hard biome gate - keep only candidates whose classified biome matches
            // onlyInBiome. Opt-in (onlyInBiome < 0 = any), and a no-op if the Biome input is unwired.
            if (onlyInBiome >= 0)
                candidates = ApplyBiomeGate(candidates, GetInputMatrix(tile, 2, stop), onlyInBiome, stop);
            Stage("biome gate");

            return candidates;

            void Stage(string stage)
            {
                if (stop == null || !stop.StopRequested) VegetationPlacementReport.Publish(tile, stage, previousCount, candidates.Count);
                previousCount = candidates.Count;
            }
        }

        /// <summary>
        /// Drops every candidate standing in water, unless this scatter type is aquatic.
        ///
        /// WHY THIS LIVES HERE (author report 2026-10-07: trees spawning in the ocean).
        /// The guard itself already existed - PlacementClearance.WaterAdmits rejects a
        /// non-aquatic species on a submerged site, and EnvironmentSampler fills
        /// submergedDepthM - but a scan of the scatter nodes found it referenced ZERO times
        /// by TreeScatter, ShrubScatter, GrassDetailScatter, RockScatter, ObjectScatter and
        /// DebrisScatter. Six of the eight placement paths simply never asked. So the fix
        /// belongs in the ONE shared solver every scatter node already inherits for its
        /// height and slope rejection, not copied into each node.
        ///
        /// The water surface comes from the same whole-world hydrology the terrain
        /// post-pass, the texture bands and the ecology layer read: WorldDrainage.Solve ->
        /// HydroField.For, memoised per bake, so the shoreline has ONE definition across
        /// every system rather than a private one per scatter node. A chunk-local test
        /// would restart at every seam and let trees stand in the sea at a chunk border.
        ///
        /// Aquatic types opt IN rather than out: a species declares a submerged depth range
        /// and only then may it stand in water (marine or lake planting), which is the
        /// author's "unless specified as marine vegetation or lake vegetation".
        /// </summary>
        protected virtual List<ScatterCandidate> RejectSubmergedCandidates(
            List<ScatterCandidate> candidates, TileData tile, StopToken stop)
        {
            if (candidates == null || candidates.Count == 0) return candidates;
            if (!avoidWater || IsAquaticScatter) return candidates;

            WorldDrainage wd = WorldDrainage.Solve(
                this, 0, tile, SeaLevelFloor.DefaultResolution, 0f, false, stop);
            if (stop != null && stop.StopRequested) return candidates;
            if (wd == null || wd.height == null) return candidates;

            // seaLevel is authored NORMALISED here, while HydroField treats its seaLevelY as
            // world METRES, so it is converted through the solve's own vertical frame. The
            // same conversion BiomeSplatOutputNode makes, for the same reason: passing a
            // 0..1 value where metres are expected silently puts the waterline on the floor.
            float baseY = wd.height.worldPos.y;
            float scaleY = wd.height.worldHeight;
            Ecology.HydroField hydro = Ecology.HydroField.For(
                wd, baseY + Mathf.Clamp01(seaLevel) * scaleY);
            if (hydro == null) return candidates;

            float clearance = Mathf.Max(0f, shoreClearanceMetres);

            List<ScatterCandidate> kept = new List<ScatterCandidate>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) return candidates;

                ScatterCandidate c = candidates[i];

                // ONE sample answers both questions. HydroField's distance is signed the
                // way EnvironmentSampler reads it (`dAny <= 0f` is inside the body, which is
                // how it decides submergedDepthM).
                float dW = hydro.DistanceToWaterM(c.worldX, c.worldZ);

                if (WaterRejects(dW, clearance, IsAquaticScatter, avoidWater)) continue;

                kept.Add(c);
            }

            return kept;
        }

        /// <summary>
        /// The whole water-exclusion decision, as a pure function: true = reject this spot.
        ///
        /// Pulled out of the loop deliberately so it can be SELF-TESTED. The gate hinges on
        /// HydroField's distance SIGN - negative/zero inside a body, positive on dry land -
        /// and if that convention were read backwards the guard would invert and plant
        /// exclusively in the sea while clearing the land, which is the one failure mode a
        /// compile cannot catch. A pure predicate can be pinned directly.
        /// </summary>
        /// <param name="distanceToWaterM">
        /// Signed metres to the nearest waterline: &lt;= 0 inside a lake or the sea,
        /// positive on dry land.
        /// </param>
        /// <param name="clearanceM">Dry shore strip also kept clear. 0 allows planting to the edge.</param>
        /// <param name="aquatic">True for a type that belongs in water; it is never rejected.</param>
        /// <param name="avoid">The author's switch. False disables the whole exclusion.</param>
        internal static bool WaterRejects(float distanceToWaterM, float clearanceM,
                                          bool aquatic, bool avoid)
        {
            if (!avoid || aquatic) return false;

            // Inside a lake or the sea: never, for a non-aquatic type.
            if (distanceToWaterM <= 0f) return true;

            // Dry, but inside the shore strip. A separate test because a beach is not a
            // lake: refusing to plant IN water must not force a bare ring around every
            // shore unless the author asked for one.
            return clearanceM > 0f && distanceToWaterM < clearanceM;
        }

        /// <summary>PERF-3: below this many candidates a parallel dispatch costs more than it saves.</summary>
        internal const int ParallelBuildMinCandidates = 256;

        /// <summary>
        /// PERF-3: main-thread diagnostic wrapper for the parallel capability rules.
        /// UC-T01-06: workers call the pure overload with their captured processor count.
        /// </summary>
        internal static bool ShouldBuildParallel(bool optedIn, int candidateCount)
        {
            // Existing main-thread diagnostic entry; runtime calls the overload with captured capabilities.
            return ShouldBuildParallel(optedIn, candidateCount, SystemInfo.processorCount);
        }

        private static bool ShouldBuildParallel(bool optedIn, int candidateCount, int processorCount)
        {
            if (!optedIn) return false;
            if (candidateCount < ParallelBuildMinCandidates) return false;
            return processorCount > 1;
        }

        /// <summary>
        /// Atomic minimum. A stop signalled by any worker must truncate the list at the SAME
        /// index the serial loop would have stopped at, or the two paths disagree on a cancel.
        /// </summary>
        private static void LowerAtomicMin(ref int target, int value)
        {
            int seen = Volatile.Read(ref target);
            while (value < seen)
            {
                int prior = Interlocked.CompareExchange(ref target, value, seen);
                if (prior == seen) return;
                seen = prior;
            }
        }

        /// <summary>
        /// One candidate to one instance. The whole per-candidate body lives here so the serial
        /// and parallel paths cannot drift: PERF-3 is a change of HOW the same maths is driven,
        /// never a second implementation of it.
        /// </summary>
        private ScatterInstance BuildOne(
            ScatterCandidate c, Matrix height, TileData tile, Noise variation, Matrix bankMask,
            float scaleMin, float scaleMax)
        {
            float worldY = tile.worldPos.y + c.height * tile.worldHeight;

            // Quantising to centimetres before hashing keeps the variation
            // stable: a position that differs in the last float bit between
            // two runs would otherwise pick a different scale.
            int hx = Mathf.RoundToInt(c.worldX * 100f);
            int hz = Mathf.RoundToInt(c.worldZ * 100f);

            float rScale = variation.Random(hx, hz);
            float rYaw = variation.Random(hz + 104729, hx + 1299709);

            float scale = Mathf.Lerp(scaleMin, scaleMax, rScale);

            // §4O item 6. With a footprint radius set, seat the base into the slope
            // instead of balancing the origin on one sample. Scaled by this instance
            // so a big tree conforms over a bigger base. Radius 0 leaves worldY as the
            // single-sample snap above, so the dial is opt-in and changes nothing until
            // an author uses it.
            if (groundConformRadiusM > 0.001f)
                worldY = ConformedGroundY(height, c.worldX, c.worldZ,
                                          groundConformRadiusM * scale, worldY);

            // VEG-GND1. Sink the base into the ground. Deliberately NOT scaled by the
            // instance: this absorbs heightmap sampling error and convex curvature, both of
            // which are properties of the TERRAIN, not of how big the plant is - scaling it
            // would bury a large tree deeper than a small one for no physical reason.
            if (groundEmbedMetres > 0f) worldY -= groundEmbedMetres;

            Quaternion rotation = Quaternion.identity;
            if (randomYaw) rotation = Quaternion.Euler(0f, rYaw * 360f, 0f);

            float align = Mathf.Clamp01(NormalAlignment);
            if (align > 0.001f)
            {
                Vector3 normal = SampleNormal(height, c.worldX, c.worldZ);
                Quaternion aligned = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                rotation = Quaternion.Slerp(rotation, aligned, align);
            }

            // GEO-3: comb the instance downslope along the terrain flow. The height gradient
            // (via SampleNormal) already gives the steepest-descent direction in XZ; turn the
            // instance to face it, blended by strength. Flat ground yields no direction and
            // keeps the yaw above. Opt-in, so a world that sets nothing is unchanged.
            if (alignToFlow)
            {
                Vector3 fn = SampleNormal(height, c.worldX, c.worldZ);
                float yawDeg;
                if (TryFlowYaw(fn, out yawDeg))
                    rotation = Quaternion.Slerp(rotation, Quaternion.Euler(0f, yawDeg, 0f),
                                                Mathf.Clamp01(alignToFlowStrength));
            }

            // RIV-4 W3: lay the instance along the river course. Last, so on a bank the river
            // direction wins over the downslope comb - which is the point of the dial. Where
            // the mask has no gradient there is no course here and the yaw above survives.
            if (bankMask != null)
            {
                float bankYaw;
                if (TryRiverBankYaw(bankMask, c.worldX, c.worldZ, out bankYaw))
                    rotation = Quaternion.Slerp(rotation, Quaternion.Euler(0f, bankYaw, 0f),
                                                Mathf.Clamp01(alignToRiverBankStrength));
            }

            ScatterInstance inst = new ScatterInstance(
                new Vector3(c.worldX, worldY, c.worldZ),
                rotation,
                new Vector3(scale, scale, scale),
                0);
            // VEG-13: a stable, position-derived identity so a harvested/erased instance can
            // stay gone across a reload (PERF-2) and an editor brush can address it.
            inst.instanceId = StableInstanceId(c.worldX, c.worldZ);
            return inst;
        }

        /// <summary>
        /// Turns accepted candidates into placed instances: world position with Y
        /// snapped to the surface, randomised uniform scale, yaw, and optional
        /// alignment to the surface normal.
        /// </summary>
        protected virtual void BuildInstances(
            List<ScatterCandidate> candidates, Matrix height, TileData tile,
            List<ScatterInstance> instances, StopToken stop)
        {
            if (candidates == null) return;

            float scaleMin = Mathf.Min(scaleRange.x, scaleRange.y);
            float scaleMax = Mathf.Max(scaleRange.x, scaleRange.y);

            // A second hash stream, offset from the placement seed so scale and yaw
            // do not correlate with position - reusing candidate.random for all
            // three would make big instances always face the same way.
            Noise variation = new Noise(seed + 7919);

            // RIV-4 W3: pulled ONCE, not per candidate - the evaluator caches the input, but a
            // per-instance pull would still re-enter it thousands of times. Null when the dial is
            // off or the Mask port is unwired, and the yaw step below then does nothing.
            Matrix bankMask = alignToRiverBank ? GetInputMatrix(tile, 1, stop) : null;

            // PERF-3. Pre-sized buffer, filled by index, then appended in candidate order, so
            // the parallel path emits exactly the list the serial loop would have emitted.
            // UC-T02-02: admitted graph workers already consume the global CPU budget; nested Parallel.For would multiply it.
            if (!TerrainSystem.WorldGenerationScheduler.IsWorkerThread && ShouldBuildParallel(parallelBuild, candidates.Count, preparedProcessorCount))
            {
                ScatterInstance[] built = new ScatterInstance[candidates.Count];
                int stoppedAt = candidates.Count;

                // One fewer worker than the machine reports: the chunk bake is ALREADY a
                // worker thread, and filling every core would starve the very pump that
                // applies the chunk this bake belongs to.
                ParallelOptions po = new ParallelOptions();
                po.MaxDegreeOfParallelism = Mathf.Max(1, preparedProcessorCount - 1); // UC-T01-06: captured main-thread capability.

                Parallel.For(0, candidates.Count, po, i =>
                {
                    if (stop != null && (i & 255) == 0 && stop.StopRequested)
                    {
                        LowerAtomicMin(ref stoppedAt, i);
                        return;
                    }
                    built[i] = BuildOne(candidates[i], height, tile, variation, bankMask,
                                        scaleMin, scaleMax);
                });

                // Serial stops BEFORE the index that saw the flag; match that exactly.
                int n = stop == null ? candidates.Count : Volatile.Read(ref stoppedAt);
                for (int i = 0; i < n; i++) instances.Add(built[i]);
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                instances.Add(BuildOne(candidates[i], height, tile, variation, bankMask,
                                       scaleMin, scaleMax));
            }
        }

        /// <summary>
        /// PERF-JOB1 harness: build the same candidates twice - once through the serial loop and
        /// once through the parallel one - and hand both lists back. Internal because the gate
        /// lives in the self-test assembly; this is the only honest way to exercise the real
        /// dispatch without a fully wired graph, and it runs the REAL code, not a copy.
        /// </summary>
        internal void BuildInstancesBothWays(
            List<ScatterCandidate> candidates, Matrix height, TileData tile,
            List<ScatterInstance> serial, List<ScatterInstance> parallel, StopToken stop)
        {
            bool saved = parallelBuild;
            try
            {
                parallelBuild = false;
                BuildInstances(candidates, height, tile, serial, stop);
                parallelBuild = true;
                BuildInstances(candidates, height, tile, parallel, stop);
            }
            finally
            {
                parallelBuild = saved;
            }
        }

        /// <summary>
        /// Instances per square kilometre the densest site may carry (V-13). When the
        /// ecology pass is active for this tile - a vegetation manager present AND its
        /// Use Ecology switch on, which is what makes <see cref="TileData.ecology"/>
        /// non-null - this is the manager's figure, converted from the per-square-metre
        /// ceiling it resolves. With ecology OFF (the shipped default) or no manager,
        /// it is this node's own density field, unchanged from Modules 1 and 2.
        /// </summary>
        /// <remarks>
        /// This is a CEILING. The solve throws darts at the spacing it implies and
        /// the ecology pass then removes the surplus per site, so an ideal hectare
        /// reaches the authored figure and nothing exceeds it. Solving at an average
        /// density instead would need a second, finer solve on good ground, and two
        /// solves do not agree about the same position.
        ///
        /// Why the manager wins for trees WHEN ECOLOGY IS ON: treesPerHectare and
        /// globalDensityScale are shipped inspector fields that until V-13 nothing
        /// read (B-1). A world whose tree count is set on one component and overridden
        /// per node is a world an author cannot reason about, so the component is
        /// authoritative and the node's density is the fallback. NOTE the honest
        /// limit: this hand-off happens only through the ecology context, so with Use
        /// Ecology off the node density below is what actually places, not the
        /// per-hectare dial. (Step 31 audit corrected this comment from claiming
        /// manager PRESENCE alone was enough.)
        /// </remarks>
        protected float CeilingDensityPerSqKm(TileData tile)
        {
            Ecology.EcoScatterContext ctx = tile != null ? tile.ecology : null;
            if (ctx == null) return density;

            float perM2 = Ecology.EcoDensity.CeilingPerSquareMetre(Channel, in ctx.settings, density);
            return perM2 * 1e6f;
        }

        /// <summary>
        /// Surface normal at a world position, by central difference on the height
        /// field. Per-instance rather than a whole-field Sobel pass, because only
        /// the accepted candidates need it.
        /// </summary>
        /// <remarks>
        /// Internal rather than protected so DebrisShaping can reuse it. It is the
        /// only normal implementation in the scatter path and must stay that way -
        /// a second copy would drift from this one silently.
        /// </remarks>
        internal static Vector3 SampleNormal(Matrix height, float worldX, float worldZ)
        {
            if (height == null) return Vector3.up;

            Vector2 ps = height.PixelSize;
            if (ps.x <= 1e-9f || ps.y <= 1e-9f) return Vector3.up;

            Vector2 p = height.WorldToPixel(worldX, worldZ);

            float hl = height.GetInterpolated(p.x - 1f, p.y);
            float hr = height.GetInterpolated(p.x + 1f, p.y);
            float hd = height.GetInterpolated(p.x, p.y - 1f);
            float hu = height.GetInterpolated(p.x, p.y + 1f);

            // Rise is normalised height scaled back to metres; run is two pixels.
            float dx = (hr - hl) * height.worldHeight / (2f * ps.x);
            float dz = (hu - hd) * height.worldHeight / (2f * ps.y);

            return new Vector3(-dx, 1f, -dz).normalized;
        }

        /// <summary>
        /// §4O item 6. World Y that seats an instance's whole base into the ground, rather
        /// than balancing its origin on one sample. On a slope a single-point snap puts the
        /// origin at the centre height, so the downhill side of the trunk floats above the
        /// falling ground - the author's "tree bottom has a gap in slope". Conforming DOWN
        /// to the LOWEST ground under the footprint buries the uphill side into the hill and
        /// brings the downhill edge to the surface, so no gap shows anywhere on the base.
        ///
        /// <paramref name="radiusM"/> &lt;= 0 returns <paramref name="centreWorldY"/>
        /// unchanged - the shipped single-point behaviour, which is why the dial is opt-in
        /// and zero-regression. Internal/static so it has one owner, like
        /// <see cref="SampleNormal"/>.
        ///
        /// DEF-7: EIGHT RING SAMPLES, NOT FOUR, AND THE REASON IS THE AUTHOR'S WORDING.
        /// This sampled only N/S/E/W. On a slope running along an axis those four catch the lowest
        /// ground and the base seats correctly - which is why the original gate passed and why the
        /// fix looked done. But on a slope running DIAGONALLY the steepest descent lies between two
        /// of those samples, so the ring never saw the lowest ground under the footprint and the
        /// conform under-dropped by up to 1 - cos(45 deg), about 29% of the fall across the radius.
        /// The downhill side of the base therefore still hung in the air, and it did so ONLY at
        /// certain slope orientations - exactly the author's "the hanging at certain angle
        /// inclination", and exactly the kind of defect an axis-aligned test cannot see.
        ///
        /// Adding the four diagonals cuts the worst-case orientation error from ~29% to ~7.6%
        /// (cos 22.5 deg), because the widest unsampled gap halves from 90 to 45 degrees. The cost
        /// is four more height reads on an instance whose author has opted into conforming at all,
        /// at BAKE time, not per frame - which the mid-tier-phone floor can carry. The diagonal
        /// offsets are r/sqrt(2) per axis so all eight samples sit on the SAME circle of radius r;
        /// using r on each axis would sample a square and over-drop corners by 41%.
        /// </summary>
        internal static float ConformedGroundY(Matrix height, float worldX, float worldZ,
                                               float radiusM, float centreWorldY)
        {
            if (height == null || radiusM <= 0.001f) return centreWorldY;

            // The lowest ground on the footprint ring decides, so the base never floats.
            float minY = centreWorldY;

            float yN = height.GetWorldHeight(worldX, worldZ + radiusM);
            float yS = height.GetWorldHeight(worldX, worldZ - radiusM);
            float yE = height.GetWorldHeight(worldX + radiusM, worldZ);
            float yW = height.GetWorldHeight(worldX - radiusM, worldZ);

            if (yN < minY) minY = yN;
            if (yS < minY) minY = yS;
            if (yE < minY) minY = yE;
            if (yW < minY) minY = yW;

            // DEF-7: the diagonals, on the same circle (r/sqrt(2) per axis).
            const float Diag = 0.70710678f;
            float d = radiusM * Diag;

            float yNE = height.GetWorldHeight(worldX + d, worldZ + d);
            float ySE = height.GetWorldHeight(worldX + d, worldZ - d);
            float ySW = height.GetWorldHeight(worldX - d, worldZ - d);
            float yNW = height.GetWorldHeight(worldX - d, worldZ + d);

            if (yNE < minY) minY = yNE;
            if (ySE < minY) minY = ySE;
            if (ySW < minY) minY = ySW;
            if (yNW < minY) minY = yNW;

            return minY;
        }

        /// <summary>
        /// VEG-2 weighted overlap pruning. Greedy, highest-score-first over the shared
        /// bounded-radius <see cref="SpatialHash"/>: an instance is kept only if its footprint
        /// does not overlap one already kept, so of two colliding instances the bigger/better-
        /// suited one survives. Deterministic (score ties break by original index). Opt-in via
        /// <see cref="resolveOverlaps"/>; a no-op when off or when no footprint/fallback is set.
        /// </summary>
        internal void ResolveInstanceOverlaps(List<ScatterInstance> instances, TileData tile, StopToken stop)
        {
            if (!resolveOverlaps || instances == null || instances.Count < 2 || tile == null) return;

            int n = instances.Count;
            float fallback = Mathf.Max(0f, overlapFallbackRadiusM);
            float[] rad = new float[n];
            float[] score = new float[n];
            float maxR = 0f;
            for (int i = 0; i < n; i++)
            {
                float r = instances[i].footprintRadius > 0f ? instances[i].footprintRadius : fallback;
                rad[i] = r;
                if (r > maxR) maxR = r;
                float suit = instances[i].suitability > 0f ? instances[i].suitability : 1f;
                score[i] = Mathf.Max(1e-4f, r * r) * suit;
            }
            if (maxR <= 0f) return;   // nothing to prune: no stamped footprint and no fallback

            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            System.Array.Sort(order, (a, b) => { int c = score[b].CompareTo(score[a]); return c != 0 ? c : a.CompareTo(b); });

            SpatialHash hash = new SpatialHash(tile.worldPos.x - maxR, tile.worldPos.z - maxR,
                Mathf.Max(1f, tile.worldSize.x) + 2f * maxR, Mathf.Max(1f, tile.worldSize.z) + 2f * maxR,
                Mathf.Max(0.5f, maxR));

            bool[] keep = new bool[n];
            List<int> hashToInst = new List<int>(n);
            List<int> q = new List<int>(16);
            for (int oi = 0; oi < n; oi++)
            {
                if (stop != null && (oi & 255) == 0 && stop.StopRequested) return;
                int i = order[oi];
                Vector3 p = instances[i].position;
                q.Clear();
                hash.QueryRadius(p.x, p.z, rad[i] + maxR, q);
                bool overlaps = false;
                for (int k = 0; k < q.Count; k++)
                {
                    int j = hashToInst[q[k]];
                    float dx = instances[j].position.x - p.x, dz = instances[j].position.z - p.z;
                    float rr = rad[i] + rad[j];
                    if (dx * dx + dz * dz < rr * rr) { overlaps = true; break; }
                }
                if (overlaps) continue;
                keep[i] = true;
                hash.Insert(p.x, p.z);
                hashToInst.Add(i);
            }

            int kept = 0;
            for (int i = 0; i < n; i++) if (keep[i]) instances[kept++] = instances[i];
            if (kept < n) instances.RemoveRange(kept, n - kept);
        }

        /// <summary>
        /// VEG-5 smooth noise-band thinning. Drops every candidate whose tileable fBm value
        /// (0-1) falls outside the keep band, so the surviving field clumps and clears instead
        /// of reading Poisson-even. Opt-in via <see cref="useNoiseBandThinning"/>; a no-op when
        /// off. The fBm uses absolute world coords so chunks agree across seams.
        /// </summary>
        protected List<ScatterCandidate> ApplyNoiseBandThinning(List<ScatterCandidate> candidates, StopToken stop)
        {
            if (!useNoiseBandThinning || candidates == null || candidates.Count == 0) return candidates;

            float size = Mathf.Max(1f, noiseBandScaleM);
            float lo = Mathf.Clamp01(Mathf.Min(noiseBandKeepLow, noiseBandKeepHigh));
            float hi = Mathf.Clamp01(Mathf.Max(noiseBandKeepLow, noiseBandKeepHigh));
            Noise n = new Noise(seed + 5003);

            int kept = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return candidates;
                ScatterCandidate c = candidates[i];
                if (!NoiseBandKeeps(n, c.worldX, c.worldZ, size, lo, hi)) continue;
                candidates[kept++] = c;
            }
            if (kept < candidates.Count) candidates.RemoveRange(kept, candidates.Count - kept);
            return candidates;
        }

        /// <summary>
        /// The 0-1 thinning-noise value at a world position: a tileable fBm mapped and clamped
        /// into [0,1]. Separated out so the keep decision and the self-test read the SAME value.
        /// </summary>
        internal static float NoiseBandValue(Noise n, float worldX, float worldZ, float size)
        {
            return Mathf.Clamp01(n.Fractal3D(worldX, 0f, worldZ, size) * 0.5f + 0.5f);
        }

        /// <summary>
        /// The pure per-point keep decision for VEG-5: true when the 0-1 thinning noise lies
        /// within [lo, hi]. Static and side-effect-free so the thinning is deterministic and
        /// directly self-testable.
        /// </summary>
        internal static bool NoiseBandKeeps(Noise n, float worldX, float worldZ, float size, float lo, float hi)
        {
            float v = NoiseBandValue(n, worldX, worldZ, size);
            return v >= lo && v <= hi;
        }

        /// <summary>
        /// VEG-3 feature-distance filter. Rejects every candidate within <see cref="avoidRadiusM"/>
        /// of an instance already on <see cref="avoidChannel"/> in the tile's output bag. Opt-in;
        /// a no-op when the channel is empty or unset. The avoided channel must have evaluated
        /// first for its instances to be present.
        /// </summary>
        protected List<ScatterCandidate> ApplyFeatureDistanceFilter(
            List<ScatterCandidate> candidates, TileData tile, StopToken stop)
        {
            if (string.IsNullOrEmpty(avoidChannel) || avoidRadiusM <= 0f) return candidates;
            if (tile == null || tile.outputs == null) return candidates;

            List<ScatterInstance> feats;
            if (!tile.outputs.scatter.TryGetValue(avoidChannel, out feats) || feats == null || feats.Count == 0)
                return candidates;

            List<Vector2> pts = new List<Vector2>(feats.Count);
            for (int i = 0; i < feats.Count; i++)
                pts.Add(new Vector2(feats[i].position.x, feats[i].position.z));

            return FilterByFeatureDistance(candidates, pts, avoidRadiusM);
        }

        /// <summary>
        /// Removes every candidate within <paramref name="radius"/> of any feature point, over a
        /// bounded-radius <see cref="SpatialHash"/>. Static and side-effect-free (bar compacting the
        /// list it is handed) so the exclusion is directly self-testable. The hash is sized to the
        /// features; a candidate far outside their extent clamps to an edge cell and correctly finds
        /// nothing within range.
        /// </summary>
        internal static List<ScatterCandidate> FilterByFeatureDistance(
            List<ScatterCandidate> candidates, List<Vector2> features, float radius)
        {
            if (candidates == null || candidates.Count == 0) return candidates;
            if (features == null || features.Count == 0 || radius <= 0f) return candidates;

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < features.Count; i++)
            {
                Vector2 f = features[i];
                if (f.x < minX) minX = f.x;
                if (f.x > maxX) maxX = f.x;
                if (f.y < minZ) minZ = f.y;
                if (f.y > maxZ) maxZ = f.y;
            }

            SpatialHash hash = new SpatialHash(minX, minZ, Mathf.Max(1f, maxX - minX),
                Mathf.Max(1f, maxZ - minZ), Mathf.Max(0.5f, radius));
            for (int i = 0; i < features.Count; i++) hash.Insert(features[i].x, features[i].y);

            int kept = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                ScatterCandidate c = candidates[i];
                if (hash.HasNeighbourWithin(c.worldX, c.worldZ, radius)) continue;
                candidates[kept++] = c;
            }
            if (kept < candidates.Count) candidates.RemoveRange(kept, candidates.Count - kept);
            return candidates;
        }

        /// <summary>
        /// VEG-9 composable filter stack. Keeps only candidates that pass the author's typed
        /// predicate stack (AND/OR) over height / slope / mask / noise. Opt-in; a no-op when off or
        /// empty. Noise reuses the same tileable fBm VEG-5 thins with, so a predicate on Noise and
        /// the noise-band thinning read one coherent field.
        /// </summary>
        protected List<ScatterCandidate> ApplyFilterStack(List<ScatterCandidate> candidates, StopToken stop)
        {
            if (!useFilterStack || filterStack == null || filterStack.Count == 0 || candidates == null)
                return candidates;

            Noise n = new Noise(seed + 6007);
            float size = Mathf.Max(1f, noiseBandScaleM);

            int kept = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return candidates;
                ScatterCandidate c = candidates[i];
                float noise01 = NoiseBandValue(n, c.worldX, c.worldZ, size);
                if (!ScatterFilterStack.Keep(filterStack, filterGroupOp, c.height, c.slope, c.maskValue, noise01))
                    continue;
                candidates[kept++] = c;
            }
            if (kept < candidates.Count) candidates.RemoveRange(kept, candidates.Count - kept);
            return candidates;
        }

        /// <summary>
        /// VEG-11: builds decorator candidates at a deterministic fraction of the source channel's
        /// instance positions (read from the tile's output bag). Empty/missing source = nothing to
        /// decorate. The decorated channel must have evaluated first.
        /// </summary>
        protected List<ScatterCandidate> BuildDecoratorCandidates(TileData tile)
        {
            List<ScatterCandidate> result = new List<ScatterCandidate>();
            if (tile == null || tile.outputs == null) return result;

            List<ScatterInstance> src;
            if (!tile.outputs.scatter.TryGetValue(decorateChannel, out src) || src == null || src.Count == 0)
                return result;

            List<Vector2> pts = new List<Vector2>(src.Count);
            for (int i = 0; i < src.Count; i++)
                pts.Add(new Vector2(src[i].position.x, src[i].position.z));

            return DecoratorCandidatesFrom(pts, decorateKeepFraction, new Noise(seed + 7013), result);
        }

        /// <summary>
        /// Keeps a deterministic fraction of source positions as decorator candidates, by a
        /// position-hashed draw. keepFraction 1 keeps all, 0 keeps none. Pure/self-testable.
        /// </summary>
        internal static List<ScatterCandidate> DecoratorCandidatesFrom(
            List<Vector2> sourcePositions, float keepFraction, Noise hash, List<ScatterCandidate> into)
        {
            if (into == null) into = new List<ScatterCandidate>();
            if (sourcePositions == null || hash == null) return into;

            float keep = Mathf.Clamp01(keepFraction);
            for (int i = 0; i < sourcePositions.Count; i++)
            {
                Vector2 p = sourcePositions[i];
                int hx = Mathf.RoundToInt(p.x * 100f), hz = Mathf.RoundToInt(p.y * 100f);
                if (keep >= 1f || hash.Random(hx, hz) < keep)
                    into.Add(new ScatterCandidate { worldX = p.x, worldZ = p.y });
            }
            return into;
        }

        /// <summary>
        /// VEG-10: builds candidates on a regular Grid or concentric Ring pattern, keeping only
        /// those inside the admissible band (filter weight >= 0.5) and up to the chunk cap. The
        /// position generators are pure/static so the pattern geometry is directly self-testable.
        /// </summary>
        protected List<ScatterCandidate> GeneratePatternCandidates(
            TileData tile, ScatterFilter filter, int maxPoints, System.Func<bool> isCancelled)
        {
            List<Vector2> positions = new List<Vector2>();
            float spacing = Mathf.Max(0.25f, patternSpacingM);

            if (placementMode == ScatterPlacementMode.Ring)
                GenerateRingPositions(tile.worldPos.x, tile.worldPos.z,
                                      tile.worldSize.x, tile.worldSize.z, spacing, positions);
            else
                GenerateGridPositions(tile.worldPos.x, tile.worldPos.z,
                                      tile.worldSize.x, tile.worldSize.z, spacing, positions);

            int cap = Mathf.Max(1, maxPoints);
            List<ScatterCandidate> result = new List<ScatterCandidate>(Mathf.Min(cap, positions.Count));
            for (int i = 0; i < positions.Count; i++)
            {
                if (isCancelled != null && (i & 255) == 0 && isCancelled()) break;
                if (result.Count >= cap) break;
                Vector2 p = positions[i];
                if (filter.EvaluateWorld(p.x, p.y) < 0.5f) continue;   // outside the admissible band
                result.Add(new ScatterCandidate { worldX = p.x, worldZ = p.y });
            }
            return result;
        }

        /// <summary>Fills a regular square lattice of world positions over the rect (inclusive).</summary>
        internal static void GenerateGridPositions(float ox, float oz, float sizeX, float sizeZ,
                                                   float spacing, List<Vector2> into)
        {
            if (into == null || spacing <= 0f) return;
            for (float z = oz; z <= oz + sizeZ + 1e-3f; z += spacing)
                for (float x = ox; x <= ox + sizeX + 1e-3f; x += spacing)
                    into.Add(new Vector2(x, z));
        }

        /// <summary>Fills concentric rings of world positions about the rect centre.</summary>
        internal static void GenerateRingPositions(float ox, float oz, float sizeX, float sizeZ,
                                                   float spacing, List<Vector2> into)
        {
            if (into == null || spacing <= 0f) return;
            float cx = ox + sizeX * 0.5f, cz = oz + sizeZ * 0.5f;
            float maxR = 0.5f * Mathf.Min(sizeX, sizeZ);
            for (float r = spacing; r <= maxR + 1e-3f; r += spacing)
            {
                int n = Mathf.Max(3, Mathf.RoundToInt(2f * Mathf.PI * r / spacing));
                for (int k = 0; k < n; k++)
                {
                    float a = (2f * Mathf.PI * k) / n;
                    into.Add(new Vector2(cx + r * Mathf.Cos(a), cz + r * Mathf.Sin(a)));
                }
            }
        }

        /// <summary>
        /// GEO-3: the yaw in degrees that faces the terrain FLOW (steepest descent) at a point,
        /// taken from the height-field normal whose XZ projection already points downslope. Returns
        /// false on flat ground, where there is no flow direction to align to. Pure/static so the
        /// orientation is deterministic and directly self-testable.
        /// </summary>
        internal static bool TryFlowYaw(Vector3 normal, out float yawDeg)
        {
            Vector2 flow = new Vector2(normal.x, normal.z);
            if (flow.sqrMagnitude < 1e-6f) { yawDeg = 0f; return false; }
            yawDeg = Mathf.Atan2(flow.x, flow.y) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// CTL-4: the prototype slot a NAMED species occupies in this world, or -1 when the name
        /// resolves to nothing. Shared by the tree and shrub nodes so the lookup, the warning and
        /// the "names win over indices" rule exist exactly once.
        /// </summary>
        /// <remarks>
        /// Resolution goes through the ecology species table, which is where the authored names
        /// already live (<c>EcoEnvelope.displayName</c>) and which is already flattened for worker
        /// reads with index i == prototype slot i. It therefore reads strings and ints only - never
        /// a GameObject - so it is legal from Generate on a Task thread.
        ///
        /// Returns -1 (never 0) for every failure: no ecology context, no table for the channel, a
        /// table nobody has described, or a name that is simply absent. The caller must then place
        /// NOTHING. Falling back to slot 0 would plant a species the author never asked for and
        /// look like success, which is the fault naming exists to remove.
        ///
        /// The warning is the author's only signal, so it names the node, the channel and the
        /// species. It is a warning rather than an error because a world that has not had its
        /// content assigned yet is a normal authoring state, not a broken project.
        /// </remarks>
        internal static int ResolveNamedSlot(TileData tile, string channel,
                                             string speciesName, string nodeName)
        {
            if (string.IsNullOrEmpty(speciesName)) return -1;

            Ecology.EcoScatterContext ctx = tile != null ? tile.ecology : null;
            if (ctx == null)
            {
                Debug.LogWarning("Universe Creator: " + nodeName + " asks for species '"
                    + speciesName + "' but this world has no ecology context, so no species "
                    + "names exist to resolve against. Nothing placed.");
                return -1;
            }

            Ecology.SpeciesTable table = ctx.TableFor(channel);
            if (table == null)
            {
                Debug.LogWarning("Universe Creator: " + nodeName + " asks for species '"
                    + speciesName + "' but the '" + channel + "' channel has no species table. "
                    + "Nothing placed.");
                return -1;
            }

            int slot = table.FindSlotByName(speciesName);
            if (slot < 0)
            {
                Debug.LogWarning("Universe Creator: " + nodeName + " asks for species '"
                    + speciesName + "' in the '" + channel + "' channel, and no prototype slot "
                    + "carries that name. Nothing placed - fix the name or describe the species, "
                    + "rather than relying on a slot number.");
            }

            return slot;
        }

        /// <summary>
        /// RIV-4 W3: the yaw in degrees that lies ALONG a river at a point, read from the River
        /// Mask's gradient. The mask falls off across the channel, so the gradient points across
        /// the water and the course direction is its perpendicular in XZ. Returns false where the
        /// mask is flat (no gradient = no river direction to read), in which case the caller must
        /// keep the yaw it already had. Pure/static so the orientation is deterministic and
        /// directly self-testable, exactly like <see cref="TryFlowYaw"/>.
        /// </summary>
        internal static bool TryRiverBankYaw(Matrix mask, float worldX, float worldZ, out float yawDeg)
        {
            yawDeg = 0f;
            if (mask == null || mask.arr == null) return false;

            int sx = mask.rect.size.x;
            int sz = mask.rect.size.z;
            if (sx < 3 || sz < 3) return false;

            Vector2 p = mask.WorldToPixel(worldX, worldZ);
            int x = Mathf.Clamp(Mathf.RoundToInt(p.x), 1, sx - 2);
            int z = Mathf.Clamp(Mathf.RoundToInt(p.y), 1, sz - 2);

            // Central differences, the same shape SampleNormal uses for the height gradient.
            float gx = mask.arr[z * sx + (x + 1)] - mask.arr[z * sx + (x - 1)];
            float gz = mask.arr[(z + 1) * sx + x] - mask.arr[(z - 1) * sx + x];

            if (gx * gx + gz * gz < 1e-10f) return false;   // flat mask: no direction to read

            // Perpendicular to the across-channel gradient, in the same Atan2(dx, dz) compass
            // convention TryFlowYaw uses. The 180-degree-opposite answer is the same AXIS, which
            // is all an elongated bank instance needs.
            yawDeg = Mathf.Atan2(gz, -gx) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// BIO-9: keeps only candidates whose classified biome (sampled from the Biome field at the
        /// candidate position and decoded) equals <paramref name="onlyInBiome"/>. A null field (Biome
        /// input unwired) leaves the list untouched - the gate cannot act on information it was not given.
        /// </summary>
        protected List<ScatterCandidate> ApplyBiomeGate(
            List<ScatterCandidate> candidates, Matrix biomeField, int onlyInBiome, StopToken stop)
        {
            if (candidates == null || candidates.Count == 0 || biomeField == null || onlyInBiome < 0)
                return candidates;

            int kept = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return candidates;
                ScatterCandidate c = candidates[i];
                Vector2 p = biomeField.WorldToPixel(c.worldX, c.worldZ);
                float encoded = biomeField.GetInterpolated(p.x, p.y);
                if (!BiomeGateKeeps(encoded, onlyInBiome)) continue;
                candidates[kept++] = c;
            }
            if (kept < candidates.Count) candidates.RemoveRange(kept, candidates.Count - kept);
            return candidates;
        }

        /// <summary>
        /// Pure BIO-9 decision: true when no gate is set (onlyInBiome &lt; 0) or the encoded biome
        /// value decodes to the gated class. Static/self-testable.
        /// </summary>
        internal static bool BiomeGateKeeps(float encodedBiome, int onlyInBiome)
        {
            if (onlyInBiome < 0) return true;
            return Ecology.WhittakerBiome.Decode(encodedBiome) == onlyInBiome;
        }

        /// <summary>
        /// VEG-13: a stable per-instance id from the quantised world position (centimetres), so the
        /// same site yields the same id on every re-bake and machine - the key a persistent
        /// removed/changed delta (PERF-2) and an editor erase brush address an instance by. Pure.
        /// </summary>
        internal static int StableInstanceId(float worldX, float worldZ)
        {
            int hx = Mathf.RoundToInt(worldX * 100f);
            int hz = Mathf.RoundToInt(worldZ * 100f);
            unchecked
            {
                uint h = (uint)(hx * 73856093) ^ (uint)(hz * 19349663);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (int)(h & 0x7FFFFFFF);   // positive; 0 stays reserved for "unassigned"
            }
        }

        /// <summary>
        /// Per-type placement rules. Receives the accepted candidate list from the
        /// shared solver and may filter, duplicate, or re-parameterise it.
        /// </summary>
        protected abstract void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop);
    }
}
