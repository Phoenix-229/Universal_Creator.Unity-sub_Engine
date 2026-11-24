// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCRiverNode.cs
//  Purpose: One control point of a River Spline body, as its own child
//           GameObject so it can be selected in the hierarchy and given its own
//           inspector - the authoring model the author chose for the rebuilt
//           river (click the spline to add a node, delete the child to remove
//           one). The node's POSITION is its transform; its hydraulic payload
//           (half-width, depth, discharge, velocity) are the fields below.
//
//  SINGLE SOURCE OF TRUTH
//  ----------------------
//  The nodes are the EDIT-TIME source of truth. UCWaterBody.GatherCourseFromNodes
//  walks the child nodes in sibling order and bakes them into the body's `outline`
//  (positions) and `riverProfile` (channels) - the one representation the solver,
//  carve and mesh read. So nothing downstream knows or cares that nodes exist;
//  they are purely how a human edits the course.
//
//  ZERO RUNTIME COST, EDITOR-TIME AUTHORING
//  ----------------------------------------
//  This is a pure data holder: no Awake, no Update, no per-frame work, nothing a
//  shipped build pays for even if a node survives into one. The baked outline +
//  riverProfile are what a build actually uses; the nodes are authoring aids that
//  belong with the course, exactly as the outline gizmos do.
//
//  The inherit convention matches RiverProfile / UCWaterBody: a channel left at 0
//  means "inherit the previous node", and a channel that is 0 on every node is
//  unauthored and the step-4 solver derives it (Leopold width/depth, drainage
//  discharge, Manning velocity). Real-world metric units; no random defaults.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// One editor-authored control point of a river course. Position is the
    /// transform; the fields are the per-node hydraulic payload, 0 = inherit/derive.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]   // created only by the river authoring tools, never from the Add Component menu
    public class UCRiverNode : MonoBehaviour
    {
        [Tooltip("Half the channel top-width at this node, in metres. 0 inherits the previous node.")]
        public float halfWidthM = 0f;

        [Tooltip("Channel depth at this node, in metres. 0 inherits the previous node.")]
        public float depthM = 0f;

        [Tooltip("Discharge Q in m3/s. 0 inherits the preceding authored value; all-unset courses derive it from drainage.")] // UC-T04-05B: shared inheritance semantics.
        public float dischargeM3PerS = 0f;

        [Tooltip("Saved mean velocity in m/s; 0 inherits. Current surface flow uses the river body's Flow Speed.")] // UC-T04-05B: identify the active consumer.
        public float velocityMps = 0f;

        /// <summary>Original serialized point read-trench flag; used only by body migration/recovery, never as a live mode switch.</summary>
        [HideInInspector] // UC-T04-01: preserve legacy data without exposing a second mode authority.
        public bool followProceduralTrench = false;

        [Tooltip("The flood plain BEGINS at this node. With no start ticked anywhere the whole river has " +
                 "a flood plain; once any node is ticked the flood plain exists only from a Start node " +
                 "to the next End node (or to the river's end).")]
        public bool floodPlainStart = false;

        [Tooltip("The flood plain ENDS at this node.")]
        public bool floodPlainEnd = false;

        [Tooltip("Weld this node to another river's channel so the two water planes join as one. Only " +
                 "offered when this node touches another river. The LAST node makes this river a " +
                 "tributary of the other; the FIRST node makes it the other river's branch.")]
        public bool connectJunction = false;

        [Tooltip("The river this node is welded to (set by the Connect Junction tick).")]
        public UCWaterBody junctionTarget;
    }
}
