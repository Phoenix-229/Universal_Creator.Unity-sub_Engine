// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TerrainHealer.cs
//  Purpose: STUB. Reads Road & City Master's cut zones and heals seams where
//           the road mesh meets the terrain. The interface is real and final; the
//           healing solver lands in Module 7.
//
//  CRITICAL FRAMING: the path tool cuts the terrain mesh to embed the road, which
//  leaves a gap unless the terrain heights along the cut boundary are adjusted
//  to match the road surface. That adjustment is "healing." TerrainHealer is
//  the "consumer" side of the PCG bridge — it reads the tool's spline data
//  and modifies Universe Creator's terrain to close the seam.
//
//  NAMING: prose in this file names Road & City Master, the path tool that ships
//  with the toolkit, because that is the tool this stub was written against and a
//  reader deserves to know which one. The symbols do not: IPCGDataConsumer and
//  every signature below stay product-neutral, so a different path tool can drive
//  the same healing pass without a rename. See Module 6 § 10.0b.
//
//  PDF REFERENCE: Section 8.3 (terrain healing pipeline).
// -----------------------------------------------------------------------------

using UnityEngine;
using System.Collections.Generic;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Integration
{
    /// <summary>
    /// Contract: the path tool publishes cut splines and their surface elevations;
    /// Universe Creator reads them and adjusts terrain heights to match. Worded
    /// without a product name on purpose — Road & City Master is the implementation
    /// that drives it today, not a requirement of the contract.
    /// </summary>
    public interface IPCGDataConsumer
    {
        /// <summary>
        /// Called by the path tool after a spline is placed or edited. The consumer
        /// should read the spline's control points, sample its surface elevation,
        /// and blend the terrain heights within blendDistance of the spline to
        /// match that elevation.
        /// </summary>
        void ConsumeSpline(GameObject splineObject, float blendDistance);

        /// <summary>
        /// Batch variant: the path tool passes a list of all active splines in one
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
        //  healing solver is TODO for Module 7, after the path tool exists.
        // =====================================================================

        public void ConsumeSpline(GameObject splineObject, float blendDistance)
        {
            // TODO [Module 7]: read the spline's control points and their Y
            // coordinates from the path tool's spline component. For each point,
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
