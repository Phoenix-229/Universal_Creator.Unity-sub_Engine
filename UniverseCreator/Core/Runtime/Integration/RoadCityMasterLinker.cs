// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    RoadCityMasterLinker.cs
//  Purpose: STUB. Exposes Universe Creator's terrain data to Road & City Master
//           (Module 7) through the IPCGDataProvider contract. The interface is
//           real and final; the implementation lands in Module 7.
//
//  CRITICAL FRAMING: Universe Creator is one engine subsystem; Road & City Master
//  is another, owned by Module 7. They communicate through a formal contract, not
//  by reaching into each other's internals. This class is the "provider" side of
//  that contract — it publishes terrain surface data, chunk bounds, and
//  heightfield queries that the path tool reads when laying splines.
//
//  NAMING: this file is named for the path tool by the author's explicit ruling,
//  which overrides C-05 for the linker pair specifically (see Module 6 § 10.0b).
//  The interface it implements stays product-neutral, so a different path tool can
//  register against it without any rename.
//
//  PDF REFERENCE: Section 8.2 (path tool integration).
// -----------------------------------------------------------------------------

using UnityEngine;
using System.Collections.Generic;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Integration
{
    /// <summary>
    /// Contract: Universe Creator publishes terrain data; Road & City Master
    /// reads it.
    /// </summary>
    public interface IPCGDataProvider
    {
        /// <summary>
        /// Returns the world-space height at (x, z). Called by the path tool when
        /// snapping a spline control point to the surface.
        /// </summary>
        float SampleHeight(float worldX, float worldZ);

        /// <summary>
        /// Returns the surface normal at (x, z). Used for banking and slope analysis.
        /// </summary>
        Vector3 SampleNormal(float worldX, float worldZ);

        /// <summary>
        /// Returns the list of active chunk bounds, so the path tool knows which
        /// regions are loaded and can defer spline operations on unloaded chunks.
        /// </summary>
        List<Bounds> GetActiveChunkBounds();

        /// <summary>
        /// Notifies the provider that the path tool intends to cut terrain in this
        /// bounding box. Universe Creator may pre-load the affected chunks or mark
        /// them for healing after the cut.
        /// </summary>
        void NotifyCutRegion(Bounds worldBounds);

        /// <summary>
        /// Cuts a tunnel opening through the terrain surface inside the given
        /// world-space box, and keeps it cut across regenerations. Called by the
        /// path tool the moment a tunnel prefab is placed on a spline - the user
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
    public class RoadCityMasterLinker : MonoBehaviour, IPCGDataProvider
    {
        /// <summary>
        /// Reference to the UniverseCreatorObject that owns the chunk manager and
        /// the terrain data. Set by the inspector or by the creator at spawn time.
        /// </summary>
        public UniverseCreatorObject universeCreator;

        // =====================================================================
        //  ROAD BEHAVIOUR  -  DECLARED, NOT IMPLEMENTED
        //
        //  The provider half above is live: height, normal, bounds and tunnel
        //  carving all resolve against the real chunk grid today. What is not
        //  written is the consumer half - how a road decides where to go and what
        //  it does to the ground it crosses.
        //
        //  These name that half, with the units and the constraints the terrain
        //  side already imposes, so the module that writes it does not invent a
        //  second set of answers. Nothing reads them yet.
        // =====================================================================

        [Header("Road Behaviour  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Metres either side of a road centre line that the " +
                 "terrain is flattened to meet it.")]
        public float roadWidthMetres = 7f;

        [Tooltip("PLACEHOLDER. Metres either side of the road over which the " +
                 "flattening blends back into the natural ground.")]
        public float shoulderBlendMetres = 4f;

        [Tooltip("PLACEHOLDER. Steepest gradient a road will climb, as a percentage. " +
                 "Past this the router cuts, tunnels or switches back.")]
        [Range(0f, 45f)]
        public float maxGradientPercent = 12f;

        [Tooltip("PLACEHOLDER. Metres of cut depth past which a tunnel is cheaper " +
                 "than a cutting, so the router bores instead of digging.")]
        public float tunnelThresholdMetres = 14f;

        [Tooltip("PLACEHOLDER. Metres of fill height past which a bridge is cheaper " +
                 "than an embankment.")]
        public float bridgeThresholdMetres = 8f;

        [Tooltip("PLACEHOLDER. Roads flatten the terrain under them. Off leaves the " +
                 "ground alone and lets the road mesh float or sink with it.")]
        public bool conformTerrainToRoad = true;

        [Tooltip("PLACEHOLDER. A cut region is re-flattened after the terrain cell " +
                 "under it regenerates, so a rebuild does not swallow the road.")]
        public bool reapplyAfterRegenerate = true;

        [Tooltip("PLACEHOLDER. Metres of clearance kept clear of vegetation either " +
                 "side of a road.")]
        public float vegetationClearanceMetres = 2.5f;

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
            // only its LOD varies, so there is still no terrain work to do here.
            //
            // What there is now is vegetation work (V-23). A road cut is a fact about
            // the world that the road system is the only one who knows, and recording
            // it is what lets the next scatter bake keep trees off the carriageway.
            // TerrainHealer will hook in alongside this.
            UniverseCreator.Ecology.CutRegionRegistry.Shared.Add(worldBounds);
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
