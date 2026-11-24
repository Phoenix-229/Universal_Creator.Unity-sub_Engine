// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    EditModeChunkDriver.cs
//  Purpose: Pumps finished chunk generations onto TerrainData while the editor is
//           NOT playing, and puts the world back on screen after anything that
//           tore the managed side of it down.
//
//  WHY THIS EXISTS
//  ---------------
//  ChunkManager.Update only runs in play mode. In edit mode a background chunk
//  would finish and then sit there forever, because nothing on the main thread
//  ever looked at it. Unity gives edit mode exactly one main-thread heartbeat -
//  EditorApplication.update - so that is what we hook.
//
//  TWO RULES THIS FILE IS BUILT AROUND
//  -----------------------------------
//  1. An idle world costs nothing.
//
//     EditorApplication.update is not a fixed-rate clock. Unity ticks it far more
//     often while the mouse is moving, so work done unconditionally in here reads
//     to the user as the terrain recomputing itself in response to the cursor -
//     which is exactly what was reported, and exactly what it looked like. The
//     earlier version ran a full pump for every world on every tick and swept the
//     scene with FindObjectsByType once a second forever, whether or not anything
//     had been asked to generate.
//
//     So: the world list is refreshed when the hierarchy changes, not on a timer,
//     and the pump is gated behind ChunkManager.HasWork. Idle now costs one
//     boolean per world per tick and nothing else. The node math runs when a build
//     is requested and at no other time.
//
//  2. Coming back must not destroy anything.
//
//     The chunk pool is plain C# state on a MonoBehaviour; the 25 Terrain objects
//     are scene state. Opening the scene, recompiling a script and leaving play
//     mode all wipe the first and keep the second, which leaves the world half
//     restored: cells parked by the last play session stay parked, the neighbour
//     links are gone, and - the one that actually made the world disappear -
//     Terrain.drawHeightmap can still be switched off from a bake that was in
//     flight when the editor closed. That field is serialised, so it went into the
//     scene file and came back as an empty world with the water still floating
//     over where the ground had been.
//
//     RestoreSurfaces fixes all three without regenerating: a cell that still has
//     its heightfield is left exactly as it was, sculpting included. Only a cell
//     with no surface at all is queued, because that is the one case with nothing
//     to preserve.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    [InitializeOnLoad]
    public static class EditModeChunkDriver
    {
        private static UniverseCreatorObject[] worlds = new UniverseCreatorObject[0];
        private static readonly Dictionary<UniverseCreatorObject, int> progressIds =
            new Dictionary<UniverseCreatorObject, int>();

        static EditModeChunkDriver()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.hierarchyChanged += Rescan;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // The static constructor runs during the reload itself, where the scene is
            // not necessarily loaded and Terrain state must not be written. delayCall
            // fires on the first editor tick after everything is up.
            EditorApplication.delayCall += RestoreAll;
        }

        // =====================================================================
        //  RESTORE
        // =====================================================================

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            // Only on the way back in. On the way out the scene is about to be reloaded
            // from the pre-play state, so anything written now is discarded - and if the
            // project has scene reload disabled, EnteredEditMode still fires and does
            // the work then.
            if (state == PlayModeStateChange.EnteredEditMode) RestoreAll();
        }

        private static void RestoreAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            Rescan();

            for (int i = 0; i < worlds.Length; i++)
            {
                UniverseCreatorObject w = worlds[i];
                if (w == null) continue;

                ChunkManager cm = w.Chunks;
                if (cm == null) continue;

                cm.Initialize(w);

                int queued;
                try
                {
                    queued = cm.RestoreSurfaces();
                }
                catch (System.Exception e)
                {
                    // A restore that throws must not take the editor tick with it, or the
                    // pump stops for every world in the scene.
                    Debug.LogError("Universe Creator: could not restore '" + w.name + "'. " + e.Message, w);
                    continue;
                }

                // Said out loud only when the restore had to re-bake, because that is the
                // case where the user will see cells fill in and should know why. A world
                // that came back whole says nothing at all.
                if (queued > 0 && string.IsNullOrEmpty(cm.ReadinessDiagnostic)) // UC climate restore: a synchronously failed recovery is not running in the background.
                    Debug.Log("Universe Creator: '" + w.name + "' came back with " + queued
                              + " cell(s) missing their surface - re-baking those in the "
                              + "background. The rest were kept as they were.", w);
            }

            SceneView.RepaintAll();
        }

        // =====================================================================
        //  PUMP
        // =====================================================================

        private static void Rescan()
        {
            worlds = Object.FindObjectsByType<UniverseCreatorObject>(FindObjectsInactive.Exclude);

            // Progress items are keyed on worlds that can be deleted under us. A stale
            // key leaves a task spinning in the bottom-right corner with nothing behind
            // it, which is worse than no report at all.
            List<UniverseCreatorObject> stale = null;
            foreach (KeyValuePair<UniverseCreatorObject, int> kvp in progressIds)
            {
                if (kvp.Key != null) continue;
                if (kvp.Value != -1 && Progress.Exists(kvp.Value))
                    Progress.Finish(kvp.Value, Progress.Status.Canceled);
                if (stale == null) stale = new List<UniverseCreatorObject>();
                stale.Add(kvp.Key);
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) progressIds.Remove(stale[i]);
        }

        private static void OnEditorUpdate()
        {
            // Play mode drives itself through ChunkManager.Update.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            bool wrote = false;

            for (int i = 0; i < worlds.Length; i++)
            {
                UniverseCreatorObject w = worlds[i];
                if (w == null) continue;

                ChunkManager cm = w.Chunks;
                if (cm == null) continue;

                // THE IDLE GATE. Everything below this line is only reached while a build
                // is actually outstanding. A settled world falls through here on one
                // boolean and the editor tick costs nothing, however fast the mouse moves.
                if (!cm.HasWork)
                {
                    CloseProgress(w);
                    continue;
                }

                if (cm.PumpCompleted()) wrote = true;

                OpenOrReportProgress(w, cm);
            }

            // Writing TerrainData does not mark the Scene view dirty by itself, so a
            // finished chunk could sit invisible until some unrelated event happened to
            // redraw the viewport - which reads as the graph edit having done nothing.
            // Only when something was actually written - C-14.
            if (wrote) SceneView.RepaintAll();
        }

        private static void OpenOrReportProgress(UniverseCreatorObject w, ChunkManager cm)
        {
            if (!cm.IsGenerating) return;

            int id;
            if (!progressIds.TryGetValue(w, out id)) id = -1;

            if (id == -1 || !Progress.Exists(id))
            {
                id = Progress.Start("Universe Creator", "Generating chunks...", Progress.Options.None);
                progressIds[w] = id;
            }

            Progress.Report(id, cm.GetProgress(), cm.GetProgressLabel());
        }

        private static void CloseProgress(UniverseCreatorObject w)
        {
            int id;
            if (!progressIds.TryGetValue(w, out id) || id == -1) return;

            if (Progress.Exists(id)) Progress.Finish(id, Progress.Status.Succeeded);
            progressIds[w] = -1;
        }
    }
}
