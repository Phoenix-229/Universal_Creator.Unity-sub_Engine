// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TerrainHealer.cs
//  Purpose: STUB. Reads RoadMaster's cut zones and heals seams where the road
//           mesh meets the terrain. The interface is real and final; the
//           healing solver lands in Module 7.
//
//  CRITICAL FRAMING: RoadMaster cuts the terrain mesh to embed the road, which
//  leaves a gap unless the terrain heights along the cut boundary are adjusted
//  to match the road surface. That adjustment is "healing." TerrainHealer is
//  the "consumer" side of the PCG bridge — it reads RoadMaster's spline data
//  and modifies Universe Creator's terrain to close the seam.
//
//  PDF REFERENCE: Section 8.3 "Terrain Healing Pipeline".
// -----------------------------------------------------------------------------

using UnityEngine;
using System.Collections.Generic;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Integration
{
    /// <summary>
    /// Contract: RoadMaster publishes cut splines and their surface elevations;
    /// Universe Creator reads them and adjusts terrain heights to match.
    /// </summary>
    public interface IPCGDataConsumer
    {
        /// <summary>
        /// Called by RoadMaster after a spline is placed or edited. The consumer
        /// should read the spline's control points, sample its surface elevation,
        /// and blend the terrain heights within blendDistance of the spline to
        /// match that elevation.
        /// </summary>
        void ConsumeSpline(GameObject splineObject, float blendDistance);

        /// <summary>
        /// Batch variant: RoadMaster passes a list of all active splines in one
        /// chunk, so the consumer can heal them in a single pass rather than
        /// triggering a terrain update per spline.
        /// </summary>
        void ConsumeSplinesInChunk(List<GameObject> splines, Bounds chunkBounds, float blendDistance);
    }

    /// <summary>
    /// Universe Creator's implementation of the consumer side. Wired to the
    /// chunk manager and the terrain builder so healing writes back to the
    /// correct TerrainData.
    /// </summary>
    public class TerrainHealer : MonoBehaviour, IPCGDataConsumer
    {
        /// <summary>
        /// Reference to the UniverseCreatorObject that owns the chunk manager.
        /// </summary>
        public UniverseCreatorObject universeCreator;

        /// <summary>
        /// Healing blend width in world metres. Heights within this distance of the
        /// spline are adjusted to match the road surface; heights beyond it are
        /// untouched. Higher values give smoother transitions but affect more terrain.
        /// </summary>
        public float defaultBlendDistance = 4f;

        // =====================================================================
        //  MODULE 1 SCOPE: interface signatures are real and final. The
        //  healing solver is TODO for Module 7, after RoadMaster exists.
        // =====================================================================

        public void ConsumeSpline(GameObject splineObject, float blendDistance)
        {
            // TODO [Module 7]: read the spline's control points and their Y
            // coordinates from RoadMaster's spline component. For each point,
            // find the terrain chunk that contains it, and blend heights within
            // blendDistance using a smooth falloff so the road surface and the
            // terrain read as one continuous surface.
        }

        public void ConsumeSplinesInChunk(List<GameObject> splines, Bounds chunkBounds, float blendDistance)
        {
            // TODO [Module 7]: batch variant of ConsumeSpline. Accumulate all the
            // blend contributions into a single delta heightmap, then apply it to
            // the chunk's TerrainData in one SetHeights call rather than writing
            // per spline.
        }

        /// <summary>
        /// Utility: finds the terrain chunk that contains the given world position.
        /// Returns null if the chunk is unloaded.
        /// </summary>
        private Terrain FindChunkAt(Vector3 worldPos)
        {
            // TODO [Module 7]: route to ChunkManager.GetChunkAt(worldPos).
            return null;
        }
    }
}
