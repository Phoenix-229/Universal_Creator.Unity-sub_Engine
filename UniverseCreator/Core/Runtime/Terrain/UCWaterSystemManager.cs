// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWaterSystemManager.cs
//  Purpose: The control surface for every drop of water in one world. Sits on the
//           WaterSystem child and owns the two ways water can exist - placed by
//           hand, or produced by the graph - plus the settings each of them uses.
//
//  WHY THIS EXISTS
//  ---------------
//  Water had two entry points and no meeting place. A hand-placed body was
//  reachable only through the GameObject menu, and the graph path was reachable
//  only by opening the graph and finding the output node. The WaterSystem object
//  that both of them end up under held nothing at all, so selecting it told the
//  user nothing and let them change nothing.
//
//  This is that meeting place. It does not replace either path: the bodies are
//  still UCWaterBody components and the graph still owns its nodes. What it adds
//  is one object that can say which path is live, push shared settings into both,
//  and act on every body at once.
//
//  WHAT THE GRAPH DECIDES, AND WHAT THIS DOES
//  ------------------------------------------
//  Every drop of water in the world is a UCWaterBody, whether a user dropped it from
//  the menu or the Hydrology Spawner produced it from the drainage solve. So there is
//  no second kind of water to override after the fact: a body's own inspector is the
//  place its shape, depth and material are decided, and this object's job is the two
//  questions no single body can answer.
//
//  Which path is allowed to exist - so hand-placed water can be switched off while a
//  generated world is being tuned, and the generator can be switched off while a
//  hand-built scene is being finished, without deleting either.
//
//  Where sea level is - one elevation that the ocean body's surface, the beach band
//  in the biome splat, and the graph all have to agree on. Two answers to that
//  question is the untextured shoreline: sand painted at a height the water never
//  reached.
//
//  Runtime assembly. Nothing here references UnityEditor: this component ships in
//  a player build, and the runtime assembly compiles before the editor one, so a
//  single editor-only reference here would take the whole toolkit's menus with it.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
// The Hydrology Spawner lives in a child namespace. C# searches enclosing
// namespaces implicitly, never nested ones, so this using is required.
using UniverseCreator.Nodes.Creators;
// Same reason, for the biome splat node whose beach band reads the sea level, and
// for the ocean-level node that turns it into a mask and a shoreline.
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
// Same again, for the two nodes that cut the land at the waterline - the wave notch
// and the near-shore shelf. They measure from sea level, so they are pushed to as well.
using UniverseCreator.Nodes.Modifiers;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level water control. Add it to the WaterSystem child; the world
    /// hierarchy created by the GameObject menu already has one.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Water System Manager")]
    [DisallowMultipleComponent]
    public partial class UCWaterSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  SOURCE
        // =====================================================================

        /// <summary>Which of the two water paths is allowed to produce water.</summary>
        public enum WaterSource
        {
            /// <summary>Only bodies placed by hand. The Hydrology Spawner is ignored.</summary>
            HandPlaced = 0,

            /// <summary>Only what the Hydrology Spawner produces. Hand-placed bodies
            /// are switched off.</summary>
            NodeGraph = 1,

            /// <summary>Both. A hand-placed lake sits inside a generated world.</summary>
            Both = 2,

            /// <summary>No water at all, without deleting anything.</summary>
            Disabled = 3
        }

        [Header("Source")]

        [Tooltip("Which path produces water. Hand Placed is the bodies you dropped " +
                 "yourself; Node Graph is what the Hydrology Spawner traces from the " +
                 "drainage solve. Either setting disables the other side's bodies " +
                 "rather than deleting them, so the choice is reversible.")]
        public WaterSource source = WaterSource.Both;

        [Tooltip("Layer every generated water surface is put on. Water usually gets " +
                 "its own layer so a camera can exclude it, an underwater volume can " +
                 "find it, and character rays can pass through it.")]
        public int waterLayer;

        [Tooltip("Keep the world's Water Layer in step with the one above. Off lets " +
                 "the world root and this component disagree, which is almost never " +
                 "what you want.")]
        public bool driveWorldWaterLayer = true;

        // =====================================================================
        //  SEA LEVEL
        //
        //  The one elevation the whole world's shoreline is measured from. It is
        //  here, and not on a node, because three separate things need the same
        //  answer: the ocean body places its surface from it, the biome splat paints
        //  its beach band at it, and the graph's ocean-level nodes read it. Held as a
        //  fraction of the world's max height rather than in metres so it survives a
        //  world being made taller.
        //
        //  Not authored blind either: SyncSeaLevelFromOcean locks it onto the sea's
        //  real settled surface, so the number here and the water on screen cannot
        //  drift apart.
        // =====================================================================

        [Header("Sea Level")]

        [Tooltip("Height that counts as sea level, as a fraction of the world's max " +
                 "height. 0.35 on a 600 m world is 210 m. Push To Graph writes it into " +
                 "the nodes that paint the beach at the waterline.")]
        [Range(0f, 1f)]
        public float nodeSeaLevel = 0.35f;

        // REMOVED: ten more "node path" fields - surface offset, mesh resolution, min
        // depth, min area, edge feather, Manning roughness, hydraulic radius cap, max
        // flow speed, horizon extent, physics volume - and the whole "Applied To Graph
        // Water After Build" override block below them. Both existed to drive the two
        // graph water output nodes, which built a throwaway slab per chunk per body and
        // have been deleted; every one of those numbers now lives on the UCWaterBody it
        // describes, where it is one body's setting instead of a world-wide default
        // applied to water the user cannot select.

        // =====================================================================
        //  DEFAULTS FOR NEW HAND-PLACED BODIES
        // =====================================================================

        [Header("Defaults For New Bodies")]

        [Tooltip("Bed source a newly created body starts with. Terrain reads the " +
                 "ground under it; Flat Floor is for a pool or a body placed before " +
                 "any terrain exists.")]
        public UCWaterBody.BedSource newBodyBedSource = UCWaterBody.BedSource.Terrain;

        [Tooltip("When a newly created body rebuilds itself.")]
        public UCWaterBody.BuildTrigger newBodyBuildTrigger = UCWaterBody.BuildTrigger.Automatic;

        [Tooltip("Scales how hard a current pushes floating bodies. 1 is physical.")]
        [Range(0f, 4f)]
        public float defaultFlowForce = 1f;

        [Tooltip("Extra viscous damping for thick water - mud, tar, a swamp.")]
        [Range(0.2f, 12f)]
        public float defaultViscosityScale = 1f;

        [Tooltip("New bodies emit ripples when something moves in them.")]
        public bool defaultEmitRipples = true;

        // =====================================================================
        //  INTERACTIVE DYNAMICS (play-mode activator)
        //
        //  The per-body settings above (Emit Ripples, Underwater View, Flow
        //  Force) are copied onto each body's UCWaterVolume at build. But two of
        //  the dynamics need a SCENE-LEVEL object to be visible, and nothing was
        //  creating it in a real play session - only the self-test did. So a
        //  world could have Emit Ripples ticked on every body and still show
        //  nothing, because the ripple simulation those bodies feed did not
        //  exist (UCWaterRippleSim.Active was null, and every emitter early-outs
        //  on that). These opt-in toggles are that missing activator: on Play the
        //  manager ensures the one ripple sim exists, and can put the underwater
        //  post effect on the active camera. Buyer-object components (buoyancy,
        //  swimming character) are still added by the developer to THEIR objects
        //  - those cannot be auto-attached to objects that do not exist yet.
        // =====================================================================

        [Header("Interactive Dynamics (Play Mode)")]

        [Tooltip("On Play, ensure the single scene-wide ripple simulation exists " +
                 "so that bodies with Emit Ripples actually show interaction " +
                 "ripples. Without it that tick does nothing at runtime.")]
        public bool spawnRippleSim = true;

        [Tooltip("On Play, add the underwater view post effect to the main camera " +
                 "so bodies with Underwater View tint and fog the screen when the " +
                 "camera goes below the surface. Leave off if you drive the " +
                 "camera effect yourself.")]
        public bool underwaterOnMainCamera = false;

        // =====================================================================
        //  SHARED MATERIAL DESCRIPTION
        // =====================================================================

        [Header("Shared Water Description")]

        [Tooltip("One description every body can be pushed to share: colour, depth " +
                 "fade, foam, caustics, waves, ripple response. The same description " +
                 "the texture graph produces, filled in by hand.")]
        public WaterProduct sharedWater = new WaterProduct();

        [Tooltip("Overrides the material entirely for bodies pushed from here. For a " +
                 "project that authored its own water shader and wants only the " +
                 "geometry and the physics from this system.")]
        public Material sharedMaterialOverride;

        // =====================================================================
        //  QUERIES
        // =====================================================================

        /// <summary>
        /// True when the Hydrology Spawner is allowed to produce water. Read by the
        /// world before it runs the spawn, and by the enable pass below, so switching
        /// this off stops generated water being made AND takes down what is already
        /// standing - without touching a single hand-placed body.
        /// </summary>
        public bool NodeWaterEnabled
        {
            get { return source == WaterSource.NodeGraph || source == WaterSource.Both; }
        }

        /// <summary>True when hand-placed bodies are allowed to build.</summary>
        public bool HandPlacedEnabled
        {
            get { return source == WaterSource.HandPlaced || source == WaterSource.Both; }
        }

        /// <summary>Every body under this object, generated or hand-placed, including
        /// disabled ones.</summary>
        public UCWaterBody[] Bodies
        {
            get { return GetComponentsInChildren<UCWaterBody>(true); }
        }

        /// <summary>Bodies that have actually built a mesh.</summary>
        public int BuiltBodyCount
        {
            get
            {
                UCWaterBody[] bodies = Bodies;
                int n = 0;
                for (int i = 0; i < bodies.Length; i++)
                    if (bodies[i] != null && bodies[i].LastBuild != null) n++;
                return n;
            }
        }

        /// <summary>Vertices across every built hand-placed body, for the readout.</summary>
        public int TotalVertexCount
        {
            get
            {
                UCWaterBody[] bodies = Bodies;
                int n = 0;
                for (int i = 0; i < bodies.Length; i++)
                    if (bodies[i] != null) n += bodies[i].BuiltVertexCount;
                return n;
            }
        }

        /// <summary>Triangles across every built hand-placed body.</summary>
        public int TotalTriangleCount
        {
            get
            {
                UCWaterBody[] bodies = Bodies;
                int n = 0;
                for (int i = 0; i < bodies.Length; i++)
                    if (bodies[i] != null) n += bodies[i].BuiltTriangleCount;
                return n;
            }
        }

        /// <summary>The world this water belongs to, or null when it is loose in a scene.</summary>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        // =====================================================================
        //  LIFECYCLE
        // =====================================================================

        private void OnEnable()
        {
            SyncWorldLayer();
            ApplySourceToBodies();

            // Edit mode is event-driven, never a per-frame ExecuteAlways loop: push the
            // current weather to the water once here so the scene view shows a stormed or
            // frozen surface after a change, without keeping the editor hot. Play mode
            // drives it per frame from the driver's Update instead. (Weather drive)
            ApplyWeatherEvent();

            // Same event-driven reasoning: resolve the quality tier and push its water
            // budget once here (ripple tier, wave-train ceiling, underwater extras) so the
            // scene view already reflects the chosen tier. Play mode re-applies on a tier
            // change from the driver's Update. (Quality drive)
            ApplyQualityEvent();

            // Interactive dynamics activator. Only in play: the ripple sim allocates
            // GPU state and the underwater effect drives a live camera, neither of
            // which belongs in an idle editor scene. This is what makes the per-body
            // Emit Ripples / Underwater View toggles actually do something at runtime.
            if (Application.isPlaying) EnsureInteractiveDynamics();
        }

        private void OnValidate()
        {
            waterLayer = Mathf.Clamp(waterLayer, 0, 31);

            if (isActiveAndEnabled)
            {
                SyncWorldLayer();
                ApplySourceToBodies();
                ApplyWeatherEvent();
                ApplyQualityEvent();
            }
        }

        // =====================================================================
        //  ACTIONS
        // =====================================================================

        /// <summary>
        /// Creates the scene-level dynamics objects the per-body toggles depend on.
        /// Idempotent and play-mode only. The ripple sim is a singleton
        /// (<see cref="UCWaterRippleSim.Active"/>): if the scene already has one it is
        /// left alone, otherwise one is added to this manager so that any body with
        /// Emit Ripples has a sim to feed - without it every emitter early-outs on a
        /// null Active and the tick looks broken. The underwater view is a camera post
        /// effect; when asked, it is added to the main camera (its
        /// <c>RequireComponent(Camera)</c> makes that the correct host) so a body with
        /// Underwater View tints the screen below the surface. Nothing here touches
        /// buoyancy or the swimming character - those attach to the developer's own
        /// floating and swimming objects, which do not exist at world-build time.
        /// </summary>
        public void EnsureInteractiveDynamics()
        {
            if (spawnRippleSim && UCWaterRippleSim.Active == null)
            {
                if (GetComponent<UCWaterRippleSim>() == null)
                    gameObject.AddComponent<UCWaterRippleSim>();
            }

            if (underwaterOnMainCamera)
            {
                Camera cam = Camera.main;
                if (cam != null && cam.GetComponent<UCUnderwaterView>() == null)
                    cam.gameObject.AddComponent<UCUnderwaterView>();
            }
        }

        /// <summary>
        /// Enables or disables bodies to match the source setting.
        ///
        /// Each body is asked which path it came from before it is judged, by its
        /// name: HydrologyGenerator names everything it produces, and that name is
        /// already the test the generator itself uses to decide what it owns and may
        /// cull. Without that question this method could only enable or disable ALL
        /// water, so choosing "Node Graph" switched off the generated rivers it was
        /// asking for along with the hand-placed lake it was trying to hide.
        ///
        /// Disabling the component rather than the GameObject on purpose: the
        /// object keeps its name, its transform and its spline points, so turning
        /// a path back on restores exactly what was there. UCWaterBody clears its own
        /// surface in OnDisable, so nothing is left standing.
        /// </summary>
        public void ApplySourceToBodies()
        {
            UCWaterBody[] bodies = Bodies;

            bool generated = NodeWaterEnabled;
            bool handPlaced = HandPlacedEnabled;

            for (int i = 0; i < bodies.Length; i++)
            {
                UCWaterBody b = bodies[i];
                if (b == null) continue;

                bool wanted = HydrologyGenerator.IsGeneratedBodyName(b.name)
                                  ? generated
                                  : handPlaced;

                if (b.enabled != wanted) b.enabled = wanted;
            }
        }

        /// <summary>Rebuilds one body. Returns false when there was nothing to build.</summary>
        public static bool RebuildBody(UCWaterBody body)
        {
            if (body == null || !body.enabled) return false;
            body.Rebuild();
            return true;
        }

        /// <summary>
        /// Rebuilds every hand-placed body and returns how many were rebuilt.
        /// The editor runs this one body per update rather than in a loop, so a
        /// world full of rivers does not freeze the editor; this method is the
        /// straight-line version for scripts and play mode.
        /// </summary>
        public int RebuildAll()
        {
            UCWaterBody[] bodies = Bodies;
            int n = 0;
            for (int i = 0; i < bodies.Length; i++)
                if (RebuildBody(bodies[i])) n++;
            return n;
        }

        /// <summary>Drops every built surface without deleting the bodies.</summary>
        public void ClearAll()
        {
            UCWaterBody[] bodies = Bodies;
            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null) bodies[i].Clear();
        }

        /// <summary>Re-reads the ground under every spline point of every body.</summary>
        public void SnapAllToGround()
        {
            UCWaterBody[] bodies = Bodies;
            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null) bodies[i].SnapCourseToGround();
        }

        /// <summary>
        /// Pushes the physics defaults onto every existing body. Separate from the
        /// defaults themselves, because a default is what a NEW body starts with -
        /// silently rewriting bodies the user already tuned would be the opposite of
        /// a default.
        /// </summary>
        public void ApplyPhysicsDefaultsToAll()
        {
            UCWaterBody[] bodies = Bodies;
            for (int i = 0; i < bodies.Length; i++)
            {
                UCWaterBody b = bodies[i];
                if (b == null) continue;

                b.flowForce = defaultFlowForce;
                b.viscosityScale = defaultViscosityScale;
                b.emitRipples = defaultEmitRipples;
            }
        }

        /// <summary>Pushes the shared description onto every body, then rebuilds them.</summary>
        public void ApplySharedDescriptionToAll()
        {
            UCWaterBody[] bodies = Bodies;
            for (int i = 0; i < bodies.Length; i++)
            {
                UCWaterBody b = bodies[i];
                if (b == null) continue;

                b.useOwnMaterial = true;
                b.water = CloneShared();
                if (sharedMaterialOverride != null) b.materialOverride = sharedMaterialOverride;
            }
        }

        /// <summary>Applies the new-body defaults to a body this manager just created.</summary>
        public void ApplyNewBodyDefaults(UCWaterBody body)
        {
            if (body == null) return;

            // The bed source is not forced onto a Pool: a pool preset deliberately
            // chooses a flat floor because there is no terrain under a built basin,
            // and a default that overrode that would break the preset it just used.
            if (body.kind != WaterBodyKind.Pool) body.bedSource = newBodyBedSource;

            body.buildTrigger = newBodyBuildTrigger;
            body.flowForce = defaultFlowForce;
            body.viscosityScale = defaultViscosityScale;
            body.emitRipples = defaultEmitRipples;
        }

        // =====================================================================
        //  GRAPH
        // =====================================================================

        /// <summary>
        /// Writes the sea level into every node in a graph that measures anything from
        /// it, and returns how many nodes were touched.
        ///
        /// The shoreline is one elevation, so it has to come from one number. Six node
        /// types read it and none of them can ask the water where it ended up: all of
        /// them run on a worker thread while the terrain is being built, before any
        /// water body exists. So it is pushed to them from here, and read back from
        /// none of them - a value with one writer cannot disagree with itself.
        ///
        /// Ocean Level turns it into the ocean mask, the depth field and the shoreline
        /// band that textures the beach. Biome Splat paints its sand at it. The
        /// Hydrology Spawner, River Network Trace, River Fill and Lake Fill decide from
        /// it where water may stand at all. When any of them and the ocean body's real
        /// surface do not agree, the sand lands at an elevation the water never reached
        /// and the true waterline stays untextured - which is the white shore of 4.4 -
        /// and the channels cut for rivers stop short of the sea that was meant to fill
        /// them.
        /// </summary>
        public int ApplyToGraph(Graph graph)
        {
            return WalkSeaLevelReaders(graph, true, nodeSeaLevel);
        }

        /// <summary>
        /// How many nodes in a graph read sea level, without writing anything. The
        /// authoring surface needs the number to say whether pushing would reach
        /// anything; it must not have its own list of which node types count, or the
        /// count and the write would eventually disagree.
        /// </summary>
        public static int CountSeaLevelReaders(Graph graph)
        {
            return WalkSeaLevelReaders(graph, false, 0f);
        }

        private static int WalkSeaLevelReaders(Graph graph, bool write, float seaLevel)
        {
            if (graph == null || graph.nodes == null) return 0;

            int touched = 0;

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null) continue;

                OceanLevelNode ocean = node as OceanLevelNode;
                if (ocean != null)
                {
                    if (write)
                    {
                        ocean.seaLevel = seaLevel;

                        // A field write on its own does not invalidate a cached result,
                        // and an unchanged mask is the exact fault being fixed here.
                        ocean.MarkDirty();
                    }
                    touched++;
                    continue;
                }

                BiomeSplatOutputNode splat = node as BiomeSplatOutputNode;
                if (splat != null)
                {
                    if (write)
                    {
                        splat.seaLevel = seaLevel;
                        splat.MarkDirty();
                    }
                    touched++;
                    continue;
                }

                // EVERY NODE THAT MEASURES FROM THE SHORELINE, NOT JUST THE TWO THAT
                // PAINT IT. The four below decide where water STANDS, and each carries
                // its own copy of the one elevation: the spawner ends its rivers at it
                // and refuses lakes below it, River Network Trace stops a course at it,
                // River Fill hands the sea's level to the reach at the mouth, Lake Fill
                // will not flood a basin at or under it.
                //
                // Left out of this walk they kept whatever the user last typed while the
                // ocean settled somewhere else entirely - so the graph carved beds and
                // flooded basins against one shoreline and the water arrived at another.
                // That is the coastal half of "trenches with no water in them": a river
                // mouth cut to 0.35 of world height under a sea standing at 0.05 is a
                // channel that the ocean never reaches, and a lake basin rejected for
                // sitting below a sea level that was never true is a basin left dry.
                HydrologyNode hydrology = node as HydrologyNode;
                if (hydrology != null)
                {
                    if (write) { hydrology.seaLevel = seaLevel; hydrology.MarkDirty(); }
                    touched++;
                    continue;
                }

                RiverNetworkTraceNode trace = node as RiverNetworkTraceNode;
                if (trace != null)
                {
                    if (write) { trace.seaLevel = seaLevel; trace.MarkDirty(); }
                    touched++;
                    continue;
                }

                RiverFillNode riverFill = node as RiverFillNode;
                if (riverFill != null)
                {
                    if (write) { riverFill.seaLevel = seaLevel; riverFill.MarkDirty(); }
                    touched++;
                    continue;
                }

                LakeFillNode lakeFill = node as LakeFillNode;
                if (lakeFill != null)
                {
                    if (write) { lakeFill.seaLevel = seaLevel; lakeFill.MarkDirty(); }
                    touched++;
                    continue;
                }

                // THE TWO THAT CUT THE LAND AT THE WATERLINE
                // Coastal Erosion cuts the wave notch and Coastal Shelf Shaper flattens
                // the near-shore bed, both measured from their own seaLevel. Left out of
                // this walk the notch is cut into a hillside the sea never reaches and
                // the shelf is drowned or left standing in the air - a shoreline the
                // water disagrees with, in terrain rather than in a mask.
                CoastalErosionNode erosion = node as CoastalErosionNode;
                if (erosion != null)
                {
                    if (write) { erosion.seaLevel = seaLevel; erosion.MarkDirty(); }
                    touched++;
                    continue;
                }

                CoastalShelfShaperNode shelf = node as CoastalShelfShaperNode;
                if (shelf != null)
                {
                    if (write) { shelf.seaLevel = seaLevel; shelf.MarkDirty(); }
                    touched++;
                }
            }

            return touched;
        }

        // Below this the beach would be repainted at a sea level no eye could separate
        // from the one already on the terrain. 0.001 of the height range is 0.6 m on a
        // 600 m world.
        private const float SeaLevelEpsilon = 0.001f;

        /// <summary>
        /// Locks the normalised sea level onto the ocean's real surface height.
        ///
        /// The sea's level is authored as a 0..1 fraction of the height range and baked
        /// into the ocean body's transform in metres. Those are one elevation in two
        /// units, and if the user drags the ocean body the metres move while the fraction
        /// does not - so the sand paints where the water is not, the untextured shoreline.
        /// This locks the fraction back onto wherever the body actually stands.
        ///
        /// Returns true when the value actually moved, which means the splatmaps already
        /// on the terrain were painted from a stale sea level and owe a repaint.
        /// </summary>
        public bool SyncSeaLevelFromOcean(UniverseCreatorObject uc)
        {
            if (uc == null || uc.maxHeightMeters <= 0f) return false;

            UCWaterBody ocean = FindOcean();
            if (ocean == null) return false;

            // Read off the body with the same expression every other consumer of a
            // surface uses: a second copy of that calculation is a second answer
            // waiting to disagree.
            float surfaceY = ocean.transform.position.y + ocean.surfaceOffsetMetres;

            float normalised = Mathf.Clamp01(
                (surfaceY - uc.transform.position.y) / uc.maxHeightMeters);

            if (Mathf.Abs(normalised - nodeSeaLevel) <= SeaLevelEpsilon) return false;

            nodeSeaLevel = normalised;
            ApplyToGraph(uc.graph);
            return true;
        }

        /// <summary>
        /// Locks the sea level onto a fraction the build has already committed to - the
        /// level a fresh procedural ocean is about to be placed at - when there is no
        /// ocean body standing yet for <see cref="SyncSeaLevelFromOcean"/> to read.
        ///
        /// This is the BEFORE-the-bake half of the same reconciliation. SyncSeaLevelFromOcean
        /// runs AFTER the terrain settles and, when it finds the two out of step, forces a
        /// full second RebuildAll to repaint the beach - which was firing on nearly every
        /// Generate, because the manager began at its 0.35 default while the graph placed the
        /// ocean at the Hydrology node's own fraction, so they always disagreed and the world
        /// "regenerated again the moment it finished". Adopting the spawn's fraction here, and
        /// pushing it to the shoreline readers, means the FIRST bake already paints the true
        /// waterline and the post-settle sync has nothing to move.
        ///
        /// Deliberately no FindOcean: this is the path taken precisely when no ocean exists
        /// yet. The clamp-and-apply is shared with SyncSeaLevelFromOcean rather than copied,
        /// so the two cannot drift. Returns true when the value moved.
        /// </summary>
        public bool AdoptNodeSeaLevel(float fraction, Graph graph)
        {
            float clamped = Mathf.Clamp01(fraction);
            if (Mathf.Abs(clamped - nodeSeaLevel) <= SeaLevelEpsilon) return false;

            nodeSeaLevel = clamped;
            ApplyToGraph(graph);
            return true;
        }

        // The world's sea, if it has one. Matched on kind, not on name, so a renamed
        // ocean is still found and a lake someone called "Ocean" is not.
        private UCWaterBody FindOcean()
        {
            UCWaterBody[] bodies = GetComponentsInChildren<UCWaterBody>(true);
            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null && bodies[i].kind == WaterBodyKind.Ocean) return bodies[i];

            return null;
        }

        /// <summary>
        /// Every Hydrology Spawner in a graph that is actually wired, which is the one
        /// honest answer to "does this world generate its own water".
        ///
        /// Presence is not enough. A spawner dropped in a graph with nothing connected to
        /// it at all has not been asked for anything, so counting it would tell a body it
        /// is graph-linked when nothing will ever re-fit it - and it would break the
        /// user's rule that water appears only where the graph asks for it.
        ///
        /// Wired means either half: its mask outputs reach an Output (walked backwards,
        /// the same reachability the evaluator uses), or it is connected at all - see
        /// SpawnerIsWired for why the object half cannot be judged by mask reachability.
        ///
        /// One test with two callers: the world reads the first spawner to run it, and
        /// a body reads the count to know whether it is graph-linked. Two copies of "is
        /// it connected" would eventually give two answers.
        /// </summary>
        public static List<HydrologyNode> ConnectedHydrology(Graph graph)
        {
            List<HydrologyNode> found = new List<HydrologyNode>();
            if (graph == null || graph.nodes == null) return found;

            HashSet<string> reached = ReachedNodeIds(graph);

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                HydrologyNode h = graph.nodes[i] as HydrologyNode;
                if (h == null) continue;
                if (reached.Contains(h.id) || SpawnerIsWired(graph, h)) found.Add(h);
            }
            return found;
        }

        /// <summary>
        /// True when a Hydrology Spawner is wired into the graph at all: its Height input
        /// is fed, or one of its mask outputs is consumed.
        /// </summary>
        /// <remarks>
        /// WHY THE SPAWNER IS NOT JUDGED BY OUTPUT-REACHABILITY ALONE
        /// Output-reachability is the right question for a node whose product is a
        /// matrix: a Lake Fill that reaches no Output has had its flood thrown away, so
        /// it did nothing. The spawner's product is not a matrix. It places river, lake
        /// and ocean OBJECTS in the scene, reading the finished ground straight off
        /// Terrain.activeTerrains - the graph is not in that path at all. Its two mask
        /// ports are a separate, optional service: they let the same drainage drive
        /// carving and splatting.
        ///
        /// Judged only by whether those optional masks reach an Output, a spawner fed a
        /// Height chain and asked for three rivers produced nothing, because the user had
        /// not also wired the mask half - and there is no wiring that makes a river mask
        /// terminate in a Height Output on its own merits. So the node that exists to
        /// make water sat there with every field filled in, doing nothing, until an
        /// unrelated second chain was built. That is the reported fault in one sentence.
        ///
        /// Being wired is still required. A spawner dropped in a graph and left
        /// completely unconnected has not been asked for anything, and must not flood a
        /// world by its mere presence - which is also the rule the user set: water
        /// appears only where the graph asks for it.
        /// </remarks>
        private static bool SpawnerIsWired(Graph graph, HydrologyNode node)
        {
            if (graph == null || graph.connections == null || node == null) return false;

            for (int i = 0; i < graph.connections.Count; i++)
            {
                Connection c = graph.connections[i];
                if (c.targetNodeId == node.id || c.sourceNodeId == node.id) return true;
            }
            return false;
        }

        /// <summary>
        /// Every node id that reaches an Output, walked backwards from every Output
        /// node. This is the same reachability the evaluator itself uses.
        /// </summary>
        /// <remarks>
        /// One walk, every caller. The spawner lookup and the water-node lookup below
        /// ask the same question about the same graph; written twice they would
        /// eventually give two answers about whether a world generates water.
        ///
        /// SEEDED ON IOutputNode, WHICH IS WHAT THE EVALUATOR SEEDS ON
        /// This used to seed on the OutputNode base class, and that is a narrower set
        /// than the evaluator's: Scatter nodes, Shader Output, and the Lighting, Weather
        /// and Wind outputs implement IOutputNode directly without deriving from
        /// OutputNode. GraphEvaluator collects "n is IOutputNode", so a graph terminating
        /// in one of those was evaluated in full - the masks were built, the ground was
        /// carved - while this walk could not see the terminal, found the Hydrology
        /// Spawner unreached, and reported that the world asks for no procedural water.
        /// ResolveHydrology then returned null and the caller SWEPT: every river, lake
        /// and ocean object removed, on a graph that had just finished drawing them a
        /// riverbed. A spawner with every field correctly filled in and wired to a live
        /// Height chain produced nothing, which is exactly the "the ports do nothing when
        /// I connect them" fault. The doc line above had claimed these two agreed for as
        /// long as they did not.
        /// </remarks>
        private static HashSet<string> ReachedNodeIds(Graph graph)
        {
            HashSet<string> reached = new HashSet<string>();
            if (graph == null || graph.nodes == null || graph.connections == null) return reached;

            Queue<string> pending = new Queue<string>();

            // Seed the walk with every Output node, then follow connections backwards.
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null || !(node is IOutputNode)) continue;
                if (reached.Add(node.id)) pending.Enqueue(node.id);
            }

            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                for (int i = 0; i < graph.connections.Count; i++)
                {
                    // Connection is a struct, so there is nothing to null-check here.
                    Connection c = graph.connections[i];
                    if (c.targetNodeId != current) continue;
                    if (reached.Add(c.sourceNodeId)) pending.Enqueue(c.sourceNodeId);
                }
            }

            return reached;
        }

        /// <summary>
        /// The hydrology settings this world's graph asks for, or null when it asks for
        /// no procedural water at all. What HydrologyGenerator is actually driven by.
        /// </summary>
        /// <remarks>
        /// THREE ANSWERS, IN PRIORITY ORDER
        ///
        /// 1. A Hydrology Spawner reaching an Output. Its numbers win outright - every
        ///    count comes off the node the user placed, and nothing here second-guesses
        ///    it. This is the user's rule: "if connected the number of rivers comes from
        ///    the hydrology node, same for the lake number".
        ///
        /// 2. No spawner, but a water node reaching an Output - a River Network Trace,
        ///    a River Fill, a Lake Fill, an Ocean Level. The graph has plainly asked for
        ///    water, so it gets water, with the counts derived from the world's own size
        ///    by the drainage math below rather than from a node that is not there. This
        ///    is the other half of the same rule: "if the hydrology spawner node is not
        ///    connected then they will use native math".
        ///
        ///    It is per kind, not all-or-nothing. A graph with only river nodes gets
        ///    rivers and stays land-locked; one with an Ocean Level gets the sea. A
        ///    world does not sprout a coastline because it was asked for a stream.
        ///
        /// 3. Neither. No procedural water, and the caller sweeps anything a
        ///    since-disconnected node left standing.
        ///
        /// The answer is a HydrologyNode either way - the real one, or one synthesized
        /// from native math - because that is the whole of what GenerateFromNode reads.
        /// A parallel settings struct would be the same eight fields written twice.
        /// </remarks>
        public static HydrologyNode ResolveHydrology(Graph graph, UniverseCreatorObject world)
        {
            if (graph == null || graph.nodes == null) return null;

            HashSet<string> reached = ReachedNodeIds(graph);

            // 1. The spawner wins whenever one is connected. Same test ConnectedHydrology
            //    applies, so the count in the inspector and the node that actually runs
            //    can never disagree about which spawners are live.
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                HydrologyNode h = graph.nodes[i] as HydrologyNode;
                if (h == null) continue;
                if (reached.Contains(h.id) || SpawnerIsWired(graph, h)) return h;
            }

            // 2. Otherwise, what kinds of water did the graph ask for?
            bool wantRivers = false;
            bool wantLakes = false;
            bool wantOcean = false;

            // THE CONNECTED NODES' OWN SETTINGS, NOT A SECOND SET OF DEFAULTS
            // Only the COUNTS are native math. Everything that decides where the water
            // goes has already been authored - on the very nodes that asked for it - so
            // a spawner synthesized on class defaults would solve a different world from
            // the one the graph carved. The case that made it plain: a River Network
            // Trace with its channel-head area lowered to suit a small world stamps a
            // network and River Carve digs it, while a spawner left on the real-world
            // quarter of a square kilometre finds no qualifying mouth, plans no course,
            // and puts no river object in the trench. Trenches with no water in them,
            // from the procedural nodes alone and with no spawner in sight.
            //
            // First of each kind wins. Two Lake Fills disagreeing about sea level is a
            // graph that already contradicts itself, and picking the later one would
            // only make which contradiction wins depend on node creation order.
            RiverNetworkTraceNode trace = null;
            LakeFillNode lakeFill = null;
            OceanLevelNode oceanLevel = null;

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null || !reached.Contains(node.id)) continue;

                // River Carve and Water Bank Seat are deliberately NOT here. They shape
                // ground for water decided elsewhere; on their own they say nothing
                // about whether any river exists, and a carve fed by a hand-drawn mask
                // must not conjure river objects the user never asked for.
                if (node is RiverNetworkTraceNode || node is RiverFillNode)
                {
                    wantRivers = true;
                    if (trace == null) trace = node as RiverNetworkTraceNode;
                }
                else if (node is LakeFillNode)
                {
                    wantLakes = true;
                    if (lakeFill == null) lakeFill = (LakeFillNode)node;
                }
                else if (node is OceanLevelNode)
                {
                    wantOcean = true;
                    if (oceanLevel == null) oceanLevel = (OceanLevelNode)node;
                }
            }

            if (!wantRivers && !wantLakes && !wantOcean) return null;   // 3.

            HydrologyNode native = new HydrologyNode();

            // Lakes and outlets keep the node's own defaults, which are already a
            // fraction of the world's chunk count - the density IS the native math.
            // Zeroed when no lake node is connected, so the graph gets what it asked
            // for and not a lake district beside it.
            if (!wantLakes)
            {
                native.targetLakeCount = 0;
                native.lakeChunkPercentage = 0f;
                native.lakeOutletChunkPercentage = 0f;
            }

            // The synthesized spawner shares the reached trace's (or lake fill's)
            // drainage lattice, so the lake solve runs on the same grid the author's
            // water nodes did. The old channel-head/max-channel copy went with the
            // spawner's River Mask - the spawner no longer emits one, and the channel
            // network is decided on the River Network Trace itself.
            if (trace != null)
            {
                native.drainageResolution = trace.drainageResolution;
            }
            else if (lakeFill != null)
            {
                native.drainageResolution = lakeFill.drainageResolution;
            }

            if (lakeFill != null)
            {
                native.minLakeDepth = lakeFill.minDepth;
                native.depthNormalise = lakeFill.depthNormalise;
                native.shoreSoftness = lakeFill.shoreSoftness;
            }

            // Sea level has several possible authors and they are all one elevation, so
            // the most specific wins: the Ocean Level node that asked for the sea, then
            // whichever water node states one. Whatever is picked here only stands until
            // the ocean is actually placed - ApplyToGraph then pushes its settled surface
            // back over every one of them, this node included.
            if (oceanLevel != null) native.seaLevel = oceanLevel.seaLevel;
            else if (trace != null) native.seaLevel = trace.seaLevel;
            else if (lakeFill != null) native.seaLevel = lakeFill.seaLevel;

            native.generateOcean = wantOcean;

            return native;
        }

        /// <summary>How many Hydrology Spawners reach an Output. Drives the readout.</summary>
        public static int CountConnectedHydrology(Graph graph)
        {
            return ConnectedHydrology(graph).Count;
        }

        // =====================================================================
        //  INTERNAL
        // =====================================================================

        // A per-body copy, not the manager's own instance. Sharing one reference
        // would mean editing any body's material description silently edited every
        // other body that had ever been pushed from here.
        private WaterProduct CloneShared()
        {
            WaterProduct copy = new WaterProduct();
            if (sharedWater == null) return copy;

            // JsonUtility is the cheapest deep copy that stays correct when
            // WaterProduct gains a field - a hand-written member-by-member copy
            // silently drops anything added later.
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(sharedWater), copy);
            return copy;
        }

        private void SyncWorldLayer()
        {
            if (!driveWorldWaterLayer) return;

            UniverseCreatorObject world = World;
            if (world == null || world.waterLayer == waterLayer) return;

            world.waterLayer = waterLayer;
        }
    }
}
