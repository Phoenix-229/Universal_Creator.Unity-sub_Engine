// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    UCShrubInstance.cs
//  Purpose: Makes one spawned shrub behave like a shrub rather than like a tree
//           or like a large blade of grass.
//
//  THE PHYSICS STATE, AND WHY IT IS THIS ONE
//  -----------------------------------------
//  A bush is soft. You walk into it, it gives, you come out the other side
//  slower and it springs back. Three states were available and two are wrong:
//
//    Solid collider (like a tree)  A bush would stop the player dead. Wrong: it
//                                  is a plant, not a wall, and being blocked by
//                                  a waist-high shrub reads as a bug.
//    No collider (like grass)      The player would pass through with no sign
//                                  anything happened. Wrong the other way: a
//                                  dense bush should be felt.
//    Trigger collider              What this uses. The character passes through
//                                  freely, the plant squeezes aside visually,
//                                  and gameplay is told it is inside a bush so
//                                  it can slow movement, rustle, or break line
//                                  of sight.
//
//  NO RIGIDBODY. A rigidbody per shrub would be thousands of simulated bodies
//  for a plant that never falls over, never gets thrown, and only ever needs to
//  answer "is something inside me". Unity raises trigger callbacks as long as
//  ONE side of the pair has a Rigidbody or CharacterController - the player
//  already does - so adding one here would cost a simulation island and buy
//  nothing. If your player is a raw transform with only a collider and no
//  CharacterController, add a kinematic Rigidbody to the PLAYER, not to every
//  bush in the world.
//
//  The visible squeeze is not physics at all: it happens in the vertex shader
//  from the interactor uniforms, so it is free and works on instanced shrubs
//  that have no collider at all.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Shrub Instance")]
    public class UCShrubInstance : MonoBehaviour
    {
        // Cached ids for the per-instance material block. The shader properties are
        // the same ones a material exposes; setting them per renderer is what lets
        // one shared material serve shrubs of different sizes and stiffness.
        private static readonly int IdVegClass = Shader.PropertyToID("_VegetationClass");
        private static readonly int IdResponse = Shader.PropertyToID("_WindResponse");
        private static readonly int IdFlutter  = Shader.PropertyToID("_Flutter");
        private static readonly int IdPivotH   = Shader.PropertyToID("_PivotHeight");
        private static readonly int IdTouch    = Shader.PropertyToID("_TouchResponse");
        private Renderer[] appearanceRenderers; // UC-T06-09: cache native consumers once at configuration, not on every appearance refresh.
        private MaterialPropertyBlock appearanceBlock;
        private ShrubPrototypeDef appearanceDefinition;
        private float appearanceScale;

        [Tooltip("How much this shrub slows something moving through it, 0-1. "
               + "Read by gameplay; this component does not move the character itself.")]
        [Range(0f, 1f)]
        public float traversalDrag = 0.45f;

        [Tooltip("How many colliders are currently inside this shrub.")]
        [SerializeField]
        private int occupants;

        /// <summary>True while something is standing inside this shrub.</summary>
        public bool IsOccupied { get { return occupants > 0; } }

        /// <summary>
        /// Raised when something enters or leaves. Gameplay hooks rustle audio,
        /// movement drag or concealment onto this rather than polling every shrub.
        /// </summary>
        public System.Action<Collider, bool> OnOccupancyChanged;

        /// <summary>
        /// Configures a freshly spawned shrub from its prototype: the trigger volume
        /// it reports through, and the per-instance wind properties its renderers use.
        /// wantsTrigger comes from the envelope's collider policy, resolved once in
        /// ScatterApplier against the chunk's collider budget - a shrub past the budget
        /// keeps its wind and drops its trigger.
        /// UC-T06-08: trigger dimensions are prefab-local metres; root scale is applied once.
        /// A false request disables/removes inherited colliders as well as omitting new shapes.
        /// </summary>
        /// <param name="def">Borrowed prototype; null leaves the instance unchanged. Active trigger dimensions must be positive finite prefab-local metres.</param>
        /// <param name="scale">Positive finite unitless instance size; the transform owns scaling and shader height remains in authored mesh-local metres.</param>
        /// <param name="wantsTrigger">Whether to retain/build trigger collision; false removes all inherited shapes.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Invalid scale, material inputs or requested trigger dimensions; nothing is changed.</exception>
        public void Configure(ShrubPrototypeDef def, float scale, bool wantsTrigger)
        {
            if (def == null) return;

            // UC-T06-08: reject invalid native/shader inputs before changing a renderer or collider.
            if (!ScatterApplier.Finite(scale) || scale <= 0f || !ScatterApplier.Finite(def.meshHeight) || def.meshHeight <= 0f ||
                !ScatterApplier.Finite(def.meshHeight * scale) || !ScatterApplier.Finite(def.windResponse) ||
                !ScatterApplier.Finite(def.flutter) || !ScatterApplier.Finite(def.squeeze))
                throw new System.ArgumentOutOfRangeException(nameof(scale), "Shrub scale and mesh height must be positive finite values; wind/touch inputs must be finite.");
            if (wantsTrigger && (!ScatterApplier.Finite(def.colliderRadius) || def.colliderRadius <= 0f ||
                !ScatterApplier.Finite(def.colliderHeight) || def.colliderHeight <= 0f || !ScatterApplier.Finite(def.colliderRadius * 2f)))
                throw new System.ArgumentOutOfRangeException(nameof(def), "Requested shrub trigger dimensions must be positive finite prefab-local metres.");

            traversalDrag = Mathf.Clamp01(def.squeeze * 0.45f);

            if (wantsTrigger) BuildTrigger(def);
            else ScatterApplier.ApplyColliderParts(gameObject, Ecology.ColliderPolicy.None); // UC-T06-08: inherited shapes cannot escape the budget.
            appearanceDefinition = def; appearanceScale = scale; // UC-T06-09: borrow the authored prototype without modifying transforms/collision on a later refresh.
            ApplyMaterialBlock(def); // UC-T06-09: shader height is authored mesh-local data.
        }

        private void BuildTrigger(ShrubPrototypeDef def)
        {
            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            if (capsule == null) capsule = gameObject.AddComponent<CapsuleCollider>();

            // UC-T06-08: multiplying local dimensions by instance scale here would make world collision grow quadratically.
            float r = Mathf.Max(0.02f, def.colliderRadius);
            float h = Mathf.Max(r * 2f, def.colliderHeight);

            capsule.radius = r;
            capsule.height = h;
            capsule.direction = 1;                       // Y axis: shrubs stand up
            capsule.center = new Vector3(0f, h * 0.5f, 0f);

            // The whole point: a trigger, so the player walks through the bush
            // instead of being stopped by it.
            capsule.isTrigger = true;

            if (def.colliderLayer >= 0 && def.colliderLayer < 32)
                gameObject.layer = def.colliderLayer;
        }

        private void ApplyMaterialBlock(ShrubPrototypeDef def)
        {
            if (appearanceRenderers == null) appearanceRenderers = GetComponentsInChildren<Renderer>(true); // UC-T06-09
            Renderer[] renderers = appearanceRenderers;
            if (renderers == null || renderers.Length == 0) return;

            if (appearanceBlock == null) appearanceBlock = new MaterialPropertyBlock(); // UC-T06-09: reuse the native upload block.
            MaterialPropertyBlock block = appearanceBlock;

            // Shrub class, not grass. This single value is the difference between a
            // bush that holds its base and a bush that lies over like a lawn.
            block.SetFloat(IdVegClass, (float)VegetationClass.Shrub);
            block.SetFloat(IdResponse, Mathf.Max(0f, def.windResponse));
            block.SetFloat(IdFlutter, Mathf.Max(0f, def.flutter));
            block.SetFloat(IdPivotH, Mathf.Max(0.05f, def.meshHeight)); // UC-T06-09: shipped shaders divide positionOS.y by this mesh-local height; matrix scale applies once.
            block.SetFloat(IdTouch, Mathf.Max(0f, def.squeeze));

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                renderers[i].SetPropertyBlock(block);
            }
        }

        // UC-T06-09: update only cached renderer parameters; collider identity/dimensions and transforms are retained.
        internal void RefreshAppearance()
        {
            var def = appearanceDefinition;
            if (def == null) return;
            if (!ScatterApplier.Finite(def.windResponse) || !ScatterApplier.Finite(def.flutter) || !ScatterApplier.Finite(def.squeeze) ||
                !ScatterApplier.Finite(def.meshHeight) || def.meshHeight <= 0 || !ScatterApplier.Finite(def.meshHeight * appearanceScale))
                throw new System.ArgumentOutOfRangeException(nameof(def), "Shrub appearance inputs must be finite and mesh height positive.");
            ApplyMaterialBlock(def);
        }

        private void OnTriggerEnter(Collider other)
        {
            occupants++;
            if (OnOccupancyChanged != null) OnOccupancyChanged(other, true);
        }

        private void OnTriggerExit(Collider other)
        {
            // Never below zero: a collider destroyed while inside fires Enter but
            // not Exit, and a negative count would leave the shrub stuck occupied.
            occupants = Mathf.Max(0, occupants - 1);
            if (OnOccupancyChanged != null) OnOccupancyChanged(other, false);
        }

        private void OnDisable()
        {
            occupants = 0;
        }
    }
}
