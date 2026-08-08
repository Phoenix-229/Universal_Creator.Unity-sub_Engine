// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    RoadMasterLinker.cs
//  Purpose: STUB. Exposes Universe Creator's terrain data to RoadMaster (Module 7)
//           through the IPCGDataProvider contract. The interface is real and final;
//           the implementation lands in Module 7.
//
//  CRITICAL FRAMING: Universe Creator is one engine subsystem; RoadMaster is
//  another, owned by Module 7. They communicate through a formal contract, not
//  by reaching into each other's internals. This class is the "provider" side
//  of that contract — it publishes terrain surface data, chunk bounds, and
//  heightfield queries that RoadMaster reads when laying splines.
//
//  PDF REFERENCE: Section 8.2 "RoadMaster Integration".
// -----------------------------------------------------------------------------

using UnityEngine;
using System.Collections.Generic;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Integration
{
    /// <summary>
    /// Contract: Universe Creator publishes terrain data; RoadMaster reads it.
    /// </summary>
    public interface IPCGDataProvider
    {
        /// <summary>
        /// Returns the world-space height at (x, z). Called by RoadMaster when
        /// snapping a spline control point to the surface.
        /// </summary>
        float SampleHeight(float worldX, float worldZ);

        /// <summary>
        /// Returns the surface normal at (x, z). Used for banking and slope analysis.
        /// </summary>
        Vector3 SampleNormal(float worldX, float worldZ);

        /// <summary>
        /// Returns the list of active chunk bounds, so RoadMaster knows which regions
        /// are loaded and can defer spline operations on unloaded chunks.
        /// </summary>
        List<Bounds> GetActiveChunkBounds();

        /// <summary>
        /// Notifies the provider that RoadMaster intends to cut terrain in this
        /// bounding box. Universe Creator may pre-load the affected chunks or mark
        /// them for healing after the cut.
        /// </summary>
        void NotifyCutRegion(Bounds worldBounds);

        /// <summary>
        /// Cuts a tunnel opening through the terrain surface inside the given
        /// world-space box, and keeps it cut across regenerations. Called by
        /// RoadMaster the moment a tunnel prefab is placed on a spline - the user
        /// never paints holes by hand.
        ///
        /// Returns a handle to pass back to ReleaseTunnelOpening.
        ///
        /// The terrain side removes surface only; the tunnel shell is the prefab's
        /// own mesh. The contract this guarantees is that the cut is exact and
        /// stable, so the prefab mouth meets the terrain edge with no gap.
        /// </summary>
        int RequestTunnelOpening(Bounds worldBounds);

        /// <summary>Fills a previously cut tunnel opening back in.</summary>
        void ReleaseTunnelOpening(int handle);
    }

    /// <summary>
    /// Universe Creator's implementation of the provider side. Wired to the
    /// chunk manager and the terrain builder so queries resolve to real data.
    /// </summary>
    public class RoadMasterLinker : MonoBehaviour, IPCGDataProvider
    {
        /// <summary>
        /// Reference to the UniverseCreatorObject that owns the chunk manager and
        /// the terrain data. Set by the inspector or by the creator at spawn time.
        /// </summary>
        public UniverseCreatorObject universeCreator;

        // Resolves the world component even when the field was left unset, so a
        // hand-placed linker still works.
        private UniverseCreatorObject World
        {
            get
            {
                if (universeCreator == null)
                    universeCreator = GetComponentInParent<UniverseCreatorObject>();
                return universeCreator;
            }
        }

        private ChunkManager Chunks
        {
            get
            {
                UniverseCreatorObject w = World;
                return w != null ? w.Chunks : null;
            }
        }

        // =====================================================================
        //  PROVIDER IMPLEMENTATION
        //
        //  These resolve against the Module 1 chunk grid. Module 7 consumes them;
        //  nothing here waits on Module 7 to exist.
        // =====================================================================

        public float SampleHeight(float worldX, float worldZ)
        {
            ChunkManager cm = Chunks;
            return cm != null ? cm.SampleHeightAt(worldX, worldZ) : 0f;
        }

        public Vector3 SampleNormal(float worldX, float worldZ)
        {
            ChunkManager cm = Chunks;
            return cm != null ? cm.SampleNormalAt(worldX, worldZ) : Vector3.up;
        }

        public List<Bounds> GetActiveChunkBounds()
        {
            ChunkManager cm = Chunks;
            return cm != null ? cm.GetLoadedChunkBounds() : new List<Bounds>();
        }

        public void NotifyCutRegion(Bounds worldBounds)
        {
            // Module 1 has nothing to pre-load: the whole continent is resident and
            // only its LOD varies. Kept as a real no-op so Module 7 can call it
            // unconditionally; TerrainHealer will hook in here.
        }

        public int RequestTunnelOpening(Bounds worldBounds)
        {
            ChunkManager cm = Chunks;
            if (cm == null)
            {
                Debug.LogWarning("Universe Creator: tunnel opening requested but no ChunkManager found.", this);
                return -1;
            }

            return cm.CarveTunnel(worldBounds);
        }

        public void ReleaseTunnelOpening(int handle)
        {
            ChunkManager cm = Chunks;
            if (cm != null) cm.ClearCarve(handle);
        }
    }
}
