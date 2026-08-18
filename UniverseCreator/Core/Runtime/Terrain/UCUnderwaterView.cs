// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCUnderwaterView.cs
//  Purpose: Drives the underwater look from wherever the camera actually is.
//
//  WHY A COMPONENT AND NOT A RENDERER FEATURE
//  ------------------------------------------
//  Everything this needs to do is "decide what medium the eye is in, then set four
//  globals". That is not a render pass - it has no geometry, no target and no
//  blit. Written as a ScriptableRendererFeature it would additionally have to be
//  registered on the URP renderer asset by hand before it did anything, which is a
//  setup step the user has to be told about and can get wrong. A component finds
//  its own camera and works the moment it is added.
//
//  The look itself lives in UC_Underwater.hlsl, shared by the terrain, water and
//  detail shaders, so ground, plants and the surface film all agree.
//
//  WHICH WATER IT READS
//  --------------------
//  UCWaterVolume.Sample, the same query the swimming and buoyancy code uses. Every
//  body kind gets a volume - ocean, lake, river and pool alike - so diving into any
//  of them is handled by this one path, and the look comes from that body's own
//  fields rather than from a scene-wide setting. A silty river and a clear sea are
//  different because their volumes say so.
//
//  Because it is the same query the swimmer uses, the view can never disagree with
//  the physics: the frame you start swimming is the frame the water closes over the
//  camera.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Put this on a camera and the world is rendered through water whenever the
    /// camera is inside a water body.
    /// </summary>
    [AddComponentMenu("Universe Creator/Water/Underwater View")]
    [RequireComponent(typeof(Camera))]
    [ExecuteAlways]
    public class UCUnderwaterView : MonoBehaviour
    {
        // Shader globals. Names match UC_Underwater.hlsl; IDs cached because these
        // are set every frame and Shader.PropertyToID on a string is a hash per call.
        private static readonly int OnId = Shader.PropertyToID("_UCUnderwaterOn");
        private static readonly int ColorId = Shader.PropertyToID("_UCUnderwaterColor");
        private static readonly int ParamsId = Shader.PropertyToID("_UCUnderwaterParams");

        [Header("Transition")]
        [Tooltip("Depth in metres over which the effect fades in as the camera crosses " +
                 "the surface.\n\n" +
                 "Not zero: a hard switch reads as a flash on every dive, and a camera " +
                 "bobbing at the waterline would strobe. A few centimetres is enough to " +
                 "hide the boundary while still feeling immediate.")]
        [Range(0.01f, 1f)] public float crossFadeMetres = 0.12f;

        [Tooltip("Also apply the effect when the camera is only just under, before the " +
                 "eye is properly submerged. Off is more correct; on is more forgiving " +
                 "of a camera that sits high in a swimming character.")]
        public bool eagerAtSurface;

        [Tooltip("Keep evaluating in the scene view without entering play mode.\n\n" +
                 "Off by default. This camera is not what draws the scene view, so in " +
                 "edit mode the work re-samples every water body once per editor frame " +
                 "- and the editor emits frames as fast as you move the mouse - to " +
                 "answer a question nothing is asking. Turn it on only to preview the " +
                 "effect from the game camera.")]
        public bool evaluateInEditor;

        [Header("Overrides")]
        [Tooltip("Ignore the water body's own visibility and use this instead. Zero " +
                 "means use the body's value, which is what you normally want - a " +
                 "river and a sea should not look alike.")]
        public float visibilityOverrideMetres;

        [Tooltip("Multiplies whatever visibility ends up being used. This is the dial " +
                 "for silt, storms or night, driven from a weather system without " +
                 "touching the bodies themselves.")]
        [Range(0.05f, 4f)] public float visibilityScale = 1f;

        [Header("Read-only")]
        [Tooltip("Whether the camera is currently under water.")]
        [SerializeField] private bool submerged;

        [Tooltip("Metres the camera is below the surface.")]
        [SerializeField] private float depthMetres;

        /// <summary>Whether the camera is currently in water.</summary>
        public bool IsSubmerged { get { return submerged; } }

        /// <summary>How far below the surface the camera is, in metres.</summary>
        public float DepthMetres { get { return depthMetres; } }

        /// <summary>The body the camera is currently in, or null.</summary>
        public UCWaterVolume Volume { get; private set; }

        private void OnEnable() { Apply(0f, null, 0f); }

        // Cleared on disable, or a camera that stops running leaves the whole scene
        // tinted for every other camera - the globals outlive the component.
        private void OnDisable() { Clear(); }

        private void OnDestroy() { Clear(); }

        // LateUpdate, not Update: the camera may be moved by a follow script this
        // frame, and reading its position before that runs measures where it was.
        //
        // Gated on play mode because [ExecuteAlways] would otherwise ask "is this
        // camera under water" once per editor frame, forever, about a camera that is
        // not rendering anything - and each answer walks the scene's water volumes.
        private void LateUpdate()
        {
            if (!Application.isPlaying && !evaluateInEditor)
            {
                // Cleared once on the way into the gated state. A preview that was
                // submerged when it got switched off would otherwise leave every
                // shader in the scene tinted with no component still driving it.
                if (submerged)
                {
                    submerged = false;
                    depthMetres = 0f;
                    Volume = null;
                    Apply(0f, null, 0f);
                }
                return;
            }

            Evaluate();
        }

        /// <summary>
        /// Samples the water at the camera and pushes the result to the shaders.
        /// Public so an editor preview or a cutscene can drive it directly.
        /// </summary>
        public void Evaluate()
        {
            Vector3 eye = transform.position;

            WaterSample s = UCWaterVolume.Sample(eye, WaveClock());

            UCWaterVolume v = s.volume;

            if (!s.submerged || v == null || !v.underwaterView)
            {
                submerged = false;
                depthMetres = 0f;
                Volume = null;
                Apply(0f, null, 0f);
                return;
            }

            submerged = true;
            depthMetres = Mathf.Max(0f, s.depth);
            Volume = v;

            // Faded by how far under the surface the eye is, so breaking the surface
            // is continuous. eagerAtSurface starts the fade at the surface itself
            // rather than a fade-length below it.
            float fade = Mathf.Max(0.01f, crossFadeMetres);
            float strength = eagerAtSurface
                ? Mathf.Clamp01(depthMetres / fade + 0.25f)
                : Mathf.Clamp01(depthMetres / fade);

            Apply(strength, v, s.surfaceY);
        }

        private void Apply(float strength, UCWaterVolume v, float surfaceY)
        {
            if (strength <= 0f || v == null)
            {
                Shader.SetGlobalFloat(OnId, 0f);
                return;
            }

            float visibility = visibilityOverrideMetres > 0.01f
                ? visibilityOverrideMetres
                : v.visibilityMetres;

            // Clamped rather than trusted. A visibility of zero divides by zero in
            // the absorption term and paints the screen a flat colour; 0.5 m is
            // already thick soup and is a floor the user cannot break the view with.
            visibility = Mathf.Max(0.5f, visibility * Mathf.Max(0.05f, visibilityScale));

            Color c = v.underwaterColor * Mathf.Max(0f, v.underwaterBrightness);
            c.a = 1f;

            Shader.SetGlobalFloat(OnId, strength);
            Shader.SetGlobalColor(ColorId, c);

            // z is the surface height so the shader can work out how deep each PIXEL
            // is, not just the camera - that is what makes the bed darken away below
            // you while the water beside you at eye level stays light.
            Shader.SetGlobalVector(ParamsId, new Vector4(
                visibility,
                1f / visibility,
                surfaceY,
                Mathf.Max(1f, v.maxDepth)));
        }

        private void Clear()
        {
            submerged = false;
            depthMetres = 0f;
            Volume = null;
            Shader.SetGlobalFloat(OnId, 0f);
        }

        // Edit mode has no Time.time that advances, so a scene view camera under water
        // would sample the waves frozen at frame zero and sit at a fixed depth while
        // the surface visibly moves past it.
        private static float WaveClock()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                return (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
            return Time.time;
        }
    }
}
