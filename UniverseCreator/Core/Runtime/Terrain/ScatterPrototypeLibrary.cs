// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    ScatterPrototypeLibrary.cs
//  Purpose: The asset side of scatter. Scatter nodes emit ScatterInstance records
//           carrying a prototypeIndex and nothing else - they run on worker
//           threads and cannot touch a prefab. This is what that index means.
//
//  Four prototype kinds, because the engine has three separate placement
//  mechanisms and vegetation has four distinct behaviours that do not map onto
//  them one to one:
//
//    trees   -> TerrainData.treePrototypes,   billboarded past treeBillboardDistance
//    shrubs  -> GameObjects with a trigger collider, wind-animated, walk-through
//    details -> TerrainData.detailPrototypes, per-cell densities, wind-animated
//    objects -> real GameObjects or GPU-instanced draws, with solid colliders
//
//  A rock is an object and not a detail because it needs a collider; grass is a
//  detail and not an object because a million GameObjects is not a thing you do.
//
//  WHY SHRUBS ARE THEIR OWN SECTION
//  --------------------------------
//  A bush is neither a tree nor grass and behaving like either one is wrong.
//  Against the wind it is woody: the base does not move and the crown gives,
//  where grass lies over from the root and a tree does not visibly move below
//  the canopy. Against a body walking into it, it is soft: you push through a
//  bush, you do not push through a tree, and you do not collide with grass at
//  all. Those are three different physics states, so they are three different
//  sections - the generator has to know which one it is placing.
//
//    grass   no collider, no rigidbody. Parts visually as you walk through it,
//            entirely in the vertex shader. Colliders on grass would mean tens
//            of thousands of them for something the player never bumps into.
//    shrub   trigger collider, no rigidbody. A trigger and not a solid because
//            the player must be able to walk into it; solid would stop them dead
//            at a plant they should be able to push through. It squeezes aside
//            visually and reports the entry so gameplay can slow the player,
//            rustle, or hide them.
//    tree    solid collider on the prefab, driven by the WindZone. You cannot
//            walk through a trunk.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// How a plant answers to wind and to being walked into. Mirrors the class
    /// constants in UC_Wind.hlsl; the integer values are the ones the shader's
    /// _VegetationClass property takes, so they must not be reordered.
    /// </summary>
    public enum VegetationClass
    {
        Grass = 0,
        Shrub = 1,
        Tree = 2
    }

    [System.Serializable]
    public class TreePrototypeDef
    {
        /// <summary>UC-T06-06: serialized prototype identity, initialized on creation and retained when this roster entry moves; missing/duplicate legacy IDs require explicit initialization/migration.</summary>
        [HideInInspector] public string persistentId = System.Guid.NewGuid().ToString("N");
        public GameObject prefab;

        [Tooltip("How far wind bends this tree. 0 is rigid. This is the only wind input "
               + "Unity's tree renderer accepts, and it drives the canopy only - a trunk "
               + "does not visibly move. Values past ~0.5 start to look like rubber.")]
        [Range(0f, 1f)]
        public float bendFactor = 0.35f;

        [Tooltip("Placement is rejected outside this altitude band, 0-1 of world height.")]
        public float minAltitude;
        public float maxAltitude = 1f;

        [Tooltip("Placement is rejected on ground steeper than this, in degrees.")]
        public float maxSlope = 30f;

        [Tooltip("Trunk width multiplier range.")]
        public Vector2 widthScale = new Vector2(0.85f, 1.15f);

        [Tooltip("Trunk height multiplier range.")]
        public Vector2 heightScale = new Vector2(0.8f, 1.25f);

        [Tooltip("Relative selection weight when a node picks between prototypes.")]
        public float weight = 1f;

        /// <summary>
        /// DEF-6. The prefab's OWN height in metres as it was modelled, measured from its mesh
        /// bounds. 0 = not measured yet. Runtime cache only, never serialized: it is a fact about
        /// the buyer's prefab, not about our asset.
        ///
        /// WHY THIS IS MEASURED AND NEVER ASSUMED. `TreeInstance.heightScale` is a MULTIPLIER on
        /// the prefab's modelled size, so "make this tree 20 m tall" is only expressible as
        /// `20 / (native height)`. That divisor is a property of the buyer's own model, and this
        /// package's hard rule is that no asset's size, path, filename or format may be assumed in
        /// code - content slots ship EMPTY and the buyer brings their own. So it is read off the
        /// mesh at registration time and cached, which also means a buyer swapping a 6 m prefab for
        /// a 30 m one gets correctly-sized trees without touching a dial.
        /// </summary>
        [System.NonSerialized] public float nativeHeightM;

        /// <summary>UC-T06-08: optional full-key size/yaw variation in addition to existing trunk ranges and species allometry.</summary>
        public ScatterPrototypeVariation variation = new ScatterPrototypeVariation();

        // The altitude band and maxSlope above are the author's hard override and
        // they stay: existing worlds have them authored, and an author is entitled
        // to say "not here" regardless of what the ecology concludes. This is the
        // separate question of where the species can actually live, in real units.
        [Tooltip("Where this species can live: its climate, soil, water and stress limits. "
               + "The altitude band and slope limit above remain a hard override on top.")]
        public EcoEnvelope eco = new EcoEnvelope();
    }

    /// <summary>
    /// A bush, shrub or fern: woody enough to hold its base against the wind,
    /// soft enough to walk through. Spawned as a GameObject rather than as a
    /// terrain detail because it needs a collider, and terrain details cannot
    /// carry one.
    /// </summary>
    [System.Serializable]
    public class ShrubPrototypeDef
    {
        /// <summary>UC-T06-06: serialized prototype identity, independent of its roster index; duplicate IDs are rejected.</summary>
        [HideInInspector] public string persistentId = System.Guid.NewGuid().ToString("N");
        public GameObject prefab;

        [Tooltip("Relative selection weight when a node picks between prototypes.")]
        public float weight = 1f;

        // ---- wind -----------------------------------------------------------

        [Tooltip("How hard the wind moves this shrub, on top of the global shrub response. "
               + "The motion model is already shrub-shaped - the lower quarter holds still "
               + "and it needs more wind than grass before it gives - so this is a trim, "
               + "not the difference between a bush and a blade of grass.")]
        [Range(0f, 2f)]
        public float windResponse = 1f;

        [Tooltip("Leaf-scale shiver on top of the bend. Lower for a dense woody bush, "
               + "higher for a light fern.")]
        [Range(0f, 2f)]
        public float flutter = 0.5f;

        [Tooltip("Height of the authored mesh in metres. The shader needs it to tell a "
               + "root vertex from a crown vertex; wrong values make the whole bush slide "
               + "instead of bend.")]
        [Range(0.05f, 10f)]
        public float meshHeight = 1.2f;

        // ---- being walked through -------------------------------------------
        //
        // Whether a trigger is built at all is eco.colliderPolicy (V-27). These three
        // stay because they are the trigger's geometry, which the policy does not carry.

        [Tooltip("Radius of the trigger, in metres. Roughly the visual footprint.")]
        [Range(0.05f, 5f)]
        public float colliderRadius = 0.6f;

        [Tooltip("Height of the trigger, in metres.")]
        [Range(0.05f, 6f)]
        public float colliderHeight = 1.2f;

        [Tooltip("How far this shrub squeezes aside when something walks through it. "
               + "0 makes it ignore the walker entirely.")]
        [Range(0f, 2f)]
        public float squeeze = 1f;

        [Tooltip("Marks the trigger collider's object on this layer, so gameplay can "
               + "filter for vegetation. -1 keeps the prefab's own layer.")]
        public int colliderLayer = -1;

        // ---- placement ------------------------------------------------------

        [Tooltip("Sinks the shrub into the ground by this fraction of its scale, so it "
               + "sits in the soil rather than balancing on it.")]
        [Range(0f, 1f)]
        public float embedDepth = 0.08f;

        [Tooltip("Tilts the shrub toward the surface normal, 0-1.")]
        [Range(0f, 1f)]
        public float normalAlignment = 0.35f;

        /// <summary>UC-T06-08: optional full-key size/yaw variation; legacy/null settings retain the node's authored variation.</summary>
        public ScatterPrototypeVariation variation = new ScatterPrototypeVariation();

        [Tooltip("Rejected on ground steeper than this, in degrees.")]
        public float maxSlope = 40f;

        [Tooltip("Rejected outside this altitude band, 0-1 of world height.")]
        public float minAltitude;
        public float maxAltitude = 1f;

        [Tooltip("Where this species can live: its climate, soil, water and stress limits. "
               + "The altitude band and slope limit above remain a hard override on top. "
               + "Instancing and collider routing live here too, on instancingPolicy and "
               + "colliderPolicy (V-27).")]
        public EcoEnvelope eco = new EcoEnvelope();
    }

    [System.Serializable]
    public class DetailPrototypeDef
    {
        /// <summary>UC-T06-06: serialized prototype identity, independent of its roster index; duplicate IDs are rejected.</summary>
        [HideInInspector] public string persistentId = System.Guid.NewGuid().ToString("N");
        [Tooltip("Mesh prototype. Takes precedence over the billboard texture.")]
        public GameObject prefabMesh;

        [Tooltip("Billboard texture, used when no mesh prototype is set.")]
        public Texture2D billboardTexture;

        [Tooltip("Instances per square metre at full mask weight.")]
        public float densityPerSquareMetre = 4f;

        public Vector2 width = new Vector2(1f, 2f);
        public Vector2 height = new Vector2(1f, 2f);

        [Tooltip("Colour variation spread. Higher gives a patchier field.")]
        public float noiseSpread = 0.1f;

        public Color healthyColour = new Color(0.35f, 0.55f, 0.2f, 1f);
        public Color dryColour = new Color(0.55f, 0.5f, 0.25f, 1f);

        [Tooltip("Lays the billboard against the surface instead of standing it upright. ON for a "
               + "new prototype: grass stood bolt upright on a hillside lifts its base off the "
               + "ground on the downhill side, which reads as hanging. Existing prototypes keep "
               + "whatever was saved.")]
        public bool alignToGround = true;

        [Tooltip("How far this leans with the slope when Align To Ground is on, 0-1. Not binary on "
               + "purpose: 1 lays a blade flat on a steep hill, which looks pressed down, while a "
               + "partial lean keeps the blade standing yet seats its base on the ground.")]
        [Range(0f, 1f)]
        public float groundAlignStrength = 0.6f;

        [Tooltip("GPU instancing for the detail mesh. Off falls back to per-patch batching.")]
        public bool useInstancing = true;

        [Tooltip("Rejected on ground steeper than this, in degrees.")]
        public float maxSlope = 45f;

        [Tooltip("Rejected outside this altitude band, 0-1 of world height.")]
        public float minAltitude;
        public float maxAltitude = 1f;

        [Tooltip("Which motion model this detail uses. Grass lies over in a breeze and "
               + "flutters fast; Shrub holds its base and needs more wind before it gives. "
               + "Only read when the prototype's material uses UniverseCreator/WindDetail.")]
        public VegetationClass vegetationClass = VegetationClass.Grass;

        [Tooltip("Trim on top of the global response for this class.")]
        [Range(0f, 2f)]
        public float windResponse = 1f;

        [Tooltip("How far this parts when something walks through it. Grass carries no "
               + "collider - the parting is entirely visual, which is why it costs nothing.")]
        [Range(0f, 2f)]
        public float squeeze = 1f;

        [Tooltip("Relative selection weight when a node picks between prototypes.")]
        public float weight = 1f;

        [Tooltip("Where this species can live: its climate, soil, water and stress limits. "
               + "The altitude band and slope limit above remain a hard override on top.")]
        public EcoEnvelope eco = new EcoEnvelope();
    }

    [System.Serializable]
    public class ObjectPrototypeDef
    {
        /// <summary>UC-T06-06: serialized prototype identity, independent of its roster index; duplicate IDs are rejected.</summary>
        [HideInInspector] public string persistentId = System.Guid.NewGuid().ToString("N");
        public GameObject prefab;

        [Tooltip("Relative selection weight when a node picks between prototypes.")]
        public float weight = 1f;

        [Tooltip("Sinks the object into the ground by this fraction of its scale.")]
        public float embedDepth = 0.15f;

        [Tooltip("Tilts the object toward the surface normal, 0-1.")]
        public float normalAlignment = 1f;

        /// <summary>UC-T06-08: optional full-key size/yaw variation, retaining prototype selection and existing deposit sizing.</summary>
        public ScatterPrototypeVariation variation = new ScatterPrototypeVariation();

        [Tooltip("Marks the spawned object static. Only meaningful for GameObject spawns.")]
        public bool markStatic = true;

        // Stones and deadfall are not alive, but they are still placed by process
        // (V-19): a talus block belongs below a slope past its angle of repose, and
        // river cobble belongs where water runs. The same envelope answers both
        // questions, which is why the non-living scatter carries one too.
        [Tooltip("Where this deposit forms: the slope, flow, salt and altitude conditions "
               + "that put loose material here rather than somewhere else.")]
        public EcoEnvelope eco = new EcoEnvelope();
    }

    /// <summary>
    /// The prototype set for one world. Lives on UniverseCreatorObject so it
    /// serializes with the scene and needs no separate asset.
    /// </summary>
    [System.Serializable]
    public class ScatterPrototypeLibrary
    {
        public List<TreePrototypeDef> trees = new List<TreePrototypeDef>();
        public List<ShrubPrototypeDef> shrubs = new List<ShrubPrototypeDef>();
        public List<DetailPrototypeDef> details = new List<DetailPrototypeDef>();
        public List<ObjectPrototypeDef> objects = new List<ObjectPrototypeDef>();

        [Tooltip("Hard ceiling on GameObject scatter spawns per chunk. The mobile budget "
               + "in Appendix B is 2000; desktop tolerates far more.")]
        public int maxObjectsPerChunk = 4000;

        [Tooltip("Ceiling on shrub spawns per chunk. Separate from the object budget "
               + "because a shrub carries a trigger collider and a rock does not, so "
               + "they cost different amounts and want different limits.")]
        public int maxShrubsPerChunk = 600;

        [Tooltip("Above this per-chunk shrub count, spawns switch to GPU instancing. "
               + "Instanced shrubs keep their wind and lose their trigger collider.")]
        public int shrubColliderBudget = 250;

        [Tooltip("Above this per-chunk count, GameObject spawns switch to GPU instancing "
               + "regardless of the per-prototype flag. Protects the transform hierarchy.")]
        public int gpuInstancingThreshold = 1500;

        [Tooltip("Scales every detail density. The one dial to turn for a platform budget.")]
        public float globalDetailDensityScale = 1f;

        public bool HasTrees
        {
            get
            {
                for (int i = 0; i < trees.Count; i++)
                    if (trees[i] != null && trees[i].prefab != null) return true;
                return false;
            }
        }

        public bool HasDetails
        {
            get
            {
                for (int i = 0; i < details.Count; i++)
                {
                    DetailPrototypeDef d = details[i];
                    if (d != null && (d.prefabMesh != null || d.billboardTexture != null)) return true;
                }
                return false;
            }
        }

        public bool HasShrubs
        {
            get
            {
                for (int i = 0; i < shrubs.Count; i++)
                    if (shrubs[i] != null && shrubs[i].prefab != null) return true;
                return false;
            }
        }
    }
}
