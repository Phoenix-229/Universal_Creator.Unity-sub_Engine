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

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level water control. Add it to the WaterSystem child; the world
    /// hierarchy created by the GameObject menu already has one.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Water System Manager")]
    [DisallowMultipleComponent]
    public class UCWaterSystemManager : MonoBehaviour
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
                 "height. 0.3 on a 600 m world is 180 m. Push To Graph writes it into " +
                 "the nodes that paint the beach at the waterline.")]
        [Range(0f, 1f)]
        public float nodeSeaLevel = 0.3f;

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
        }

        private void OnValidate()
        {
            waterLayer = Mathf.Clamp(waterLayer, 0, 31);

            if (isActiveAndEnabled)
            {
                SyncWorldLayer();
                ApplySourceToBodies();
            }
        }

        // =====================================================================
        //  ACTIONS
        // =====================================================================

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
        /// The shoreline is one elevation, so it has to come from one number. Two
        /// nodes read it and neither can ask the water where it ended up: both run on
        /// a worker thread while the terrain is being built, before any water body
        /// exists. So it is pushed to them from here, and read back from none of them
        /// - a value with one writer cannot disagree with itself.
        ///
        /// Ocean Level turns it into the ocean mask, the depth field and the shoreline
        /// band that textures the beach. Biome Splat paints its sand at it. When those
        /// two and the ocean body's real surface do not agree, the sand lands at an
        /// elevation the water never reached and the true waterline stays untextured -
        /// which is the white shore of 4.4.
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
        /// The sea's level is not authored: it is derived from the median terrain height
        /// around the world edge, in metres, and baked into the ocean body's transform.
        /// The splat node paints its beach from a 0..1 fraction of the height range.
        /// Those are one elevation in two units, and until they are locked together the
        /// sand paints where the water is not - the untextured shoreline.
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
            // surface uses, rather than recomputing the median here: a second copy of
            // that calculation is a second answer waiting to disagree.
            float surfaceY = ocean.transform.position.y + ocean.surfaceOffsetMetres;

            float normalised = Mathf.Clamp01(
                (surfaceY - uc.transform.position.y) / uc.maxHeightMeters);

            if (Mathf.Abs(normalised - nodeSeaLevel) <= SeaLevelEpsilon) return false;

            nodeSeaLevel = normalised;
            ApplyToGraph(uc.graph);
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
        /// Every Hydrology Spawner in a graph that actually reaches an Output, which is
        /// the one honest answer to "does this world generate its own water".
        ///
        /// Presence is not enough. A spawner sitting in a graph with nothing plugged
        /// into an Output produces nothing, so counting it would tell a body it is
        /// graph-linked when the graph will never re-fit it - and it would break the
        /// user's rule that water appears only where the graph asks for it. Walked
        /// backwards from every Output node, which is the same reachability the
        /// evaluator itself uses.
        ///
        /// One walk with two callers: the world reads the first spawner to run it, and
        /// a body reads the count to know whether it is graph-linked. Two copies of "is
        /// it connected" would eventually give two answers.
        /// </summary>
        public static List<HydrologyNode> ConnectedHydrology(Graph graph)
        {
            List<HydrologyNode> found = new List<HydrologyNode>();
            if (graph == null || graph.nodes == null || graph.connections == null) return found;

            // Seed the walk with every Output node, then follow connections backwards.
            HashSet<string> reached = new HashSet<string>();
            Queue<string> pending = new Queue<string>();

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null || !(node is OutputNode)) continue;
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

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                HydrologyNode h = graph.nodes[i] as HydrologyNode;
                if (h != null && reached.Contains(h.id)) found.Add(h);
            }
            return found;
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
