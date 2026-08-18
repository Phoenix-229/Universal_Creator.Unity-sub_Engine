// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    UCVegetationInteractor.cs
//  Purpose: Marks a moving body - the player, a companion, a vehicle - as
//           something that parts grass and pushes bushes aside as it passes.
//
//  HOW IT WORKS
//  ------------
//  Put this component on the player and every frame the wind manager pulls its
//  world position into a small array of shader uniforms. The grass and shrub
//  vertex shaders sample that array, find which plants are inside the body's
//  radius, and push them aside. There is no collider on the grass involved and
//  no per-instance state anywhere - a million blades can be parted for the cost
//  of one 32-float upload per frame.
//
//  WHY A TRIGGER COLLIDER IS OPTIONAL
//  ----------------------------------
//  Grass never needs one: you pass through a meadow and it just looks right.
//  A bush, though, is dense enough that a character should be able to walk into
//  it and be slowed by it, so shrub prototypes can spawn a trigger collider and
//  report their volume. This component has nothing to do with that: it is the
//  mover, not the plant. If you are walking into bushes and they are not giving
//  way, check the shrub's collider and squeeze settings, not this component.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Vegetation Interactor")]
    public class UCVegetationInteractor : MonoBehaviour
    {
        [Tooltip("Radius in metres of the parting this body pushes through vegetation.")]
        [Min(0.05f)]
        public float radius = 0.55f;

        [Tooltip("How hard this body pushes plants aside. 0 makes it walk through a "
               + "field without disturbing it; 2 makes a wake you can see across a lawn.")]
        [Range(0f, 2f)]
        public float pushStrength = 1f;

        // Where the parting is anchored. Most bodies part plants around their feet,
        // but a low hovercraft should part them around its underside.
        [Tooltip("Anchor of the parting. 0 = feet, 1 = head, 0.5 = mid-body.")]
        [Range(0f, 1f)]
        public float verticalAnchor = 0f;

        /// <summary>World position the vegetation parting is centred on.</summary>
        public Vector3 SamplePosition
        {
            get
            {
                Vector3 p = transform.position;
                p.y += verticalAnchor * Mathf.Max(0f, transform.lossyScale.y) * 1.6f;
                return p;
            }
        }

        private void OnEnable()
        {
            UCWindManager.Register(this);
        }

        private void OnDisable()
        {
            UCWindManager.Unregister(this);
        }

        private void OnDestroy()
        {
            UCWindManager.Unregister(this);
        }
    }
}
