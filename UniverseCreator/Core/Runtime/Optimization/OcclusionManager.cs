// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    OcclusionManager.cs
//  Purpose: Culls chunks and instances that cannot contribute to the current view.
//
//  Frustum and distance culling only. True occlusion (something hidden BEHIND a
//  hill rather than outside the view) is left to Unity's own occlusion culling,
//  which already has the depth buffer this class would otherwise have to rebuild.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Optimization
{
    /// <summary>
    /// Frustum- and distance-culls world bounds against a camera. Terrain chunks
    /// and scatter batches both test through the same path.
    /// </summary>
    public class OcclusionManager : MonoBehaviour
    {
        [Tooltip("Camera to cull against. Falls back to Camera.main when unset.")]
        public Camera targetCamera;

        [Tooltip("Beyond this distance nothing is drawn, regardless of frustum.")]
        public float maxDrawDistance = 2000f;

        [Tooltip("Grows each test bounds slightly so an object straddling the frustum edge does not pop.")]
        public float boundsPadding = 5f;

        // Reused every test: GeometryUtility fills six planes, and allocating a
        // fresh array per call would churn the GC once per chunk per frame.
        private readonly Plane[] frustum = new Plane[6];
        private Camera cachedCamera;
        private int framePlanesUpdated = -1;

        /// <summary>
        /// True if the bounds should be drawn this frame. Refreshes the cached
        /// frustum once per frame, so calling this per chunk is cheap.
        /// </summary>
        public bool IsVisible(Bounds worldBounds)
        {
            Camera cam = ResolveCamera();
            if (cam == null) return true;   // no camera to cull against: draw everything

            RefreshPlanes(cam);

            // Distance first: it is a single sqrMagnitude and rejects the majority
            // of a large world before the six plane tests run.
            float sqrLimit = maxDrawDistance * maxDrawDistance;
            if ((worldBounds.ClosestPoint(cam.transform.position) - cam.transform.position).sqrMagnitude > sqrLimit)
                return false;

            return TestFrustum(frustum, worldBounds, boundsPadding); // UC-T06-10: scatter shares frustum math without inheriting the chunk distance cap.
        }

        internal static bool TestFrustum(Plane[] planes, Bounds bounds, float padding)
        {
            bounds.Expand(Mathf.Max(0f, padding) * 2f); // UC-T06-10: caller-validated deformation padding, in world metres.
            return GeometryUtility.TestPlanesAABB(planes, bounds);
        }

        /// <summary>
        /// Filters a list of bounds down to the visible ones, returning how many
        /// survived. Results are written into the caller's list to avoid allocating.
        /// </summary>
        public int FilterVisible(IList<Bounds> candidates, List<int> visibleIndices)
        {
            if (candidates == null || visibleIndices == null) return 0;

            visibleIndices.Clear();
            for (int i = 0; i < candidates.Count; i++)
                if (IsVisible(candidates[i])) visibleIndices.Add(i);

            return visibleIndices.Count;
        }

        private Camera ResolveCamera()
        {
            if (targetCamera != null) return targetCamera;
            if (cachedCamera == null) cachedCamera = Camera.main;
            return cachedCamera;
        }

        private void RefreshPlanes(Camera cam)
        {
            // Time.frameCount guard: the planes only change when the camera moves,
            // which happens at most once per frame.
            if (framePlanesUpdated == Time.frameCount) return;

            GeometryUtility.CalculateFrustumPlanes(cam, frustum);
            framePlanesUpdated = Time.frameCount;
        }
    }
}
