// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    EditModeChunkDriver.cs
//  Purpose: Pumps finished chunk generations onto TerrainData while the editor is
//           NOT playing.
//
//  WHY THIS EXISTS
//  ---------------
//  ChunkManager.Update only runs in play mode. In edit mode a background chunk
//  would finish and then sit there forever, because nothing on the main thread ever
//  looked at it. Unity gives edit mode exactly one main-thread heartbeat -
//  EditorApplication.update - so that is what we hook.
//
//  This is also what makes "the whole terrain is visible while you build" work:
//  every cell of the grid generates and applies, with no player and no streaming.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    [InitializeOnLoad]
    public static class EditModeChunkDriver
    {
        // Re-scanning every frame would be wasteful; the scene only changes shape
        // occasionally, so we refresh the list on a slow cadence and on hierarchy
        // changes.
        private static UniverseCreatorObject[] worlds = new UniverseCreatorObject[0];
        private static double nextScan;
        private static System.Collections.Generic.Dictionary<UniverseCreatorObject, int> progressIds = new System.Collections.Generic.Dictionary<UniverseCreatorObject, int>();

        static EditModeChunkDriver()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.hierarchyChanged += Rescan;
        }

        private static void Rescan()
        {
            // No sort-mode argument: that overload is deprecated in Unity 6.5, and we
            // do not care about ordering here.
            worlds = Object.FindObjectsByType<UniverseCreatorObject>(FindObjectsInactive.Exclude);

            // Clean up missing worlds from progress dictionary
            var toRemove = new System.Collections.Generic.List<UniverseCreatorObject>();
            foreach (var kvp in progressIds)
            {
                if (kvp.Key == null)
                {
                    if (Progress.Exists(kvp.Value)) Progress.Finish(kvp.Value, Progress.Status.Canceled);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (var w in toRemove) progressIds.Remove(w);
        }

        private static void OnEditorUpdate()
        {
            // Play mode drives itself through ChunkManager.Update.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (EditorApplication.timeSinceStartup >= nextScan)
            {
                nextScan = EditorApplication.timeSinceStartup + 1.0;
                Rescan();
            }

            bool wrote = false;

            for (int i = 0; i < worlds.Length; i++)
            {
                UniverseCreatorObject w = worlds[i];
                if (w == null) continue;
                if (w.PumpChunks()) wrote = true;

                ChunkManager cm = w.Chunks;
                if (cm != null)
                {
                    bool isGen = cm.IsGenerating;
                    progressIds.TryGetValue(w, out int pId);
                    if (!progressIds.ContainsKey(w)) pId = -1;

                    if (isGen && pId == -1)
                    {
                        pId = Progress.Start("Universe Creator", "Generating chunks...", Progress.Options.None);
                        progressIds[w] = pId;
                    }
                    else if (!isGen && pId != -1)
                    {
                        if (Progress.Exists(pId)) Progress.Finish(pId, Progress.Status.Succeeded);
                        progressIds[w] = -1;
                    }

                    if (isGen && pId != -1)
                    {
                        Progress.Report(pId, cm.GetProgress(), "Generating chunks...");
                    }
                }
            }

            // Writing TerrainData does not mark the Scene view dirty by itself, so a
            // finished chunk could sit invisible until some unrelated event happened to
            // redraw the viewport - which reads as the graph edit having done nothing.
            // Repainting only when something was actually written keeps this off the
            // idle path rather than forcing a redraw every editor tick - C-14.
            if (wrote) SceneView.RepaintAll();
        }
    }
}
