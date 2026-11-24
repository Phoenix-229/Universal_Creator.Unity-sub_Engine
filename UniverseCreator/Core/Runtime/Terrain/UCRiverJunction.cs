// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCRiverJunction.cs
//  Purpose: The child object that marks a river-to-river junction under a river node. It carries
//           the junction's target and the blend settings, and draws a gizmo so the weld is visible
//           in the Scene view. The welding itself (one shared water level, blended width and bed)
//           is done by UCWaterBody.BuildRiverDig from the node's Connect Junction tick - this is the
//           visible, selectable, deletable handle for it. Delete this child to see the node's tick
//           reflected back: the junction is removed on the next rebuild.
//  Zero runtime cost: pure data holder, no Update.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class UCRiverJunction : MonoBehaviour
    {
        [Tooltip("The river this junction is welded to.")]
        public UCWaterBody target;

        [Tooltip("Metres of channel, measured back along this river from the junction, over which its bed, " +
                 "width and water level blend into the other river. 0 uses the body's Confluence Blend.")]
        public float blendMetresOverride = 0f;

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 1f);
            Gizmos.DrawWireSphere(transform.position, 1.2f);
            if (target != null)
            {
                Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.5f);
                Gizmos.DrawLine(transform.position, target.transform.position);
            }
        }
    }
}
