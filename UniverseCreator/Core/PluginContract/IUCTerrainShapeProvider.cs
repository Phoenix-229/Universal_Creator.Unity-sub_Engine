// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1)
//  File:    IUCTerrainShapeProvider.cs
//  Purpose: Extension point: a plugin hands the host world-space shaping requests so
//           the sub-engine conforms its own terrain/water to what the plugin built
//           beside it - a road flattens a corridor, a dam impounds a reservoir, a
//           tunnel opens a hole. This is the plugin-era replacement for the Linker's
//           push-an-inbox model: the HOST PULLS from each registered provider, so it
//           never names the plugin and control points one way (plugin -> host data).
//
//  WHY TerrainShapeRequest IS THE PAYLOAD
//  --------------------------------------
//  TerrainShapeRequest is already the sub-engine's own pure, serialisable statement
//  of a shaping ("conform this corridor", "raise this water"): only floats, ints,
//  enums and a Bounds, no interface or foreign-assembly references. It was relocated
//  into this contract assembly for exactly that reason, so a plugin and the host
//  speak the SAME shape type with no duplication and no adapter. Its own existing
//  consumers (the applier, the dam reconciler, the chunk manager) are unchanged.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UniverseCreator.Integration;   // TerrainShapeRequest (relocated into this assembly)

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// A plugin implements this to shape the host's terrain/water. The host calls it
    /// on the main thread at bake prime and stamps whatever is returned exactly where
    /// it stamps its own footprints. Add requests to <paramref name="into"/>; do not
    /// clear it (other providers share the list). A request whose <c>HasWork</c> is
    /// false is ignored by the host, so a provider may add provisional entries.
    /// </summary>
    public interface IUCTerrainShapeProvider
    {
        /// <summary>
        /// Append this provider's shaping requests for the region described by
        /// <paramref name="ctx"/>. Called main-thread, before chunks dispatch.
        /// </summary>
        void CollectTerrainShapes(UCPluginContext ctx, List<TerrainShapeRequest> into);
    }
}
