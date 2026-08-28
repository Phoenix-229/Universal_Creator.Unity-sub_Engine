// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ShadowLodBudget.cs
//  Purpose: Shadow casting gets its OWN, shorter budget - it is never selected on
//           the same allowance as the visible mesh. A prop's shadow carries far less
//           silhouette information than the prop, and the shadow pass is the cheapest
//           thing to cut, so shadow LOD is coarser at a given distance and shadow
//           casting stops EARLIER than geometry, honouring § 5's authored
//           shadowCastDistanceM. The failure this prevents is the common one: a
//           billboard that has already dropped to a card still casting a full-mesh
//           shadow - pure cost for a shape that no longer exists as geometry.
//
//  THE THREE SHADOW MODES, AND WHY THE ORDER IS FIXED
//  --------------------------------------------------
//  Near the camera the geometry casts its true shadow (FullMesh). Past the impostor
//  switch the object IS a card, so its shadow can only come from the impostor's baked
//  depth channel (DepthDerived) - never a re-selected full mesh, which is exactly what
//  the done-when forbids. Past the (shorter) shadow-cast distance the shadow stops
//  entirely (None). The classifier is monotone in distance through those three, so a
//  receding prop can only ever shed shadow detail, never regain it.
//
//  THE BUDGET IS CLAMPED BELOW GEOMETRY
//  ------------------------------------
//  § 5 authors shadowCastDistanceM per species, but the effective distance is clamped
//  to at most the geometry cull distance, so shadows can never outlast the thing that
//  casts them. And the shadow LOD rides a REDUCED bias (an acuity scale <= 1), so at
//  any distance the shadow's LOD index is no finer than the visible mesh's.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Every method is a static function of numbers, reusing MeshLODController's selection
//  for the shadow LOD, so a self-test pins the mode order and the budget clamp without
//  a light, a camera or a shadow map. The pass that actually suppresses the caster or
//  swaps in the depth-derived shadow is the scene layer above.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>How a prop casts its shadow at a given distance, coarsest-last: the true mesh, the impostor's
    /// depth-derived shadow, or nothing once past the shadow budget.</summary>
    public enum ShadowCastMode
    {
        /// <summary>Past the shadow-cast budget: the prop casts no shadow at all.</summary>
        None = 0,
        /// <summary>The prop is an impostor; its shadow comes from the baked depth channel, never a full mesh.</summary>
        DepthDerived = 1,
        /// <summary>Near the camera: the visible geometry casts its true shadow.</summary>
        FullMesh = 2
    }

    /// <summary>
    /// Pure reference for the separate, shorter shadow budget: the effective shadow-cast distance (clamped below
    /// geometry), the coarser shadow LOD bias, and the distance-to-mode classifier that guarantees an impostor
    /// never casts a full-mesh shadow. No light, no camera, no allocation.
    /// </summary>
    public static class ShadowLodBudget
    {
        /// <summary>Default acuity scale for the shadow pass: shadows are selected at half the visible bias, so a
        /// shadow's LOD is coarser than the mesh's at the same distance.</summary>
        public const float DefaultShadowAcuityScale = 0.5f;

        /// <summary>
        /// The effective shadow-cast distance: § 5's authored <paramref name="authoredShadowCastDistanceM"/>,
        /// clamped to at most the geometry cull distance so shadows never outlast the geometry that casts them.
        /// A non-positive authored value (author disabled shadows) stays 0. Guards a non-positive cull to 0.
        /// </summary>
        public static float EffectiveShadowCastDistanceM(float authoredShadowCastDistanceM, float geometryCullDistanceM)
        {
            if (authoredShadowCastDistanceM <= 0f || geometryCullDistanceM <= 0f) return 0f;
            return Mathf.Min(authoredShadowCastDistanceM, geometryCullDistanceM);
        }

        /// <summary>
        /// The LOD bias the shadow pass selects with: the visible <paramref name="mainLodBias"/> times an acuity
        /// scale in [0,1]. A scale &lt; 1 makes the shadow read as smaller, so its LOD is coarser than the mesh's
        /// at the same distance. Guards non-positive inputs to 0 (no shadow selection).
        /// </summary>
        public static float ShadowLodBias(float mainLodBias, float shadowAcuityScale = DefaultShadowAcuityScale)
        {
            if (mainLodBias <= 0f || shadowAcuityScale <= 0f) return 0f;
            return mainLodBias * Mathf.Clamp01(shadowAcuityScale);
        }

        /// <summary>
        /// Classifies how the prop casts at <paramref name="distanceM"/>: FullMesh before the impostor switch,
        /// DepthDerived once it is an impostor (never a full mesh), and None past the effective shadow-cast
        /// distance. Monotone in distance through the three modes, so a receding prop only sheds shadow detail.
        /// A non-positive shadow-cast distance yields None (shadows disabled for this species).
        /// </summary>
        public static ShadowCastMode SelectShadowMode(float distanceM, float impostorSwitchDistanceM, float effectiveShadowCastDistanceM)
        {
            if (effectiveShadowCastDistanceM <= 0f || distanceM >= effectiveShadowCastDistanceM)
                return ShadowCastMode.None;
            if (impostorSwitchDistanceM > 0f && distanceM >= impostorSwitchDistanceM)
                return ShadowCastMode.DepthDerived;
            return ShadowCastMode.FullMesh;
        }

        /// <summary>
        /// The shadow pass's LOD index at a distance: the same screen-space-error selection the visible mesh uses,
        /// but at the reduced shadow bias, so the shadow's LOD is never finer than the mesh's. Delegates to
        /// <see cref="MeshLODController.SelectLod(float,float,float,IReadOnlyList{float},float)"/>.
        /// </summary>
        public static int SelectShadowLod(
            float boundsRadiusM, float distanceM, float fovDeg,
            IReadOnlyList<float> screenRelativeTransitionHeights,
            float mainLodBias = 1f, float shadowAcuityScale = DefaultShadowAcuityScale)
        {
            float bias = ShadowLodBias(mainLodBias, shadowAcuityScale);
            return MeshLODController.SelectLod(boundsRadiusM, distanceM, fovDeg, screenRelativeTransitionHeights, bias);
        }
    }
}
