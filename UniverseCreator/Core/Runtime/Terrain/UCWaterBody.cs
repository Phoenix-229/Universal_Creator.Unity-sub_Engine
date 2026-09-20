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

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// A hand-placed body of water. Add it, choose a shape, press Build.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Water Body")]
    [DisallowMultipleComponent]
    public class UCWaterBody : MonoBehaviour, IUCTerrainCarver
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
        //  RIVER
        // =====================================================================

        [Header("River (Spline)")]

        [Tooltip("Centre line in local metres, upstream first. The order is the " +
                 "flow direction when Enforce Downhill is off.")]
        public Polyline course = new Polyline();

        [Tooltip("Channel width in metres. Per-point weights on the spline scale " +
                 "this, so a river can widen as it goes.")]
        public float widthMetres = 12f;

        [Tooltip("Vertices across the channel. 4 is enough for a stream, 8 for a " +
                 "wide river whose banks need a smooth curve.")]
        [Range(2, 24)]
        public int widthSegments = 6;

        [Tooltip("Distance between cross-sections along the course, in metres. " +
                 "Smaller follows a tight meander better and costs vertices.")]
        public float pointSpacingMetres = 6f;

        [Tooltip("How deep the channel is under the water surface, at midstream. " +
                 "Drives the hydraulic radius Manning's equation uses, the depth " +
                 "colour ramp, and how far down the physics volume reaches.")]
        public float channelDepthMetres = 1.5f;

        [Tooltip("Bank profile exponent. 2 is a parabolic natural channel, 4 is a " +
                 "flat bed with steep banks, 1 is a shallow V.")]
        [Range(1f, 6f)]
        public float bankProfile = 4f;

        [Tooltip("Forces the water surface never to rise as it goes downstream. " +
                 "Water cannot run uphill; with this off, a course drawn across a " +
                 "rise produces a river that climbs it.")]
        public bool enforceDownhill = true;

        [Tooltip("Smoothing passes over the surface profile. A raw terrain sample " +
                 "gives the water a stepped surface that reads as a staircase of " +
                 "tiny waterfalls; a real river surface is smooth even where its bed " +
                 "is not.")]
        [Range(0, 12)]
        public int profileSmoothing = 4;

        // ---- Per-node depth and width ---------------------------------------
        //
        //  A river does not have one depth. It starts as a shallow scrape, gathers
        //  water, and arrives at its mouth deep and wide. These two lists run
        //  alongside the course points and say what the channel is doing at each
        //  one; anything left at 0 is not authored and inherits the value carried
        //  down from the last node that was.

        [Tooltip("Depth at each course node, in metres. One entry per spline point.\n\n" +
                 "0 means 'not set here' - that node inherits the depth carried down " +
                 "from the last node above it that was set, and if none was, from " +
                 "Channel Depth. So you only type numbers where the river actually " +
                 "changes, and the reach between two set nodes eases from one to the " +
                 "other rather than stepping.")]
        public List<float> nodeDepthMetres = new List<float>();

        [Tooltip("Width at each course node, in metres. Same rule as the depths - 0 " +
                 "inherits from upstream, and the first unset run takes Width.")]
        public List<float> nodeWidthMetres = new List<float>();

        // ---- Junctions ------------------------------------------------------

        [Tooltip("The body this river runs into: an ocean, a lake, or another river " +
                 "it joins as a tributary. Pools are refused - a river does not empty " +
                 "into a swimming pool.\n\n" +
                 "The mouth is pulled onto the target's edge and held at the target's " +
                 "surface level, so the two water planes meet with no step and the bed " +
                 "runs on into the basin instead of stopping at a wall.")]
        public UCWaterBody joinDownstreamTo;

        [Tooltip("The body this river runs out of - a lake outlet, or the trunk river " +
                 "this one branches from. Same treatment at the head as the mouth.")]
        public UCWaterBody joinUpstreamFrom;

        [Tooltip("Distance over which a junction blends, in metres. The channel eases " +
                 "into the body it meets over this run; too short and the join is a " +
                 "step, too long and the river loses its own shape before it arrives.")]
        public float joinBlendMetres = 25f;

        [Tooltip("Deepens the mouth to match what it is joining, so a river arriving " +
                 "at a lake does not sit in a shallow trench above a deep basin. Off " +
                 "keeps the channel's own depth all the way to the join.")]
        public bool joinMatchesDepth = true;

        // =====================================================================
        //  ROUTING  (optional: let the ground move the drawn line sideways)
        //
        //  BOTH OFF BY DEFAULT, AND THAT IS DELIBERATE.
        //
        //  These two move the course in XZ - away from where you drew it. The
        //  elevation work is somewhere else entirely and always runs; what these
        //  change is the PLAN of the river, not its profile.
        //
        //  Left on, the river you see dug is not the river you drew: you place a
        //  node, the valley fit slides it 30 m downslope and the descent walk takes
        //  a detour round a spur, and the trench comes out beside your line looking
        //  like a separate object that happens to start near it. When you are
        //  clicking nodes onto the terrain, YOUR line is the answer - the ground
        //  does not get a vote on where it goes, only on how deep it is cut.
        //
        //  Turn one on when you want the opposite: a rough line sketched across a
        //  landscape you have not studied, which you want fitted to whatever is
        //  actually there. Then the fit happens on a copy at build time, so your
        //  control points still stay where you put them.
        // =====================================================================

        [Tooltip("Pulls each point of the course sideways onto the lowest ground near " +
                 "it, so a line drawn across a valley settles into its floor instead " +
                 "of lying across the slope.\n\n" +
                 "OFF by default: it moves the river off the line you drew, and when " +
                 "you have placed the nodes on the terrain yourself that reads as the " +
                 "dig ignoring your spline. Turn it on for a rough sketch you want " +
                 "snapped to the landscape.\n\n" +
                 "On flat ground there is no lowest point to find, so nothing moves - " +
                 "which is why a river on a plain stays exactly where you drew it and " +
                 "one on a hillside snaps into the trench.")]
        public bool fitToValley;

        [Tooltip("How far to either side the valley seek looks, in metres. About half " +
                 "a valley width. Too large and the river jumps into the next valley " +
                 "over; too small and it never finds the floor of the one it is in.")]
        public float valleySearchMetres = 2000f;

        [Tooltip("How much of the way to the low point each node moves, 0..1. 1 lands " +
                 "the course exactly on the lowest sample and follows every wobble in " +
                 "the heightfield; around 0.85 keeps the line the designer drew " +
                 "recognisable while still sitting in the trench.")]
        [Range(0f, 1f)] public float valleyPull = 0.85f;

        [Tooltip("Forbids the channel from ever climbing. The course is walked from " +
                 "the head one step at a time; where the direct line would go uphill " +
                 "the walk fans out and takes the best descending way round, so the " +
                 "river goes around a spur into the adjacent valley rather than being " +
                 "cut through the top of it.\n\n" +
                 "OFF by default, because the detour it takes is a different path " +
                 "from the one you drew and the trench ends up somewhere you did not " +
                 "put it. With it off the river still never runs uphill - Enforce " +
                 "Downhill above handles that by lowering the water surface along " +
                 "your line instead of moving the line.\n\n" +
                 "Turn it on when the line is a rough sketch and you would rather the " +
                 "river find its own way down than be cut through a ridge.")]
        public bool routeAroundHighGround;

        [Tooltip("How far off the drawn heading the reroute may swing, in degrees. " +
                 "Around 70 lets a river turn along the foot of a ridge; near 0 keeps " +
                 "it on the drawn line and lets it stop instead of detouring.")]
        [Range(0f, 170f)] public float routeFanDegrees = 70f;

        [Tooltip("Rise treated as level, in metres. A heightfield is not smooth at the " +
                 "centimetre scale, and a zero tolerance makes the walk zig-zag around " +
                 "bumps far smaller than the channel it is cutting.")]
        public float climbToleranceMetres = 0.05f;

        [Tooltip("Length of the fade where a river that found no way down disappears, " +
                 "in metres. The channel narrows and shallows over this run so it " +
                 "reads as water sinking underground rather than as a trench that " +
                 "stops against a wall. Real rivers do this in karst and in desert.")]
        public float sinkRunMetres = 30f;

        // ---- Meander -------------------------------------------------------
        //
        //  A river on a plain does not run in a straight line - it wanders in
        //  regular loops (meanders), because the fastest water on the outside of
        //  any small bend erodes that bank (the cut-bank) while the slack water on
        //  the inside drops its load (the point-bar), and the bend grows. The loop
        //  wavelength is not arbitrary: field measurement across rivers of every
        //  size gives a meander wavelength of roughly 10-14 channel widths, which
        //  is the one number this pass is built on. Steep ground is left straight,
        //  because a river in a gorge has no floodplain to wander across - it is
        //  pinned to the valley floor, which is exactly what the reported "sine
        //  wiggle everywhere" got wrong.

        [Tooltip("Let the river wander in natural meander loops on flat ground. " +
                 "Off keeps the course exactly as fitted (straight down the valley). " +
                 "The loops only form where the ground is flat enough to have a " +
                 "floodplain - a river in a gorge stays pinned to the valley floor - " +
                 "so this does not put a wiggle through a canyon.")]
        public bool meander = true;

        [Tooltip("How pronounced the meander loops are, 0..1. This scales the loop " +
                 "amplitude, which is set from the channel width the way real rivers " +
                 "are (wavelength about 11 widths, amplitude about 3 widths). At 0 " +
                 "the river is straight; around 0.6 is an ordinary lowland river; 1 " +
                 "is a strongly looping one near its floodplain base level.")]
        [Range(0f, 1f)] public float meanderStrength = 0.6f;

        [Tooltip("The downstream slope, as metres-fall per metre-run, at which " +
                 "meandering is fully suppressed. Above this grade the reach is steep " +
                 "enough that the river is confined and runs straight; below it the " +
                 "loops fade in. 0.02 (a 2 percent grade) is the usual boundary " +
                 "between a wandering lowland river and a confined upland one.")]
        [Range(0.002f, 0.2f)] public float meanderMaxGrade = 0.02f;

        // ---- Downstream hydraulic geometry (J.4) ----------------------------
        //
        //  A real river is not one width. It leaves its source as a narrow scrape,
        //  and every tributary and hillside that drains into it adds discharge, so
        //  it grows wider and deeper all the way to its mouth (Leopold & Maddock's
        //  downstream hydraulic geometry: width goes as about the square root of
        //  discharge, depth as about its 0.4 power). That growth is a property of
        //  the LAND, not of numbers a designer types - so this makes it automatic.
        //
        //  It also does the cross-section trade the eye reads as "a real river":
        //  the discharge at a reach fixes the flow AREA (width x depth), but how
        //  that area is split between width and depth is set by how much the valley
        //  confines the channel. In a tight gorge the river is forced narrow, so it
        //  runs deep; where the valley opens onto a plain it spreads wide and shallow.
        //  Width and depth therefore move OPPOSITE ways across a constriction while
        //  their product - the discharge - is preserved. That is why a river can get
        //  narrower yet deeper, exactly as asked.

        [Tooltip("Grow the river's width and depth downstream automatically, from the " +
                 "land it drains, instead of only where you type per-node numbers. A " +
                 "river starts narrow at its source and widens to its mouth the way a " +
                 "real one does (Leopold & Maddock). Leave on for procedural, natural " +
                 "rivers; turn off if you want the width to be exactly the numbers you " +
                 "set at each node and nothing else.")]
        public bool autoHydraulicGeometry = true;

        [Tooltip("How much of the source-to-mouth width is present already at the " +
                 "source, 0..1. 0.15 means the headwater is about 15 percent of the " +
                 "mouth width - a narrow brook growing to a wide river. Higher makes " +
                 "the whole river more uniform; lower makes the source a thin trickle.")]
        [Range(0.02f, 1f)] public float sourceWidthFraction = 0.15f;

        [Tooltip("How strongly the valley walls trade width for depth, 0..1. 0 keeps " +
                 "a constant width-to-depth ratio down the whole river (no gorge/plain " +
                 "difference). 1 lets a tight gorge pull the channel to roughly half " +
                 "its open-plain width and correspondingly deeper, at the same flow. " +
                 "The flow area (width x depth) is preserved either way, so this only " +
                 "redistributes the channel, it does not add or remove water.")]
        [Range(0f, 1f)] public float valleyConfinementStrength = 0.6f;

        [Tooltip("On Build, if the mouth of the river comes within this many metres of " +
                 "an ocean or a lake, it joins that body automatically - no need to " +
                 "set the junction by hand. Pools are ignored. 0 switches it off.")]
        public float autoConnectMetres = 2000f;

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

                if (shape == ShapeMode.RiverSpline && valleyWidthMultiple > 0f)
                    return Mathf.Clamp(widthMetres * valleyWidthMultiple,
                                       MinValleyCorridorMetres,
                                       MaxValleyCorridorMetres);

                return 0f;
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

        [Tooltip("How far in from the shore the bed reaches full depth, in metres. " +
                 "Past it the floor is flat. This is what makes a lake read as a lake " +
                 "with a floor rather than a funnel with a point at the bottom.")]
        public float shoreRunMetres = 40f;

        [Tooltip("Fraction of the run from shore to deep taken up by the flat shelf. " +
                 "0.25 is a natural lake margin; an ocean wants 0.5 or more, because " +
                 "a real continental shelf is most of the distance to the deep.")]
        [Range(0.02f, 0.95f)]
        public float shelfFraction = 0.25f;

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

        // REMOVED: horizonExtentMetres, "extends a flat plane this far past the body
        // so the sea reaches the horizon". Nothing in the codebase ever built that
        // plane - the field was written here, copied into WaterBodyData, shown in the
        // inspector and set to 4000 by the ocean preset, and no line of code ever read
        // it to make geometry. An ocean reaching the horizon is what the body's own
        // extent is for: give it the size you want the sea to be.

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
            WaterCarveRegistry.Unregister(this);
            Clear();
        }

        private void OnValidate()
        {
            // Clamped here rather than trusted, because a zero or negative value in
            // any of these divides by zero somewhere in the build and the symptom is
            // a NaN mesh Unity refuses to draw with no message about why.
            sizeMetres.x = Mathf.Max(1f, sizeMetres.x);
            sizeMetres.y = Mathf.Max(1f, sizeMetres.y);
            widthMetres = Mathf.Max(0.2f, widthMetres);
            pointSpacingMetres = Mathf.Max(0.5f, pointSpacingMetres);
            channelDepthMetres = Mathf.Max(0.05f, channelDepthMetres);
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
            joinBlendMetres = Mathf.Max(0f, joinBlendMetres);

            // A river cannot empty into a pool, and nothing can join itself. Cleared
            // here rather than warned about, because a dangling junction would move
            // the mouth to a nonsense place and the user would be looking at the
            // wrong end of the river trying to work out why.
            if (!CanJoin(joinDownstreamTo)) joinDownstreamTo = null;
            if (!CanJoin(joinUpstreamFrom)) joinUpstreamFrom = null;

            TrimNodeSeries(nodeDepthMetres);
            TrimNodeSeries(nodeWidthMetres);

            corridorMetres = Mathf.Max(0f, corridorMetres);
            valleyWidthMultiple = Mathf.Max(0f, valleyWidthMultiple);
            autoConnectMetres = Mathf.Max(0f, autoConnectMetres);

            // Anything typed in the inspector can change where the course lands, so the
            // fitted one is dropped and the next build re-fits. Cheap: it is a null,
            // and the solve only runs when something actually asks for the course.
            InvalidateSolvedCourse();
        }

        /// <summary>
        /// Whether this river may join that body. Oceans, lakes and other rivers yes;
        /// pools no, and itself never.
        /// </summary>
        public bool CanJoin(UCWaterBody other)
        {
            if (other == null) return false;
            if (other == this) return false;

            return other.kind == WaterBodyKind.Ocean
                || other.kind == WaterBodyKind.Lake
                || other.kind == WaterBodyKind.River;
        }

        /// <summary>
        /// Keeps a per-node list the same length as the course. Growing it with zeros
        /// is what makes a newly added spline point mean "inherit" rather than
        /// "suddenly 0 metres deep".
        /// </summary>
        private void TrimNodeSeries(List<float> series)
        {
            if (series == null || course == null) return;

            int want = course.Count;

            while (series.Count > want) series.RemoveAt(series.Count - 1);
            while (series.Count < want) series.Add(0f);
        }

        // =====================================================================
        //  BUILD
        // =====================================================================

        // =====================================================================
        //  THE DIG
        // =====================================================================

        [System.NonSerialized] private WaterCarveLayer carve;

        /// <summary>The world this body may dig, resolved from the hierarchy.</summary>
        public UniverseCreatorObject CarveWorld
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        /// <summary>The dig, or null when this body is not carving.</summary>
        public WaterCarveLayer CarveLayer
        {
            get { return digTerrain && carve != null && carve.HasShape ? carve : null; }
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
        /// </summary>
        public void BuildDig(bool reapplyToTerrain)
        {
            if (carve == null) carve = new WaterCarveLayer();

            // Pressing Build means "fit this to the ground as it is now", so last
            // build's fitted course is dropped rather than reused. This is the half of
            // the answer a river with no graph node connected relies on: the terrain
            // moved, you press Build, the river is re-placed.
            InvalidateSolvedCourse();

            carve.Clear();
            carve.enabled = digTerrain;
            carve.kind = kind;
            carve.seed = ruggednessSeed;

            if (!digTerrain)
            {
                // Nothing is fitted to anything, so the next settle must not read a
                // baseline left over from when this body was still digging.
                fittedGround = null;

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
            carve.corridorMetres = ValleyCorridorMetres;
            carve.corridorStrength = corridorStrength;

            if (shape == ShapeMode.RiverSpline) BuildRiverDig();
            else BuildLevelDig();

            carve.RecomputeBounds();

            // The ground this dig is now fitted to. Read after the course is solved and
            // before anything is re-cut, which is safe in either order: it reads the
            // graph's undug output and a re-cut does not touch that.
            //
            // Captured here rather than in OnTerrainSettled so that every path which
            // changes the dig - the Build button, a field edit, a spline nudge - leaves
            // a baseline behind. A path that changed the dig without recording what it
            // fitted to would make the next settle compare against stale ground.
            fittedGround = SampleFittedGround();

            if (reapplyToTerrain) ReapplyToTerrain();
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
            if (!isActiveAndEnabled || !digTerrain) return false;
            if (onRegenerate != ProceduralResponse.RefitAutomatically) return false;

            // WaitForBuild and HoldPlacement both mean "do not move", and a body with
            // no graph node behind it has no automatic trigger by the user's own rule:
            // no node connected means press Build again.
            if (!IsGraphLinked) return false;

            if (shape != ShapeMode.RiverSpline) return false;

            // There has to be a course before there is ground under it to probe. After
            // a domain reload there is not, and solving here is what gives the compare
            // below something to measure.
            if (!EnsureSolvedCourse()) return false;

            float[] now = SampleFittedGround();

            // NO UNDUG RECORD, NO MOVEMENT.
            //
            // Without one the only ground available is the live terrain, which already
            // carries this river's own channel - and fitting a course to its own channel
            // is how a river sinks a depth deeper on every pass. Declining costs a
            // re-fit that waits until the cell is baked again. Accepting would cost the
            // shape of the river. Same trade, and the same reasoning, as
            // Chunk.bakedThisSession.
            if (now == null) return false;

            // The course is derived from this ground and nothing else, so unchanged
            // ground means the solve would return the answer it already has. Saying no
            // here is what keeps 25 settles from doing 25 identical re-fits.
            if (GroundProbesMatch(fittedGround, now)) return false;

            BuildDig(false);
            return true;
        }

        /// <summary>
        /// The river channel: a course in world space carrying a width, a depth and a
        /// water surface elevation at every point.
        ///
        /// The surface profile is the same one the mesh uses - sampled from the
        /// ground, forced never to run uphill, then smoothed - so the water plane and
        /// the channel under it are derived from one set of numbers and cannot
        /// disagree. That is what makes the river sit IN its bed rather than above it.
        ///
        /// Width and depth are per node with inheritance, so one river can be a
        /// shallow scrape at its head and a deep run at its mouth without being cut
        /// into three separate bodies.
        /// </summary>
        private void BuildRiverDig()
        {
            carve.isCourse = true;
            carve.profile = BedProfile.RiverChannel;
            carve.bankExponent = bankProfile;
            carve.shelfFraction = shelfFraction;
            carve.maxDepthMetres = channelDepthMetres;

            // THE VALLEY (T17a) is already in carve: the shared setup above writes
            // ValleyCorridorMetres, which derives a river's corridor from its own width
            // whenever no absolute metres are typed, because a valley floor scales with
            // the channel that cut it - the same reasoning that sizes width from
            // catchment.
            //
            // Before that derivation existed, the field's 0 default left the corridor
            // OFF for every river, procedural and hand-placed alike: past the bevel the
            // dig handed back raw generated ground, so a channel crossing a hillside
            // read as a slot sawn through a cliff rather than a river in a valley. A
            // level body is untouched - a lake's shore is its bevel.

            carve.ruggednessMetres = bedRuggednessMetres;
            carve.ruggednessFeatureMetres = ruggednessFeatureMetres;
            carve.ruggednessOctaves = ruggednessOctaves;
            carve.ruggednessPersistence = ruggednessPersistence;
            carve.ruggednessCalmMetres = ruggednessCalmMetres;

            if (!SolveRiverCourse()) return;

            for (int i = 0; i < solvedPoints.Count; i++)
            {
                carve.coursePoints.Add(solvedPoints[i]);
                carve.courseWidths.Add(Mathf.Max(0.2f, solvedWidths[i]));
                carve.courseDepths.Add(Mathf.Max(0.05f, solvedDepths[i]));
                carve.courseSurfaceY.Add(solvedSurfaceY[i]);
            }
        }

        // =====================================================================
        //  THE SOLVED COURSE
        //
        //  The channel and the water in it are two views of one river, and they
        //  are built by two different methods a thousand lines apart. Let each
        //  work the course out for itself and they drift: the mesh resamples the
        //  raw spline while the dig resamples a valley-fitted one, and the result
        //  is a ribbon of water lying beside the trench that was cut for it.
        //
        //  So the course is solved once and both read the answer. This is the only
        //  place the fitted geometry of a river exists.
        // =====================================================================

        [System.NonSerialized] private List<Vector3> solvedPoints;
        [System.NonSerialized] private List<float> solvedWidths;
        [System.NonSerialized] private List<float> solvedDepths;
        [System.NonSerialized] private float[] solvedSurfaceY;

        /// <summary>
        /// One steep reach of a river where the solved surface plunges fast enough to
        /// read as a waterfall or cascade. Found at solve time from the surface profile,
        /// consumed by <see cref="UCWaterfallEffect"/> which lays foam and spray over it.
        /// No mesh is cut for these: the ribbon still follows the surface, the effect is
        /// purely additive, exactly as the dam spillway seam is.
        /// </summary>
        public struct WaterfallReach
        {
            /// <summary>World point at the crest, where the plunge begins.</summary>
            public Vector3 crest;
            /// <summary>World point at the foot, where the plunge lands.</summary>
            public Vector3 foot;
            /// <summary>Vertical fall from crest to foot, metres. Always positive.</summary>
            public float dropMetres;
            /// <summary>Horizontal run the fall is spread over, metres.</summary>
            public float runMetres;
            /// <summary>Channel width at the crest, metres. Sets how wide the foam is.</summary>
            public float widthMetres;
        }

        [System.NonSerialized] private List<WaterfallReach> solvedCascades;

        /// <summary>
        /// The steep reaches the last solve found on this course, or an empty list.
        /// Read by <see cref="UCWaterfallEffect"/>. Never null after a solve; callers
        /// still guard for null in case they run before the first solve.
        /// </summary>
        public IReadOnlyList<WaterfallReach> Cascades => solvedCascades;

        /// <summary>Throws away the last solve, so the next thing that needs the course
        /// re-fits it against the ground as it is now.</summary>
        private void InvalidateSolvedCourse()
        {
            solvedPoints = null;
            solvedWidths = null;
            solvedDepths = null;
            solvedSurfaceY = null;
            solvedCascades = null;
        }

        /// <summary>Solves the course if it has not been solved since the last change.</summary>
        private bool EnsureSolvedCourse()
        {
            if (solvedPoints != null && solvedPoints.Count >= 2) return true;
            return SolveRiverCourse();
        }

        /// <summary>
        /// Builds this body's published river profile from its solved course -- the per-station width, water-surface
        /// level and bed level, in world space -- for the settle pass to deposit into the world's RiverProfilePublication
        /// so a city or road tool can bank the river UC actually cut. Rivers only: a level body (lake, ocean, reservoir)
        /// is not a reach and returns false, as does a river whose course will not solve against the current ground. The
        /// bed is the surface less the dug depth, floored so a dried reach never reports water below its own bed. The
        /// publisher stamps the reach's stable id; this fills the geometry and a human label only.
        /// </summary>
        public bool TryBuildRiverProfile(out UniverseCreator.Integration.RiverReachProfile profile)
        {
            profile = null;
            if (shape != ShapeMode.RiverSpline) return false;
            if (!EnsureSolvedCourse()) return false;
            if (solvedPoints == null || solvedPoints.Count < 2) return false;

            int n = solvedPoints.Count;
            var stations = new UniverseCreator.Integration.RiverProfileStation[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = solvedPoints[i];
                float surfaceY = solvedSurfaceY[i];
                float depth = Mathf.Max(0f, solvedDepths[i]);

                stations[i] = new UniverseCreator.Integration.RiverProfileStation
                {
                    worldX = p.x,
                    worldZ = p.z,
                    widthMetres = Mathf.Max(0f, solvedWidths[i]),
                    surfaceLevelMetres = surfaceY,
                    bedLevelMetres = surfaceY - depth,
                };
            }

            profile = new UniverseCreator.Integration.RiverReachProfile
            {
                sourceLabel = name,
                stations = stations,
            };
            return true;
        }

        // =====================================================================
        //  "DID THE GROUND ACTUALLY MOVE?"
        //
        //  A settle happens once per chunk that finishes, so a 25-cell world
        //  settles 25 times per build. Re-fitting on all of them means re-solving
        //  the course, rebuilding the water mesh and rewriting every chunk the
        //  river crosses, 25 times over, to reach the same answer the first pass
        //  already had - and, while the solve was reading the live terrain, to
        //  reach a DIFFERENT and steadily deeper answer each time.
        //
        //  So a body keeps a record of the ground it is currently fitted to and
        //  compares before it moves. The record is the undug ground, which only
        //  changes when a cell is genuinely re-baked - which is exactly and only
        //  when a re-fit is owed.
        // =====================================================================

        // x, z and undug ground height per probe, in course order. Null means "not
        // fitted to anything I can verify": either nothing has been baked yet, or part
        // of the course sits on a cell with no undug record. Not serialised - it
        // describes this session's terrain, and claiming it survived a reload is the
        // mistake Chunk.bakedThisSession exists to refuse.
        [System.NonSerialized] private float[] fittedGround;

        // Enough to catch a valley moving under any part of a course, cheap enough to
        // take on every settle. A long river solves to a few hundred points, so this
        // samples roughly every third or fourth one.
        private const int MaxGroundProbes = 96;

        // A centimetre. Below this the ground did not move, it was re-quantised by the
        // 16-bit heightmap it came back through.
        private const float GroundProbeEpsilon = 0.01f;

        // Where across the bevel corridor the containment pass looks for the bank, as
        // fractions of the bevel out from the channel edge. The waterline itself, the
        // middle of the blend, and the far edge where the corridor gives way to
        // untouched ground: three samples per side is enough to catch a bank that falls
        // away, and few enough that a several-hundred-node river still solves in one
        // frame. See ContainToBanks.
        private static readonly float[] BankProbeFractions = { 0f, 0.5f, 1f };

        /// <summary>
        /// The undug ground along the solved course, or null when it cannot all be
        /// read.
        /// </summary>
        /// <remarks>
        /// All or nothing on purpose. A partial baseline would compare the half of a
        /// course it can still see against the half that moved and call that unchanged,
        /// which is a river left cutting a trench across a hillside.
        /// </remarks>
        private float[] SampleFittedGround()
        {
            if (solvedPoints == null || solvedPoints.Count < 2) return null;

            UniverseCreatorObject world = CarveWorld;
            ChunkManager chunks = world != null ? world.Chunks : null;
            if (chunks == null) return null;

            int n = solvedPoints.Count;
            int stride = Mathf.Max(1, Mathf.CeilToInt(n / (float)MaxGroundProbes));

            List<float> probe = new List<float>();

            for (int i = 0; i < n; i += stride)
            {
                Vector3 p = solvedPoints[i];

                float y;
                if (!chunks.TrySampleGraphHeight(p.x, p.z, out y)) return null;

                probe.Add(p.x);
                probe.Add(p.z);
                probe.Add(y);
            }

            return probe.Count > 0 ? probe.ToArray() : null;
        }

        /// <summary>
        /// True when two probes describe the same ground under the same course, so the
        /// solve would return the answer it already returned.
        /// </summary>
        /// <remarks>
        /// Written as !(diff &lt;= eps) rather than (diff &gt; eps) deliberately. A NaN
        /// difference fails every comparison it is put through, so the &gt; form would
        /// read it as "close enough" and skip a re-fit the world may well owe. This way
        /// a NaN reports movement, which errs towards doing the work rather than
        /// towards silently not doing it.
        ///
        /// Public because it is the whole gate, and a gate that decides whether the
        /// terrain gets rewritten is worth a test.
        /// </remarks>
        public static bool GroundProbesMatch(float[] a, float[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length || a.Length == 0) return false;

            for (int i = 0; i < a.Length; i++)
                if (!(Mathf.Abs(a[i] - b[i]) <= GroundProbeEpsilon)) return false;

            return true;
        }

        /// <summary>
        /// Fits the drawn line to the ground, carries it into whatever it joins, and
        /// works out the water surface along the result.
        /// </summary>
        private bool SolveRiverCourse()
        {
            InvalidateSolvedCourse();

            // The world's chunk manager is looked up again for this solve. Holding it
            // across solves would survive a domain reload as a dead reference and send
            // every ground query to the fallback without saying so.
            groundSourceResolved = false;

            List<float> widths;
            List<float> depths;
            List<Vector3> world = WorldCourse(out widths, out depths);
            if (world == null || world.Count < 2) return false;

            // Junctions extend the course onto whatever it meets, so the channel is
            // actually cut all the way to the water it empties into rather than
            // stopping a few metres short with a bank between them.
            ExtendToJunction(world, widths, depths, false);
            ExtendToJunction(world, widths, depths, true);

            int n = world.Count;
            float[] surf = new float[n];

            for (int i = 0; i < n; i++)
                surf[i] = RawSurfaceAt(world[i]);

            // Water cannot run uphill. Without this a course drawn across a rise
            // produces a river that climbs it, and a channel dug to match would be a
            // trench going up a hill with water in it.
            EnforceDownhill(surf);

            // A raw ground sample gives a stepped surface that reads as a staircase of
            // tiny waterfalls. A real river surface is smooth even where its bed is not.
            for (int pass = 0; pass < profileSmoothing; pass++)
            {
                // Endpoints held: smoothing them would drag the mouth off the level it
                // was placed at, and the mouth is the end a designer cares about.
                for (int i = 1; i < n - 1; i++)
                    surf[i] = (surf[i - 1] + surf[i] * 2f + surf[i + 1]) * 0.25f;
            }

            // Again after smoothing: the average of a step can lift a point above its
            // upstream neighbour, and one uphill metre reads as water running the wrong way.
            EnforceDownhill(surf);

            // Then hold the water inside its own banks, which is the only place the
            // hanging-water fault can be fixed. See ContainToBanks.
            ContainToBanks(world, widths, depths, surf);
            EnforceDownhill(surf);

            // Last, so nothing downstream of here can pull the mouth back off the
            // level of the body it joins. A step of even a few centimetres at a
            // junction is visible as a seam between two water planes.
            BlendJunction(world, surf, depths, true);
            BlendJunction(world, surf, depths, false);

            solvedPoints = world;
            solvedWidths = widths;
            solvedDepths = depths;
            solvedSurfaceY = surf;
            solvedCascades = DetectCascades(world, surf, widths);

            return true;
        }

        // A reach is a waterfall when the water surface itself drops faster than this
        // gradient. It is the SOLVED surface, not the raw ground, so the many small
        // steps the smoothing and downhill passes already flattened do not count - only
        // a sustained plunge that survived those passes reads as a fall. Below this the
        // reach is just fast water and gets no foam column of its own.
        private const float WaterfallGrade = 0.28f;       // ~15.6 deg; steeper than any bank
        private const float WaterfallMinDropMetres = 1.5f; // shorter than this is a riffle, not a fall

        /// <summary>
        /// Walks the solved surface and gathers the reaches steep enough to read as a
        /// waterfall or cascade. Elevation-driven exactly as the requirement asks: a
        /// river that happens to cross a steep hillside grows a fall there, and one that
        /// stays on gentle ground grows none, with no authoring and no marker node.
        /// </summary>
        private List<WaterfallReach> DetectCascades(List<Vector3> pts, float[] surf, List<float> w)
        {
            var found = new List<WaterfallReach>();
            if (pts == null || surf == null || pts.Count < 2) return found;

            int n = Mathf.Min(pts.Count, surf.Length);
            int i = 0;
            while (i < n - 1)
            {
                // Grade of this single segment, drop over horizontal run.
                float run = Horizontal(pts[i], pts[i + 1]);
                float drop = surf[i] - surf[i + 1];
                if (run <= 0.001f || drop <= 0f || (drop / run) < WaterfallGrade)
                {
                    i++;
                    continue;
                }

                // A fall can span several segments. Grow the reach downstream while the
                // surface keeps plunging, so one tall cascade is one effect and not a
                // stack of short ones fighting for the same water.
                int start = i;
                float totalDrop = drop;
                float totalRun = run;
                int j = i + 1;
                while (j < n - 1)
                {
                    float r2 = Horizontal(pts[j], pts[j + 1]);
                    float d2 = surf[j] - surf[j + 1];
                    if (r2 <= 0.001f || d2 <= 0f || (d2 / r2) < WaterfallGrade) break;
                    totalDrop += d2;
                    totalRun += r2;
                    j++;
                }

                if (totalDrop >= WaterfallMinDropMetres)
                {
                    found.Add(new WaterfallReach
                    {
                        crest = new Vector3(pts[start].x, surf[start], pts[start].z),
                        foot = new Vector3(pts[j].x, surf[j], pts[j].z),
                        dropMetres = totalDrop,
                        runMetres = totalRun,
                        widthMetres = (start < w.Count) ? Mathf.Max(0.5f, w[start]) : 1f,
                    });
                }

                i = j; // resume past the reach just consumed
            }

            return found;
        }

        private static float Horizontal(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// The water surface elevation a course point implies, before the solve's
        /// downhill, smoothing, containment and junction passes get hold of it.
        /// </summary>
        /// <remarks>
        /// A flat-floor river measures its surface down from the line as drawn and never
        /// asks the terrain anything - that is the whole point of the mode, and it is why
        /// Y is preserved through the reroute. Every other mode reads the ground, because
        /// a real river's elevation is set by the land it crosses and not by where a
        /// spline was dropped.
        ///
        /// One implementation, called by the solve and by the inspector's per-node
        /// readout. They disagreed before: the readout showed the raw local Y of the
        /// point, which in every mode but flat-floor is discarded here, so the elevation
        /// box in the node panel displayed a number that changed nothing and bore no
        /// relation to where the water actually was.
        /// </remarks>
        private float RawSurfaceAt(Vector3 world)
        {
            float ground = bedSource == BedSource.FlatFloor
                ? world.y - flatFloorDepthMetres
                : SampleGround(world.x, world.z);

            return ground + surfaceOffsetMetres;
        }

        /// <summary>
        /// Makes the surface profile monotonically non-increasing from head to mouth.
        /// </summary>
        /// <remarks>
        /// One implementation, called at each of the three points in the solve where the
        /// profile can have been lifted: after the ground samples, after smoothing, and
        /// after containment. Three copies of the same two lines is how they end up
        /// disagreeing when one of them is later changed.
        /// </remarks>
        private void EnforceDownhill(float[] surf)
        {
            if (!enforceDownhill || surf == null) return;

            for (int i = 1; i < surf.Length; i++)
                if (surf[i] > surf[i - 1]) surf[i] = surf[i - 1];
        }

        /// <summary>
        /// Lowers the water surface to whatever its own banks can actually hold.
        /// </summary>
        /// <remarks>
        /// THE FAULT THIS FIXES
        /// A river's surface is authored from the ground under its CENTRELINE. Where the
        /// course runs along a hillside rather than down a valley floor, the ground one
        /// bank width out on the low side is below that centreline - so the water plane
        /// stands over open ground and the terrain never comes up to meet it. That is the
        /// reported water hanging in the air with a gap at the bank.
        ///
        /// WHY IT CANNOT BE FIXED IN THE CARVE
        /// WaterCarveLayer is cut-only, on purpose: it may pull a bank down to the
        /// waterline but never push one up, because raising ground would build a dam
        /// around every pond dropped on a slope. So the bank cannot rise to the water,
        /// which leaves exactly one honest answer - the water comes down to the bank.
        /// That is also what real water does: a channel's stage cannot exceed its lowest
        /// containing bank, because past that point it spills over it.
        ///
        /// HOW THE BANK IS FOUND
        /// Sampled perpendicular to the local flow direction, both sides, out across the
        /// bevel corridor - the same reach WaterCarveLayer blends over, so what is
        /// measured here is precisely what gets cut there. The lowest sample is the
        /// containing level.
        ///
        /// WHY THE DROP IS CAPPED
        /// Where a hillside falls further over one bank width than the channel is deep,
        /// the course is not on a river's ground at all, and following it down would
        /// gouge a canyon instead of cutting a channel. So the drop stops at one channel
        /// depth below the centreline. A gap can survive that cap, and this is the right
        /// place for it to survive: no surface elevation contains water on a slope that
        /// steep - the course is in the wrong place, and the valley seek is what moves it.
        ///
        /// A flat-floor river is excluded because it measures itself from the drawn line
        /// and never asks the terrain anything, which is the entire point of that mode.
        /// </remarks>
        private void ContainToBanks(List<Vector3> world, List<float> widths,
                                    List<float> depths, float[] surf)
        {
            if (bedSource == BedSource.FlatFloor) return;
            if (world == null || surf == null || world.Count != surf.Length) return;

            int n = world.Count;
            float bevel = Mathf.Max(0f, shoreBevelMetres);

            for (int i = 0; i < n; i++)
            {
                // Plan-view bank normal: perpendicular to the flow through this node.
                // Taken across the neighbours rather than from one segment, so a node on
                // a bend measures the bank of the curve instead of the bank of whichever
                // segment it happens to be indexed against.
                Vector3 a = world[i > 0 ? i - 1 : 0];
                Vector3 b = world[i < n - 1 ? i + 1 : n - 1];

                float tx = b.x - a.x;
                float tz = b.z - a.z;
                float len = Mathf.Sqrt(tx * tx + tz * tz);
                if (len < 1e-4f) continue;

                float nx = -tz / len;
                float nz = tx / len;

                float half = 0.5f * Mathf.Max(0.2f, widths != null && i < widths.Count
                                                        ? widths[i] : 1f);

                float centre = SampleGround(world[i].x, world[i].z);
                float depth = Mathf.Max(0.5f, depths != null && i < depths.Count
                                                  ? depths[i] : 1f);

                float lowest = surf[i];

                for (int s = 0; s < BankProbeFractions.Length; s++)
                {
                    float reach = half + bevel * BankProbeFractions[s];

                    float left = SampleGround(world[i].x + nx * reach,
                                              world[i].z + nz * reach);
                    float right = SampleGround(world[i].x - nx * reach,
                                               world[i].z - nz * reach);

                    if (left < lowest) lowest = left;
                    if (right < lowest) lowest = right;
                }

                float limit = centre - depth;
                if (lowest < limit) lowest = limit;

                if (lowest < surf[i]) surf[i] = lowest;
            }
        }

        // =====================================================================
        //  JUNCTIONS
        //
        //  A river has to arrive somewhere. Left alone it ends in mid-terrain with
        //  a wall of ground between its last cross-section and the lake it was
        //  drawn towards, and no amount of tuning the channel fixes that, because
        //  the problem is that the two bodies were never told about each other.
        // =====================================================================

        /// <summary>
        /// Walks the course out to the edge of the body it joins, adding points at the
        /// authored spacing so the dig carries on into the junction.
        /// </summary>
        private void ExtendToJunction(List<Vector3> world, List<float> widths,
                                      List<float> depths, bool downstream)
        {
            UCWaterBody target = ResolveJoin(downstream);
            if (target == null) return;
            if (world.Count == 0) return;

            int end = downstream ? world.Count - 1 : 0;
            Vector3 from = world[end];

            Vector3 snapped;
            float targetY, targetDepth;
            if (!target.NearestEdgePoint(from, joinBlendMetres, out snapped, out targetY, out targetDepth))
                return;

            float gap = new Vector2(snapped.x - from.x, snapped.z - from.z).magnitude;
            if (gap < 0.25f) return;

            // Beyond this the two bodies are not neighbours and joining them would
            // trench a channel across whatever happens to lie between. Left alone
            // rather than forced, so the fix is to drag the spline closer.
            float reach = Mathf.Max(50f, joinBlendMetres * 4f);
            if (gap > reach) return;

            int steps = Mathf.Max(1, Mathf.CeilToInt(gap / Mathf.Max(0.5f, pointSpacingMetres)));

            for (int s = 1; s <= steps; s++)
            {
                float t = s / (float)steps;
                Vector3 p = Vector3.Lerp(from, snapped, t);

                if (downstream)
                {
                    world.Add(p);
                    widths.Add(widths[widths.Count - 1]);
                    depths.Add(depths[depths.Count - 1]);
                }
                else
                {
                    // Inserted at the head in increasing t, which lands them in
                    // upstream-first order with the snapped point at index 0.
                    world.Insert(0, p);
                    widths.Insert(0, widths[0]);
                    depths.Insert(0, depths[0]);
                }
            }
        }

        /// <summary>
        /// Eases the surface and the depth into the body being joined over the blend
        /// distance, so the channel does not arrive as a step.
        /// </summary>
        private void BlendJunction(List<Vector3> world, float[] surf, List<float> depths,
                                   bool downstream)
        {
            UCWaterBody target = ResolveJoin(downstream);
            if (target == null) return;

            int n = world.Count;
            if (n < 2) return;

            int end = downstream ? n - 1 : 0;

            Vector3 snapped;
            float targetY, targetDepth;
            if (!target.NearestEdgePoint(world[end], joinBlendMetres, out snapped, out targetY, out targetDepth))
                return;

            float blend = Mathf.Max(0.01f, joinBlendMetres);

            float travelled = 0f;
            int step = downstream ? -1 : 1;

            for (int i = end; i >= 0 && i < n; i += step)
            {
                if (i != end)
                {
                    Vector3 a = world[i];
                    Vector3 b = world[i - step];
                    travelled += new Vector2(b.x - a.x, b.z - a.z).magnitude;
                }

                if (travelled > blend) break;

                // 1 at the junction, 0 a blend-length back up the river. Smoothstep
                // rather than linear so the surface arrives tangent instead of with a
                // crease at the point the blend runs out.
                float w = Mathf.SmoothStep(0f, 1f, 1f - travelled / blend);

                surf[i] = Mathf.Lerp(surf[i], targetY, w);

                // Deepen only. A river meeting a lake should reach the lake's bed, but
                // a deep river meeting a shallow shore must not be filled in.
                if (joinMatchesDepth && targetDepth > depths[i])
                    depths[i] = Mathf.Lerp(depths[i], targetDepth, w);
            }
        }

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

            if (shape == ShapeMode.RiverSpline)
            {
                List<Vector3> line = ToWorld(course);
                if (line == null || line.Count < 2) return false;

                if (!NearestOnPolyline(line, false, fromWorld, out point)) return false;

                surfaceY = SampleGround(point.x, point.z) + surfaceOffsetMetres;
                depthAtJoin = Mathf.Max(0.05f, channelDepthMetres);
                return true;
            }

            List<Vector3> ring = WorldShoreRing();
            if (ring == null || ring.Count < 3) return false;

            if (!NearestOnPolyline(ring, true, fromWorld, out point)) return false;

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
            carve.isCourse = false;
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

        /// <summary>
        /// The course in world space, resampled to the authored spacing, carrying the
        /// width and depth that apply at every resampled point.
        ///
        /// Resampled rather than used raw because the carve interpolates width, depth
        /// and surface between consecutive points: two points a hundred metres apart
        /// would blend across the whole span and lose every bend between them.
        ///
        /// This is also where the drawn line becomes a line water could follow - the
        /// valley seek and the descent walk run here, on the resampled points, so the
        /// mesh and the dig are both built from the fitted course and cannot end up
        /// describing two different rivers.
        /// </summary>
        public void ApplyValleyReroute()
        {
            if (course == null || course.Count < 2) return;

            List<Vector3> worldPoints = ToWorld(course);
            RiverCourseSolver.GroundSampler ground = SampleGround;

            RiverCourseSolver.SnapToValley(worldPoints, ground, valleySearchMetres, valleyPull, 12);
            RiverCourseSolver.Relax(worldPoints, 2);

            // Both passes move points in XZ only, so each one's stored elevation now
            // belongs to ground it no longer stands on. Re-read it, and clamp to a
            // running minimum from the head down: a water surface never climbs, and
            // without this the reroute button could itself make a river run uphill.
            float ceiling = ground(worldPoints[0].x, worldPoints[0].z);
            worldPoints[0] = new Vector3(worldPoints[0].x, ceiling, worldPoints[0].z);

            for (int i = 1; i < worldPoints.Count; i++)
            {
                Vector3 p = worldPoints[i];
                ceiling = Mathf.Min(ceiling, ground(p.x, p.z));
                worldPoints[i] = new Vector3(p.x, ceiling, p.z);
            }

            for (int i = 0; i < worldPoints.Count; i++)
            {
                course.points[i] = transform.InverseTransformPoint(worldPoints[i]);
            }
        }

        private List<Vector3> WorldCourse(out List<float> widths, out List<float> depths)
        {
            widths = null;
            depths = null;

            if (course == null || course.Count < 2) return null;

            List<Vector3> world = ToWorld(course);

            // Per-node width and depth resolved with inheritance BEFORE the resample,
            // because inheritance is defined on the nodes the user placed. Resolving
            // it afterwards would inherit from interpolated points, which is a
            // different and meaningless thing.
            float[] nodeW = ResolveNodeSeries(nodeWidthMetres, widthMetres);
            float[] nodeD = ResolveNodeSeries(nodeDepthMetres, channelDepthMetres);

            // Packed into the spline's weight channel so the resample carries them
            // through for free: weight is width, and depth is recovered from the
            // width fraction afterwards using the same node interpolation.
            Polyline line = new Polyline();
            for (int i = 0; i < world.Count; i++)
                line.Add(world[i], nodeW[Mathf.Min(i, nodeW.Length - 1)]);

            // Centripetal Catmull-Rom, not the uniform one. A river bends through its
            // nodes; it does not run in straight reaches between them with a corner at
            // each. Uniform parameterisation would overshoot wherever two consecutive
            // spans are very different lengths - which is every hand-placed line - and
            // an overshooting river digs a trench outside its own bank.
            //
            // Tessellated at half the final spacing so the resample below is walking
            // an already-curved line. Resampling a chord at any spacing only ever
            // produces points on that chord, so doing it the other way round would
            // give evenly spaced corners rather than a curve.
            Polyline smooth = SplineOperations.TessellateCatmullRom(
                line, Mathf.Max(0.25f, pointSpacingMetres * 0.5f));

            Polyline spaced = SplineOperations.Resample(smooth, Mathf.Max(0.5f, pointSpacingMetres), true);
            if (spaced == null || spaced.Count < 2) return null;

            List<Vector3> result = new List<Vector3>(spaced.Count);
            for (int i = 0; i < spaced.Count; i++) result.Add(spaced.PointAt(i));

            // position rather than read off the resampled line. The tessellation does
            // carry the weight channel, but depth has no channel to ride in and both
            // have to come from the same place or they can disagree at a node.
            widths = new List<float>(result.Count);
            depths = new List<float>(result.Count);
            SampleNodeSeries(world, result, nodeW, nodeD, widths, depths);

            // ---- Fitting the drawn line to the ground ------------------------

            // Gathered before the walk, because the walk asks about them at every step
            // and a scene search per step would dominate the build.
            RefreshJoinCandidates();

            RiverCourseSolver.GroundSampler groundFit = SampleGround;

            // BOUNDED DE-BOW, always on. A course - drawn or D8-generated - is a set of
            // sparse nodes with a Catmull-Rom curve through them, and that curve bows
            // OUTSIDE the valley between two nodes: the river reads as running along a
            // hillside instead of down the valley floor (the reported "flows on the
            // inside of a hill"). A short lateral snap toward local low ground pulls the
            // bow back onto the thalweg. The reach is only a few channel widths, so it
            // corrects the bow but can NEVER jump the river into the next valley - which
            // is what the 2 km fitToValley seek could do, and why the generator had to
            // switch that off. This one is safe to leave on for every river, and gives a
            // hand-placed river the same "never climb the bank" fit a generated one gets.
            {
                float debowReach = Mathf.Clamp(Mathf.Max(0.5f, widthMetres) * DebowWidths,
                                                DebowMinMetres, DebowMaxMetres);
                RiverCourseSolver.SnapToValley(result, groundFit, debowReach, DebowPull, 8);
                RiverCourseSolver.Relax(result, 1);
            }

            if (fitToValley || routeAroundHighGround)
            {
                RiverCourseSolver.GroundSampler ground = groundFit;

                if (fitToValley)
                {
                    RiverCourseSolver.SnapToValley(result, ground,
                                                   valleySearchMetres, valleyPull, 12);
                    RiverCourseSolver.Relax(result, 2);
                }

                if (routeAroundHighGround)
                {
                    List<Vector3> walked = new List<Vector3>(result.Count + 16);

                    lastDescentEnding = RiverCourseSolver.WalkDownhill(
                        result, walked, ground,
                        Mathf.Max(0.5f, pointSpacingMetres),
                        routeFanDegrees, climbToleranceMetres,
                        autoConnectMetres > 0f ? IsAtJoinableWater : null);

                    if (walked.Count >= 2)
                    {
                        // The walk emits its own point count - it swings wide round a
                        // spur and it stops early where the ground closes in - so the
                        // width and depth series are re-sampled against the walked
                        // path rather than carried across by index.
                        List<float> w2 = new List<float>(walked.Count);
                        List<float> d2 = new List<float>(walked.Count);
                        ResampleSeriesAlong(result, widths, depths, walked, w2, d2);

                        result = walked;
                        widths = w2;
                        depths = d2;

                        RiverCourseSolver.Relax(result, 1);
                    }
                }
            }
            else
            {
                lastDescentEnding = RiverCourseSolver.DescentEnding.ReachedEnd;
            }

            // A river that found no way down does not stop dead in a wall of ground.
            // It thins out and sinks, the way one does when it runs into karst or into
            // a desert - and that is the honest answer here, because the alternative
            // is to climb, which water does not do.
            if (lastDescentEnding == RiverCourseSolver.DescentEnding.NoWayDown)
                RiverCourseSolver.TaperSink(result, widths, depths, sinkRunMetres);

            // Let the fitted line wander into natural meander loops where the ground is
            // flat enough to have a floodplain. Last, so it works on the planform the
            // valley seek and descent walk already put in the right valley - it moves a
            // point across its own flow, not down the course, so it cannot undo the fit
            // or send the river uphill (steep reaches are gated out and every offset is
            // refused if it would climb the bank). The surface solve that follows
            // re-reads the ground under this wandered line, so the water still sits on
            // the terrain the loops now cross.
            if (meander)
                RiverCourseSolver.Meander(result, SampleGround, widths, meanderStrength,
                                          meanderMaxGrade, Mathf.Max(0.5f, widthMetres));

            // Downstream hydraulic geometry (J.4). Last, on the fitted planform, so it
            // reads the valley the course now sits in. Grows width/depth source->mouth
            // from the land drained, then trades width for depth through gorges and back
            // on plains at constant flow area. Operates on the resolved series in place;
            // the surface solve that follows re-reads depth from these.
            if (autoHydraulicGeometry)
                ApplyHydraulicGeometry(result, widths, depths);

            return result;
        }

        /// <summary>
        /// Grows the channel downstream and trades width for depth by valley confinement,
        /// so a procedural (or plainly-drawn) river sizes itself the way the land makes a
        /// real one - without a designer typing per-node numbers. See the field block on
        /// <see cref="autoHydraulicGeometry"/> for the physics.
        /// </summary>
        /// <remarks>
        /// Two independent effects, applied in order:
        ///
        ///  1. DOWNSTREAM GROWTH. If the width series is essentially flat - which is the
        ///     hand-drawn case, one inherited width all the way down, and also the case a
        ///     designer wants "just make it look real" - the width is regraded from the
        ///     source fraction up to the mouth width along arc length under the area^0.4
        ///     law. A river whose widths were authored to vary (the procedural generator
        ///     fills a real per-node profile from catchment) is left as graded: its
        ///     growth is already the honest one and must not be flattened to a curve.
        ///
        ///  2. CONFINEMENT TRADE. At every point the cross-valley ground slope says how
        ///     hard the walls squeeze the channel. Tight walls pull width down and depth
        ///     up; open ground lets width up and depth down. Width*depth (the flow area
        ///     the discharge sets) is held, so no water is invented or lost - the channel
        ///     is only reshaped. This is what makes a reach get narrower AND deeper.
        /// </remarks>
        private void ApplyHydraulicGeometry(List<Vector3> pts, List<float> widths, List<float> depths)
        {
            if (pts == null || widths == null || depths == null) return;
            int n = Mathf.Min(pts.Count, Mathf.Min(widths.Count, depths.Count));
            if (n < 2) return;

            // --- 1. Downstream growth, only when the width is otherwise flat ----
            float wMin = float.MaxValue, wMax = 0f;
            for (int i = 0; i < n; i++)
            {
                float w = widths[i];
                if (w < wMin) wMin = w;
                if (w > wMax) wMax = w;
            }
            bool flat = wMax <= wMin * 1.05f + 1e-3f;   // no real downstream variation authored

            if (flat)
            {
                // Arc length as the proxy for accumulated drainage: a longer course has
                // gathered more land by the time it reaches a point. Not the true
                // catchment (a hand-drawn river has no drainage grid), but monotone and
                // source->mouth, which is the property that matters.
                float[] s = new float[n];
                for (int i = 1; i < n; i++) s[i] = s[i - 1] + Horizontal(pts[i - 1], pts[i]);
                float total = Mathf.Max(1e-3f, s[n - 1]);

                float mouthWidth = wMax;                 // the flat value is the target mouth width
                float src = Mathf.Clamp01(sourceWidthFraction);

                for (int i = 0; i < n; i++)
                {
                    float f = s[i] / total;              // 0 at source, 1 at mouth
                    // width fraction eases from src to 1 as area^0.4; area rises ~linearly
                    // with arc-proxy, so width rises as f^0.4 blended off the source floor.
                    float grow = src + (1f - src) * Mathf.Pow(f, WidthAreaExponent);
                    widths[i] = Mathf.Max(0.2f, mouthWidth * grow);
                    // Depth tracks width at the base ratio here; the trade below reshapes it.
                    depths[i] = Mathf.Max(0.1f, widths[i] / BaseWidthDepthRatio);
                }
            }

            // --- 2. Confinement trade: reshape width vs depth at constant area --
            float strength = Mathf.Clamp01(valleyConfinementStrength);
            if (strength <= 1e-3f) return;

            for (int i = 0; i < n; i++)
            {
                float w = widths[i];
                float d = depths[i];
                if (w <= 0.05f || d <= 0.02f) continue;

                float area = w * d;                      // the flow the discharge fixed - held
                float conf = ValleyConfinement(pts[i], w); // 0 open plain .. 1 tight gorge

                // Confinement pulls the aspect ratio: open ground spreads wide (ratio up),
                // a gorge squeezes narrow-and-deep (ratio down). Centred on the base ratio
                // so an average valley is unchanged.
                float shift = (0.5f - conf) * 2f;        // +1 fully open .. -1 fully confined
                float ratio = BaseWidthDepthRatio * Mathf.Pow(GorgeRatioSpan, shift * strength);

                // width*depth = area, width/depth = ratio  ->  width = sqrt(area*ratio)
                float newW = Mathf.Sqrt(area * ratio);
                float newD = area / Mathf.Max(0.05f, newW);

                widths[i] = Mathf.Max(0.2f, newW);
                depths[i] = Mathf.Max(0.1f, newD);
            }
        }

        // Bounded de-bow (R2/R10). The lateral snap that pulls a bowed spline back onto
        // the valley floor searches this many channel widths to each side - wide enough
        // to catch the floodplain the river belongs in, far too short to reach the next
        // valley over, so it can correct a bow but never relocate the river. Floored and
        // capped in metres so a metre-wide brook still gets a usable search and a 200 m
        // trunk does not sweep half a kilometre. Pull is below 1 so the correction eases
        // in over the profile-smoothing passes rather than snapping in one build.
        private const float DebowWidths = 3f;
        private const float DebowMinMetres = 12f;
        private const float DebowMaxMetres = 120f;
        private const float DebowPull = 0.6f;

        // The width-to-depth ratio of an ordinary alluvial channel in an average valley.
        // Matches the generator's 18:1 so procedural and drawn rivers read the same.
        private const float BaseWidthDepthRatio = 18f;

        // How channel width grows with the land it drains. Downstream hydraulic geometry
        // (Leopold & Maddock, 1953): width ~ Q^0.5, discharge Q ~ area^0.8, so width goes
        // as area^0.4. The generator holds its own copy of this same number; kept local
        // here so UCWaterBody's growth law does not reach across into the generator.
        private const float WidthAreaExponent = 0.4f;

        // How far the ratio can swing between a fully open plain and a fully confined
        // gorge at full strength. 2.0 means a gorge reaches ~half the plain's width (and
        // proportionally deeper) for the same flow - a strong but real difference.
        private const float GorgeRatioSpan = 2.0f;

        /// <summary>
        /// How much the valley confines the channel at a point, 0 (open plain) to 1
        /// (tight gorge). Read from the ground rising away from the course across the
        /// flow: sheer walls close by give a high value, flat ground either side gives
        /// near zero. Sampled at a few channel-widths out so it measures the valley, not
        /// the channel's own dug banks.
        /// </summary>
        private float ValleyConfinement(Vector3 at, float channelWidth)
        {
            float reach = Mathf.Clamp(channelWidth * 2.5f, 6f, 120f);
            float here = SampleGround(at.x, at.z);

            // Cross-flow probes. Direction does not need to be exact - a gorge rises on
            // both sides regardless of orientation - so four compass offsets sample the
            // surrounding walls cheaply and symmetrically.
            float rise = 0f;
            rise += Mathf.Max(0f, SampleGround(at.x + reach, at.z) - here);
            rise += Mathf.Max(0f, SampleGround(at.x - reach, at.z) - here);
            rise += Mathf.Max(0f, SampleGround(at.x, at.z + reach) - here);
            rise += Mathf.Max(0f, SampleGround(at.x, at.z - reach) - here);
            rise *= 0.25f;                               // mean rise of the surrounding ground

            // Normalise against the reach: a rise equal to the reach (a 45 deg wall all
            // round) is full confinement. Gentle ground gives a small fraction.
            return Mathf.Clamp01(rise / reach);
        }

#if UNITY_EDITOR
        // ---------------------------------------------------------------------
        //  Test seams. The self test drives the two elevation/physics passes
        //  added for natural waterfalls (J.2) and downstream hydraulic geometry
        //  (J.4) directly, so their maths is pinned by an executed assertion and
        //  not only by "it compiled". These forward verbatim to the private
        //  methods and add no behaviour; they are compiled out of player builds.
        // ---------------------------------------------------------------------

        /// <summary>Editor-only: run the cascade detector on a supplied course and
        /// solved surface, exactly as a real solve does. Lets the self test build a
        /// steep course and require that a fall is found where one physically is.
        /// Public only because the diagnostics assembly cannot see internals; it is
        /// compiled out of player builds by the guard.</summary>
        public List<WaterfallReach> DetectCascadesForTest(List<Vector3> pts, float[] surf, List<float> w)
        {
            return DetectCascades(pts, surf, w);
        }

        /// <summary>Editor-only: run the downstream hydraulic-geometry pass on supplied
        /// series. Set <see cref="valleyConfinementStrength"/> to 0 first to isolate the
        /// growth law from the ground-sampling confinement trade. Public for the same
        /// cross-assembly reason as above, and behind the same build guard.</summary>
        public void ApplyHydraulicGeometryForTest(List<Vector3> pts, List<float> widths, List<float> depths)
        {
            ApplyHydraulicGeometry(pts, widths, depths);
        }
#endif

        /// <summary>Where the last build's descent walk ended. Reported by the
        /// inspector, because "the river stops here" is worth knowing about.</summary>
        [System.NonSerialized]
        private RiverCourseSolver.DescentEnding lastDescentEnding =
            RiverCourseSolver.DescentEnding.ReachedEnd;

        public RiverCourseSolver.DescentEnding LastDescentEnding { get { return lastDescentEnding; } }

        /// <summary>
        /// Fills in the unset entries of a per-node series from the last node above
        /// them that was set.
        ///
        /// Inheritance runs downstream only, which is the direction the value has a
        /// meaning in: a river carries what it has gathered so far. The run before the
        /// first authored node takes the body-wide default, so a course with nothing
        /// typed on it behaves exactly as it did before per-node values existed.
        /// </summary>
        private float[] ResolveNodeSeries(List<float> authored, float fallback)
        {
            int n = course.Count;
            float[] resolved = new float[Mathf.Max(1, n)];

            float carried = Mathf.Max(0.05f, fallback);

            for (int i = 0; i < n; i++)
            {
                float v = authored != null && i < authored.Count ? authored[i] : 0f;

                // Non-zero is authored. Zero means "not set here", which is the whole
                // point: a designer types a number at the three places the river
                // actually changes and leaves the rest alone.
                if (v > 1e-4f) carried = v;

                resolved[i] = carried;
            }

            // Between two authored nodes the value eases rather than steps. A river
            // does not double in depth at one cross-section, and a step in the dig
            // shows as a shelf across the channel.
            SmoothBetweenAuthored(authored, resolved);

            return resolved;
        }

        /// <summary>
        /// Blends each unset run from the value above it to the value below it, so an
        /// inherited stretch is a transition rather than a plateau ending in a step.
        /// </summary>
        private void SmoothBetweenAuthored(List<float> authored, float[] resolved)
        {
            int n = resolved.Length;

            int prevSet = -1;

            for (int i = 0; i < n; i++)
            {
                bool set = authored != null && i < authored.Count && authored[i] > 1e-4f;
                if (!set) continue;

                if (prevSet >= 0 && i - prevSet > 1)
                {
                    float a = resolved[prevSet];
                    float b = resolved[i];

                    for (int k = prevSet + 1; k < i; k++)
                    {
                        float t = (k - prevSet) / (float)(i - prevSet);
                        resolved[k] = Mathf.Lerp(a, b, Mathf.SmoothStep(0f, 1f, t));
                    }
                }

                prevSet = i;
            }
        }

        /// <summary>
        /// Reads the node width and depth at every resampled point, by nearest
        /// position along the original course.
        /// </summary>
        private static void SampleNodeSeries(List<Vector3> nodes, List<Vector3> sampled,
                                             float[] nodeW, float[] nodeD,
                                             List<float> widths, List<float> depths)
        {
            // Cumulative arc length of the authored nodes, so a point halfway along
            // the course reads the value halfway along the node list even when the
            // nodes are unevenly spaced.
            int n = nodes.Count;
            float[] arc = new float[n];

            for (int i = 1; i < n; i++)
                arc[i] = arc[i - 1] + new Vector2(nodes[i].x - nodes[i - 1].x,
                                                  nodes[i].z - nodes[i - 1].z).magnitude;

            float total = Mathf.Max(1e-4f, arc[n - 1]);

            int m = sampled.Count;
            float travelled = 0f;

            for (int s = 0; s < m; s++)
            {
                if (s > 0)
                    travelled += new Vector2(sampled[s].x - sampled[s - 1].x,
                                             sampled[s].z - sampled[s - 1].z).magnitude;

                float u = Mathf.Clamp01(travelled / total) * total;

                int seg = 0;
                while (seg < n - 2 && arc[seg + 1] < u) seg++;

                float span = Mathf.Max(1e-4f, arc[seg + 1] - arc[seg]);
                float t = Mathf.Clamp01((u - arc[seg]) / span);

                widths.Add(Mathf.Lerp(nodeW[seg], nodeW[Mathf.Min(seg + 1, n - 1)], t));
                depths.Add(Mathf.Lerp(nodeD[seg], nodeD[Mathf.Min(seg + 1, n - 1)], t));
            }
        }

        /// <summary>
        /// Carries a width and depth series from one path onto another by nearest
        /// point. Used after the descent walk, which produces a path of its own
        /// length that no longer lines up with the input index for index.
        ///
        /// It also carries the drawn elevation back onto the walked path. The walk
        /// writes ground height into Y as it goes, but Y on this course means the
        /// height the designer drew at - which is what a flat-floor river measures its
        /// surface down from - so it has to survive the reroute.
        /// </summary>
        private static void ResampleSeriesAlong(List<Vector3> from, List<float> fromW, List<float> fromD,
                                                List<Vector3> to, List<float> toW, List<float> toD)
        {
            for (int i = 0; i < to.Count; i++)
            {
                Vector3 p = to[i];

                int best = 0;
                float bestD2 = float.MaxValue;

                for (int j = 0; j < from.Count; j++)
                {
                    float dx = from[j].x - p.x;
                    float dz = from[j].z - p.z;
                    float d2 = dx * dx + dz * dz;

                    if (d2 < bestD2) { bestD2 = d2; best = j; }
                }

                toW.Add(best < fromW.Count ? fromW[best] : 1f);
                toD.Add(best < fromD.Count ? fromD[best] : 1f);

                to[i] = new Vector3(p.x, from[best].y, p.z);
            }
        }

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
        //  AUTO CONNECT
        //
        //  A river drawn towards a lake should end in the lake. Making the designer
        //  also pick that lake out of a dropdown is asking them to say the same
        //  thing twice, and it is the kind of thing that gets forgotten - which
        //  shows up as a river stopping in a bank of ground a few metres short of
        //  water.
        //
        //  So the descent walk asks, at every step, whether it has arrived at
        //  water. When it has, that body becomes the junction target for this
        //  build. The serialised field still wins when it is set, because an
        //  explicit choice must beat a guess.
        // =====================================================================

        /// <summary>Bodies this river might arrive at, gathered once per build rather
        /// than searched for at every step of the walk.</summary>
        [System.NonSerialized] private UCWaterBody[] joinCandidates;

        /// <summary>What the walk found, if it found anything. Not serialised: it is a
        /// result of the last build, not a setting.</summary>
        [System.NonSerialized] private UCWaterBody autoJoinDownstream;

        /// <summary>The body this river joins at one end - the authored one if there is
        /// one, otherwise whatever the walk ran into.</summary>
        private UCWaterBody ResolveJoin(bool downstream)
        {
            UCWaterBody authored = downstream ? joinDownstreamTo : joinUpstreamFrom;
            if (CanJoin(authored)) return authored;

            // Upstream is never guessed. A river's head is where the designer put it;
            // arriving at water going downhill is a discovery, starting in it is not.
            if (downstream && CanJoin(autoJoinDownstream)) return autoJoinDownstream;

            return null;
        }

        /// <summary>
        /// Collects the bodies auto-connect may consider, and forgets last build's
        /// guess so a river dragged away from a lake stops joining it.
        /// </summary>
        private void RefreshJoinCandidates()
        {
            autoJoinDownstream = null;
            joinCandidates = null;

            if (autoConnectMetres <= 0f) return;

            // Scoped to the world this river belongs to when there is one, so two
            // worlds open in one scene cannot connect to each other's water.
            UniverseCreatorObject world = CarveWorld;

            // No sort mode: Unity 6 deprecated the overload that takes one, and this
            // list never depended on order anyway - auto-connect picks the nearest
            // candidate, not the first.
            joinCandidates = world != null
                ? world.GetComponentsInChildren<UCWaterBody>(false)
                : Object.FindObjectsByType<UCWaterBody>(FindObjectsInactive.Exclude);
        }

        /// <summary>
        /// Whether this point has reached standing water the river can join. Handed to
        /// the descent walk, which stops there and reports ReachedWater.
        ///
        /// Oceans and lakes only. Pools are decorative and a river running into one
        /// should carry on past it; river-to-river junctions are set on the nodes,
        /// where the designer can say which one joins which.
        /// </summary>
        private bool IsAtJoinableWater(Vector3 p)
        {
            if (joinCandidates == null) return false;

            float reach = Mathf.Max(0f, autoConnectMetres);
            if (reach <= 0f) return false;

            for (int i = 0; i < joinCandidates.Length; i++)
            {
                UCWaterBody other = joinCandidates[i];

                if (other == null || other == this) continue;
                if (other.kind != WaterBodyKind.Ocean && other.kind != WaterBodyKind.Lake) continue;

                Vector3 edge;
                float surfaceAt, depthAt;
                if (!other.NearestEdgePoint(p, joinBlendMetres, out edge, out surfaceAt, out depthAt))
                    continue;

                float gap = new Vector2(edge.x - p.x, edge.z - p.z).magnitude;
                if (gap > reach) continue;

                // First match wins rather than nearest. The walk moves a fraction of a
                // step at a time, so by the time anything is in reach at all it is the
                // one the river is arriving at, and scoring them all every step would
                // cost far more than it could ever change.
                autoJoinDownstream = other;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Asks the world to re-cut every dig into the terrain that is already built.
        ///
        /// Cheap enough to call on every commit: only chunks the digs actually reach
        /// are rewritten, and no graph evaluation happens at all.
        /// </summary>
        public void ReapplyToTerrain()
        {
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
            // The dig is rebuilt first and the surface second, because the surface
            // has to sit on the bed the dig describes. Build them the other way round
            // and the mesh is placed against last build's ground, which is exactly the
            // gap between water and terrain this whole path exists to close.
            BuildDig();

            RebuildSurface();
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
            Clear();

            WaterBodyData body = BuildData();
            if (body == null) return;

            lastBuild = body;

            surfaceObject = new GameObject("UC Water Surface");
            surfaceObject.layer = gameObject.layer;

            // Never serialised. It is rebuilt from these fields on every enable, so
            // saving it would mean a scene that grows by every water surface it has
            // ever shown and duplicates them on the next build.
            surfaceObject.hideFlags = HideFlags.DontSave;
            surfaceObject.transform.SetParent(transform, false);
            surfaceObject.transform.localPosition = Vector3.zero;
            surfaceObject.transform.localRotation = Quaternion.identity;
            surfaceObject.transform.localScale = Vector3.one;

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
            renderer.sharedMaterial = ResolveMaterial(body);

            // Transparent water casting a hard shadow is wrong, and receiving one
            // costs a shadow sample per water pixel to change almost nothing.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;

            if (generateVolume) AttachVolume(body);

            // Natural waterfalls (J.2). A river whose solved surface plunges down a
            // steep reach grows foam and spray there, driven by the drop the solve
            // measured - no marker, no authored flag. Attached only when the course
            // actually has falls and removed when it does not, the same reversible
            // sync the dam spillway uses. Additive: the ribbon is untouched.
            if (shape == ShapeMode.RiverSpline && solvedCascades != null && solvedCascades.Count > 0)
                UCWaterfallEffect.Ensure(this);
            else
                UCWaterfallEffect.Remove(this);
        }

        /// <summary>Destroys everything Rebuild created. Leaves the settings alone.</summary>
        public void Clear()
        {
            if (volume != null) { DestroySafe(volume); volume = null; }

            // The mesh first. Destroying the GameObject leaves the Mesh resident -
            // it is an asset object, not a component, and nothing collects it.
            if (mesh != null) { DestroySafe(mesh); mesh = null; }
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

            WaterBodyData body = shape == ShapeMode.RiverSpline
                ? BuildRiver(terrains)
                : BuildLevelBody(terrains);

            if (body == null) return null;

            body.kind = kind;
            body.name = kind.ToString();
            body.generateVolume = generateVolume;
            body.maxFlowSpeed = Mathf.Max(0.01f, maxFlowSpeed);
            body.product = useOwnMaterial ? water : null;

            return body;
        }

        // ---------------------------------------------------------------------
        //  Level body
        // ---------------------------------------------------------------------

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
        //  River
        // ---------------------------------------------------------------------

        private WaterBodyData BuildRiver(Terrain[] terrains)
        {
            if (course == null || course.Count < 2) return null;

            // The same fitted course the dig uses. The mesh and the channel are built
            // from the same points, widths, depths and surface, so the ribbon cannot
            // drift off the trench that was cut for it.
            if (!EnsureSolvedCourse()) return null;

            int count = solvedPoints.Count;

            // The surface is already a built thing on the solved course - forced
            // downhill, smoothed with the ends held, blended into whatever the river
            // joins - so the mesh reads it rather than deriving a second one that could
            // disagree by a few centimetres and show a seam between two water planes.
            float[] surface = solvedSurfaceY;

            float[] centreDepth = new float[count];
            Vector2[] tangent = new Vector2[count];
            float[] arcStep = new float[count];
            float[] halfWidth = new float[count];

            for (int i = 0; i < count; i++)
            {
                int prev = Mathf.Max(i - 1, 0);
                int next = Mathf.Min(i + 1, count - 1);

                Vector3 a = solvedPoints[prev];
                Vector3 b = solvedPoints[next];

                Vector2 t2 = new Vector2(b.x - a.x, b.z - a.z);
                tangent[i] = t2.sqrMagnitude > 1e-8f ? t2.normalized : Vector2.right;

                // Already metres, not a multiplier: the node series resolves against
                // widthMetres as its fallback, so scaling by it again would square it.
                halfWidth[i] = Mathf.Max(0.1f, solvedWidths[i]) * 0.5f;

                float run = t2.magnitude;
                arcStep[i] = Mathf.Max(0.01f, run / Mathf.Max(1, next - prev));
            }

            // Hydraulics per cross-section.
            float invN = 1f / Mathf.Max(0.005f, manningRoughness);
            float cap = Mathf.Max(0.05f, hydraulicRadiusCap);

            float[] speed = new float[count];
            float[] whitewater = new float[count];

            for (int i = 0; i < count; i++)
            {
                centreDepth[i] = Mathf.Max(minDepthMetres, solvedDepths[i]);

                int prev = Mathf.Max(i - 1, 0);
                int next = Mathf.Min(i + 1, count - 1);

                float run = arcStep[i] * Mathf.Max(1, next - prev);
                float drop = surface[prev] - surface[next];

                // Surface slope, not bed slope. A reach ponded behind a rise has a
                // level surface and does not flow however steep the ground under it
                // is - that one choice is why a lake and a river are the same code.
                float slope = run > 1e-4f ? Mathf.Max(0f, drop / run) : 0f;

                float radius = Mathf.Min(centreDepth[i], cap);
                float v = invN * Mathf.Pow(radius, 0.6666667f) * Mathf.Sqrt(slope);
                speed[i] = Mathf.Clamp(v, 0f, 40f);

                // Froude number. Above about 1 the flow is supercritical and stands
                // up in a hydraulic jump - standing white water - so the whitening
                // ramps in around there rather than being painted by hand.
                float froude = speed[i] / Mathf.Sqrt(Gravity * Mathf.Max(0.05f, centreDepth[i]));
                whitewater[i] = Mathf.Clamp01((froude - 0.8f) / 0.6f);
            }

            // Ribbon.
            int across = Mathf.Clamp(widthSegments, 2, 24) + 1;
            int vertexCount = count * across;
            if (vertexCount < 4) return null;

            WaterBodyData body = new WaterBodyData();
            body.vertices = new Vector3[vertexCount];
            body.normals = new Vector3[vertexCount];
            body.tangents = new Vector4[vertexCount];
            body.uv = new Vector2[vertexCount];
            body.colors = new Color[vertexCount];
            body.triangles = new int[(count - 1) * (across - 1) * 6];

            float feather = Mathf.Max(0.01f, edgeFeatherMetres);
            float invMax = 1f / Mathf.Max(0.01f, maxFlowSpeed);
            float exponent = Mathf.Clamp(bankProfile, 1f, 6f);

            Bounds worldBounds = new Bounds();
            bool boundsStarted = false;

            float surfSum = 0f;
            Vector2 flowSum = Vector2.zero;
            float deepest = 0f;

            for (int i = 0; i < count; i++)
            {
                Vector3 centre = solvedPoints[i];
                Vector2 t = tangent[i];
                Vector2 side = new Vector2(-t.y, t.x);

                // Along-stream surface derivative, so the surface normal tilts
                // downstream. A level normal on a sloping run puts the sun's
                // reflection where it would be on flat water, which reads as a
                // painted texture rather than a moving surface.
                int prev = Mathf.Max(i - 1, 0);
                int next = Mathf.Min(i + 1, count - 1);
                float run = arcStep[i] * Mathf.Max(1, next - prev);
                float dSurf = run > 1e-4f ? (surface[next] - surface[prev]) / run : 0f;

                Vector3 worldNormal = new Vector3(-t.x * dSurf, 1f, -t.y * dSurf).normalized;
                Vector3 localNormal = transform.InverseTransformDirection(worldNormal).normalized;

                // World +X orthogonalised, not flow aligned. A flow-aligned tangent
                // rotates the wave normal map through every meander, and the rotation
                // shows as the texture spinning under itself where the channel turns.
                Vector3 tan = Vector3.right - worldNormal * Vector3.Dot(worldNormal, Vector3.right);
                if (tan.sqrMagnitude < 1e-6f) tan = Vector3.forward;
                tan = transform.InverseTransformDirection(tan.normalized).normalized;

                float normalisedSpeed = Mathf.Clamp01(speed[i] * invMax);
                Color colour = new Color(t.x * 0.5f + 0.5f, t.y * 0.5f + 0.5f, normalisedSpeed, 1f);

                surfSum += surface[i];
                flowSum += t * speed[i];

                for (int j = 0; j < across; j++)
                {
                    float u = across > 1 ? (j / (float)(across - 1)) * 2f - 1f : 0f;

                    // The CARVE is the authority: its waterline sits at halfWidth[i] (the
                    // same solvedWidths the trench was dug with), so the water surface must
                    // meet that line, not sail past it. Extend by only a hairline seal so
                    // the plane still bites the rising bank and leaves no gap - never the
                    // full bank feather, which is what made the shader "surpass the carve
                    // with much". The seal is a small fraction of the local half-width so a
                    // wide river does not balloon either, floored so a metre-wide brook is
                    // still sealed and capped so it can never exceed the feather.
                    float seal = Mathf.Min(feather, Mathf.Max(0.12f, halfWidth[i] * 0.08f));
                    float extendWidth = halfWidth[i] + seal;

                    // Channel cross-section. exponent 2 is the parabola a natural
                    // stream cuts; 4 is a flat bed with steep banks; 1 is a shallow V.
                    float d = centreDepth[i] * (1f - Mathf.Pow(Mathf.Abs(u), exponent));
                    if (d < 0f) d = 0f;
                    
                    // Same conformer as the level bodies, applied per vertex because a
                    // ribbon's surface follows its own gradient rather than one level:
                    // bound the depth, then drop the dry bank edge into the ground.
                    d = ShorelineConformer.CapDepth(d, ShorelineConformer.MaxSkirtDepthMetres);

                    float skirtY = ShorelineConformer.CalculateSkirtHeight(
                                       surface[i], d, ShorelineConformer.SkirtDropMetres);

                    Vector3 world = new Vector3(centre.x + side.x * u * extendWidth,
                                                skirtY,
                                                centre.z + side.y * u * extendWidth);

                    int v = i * across + j;

                    body.vertices[v] = transform.InverseTransformPoint(world);
                    body.normals[v] = localNormal;
                    body.tangents[v] = new Vector4(tan.x, tan.y, tan.z, -1f);

                    if (d > deepest) deepest = d;

                    body.uv[v] = new Vector2(whitewater[i], d);

                    Color c = colour;
                    c.a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / feather));
                    body.colors[v] = c;

                    if (!boundsStarted) { worldBounds = new Bounds(world, Vector3.zero); boundsStarted = true; }
                    else worldBounds.Encapsulate(world);
                }
            }

            int idx = 0;
            for (int i = 0; i < count - 1; i++)
            {
                for (int j = 0; j < across - 1; j++)
                {
                    int v0 = i * across + j;
                    int v1 = v0 + 1;
                    int v2 = v0 + across;
                    int v3 = v2 + 1;

                    body.triangles[idx++] = v0;
                    body.triangles[idx++] = v2;
                    body.triangles[idx++] = v1;

                    body.triangles[idx++] = v1;
                    body.triangles[idx++] = v2;
                    body.triangles[idx++] = v3;
                }
            }

            body.surfaceLevel = surfSum / count;
            body.maxDepth = Mathf.Max(0.1f, deepest);
            body.meanFlow = flowSum / count;

            // Surface extent only - see the note in Assemble. One writer for the
            // downward reach, in UCWaterVolume.ExtendDown.
            body.worldBounds = worldBounds;

            return body;
        }

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

            return material;
        }

        private void AttachVolume(WaterBodyData body)
        {
            volume = surfaceObject.AddComponent<UCWaterVolume>();

            volume.kind = (UCWaterVolume.BodyKind)(int)kind;
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

            // A river's surface falls from head to mouth, so it hands the volume the
            // profile the solve already produced. One level for a reach that drops
            // hundreds of metres is a wall of water over half of it and dry over the
            // rest. Still water passes null and keeps its single level.
            if (kind == WaterBodyKind.River)
                volume.SetReachProfile(solvedPoints, solvedSurfaceY);

            // One description drives both the look and the physics. Reading the wave
            // from anywhere else is how a boat ends up floating a hand's width above
            // the visible surface.
            volume.surface = WaterSurfaceParams.Default(body.surfaceLevel);
            volume.surface.waveHeight = water.waveHeightMetres;
            volume.surface.waveLength = water.waveLengthMetres;
            volume.surface.waveSpeed = water.waveSpeed;
            volume.surface.flow = body.meanFlow != Vector2.zero
                ? body.meanFlow
                : water.flowMetresPerSecond;

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
            SnapToGround(course, terrains);
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

        /// <summary>
        /// The course as the carve will actually follow it in XZ: the same centripetal
        /// Catmull-Rom tessellation WorldCourse runs, in world space, with none of the
        /// ground work.
        ///
        /// This exists so the scene view can draw the curve instead of the control
        /// polygon. Drawing straight lines between the nodes was the reason the river
        /// looked like "a separate object" from its own spline - the line on screen was
        /// the polygon, and the trench was the curve through it, and on a bend those
        /// are metres apart. Now both come out of the same function.
        ///
        /// Deliberately does NOT sample the ground or run the valley fit, because this
        /// is called from a scene-view repaint, which happens as fast as the mouse
        /// moves. It is list arithmetic and nothing else.
        [System.NonSerialized]
        private List<Vector3> cachedPreviewCurve;
        [System.NonSerialized]
        private int cachedPreviewCourseHash;
        [System.NonSerialized]
        private Vector3 cachedPreviewTransformPos;

        /// <summary>
        /// The curve the water body will follow, interpolated and in world space.
        /// </summary>
        public List<Vector3> PreviewCourseWorld()
        {
            if (course == null || course.Count < 2) return null;

            int currentHash = course.points.Count;
            if (course.points.Count > 0)
            {
                currentHash ^= course.points[0].GetHashCode();
                currentHash ^= course.points[course.points.Count - 1].GetHashCode();
            }

            if (cachedPreviewCurve != null && cachedPreviewCourseHash == currentHash && cachedPreviewTransformPos == transform.position)
            {
                return cachedPreviewCurve;
            }

            Polyline curve = new Polyline(course.points.Count);
            for (int i = 0; i < course.points.Count; i++)
                curve.Add(course.points[i]);

            curve = SplineOperations.Smooth(curve, SmoothMethod.MovingAverage, 4);
            curve = SplineOperations.Subdivide(curve, 4);

            List<Vector3> worldPoints = new List<Vector3>(curve.points.Count);
            for (int i = 0; i < curve.points.Count; i++)
                worldPoints.Add(transform.TransformPoint(curve.points[i]));

            cachedPreviewCurve = worldPoints;
            cachedPreviewCourseHash = currentHash;
            cachedPreviewTransformPos = transform.position;

            return worldPoints;
        }

        /// <summary>Total course length in world metres, for the inspector readout.</summary>
        public float CourseLengthMetres()
        {
            if (course == null || course.Count < 2) return 0f;

            float total = 0f;
            for (int i = 1; i < course.Count; i++)
                total += Vector3.Distance(transform.TransformPoint(course.points[i - 1]),
                                          transform.TransformPoint(course.points[i]));

            return total;
        }

        /// <summary>
        /// Estimated surface drop over the whole course, in metres. Negative means
        /// the course was drawn uphill - worth reporting, because the fix is to
        /// reverse the points rather than to change any setting.
        /// </summary>
        public float CourseDropMetres()
        {
            if (course == null || course.Count < 2) return 0f;

            Terrain[] terrains = Terrain.activeTerrains;

            Vector3 first = transform.TransformPoint(course.points[0]);
            Vector3 last = transform.TransformPoint(course.points[course.Count - 1]);

            return SampleGround(terrains, first, first.y) - SampleGround(terrains, last, last.y);
        }

        // =====================================================================
        //  PER-NODE READOUTS  (T23)
        //
        //  A road node in Road & City Master tells you its grade to the node either
        //  side of it, and that one number is what a designer actually steers a
        //  course with: not "the river drops 240 m overall" but "this reach is at
        //  9%, which is a rapid, and the one before it is at 0.3%, which is a
        //  pond". The same reading is what makes a hand-drawn river fixable -
        //  you can see which single node is wrong.
        //
        //  The math lives here rather than in the inspector for two reasons. It
        //  is the body's own property, so the inspector should be displaying it
        //  and not deciding it; and a method on the component can be asserted by
        //  the batchmode self-test, which cannot drive a scene GUI. Anything the
        //  editor computes for itself is untestable by construction.
        // =====================================================================

        /// <summary>Where node <paramref name="index"/> sits in the world. The course is
        /// stored local, so every reader needs this and none should transform by hand.</summary>
        public Vector3 NodeWorldPoint(int index)
        {
            if (course == null || index < 0 || index >= course.Count) return transform.position;

            return transform.TransformPoint(course.points[index]);
        }

        /// <summary>Moves node <paramref name="index"/> to a world position, keeping the
        /// stored course local. Paired with NodeWorldPoint so the two never disagree.</summary>
        public void SetNodeWorldPoint(int index, Vector3 world)
        {
            if (course == null || index < 0 || index >= course.Count) return;

            course.points[index] = transform.InverseTransformPoint(world);
            InvalidateSolvedCourse();
        }

        /// <summary>
        /// The water surface elevation at node <paramref name="index"/>: what the river
        /// is actually at there, not what the point's Y happens to hold.
        /// </summary>
        /// <remarks>
        /// Reported before the solve's downhill and containment passes, so a node sitting
        /// in a rise reads as the uphill step it is instead of the flattened value the
        /// water ends up at. That is the more useful of the two readings, because the fix
        /// is to move that node and this is the number that says which one to move.
        /// </remarks>
        public float NodeSurfaceY(int index)
        {
            return RawSurfaceAt(NodeWorldPoint(index));
        }

        /// <summary>
        /// Node <paramref name="index"/> on the water surface - the point moved vertically
        /// onto the elevation the river has there.
        /// </summary>
        /// <remarks>
        /// Every scene-view marker and every grade reading uses this and not the stored
        /// point, so a node is drawn where its water is. The stored Y is meaningless in
        /// the ground-following modes, and drawing markers at it left the whole spline
        /// hanging in the air above its own river or buried under it - a road laid out
        /// with floating nodes, which is not how a course is laid out at all.
        /// </remarks>
        public Vector3 NodeSurfacePoint(int index)
        {
            Vector3 w = NodeWorldPoint(index);
            w.y = NodeSurfaceY(index);
            return w;
        }

        /// <summary>
        /// Grade in percent from node <paramref name="index"/> to the next node - rise
        /// over run, times a hundred, the surveyor's figure. Negative means that reach
        /// climbs, which for a river is the one reading that is always a mistake.
        /// Zero when there is no next node, or when the two share a position.
        /// </summary>
        public float NodeGradePercentToNext(int index)
        {
            if (course == null || index < 0 || index + 1 >= course.Count) return 0f;

            return GradePercentBetween(NodeSurfacePoint(index), NodeSurfacePoint(index + 1));
        }

        /// <summary>Grade in percent arriving at node <paramref name="index"/> from the
        /// previous one. Same sign convention: negative climbs.</summary>
        public float NodeGradePercentFromPrev(int index)
        {
            if (course == null || index - 1 < 0 || index >= course.Count) return 0f;

            return GradePercentBetween(NodeSurfacePoint(index - 1), NodeSurfacePoint(index));
        }

        /// <summary>
        /// Rise over horizontal run as a percentage. Run is measured on the XZ plane and
        /// not as the straight-line distance: a 45 degree drop is 100%, not 71%, and
        /// using the slope distance would quietly compress every steep reach.
        /// </summary>
        private static float GradePercentBetween(Vector3 from, Vector3 to)
        {
            float run = Vector2.Distance(new Vector2(from.x, from.z), new Vector2(to.x, to.z));
            if (run < 1e-4f) return 0f;

            return ((to.y - from.y) / run) * 100f;
        }

        /// <summary>
        /// The body joined at node <paramref name="index"/>, or null. Only the two ends
        /// of a course can be junctions - a river meets another body where it starts or
        /// where it finishes, never in the middle - so this answers for those and
        /// nothing else. Includes the junction the descent walk found on the last build,
        /// because a connection that was discovered is still a connection and the
        /// designer needs to see it to know they need not author it.
        /// </summary>
        public UCWaterBody JunctionAt(int index)
        {
            if (kind != WaterBodyKind.River || course == null || course.Count < 2) return null;

            if (index == 0) return ResolveJoin(false);
            if (index == course.Count - 1) return ResolveJoin(true);

            return null;
        }

        /// <summary>Whether node <paramref name="index"/> is an end of the course, and so
        /// a node that MAY carry a junction. Drawn larger than a plain node either way,
        /// the way a road's connection node is.</summary>
        public bool IsEndNode(int index)
        {
            if (course == null || course.Count == 0) return false;

            return index == 0 || index == course.Count - 1;
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
