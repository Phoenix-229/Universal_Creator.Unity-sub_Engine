// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    AsyncProgressUI.cs
//  Purpose: Reports background generation progress into Unity's bottom-right task area.
//
//  WHY THIS LIVES IN EDITOR CODE
//  -----------------------------
//  UnityEditor.Progress is editor-only. Calling it from a MonoBehaviour would
//  break player builds, so UniverseCreatorObject exposes IsGenerating/CurrentTask
//  and this class polls them from EditorApplication.update instead.
//
//  PDF Section 19.3: never EditorUtility.DisplayProgressBar - that is modal and
//  blocks the editor. Progress.Report shows in the background task panel and
//  leaves the editor fully responsive.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Non-blocking progress reporting for background terrain generation.
    /// Tracks one entry per UniverseCreatorObject so several worlds can generate
    /// at once without their progress bars colliding.
    /// </summary>
    [InitializeOnLoad]
    public static class AsyncProgressUI
    {
        private class Entry
        {
            public int progressId;
            public Task task;
            public UniverseCreatorObject owner;
        }

        private static readonly List<Entry> active = new List<Entry>();

        static AsyncProgressUI()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        /// <summary>
        /// Registers a running generation task. Safe to call repeatedly for the
        /// same owner: an existing entry is cancelled and replaced, which is what
        /// happens when the user edits a parameter mid-generation.
        /// </summary>
        public static void Track(UniverseCreatorObject owner, Task task, string label)
        {
            if (owner == null || task == null) return;

            // Retire any previous bar for this owner so the panel does not stack.
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].owner != owner) continue;
                FinishSafe(active[i].progressId, Progress.Status.Canceled);
                active.RemoveAt(i);
            }

            int id = Progress.Start("Universe Creator", label, Progress.Options.None);

            // The Cancel button on the progress item routes back into the token.
            Progress.RegisterCancelCallback(id, () =>
            {
                owner.CancelGeneration();
                return true;
            });

            active.Add(new Entry { progressId = id, task = task, owner = owner });
        }

        // Polled from EditorApplication.update. The worker thread cannot touch the
        // Progress API, so completion is detected here on the main thread.
        private static void Poll()
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                Entry e = active[i];

                if (e.owner == null)
                {
                    // Object was deleted mid-generation.
                    FinishSafe(e.progressId, Progress.Status.Canceled);
                    active.RemoveAt(i);
                    continue;
                }

                if (!e.task.IsCompleted)
                {
                    // Indeterminate: chunk-level step counts arrive with the 25+1
                    // grid loop, which is not wired yet.
                    Progress.Report(e.progressId, -1f, "Evaluating graph...");
                    continue;
                }

                Progress.Status status = Progress.Status.Succeeded;
                if (e.task.IsFaulted) status = Progress.Status.Failed;
                else if (e.task.IsCanceled) status = Progress.Status.Canceled;

                FinishSafe(e.progressId, status);
                active.RemoveAt(i);

                if (e.task.IsFaulted && e.task.Exception != null)
                    Debug.LogError("Universe Creator: generation failed - " +
                                   e.task.Exception.GetBaseException().Message);
            }
        }

        // Progress ids go stale across domain reloads; finishing an unknown id throws.
        private static void FinishSafe(int id, Progress.Status status)
        {
            try
            {
                if (Progress.Exists(id)) Progress.Finish(id, status);
            }
            catch (System.Exception)
            {
                // Nothing actionable - the bar is already gone.
            }
        }
    }
}
