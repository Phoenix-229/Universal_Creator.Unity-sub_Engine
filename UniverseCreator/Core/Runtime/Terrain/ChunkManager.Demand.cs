using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.TerrainSystem
{
    public partial class ChunkManager
    {
        // UC-T02-08: bounded by this world's dispatched cells; running bodies leave this list at the next pump.
        private readonly List<Chunk> waitingAdmission = new List<Chunk>();

        private void RefreshQueuedChunkDemand(UniverseCreatorObject owner)
        {
            if (waitingAdmission.Count == 0) return;
            Camera camera = owner.frustumFirstBuildOrder ? Camera.main : null;
            Transform focus = ResolveFocus();
            Vector3 position = focus != null ? focus.position : Vector3.zero;
            if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            float size = owner.GetChunkSize(), height = owner.maxHeightMeters;
            Vector3 extent = new Vector3(size, height, size);
            for (int i = waitingAdmission.Count - 1; i >= 0; i--)
            {
                Chunk chunk = waitingAdmission[i];
                Vector3 centre = CellCentreWorld(owner, chunk) + extent * 0.5f;
                // Demand bounds are the exact finite cell footprint and authored height range, not the pooled object's old transform.
                Bounds bounds = new Bounds(centre, extent);
                bool visible = camera != null && GeometryUtility.TestPlanesAABB(_frustumPlanes, bounds);
                bool predicted = camera != null && !visible && InForwardStrip(owner, chunk, centreCell, owner.GetStreamRadius() + lodRingBoost);
                float distance = camera != null ? Vector3.Distance(position, centre) :
                    DualLODSystem.RingDistance(chunk.cell.x, chunk.cell.z, centreCell.x, centreCell.z);
                int band = camera != null ? (int)ChunkBuildPriority.Classify(visible, predicted, distance, Mathf.Max(1, owner.drawDistance)) : 0;
                chunk.demand = ResolveRegionPriority(chunk, new GenerationDemand(IsCollisionDemand(owner, chunk), band, distance, chunk.queuedAt));
                if (!owner.GenerationScheduler.UpdateQueuedDemand(chunk.stop, chunk.demand)) waitingAdmission.RemoveAt(i);
            }
        }
    }
}
