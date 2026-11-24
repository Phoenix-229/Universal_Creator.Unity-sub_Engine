// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1)
//  File:    UCPluginContext.cs
//  Purpose: The read-only handle bundle the HOST hands to a plugin when it calls a
//           contract method. It carries the world's spatial datum (bounds, sea
//           level, pixel size), a cancellation token, and - after a bake finishes -
//           sampling delegates the host fills so a plugin can read finished world
//           data WITHOUT referencing any sub-engine type (Matrix, TileData).
//
//  WHY DELEGATES AND NOT UC TYPES
//  ------------------------------
//  The contract assembly references no other UC assembly, so it cannot name Matrix,
//  TileData or Terrain. Exposing readback as System.Func delegates the host plugs in
//  keeps the surface tiny and keeps the one-way dependency intact: the plugin reads
//  through a handed-in function, it never reaches into the engine. The delegates are
//  null until the host has data to offer (e.g. before a bake completes), so a plugin
//  always null-checks before sampling.
//
//  DIRECTION OF CONTROL
//  --------------------
//  The host CONSTRUCTS this and passes it in; a plugin only reads it. Nothing a
//  plugin writes here flows back - it is a context, not a channel.
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using UnityEngine;

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// Read-only world context the host passes to a plugin on every contract call.
    /// Spatial fields are always set; sampling delegates are set only once the host
    /// has finished data to read, so a plugin null-checks each before using it.
    /// </summary>
    public sealed class UCPluginContext
    {
        /// <summary>World-space bounds of the region being baked, in metres.</summary>
        public Bounds WorldBounds;

        /// <summary>Sea level in metres (world Y), the datum water bodies sit against.</summary>
        public float SeaLevelMetres;

        /// <summary>Ground sample spacing in metres - the heightfield's world resolution.</summary>
        public float PixelSizeMetres;

        /// <summary>Cooperative cancellation for a long-running bake. Default = none.</summary>
        public CancellationToken Cancellation;

        /// <summary>
        /// (worldX, worldZ) -&gt; finished ground height in metres. Null until a bake
        /// has produced heights; a consumer must null-check before calling.
        /// </summary>
        public Func<float, float, float> SampleHeightMetres;

        /// <summary>
        /// (worldX, worldZ) -&gt; water surface level in metres, or <see cref="float.NaN"/>
        /// where there is no water body. Null until water has been resolved.
        /// </summary>
        public Func<float, float, float> SampleWaterLevelMetres;

        /// <summary>
        /// (worldX, worldZ) -&gt; biome index (see the host's biome enum), or -1 where
        /// unclassified. Null until the biome field exists.
        /// </summary>
        public Func<float, float, int> SampleBiomeIndex;

        /// <summary>True when the host has populated the height sampler.</summary>
        public bool HasHeightData { get { return SampleHeightMetres != null; } }
    }
}
