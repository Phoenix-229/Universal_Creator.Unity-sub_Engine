// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterBody.cs
//  Purpose: A water body the user placed by hand. An ocean, a lake, a pond or a
//           river drawn as a spline, built from the terrain that is actually under
//           it, with the same mesh contract and the same physics the graph path
//           produces.
//
//  WHY THIS EXISTS ALONGSIDE THE GRAPH NODES
//  -----------------------------------------
//  The graph is procedural: it floods whatever the height field says should be
//  flooded, everywhere, at once. That is the right answer for a world where the
//  water is part of the terrain design and the wrong answer for one lake the
//  designer wants in one particular valley.
//
//  So there are two ways in, and they are deliberately different gestures:
//
//    graph node  - "flood this whole world to this level"
//    this        - "there is a lake HERE, this shape, this deep"
//
//  Both end at the same WaterBodyData, the same UC_Water shader and the same
//  UCWaterVolume, so a boat cannot tell them apart and neither can the ripple
//  simulation.
//
//  NOTHING HERE IS AUTOMATIC
//  -------------------------
//  No part of Universe Creator creates one of these. It appears when a user adds
//  the component or uses the GameObject menu, and never otherwise. A project that
//  wants no water gets no water: no output node in the graph and no component in
//  the scene means the water code never runs at all.
//
//  MOBILE BUDGET
//  -------------
//  One mesh per body, coarse lattice, 16-bit indices, shadows off both ways. The
//  hydraulics run once at build time on the main thread and produce a vertex
//  stream; nothing here costs anything per frame except the draw call.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// A hand-placed body of water. Add it, choose a shape, press Build.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Water Body")]
    [DisallowMultipleComponent]
    public partial class UCWaterBody : MonoBehaviour, IUCTerrainCarver // UC-T04-01: versioned body-mode migration shares the existing owner.
    {
        // =====================================================================
        //  ENUMS
        // =====================================================================

        public enum ShapeMode
        {
            /// <summary>Axis-aligned rectangle centred on the transform. Oceans,
            /// square pools, and any lake whose exact outline does not matter
            /// because the terrain around it defines the shoreline anyway.</summary>
            Rectangle = 0,

            /// <summary>A closed spline outline. Use when the shoreline must follow
            /// a shape the terrain does not already imply.</summary>
            Polygon = 1,

            /// <summary>An open spline with a width. This is the river.</summary>
            RiverSpline = 2
        }

        public enum BedSource
        {
            /// <summary>Depth comes from the Unity terrain under the body. The
            /// shoreline is then exactly where the ground crosses the water level,
            /// with no authoring needed.</summary>
            Terrain = 0,

            /// <summary>Depth is a fixed number everywhere. For a body with no
            /// terrain under it - a built pool, a cistern, a boat scene with no
            /// landscape - and for placing water before the terrain exists.</summary>
            FlatFloor = 1
        }

        public enum BuildTrigger
        {
            /// <summary>Builds when the object is enabled and whenever a field is
            /// committed in the inspector. The usual choice.</summary>
            Automatic = 0,

            /// <summary>Builds only when Build Now is pressed. For a heavy body you
            /// do not want rebuilt every time you nudge a slider.</summary>
            Manual = 1
        }

        // =====================================================================
        //  BODY
        // =====================================================================

        [Header("Body")]

        [Tooltip("What this is. Sets the physics body kind and the water density - " +
                 "an Ocean is salt water at 1025 kg/m3, everything else is fresh at " +
                 "998, and that is what decides how high a boat sits.\n\n" +
                 "Fixed when the body is created. A lake stays a lake.")]
        [SerializeField] private WaterBodyKind bodyKind = WaterBodyKind.Lake;

        [Tooltip("Set once, by whatever created this body. Its presence is what marks " +
                 "the kind as settled.")]
        [SerializeField, HideInInspector] private bool kindLocked;

        /// <summary>
        /// What this body is. Read-only after creation.
        ///
        /// WHY THIS IS NOT AN EDITABLE FIELD
        /// ---------------------------------
        /// Kind is not a label, it is a set of decisions that other fields were
        /// authored against: an ocean carries a continental shelf, a horizon plane and
        /// salt-water density; a river carries a course, a width and Manning flow; a
        /// pool carries a flat floor and no natural law at all. Switching a finished
        /// lake to a river would leave a closed outline with no course, a shelf
        /// profile on a channel body, and a shoreline pen mark describing a shape the
        /// new kind cannot use. The old behaviour let a user do that in one click and
        /// then wonder why nothing built.
        ///
        /// So the kind is authored once, by the preset that spawns the body, and after
        /// that the inspector shows it as text. Making a river out of a lake means
        /// creating a river, which takes the same one click and leaves the lake intact
        /// until the user deletes it.
        /// </summary>
        public WaterBodyKind kind { get { return bodyKind; } }

        /// <summary>True once the kind has been settled by a creation preset.</summary>
        public bool KindIsLocked { get { return kindLocked; } }

        /// <summary>
        /// Sets the kind and locks it. Called by the spawn presets and by nothing
        /// else; a second call is ignored, so a body cannot be re-kinded by a script
        /// that finds it later.
        /// </summary>
        public void SetKindOnce(WaterBodyKind value)
        {
            if (kindLocked) return;

            bodyKind = value;
            kindLocked = true;

            ApplyKindUnderwaterDefaults(value);
        }

        /// <summary>
        /// Seeds the underwater look for the kind being spawned.
        ///
        /// Only ever called from SetKindOnce, so it runs once, at creation, before
        /// the user has touched anything - it can never overwrite authored values.
        /// A single shared default would be wrong for three of the four kinds, so
        /// the numbers come from UCWaterVolume, which is also where the graph path
        /// reads them: a lake dives the same whether it was dropped by hand or
        /// produced by a node, because there is one table and not two.
        /// </summary>
        private void ApplyKindUnderwaterDefaults(WaterBodyKind value)
        {
            UCWaterVolume.UnderwaterDefaultsFor((UCWaterVolume.BodyKind)(int)value,
                                                out underwaterColor,
                                                out visibilityMetres,
                                                out underwaterBrightness);
        }

        [Tooltip("How the outline is described. Rectangle and Polygon are level " +
                 "bodies; River Spline follows the ground downhill and flows.")]
        public ShapeMode shape = ShapeMode.Rectangle;

        [Tooltip("When the mesh is rebuilt. Nothing is ever built without one of " +
                 "these having happened - this component is the only thing in the " +
                 "scene that can create this water.")]
        public BuildTrigger buildTrigger = BuildTrigger.Automatic;

        // =====================================================================
        //  LEVEL BODY - RECTANGLE AND POLYGON
        // =====================================================================

        [Header("Level Body (Rectangle / Polygon)")]

        [Tooltip("Footprint in metres, centred on this object. Rectangle only.")]
        public Vector2 sizeMetres = new Vector2(200f, 200f);

        [Tooltip("Closed outline in local metres. Polygon only. Edit the points in " +
                 "the scene view, or use the buttons below to seed a shape.")]
        public Polyline outline = new Polyline();

        [Tooltip("Per-course-node hydraulic payload (half-width, depth, discharge, " +
                 "velocity) for a River Spline body. Each channel is empty (the solver " +
                 "derives it) or one value per outline node; 0 inherits the previous " +
                 "node. River Spline only - a lake, pool or ocean ignores it.")]
        public RiverProfile riverProfile = new RiverProfile();

        [Header("Natural River (River Spline only)")]
        [Range(0f, 2f)]
        [Tooltip("How natural the river looks: 0 = clean geometric channel, 1 = natural (pool-and-riffle bed, " +
                 "wandering banks, uneven flood-plain), 2 = very rugged.")]
        public float ruggedness = 1f;

        /// <summary>Authored between-point meander strength, 0..3; build-time clamping rejects nonfinite input and preserves endpoints.</summary>
        [Range(0f, 3f)]
        [Tooltip("Gentle sideways wander added between your nodes, so a few clicks give a river that bends " +
                 "like a real one instead of a stiff curve. 0 = follows the nodes exactly. Ends stay on the nodes.")]
        public float meander = 1f;

        [Tooltip("Seed for the natural variation. Change it for a different but equally natural river.")]
        public int naturalSeed = 0;

        [Tooltip("Most ground beside the channel that may be raised to meet the water surface, in metres. " +
                 "This is what keeps the water from ever hanging over a drop-off.")]
        public float maxBankRaiseMetres = 2f;

        /// <summary>Legacy serialized read-trench flag, retained for migration and recovery; after migration use TerrainMode.</summary>
        [HideInInspector] // UC-T04-01: keep the serialized name and original value; new authoring has one body mode.
        public bool followProceduralTrench = false;

        /// <summary>Legacy valley-snap distance in metres; preserved for saved data, no longer used by placement.</summary>
        [HideInInspector] // UC-T04-05B: author clicks set positions exactly; never relocate them implicitly.
        public float valleySnapMetres = 10f;

        // The solved bed is remembered with a signature of the course. A rebuild with an unchanged course
        // REUSES it instead of re-solving from whatever ground is there now - and the ground there now may
        // already carry this river's own trench (the graph's River Course Input cut it, or the previous
        // dig did), which made every re-solve cut one channel-depth deeper than the last: a river that
        // sank a little further each time the scene reloaded or the terrain regenerated.
        [SerializeField, HideInInspector] private float[] cachedBedY;
        [SerializeField, HideInInspector] private int cachedBedSig;
        [SerializeField, HideInInspector] private float cachedBedSlope;
        [System.NonSerialized] private bool _forceResolveBed;

        /// <summary>Re-solves the bed from the terrain as it is NOW and rebuilds. Use after the terrain has
        /// been regenerated into a different shape, so the river is re-fitted to the new valley.</summary>
        public void ResolveBedFromTerrain()
        {
            _forceResolveBed = true;
            cachedBedY = null;
            Rebuild();
        }

        private static int BedSignature(List<Vector3> course, float[] depth)
        {
            unchecked
            {
                int h = 17 + course.Count;
                for (int i = 0; i < course.Count; i++)
                {
                    h = h * 31 + Mathf.RoundToInt(course[i].x * 4f);
                    h = h * 31 + Mathf.RoundToInt(course[i].z * 4f);
                    if (depth != null && i < depth.Length) h = h * 31 + Mathf.RoundToInt(depth[i] * 100f);
                }
                return h;
            }
        }

        /// <summary>True when the authoritative body mode reads committed procedural trench data; legacy settings migrate once on first use.</summary>
        public bool FollowTrench { get { return TerrainMode == RiverTerrainMode.ReadProceduralTrench; } } // UC-T04-01: child flags no longer change a migrated body's mode.
        /// <summary>UC-T03-09: latest accepted-channel lookup result for procedural read mode; absence prevents excavation.</summary>
        public RiverChannelValidity LastChannelValidity { get; private set; } = RiverChannelValidity.NoChannel;

        [Tooltip("LAKES / PONDS: rivers that START at this water body (its outlets). A river whose first " +
                 "node lies in this body registers itself here automatically. The lake is then the SOURCE " +
                 "of that river - it gives the river water rather than receiving it - and the river's " +
                 "first reach is held at the lake's water level so the two surfaces are one sheet.")]
        public List<UCWaterBody> riverOutlets = new List<UCWaterBody>();

        /// <summary>The lake/pond this river rises from (null if it starts on land). Set at build time.</summary>
        [System.NonSerialized] public UCWaterBody sourceBody;

        /// <summary>The lake/ocean this river empties into (null if it ends on land or joins a river).</summary>
        [System.NonSerialized] public UCWaterBody mouthBody;

        /// <summary>Per control node: 1 = flood plain here, ~0 = none. Built from the nodes' Flood Plain ticks.</summary>
        [System.NonSerialized] public List<float> nodeFloodPlain = new List<float>();

        // UC-T04-06: describe the computational extension and actual geometric acceptance.
        [Tooltip("Optional receiving river. The generated course extends to its channel without moving authored nodes. " +
                 "Owned accepted coverage also detects river connections automatically. Pending or incompatible receivers remain diagnostic.")]
        public UCWaterBody joinsRiver;

        [Tooltip("JOIN: how far along the join the channel is widened and blended, in metres, so the " +
                 "confluence is a smooth mouth rather than a notch.")]
        public float confluenceBlendMetres = 25f;

        [Tooltip("Raises or lowers the water surface relative to this object, in " +
                 "metres. The object's own Y is the still-water level; this is the " +
                 "fine adjustment you make while looking at the shoreline.")]
        public float surfaceOffsetMetres;

        [Tooltip("Steady surface current in world XZ, metres per second. A level " +
                 "body has no slope, so Manning's equation gives it no flow of its " +
                 "own - this is the wind-driven drift a real lake has, and what " +
                 "makes the surface scroll instead of sitting dead still.")]
        public Vector2 currentMetresPerSecond = new Vector2(0.06f, 0.03f);





        // =====================================================================
        //  PROCEDURAL POLICY  (what happens when the terrain is regenerated)
        //
        //  A generated terrain and a placed water body are two authorities over
        //  the same heights, and the obvious failure is the generator winning: you
        //  cut a river, you change a noise setting, and the channel is gone.
        //
        //  It does not happen here, and the reason is structural rather than a
        //  setting: the dig is not an edit stored in the heightmap, it is a rule
        //  re-applied at the end of every chunk build. The generator writes its
        //  heights, the dig cuts them, the chunk is applied. Regenerate all day -
        //  the last thing to touch the ground is always the water.
        //
        //  What this section adds on top is the corridor: the ground immediately
        //  around the channel is eased toward the banks the river needs, so the
        //  terrain is built AROUND the water rather than merely being cut by it
        //  afterwards. Without it a regenerated ridge can land against the bank and
        //  the river reads as a slot sawn through a cliff.
        //
        //  And the placement half of it: whether the course is re-fitted to the new
        //  ground automatically, or waits for you to press Build.
        // =====================================================================

        /// <summary>How a body answers a terrain regeneration.</summary>
        public enum ProceduralResponse
        {
            /// <summary>Re-fit the course to the new ground the moment it settles.
            /// What a river wants: the valleys moved, so the river moves.</summary>
            RefitAutomatically = 0,

            /// <summary>Keep the course exactly where it was solved and re-cut it into
            /// whatever the generator produced. What a lake, a pool and an ocean want:
            /// their footprint is a placement, not a guess at the ground.</summary>
            HoldPlacement = 1,

            /// <summary>Do nothing until Build is pressed.</summary>
            WaitForBuild = 2
        }

        [Header("Procedural")]

        [Tooltip("Whether a graph regeneration or a cell rebuild re-places this body.\n\n" +
                 "Re-fit Automatically re-runs the valley fit against the new ground, " +
                 "so a river settles into the valleys the generator just made. This is " +
                 "what happens when the body is driven by a node in the graph.\n\n" +
                 "Hold Placement keeps the shape where you put it and simply re-cuts " +
                 "it - right for lakes, pools and oceans, whose footprint is a decision " +
                 "rather than a guess.\n\n" +
                 "Wait For Build leaves it alone until you press Build.")]
        public ProceduralResponse onRegenerate = ProceduralResponse.RefitAutomatically;

        [Tooltip("Set by the Save Placement button once you have hand-adjusted a " +
                 "procedural body. A saved body is pinned exactly like a hand-placed " +
                 "one: the generator's reconcile-down and its disconnect teardown both " +
                 "leave it alone, and its footprint is held and re-cut into the new " +
                 "terrain rather than re-fitted - so your edit is merged with the new " +
                 "world, not overwritten.")]
        public bool placementSaved = false;

        [Tooltip("Width of the ground the terrain is shaped around the channel, in " +
                 "metres beyond the bank. Inside it the generated heights are eased " +
                 "toward the banks the water needs, so the river sits in a valley the " +
                 "terrain agrees with instead of in a slot cut through whatever the " +
                 "generator happened to put there.\n\n" +
                 "0 hands a river to Valley Width Multiple below, which derives the " +
                 "corridor from the channel's own width. A number typed here is an " +
                 "absolute override and wins.")]
        public float corridorMetres = 0f;

        [Tooltip("Valley width for a river, as a multiple of the channel's own width, " +
                 "used when Corridor Metres is 0. A valley floor scales with the " +
                 "channel that cut it, so this one number gives a 4 m brook a narrow " +
                 "shoulder and an 80 m trunk a broad floodplain.\n\n" +
                 "0 here AND 0 above means no corridor at all: the dig cuts what it " +
                 "finds. Level bodies ignore this - a lake's shore is its bevel.")]
        public float valleyWidthMultiple = 3f;

        // Bounds on the derived valley. Below the first a corridor is thinner than the
        // heightmap's own texel spacing and cannot express a ramp at all; above the
        // second it reaches far enough to reshape ground that has nothing to do with
        // the river, and every extra metre widens the area each chunk apply samples.
        private const float MinValleyCorridorMetres = 8f;
        private const float MaxValleyCorridorMetres = 160f;

        /// <summary>
        /// The corridor this body will actually be dug with, in metres beyond the bank.
        /// The absolute override wins; otherwise a river derives it from its own width.
        /// A level body has no corridor - its shore is its bevel - and neither has an
        /// ocean, which rounds its beach and shapes nothing else.
        ///
        /// The dig reads this, and so does the inspector, so the greyed state and the
        /// number reported are the ones the build uses rather than a second opinion.
        /// </summary>
        public float ValleyCorridorMetres
        {
            get
            {
                if (kind == WaterBodyKind.Ocean) return 0f;
                if (corridorMetres > 0f) return corridorMetres;

                // FIX 2026-10-19: the derivation the tooltip above promises had been reduced to
                // "return 0". With no corridor the dig cut the channel and handed the ground back
                // raw at the bevel's edge, which is how a river became a slot sawn through a
                // hillside. A drawn river now derives its valley from its own channel width.
                // Level bodies are unchanged here (a lake's shore is its bevel; see
                // LakeBankEaseMetres) so the corridor accessor keeps its documented contract.
                if (kind == WaterBodyKind.River && shape == ShapeMode.RiverSpline && valleyWidthMultiple > 0f)
                {
                    float widest = 0f;
                    if (carve != null && carve.riverHalfWidth != null)
                        for (int i = 0; i < carve.riverHalfWidth.Length; i++)
                            if (carve.riverHalfWidth[i] > widest) widest = carve.riverHalfWidth[i];
                    if (widest <= 0f) widest = carve != null ? carve.riverDefaultHalfWidthM : 3f;

                    return Mathf.Clamp(valleyWidthMultiple * widest * 2f,
                                       MinValleyCorridorMetres, MaxValleyCorridorMetres);
                }

                return 0f;
            }
        }

        /// <summary>
        /// Metres beyond a LAKE's bevel over which the surrounding ground is graded so it rises
        /// at a valley's rate instead of standing as a wall. FIX 2026-10-19: a lake used the 6 m
        /// bevel alone, so on a hillside the ground went from waterline to full height in 6 m -
        /// the sharp-walled pit seen around the lakes. 8 x the bevel, kept between 24 and 96 m,
        /// is derived here (not stored) so it applies to bodies already in a scene.
        /// A typed Corridor Metres still wins. Rivers and oceans are not affected.
        /// </summary>
        public float LakeBankEaseMetres
        {
            get
            {
                if (kind == WaterBodyKind.Ocean || kind == WaterBodyKind.River) return 0f;
                if (shape == ShapeMode.RiverSpline) return 0f;
                if (corridorMetres > 0f) return corridorMetres;
                return Mathf.Clamp(shoreBevelMetres * 8f, 24f, 96f);
            }
        }

        [Tooltip("How strongly the corridor pulls the generated ground toward the " +
                 "river's own banks, 0..1. 1 replaces the terrain across the corridor " +
                 "with a clean valley; around 0.5 keeps the generator's character and " +
                 "just stops it fighting the channel.")]
        [Range(0f, 1f)] public float corridorStrength = 0.6f;

        // =====================================================================
        //  BED
        // =====================================================================

        [Header("Bed")]

        public BedSource bedSource = BedSource.Terrain;

        [Tooltip("Depth used when the bed source is Flat Floor, in metres.")]
        public float flatFloorDepthMetres = 3f;

        [Tooltip("Water thinner than this is not water, in metres. Raise it to stop " +
                 "a noisy terrain leaving a rim of one-texel puddles along the " +
                 "shoreline.")]
        public float minDepthMetres = 0.05f;

        [Tooltip("Depth over which the surface fades in at the bank, in metres. " +
                 "Larger hides the mesh edge better and eats further into the " +
                 "shallows.")]
        public float edgeFeatherMetres = 0.6f;

        // =====================================================================
        //  DIG - THE HOLE IN THE GROUND
        //
        //  Water is a surface AND a hole. Without the hole, a river is a ribbon
        //  laid over unbroken ground: it reads as a painted stripe, it has no
        //  banks, and the terrain shows straight through it wherever the ground
        //  rises. Everything below describes the hole.
        //
        //  The dig is re-derived from these fields every time a chunk is applied,
        //  which is why regeneration no longer erases it and why hand-sculpting a
        //  bank afterwards still works.
        // =====================================================================

        [Header("Dig  (carves the terrain)")]

        [Tooltip("Cut this body's bed into the terrain. Off leaves the ground alone " +
                 "and builds only the surface, which is what you want for water in a " +
                 "basin that is already modelled.")]
        public bool digTerrain = true;

        [Tooltip("Shape of the bed under the water.\n\n" +
                 "River Channel is a flow-cut parabola. Lake Basin is a wave-planed " +
                 "shelf then a dish. Continental Shelf is the ocean's long flat wade, " +
                 "a break, then the drop. Flat Floor is a built pool.")]
        public BedProfile bedProfile = BedProfile.LakeBasin;

        [Tooltip("Deepest the bed goes below the water surface, in metres. Level " +
                 "bodies only - a river's depth is Channel Depth above.")]
        public float digDepthMetres = 5f;

        [Tooltip("Optional per-node bed depth for a Polygon lake, in metres, one " +
                 "entry per outline point. A border node over the deep trough asks " +
                 "for a large depth, one on a shelf a small one, and the carve eases " +
                 "between them round the shore. 0 means \"not set here\" and inherits " +
                 "the last authored value, finally Dig Depth. Left empty for a " +
                 "Rectangle and when nothing is authored, and then the flat Dig Depth " +
                 "is used throughout.")]
        public List<float> nodeDepthMetres = new List<float>();

        [Tooltip("How far in from the shore the bed reaches full depth, in metres. " +
                 "Past it the floor is flat. This is what makes a lake read as a lake " +
                 "with a floor rather than a funnel with a point at the bottom.")]
        public float shoreRunMetres = 40f;

        [Tooltip("Fraction of the run from shore to deep taken up by the flat shelf. " +
                 "0.25 is a natural lake margin; an ocean wants 0.5 or more, because " +
                 "a real continental shelf is most of the distance to the deep.")]
        [Range(0.02f, 0.95f)]
        public float shelfFraction = 0.25f;

        [Tooltip("Curve of the bed as it falls from the shelf to full depth. 1 is a " +
                 "straight ramp; higher hugs the shelf and then drops away steeply, " +
                 "which reads as a more scooped basin with a defined lip.")]
        [Range(1f, 6f)]
        public float bankProfile = 2f;

        // ---- The bevel: why shorelines stop being sharp ---------------------

        [Tooltip("Width of the rounded bank outside the waterline, in metres.\n\n" +
                 "This is what stops the shoreline being a cut edge. Inside it the " +
                 "ground is pulled to exactly the water level and then eased back up " +
                 "to the terrain, so the bank is a fillet and the water meets the " +
                 "land with no lip and no gap. 0 is a sharp cut.")]
        public float shoreBevelMetres = 6f;

        [Tooltip("Shapes the bevel. 1 is an even roll. Higher hugs the waterline and " +
                 "then climbs away, which reads as a more scooped, undercut bank.")]
        [Range(0.25f, 4f)]
        public float bevelRoundness = 1f;

        // ---- Ruggedness ----------------------------------------------------

        [Tooltip("How far the bed wanders up and down, in metres. 0 is a smooth " +
                 "bed, which no natural water body has. A stream over stones is " +
                 "0.1-0.3; a boulder-strewn river is 0.5 and up.")]
        public float bedRuggednessMetres = 0.25f;

        [Tooltip("Size of the bumps, in metres. Small is gravel, large is bedrock " +
                 "ledges and sandbars.")]
        public float ruggednessFeatureMetres = 18f;

        [Tooltip("Layers of detail. More adds finer bumps on top of the coarse ones " +
                 "and costs a little time per sample.")]
        [Range(1, 6)]
        public int ruggednessOctaves = 3;

        [Tooltip("How much each finer layer contributes. 0.5 is natural; higher is " +
                 "harsher and noisier.")]
        [Range(0.05f, 0.95f)]
        public float ruggednessPersistence = 0.5f;

        [Tooltip("Distance in from the shore over which ruggedness fades up from " +
                 "nothing, in metres. Without it, bumps break the waterline and the " +
                 "shoreline turns into a dashed line of tiny islands.")]
        public float ruggednessCalmMetres = 8f;

        [Tooltip("Seed for the bed noise. Change it for a different bed of the same " +
                 "character. The same seed always gives the same bed, on every " +
                 "machine and after every rebuild.")]
        public int ruggednessSeed = 20250811;

        // ---- Basin irregularity  (lakes and pools) --------------------------

        [Tooltip("How much the deep water wanders, for lakes and pools.\n\n" +
                 "0 gives the textbook cone: one single deepest point dead in the " +
                 "middle, which no real basin has. Raise it and some directions reach " +
                 "depth quickly while others shelve out a long way, so the floor has " +
                 "holes and rises in it. The waterline is never moved by this, however " +
                 "high it is set - the wander is scaled by how far in you are, so the " +
                 "shore stays exactly on the footprint.\n\n" +
                 "Rivers ignore it: a channel really is close to symmetric across its " +
                 "width.")]
        [Range(0f, 1f)]
        public float basinIrregularity = 0.45f;

        [Tooltip("Size of the deep holes and shallow rises, in metres. Roughly the " +
                 "width of one bay or one bar.")]
        public float basinFeatureMetres = 60f;

        /// <summary>Saved basin family for level natural bodies; Legacy retains existing assets' authored profile and depth controls.</summary>
        [Tooltip("Basin formation model beneath the accepted shoreline. Existing outlines, per-node depths and water levels stay authoritative. Legacy retains the original profile.")]
        public LakeFormation lakeFormation;

        /// <summary>Later sediment infill smoothing, 0..1; zero retains formation relief. Ignored by Legacy and river/ocean excavation.</summary>
        [Range(0, 1), Tooltip("0 preserves formation relief; 1 smooths later sediment infill. The waterline and authored maximum depths stay fixed.")]
        public float lakeSedimentation;

        // Shore dressing (rock/plant scatter) and the shore-ripple / bank-drag /
        // ruggedness-whitewater surface fields were declared here but nothing in
        // the project ever read them, so they showed up as live controls in the
        // multi-select inspector and did nothing. Removed rather than documented.
        // If shore dressing is wanted later it is a feature, not a field.

        // =====================================================================
        //  MESH
        // =====================================================================

        [Header("Mesh")]

        [Tooltip("Vertices along the longest edge. 64 is right for a phone - the " +
                 "wave detail comes from the shader, not from these triangles. " +
                 "Rectangle and Polygon only.")]
        [Range(4, 240)]
        public int meshResolution = 64;

        // SUPERSEDES the old dead `horizonExtentMetres`. That earlier field was written,
        // serialized, shown in the inspector and set to 4000 by the ocean preset but NO
        // line of code ever read it to make geometry - a pure dead field, correctly
        // removed. The §4i.3 Water-Meshing study then grounded the real, mobile-safe win
        // it was reaching for: a cheap low-vertex Far Distance Mesh that fills ocean-to-
        // horizon just below the surface so the sea is bounded by the camera's far clip,
        // not by this body's own mesh edge. Unlike the old field, horizonDistanceMetres
        // below is actually consumed - BuildOceanHorizon builds the ring from it. Ocean
        // only; 0 disables it. Default 8000 m: far enough that a raised camera's geometric
        // sea horizon and a typical far-clip plane fall on the flat ring, not on the body
        // edge; the author sizes it up for a higher viewpoint.
        [Tooltip("Ocean only. How far past the body edge a flat, color-matched 'reaches " +
                 "the horizon' sea ring extends, in metres. 0 turns it off. The ring is a " +
                 "cheap low-vertex strip just below the surface - it fills the gap between " +
                 "the body mesh edge and the camera's far clip so the ocean does not " +
                 "visibly end.")]
        public float horizonDistanceMetres = 8000f;

        // =====================================================================
        //  HYDRAULICS
        // =====================================================================

        [Header("Hydraulics")]

        [Tooltip("Manning roughness. 0.011 smooth concrete, 0.025 a clean earth " +
                 "channel, 0.035 a natural stream, 0.07 a boulder field. Higher is " +
                 "rougher, which means slower and whiter.")]
        [Range(0.008f, 0.15f)]
        public float manningRoughness = 0.035f;

        [Tooltip("Caps the hydraulic radius, in metres. Manning is a channel " +
                 "formula; handing it a 200m lake depth returns a velocity no lake " +
                 "has.")]
        public float hydraulicRadiusCap = 3f;

        [Tooltip("Speed that maps to a full flow channel, in m/s. The material's " +
                 "flow scale is set to this, so the surface scrolls at the metres " +
                 "per second the hydraulics computed rather than at whatever the " +
                 "material happened to be authored with.")]
        public float maxFlowSpeed = 4f;

        // =====================================================================
        //  PHYSICS
        // =====================================================================

        [Header("Physics")]

        [Tooltip("Attaches a UC Water Volume so swimmers, boats and ripples know " +
                 "where the water is. Off is visuals only - a decorative pond " +
                 "nothing ever enters does not need to be queried by every buoyant " +
                 "object in the scene.")]
        public bool generateVolume = true;

        [Tooltip("Scales how hard the current pushes floating bodies. 1 is physical; " +
                 "lower it if a river is meant to be crossable.")]
        [Range(0f, 4f)]
        public float flowForce = 1f;

        [Tooltip("Extra viscous damping for thick water - mud, tar, a swamp. 1 is " +
                 "ordinary water.")]
        [Range(0.2f, 12f)]
        public float viscosityScale = 1f;

        [Tooltip("Emit ripples into the ripple simulation when something moves in " +
                 "this water. Off for water the player can never reach.")]
        public bool emitRipples = true;

        [Tooltip("Overrides the water density in kg/m3. 0 takes the default for the " +
                 "body kind - 1025 salt for an ocean, 998 fresh for everything else. " +
                 "Density is the only thing that decides what floats.")]
        public float densityOverride;

        // =====================================================================
        //  UNDERWATER
        //
        //  What the world looks like from INSIDE this body. Separate from the
        //  surface colours on purpose: the two are not the same colour and cannot
        //  be derived from one another.
        //
        //  Seen from outside, water is mostly what it reflects - sky, cliff,
        //  whatever is behind it. Seen from inside, none of that is in play. What
        //  is left is absorption: red light is gone within a few metres, green
        //  lasts longer, blue travels furthest, which is why everything below the
        //  surface trends blue-green and why distance reads as colour rather than
        //  as haze. Tinting the above-water colour is the usual shortcut and it is
        //  exactly what makes diving look like a coloured filter over dry land
        //  instead of like being in water.
        //
        //  These are per body, so a peat river is brown and dim, a chalk lake is
        //  pale green, and open sea is deep blue - as they are in life.
        // =====================================================================

        [Header("Underwater")]
        [Tooltip("Render the world through water when the camera goes under this body.\n\n" +
                 "Needs a UC Underwater View component on the camera. Leave on: it " +
                 "costs nothing until something is actually submerged.")]
        public bool underwaterView = true;

        [Tooltip("The colour distance fades towards under water. This is the single " +
                 "field that decides whether the dive reads as sea, river or pool - " +
                 "it is what the far side of the body turns into.")]
        [ColorUsage(false, true)]
        public Color underwaterColor = new Color(0.10f, 0.36f, 0.44f, 1f);

        [Tooltip("How far you can see through this water, in metres. Sight distance, " +
                 "not a fog density - the seabed disappears at roughly this range.\n\n" +
                 "Clear tropical sea 30-50, open sea 15-25, a clean lake 8-15, a " +
                 "silty river 3-6, a swimming pool 25+.")]
        [Range(0.5f, 80f)]
        public float visibilityMetres = 14f;

        [Tooltip("Scales the underwater colour. Below 1 for depth, murk or night - " +
                 "under water there is less light than above it, and a medium that is " +
                 "as bright as the sky above it reads as fog rather than water.")]
        [Range(0.05f, 2f)]
        public float underwaterBrightness = 0.85f;

        // =====================================================================
        //  MATERIAL
        // =====================================================================

        [Header("Material")]

        [Tooltip("Use the description below instead of the shared water material. " +
                 "Off shares one material with every other body that shares it, " +
                 "which is one draw call for all of them.")]
        public bool useOwnMaterial = true;

        [Tooltip("Everything the water shader is told: colour, depth fade, foam, " +
                 "caustics, waves, ripple response. The same description the texture " +
                 "graph produces, filled in by hand.")]
        public WaterProduct water = new WaterProduct();

        [Tooltip("Overrides the material entirely. Anything set here wins over the " +
                 "description above - for a project that authored its own water " +
                 "shader and only wants the geometry and the physics from here.")]
        public Material materialOverride;

        // =====================================================================
        //  RUNTIME STATE
        // =====================================================================

        [System.NonSerialized] private GameObject surfaceObject;
        [System.NonSerialized] private Mesh mesh;
        [System.NonSerialized] private Material material;
        [System.NonSerialized] private UCWaterVolume volume;
        [System.NonSerialized] private WaterBodyData lastBuild;

        // Ocean-only Far Distance Mesh (§4i.3). A separate low-vertex child so it can use
        // the same material at a flat, wave-free weight without touching the body mesh.
        [System.NonSerialized] private GameObject horizonObject;
        [System.NonSerialized] private Mesh horizonMesh;

        /// <summary>Result of the last build, or null if nothing has been built.</summary>
        public WaterBodyData LastBuild { get { return lastBuild; } }

        /// <summary>Vertices in the built mesh, for the inspector's readout.</summary>
        public int BuiltVertexCount { get { return lastBuild != null ? lastBuild.VertexCount : 0; } }

        /// <summary>Triangles in the built mesh, for the inspector's readout.</summary>
        public int BuiltTriangleCount { get { return lastBuild != null ? lastBuild.TriangleCount : 0; } }

        // =====================================================================
        //  DAM RESERVOIR GOVERNANCE  (Module 6 seam - RaiseWaterBody)
        // =====================================================================
        //
        // Transient, never serialised. A dam request does not edit this body's own
        // level or outline; it governs them for as long as the request stands, and
        // DamReservoirReconciler re-derives that governance from the request set on
        // every settle. Withdraw the dam and rebuild and the body is its authored
        // self again - the reversibility the whole seam depends on. Only a level body
        // is ever governed; the river path never reads these.

        [System.NonSerialized] private bool damGoverned;
        [System.NonSerialized] private float damSurfaceLevelMetres;
        [System.NonSerialized] private float damTailwaterLevelMetres;
        [System.NonSerialized] private Vector2 damBarrierCentre;   // world XZ
        [System.NonSerialized] private Vector2 damUpstreamNormal;  // unit, world XZ
        [System.NonSerialized] private float damCrestLengthMetres; // span of the dam line, metres
        [System.NonSerialized] private string damSourceId = string.Empty;

        /// <summary> True while a dam request is holding this body as its reservoir. The spillway illusion (U-2) and the
        /// inspector read it to show the body is level-governed rather than sitting at its authored surface. </summary>
        public bool IsDamGoverned { get { return damGoverned; } }

        /// <summary> The tailwater level the governing dam holds below the line, in metres. Meaningful only while
        /// <see cref="IsDamGoverned"/>; the reservoir-minus-tailwater drop is what the spillway flow (U-2) reads.
        /// </summary>
        public float GovernedTailwaterLevelMetres { get { return damTailwaterLevelMetres; } }

        /// <summary> The world XZ midpoint of the dam line governing this reservoir. Meaningful only while
        /// <see cref="IsDamGoverned"/>. The spillway illusion (U-2) plunges its foam and spray on the downstream side of
        /// this point; the value is the request's outlet position, carried here by <see cref="GovernAsReservoir"/> so
        /// U-2 reads U-1's governance and never the inbox a second time. </summary>
        public Vector2 DamBarrierCentre { get { return damBarrierCentre; } }

        /// <summary> The unit world-XZ normal pointing upstream (into the reservoir) from the dam line. Meaningful only
        /// while <see cref="IsDamGoverned"/>. Downstream - where the spillway jet lands - is its negation, so U-2 offsets
        /// the plunge point along <c>-DamUpstreamNormal</c> from <see cref="DamBarrierCentre"/>. </summary>
        public Vector2 DamUpstreamNormal { get { return damUpstreamNormal; } }

        /// <summary> The span of the governing dam line in metres - the request's crest length. Meaningful only while
        /// <see cref="IsDamGoverned"/>. U-2 spreads the plunge foam and spray across this width along the barrier axis, so
        /// a wide dam aerates a wide tailwater and a narrow one a narrow plume, rather than a guessed footprint. </summary>
        public float GovernedCrestLengthMetres { get { return damCrestLengthMetres; } }

        /// <summary> True for a lake, pool or ocean - a closed ring at one level - and false for a river. A reservoir is
        /// a level body, so the reconciler governs only these. </summary>
        public bool IsLevelBody { get { return shape != ShapeMode.RiverSpline; } }

        /// <summary> The world Y of this body's surface: the level a governing dam holds it to, or its own authored
        /// transform.position.y + surfaceOffsetMetres when no dam governs it. Every level-build site reads the surface
        /// through here, so the raise is applied in exactly one place and cannot be half-applied. </summary>
        public float SurfaceWorldY()
        {
            return damGoverned ? damSurfaceLevelMetres : transform.position.y + surfaceOffsetMetres;
        }

        /// <summary> Hold this body as the reservoir of a dam: raise it to <paramref name="surfaceLevelMetres"/> and
        /// clip it at the dam line through (<paramref name="barrierCentreX"/>, <paramref name="barrierCentreZ"/>) with
        /// <paramref name="upstreamNormal"/> impounding, spanning <paramref name="crestLengthMetres"/> along the line.
        /// Returns true if any of the governance actually changed, so the reconciler rebuilds only a body whose reservoir
        /// moved. Transient: never written to the serialised fields. </summary>
        public bool GovernAsReservoir(float surfaceLevelMetres, float tailwaterLevelMetres,
            float barrierCentreX, float barrierCentreZ, Vector2 upstreamNormal, float crestLengthMetres, string sourceId)
        {
            Vector2 centre = new Vector2(barrierCentreX, barrierCentreZ);
            string id = sourceId ?? string.Empty;

            bool changed = !damGoverned
                || !Mathf.Approximately(damSurfaceLevelMetres, surfaceLevelMetres)
                || !Mathf.Approximately(damTailwaterLevelMetres, tailwaterLevelMetres)
                || (damBarrierCentre - centre).sqrMagnitude > 1e-6f
                || (damUpstreamNormal - upstreamNormal).sqrMagnitude > 1e-6f
                || !Mathf.Approximately(damCrestLengthMetres, crestLengthMetres)
                || damSourceId != id;

            damGoverned = true;
            damSurfaceLevelMetres = surfaceLevelMetres;
            damTailwaterLevelMetres = tailwaterLevelMetres;
            damBarrierCentre = centre;
            damUpstreamNormal = upstreamNormal;
            damCrestLengthMetres = crestLengthMetres;
            damSourceId = id;
            return changed;
        }

        /// <summary> Release any dam governance so the body returns to its authored level and outline on the next build.
        /// Returns true if it was governed, so the reconciler rebuilds a body a withdrawn dam just let go. </summary>
        public bool ClearReservoirGovernance()
        {
            if (!damGoverned) { return false; }
            damGoverned = false;
            damSurfaceLevelMetres = 0f;
            damTailwaterLevelMetres = 0f;
            damBarrierCentre = Vector2.zero;
            damUpstreamNormal = Vector2.zero;
            damCrestLengthMetres = 0f;
            damSourceId = string.Empty;
            return true;
        }

        /// <summary> This body's footprint as a plan-view Bounds in world space (the shore ring's XZ extent, the height
        /// axis flattened). The reconciler uses it to decide which dam impounds this body. False when the body has no
        /// usable footprint yet. </summary>
        public bool TryGetPlanBounds(out Bounds bounds)
        {
            bounds = new Bounds();
            List<Vector3> ring = WorldShoreRing();
            if (ring == null || ring.Count < 3) { return false; }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 p = ring[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            bounds = new Bounds(new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f),
                                new Vector3(maxX - minX, 0f, maxZ - minZ));
            return true;
        }

        /// <summary>
        /// This body's world footprint as a flat XZ ring plus its plan bounds (§A6). The ring
        /// is the tight shore outline for a Polygon body, or the rectangle corners otherwise;
        /// the bounds match <see cref="TryGetPlanBounds"/>. Public so the water manager can push
        /// real lake footprints into the height graph exactly as it pushes river courses, letting
        /// the continental-rise guard exempt ONLY ground a real lake actually covers. False when
        /// the body has no usable footprint yet.
        /// </summary>
        public bool TryGetWorldFootprint(out Vector2[] ringXZ, out Bounds planBounds)
        {
            ringXZ = null;
            planBounds = new Bounds();

            List<Vector3> ring = WorldShoreRing();
            if (ring == null || ring.Count < 3) return false;

            ringXZ = new Vector2[ring.Count];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 p = ring[i];
                ringXZ[i] = new Vector2(p.x, p.z);
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            planBounds = new Bounds(new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f),
                                    new Vector3(maxX - minX, 0f, maxZ - minZ));
            return true;
        }

        private const float Gravity = 9.81f;

        // =====================================================================
        //  LIFECYCLE
        // =====================================================================

        private void OnEnable()
        {
            // Registered before the build, so a chunk that happens to be applied
            // during it already knows to dig. Registration is what makes the dig
            // survive regeneration: the terrain build asks the registry, not the
            // scene, and the registry has this body in it from here until OnDisable.
            WaterCarveRegistry.Register(this);

            if (buildTrigger == BuildTrigger.Automatic) Rebuild();
        }

        private void OnDisable()
        {
            // UC-T04-07: frozen workers from a deleted/disabled authoring body cannot commit its old course.
            var owningWorld = CarveWorld;
            if (owningWorld != null && owningWorld.isActiveAndEnabled &&
                (placementSaved || !HydrologyGenerator.IsGeneratedBodyName(name)))
                InvalidatePendingWaterAuthoring(true); // UC-T04-07: retiring graph-generated outputs must not cancel their own generating run.
            // GetEntityId, not GetInstanceID: the latter is obsolete-as-error in Unity 6.5 and fails
            // the build outright. The course registry keys on an int, so the EntityId is hashed.
            RiverCourseInputNode.UnpublishBodyCourse(GetEntityId().GetHashCode());
            WaterCarveRegistry.Unregister(this);
            ClearWaterJunctions(true); // UC-T04-07: disable retires acceptance while retaining saved endpoint candidates for re-enable.
            Clear();
        }

        private void OnValidate()
        {
            // Clamped here rather than trusted, because a zero or negative value in
            // any of these divides by zero somewhere in the build and the symptom is
            // a NaN mesh Unity refuses to draw with no message about why.
            sizeMetres.x = Mathf.Max(1f, sizeMetres.x);
            sizeMetres.y = Mathf.Max(1f, sizeMetres.y);
            flatFloorDepthMetres = Mathf.Max(0.05f, flatFloorDepthMetres);
            minDepthMetres = Mathf.Max(0.001f, minDepthMetres);
            edgeFeatherMetres = Mathf.Max(0.01f, edgeFeatherMetres);
            hydraulicRadiusCap = Mathf.Max(0.05f, hydraulicRadiusCap);
            maxFlowSpeed = Mathf.Max(0.01f, maxFlowSpeed);
            densityOverride = Mathf.Max(0f, densityOverride);

            // Floored, not just ranged: the slider cannot reach zero but a script or a
            // stale serialised value can, and zero visibility divides by zero in the
            // absorption term and fills the screen with a flat colour.
            visibilityMetres = Mathf.Max(0.5f, visibilityMetres);
            underwaterBrightness = Mathf.Max(0.01f, underwaterBrightness);

            // Dig. Same reasoning - a zero run or a zero feature size divides by zero
            // inside the bed math and produces a NaN heightfield, which Unity accepts
            // and then draws as an invisible terrain with no error in the console.
            digDepthMetres = Mathf.Max(0.05f, digDepthMetres);
            shoreRunMetres = Mathf.Max(0.5f, shoreRunMetres);
            shoreBevelMetres = Mathf.Max(0f, shoreBevelMetres);
            basinFeatureMetres = Mathf.Max(2f, basinFeatureMetres);
            bedRuggednessMetres = Mathf.Max(0f, bedRuggednessMetres);
            ruggednessFeatureMetres = Mathf.Max(0.5f, ruggednessFeatureMetres);
            ruggednessCalmMetres = Mathf.Max(0.01f, ruggednessCalmMetres);

            // A Polygon lake carries an optional per-outline-node bed depth; keep it
            // the same length as the outline so a newly added point means "inherit"
            // rather than "suddenly 0 metres deep".
            TrimNodeSeries(nodeDepthMetres);

            // A River Spline body carries a per-course-node hydraulic payload on the
            // same open outline. Keep every authored channel the same length as the
            // course, with the same "new node inherits" rule as the lake depths above.
            if (riverProfile != null) riverProfile.Trim(outline);

            corridorMetres = Mathf.Max(0f, corridorMetres);
            valleyWidthMultiple = Mathf.Max(0f, valleyWidthMultiple);
        }

        /// <summary>
        /// Keeps a per-node list the same length as the polygon outline. Growing it
        /// with zeros is what makes a newly added outline point mean "inherit" rather
        /// than "suddenly 0 metres deep".
        /// </summary>
        private void TrimNodeSeries(List<float> series)
        {
            if (series == null || outline == null) return;

            int want = outline.Count;

            while (series.Count > want) series.RemoveAt(series.Count - 1);
            while (series.Count < want) series.Add(0f);
        }

        /// <summary>
        /// Bakes the child <see cref="UCRiverNode"/> handles into this body's open
        /// <c>outline</c> (positions, body-local) and <c>riverProfile</c> (per-node
        /// channels), in hierarchy/sibling order. The nodes are the edit-time source
        /// of truth; this is the one place they become the baked course the solver,
        /// carve and mesh read, so downstream code never needs to know nodes exist.
        ///
        /// Channels are written one value per node (0 = inherit, carried forward at
        /// Bake); a channel that is 0 on every node forward-fills to nothing and the
        /// solver derives it. A course needs two nodes to be a line; a single point
        /// retains its source position but produces no segment or water surface (UC-T04-05B).
        /// Returns the node count gathered.
        /// </summary>
        public int GatherCourseFromNodes()
        {
            EnsureRiverTerrainMode(); // UC-T04-01: capture legacy intent before replacing derived course data.
            if (outline == null) outline = new Polyline();
            if (riverProfile == null) riverProfile = new RiverProfile();

            outline.points.Clear();
            outline.weights.Clear();
            outline.closed = false;
            riverProfile.halfWidthM.Clear();
            riverProfile.depthM.Clear();
            riverProfile.dischargeM3PerS.Clear();
            riverProfile.velocityMps.Clear();

            // Includes inactive children so a node toggled off in the hierarchy is not
            // silently dropped from the course mid-edit.
            UCRiverNode[] nodes = OwnedRiverNodes(); // UC-T04-05: nested water bodies own their own endpoint identities and routes.
            Transform t = transform;

            // Flood plain: with no Start ticked anywhere the whole river has one. Once any node is a
            // Start, the plain exists from a Start to the next End (or the river's end) only.
            if (nodeFloodPlain == null) nodeFloodPlain = new List<float>();
            nodeFloodPlain.Clear();
            bool anyStart = false;
            for (int i = 0; i < nodes.Length; i++) if (nodes[i] != null && nodes[i].floodPlainStart) { anyStart = true; break; }
            bool inPlain = false;

            ResolveMouthControl(nodes); // UC-T04-07: retire old node-managed/automatic targets before recognizing the current endpoint.

            for (int i = 0; i < nodes.Length; i++)
            {
                UCRiverNode n = nodes[i];
                if (n == null) continue;

                if (!anyStart) nodeFloodPlain.Add(1f);
                else
                {
                    if (n.floodPlainStart) inPlain = true;
                    nodeFloodPlain.Add(inPlain ? 1f : 0.001f);
                    if (n.floodPlainEnd) inPlain = false;
                }

                // World -> body-local, matching how the outline is drawn and carved
                // (the inspector and dig both go through transform.TransformPoint).
                outline.points.Add(t.InverseTransformPoint(n.transform.position));
                riverProfile.halfWidthM.Add(Mathf.Max(0f, n.halfWidthM));
                riverProfile.depthM.Add(Mathf.Max(0f, n.depthM));
                riverProfile.dischargeM3PerS.Add(Mathf.Max(0f, n.dischargeM3PerS));
                riverProfile.velocityMps.Add(Mathf.Max(0f, n.velocityMps));
            }

            // UC-T04-06: automatic recognition uses owned accepted coverage across grouping parents.
            if (joinsRiver == null && nodes.Length > 1 && TryAutomaticRiverReceiver(nodes[nodes.Length - 1].transform.position, out var automaticReceiver))
            { joinsRiver = automaticReceiver; automaticMouthId = automaticReceiver.StableWaterBodyId; }
            AppendConfluencePoint();
            return outline.points.Count;
        }

        /// <summary>
        /// River-to-river connection. When Joins River is set, a final point is appended at the
        /// nearest point on that river's own (smoothed) course, so a tributary runs INTO the main
        /// channel and the two carves overlap at the mouth. Skipped for a self-join or a loop.
        /// </summary>
        private void AppendConfluencePoint()
        {
            if (!SameJunctionScope(joinsRiver)) return; // UC-T04-05: overlapping worlds cannot append each other's routes.
            if (joinsRiver.kind != WaterBodyKind.River || joinsRiver.shape != ShapeMode.RiverSpline) return;
            if (joinsRiver.joinsRiver == this) return;                       // A joins B joins A
            if (outline.points.Count < 1) return;

            Polyline target = joinsRiver.GetSmoothCourseLocal(6f);
            if (target == null || target.Count < 2) return;

            Transform tt = joinsRiver.transform;
            Transform t = transform;
            Vector3 last = t.TransformPoint(outline.points[outline.points.Count - 1]);

            float best = float.MaxValue; Vector3 bestW = last;
            for (int i = 0; i + 1 < target.Count; i++)
            {
                Vector3 a = tt.TransformPoint(target.points[i]);
                Vector3 b = tt.TransformPoint(target.points[i + 1]);
                Vector3 ab = b - a; ab.y = 0f;
                float len2 = ab.sqrMagnitude;
                Vector3 lp = last - a; lp.y = 0f;
                float u = len2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(lp, ab) / len2) : 0f;
                Vector3 q = a + (b - a) * u;
                float dx = q.x - last.x, dz = q.z - last.z;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; bestW = q; }
            }
            if (best > 1e11f) return;
            if (best < 1e-6f) return; // UC-T04-06: an endpoint already on the receiver must not acquire a zero-length station.

            // Ground-snapped, a few metres short of the centreline would notch the main channel; ending
            // exactly on it lets both carves meet. The target's dig lowers the ground there anyway.
            if (TrySampleSurfaceGround(Terrain.activeTerrains, CarveWorld, bestW.x, bestW.z, out float joinGround)) bestW.y = joinGround; // UC-T04-06: never seat a connection on a foreign world.
            outline.points.Add(t.InverseTransformPoint(bestW));
            if (nodeFloodPlain != null && nodeFloodPlain.Count > 0) nodeFloodPlain.Add(nodeFloodPlain[nodeFloodPlain.Count - 1]);
            outline.weights.Clear();
            riverProfile.halfWidthM.Add(0f);
            riverProfile.depthM.Add(0f);
            riverProfile.dischargeM3PerS.Add(0f);
            riverProfile.velocityMps.Add(0f);
        }

        /// <summary>This river's Catmull-Rom smoothed course in body-local space (nodes only, no join).</summary>
        public Polyline GetSmoothCourseLocal(float tessellationMetres)
        {
            UCRiverNode[] nodes = OwnedRiverNodes(); // UC-T04-05: receiver sampling excludes nested bodies' handles.
            if (nodes.Length < 2) return null;
            Polyline p = new Polyline();
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i] != null) p.points.Add(transform.InverseTransformPoint(nodes[i].transform.position));
            p.closed = false;
            return SplineOperations.TessellateCatmullRom(p, tessellationMetres, 0.5f);
        }

        /// <summary>
        /// Adds a gentle, seeded sideways wander to a tessellated river course. Zero at both ends (so
        /// the river still starts and ends exactly on its nodes), tapering over the first and last 15%,
        /// with two wavelengths so it never looks like a sine wave. Body-local XZ; Y is left alone.
        /// </summary>
        private void ApplyMeander(Polyline smooth)
        {
            float amount = float.IsNaN(meander) || float.IsInfinity(meander) ? 0f : Mathf.Clamp(meander, 0f, 3f); // UC-T04-03: external/authored input cannot create unbounded or nonfinite displacement.
            if (smooth == null || smooth.Count < 4 || amount <= 1e-3f) return;
            float[] arc = smooth.BuildArcTable();
            float total = arc[arc.Length - 1];
            if (total < 20f) return;

            int n = smooth.Count;
            Vector3[] src = new Vector3[n];
            for (int i = 0; i < n; i++) src[i] = smooth.points[i];

            float amp = 6f * amount;                         // UC-T04-03: metres; combined noise is bounded by 1.4, endpoints taper to zero.

            // Where the course already lies on a procedural trench the river must FOLLOW that trench,
            // not wander off it. Mark those points, blur the mark so the wander fades in and out
            // instead of kinking at the boundary, and scale the offset by what is left.
            float[] free = new float[n];
            UniverseCreatorObject channelWorld = CarveWorld; // UC-T02-07: resolve once for this course, outside the sample loop.
            for (int i = 0; i < n; i++)
            {
                Vector3 w = transform.TransformPoint(src[i]);
                free[i] = (FollowTrench && RiverChannelRegistry.TrySampleCommitted(channelWorld, w.x, w.z, 0,
                    out var accepted, out _) && accepted.SignedBankDistanceMetres <= 0) ? 0f : 1f; // UC-T03-09: no meander suppression from candidate-only metadata.
            }
            for (int pass = 0; pass < 6; pass++)
            {
                float[] f2 = (float[])free.Clone();
                for (int i = 1; i < n - 1; i++) free[i] = 0.25f * f2[i - 1] + 0.5f * f2[i] + 0.25f * f2[i + 1];
            }

            for (int i = 1; i < n - 1; i++)
            {
                Vector3 tan = src[i + 1] - src[i - 1]; tan.y = 0f;
                if (tan.sqrMagnitude < 1e-8f) continue;
                tan.Normalize();
                Vector3 nrm = new Vector3(-tan.z, 0f, tan.x);

                float s = arc[i];
                float u = s / total;
                float taper = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.15f)) *
                              Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - u) / 0.15f));

                float w = (Mathf.PerlinNoise(s * 0.011f + naturalSeed * 0.37f, 0.5f) - 0.5f) * 2f
                        + (Mathf.PerlinNoise(s * 0.037f + naturalSeed * 0.91f, 7.5f) - 0.5f) * 0.8f;
                smooth.points[i] = src[i] + nrm * (w * amp * taper * free[i]);
            }
        }

        /// <summary>
        /// Bakes this river's hand-drawn course into WORLD-space XZ points and per-point channel
        /// half-widths, for the graph's River Course Input node to rasterize into a River Mask.
        /// Reuses the SAME smoothing the carve uses (GatherCourseFromNodes → centripetal
        /// Catmull-Rom → authored width by arc), coarsened for a mask, so the mask follows the
        /// identical curve the trench was cut along. Does NOT carve or touch the terrain. Returns
        /// false (and null outs) when this is not a river or has fewer than two course points.
        /// </summary>
        public bool TryBakeWorldCourseXZ(float fallbackHalfWidthM, out Vector2[] pointsXZ, out float[] halfWidthM)
        {
            pointsXZ = null;
            halfWidthM = null;
            if (kind != WaterBodyKind.River) return false;

            GatherCourseFromNodes();
            int n = outline != null ? outline.Count : 0;
            if (n < 2) return false;

            // Coarser than the carve's 5 m tessellation: a mask lives at chunk-pixel resolution,
            // so a dense curve would only cost segments the rasteriser cannot resolve.
            const float MaskTessellationMetres = 8f;

            BakedRiverProfile authored = riverProfile != null ? riverProfile.Bake(outline) : null;
            Polyline smooth = SplineOperations.TessellateCatmullRom(outline, MaskTessellationMetres, 0.5f);
            ApplyMeander(smooth);
            int m = smooth != null ? smooth.Count : 0;
            if (m < 2) return false;

            float[] arc = smooth.BuildArcTable();
            float smoothTotal = arc.Length > 0 ? arc[arc.Length - 1] : 0f;
            float ctrlTotal = authored != null ? authored.TotalLengthM : 0f;
            bool hasW = authored != null && authored.HasWidth;

            float fallback = Mathf.Max(0.01f, fallbackHalfWidthM);
            Transform t = transform;
            Vector2[] pts = new Vector2[m];
            float[] half = new float[m];

            for (int i = 0; i < m; i++)
            {
                Vector3 w = t.TransformPoint(smooth.points[i]);
                pts[i] = new Vector2(w.x, w.z);

                float hw = fallback;
                if (hasW)
                {
                    float arcC = smoothTotal > 1e-4f ? (arc[i] / smoothTotal) * ctrlTotal : 0f;
                    float authoredHw = authored.SampleAtArc(arcC).halfWidthM;
                    if (authoredHw > 0f) hw = authoredHw;
                }
                half[i] = hw;
            }

            pointsXZ = pts;
            halfWidthM = half;
            return true;
        }

        /// <summary>
        /// The river's SOLVED course for the graph: XZ points, half-width, thalweg elevation
        /// (world metres) and depth - exactly what the scene-side dig uses, so the River Course
        /// Input node can cut the same channel in the graph. Builds the dig (without re-cutting
        /// the terrain) when it has not been built yet. False for read mode, disabled excavation, a non-river or an unsolved course.
        /// </summary>
        public bool TryGetSolvedCourse(float fallbackHalfWidthM, out Vector2[] pointsXZ, out float[] halfWidthM,
                                       out float[] bedY, out float[] depthM)
        {
            pointsXZ = null; halfWidthM = null; bedY = null; depthM = null;
            if (kind != WaterBodyKind.River || shape != ShapeMode.RiverSpline || FollowTrench || !digTerrain) return false; // UC-T04-02: a graph carve request can only come from an enabled manual excavator.

            if (carve == null || !carve.isRiver || carve.courseWorld == null || carve.courseWorld.Count < 2 ||
                carve.riverBedY == null)
            {
                if (!digTerrain) return false;
                BuildDig(false);
            }
            if (carve == null || !carve.isRiver || carve.courseWorld == null || carve.courseWorld.Count < 2 ||
                carve.riverBedY == null || carve.riverBedY.Length != carve.courseWorld.Count)
                return false;

            int m = carve.courseWorld.Count;
            pointsXZ = new Vector2[m]; halfWidthM = new float[m]; bedY = new float[m]; depthM = new float[m];
            float defHalf = fallbackHalfWidthM > 0f ? fallbackHalfWidthM : carve.riverDefaultHalfWidthM;
            for (int i = 0; i < m; i++)
            {
                Vector3 w = carve.courseWorld[i];
                pointsXZ[i] = new Vector2(w.x, w.z);
                float hw = (carve.riverHalfWidth != null && i < carve.riverHalfWidth.Length) ? carve.riverHalfWidth[i] : 0f;
                float dp = (carve.riverDepthM != null && i < carve.riverDepthM.Length) ? carve.riverDepthM[i] : 0f;
                halfWidthM[i] = hw > 0f ? hw : defHalf;
                depthM[i] = dp > 0f ? dp : carve.riverDefaultDepthM;
                bedY[i] = carve.riverBedY[i];
            }
            return true;
        }

        // =====================================================================
        //  BUILD
        // =====================================================================

        // =====================================================================
        //  THE DIG
        // =====================================================================

        [System.NonSerialized] private WaterCarveLayer carve;
        [System.NonSerialized] private WaterCarveLayer riverSurfaceFit; // UC-T04-04: fitted widths never mutate the authored excavation request.
        [System.NonSerialized] private RiverCrossSectionFit.Shoreline riverShoreline; // UC-T04-06: independent bank extents accompany the same fitted course.
        [System.NonSerialized] private GenerationRunId riverFitRun;

        /// <summary>Latest surface-fit failure, or empty after a valid fit; fitting never grants excavation permission.</summary>
        public string RiverSurfaceDiagnostic { get; private set; } = string.Empty;

        /// <summary>The nearest world owning this body's dig and accepted profiles, including inactive parents used by Editor preparation; null when unowned.</summary>
        public UniverseCreatorObject CarveWorld
        {
            get { return GetComponentInParent<UniverseCreatorObject>(true); } // UC-T03-09: disabling a hierarchy must not erase its world identity.
        }

        /// <summary>The owned terrain excavation request, or null for procedural-read rivers, disabled digging or a missing solve.</summary>
        public WaterCarveLayer CarveLayer
        {
            get { return digTerrain && (shape != ShapeMode.RiverSpline || !FollowTrench) && carve != null && carve.HasShape ? carve : null; } // UC-T04-02: surface fit data cannot enter the terrain registry in read mode.
        }

        /// <summary>Copies the latest fitted river profile for surface consumers without exposing an excavation request or triggering a build.</summary>
        /// <param name="courseWorld">World-metre stations, or null when no complete fit exists.</param>
        /// <param name="bedY">Bed elevations in world metres, matching station count, or null on failure.</param>
        /// <param name="depthM">Nonnegative fitted water depths in metres, or null on failure.</param>
        /// <param name="halfWidthM">Fitted channel half-widths in metres, or null on failure.</param>
        /// <returns>True for a complete fitted river in either mode; arrays are independent copies. Does not write terrain.</returns>
        public bool TryGetFittedRiverProfile(out Vector3[] courseWorld, out float[] bedY, out float[] depthM, out float[] halfWidthM)
        {
            courseWorld = null; bedY = null; depthM = null; halfWidthM = null;
            WaterCarveLayer fit = riverSurfaceFit; // UC-T04-04: report only validated surface dimensions, separately from the cut.
            if (shape != ShapeMode.RiverSpline || fit == null || !fit.isRiver || !fit.HasShape) return false;
            int count = fit.courseWorld.Count;
            if (fit.riverBedY?.Length != count || fit.riverDepthM?.Length != count || fit.riverHalfWidth?.Length != count) return false;
            for (int i = 0; i < count; i++)
                if (!RiverChannelSnapshot.Finite(fit.courseWorld[i].x) || !RiverChannelSnapshot.Finite(fit.courseWorld[i].y) ||
                    !RiverChannelSnapshot.Finite(fit.courseWorld[i].z) || !RiverChannelSnapshot.Finite(fit.riverBedY[i]) ||
                    !RiverChannelSnapshot.Finite(fit.riverDepthM[i]) || fit.riverDepthM[i] < 0 ||
                    !RiverChannelSnapshot.Finite(fit.riverHalfWidth[i]) || fit.riverHalfWidth[i] <= 0) return false;
            courseWorld = fit.courseWorld.ToArray(); bedY = (float[])fit.riverBedY.Clone();
            depthM = (float[])fit.riverDepthM.Clone(); halfWidthM = (float[])fit.riverHalfWidth.Clone();
            return true;
        }

        /// <summary>
        /// True when the world driving this body has hydrology wired into its graph.
        ///
        /// This is the difference between the two procedural promises. With a Hydrology
        /// Spawner reaching an Output the generator itself produces hydrology, so a
        /// regeneration or a cell rebuild ends with ground the course can be re-fitted
        /// to automatically and the body re-places itself. With no such node the graph
        /// knows nothing about this water, so the only honest moment to re-fit is when
        /// the user presses Build - the dig is still re-cut on every chunk build either
        /// way, so the channel cannot be erased, but its route is only as current as
        /// the last fit.
        /// </summary>
        public bool IsGraphLinked
        {
            get
            {
                UniverseCreatorObject world = CarveWorld;
                if (world == null) return false;
                return UCWaterSystemManager.CountConnectedHydrology(world.graph) > 0;
            }
        }

        /// <summary>
        /// True when this river owns a directed source/mouth record; geometry acceptance is reported separately.
        /// </summary>
        public bool IsConnected
        {
            get { return waterJunctions != null && waterJunctions.Count > 0; } // UC-T04-05: show the actual persistent connection owner.
        }

        /// <summary>
        /// Directed source/mouth kinds for the inspector; empty when unconnected, with separate invalid-state diagnostics.
        /// </summary>
        public string ConnectionSummary
        {
            get { return IsConnected ? string.Join(", ", waterJunctions.ConvertAll(record => record.Kind.ToString())) : string.Empty; } // UC-T04-05: derived from owned records.
        }

        /// <summary>
        /// Translates the fields above into a dig the terrain build can apply, then
        /// re-cuts it into whatever terrain is already on screen.
        ///
        /// This is what the Build button runs. It does not touch the graph and does
        /// not regenerate anything - the dig is a rule the terrain build consults, so
        /// building it is a matter of describing it correctly and asking the chunks to
        /// read it again.
        /// </summary>
        public void BuildDig()
        {
            BuildDig(true);
        }

        /// <summary>
        /// The dig, with control over whether the terrain is re-cut afterwards.
        ///
        /// The settle re-fit passes false and re-cuts once for every body it touched,
        /// because ReapplyCarves rewrites whole chunks - running it per body would
        /// rewrite the same chunk once per water body in reach of it.
        /// UC-T04-04: river surface fitting also runs when digging is disabled. A manual
        /// non-excavating course uses authored world Y as its water-level reference and
        /// must fit real wet ground; a procedural reader uses the accepted bed/level.
        /// </summary>
        public void BuildDig(bool reapplyToTerrain)
        {
            if (carve == null) carve = new WaterCarveLayer();

            carve.Clear();
            riverSurfaceFit = null; RiverSurfaceDiagnostic = string.Empty; // UC-T04-04: a failed replacement exposes no newly accepted fit.
            carve.enabled = digTerrain && (shape != ShapeMode.RiverSpline || !FollowTrench); // UC-T04-02: read fit data is disabled as a terrain writer from the first branch.
            carve.kind = kind;
            carve.seed = ruggednessSeed;

            if (!digTerrain && shape != ShapeMode.RiverSpline) // UC-T04-04: water fitting still runs with excavation disabled.
            {
                if (reapplyToTerrain) ReapplyToTerrain();
                return;
            }

            // An ocean rounds its beach and does nothing else. Its depth, its seabed
            // and its shoreline stay exactly what the terrain and the graph make them,
            // which is how it worked before the dig existed and how it works best.
            carve.bevelOnly = kind == WaterBodyKind.Ocean;

            carve.bevelMetres = shoreBevelMetres;
            carve.bevelRoundness = bevelRoundness;
            carve.minDepthMetres = minDepthMetres;

            // The corridor is what makes the terrain build AROUND the water rather than
            // just get cut by it. An ocean never takes one: its beach is the bevel and
            // nothing else, which is how it worked before any of this existed. A river's
            // valley is derived from its width when no absolute metres are typed - all
            // of that lives in the accessor, so this is the only write.
            carve.corridorMetres = Mathf.Max(ValleyCorridorMetres, LakeBankEaseMetres);
            carve.corridorStrength = (kind == WaterBodyKind.River || kind == WaterBodyKind.Ocean)
                ? corridorStrength : Mathf.Max(corridorStrength, 0.85f);

            if (shape == ShapeMode.RiverSpline)
            {
                BuildRiverDig();
                // The derived valley needs the solved channel width, which only exists now.
                carve.corridorMetres = ValleyCorridorMetres;
            }
            else
                BuildLevelDig();

            carve.RecomputeBounds();

            if (reapplyToTerrain) ReapplyToTerrain();
        }

        /// <summary>
        /// River variant of the dig: gather the course from its child nodes, sample
        /// the ground under each node, solve the descending bed (step 4), and hand the
        /// swept channel to the carve layer. The lake/ocean ring path (BuildLevelDig)
        /// is left completely untouched.
        /// </summary>
        private void BuildRiverDig()
        {
            LastChannelValidity = RiverChannelValidity.NoChannel; // UC-T04-02: an empty replacement cannot retain a previous valid lookup.
            var savedSource = SavedJunctionTarget(true); var savedMouth = SavedJunctionTarget(false); // UC-T04-07: recover stable endpoints before retiring the previous fit.
            ClearWaterJunctions(); // UC-T04-05: edited/deleted endpoints withdraw their previous records and inverse links.
            RiverSurfaceDiagnostic = "River has no complete fitted course."; // UC-T04-04: early empty/coverage failures remain visible.
            GatherCourseFromNodes();
            RiverCourseInputNode.UnpublishBodyCourse(GetEntityId().GetHashCode()); // UC-T04-02: an incomplete replacement cannot retain the previous course's graph excavation.
            carve.followOnly = FollowTrench; // UC-T03-09: resolve node-authored mode before any missing-data return.

            int n = outline != null ? outline.Count : 0;
            if (n < 2)
            {
                // UC-T04-05B: an incomplete authored course intentionally withdraws its old water.
                RiverSurfaceDiagnostic = n == 0 ? "Place the first river point to set its source."
                    : "Source placed. Add a second point to build the first river segment.";
                return;
            }

            // §2 / gap #1 DE-FACET: the author's control points are straight-segment, which
            // reads as a "valley of joined stones" on a meander. Tessellate them through the
            // centripetal Catmull-Rom curve we already own (cusp/self-intersection-proof) so
            // the channel, bed and mesh follow a SMOOTH curve. Channels authored per control
            // node are sampled onto the dense curve by fractional arc.
            const float RiverTessellationMetres = 5f;

            Transform t = transform;
            Terrain[] terrains = Terrain.activeTerrains;
            UniverseCreatorObject channelWorld = CarveWorld; // UC-T02-07: sample only this body's accepted world metadata.

            BakedRiverProfile authored = riverProfile != null ? riverProfile.Bake(outline) : null;

            Polyline smooth = SplineOperations.TessellateCatmullRom(outline, RiverTessellationMetres, 0.5f);
            ApplyMeander(smooth);
            int m = smooth != null ? smooth.Count : 0;
            if (m < 2) return;

            float[] smoothArc = smooth.BuildArcTable();
            float smoothTotal = smoothArc.Length > 0 ? smoothArc[smoothArc.Length - 1] : 0f;
            float ctrlTotal = authored != null ? authored.TotalLengthM : 0f;

            bool hasW = authored != null && authored.HasWidth;
            bool hasD = authored != null && authored.HasDepth;
            bool hasQ = authored != null && authored.HasDischarge;

            List<Vector3> courseWorld = new List<Vector3>(m);
            RiverChannelSample[] acceptedCourse = FollowTrench ? new RiverChannelSample[m] : null; // UC-T03-09: one lookup per final station.
            float[] halfW = hasW ? new float[m] : null;
            float[] depth = hasD ? new float[m] : null;
            float[] discharge = hasQ ? new float[m] : null;

            for (int i = 0; i < m; i++)
            {
                Vector3 w = t.TransformPoint(smooth.points[i]);
                if (digTerrain || acceptedCourse != null) w.y = SampleGround(terrains, w, w.y); // UC-T04-04: non-excavating manual water uses the authored level, then validates ground coverage.
                if (acceptedCourse != null)
                {
                    if (!RiverChannelRegistry.TrySampleCommitted(channelWorld, w.x, w.z, 0, out acceptedCourse[i], out var validity))
                    { LastChannelValidity = validity; return; } // UC-T03-09: expose absence rather than inventing an unrelated profile.
                    LastChannelValidity = RiverChannelValidity.Valid;
                    if (i == 0) riverFitRun = acceptedCourse[i].Run; // UC-T04-04: every bank sample must belong to this accepted revision.
                    else if (!acceptedCourse[i].Run.Equals(riverFitRun))
                    { RiverSurfaceDiagnostic = "River spans different accepted terrain revisions."; return; }
                    w.y = acceptedCourse[i].Bed.y;
                }
                courseWorld.Add(w);

                if (authored != null)
                {
                    // Smooth point's position along the control course, by arc fraction.
                    float arcC = smoothTotal > 1e-4f ? (smoothArc[i] / smoothTotal) * ctrlTotal : 0f;
                    RiverSample samp = authored.SampleAtArc(arcC);
                    if (halfW != null) halfW[i] = samp.halfWidthM;
                    if (depth != null) depth[i] = samp.depthM;
                    if (discharge != null) discharge[i] = samp.dischargeM3PerS;
                }
            }

            // ---------------------------------------------------------------------------------
            // NATURAL SIZE: a river never starts at zero width or depth, and it follows the trench.
            // Applies only to what the author did not type (typed values always win) and when no
            // discharge was given (a discharge is sized by the hydraulic-geometry path below).
            //   1. Follow Procedural Trench ON and on a trench -> INHERIT that trench's width and depth, and seat
            //      the bed on the trench floor, so the water fills the trench instead of digging a
            //      second one inside it.
            //   2. Anywhere else                   -> start as a STREAM at the source and GROW
            //      downstream (stream -> small river -> river -> big river), never zero.
            // ---------------------------------------------------------------------------------
            float[] solveDepth = depth;
            if (!hasQ && (halfW == null || depth == null))
            {
                float[] nHalf = new float[m];
                float[] nDepth = new float[m];
                bool[] onTrench = new bool[m];
                int hits = 0;

                float mouthWidth = Mathf.Max(2f * carve.riverDefaultHalfWidthM, RiverCourseSolver.HeadwaterWidthM * 2f);

                for (int i = 0; i < m; i++)
                {
                    if (acceptedCourse != null) // UC-T03-09: reuse this station's validated accepted profile.
                    {
                        RiverChannelSample accepted = acceptedCourse[i];
                        nHalf[i] = accepted.HalfWidthMetres; nDepth[i] = accepted.DepthMetres / 0.75f; onTrench[i] = true; hits++;
                        Vector3 point = courseWorld[i]; point.y = accepted.Bed.y; courseWorld[i] = point;
                    }
                    else
                    {
                        float tt = smoothTotal > 1e-4f ? smoothArc[i] / smoothTotal : 0f;
                        RiverCourseSolver.GrowthSection(tt, mouthWidth, out nHalf[i], out nDepth[i]);
                    }
                }

                // Light smoothing so the coarse solve grid does not show as steps, then make the width
                // non-decreasing downstream (a river does not get narrower without losing flow).
                for (int pass = 0; pass < 3; pass++)
                {
                    float[] sh = (float[])nHalf.Clone(); float[] sd = (float[])nDepth.Clone();
                    for (int i = 1; i < m - 1; i++)
                    {
                        if (onTrench[i]) continue; // UC-T03-09: accepted dimensions are immutable; smooth only the authored fallback.
                        nHalf[i]  = 0.25f * sh[i - 1] + 0.5f * sh[i] + 0.25f * sh[i + 1];
                        nDepth[i] = 0.25f * sd[i - 1] + 0.5f * sd[i] + 0.25f * sd[i + 1];
                    }
                }
                for (int i = 1; i < m; i++)
                {
                    if (onTrench[i]) continue; // UC-T03-09: a neighbouring wider channel cannot widen accepted banks.
                    if (nHalf[i]  < nHalf[i - 1])  nHalf[i]  = nHalf[i - 1];
                    if (nDepth[i] < nDepth[i - 1]) nDepth[i] = nDepth[i - 1];
                }
                float minHalf = 0.5f * RiverCourseSolver.HeadwaterWidthM;
                for (int i = 0; i < m; i++)
                {
                    if (onTrench[i]) continue; // UC-T03-09: no headwater floor overrides accepted depth.
                    if (nHalf[i]  < minHalf) nHalf[i]  = minHalf;
                    if (nDepth[i] < 0.2f)    nDepth[i] = 0.2f;
                }

                bool inheritsTrench = hits >= Mathf.Max(2, m / 10);
                bool depthWasNull = depth == null;

                // Following: the water sits BELOW the bank top, in the trench, not brim-full to the
                // original ground level - so the banks stand above the water on both sides.
                if (FollowTrench && inheritsTrench)
                {
                    // UC-T04-02: the body mode owns excavation permission; inherited dimensions only fit the water.
                    for (int i = 0; i < m; i++) if (onTrench[i]) nDepth[i] *= 0.75f;
                }
                if (halfW == null) halfW = nHalf;
                if (depth == null) depth = nDepth;

                if (inheritsTrench && depthWasNull)
                {
                    // Seat the bed on the trench FLOOR: lowest ground across the channel at each point,
                    // and ask the bed solve for ~no extra depth there, so the existing trench is the bed.
                    solveDepth = new float[m];
                    for (int i = 0; i < m; i++)
                    {
                        if (!onTrench[i]) { solveDepth[i] = nDepth[i]; continue; }

                        Vector3 a = courseWorld[Mathf.Max(0, i - 1)];
                        Vector3 b = courseWorld[Mathf.Min(m - 1, i + 1)];
                        Vector2 tan = new Vector2(b.x - a.x, b.z - a.z);
                        tan = tan.sqrMagnitude > 1e-6f ? tan.normalized : Vector2.up;
                        Vector2 nrm = new Vector2(-tan.y, tan.x);

                        float low = courseWorld[i].y;
                        for (int k = -2; k <= 2; k++)
                        {
                            Vector3 q = courseWorld[i];
                            q.x += nrm.x * k * 0.3f * nHalf[i];
                            q.z += nrm.y * k * 0.3f * nHalf[i];
                            float g = SampleGround(terrains, q, courseWorld[i].y);
                            if (g < low) low = g;
                        }
                        Vector3 cw = courseWorld[i]; cw.y = low; courseWorld[i] = cw;
                        solveDepth[i] = 0.001f;
                    }
                }
            }

            SolvedRiverBed bed;
            int bedSig = BedSignature(courseWorld, solveDepth);
            if (!_forceResolveBed && cachedBedY != null && cachedBedY.Length == m && cachedBedSig == bedSig)
            {
                // Unchanged course: keep the bed we already solved (see cachedBedY).
                float[] arcC = new float[m];
                for (int i = 1; i < m; i++)
                {
                    float dx = courseWorld[i].x - courseWorld[i - 1].x, dz = courseWorld[i].z - courseWorld[i - 1].z;
                    arcC[i] = arcC[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
                }
                bed = new SolvedRiverBed
                {
                    bedElevationM = (float[])cachedBedY.Clone(), arcM = arcC,
                    channelSlope = cachedBedSlope, sinuosity = 1f, valid = true
                };
            }
            else
            {
                bed = RiverCourseSolver.SolveBedProfile(courseWorld, solveDepth, carve.riverDefaultDepthM);
                if (!bed.valid) return;
                cachedBedY = (float[])bed.bedElevationM.Clone();
                cachedBedSig = bedSig;
                cachedBedSlope = bed.channelSlope;
            }
            _forceResolveBed = false;

            // Guarantee C (G-C1): where the author gave a discharge but not an explicit
            // width/depth, SIZE the channel by hydraulic geometry (Leopold-Maddock width
            // = a·Q^0.5, depth ∝ width^0.8, Shields grain-stability floor) using the
            // solved channel slope - shape follows flow. Then re-solve the bed against the
            // derived depth so the thalweg and waterline agree. Authored values always win.
            if (discharge != null && (halfW == null || depth == null))
            {
                float[] dw = new float[m];
                float[] dd = new float[m];
                for (int i = 0; i < m; i++)
                    RiverCourseSolver.HydraulicGeometry(discharge[i], bed.channelSlope, out dw[i], out dd[i]);

                if (halfW == null) halfW = dw;
                if (depth == null)
                {
                    depth = dd;
                    bed = RiverCourseSolver.SolveBedProfile(courseWorld, depth, carve.riverDefaultDepthM);
                    if (!bed.valid) return;
                }
            }

            // ---------------------------------------------------------------------------------
            // WATER-PLANE WELDS. Every place this river's water meets other water, the two surfaces
            // must be ONE level, or a step shows (water standing proud of the lake, or a tributary
            // hanging above its main river). At each such end the bed, width and depth are blended
            // over a run of channel so the waterline arrives exactly at the other water's level.
            //   * river mouth in a lake or the ocean     -> waterline = that body's surface
            //   * river source in a lake / pond          -> the lake is the SOURCE; first reach held at its level
            //   * last node welded to another river      -> waterline = that river's waterline there
            // The old mouth blend only pulled the BED to the lake surface, which left the river's WATER
            // a full channel-depth ABOVE the lake - the step. The blend now targets the waterline.
            // ---------------------------------------------------------------------------------
            sourceBody = null; mouthBody = null;
            float[] bedArr = bed.bedElevationM;
            float[] arcArr = bed.arcM;
            if (halfW == null) { halfW = new float[m]; for (int i = 0; i < m; i++) halfW[i] = carve.riverDefaultHalfWidthM; }
            if (depth == null) { depth = new float[m]; for (int i = 0; i < m; i++) depth[i] = carve.riverDefaultDepthM; }

            float blendRun = Mathf.Max(4f, confluenceBlendMetres);

            if (joinsRiver == null && savedMouth != null && !savedMouth.IsLevelBody)
            { joinsRiver = savedMouth; automaticMouthId = savedMouth.StableWaterBodyId; } // UC-T04-07: restore the saved receiving river after a reload.

            UCWaterBody endBody;
            float endLevel;
            if (joinsRiver != null && TryGetRiverJoinLevel(joinsRiver, courseWorld[m - 1], out endLevel, out float mainHalf))
            {
                WeldChannelEnd(bedArr, arcArr, halfW, depth, endLevel, mainHalf, blendRun, false);
            }
            else if (savedMouth != null && savedMouth.IsLevelBody)
            {
                mouthBody = savedMouth;
                WeldChannelEnd(bedArr, arcArr, halfW, depth, savedMouth.SurfaceWorldY(), 0f, blendRun, false);
            }
            else if (TryFindLevelBodyAt(courseWorld[m - 1], Mathf.Max(2f, halfW[m - 1] * 2f), out endBody, out endLevel))
            {
                mouthBody = endBody;
                WeldChannelEnd(bedArr, arcArr, halfW, depth, endLevel, 0f, blendRun, false);
            }

            UCWaterBody startBody = null; float startLevel = 0;
            // UC-T04-06: explicit source direction is resolved before taper/fitting, not only in a saved record.
            var endpointNodes = OwnedRiverNodes();
            var explicitSource = endpointNodes.Length > 0 && endpointNodes[0].connectJunction ? endpointNodes[0].junctionTarget : savedSource; // UC-T04-07: stable saved sources survive reload without proximity rebinding.
            bool sourceFound = false;
            if (SameJunctionScope(explicitSource) && explicitSource.kind != WaterBodyKind.Ocean)
            {
                startBody = explicitSource;
                sourceFound = explicitSource.IsLevelBody ? RiverChannelSnapshot.Finite(startLevel = explicitSource.SurfaceWorldY())
                    : TryGetRiverJoinLevel(explicitSource, courseWorld[0], out startLevel, out _);
            }
            else sourceFound = TryFindLevelBodyAt(courseWorld[0], Mathf.Max(2f, halfW[0] * 2f), out startBody, out startLevel);
            if (sourceFound && startBody.kind != WaterBodyKind.Ocean)
            {
                sourceBody = startBody;
                if (startBody.riverOutlets == null) startBody.riverOutlets = new List<UCWaterBody>();
                if (!startBody.riverOutlets.Contains(this)) startBody.riverOutlets.Add(this);
                WeldChannelEnd(bedArr, arcArr, halfW, depth, startLevel, 0f, blendRun, true);
            }

            // FOLLOWING: fit the water's width to the trench actually in the ground, so the ribbon never
            // overhangs a bank (the "floating water" gap). Walk outward from the centreline until the
            // ground rises above the waterline; the ribbon is as wide as the trench is at that level.
            if (acceptedCourse != null)
            {
                // UC-T04-02/UC-T03-09: read mode consumes accepted bed/level without a second excavation flag.
                if (depth == null) depth = new float[m];
                if (halfW == null) halfW = new float[m];
                for (int i = 0; i < m; i++)
                { bedArr[i] = acceptedCourse[i].Bed.y; depth[i] = acceptedCourse[i].DepthMetres; halfW[i] = acceptedCourse[i].HalfWidthMetres; }
            }
            carve.followOnly = FollowTrench;

            // UC-T04-06: an unfed manual spring introduces depth/width gradually;
            // lake-fed and accepted procedural profiles keep their actual source geometry.
            if (!FollowTrench && digTerrain && sourceBody == null)
            {
                float introduction = Mathf.Min(arcArr[m - 1] * 0.35f, Mathf.Max(2f, halfW[0] * 4f));
                if (introduction > 0.001f)
                    for (int i = 0; i < m && arcArr[i] < introduction; i++)
                    {
                        float weight = Mathf.SmoothStep(0, 1, arcArr[i] / introduction);
                        float level = bedArr[i] + depth[i];
                        depth[i] = Mathf.Lerp(Mathf.Min(0.02f, depth[i]), depth[i], weight);
                        halfW[i] = Mathf.Lerp(Mathf.Min(0.15f, halfW[i]), halfW[i], weight);
                        bedArr[i] = level - depth[i];
                    }
            }

            // UC-T04-06: a short endpoint blend may dip below a feasible downstream
            // level. Raise only that dip, in one bounded backward pass, while keeping
            // both bed and waterline non-increasing and respecting source/ground caps.
            if (!FollowTrench && (sourceBody != null || mouthBody != null || joinsRiver != null))
                for (int i = m - 2; i >= 0; i--)
                {
                    float minimum = Mathf.Max(bedArr[i + 1], bedArr[i + 1] + depth[i + 1] - depth[i]);
                    if (bedArr[i] >= minimum) continue;
                    // A receiving waterline may submerge low mouth ground; the bed may reach that ground, without inventing ground above it.
                    float ceiling = i == 0 && sourceBody != null ? startLevel - depth[i] : courseWorld[i].y;
                    if (minimum > ceiling + 0.001f)
                    { RiverSurfaceDiagnostic = $"Receiving/source level requires an uphill river bed above the available source/ground at station {i}."; return; }
                    bedArr[i] = minimum;
                }

            // A genuinely uphill source/receiver pair remains invalid.
            if (!FollowTrench)
                for (int i = 1; i < m; i++)
                    if (bedArr[i] > bedArr[i - 1] + 0.001f)
                    { RiverSurfaceDiagnostic = $"Receiving/source level requires an uphill river bed at station {i}: {bedArr[i - 1]:F3} to {bedArr[i]:F3} m (source {bedArr[0]:F3}, mouth {bedArr[m - 1]:F3})."; return; } // UC-T04-06: report the actual infeasible span.

            // Flood plain only where the nodes ask for it.
            float[] flood = null;
            if (nodeFloodPlain != null && nodeFloodPlain.Count == outline.Count)
            {
                bool allOne = true;
                for (int i = 0; i < nodeFloodPlain.Count; i++) if (nodeFloodPlain[i] < 0.999f) { allOne = false; break; }
                if (!allOne)
                {
                    RiverProfile fp = new RiverProfile();
                    for (int i = 0; i < nodeFloodPlain.Count; i++) fp.halfWidthM.Add(Mathf.Max(0.001f, nodeFloodPlain[i]));
                    BakedRiverProfile fb = fp.Bake(outline);
                    if (fb != null)
                    {
                        flood = new float[m];
                        for (int i = 0; i < m; i++)
                        {
                            float arcC = smoothTotal > 1e-4f ? (smoothArc[i] / smoothTotal) * fb.TotalLengthM : 0f;
                            flood[i] = Mathf.Clamp01(fb.SampleAtArc(arcC).halfWidthM);
                        }
                    }
                }
            }

            carve.riverRuggedness = ruggedness;
            carve.riverSeed = naturalSeed;
            carve.riverMaxRaiseM = maxBankRaiseMetres;
            carve.SetRiverCourse(courseWorld, bed.bedElevationM, bed.arcM, halfW, depth);
            carve.riverFloodWeight = flood;
            FitWidthToTrench(!FollowTrench && digTerrain); // UC-T04-04: fit a separate read-only shape, using the prospective cut only before native reapplication.
            CaptureWaterJunctions(); // UC-T04-05: persistent identity/direction/dimensions accompany every solved source/mouth.

            // Publish this river's solved course so the graph's River Course Input node always has it,
            // whatever order a regenerate runs in. A river that FOLLOWS a procedural trench publishes the
            // mask only (no bed / depth), so the node never cuts a second trench beside the real one.
            {
                Vector2[] pxz = new Vector2[m];
                for (int i = 0; i < m; i++) pxz[i] = new Vector2(courseWorld[i].x, courseWorld[i].z);
                RiverCourseInputNode.ManualRiverCourse pub = new RiverCourseInputNode.ManualRiverCourse
                {
                    pointsXZ = pxz,
                    halfWidthM = (float[])halfW.Clone(),
                    bedY = FollowTrench || !digTerrain ? null : (float[])bedArr.Clone(), // UC-T04-02: body authority controls graph publication even with explicit dimensions/discharge.
                    depthM = FollowTrench || !digTerrain ? null : (float[])depth.Clone()
                };
                RiverCourseInputNode.PublishBodyCourse(GetEntityId().GetHashCode(), pub);
            }

            // G-X1: per-node curvature so the carve skews the pool to the outer bank of a bend.
            carve.SetRiverCurvature(RiverCourseSolver.SignedCurvature(courseWorld));
        }

        /// <summary>
        /// Fits a separate surface profile to both wetted banks; absent terrain and unsupported geometry fail explicitly.
        /// </summary>
        private bool FitWidthToTrench(bool prospectiveCut)
        {
            riverSurfaceFit = null; riverShoreline = null;
            if (carve == null || !carve.HasShape) return false;
            Terrain[] terrains = Terrain.activeTerrains;
            var world = CarveWorld;
            bool read = FollowTrench;
            bool Sample(float x, float z, out float y)
            {
                y = 0;
                if (read) return world != null && RiverChannelRegistry.TrySampleGround(riverFitRun.WorldId, x, z, riverFitRun.Revision, out y, out var run) && run.Equals(riverFitRun);
                if (!TrySampleSurfaceGround(terrains, world, x, z, out y)) return false;
                if (prospectiveCut) y = carve.HeightAt(x, z, y);
                return true;
            }
            if (!RiverCrossSectionFit.TryFitShoreline(carve.courseWorld, carve.riverBedY, carve.riverDepthM, carve.riverHalfWidth,
                Sample, out var shoreline, out var diagnostic))
            { RiverSurfaceDiagnostic = diagnostic; return false; }
            // UC-T04-06: legacy width consumers receive the conservative half-width; mesh edges keep both actual banks.
            var width = new float[shoreline.Course.Count];
            for (int i = 0; i < width.Length; i++) width[i] = Mathf.Min(shoreline.Left[i], shoreline.Right[i]);
            var fit = new WaterCarveLayer { enabled = false, followOnly = true, kind = WaterBodyKind.River };
            fit.SetRiverCourse(shoreline.Course, shoreline.Bed, shoreline.Arc, width, shoreline.Depth);
            riverShoreline = shoreline; riverSurfaceFit = fit; RiverSurfaceDiagnostic = string.Empty; return true;
        }

        // UC-T04-04: never use an authored Y as proof of ground coverage or sample another world's terrain.
        private static bool TrySampleSurfaceGround(Terrain[] terrains, UniverseCreatorObject world, float x, float z, out float y)
        {
            y = 0;
            if (terrains == null) return false;
            foreach (Terrain terrain in terrains)
            {
                if (terrain == null || terrain.terrainData == null || terrain.GetComponentInParent<UniverseCreatorObject>(true) != world) continue;
                Vector3 origin = terrain.transform.position, size = terrain.terrainData.size;
                if (x < origin.x || z < origin.z || x > origin.x + size.x || z > origin.z + size.z) continue;
                float u = (x - origin.x) / size.x, v = (z - origin.z) / size.z;
                int holes = terrain.terrainData.holesResolution;
                if (holes > 0 && terrain.terrainData.IsHole(Mathf.Min(holes - 1, (int)(u * holes)), Mathf.Min(holes - 1, (int)(v * holes)))) return false;
                y = origin.y + terrain.terrainData.GetInterpolatedHeight(u, v);
                return RiverChannelSnapshot.Finite(y);
            }
            return false;
        }

        /// <summary>
        /// Blends one end of a channel so its WATERLINE arrives at <paramref name="level"/>, over
        /// <paramref name="runMetres"/> of channel, and (for a river junction) widens it toward the
        /// other channel's half-width. At the junction the waterline equals the other water exactly, so
        /// the two surfaces are one plane; further back the blend eases out so the bed keeps its own
        /// shape. Mutates the arrays.
        /// </summary>
        private static void WeldChannelEnd(float[] bed, float[] arc, float[] halfW, float[] depth,
                                           float level, float otherHalfWidth, float runMetres, bool atStart)
        {
            int m = bed.Length;
            if (m < 2 || arc == null || arc.Length != m) return;
            float total = arc[m - 1];
            int end = atStart ? 0 : m - 1;

            float delta = level - (bed[end] + depth[end]);          // how far the waterline must move
            float run = Mathf.Max(1f, Mathf.Min(runMetres, total));

            for (int i = 0; i < m; i++)
            {
                float d = atStart ? arc[i] : total - arc[i];
                if (d > run) continue;
                float u = Mathf.SmoothStep(0f, 1f, 1f - d / run);   // 1 at the weld, 0 at the run's far end
                bed[i] += delta * u;
                if (otherHalfWidth > 0f)
                    halfW[i] = Mathf.Lerp(halfW[i], Mathf.Max(halfW[i], 0.9f * otherHalfWidth), u);
            }
        }

        /// <summary>Reads a prepared receiving river's local level and width without recursively rebuilding it.</summary>
        private bool TryGetRiverJoinLevel(UCWaterBody main, Vector3 endWorld, out float waterlineY, out float halfWidthM)
        {
            waterlineY = 0f; halfWidthM = 0f;
            if (!SameJunctionScope(main) || main.shape != ShapeMode.RiverSpline) return false; // UC-T04-05: reject foreign-world receiver lookups.
            if (main.joinsRiver == this) return false;                         // A joins B joins A

            // UC-T04-07: the explicit dependency owner prepares receivers before incoming rivers.
            if (main.carve == null || !main.carve.isRiver) return false;

            float bedY, lat;
            return main.carve.TryRiverSurfaceAt(endWorld.x, endWorld.z, out waterlineY, out bedY, out halfWidthM, out lat);
        }

        /// <summary>The lake / pond / ocean whose water covers (or is within slack of) a world point,
        /// with its surface level. Uses the body's real shore ring, not a bounding radius, so a river
        /// that merely passes near a lake's bounding circle is not mistaken for entering it.</summary>
        private bool TryFindLevelBodyAt(Vector3 world, float slackMetres, out UCWaterBody found, out float surfaceY)
        {
            found = null; surfaceY = 0f;
            UCWaterBody[] bodies = ScopedJunctionBodies(); // UC-T04-06: scope by actual world/scene, not sibling organization.

            float bestD = float.MaxValue;
            for (int i = 0; i < bodies.Length; i++)
            {
                UCWaterBody b = bodies[i];
                if (!SameJunctionScope(b) || !b.IsLevelBody) continue; // UC-T04-05: level-footprint overlap alone cannot connect different worlds.

                List<Vector3> ring = b.WorldShoreRing();
                float d;
                if (ring != null && ring.Count >= 3)
                {
                    bool inside = PointInRingXZ(ring, world.x, world.z);
                    if (inside) d = 0f;
                    else
                    {
                        Vector3 q;
                        d = NearestOnPolyline(ring, true, world, out q)
                            ? Mathf.Sqrt((q.x - world.x) * (q.x - world.x) + (q.z - world.z) * (q.z - world.z))
                            : float.MaxValue;
                    }
                }
                else continue; // UC-T04-06: a bounding circle is not a receiving shoreline.

                if (d <= slackMetres && Mathf.Abs(d - bestD) <= 0.001f)
                { WaterJunctionDiagnostic = "River endpoint has ambiguous standing-water receivers."; found = null; return false; }
                if (d <= slackMetres && d < bestD) { bestD = d; found = b; surfaceY = b.SurfaceWorldY(); }
            }
            return found != null;
        }

        /// <summary>
        /// Finds a lake/ocean/pool whose footprint the river mouth reaches and returns
        /// its water-surface elevation, for the step-7 mouth blend. Scans sibling water
        /// bodies under the same parent (the WaterSystem root); self and other rivers
        /// are skipped. Returns false when the mouth ends in dry land.
        /// </summary>
        private bool TryFindMouthJoinSurface(Vector3 mouthWorld, out float surfaceY)
        {
            surfaceY = 0f;

            Transform root = transform.parent != null ? transform.parent : transform;
            UCWaterBody[] bodies = root.GetComponentsInChildren<UCWaterBody>(true);

            Vector2 mouthXZ = new Vector2(mouthWorld.x, mouthWorld.z);
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < bodies.Length; i++)
            {
                UCWaterBody b = bodies[i];
                if (b == null || b == this) continue;
                if (!b.IsLevelBody) continue;   // only a still body receives a river mouth

                Vector3 c = b.transform.position;
                float reach = b.JoinReachMetres();
                float d = Vector2.Distance(mouthXZ, new Vector2(c.x, c.z));
                if (d <= reach + MouthJoinSlackMetres && d < best)
                {
                    best = d;
                    surfaceY = b.SurfaceWorldY();
                    found = true;
                }
            }

            return found;
        }

        /// <summary>Slack past a body's reach within which a river mouth still counts as joining it.</summary>
        private const float MouthJoinSlackMetres = 5f;

        /// <summary>
        /// How far this body's edge reaches from its own origin, in metres - a Polygon
        /// from its outline, a Rectangle from half its footprint. Used to decide whether
        /// a river mouth has arrived at it.
        /// </summary>
        public float JoinReachMetres()
        {
            if (shape == ShapeMode.Polygon && outline != null && outline.points != null && outline.Count > 0)
            {
                float reach = 0f;
                for (int i = 0; i < outline.points.Count; i++)
                {
                    Vector3 p = outline.points[i];
                    float d = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                    if (d > reach) reach = d;
                }
                return reach;
            }
            return 0.5f * Mathf.Max(sizeMetres.x, sizeMetres.y);
        }

        /// <summary>Protect a hand-adjusted body from the next regeneration. The
        /// procedural sweep then treats it like a placed body - not culled, not torn
        /// down when the node is disconnected - and its footprint is held and re-cut
        /// rather than re-fitted, so the graph math merges around the edit.</summary>
        public void SavePlacement()
        {
            placementSaved = true;
            onRegenerate = ProceduralResponse.HoldPlacement;
        }

        /// <summary>Undo SavePlacement - hand the body back to the generator so a
        /// regeneration can re-fit, re-count and remove it again.</summary>
        public void ReleasePlacement()
        {
            placementSaved = false;
        }

        /// <summary>
        /// What a body does when the terrain under it has just finished regenerating.
        /// Returns true when the dig description changed and the terrain owes itself a
        /// re-cut.
        ///
        /// This is the automatic half of the procedural promise. The channel itself is
        /// never at risk - it is re-derived inside every chunk apply, so a regeneration
        /// structurally cannot erase it. What this decides is whether the ROUTE is
        /// re-fitted: new ground means the valley moved, and a river that keeps its old
        /// course is now cutting a trench across a hillside instead of running down the
        /// bottom of the new valley.
        ///
        /// Only rivers re-fit. A lake or a pool is where the designer put it - the
        /// footprint is the mark, and re-fitting it would mean the body wandering off
        /// on its own every time the world was rebuilt.
        /// </summary>
        public bool OnTerrainSettled()
        {
            // River auto-refit has been removed. Lakes, pools and oceans use
            // HoldPlacement - their footprint is a placement, re-cut in place by the
            // chunk build - so no body re-fits its route on settle and nothing here
            // owes the terrain a re-cut.
            return false;
        }

        // River channel dig was removed in the Phase 0 teardown and then REBUILT:
        // BuildRiverDig is live again (defined ~line 1033, called from the shape
        // dispatch ~line 1018) and digs the manual spline channel. What stayed gone
        // is ConfineTargetAt and the standalone course solver's internals (see the
        // block below). Lake/ocean digs are built by BuildLevelDig below.

        // The solved-course machinery (solved* arrays, the WaterfallReach struct,
        // the course solver and its cascade detection) was removed in the river
        // teardown.

        // The river course solver and all of its helpers (SolveRiverCourse,
        // BuildBankAwareReadChannel, DetectCascades, BuildManualCascades,
        // RawSurfaceAt, EnforceDownhill, ApplyCoastalTaper, ContainToBanks, the
        // junction extension/blend, hydraulic geometry and node-series sampling)
        // were removed in the river teardown. Rivers are being rebuilt from
        // scratch; lake/ocean level bodies never used this machinery.
















        // ExtendToJunction and BlendJunction (river-only course welding onto the
        // body a mouth/head joins) were removed in the river teardown.



        /// <summary>
        /// Closest point on this body's water edge to a world position, with the
        /// surface level there and how deep this body is a short way inside it.
        ///
        /// This is what a joining river asks. It is answered from the serialised
        /// fields rather than from a built dig, so the answer does not depend on which
        /// of the two bodies happened to be enabled first.
        /// </summary>
        public bool NearestEdgePoint(Vector3 fromWorld, float insetMetres,
                                     out Vector3 point, out float surfaceY, out float depthAtJoin)
        {
            point = fromWorld;
            surfaceY = SurfaceWorldY();
            depthAtJoin = 0f;

            List<Vector3> ring = WorldShoreRing();
            if (ring == null || ring.Count < 3) return false;

            // Refinement #4 (P3) - a river joining a LAKE snaps to the nearest lake POLYGON
            // NODE, not to an interpolated point along an edge. A polygon lake's ring IS its
            // authored outline (WorldShoreRing returns ToWorld(outline) for a Polygon), so its
            // vertices are exactly the nodes the author dropped. Landing the mouth on a node
            // makes the two bodies touch where the lake was actually pinned, so they read as
            // one sheet instead of the river ending at a gizmo edge a little off the node. The
            // OCEAN carries no node connection (the author never places its outline the same
            // way), and a rectangle has no meaningful "nodes", so both keep the edge-point snap.
            if (shape == ShapeMode.Polygon && kind != WaterBodyKind.Ocean)
            {
                int best = -1;
                float bestD2 = float.MaxValue;
                for (int i = 0; i < ring.Count; i++)
                {
                    float ex = fromWorld.x - ring[i].x;
                    float ez = fromWorld.z - ring[i].z;
                    float d2 = ex * ex + ez * ez;
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
                if (best < 0) return false;
                point = ring[best];
            }
            else if (!NearestOnPolyline(ring, true, fromWorld, out point)) return false;

            // How deep this basin is one blend-length in from its own shore. A river
            // arriving at a lake should be as deep as the lake is where it arrives,
            // which is not the lake's maximum depth and is not zero either.
            float t = Mathf.Clamp01(Mathf.Max(0f, insetMetres) / Mathf.Max(0.5f, shoreRunMetres));
            float frac = WaterBedMath.DepthFraction(bedProfile, t, shelfFraction, bankProfile);
            depthAtJoin = Mathf.Max(0f, digDepthMetres * frac);

            return true;
        }

        /// <summary>Closest point on a polyline in XZ. Y is carried from the segment.</summary>
        private static bool NearestOnPolyline(List<Vector3> line, bool closed,
                                              Vector3 fromWorld, out Vector3 point)
        {
            point = fromWorld;
            if (line == null || line.Count < 2) return false;

            int segs = closed ? line.Count : line.Count - 1;
            float best = float.MaxValue;

            for (int i = 0; i < segs; i++)
            {
                Vector3 a = line[i];
                Vector3 b = line[(i + 1) % line.Count];

                float dx = b.x - a.x;
                float dz = b.z - a.z;
                float len2 = dx * dx + dz * dz;
                if (len2 < 1e-8f) continue;

                float t = Mathf.Clamp01(((fromWorld.x - a.x) * dx + (fromWorld.z - a.z) * dz) / len2);

                Vector3 p = new Vector3(a.x + dx * t, Mathf.Lerp(a.y, b.y, t), a.z + dz * t);

                float ex = fromWorld.x - p.x;
                float ez = fromWorld.z - p.z;
                float d2 = ex * ex + ez * ez;

                if (d2 < best) { best = d2; point = p; }
            }

            return best < float.MaxValue;
        }

        /// <summary>
        /// A lake, a pool or an ocean: a closed ring at one elevation.
        ///
        /// The ring is the body's own footprint - the rectangle it covers or the
        /// polygon outline that was drawn on it. That is the whole of "the place where
        /// the body sits is where the carve happens": outside the ring and its bevel,
        /// HeightAt returns the ground it was handed, untouched.
        /// </summary>
        private void BuildLevelDig()
        {
            carve.profile = bedProfile;
            carve.bankExponent = bankProfile;
            carve.shelfFraction = shelfFraction;
            carve.maxDepthMetres = digDepthMetres;
            carve.shoreRunMetres = shoreRunMetres;
            carve.surfaceY = SurfaceWorldY();

            // What stops the basin being a funnel with a point at the bottom. The
            // depth still increases inward from every part of the shore, but it does
            // not arrive at the same time on every bearing.
            carve.basinIrregularity = basinIrregularity;
            carve.basinFeatureMetres = basinFeatureMetres;

            // A pool is built, not eroded, so it has no natural bed to roughen unless
            // the designer asks for one. Lakes get their ruggedness as authored.
            carve.ruggednessMetres = bedRuggednessMetres;
            carve.ruggednessFeatureMetres = ruggednessFeatureMetres;
            carve.ruggednessOctaves = ruggednessOctaves;
            carve.ruggednessPersistence = ruggednessPersistence;
            carve.ruggednessCalmMetres = ruggednessCalmMetres;

            List<Vector3> ring = WorldShoreRing();
            if (ring == null || ring.Count < 3) return;

            // A governing dam impounds only the upstream side of its line, so the basin is
            // cut there and not through the wall. Clipping the ring here - the one place the
            // dig's bound is built - keeps the excavation, the island mask and the mesh cull
            // all reading the same clipped footprint. Fewer than three points left means the
            // dam holds back nothing placed here, and the dig honestly cuts nothing.
            if (damGoverned)
            {
                ClipRingUpstream(ring);
                if (ring.Count < 3) return;
            }

            for (int i = 0; i < ring.Count; i++) carve.ringPoints.Add(ring[i]);

            // UC-T08-15: capture axes once per accepted ring; samples reuse the shared immutable formation tables.
            carve.PrepareLakeFormation(lakeFormation, lakeSedimentation);

            // PER-RING-NODE DEPTH (the rugged basin). A Polygon lake's ring points are
            // its outline points in order (WorldShoreRing returns ToWorld(outline)), so
            // nodeDepthMetres[i] describes ring node i: a border node over the deep
            // trough asks for a large depth, one on a shelf a small one, and the carve
            // eases between them round the shore (RingDepthAt). Same carry-forward rule
            // as a river's node depths - 0 means "not set here", inheriting the last
            // authored value and finally digDepthMetres - so a designer types depths
            // only where the bed actually changes. Left empty for a Rectangle (four
            // corners, no meaningful per-node bed) and when nothing is authored, and
            // then RingDepthAt falls back to the flat digDepthMetres exactly as before.
            if (!damGoverned && shape == ShapeMode.Polygon &&
                nodeDepthMetres != null && nodeDepthMetres.Count > 0 &&
                ring.Count == (outline != null ? outline.Count : -1))
            {
                float carried = Mathf.Max(0.05f, digDepthMetres);
                for (int i = 0; i < ring.Count; i++)
                {
                    float v = i < nodeDepthMetres.Count ? nodeDepthMetres[i] : 0f;
                    if (v > 1e-4f) carried = v;
                    carve.ringDepths.Add(carried);
                }
            }

            // Keep large projections inside the ring as islands; the carve steps around
            // them and the water routes around them. Small ones carry no mask and are
            // dug to the bed. The ocean is exempt: its "islands" are continents above
            // sea level, already handled by the bevel-against-level shore cut. (T22.D/E)
            if (!carve.bevelOnly) BuildIslandMask(ring, carve.surfaceY);
        }

        /// <summary>
        /// The ring the dig is bounded by, in world space.
        ///
        /// The polygon outline when there is one, because a drawn outline is an
        /// explicit statement about where the water's edge is. Otherwise the
        /// rectangle footprint, so a body works the moment it is dropped in the scene
        /// with nothing authored on it.
        /// </summary>
        private List<Vector3> WorldShoreRing()
        {
            if (shape == ShapeMode.Polygon && outline != null && outline.Count >= 3)
                return ToWorld(outline);

            // Rectangle footprint. Corners only: the ring math is exact on straight
            // edges, so subdividing them would cost samples and change nothing.
            float hx = sizeMetres.x * 0.5f;
            float hz = sizeMetres.y * 0.5f;

            List<Vector3> rect = new List<Vector3>(4);
            rect.Add(transform.TransformPoint(new Vector3(-hx, 0f, -hz)));
            rect.Add(transform.TransformPoint(new Vector3( hx, 0f, -hz)));
            rect.Add(transform.TransformPoint(new Vector3( hx, 0f,  hz)));
            rect.Add(transform.TransformPoint(new Vector3(-hx, 0f,  hz)));

            return rect;
        }

        /// <summary>
        /// Clips a shore ring to the upstream side of a governing dam's line, in place.
        ///
        /// Sutherland-Hodgman against the single half-plane (p - barrierCentre) . upstreamNormal
        /// >= 0: vertices on the impounded side are kept, and each edge that crosses the line
        /// contributes its intersection point, so the ring closes cleanly along the wall. The
        /// same predicate the mesh cull and the tool's own flood fill use, so all three agree on
        /// which side the water is. Fewer than three points left means the body lies wholly
        /// downstream and impounds nothing - the honest zero of a dam holding back no placed lake.
        /// Only called while <see cref="damGoverned"/>.
        /// </summary>
        private void ClipRingUpstream(List<Vector3> ring)
        {
            if (ring == null || ring.Count < 3) return;

            List<Vector3> kept = new List<Vector3>(ring.Count + 2);
            float cx = damBarrierCentre.x, cz = damBarrierCentre.y;
            float nx = damUpstreamNormal.x, nz = damUpstreamNormal.y;

            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 cur = ring[i];
                Vector3 prev = ring[(i - 1 + ring.Count) % ring.Count];

                float dCur = (cur.x - cx) * nx + (cur.z - cz) * nz;
                float dPrev = (prev.x - cx) * nx + (prev.z - cz) * nz;
                bool curIn = dCur >= 0f;
                bool prevIn = dPrev >= 0f;

                // Signs differ => the edge crosses the line and dPrev - dCur is non-zero, so the
                // parameter is safe from a divide by zero; equal signs never take that branch.
                if (curIn)
                {
                    if (!prevIn) kept.Add(Vector3.LerpUnclamped(prev, cur, dPrev / (dPrev - dCur)));
                    kept.Add(cur);
                }
                else if (prevIn)
                {
                    kept.Add(Vector3.LerpUnclamped(prev, cur, dPrev / (dPrev - dCur)));
                }
            }

            ring.Clear();
            ring.AddRange(kept);
        }

        private List<Vector3> ToWorld(Polyline line)
        {
            List<Vector3> world = new List<Vector3>(line.Count);
            for (int i = 0; i < line.Count; i++)
                world.Add(transform.TransformPoint(line.PointAt(i)));

            return world;
        }

        // ---------------------------------------------------------------------
        //  Islands
        // ---------------------------------------------------------------------

        /// <summary>
        /// Fixed real-world floor for a kept island, in square metres. A projection
        /// inside the ring at or above this area survives as land; anything smaller is
        /// dug into the bed so a lake is not stippled with islets. A hard floor rather
        /// than a fraction of the lake, because "big enough to be land" is an absolute
        /// size in the world, not relative to the water it happens to sit in.
        /// </summary>
        private const float IslandAreaFloorSqM = 500f;

        /// <summary>
        /// Builds the carve's island keep-out mask: the cells of large above-water
        /// projections inside the ring. Samples the ground on a coarse grid over the
        /// ring's plan bounds, flags the cells that are inside the ring and rise to or
        /// above the water surface, groups them into connected components, and keeps
        /// only the components whose area reaches the island floor. Leaves the mask null
        /// when nothing qualifies, which is the common case and the old behaviour.
        /// </summary>
        private void BuildIslandMask(List<Vector3> ring, float surfaceY)
        {
            carve.islandMask = null;
            carve.islandCols = 0;
            carve.islandRows = 0;

            if (ring == null || ring.Count < 3) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 p = ring[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            if (spanX < 1f || spanZ < 1f) return;

            // ~4 m cells, clamped so a huge lake does not sample a giant grid. Coarser
            // on a very large body, which only softens the island edge, never the area
            // test - that always multiplies by the real cell area.
            int cols = Mathf.Clamp(Mathf.CeilToInt(spanX / 4f), 2, 160);
            int rows = Mathf.Clamp(Mathf.CeilToInt(spanZ / 4f), 2, 160);
            float cell = Mathf.Max(spanX / cols, spanZ / rows);
            float cellArea = cell * cell;

            int n = cols * rows;
            bool[] above = new bool[n];

            for (int r = 0; r < rows; r++)
            {
                float wz = minZ + (r + 0.5f) * cell;
                for (int c = 0; c < cols; c++)
                {
                    float wx = minX + (c + 0.5f) * cell;
                    if (!PointInRingXZ(ring, wx, wz)) continue;
                    if (SampleGround(wx, wz) >= surfaceY) above[r * cols + c] = true;
                }
            }

            // Connected components, 4-neighbour flood fill. A component is kept whole or
            // not at all, so an island never has a chewed edge from the threshold.
            bool[] keep = new bool[n];
            bool[] seen = new bool[n];
            int[] stack = new int[n];
            List<int> comp = new List<int>();
            bool any = false;

            for (int start = 0; start < n; start++)
            {
                if (!above[start] || seen[start]) continue;

                comp.Clear();
                int sp = 0;
                stack[sp++] = start;
                seen[start] = true;

                while (sp > 0)
                {
                    int cur = stack[--sp];
                    comp.Add(cur);

                    int cc = cur % cols;
                    int cr = cur / cols;

                    if (cc > 0)        { int nb = cur - 1;    if (above[nb] && !seen[nb]) { seen[nb] = true; stack[sp++] = nb; } }
                    if (cc < cols - 1) { int nb = cur + 1;    if (above[nb] && !seen[nb]) { seen[nb] = true; stack[sp++] = nb; } }
                    if (cr > 0)        { int nb = cur - cols; if (above[nb] && !seen[nb]) { seen[nb] = true; stack[sp++] = nb; } }
                    if (cr < rows - 1) { int nb = cur + cols; if (above[nb] && !seen[nb]) { seen[nb] = true; stack[sp++] = nb; } }
                }

                if (comp.Count * cellArea >= IslandAreaFloorSqM)
                {
                    for (int k = 0; k < comp.Count; k++) keep[comp[k]] = true;
                    any = true;
                }
            }

            if (!any) return;

            carve.islandMask = keep;
            carve.islandMinX = minX;
            carve.islandMinZ = minZ;
            carve.islandCellSize = cell;
            carve.islandCols = cols;
            carve.islandRows = rows;
        }

        /// <summary>Even-odd point-in-polygon on the ring's XZ, in world space.</summary>
        private static bool PointInRingXZ(List<Vector3> ring, float x, float z)
        {
            bool inside = false;
            int count = ring.Count;
            int j = count - 1;
            for (int i = 0; i < count; i++)
            {
                Vector3 a = ring[i];
                Vector3 b = ring[j];
                if ((a.z > z) != (b.z > z))
                {
                    float cross = (b.x - a.x) * (z - a.z) / (b.z - a.z) + a.x;
                    if (x < cross) inside = !inside;
                }
                j = i;
            }
            return inside;
        }

        // The river read-mode trench scanners (MeasureTrenchWidths, ScanTrenchWall),
        // the course resample/solve helpers (WorldCourse, ApplyHydraulicGeometry,
        // NaturalDepthForWidth, ComputeNaturalChannel, ApplySourceStreamProfile,
        // ValleyConfinement), the per-node series helpers (ResolveNodeSeries,
        // SmoothBetweenAuthored, SampleNodeSeries, SampleFloodplainAlong,
        // ResampleSeriesAlong) and their *ForTest wrappers were removed in the river
        // teardown. Lake/ocean bodies read their bed from WorldShoreRing, not a course.



        // The downstream hydraulic-geometry pass (ApplyHydraulicGeometry), the natural
        // channel / source-stream helpers (NaturalDepthForWidth, ComputeNaturalChannel,
        // ApplySourceStreamProfile), the valley-confinement probe (ValleyConfinement),
        // their hydraulic constants (BaseWidthDepthRatio, WidthAreaExponent,
        // GorgeRatioSpan) and the editor-only *ForTest seams were removed in the river
        // teardown. Lake/ocean bodies size their bed from the shore ring, not a course.





        /// <summary>Ground elevation at a world XZ. The solver's view of the world.</summary>
        /// <remarks>
        /// PRISTINE GROUND FIRST, AND IT MATTERS.
        ///
        /// The course takes its water surface from this, and the dig then cuts the bed
        /// one depth below that surface. Read off the live terrain, the answer already
        /// contains the channel this river cut on the previous pass, so every re-fit
        /// hands back a surface a channel-depth lower than the last one and the river
        /// walks itself down into the world - once per chunk that settles.
        ///
        /// Read off the graph's undug output, the answer is the same on every pass, so
        /// re-fitting is idempotent and "the ground moved" means what it says.
        ///
        /// The live terrain stays the fallback for the world's very first fit, before
        /// anything has been baked - it is undug at that point, which is exactly when
        /// the fallback is safe. OnTerrainSettled refuses to move at all while the
        /// pristine answer is missing, so the fallback can never drive a re-fit.
        /// </remarks>
        private float SampleGround(float worldX, float worldZ)
        {
            // Resolved once per solve rather than once per point: CarveWorld is a
            // GetComponentInParent walk and a solve asks for hundreds of points.
            if (!groundSourceResolved)
            {
                UniverseCreatorObject world = CarveWorld;
                groundSource = world != null ? world.Chunks : null;
                groundSourceResolved = true;
            }

            if (groundSource != null)
            {
                float pristine;
                if (groundSource.TrySampleGraphHeight(worldX, worldZ, out pristine))
                    return pristine;
            }

            Vector3 p = new Vector3(worldX, 0f, worldZ);
            return SampleGround(Terrain.activeTerrains, p, transform.position.y);
        }

        [System.NonSerialized] private ChunkManager groundSource;
        [System.NonSerialized] private bool groundSourceResolved;

        // =====================================================================
        //  AUTO CONNECT (river-only) removed in the river teardown. The join
        //  candidate gather, ResolveJoin, RefreshJoinCandidates, SubmergedFloorY
        //  and the river->ocean/lake proximity read (IsAtJoinableWater), together
        //  with the TouchingJoinables / IsJoinedTo / SetJoinTicked tick machinery
        //  and the joinCandidates/autoJoinDownstream state, are gone. Lakes and
        //  oceans are placed footprints that do not "join"; IsConnected /
        //  ConnectionSummary below are neutralised so the shared inspector status
        //  line still compiles.
        // =====================================================================

        /// <summary>
        /// Asks the world to re-cut every dig into the terrain that is already built.
        ///
        /// Cheap enough to call on every commit: only chunks the digs actually reach
        /// are rewritten, and no graph evaluation happens at all.
        /// </summary>
        public void ReapplyToTerrain()
        {
            if (shape == ShapeMode.RiverSpline && FollowTrench) return; // UC-T04-02: surface refitting never requests a native terrain rewrite.
            UniverseCreatorObject world = CarveWorld;
            if (world == null || world.Chunks == null) return;

            world.Chunks.ReapplyCarves();
        }



        // =====================================================================
        //  BUILD
        // =====================================================================

        /// <summary>
        /// Builds the surface. Safe to call repeatedly - it replaces rather than
        /// accumulates, which is why the inspector can call it on every commit.
        /// </summary>
        public void Rebuild()
        {
            RebuildWithJunctionDependencies(); // UC-T04-07: one bounded nonrecursive owner prepares receivers and affected incoming bodies.
        }

        /// <summary>
        /// Rebuilds the visible surface from the course as it currently stands, without
        /// re-solving the dig.
        ///
        /// The settle re-fit calls this after BuildDig(false) has already produced the
        /// new course: the mesh reads the same solved points the channel was cut from,
        /// so re-solving here would be the same answer computed twice.
        /// </summary>
        public void RebuildSurface()
        {
            // UC-T04-05B: deleting the final segment is not a failed replacement fit.
            if (shape == ShapeMode.RiverSpline && (outline == null || outline.Count < 2)) { Clear(); return; }
            WaterBodyData body = BuildData();
            if (body == null)
            {
                // UC-T04-07: a rejected fit cannot preserve a surface from a retired world revision.
                var world = CarveWorld;
                if (world != null && !world.GenerationScheduler.CurrentRun.Equals(junctionAcceptedRun)) Clear();
                return;
            }
            var querySurface = generateVolume ? new WaterQuerySurface(body, transform.localToWorldMatrix, kind != WaterBodyKind.Ocean, water.flowMetresPerSecond) : null; // UC-T04-09: validate query budgets before retiring the accepted mesh.
            Clear(); // UC-T04-04: preserve the last accepted native mesh/volume until replacement data validates.

            lastBuild = body;

            surfaceObject = new GameObject("UC Water Surface");
            surfaceObject.layer = gameObject.layer;

            // Never serialised. It is rebuilt from these fields on every enable, so
            // saving it would mean a scene that grows by every water surface it has
            // ever shown and duplicates them on the next build.
            surfaceObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable; // UC-T04-05B: derived children stay read-only without writing Editor Undo history.
            surfaceObject.transform.SetParent(transform, false);
            surfaceObject.transform.localPosition = Vector3.zero;
            surfaceObject.transform.localRotation = Quaternion.identity;
            surfaceObject.transform.localScale = Vector3.one;

            // UC-T04-05B: SceneVisibilityManager.DisablePicking records an Undo operation.
            // Calling it while refreshing an undone course clears the user's Redo branch.
            // Use native read-only flags for these temporary render children instead.

            mesh = new Mesh();
            mesh.name = "UC Water Mesh";
            mesh.hideFlags = HideFlags.DontSave;
            mesh.indexFormat = body.vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices = body.vertices;
            mesh.normals = body.normals;
            mesh.tangents = body.tangents;
            mesh.uv = body.uv;
            mesh.colors = body.colors;
            mesh.triangles = body.triangles;

            // By hand rather than RecalculateBounds: the shader lifts vertices by the
            // wave height, and a mesh culled on its undisplaced bounds pops out of
            // view at the screen edge on a swell.
            Bounds local = new Bounds(body.vertices[0], Vector3.zero);
            for (int i = 1; i < body.vertices.Length; i++) local.Encapsulate(body.vertices[i]);
            local.Expand(new Vector3(0f, 4f, 0f));
            mesh.bounds = local;

            MeshFilter filter = surfaceObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = surfaceObject.AddComponent<MeshRenderer>();
            readinessSurfaceRenderer = renderer; // UC-T09-03: cache the derived renderer for dependency presentation without per-pump component lookup.
            renderer.sharedMaterial = ResolveMaterial(body);

            // Transparent water casting a hard shadow is wrong, and receiving one
            // costs a shadow sample per water pixel to change almost nothing.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;

            if (generateVolume) AttachVolume(body, querySurface); // UC-T04-09: rendering and gameplay publish one accepted footprint.

            // Far Distance Mesh (§4i.3): an ocean fills to the camera's far clip, not to
            // its own mesh edge. Built from the same material at zero wave weight so it is
            // flat and color-matched, and cheap (two loops of the body outline). Ocean
            // only, and only when horizonDistanceMetres > 0.
            BuildOceanHorizon(body);

            AcceptWaterJunctionGeometry(); // UC-T04-06: clipped/contained preparation is now an accepted native surface.
            nativeWaterPublications[this] = new WaterPublicationOwner(gameObject.scene.handle.GetRawData(), GetEntityId().GetHashCode()); // UC-T04-07: retain the full supported scene handle; native and graph keys retire never-active bodies.
            waterPublicationWorld = CarveWorld; // UC-T04-07: capture the actual owner before hierarchy changes.
            RegisterStreamingPresentation(); // UC-T09-03: newly rebuilt water inherits this world's settlement hold before any frame can render it.

            // Waterfalls were a river-course feature (driven by a river's solved cascades).
            // The river teardown removed the course solve, its cascades, and the waterfall
            // effect component itself; no body produces falls now, so there is nothing to
            // attach or tear down here. The rebuilt river system will reintroduce falls on
            // its own terms.
        }

        /// <summary>Destroys everything Rebuild created. Leaves the settings alone.</summary>
        public void Clear()
        {
            RetireJunctionDependents(); // UC-T04-07: explicit clear retires affected incoming surfaces without recursion.
            ClearNativeGeometry();
        }

        private void ClearNativeGeometry()
        {
            RetireStreamingPresentation(); // UC-T09-03: retired/rebound native water cannot remain in a former world's presentation list.
            waterPublicationWorld = null; // UC-T04-07: release the former world's runtime ownership witness.
            nativeWaterPublications.Remove(this); // UC-T04-07: stop retaining this retired scene publication.
            if (volume != null) { DestroySafe(volume); volume = null; }

            // The mesh first. Destroying the GameObject leaves the Mesh resident -
            // it is an asset object, not a component, and nothing collects it.
            if (mesh != null) { DestroySafe(mesh); mesh = null; }
            if (horizonMesh != null) { DestroySafe(horizonMesh); horizonMesh = null; }
            if (horizonObject != null) { DestroySafe(horizonObject); horizonObject = null; }
            if (material != null) { DestroySafe(material); material = null; }
            if (surfaceObject != null) { DestroySafe(surfaceObject); surfaceObject = null; }

            lastBuild = null;
        }

        private static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // =====================================================================
        //  DATA
        // =====================================================================

        /// <summary>
        /// Produces the body data without touching the scene. Public so the
        /// inspector can report what a build would produce, and so a test can check
        /// the geometry without instantiating anything.
        /// </summary>
        public WaterBodyData BuildData()
        {
            Terrain[] terrains = bedSource == BedSource.Terrain ? Terrain.activeTerrains : null;

            // A river's surface is a descending swept ribbon along its course; every
            // other body is a level sheet. The river branch reuses the SAME grid +
            // Assemble path as a lake, only its per-cell surface descends and its wet
            // mask is the channel band - so there is one mesh builder, not two.
            WaterBodyData body = shape == ShapeMode.RiverSpline
                ? BuildRiverBody(terrains)
                : BuildLevelBody(terrains);


            if (body == null) return null;

            body.kind = kind;
            body.name = kind.ToString();
            body.generateVolume = generateVolume;
            body.maxFlowSpeed = Mathf.Max(0.01f, maxFlowSpeed);
            body.product = useOwnMaterial ? water : null;

            // UC-T04-06: remove receiver coverage before native publication; shared edges use its actual level.
            if (shape == ShapeMode.RiverSpline && !TryJoinRiverSurface(body, out body)) return null;
            if (!ApplyJunctionWaveContacts(body)) return null; // UC-T04-09: receiver patches and incoming shared edges have coherent still geometry.

            return body;
        }

        // ---------------------------------------------------------------------
        //  Level body
        // ---------------------------------------------------------------------

        private WaterBodyData BuildRiverBody(Terrain[] terrains)
        {
            // UC-T04-04: a grid extending past the bank cannot represent a thin channel.
            // The bounded fitted strip shares WaterBodyData/material attributes with level bodies.
            return FitWidthToTrench(false) ? BuildFittedRiverSurface(terrains) : null;
        }

        private WaterBodyData BuildLevelBody(Terrain[] terrains)
        {
            // Footprint in LOCAL metres. Building in local space and only going to
            // world for the terrain sample is what makes a rotated pool rotate its
            // outline rather than shear it.
            float halfX, halfZ, centreX, centreZ;

            if (shape == ShapeMode.Polygon)
            {
                if (outline == null || outline.Count < 3) return null;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;

                for (int i = 0; i < outline.Count; i++)
                {
                    Vector3 p = outline.points[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }

                // One cell of margin so the shoreline fade has somewhere to happen
                // instead of being clipped off by the bounding box.
                float margin = Mathf.Max(maxX - minX, maxZ - minZ) / Mathf.Max(4, meshResolution) * 2f;

                centreX = (minX + maxX) * 0.5f;
                centreZ = (minZ + maxZ) * 0.5f;
                halfX = (maxX - minX) * 0.5f + margin;
                halfZ = (maxZ - minZ) * 0.5f + margin;
            }
            else
            {
                centreX = 0f;
                centreZ = 0f;
                halfX = sizeMetres.x * 0.5f;
                halfZ = sizeMetres.y * 0.5f;
            }

            if (halfX < 0.01f || halfZ < 0.01f) return null;

            // Square cells regardless of aspect, so a long thin lake is not sampled
            // finely across and coarsely along.
            int longest = Mathf.Clamp(meshResolution, 4, 240);
            float cell = Mathf.Max(halfX, halfZ) * 2f / longest;

            int gx = Mathf.Clamp(Mathf.CeilToInt(halfX * 2f / cell) + 1, 2, 512);
            int gz = Mathf.Clamp(Mathf.CeilToInt(halfZ * 2f / cell) + 1, 2, 512);

            int n = gx * gz;
            float[] surf = new float[n];
            float[] depth = new float[n];
            bool[] wet = new bool[n];

            float surfaceY = SurfaceWorldY();

            bool usePolygon = shape == ShapeMode.Polygon && outline != null && outline.closed && outline.Count >= 3;

            // A governing dam impounds only the upstream side of its line. The dig ring was clipped to that half-plane;
            // the mesh must cull the same way, or the flat raised surface would flood over the natural ground the dig
            // deliberately left standing downstream of the wall.
            bool clipDam = damGoverned;
            float damCx = damBarrierCentre.x, damCz = damBarrierCentre.y;
            float damNx = damUpstreamNormal.x, damNz = damUpstreamNormal.y;

            // Trim the surface to the bed the water will actually sit on. When this body
            // digs, that bed is the carved basin solved in BuildDig (run immediately
            // before this on Rebuild), not the raw pre-carve ground - so the waterline
            // lands where the ground crosses the surface rather than at the polygon edge,
            // a small bump the carve flattens reads as covered, and a kept island reads
            // as dry. Null (no dig, or a standalone preview before BuildDig) falls back
            // to the raw ground, which for an undug body is the same bed.
            WaterCarveLayer dugBed = (carve != null && carve.HasShape) ? carve : null;

            for (int z = 0; z < gz; z++)
            {
                float lz = centreZ - halfZ + z * cell;

                for (int x = 0; x < gx; x++)
                {
                    float lx = centreX - halfX + x * cell;
                    int i = z * gx + x;

                    Vector3 world = transform.TransformPoint(new Vector3(lx, 0f, lz));

                    float ground = bedSource == BedSource.FlatFloor
                        ? surfaceY - flatFloorDepthMetres
                        : SampleGround(terrains, world, surfaceY - flatFloorDepthMetres);

                    // A FlatFloor body sits in its own pit at a fixed depth and is meant
                    // to fill its whole footprint, so it never reads the carve. A
                    // terrain-bed body reads the carved bed so the mesh matches the hole.
                    float bed = (dugBed != null && bedSource != BedSource.FlatFloor)
                        ? dugBed.HeightAt(world.x, world.z, ground)
                        : ground;

                    surf[i] = surfaceY;
                    depth[i] = surfaceY - bed;

                    bool inside = !usePolygon || SplineOperations.ContainsXZ(outline, lx, lz);

                    // Downstream of the dam line the reservoir does not reach, so its wet mask stops at the wall.
                    if (clipDam && (world.x - damCx) * damNx + (world.z - damCz) * damNz < 0f) inside = false;

                    // Cull at the waterline (this REPLACES the old flood-the-whole-polygon
                    // rule): a vertex whose bed is at or above the surface is dry - a bank,
                    // a kept island, or ground the ring merely bounds - and carries no water
                    // shader. Assemble still keeps one dry ring past the wet area for the
                    // shoreline fade, and ConformGrid drops that ring onto the ground, so the
                    // seam stays watertight with no floating edge. Lakes, pools and the ocean
                    // now begin exactly where the ground crosses the water, like the
                    // hand-drawn ocean - no shader under the banks. (T22.A/C/H, T10 shore cut.)
                    wet[i] = inside && depth[i] > 0f;
                }
            }

            // How deep this body ACTUALLY is, read before the conform below bounds
            // the depth channel. See the note under the Assemble call.
            float deepestRaw = 0f;
            for (int i = 0; i < n; i++)
                if (wet[i] && depth[i] > deepestRaw) deepestRaw = depth[i];

            // Seat the shore edge into the ground and bound the depth. The named
            // constants are the single source for both figures - passing literals
            // here is how the two water paths drifted apart before. (Spec 4.2/4.3.)
            float[] shoreDepth = (float[])depth.Clone(); // UC-T07-11: keep physical sign crossings before the skirt clamps dry depth and moves geometry.
            ShorelineConformer.ConformGrid(surf, depth, n,
                                          ShorelineConformer.SkirtDropMetres,
                                          ShorelineConformer.MaxSkirtDepthMetres);

            // A level body's flow is the drift the user asked for. Manning would give
            // zero here and be right to - a level surface has no slope - so the only
            // honest source of movement is the wind current.
            Vector2 flowWorld = currentMetresPerSecond;
            float speed = flowWorld.magnitude;
            Vector2 flowDir = speed > 1e-5f ? flowWorld / speed : Vector2.zero;

            WaterBodyData built = Assemble(gx, gz, cell, centreX, centreZ, halfX, halfZ,
                                           surf, depth, wet, flowDir, speed, 0f);
            if (built != null) built.shorelineSegmentsLocal = WaterShorelineBuilder.Grid(shoreDepth, wet, gx, gz,
                new Vector2(centreX - halfX, centreZ - halfZ), cell, surfaceY, transform.localToWorldMatrix); // UC-T07-11: retained wet connectivity and actual bed crossings define source shores, not expanded skirt edges.

            // THE BODY'S FLOOR IS THE WATER, NOT THE SKIRT  (T18)
            //
            // ConformGrid bounds the vertex depth channel at
            // ShorelineConformer.MaxSkirtDepthMetres = 100 m. That is the right bound
            // for SKIRT GEOMETRY - past it the mesh has stopped describing anything -
            // and the wrong one for how deep the body is. Assemble measures its
            // deepest point from that same capped channel, UCWaterVolume takes its
            // floor from that, and SampleLocal then answers DRY for every point more
            // than 100 m down. On an ocean with a seabed below that line the dive
            // switched off at exactly 100 m: the camera on the sea floor was reported
            // out of the water and the world came back in air.
            //
            // Water deeper than the skirt is still water, so the uncapped reading
            // wins here. The mesh keeps the capped channel it needs.
            if (built != null && deepestRaw > built.maxDepth) built.maxDepth = deepestRaw;

            return built;
        }

        /// <summary>
        /// Turns a wet/depth lattice into the vertex streams UC_Water reads: COLOR.rg
        /// is flow direction packed around 0.5, COLOR.b is speed over maxFlowSpeed,
        /// COLOR.a is the shoreline weight, UV is (whitewater, depth). This is the only
        /// place that contract is produced, so the shader has one author.
        /// </summary>
        private WaterBodyData Assemble(int gx, int gz, float cell,
                                       float centreX, float centreZ, float halfX, float halfZ,
                                       float[] surf, float[] depth, bool[] wet,
                                       Vector2 flowDir, float flowSpeed, float whitewater)
        {
            int[] index = new int[gx * gz];
            for (int i = 0; i < index.Length; i++) index[i] = -1;

            // ONE BASIN, ONE SURFACE.
            //
            // A lake or a pool is a single body of standing water. Anything in its
            // footprint that reads wet but does not join it is not part of it, and
            // rendering it paints a scrap of water shader on dry land beside the real
            // lake - the reported "small box shader below the nodes". Reduced to the
            // one region the body actually is, before a single quad is counted.
            //
            // Rivers are exempt: a course sampled across a riffle can read one dry
            // cell mid-channel, and pruning to the largest region there would delete
            // half the river to remove nothing. The ocean is exempt because a
            // world-sized sea legitimately floods several basins its own coastline
            // separates, and they are all the same ocean.
            if (shape != ShapeMode.RiverSpline && kind != WaterBodyKind.Ocean)
                KeepLargestBody(wet, gx, gz);

            // THE SEA REACHES ONLY WHAT THE SEA CAN REACH  (R11).
            //
            // An ocean is a world-sized flood plane culled by a per-vertex depth test:
            // any point whose ground is below the sea surface reads wet. That floods a
            // real coastline correctly - several bays are all one sea - but it ALSO
            // floods a landlocked hollow that merely happens to sit below sea level, so a
            // basin the sea has no path into still shows ocean water with no shore. Real
            // water cannot teleport into a closed bowl.
            //
            // The open sea is the water touching the world edge (the ocean plane spans
            // the whole world, so its own boundary is the coastline out to open water).
            // Keep every wet cell the sea can walk to from that edge across other wet
            // cells, and drop the rest. A hollow with no wet path to the edge is left dry
            // here - to be filled by a LAKE instead, which is the correct body for water
            // with no connection to the sea. Ocean only, and only when a real edge is wet,
            // so a fully-enclosed inland sea the user placed on purpose is untouched.
            if (kind == WaterBodyKind.Ocean)
                KeepSeaConnected(wet, gx, gz);

            // A quad joins if any corner is wet, which is what makes the mesh reach
            // one cell past the waterline and gives the alpha fade room to work.
            int quads = 0;
            for (int z = 0; z < gz - 1; z++)
            {
                for (int x = 0; x < gx - 1; x++)
                {
                    int i0 = z * gx + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + gx;
                    int i3 = i2 + 1;

                    if (!wet[i0] && !wet[i1] && !wet[i2] && !wet[i3]) continue;

                    index[i0] = 0; index[i1] = 0; index[i2] = 0; index[i3] = 0;
                    quads++;
                }
            }

            if (quads == 0) return null;

            int vertexCount = 0;
            for (int i = 0; i < index.Length; i++)
                if (index[i] == 0) index[i] = vertexCount++;

            if (vertexCount < 4) return null;

            WaterBodyData body = new WaterBodyData();
            body.vertices = new Vector3[vertexCount];
            body.normals = new Vector3[vertexCount];
            body.tangents = new Vector4[vertexCount];
            body.uv = new Vector2[vertexCount];
            body.colors = new Color[vertexCount];
            body.triangles = new int[quads * 6];

            float feather = Mathf.Max(0.01f, edgeFeatherMetres);
            float invMax = 1f / Mathf.Max(0.01f, maxFlowSpeed);
            float normalisedSpeed = Mathf.Clamp01(flowSpeed * invMax);

            // Flow direction is authored in world XZ, but the vertex stream is read in
            // world space by the shader too, so it goes in unrotated.
            Color colour = new Color(flowDir.x * 0.5f + 0.5f, flowDir.y * 0.5f + 0.5f,
                                     normalisedSpeed, 1f);

            float deepest = 0f;
            float surfSum = 0f;
            int wetCount = 0;

            Bounds worldBounds = new Bounds();
            bool boundsStarted = false;

            for (int z = 0; z < gz; z++)
            {
                float lz = centreZ - halfZ + z * cell;

                for (int x = 0; x < gx; x++)
                {
                    int i = z * gx + x;
                    int v = index[i];
                    if (v < 0) continue;

                    float lx = centreX - halfX + x * cell;

                    // Local, because the mesh is parented to this transform. The
                    // surface height is world Y, so it has to come back through the
                    // inverse to land in the same space as the footprint.
                    Vector3 world = transform.TransformPoint(new Vector3(lx, 0f, lz));
                    world.y = surf[i];

                    body.vertices[v] = transform.InverseTransformPoint(world);
                    body.normals[v] = transform.InverseTransformDirection(Vector3.up).normalized;

                    Vector3 t = transform.InverseTransformDirection(Vector3.right);
                    if (t.sqrMagnitude < 1e-6f) t = Vector3.right;
                    t.Normalize();
                    body.tangents[v] = new Vector4(t.x, t.y, t.z, -1f);

                    float d = Mathf.Max(0f, depth[i]);
                    body.uv[v] = new Vector2(whitewater, d);

                    Color c = colour;
                    c.a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / feather));
                    body.colors[v] = c;

                    if (!boundsStarted) { worldBounds = new Bounds(world, Vector3.zero); boundsStarted = true; }
                    else worldBounds.Encapsulate(world);

                    if (!wet[i]) continue;

                    surfSum += surf[i];
                    wetCount++;
                    if (d > deepest) deepest = d;
                }
            }

            int idx = 0;
            for (int z = 0; z < gz - 1; z++)
            {
                for (int x = 0; x < gx - 1; x++)
                {
                    int i0 = z * gx + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + gx;
                    int i3 = i2 + 1;

                    if (!wet[i0] && !wet[i1] && !wet[i2] && !wet[i3]) continue;

                    // Upward-facing winding for an XZ lattice: row-major index, so the
                    // +Z neighbour comes second. Reversing either triangle here makes
                    // the surface invisible from above and visible from below, which
                    // reads as "the water did not build" rather than as a winding bug.
                    body.triangles[idx++] = index[i0];
                    body.triangles[idx++] = index[i2];
                    body.triangles[idx++] = index[i1];

                    body.triangles[idx++] = index[i1];
                    body.triangles[idx++] = index[i2];
                    body.triangles[idx++] = index[i3];
                }
            }

            body.surfaceLevel = wetCount > 0 ? surfSum / wetCount : transform.position.y + surfaceOffsetMetres;
            body.maxDepth = Mathf.Max(0.1f, deepest);
            body.meanFlow = flowDir * flowSpeed;

            // The SURFACE extent, and only that. Reaching it down into a volume is
            // UCWaterVolume.ExtendDown's job and is applied once, where the volume is
            // attached. Doing it here as well - which this used to - reached down by
            // twice the depth, and the queries then answered for a body twice as deep
            // as the one that was built.
            body.worldBounds = worldBounds;

            return body;
        }

        /// <summary>
        /// Reduces a wet lattice to its single largest connected region, clearing the
        /// rest.
        /// </summary>
        /// <remarks>
        /// Why it is needed at all: Assemble emits a quad whenever ANY of its four
        /// corners is wet, which is what lets the mesh reach one cell past the
        /// waterline for the shore fade. The same rule means ONE stray wet vertex
        /// paints the four quads around it - a two-cell square of water shader
        /// standing on dry ground. A node dragged below the surface, a dip in the bed
        /// under the shore ring, or near-level ground crossing the waterline all
        /// produce such strays, and each arrives as its own little box of water
        /// beside the real lake.
        ///
        /// Eight-connected, because a quad binds its diagonal corners as well as its
        /// orthogonal ones: two diagonally adjacent wet vertices already share a quad
        /// and are therefore one surface, so counting them as two regions would let
        /// this delete water that is genuinely attached.
        ///
        /// Iterative with an explicit stack. A lattice is up to 512x512, and a
        /// recursive fill over a quarter of a million connected cells overflows the
        /// stack rather than returning an answer.
        /// </remarks>
        private static void KeepLargestBody(bool[] wet, int gx, int gz)
        {
            int n = gx * gz;
            if (n <= 0) return;

            // 0 means unvisited; regions are numbered from 1.
            int[] region = new int[n];
            int[] stack = new int[n];

            int largest = 0;
            int largestSize = 0;
            int current = 0;

            for (int seed = 0; seed < n; seed++)
            {
                if (!wet[seed] || region[seed] != 0) continue;

                current++;
                int size = 0;
                int top = 0;

                // Labelled as it is pushed, never as it is popped, so no cell can be
                // pushed twice and the stack cannot exceed the lattice.
                region[seed] = current;
                stack[top++] = seed;

                while (top > 0)
                {
                    int i = stack[--top];
                    size++;

                    int x = i % gx;
                    int z = i / gx;

                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nz = z + dz;
                        if (nz < 0 || nz >= gz) continue;

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= gx) continue;

                            int ni = nz * gx + nx;
                            if (!wet[ni] || region[ni] != 0) continue;

                            region[ni] = current;
                            stack[top++] = ni;
                        }
                    }
                }

                if (size > largestSize) { largestSize = size; largest = current; }
            }

            // Nothing was wet. Assemble's own quad count then returns null, which is
            // the honest "this body holds no water", so there is nothing to prune.
            if (largest == 0) return;

            for (int i = 0; i < n; i++)
                if (wet[i] && region[i] != largest) wet[i] = false;
        }

        /// <summary>
        /// Keeps only the wet cells the open sea can reach, and clears the rest. The open
        /// sea is every wet cell on the lattice boundary; the flood spreads inward across
        /// wet neighbours from all of them at once. A below-sea-level hollow with no wet
        /// path to the edge is dropped, so the ocean shader never appears in a basin the
        /// sea cannot flow into (R11). Ocean-only: called after the depth cull has set
        /// <paramref name="wet"/>.
        ///
        /// If NO boundary cell is wet - a sea whose footprint sits entirely inland, which
        /// is a deliberately placed enclosed body - nothing is connected and the mask is
        /// left exactly as it was rather than emptied, so the placed body is not erased.
        /// </summary>
        private static void KeepSeaConnected(bool[] wet, int gx, int gz)
        {
            int n = gx * gz;
            if (n <= 0) return;

            bool[] reached = new bool[n];
            int[] stack = new int[n];
            int top = 0;

            // Seed from every wet boundary cell - the whole edge is open water, because
            // the ocean plane is sized to span the world.
            for (int x = 0; x < gx; x++)
            {
                int bottom = x;                 // z = 0
                int topRow = (gz - 1) * gx + x;  // z = gz-1
                if (wet[bottom] && !reached[bottom]) { reached[bottom] = true; stack[top++] = bottom; }
                if (wet[topRow] && !reached[topRow]) { reached[topRow] = true; stack[top++] = topRow; }
            }
            for (int z = 0; z < gz; z++)
            {
                int left = z * gx;              // x = 0
                int right = z * gx + (gx - 1);  // x = gx-1
                if (wet[left] && !reached[left]) { reached[left] = true; stack[top++] = left; }
                if (wet[right] && !reached[right]) { reached[right] = true; stack[top++] = right; }
            }

            // No open-water edge at all: an enclosed placed sea. Leave it be.
            if (top == 0) return;

            while (top > 0)
            {
                int i = stack[--top];
                int x = i % gx;
                int z = i / gx;

                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = z + dz;
                    if (nz < 0 || nz >= gz) continue;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= gx) continue;

                        int ni = nz * gx + nx;
                        if (!wet[ni] || reached[ni]) continue;

                        reached[ni] = true;
                        stack[top++] = ni;
                    }
                }
            }

            for (int i = 0; i < n; i++)
                if (wet[i] && !reached[i]) wet[i] = false;
        }

        // ---------------------------------------------------------------------
        //  River  (implementation removed in the river teardown; the mesh/carve
        //  builder is gone. WaterBodyKind.River / ShapeMode.RiverSpline remain as
        //  inert taxonomy for the weather/ice/wave/ecology systems.)
        // ---------------------------------------------------------------------

        // =====================================================================
        //  SCENE PLUMBING
        // =====================================================================

        public static float SampleGround(Terrain[] terrains, Vector3 world, float fallback)
        {
            if (terrains == null) return fallback;

            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null || t.terrainData == null) continue;

                Vector3 origin = t.transform.position;
                Vector3 size = t.terrainData.size;

                if (world.x < origin.x || world.x > origin.x + size.x) continue;
                if (world.z < origin.z || world.z > origin.z + size.z) continue;

                float u = (world.x - origin.x) / Mathf.Max(0.0001f, size.x);
                float v = (world.z - origin.z) / Mathf.Max(0.0001f, size.z);

                // GetInterpolatedHeight rather than SampleHeight: SampleHeight walks
                // the whole active terrain list itself, and doing that inside a loop
                // that already found the terrain is the same work twice per vertex.
                return origin.y + t.terrainData.GetInterpolatedHeight(u, v);
            }

            return fallback;
        }

        private Material ResolveMaterial(WaterBodyData body)
        {
            if (materialOverride != null) return materialOverride;

            // body.product is null exactly when "Use Own Material" is off, and off means
            // "look like default water": one material serves every body that says it, so
            // twenty ponds cost one draw call instead of twenty. The trade is the two
            // per-body flow numbers at the bottom of this method - they cannot be written
            // onto a material other bodies share, which is what ticking it buys.
            if (body.product == null)
            {
                Material shared = UCWaterMaterials.SharedDefault();
                if (shared != null) return shared;
            }

            // Shared resolver: UC_Water, or a pipeline-correct URP fallback if it was
            // stripped/failed to compile, so this never renders the white default
            // material (spec 4.4). The missing-shader warning is logged once there.
            Shader shader = UCWaterMaterials.ResolveWaterShader();
            if (shader == null) return null;

            material = new Material(shader);
            material.name = "UC Water - " + name;
            material.hideFlags = HideFlags.DontSave;

            UCWaterMaterials.ApplyProductRuntime(material, water);

            // COLOR.b is normalised, so the shader has to be told what 1 means, or the
            // surface scrolls at whatever the material was authored with instead of at
            // the metres per second the hydraulics computed.
            material.SetFloat(UCWaterProps.FlowStrength, body.maxFlowSpeed);

            // An ocean's motion is swell, not translation, so it reads the material's
            // uniform flow. Everything else follows its own vertex stream.
            material.SetFloat(UCWaterProps.UseVertexFlow, kind == WaterBodyKind.Ocean ? 0f : 1f);

            // Spectral richness by body kind, keyed off the kind like the flow above and
            // mirrored into the physics surface in AttachVolume so a boat sits on the same
            // swell it is drawn on. An open ocean carries a broad set of wave trains, a
            // lake a simpler pair, a decorative pool barely a ripple. A river advects that
            // swell downstream (the only kind that does); still water keeps _WaveFlowAdvect
            // at 0 so it stays a line-for-line match for the CPU buoyancy mirror.
            material.SetFloat(UCWaterProps.WaveOctaves, WaveOctavesFor(kind));
            material.SetFloat(UCWaterProps.WaveFlowAdvect, kind == WaterBodyKind.River ? 1f : 0f);

            // Calm to begin with; the weather layer raises it in a storm (UCWaterWeatherCoupling).
            material.SetFloat(UCWaterProps.WaveStormAmp, 1f);

            return material;
        }

        /// <summary>
        /// How many ambient wave trains a body of this kind runs, 1..4. An open ocean
        /// carries the broadest swell spectrum, a lake a calmer pair, a pool a single
        /// ripple; a river reads like a moving lake. Feeds both the material
        /// (_WaveOctaves) and the physics surface (WaterSurfaceParams.octaves), which
        /// must agree - see UC_WaterInput.hlsl / UCWaterSurface.WaveOffset.
        ///
        /// Public and static so the quality-tier driver (W8) can recompute a body's
        /// AUTHORED octave count straight from its kind and cap it per tier, without
        /// reading the possibly-already-capped live value - that is what lets a lowered
        /// tier be raised again and restore the full spectrum.
        /// </summary>
        public static float WaveOctavesFor(WaterBodyKind k)
        {
            switch (k)
            {
                case WaterBodyKind.Ocean: return 4f;
                case WaterBodyKind.Lake:  return 2f;
                case WaterBodyKind.River: return 2f;
                default:                  return 1f;   // Pool and anything decorative
            }
        }

        // Builds the ocean Far Distance Mesh (§4i.3): a flat, color-matched ring that
        // extends the sea from the body outline out by horizonDistanceMetres, sitting a
        // hair below the surface so the body's own swell wins at the seam. Two loops of
        // the outline (so a rectangle ocean is 8 verts), wave-free, ocean only. The water
        // shader is Cull Off, so winding is immaterial here; normals point up for lighting.
        private void BuildOceanHorizon(WaterBodyData body)
        {
            if (kind != WaterBodyKind.Ocean) return;
            if (horizonDistanceMetres <= 0.01f) return;
            if (surfaceObject == null || body == null) return;

            MeshRenderer bodyRenderer = surfaceObject.GetComponent<MeshRenderer>();
            Material mat = bodyRenderer != null ? bodyRenderer.sharedMaterial : null;
            if (mat == null) return;   // nothing to color-match to; skip rather than guess

            List<Vector3> ring = WorldShoreRing();
            if (ring == null || ring.Count < 3) return;

            int n = ring.Count;

            // Centre in world XZ so the outward push is radial from the body middle.
            float cx = 0f, cz = 0f;
            for (int i = 0; i < n; i++) { cx += ring[i].x; cz += ring[i].z; }
            cx /= n; cz /= n;

            // A hair below the surface: the body mesh (which rides the swell) stays on top
            // at the seam, so there is no z-fight and no visible lip between the two.
            float y = body.surfaceLevel - 0.05f;

            Vector3[] verts = new Vector3[n * 2];
            Vector3[] norms = new Vector3[n * 2];
            Vector4[] tans  = new Vector4[n * 2];
            Vector2[] uvs   = new Vector2[n * 2];
            Color[]   cols  = new Color[n * 2];

            // R=G=0.5, A=0: UCDecodeWaterVertex sees non-zero data (so it does NOT fall back
            // to "fully wet") and reads weight w=0, which zeroes BOTH the Gerstner vertical
            // and the new horizontal term -> a dead-flat, color-matched horizon at no wave
            // cost. uv.y large so the depth-tinted colour reads as open deep sea.
            Color flat = new Color(0.5f, 0.5f, 0f, 0f);

            Vector3 upL = transform.InverseTransformDirection(Vector3.up).normalized;
            Vector3 rtL = transform.InverseTransformDirection(Vector3.right);
            if (rtL.sqrMagnitude < 1e-6f) rtL = Vector3.right;
            rtL.Normalize();
            Vector4 tan = new Vector4(rtL.x, rtL.y, rtL.z, -1f);

            // WORLD-EDGE DERIVED REACH (author report 2026-10-06: "the horizon is at 8k by 8k
            // and the world is 4k by 4k - it should be at the end of the world", then: "do not
            // hard code it ... what if I have a 40k by 40k or 1k by 1k world").
            //
            // The ring's job is that the sea never visibly ends. The physical scale of that job
            // is the geometric horizon, d = sqrt(2*R*h + h*h) with R = 6 371 000 m (Earth mean
            // radius) - ~4.7 km at a 1.7 m eye height, ~8 km at 5 m, ~35 km from a 100 m hill.
            // That equation is why the authored default is thousands of metres rather than tens.
            //
            // But a FINITE world ends sooner than the planet does, and no single constant fits
            // every world: 8 km dwarfs a 1 km world and falls short inside a 40 km one. So the
            // reach is MEASURED from the live terrain bounds instead of authored - the ring
            // stops exactly at the world edge whatever that edge is. The authored metres remain
            // only as the no-terrain fallback and the 0 = off switch.
            bool hasWorld = false;
            float wMinX = 0f, wMaxX = 0f, wMinZ = 0f, wMaxZ = 0f;
            Terrain[] worldTerrains = Terrain.activeTerrains;
            if (worldTerrains != null)
            {
                for (int i = 0; i < worldTerrains.Length; i++)
                {
                    Terrain tr = worldTerrains[i];
                    if (tr == null || tr.terrainData == null) continue;

                    Vector3 tp = tr.transform.position;
                    Vector3 ts = tr.terrainData.size;

                    if (!hasWorld)
                    {
                        wMinX = tp.x; wMaxX = tp.x + ts.x;
                        wMinZ = tp.z; wMaxZ = tp.z + ts.z;
                        hasWorld = true;
                    }
                    else
                    {
                        if (tp.x < wMinX) wMinX = tp.x;
                        if (tp.z < wMinZ) wMinZ = tp.z;
                        if (tp.x + ts.x > wMaxX) wMaxX = tp.x + ts.x;
                        if (tp.z + ts.z > wMaxZ) wMaxZ = tp.z + ts.z;
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                Vector3 p = ring[i];
                float dx = p.x - cx, dz = p.z - cz;
                float len = Mathf.Sqrt(dx * dx + dz * dz);
                float ux = len > 1e-4f ? dx / len : 0f;
                float uz = len > 1e-4f ? dz / len : 0f;

                // How far this vertex may travel before it leaves the world box: the 2D
                // ray-box exit distance along (ux, uz). A vertex already outside the box, or
                // pointing along a zero axis, yields no travel on that axis.
                //
                // When the world is measurable this IS the reach - not a minimum against the
                // authored metres. A fixed number cannot serve a 1 km world and a 40 km world
                // at once: on the small one it overshoots into empty space, on the large one it
                // stops short and the sea visibly ends mid-world. Deriving it from the terrain
                // bounds makes the ring end at the world edge at EVERY world size, which is
                // what the author asked for. horizonDistanceMetres survives only as the
                // fallback for when there is no terrain to measure (and as the on/off switch).
                float reach = horizonDistanceMetres;
                if (hasWorld)
                {
                    float tx = ux > 1e-6f  ? (wMaxX - p.x) / ux
                             : ux < -1e-6f ? (wMinX - p.x) / ux
                                           : float.MaxValue;
                    float tz = uz > 1e-6f  ? (wMaxZ - p.z) / uz
                             : uz < -1e-6f ? (wMinZ - p.z) / uz
                                           : float.MaxValue;

                    float exit = tx < tz ? tx : tz;
                    if (exit < 0f) exit = 0f;
                    if (exit < float.MaxValue) reach = exit;
                }

                Vector3 innerW = new Vector3(p.x, y, p.z);
                Vector3 outerW = new Vector3(p.x + ux * reach, y, p.z + uz * reach);

                verts[i]     = transform.InverseTransformPoint(innerW);
                verts[i + n] = transform.InverseTransformPoint(outerW);
                norms[i] = upL;             norms[i + n] = upL;
                tans[i]  = tan;             tans[i + n]  = tan;
                uvs[i]   = new Vector2(0f, 50f); uvs[i + n] = new Vector2(0f, 50f);
                cols[i]  = flat;            cols[i + n]  = flat;
            }
            // HORIZON_BUILD_TAIL
            int[] tris = new int[n * 6];
            int t = 0;
            for (int i = 0; i < n; i++)
            {
                int a  = i;
                int b  = (i + 1) % n;
                int ao = i + n;
                int bo = ((i + 1) % n) + n;
                tris[t++] = a;  tris[t++] = b;  tris[t++] = bo;
                tris[t++] = a;  tris[t++] = bo; tris[t++] = ao;
            }

            horizonMesh = new Mesh();
            horizonMesh.name = "UC Water Horizon";
            horizonMesh.hideFlags = HideFlags.DontSave;
            horizonMesh.vertices = verts;
            horizonMesh.normals  = norms;
            horizonMesh.tangents = tans;
            horizonMesh.uv       = uvs;
            horizonMesh.colors   = cols;
            horizonMesh.triangles = tris;

            Bounds hb = new Bounds(verts[0], Vector3.zero);
            for (int i = 1; i < verts.Length; i++) hb.Encapsulate(verts[i]);
            hb.Expand(new Vector3(0f, 1f, 0f));
            horizonMesh.bounds = hb;

            horizonObject = new GameObject("UC Water Horizon");
            horizonObject.layer = gameObject.layer;
            horizonObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable; // UC-T04-05B: an ocean refresh must preserve the same global Redo branch.
            horizonObject.transform.SetParent(transform, false);
            horizonObject.transform.localPosition = Vector3.zero;
            horizonObject.transform.localRotation = Quaternion.identity;
            horizonObject.transform.localScale = Vector3.one;

            MeshFilter hf = horizonObject.AddComponent<MeshFilter>();
            hf.sharedMesh = horizonMesh;

            MeshRenderer hr = horizonObject.AddComponent<MeshRenderer>();
            readinessHorizonRenderer = hr; // UC-T09-03: the far mesh follows the same accepted-water presentation hold.
            hr.sharedMaterial = mat;   // the body's own material -> SRP-batched, color-matched
            hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            hr.receiveShadows = false;
            hr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            hr.allowOcclusionWhenDynamic = false;
        }

        private void AttachVolume(WaterBodyData body, WaterQuerySurface querySurface)
        {
            volume = surfaceObject.AddComponent<UCWaterVolume>();
            volume.WorldOwner = CarveWorld; // UC-T04-09: inactive/native publications retain their actual world scope too.

            volume.kind = (UCWaterVolume.BodyKind)(int)kind;
            volume.UseAcceptedSurface(querySurface); // UC-T06-11: freeze/notify accepted water only after its owning world and category are assigned.
            volume.surfaceLevel = body.surfaceLevel;
            volume.maxDepth = Mathf.Max(0.1f, body.maxDepth);

            // The built bounds describe the SURFACE, which is a sheet. Reaching them
            // down by the body's own depth is what makes them a volume, and it is the
            // same rule the renderer-driven fit uses - one implementation, so the box
            // the gizmo draws is the box the queries answer from.
            volume.worldBounds = UCWaterVolume.ExtendDown(body.worldBounds, volume.maxDepth);

            volume.flow = body.meanFlow;
            volume.flowForce = flowForce;
            volume.viscosityScale = viscosityScale;
            volume.emitRipples = emitRipples;

            // One description drives both the look and the physics. Reading the wave
            // from anywhere else is how a boat ends up floating a hand's width above
            // the visible surface.
            volume.surface = WaterSurfaceParams.Default(body.surfaceLevel);
            volume.surface.waveHeight = water.waveHeightMetres;
            volume.surface.waveLength = water.waveLengthMetres;
            volume.surface.waveSpeed = water.waveSpeed;
            // Mirror the material's per-kind octave count and calm-start storm scale, so
            // buoyancy reads the exact swell the shader draws (see ConfigureMaterial).
            volume.surface.octaves = WaveOctavesFor(kind);
            volume.surface.stormAmp = 1f;
            volume.surface.flow = body.meanFlow != Vector2.zero
                ? body.meanFlow
                : water.flowMetresPerSecond;
            volume.surface.flowAdvection = kind == WaterBodyKind.River ? 1 : 0; // UC-T04-09: mirror material phase transport.
            volume.surface.horizontalDisplacement = true;

            // Salt water is about 2.5% denser than fresh, and that is enough to change
            // what floats: it is why a boat sits measurably higher at sea than in a
            // river, and why swimming in the sea is easier.
            volume.density = densityOverride > 0.01f
                ? densityOverride
                : (kind == WaterBodyKind.Ocean ? 1025f : 998f);

            volume.underwaterView = underwaterView;
            volume.underwaterColor = underwaterColor;
            volume.visibilityMetres = Mathf.Max(0.5f, visibilityMetres);
            volume.underwaterBrightness = underwaterBrightness;

            // A river bends, so its flow does too. Hand the volume the solved centreline
            // and the water surface along it; UCWaterVolume.ReachAt then answers a query
            // with the flow pointing the way the water actually goes at the nearest point
            // on the course, instead of the single head->mouth vector set above. The speed
            // stays the body's authored hydraulic value - the reach only decides direction.
            // Opt-in and non-breaking: a null/short course leaves surfaceProfile null and
            // the volume answers as still water exactly as before, so lake/ocean are
            // untouched (their carve has no river course). This is what makes the per-reach
            // flow live - SetReachProfile had no caller until here.
            if (kind == WaterBodyKind.River && riverSurfaceFit != null && riverSurfaceFit.isRiver &&
                riverSurfaceFit.courseWorld != null && riverSurfaceFit.courseWorld.Count >= 2) // UC-T04-04: queries consume the accepted surface, not excavation dimensions.
            {
                List<Vector3> pts = riverSurfaceFit.courseWorld;
                float[] surfY = new float[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    float wl, bed, hw, lat;
                    // The surface at the centreline is the channel water level there; if the
                    // sample falls outside the carved band (shouldn't on the centreline) keep
                    // the course point's own elevation rather than inventing one.
                    surfY[i] = riverSurfaceFit.TryRiverSurfaceAt(pts[i].x, pts[i].z, out wl, out bed, out hw, out lat)
                        ? wl
                        : pts[i].y;
                }
                volume.SetReachProfile(pts, surfY);
            }
        }

        // =====================================================================
        //  AUTHORING HELPERS
        // =====================================================================

        /// <summary>Seeds a closed outline as a regular polygon inscribed in the
        /// current footprint, so Polygon mode starts from something rather than from
        /// an empty list.</summary>
        public void SeedOutline(int sides)
        {
            sides = Mathf.Clamp(sides, 3, 64);

            outline = new Polyline(sides);
            outline.closed = true;

            float rx = sizeMetres.x * 0.5f;
            float rz = sizeMetres.y * 0.5f;

            for (int i = 0; i < sides; i++)
            {
                float a = (i / (float)sides) * Mathf.PI * 2f;
                outline.Add(new Vector3(Mathf.Cos(a) * rx, 0f, Mathf.Sin(a) * rz));
            }
        }

        /// <summary>
        /// Drops every course point onto the terrain under it. The Y of a course
        /// point is ignored by the builder - the surface comes from the ground - but
        /// a course sitting at Y 0 while the valley is at 300 is impossible to edit
        /// in the scene view.
        /// </summary>
        public void SnapCourseToGround()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            SnapToGround(outline, terrains);
        }

        private void SnapToGround(Polyline line, Terrain[] terrains)
        {
            if (line == null) return;

            for (int i = 0; i < line.Count; i++)
            {
                Vector3 world = transform.TransformPoint(line.points[i]);
                world.y = SampleGround(terrains, world, world.y);
                line.points[i] = transform.InverseTransformPoint(world);
            }
        }

        // =====================================================================
        //  NO OnDrawGizmosSelected HERE, ON PURPOSE.
        //
        //  Unity calls OnDrawGizmosSelected on the selected object AND on every
        //  child of it. A water body lives under the world root, so selecting
        //  the world - the ordinary thing to have selected while building a
        //  world - made every lake and river paint its outline across the
        //  terrain in blue. Worse, gizmos redraw on every scene-view repaint and
        //  the editor repaints as fast as the mouse moves, so the lines appeared
        //  to be continuously re-generating: it read as the terrain still
        //  computing when nothing was.
        //
        //  The course and outline are edit-time authoring aids, so they belong to
        //  the inspector's OnSceneGUI, which Unity only runs for the body that is
        //  actually being inspected. Same lines, drawn when they are wanted, and
        //  the world root is quiet again.
        // =====================================================================
    }
}
