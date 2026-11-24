// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    DomainReloadGuard.cs
//  Purpose: Stops in-flight chunk generation before Unity unloads the managed
//           domain, and before entering play mode.
//
//  WHY THIS EXISTS
//  ---------------
//  Chunk generation runs on Task.Run worker threads that read the graph, the
//  node instances and the TileData buffers - all managed objects. A domain
//  reload destroys every one of them. Unity will not start that reload while it
//  cannot establish a safe point, so instead of reloading it posts the modal
//  "Hold on... Application.Tick - Waiting for Unity's code to finish executing"
//  and waits, sometimes for as long as the slowest node takes. Save a script
//  while a 56 km world is baking and that is a locked editor.
//
//  The fix is to be the thing that finishes first: on beforeAssemblyReload,
//  cancel every worker and wait for it to leave, with a timeout so a node that
//  ignores cancellation cannot deadlock the editor either. Play mode entry is
//  the same problem in a different costume - it reloads the domain too, unless
//  the project has disabled that, in which case cancelling is still correct
//  because ChunkManager.RebuildAll is about to re-dispatch the whole grid.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    [InitializeOnLoad]
    public static class DomainReloadGuard
    {
        // Long enough for a node to notice the stop flag between iterations, short
        // enough that a misbehaving one costs a pause rather than a hang.
        private const int DrainTimeoutMs = 2000;

        static DomainReloadGuard()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.quitting += DrainAll; // UC-T07-10: final native preview inputs retire before editor shutdown.
            EditorSceneManager.sceneClosing += (scene, removing) => { TextureGraphBake.CancelAll(); GraphPreviewScheduler.CancelScenePreviews(); }; // UC-T07-10: workers retain their immutable scene inputs until exit.
        }

        private static void OnBeforeAssemblyReload()
        {
            DrainAll();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.ExitingPlayMode)
            {
                DrainAll();
            }
        }

        private static void DrainAll()
        {
            TextureGraphBake.CancelAll(); // UC-T07-10: prepared main-thread bakes have no in-flight GPU readback to wait for.
            if (!GraphPreviewScheduler.CancelAndDrain(DrainTimeoutMs))
                Debug.LogError("UC-T07-10: a preview worker did not retire within the two-second reload/shutdown drain; its input/resource pins remain owned until it exits.");
            // Inactive included: a disabled world can still hold threads that were
            // dispatched while it was enabled, and those read the same managed heap.
            ChunkManager[] managers =
                Object.FindObjectsByType<ChunkManager>(FindObjectsInactive.Include);

            for (int i = 0; i < managers.Length; i++)
            {
                if (managers[i] == null) continue;
                managers[i].CancelAndDrain(DrainTimeoutMs);
            }
        }
    }
}
