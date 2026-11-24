// UC-T09-03: presentation follows accepted chunk settlement without disabling native ground or water queries.
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWaterBody
    {
        private ChunkManager presentationChunks;
        private MeshRenderer readinessSurfaceRenderer, readinessHorizonRenderer;

        internal void SetStreamingPresentation(bool ready)
        {
            // Only UC's derived renderers are owned here; authored materials, enabled state and volumes remain intact.
            if (readinessSurfaceRenderer != null) readinessSurfaceRenderer.forceRenderingOff = !ready;
            if (readinessHorizonRenderer != null) readinessHorizonRenderer.forceRenderingOff = !ready;
        }

        private void RegisterStreamingPresentation()
        {
            var world = CarveWorld;
            var chunks = world != null ? world.Chunks : null;
            if (presentationChunks != null && presentationChunks != chunks) presentationChunks.UnregisterReadinessWater(this);
            presentationChunks = chunks;
            presentationChunks?.RegisterReadinessWater(this);
        }

        private void RetireStreamingPresentation()
        {
            presentationChunks?.UnregisterReadinessWater(this);
            presentationChunks = null;
            readinessSurfaceRenderer = null; readinessHorizonRenderer = null;
        }
    }
}
