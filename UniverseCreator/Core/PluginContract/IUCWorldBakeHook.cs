// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1)
//  File:    IUCWorldBakeHook.cs
//  Purpose: Extension point: a plugin observes/participates in a bake at its two
//           boundaries. OnBakeBegin lets it prime caches or publish inputs before
//           chunks generate; OnBakeEnd lets it finalise once generation is done.
//           Both run on the main thread, so a hook may touch Unity objects.
//
//  WHY TWO EXPLICIT BOUNDARIES
//  ---------------------------
//  The Linker reached into UC's bake at hard-coded points. A hook is the inverse:
//  the host owns the bake and simply announces its edges to whoever registered. A
//  plugin that needs nothing here implements nothing; a plugin that does gets a
//  stable, named place instead of a patch into engine code - Unreal's "add features
//  without modifying engine code", done as a callback the host drives.
// -----------------------------------------------------------------------------

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// A plugin implements this to run work at the start and end of a world bake.
    /// Both methods are called on the main thread; neither must block for long. The
    /// host swallows and reports an exception from a hook rather than letting one
    /// plugin fail the whole bake.
    /// </summary>
    public interface IUCWorldBakeHook
    {
        /// <summary>Called once, main thread, before any chunk is dispatched.</summary>
        void OnBakeBegin(UCPluginContext ctx);

        /// <summary>Called once, main thread, after all chunks have finished.</summary>
        void OnBakeEnd(UCPluginContext ctx);
    }
}
