// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1)
//  File:    IUCWorldDataConsumer.cs
//  Purpose: Extension point: once a bake has produced finished world data, a plugin
//           reads it to drive its own output - a road tool drapes its meshes onto
//           the final heights, a city tool reads the biome field to pick materials.
//           The data is reached through the sampling delegates on UCPluginContext,
//           so the plugin never references a sub-engine type.
//
//  WHY SEPARATE FROM IUCWorldBakeHook.OnBakeEnd
//  --------------------------------------------
//  OnBakeEnd fires for plugins that PARTICIPATE in the bake (prime/finalise);
//  OnWorldDataReady fires for plugins that only CONSUME the result. A plugin may
//  implement one, the other, or both. Keeping them distinct means the host can hand
//  consumers a context whose sampling delegates are already populated, and keeps a
//  pure reader from having to no-op a begin/end it does not care about.
// -----------------------------------------------------------------------------

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// A plugin implements this to read finished world data after a bake. Called on
    /// the main thread once the bake completes, with a <see cref="UCPluginContext"/>
    /// whose sampling delegates (height/water/biome) are populated. The plugin reads
    /// through those delegates and must not retain the context past the call.
    /// </summary>
    public interface IUCWorldDataConsumer
    {
        /// <summary>Called once, main thread, after a bake; ctx samplers are ready.</summary>
        void OnWorldDataReady(UCPluginContext ctx);
    }
}
