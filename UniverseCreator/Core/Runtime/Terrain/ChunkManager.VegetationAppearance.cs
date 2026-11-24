// UC-T06-09: share the existing apply pump, never launch a worker or replace placement for appearance.
namespace UniverseCreator.TerrainSystem
{
    public partial class ChunkManager
    {
        private bool appearanceWorkPending;
        internal void RequestVegetationAppearance() { appearanceWorkPending = true; }

        private bool PumpVegetationAppearance()
        {
            if (!appearanceWorkPending || IsGenerating || rescatterQueue.Count > 0 || Owner == null) return false;
            if (Owner.scatterLibrary == null) { appearanceWorkPending = false; return false; }
            var vegetation = EnsureVegetationManager();
            if (vegetation == null || !vegetation.vegetationEnabled) { appearanceWorkPending = false; return false; }
            foreach (var chunk in byCell.Values)
            {
                if (chunk == null || chunk.go == null || !chunk.go.activeSelf || !chunk.bakedThisSession || chunk.data == null || chunk.terrain == null) continue;
                if (chunk.scatterAppearanceRevision == vegetation.AppearanceRevision) continue;
                if (chunk.appearanceUpdate == null || chunk.appearanceUpdateRevision != vegetation.AppearanceRevision || chunk.appearanceData != chunk.data)
                {
                    chunk.appearanceUpdateRevision = vegetation.AppearanceRevision; chunk.appearanceData = chunk.data;
                    chunk.appearanceObjectCursor = 0;
                    chunk.appearanceUpdate = new ScatterApplier.AppearanceUpdate(chunk.terrain, chunk.data, Owner.scatterLibrary, vegetation);
                    if (Owner.enableGPUInstancing) Instancing().RefreshAppearance(ScatterOwnerOf(chunk)); // UC-T06-09: same explicit request reaches actual cached GPU material consumers.
                }
                int end = (int)System.Math.Min((long)chunk.appearanceObjectCursor + 128, chunk.scatterObjects.Count);
                while (chunk.appearanceObjectCursor < end)
                {
                    var instance = chunk.scatterObjects[chunk.appearanceObjectCursor++];
                    if (instance != null && instance.TryGetComponent<UCShrubInstance>(out var shrub)) shrub.RefreshAppearance();
                }
                // Fixed per-pump tree work; no tree array readback or per-instance allocation.
                if (chunk.appearanceUpdate.Step(256) && chunk.appearanceObjectCursor >= chunk.scatterObjects.Count)
                {
                    chunk.scatterAppearanceRevision = chunk.appearanceUpdateRevision;
                    chunk.appearanceUpdate = null; chunk.appearanceData = null;
                }
                return true;
            }
            appearanceWorkPending = false;
            return false;
        }
    }
}
