// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCBatchRunner.cs
//  Purpose: Runs a list of editor-side jobs one item per editor tick, reporting
//           into Unity's bottom-right task area.
//
//  WHY NOT JUST A FOR LOOP
//  -----------------------
//  "Rebuild all water" over forty rivers is forty spline walks and forty mesh
//  builds. In a straight loop the editor stops repainting until the last one
//  lands - no cursor, no cancel, no way to tell whether it is working or hung.
//
//  Stepping from EditorApplication.update instead means the editor paints between
//  items. The job takes marginally longer in wall-clock terms and the editor stays
//  usable the whole way through, which is the trade this toolkit makes everywhere.
//
//  NO MODAL DIALOGS. Progress.Start/Report/Finish put a cancellable entry in the
//  bottom-right task area; EditorUtility.DisplayProgressBar would take the editor
//  hostage and is never used.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Steps through a list of items one per editor tick with a cancellable
    /// background progress entry. Editor-only.
    /// </summary>
    public class UCBatchRunner
    {
        // One runner per key. A second "Rebuild All" click while the first is still
        // going replaces it rather than running two interleaved passes over the
        // same bodies.
        private static readonly Dictionary<string, UCBatchRunner> running =
            new Dictionary<string, UCBatchRunner>();

        private readonly string key;
        private readonly string title;
        private readonly int count;
        private readonly Action<int> step;
        private readonly Action done;

        private int index;
        private int progressId = -1;
        private bool cancelled;

        private UCBatchRunner(string key, string title, int count, Action<int> step, Action done)
        {
            this.key = key;
            this.title = title;
            this.count = count;
            this.step = step;
            this.done = done;
        }

        /// <summary>
        /// Starts a batch. key identifies the operation so a repeat click replaces
        /// the running one; step is called once per index on the main thread; done
        /// runs after the last step, or after a cancel, and may be null.
        /// </summary>
        public static void Run(string key, string title, int count, Action<int> step, Action done)
        {
            if (count <= 0 || step == null)
            {
                if (done != null) done();
                return;
            }

            Cancel(key);

            UCBatchRunner runner = new UCBatchRunner(key, title, count, step, done);
            running[key] = runner;
            runner.Begin();
        }

        /// <summary>Cancels a running batch by key. Does nothing if none is running.</summary>
        public static void Cancel(string key)
        {
            UCBatchRunner existing;
            if (!running.TryGetValue(key, out existing)) return;

            existing.cancelled = true;
            existing.Finish(Progress.Status.Canceled);
        }

        /// <summary>Whether a batch with this key is currently stepping.</summary>
        public static bool IsRunning(string key)
        {
            return running.ContainsKey(key);
        }

        private void Begin()
        {
            progressId = Progress.Start("Universe Creator", title, Progress.Options.None);

            Progress.RegisterCancelCallback(progressId, () =>
            {
                cancelled = true;
                return true;
            });

            EditorApplication.update += Tick;
        }

        private void Tick()
        {
            if (cancelled)
            {
                Finish(Progress.Status.Canceled);
                return;
            }

            if (index >= count)
            {
                Finish(Progress.Status.Succeeded);
                return;
            }

            int current = index;
            index++;

            try
            {
                step(current);
            }
            catch (Exception e)
            {
                // One bad item must not abandon the rest of the batch, and must not
                // leave a progress entry stuck in the task area forever.
                Debug.LogError("Universe Creator: " + title + " failed on item " +
                               current + ". " + e.Message);
            }

            if (Progress.Exists(progressId))
                Progress.Report(progressId, (float)index / count,
                                index + " of " + count);
        }

        private void Finish(Progress.Status status)
        {
            EditorApplication.update -= Tick;

            // Progress ids go stale across a domain reload; finishing an unknown id
            // throws, and this runs from a callback where a throw is silent.
            try
            {
                if (progressId >= 0 && Progress.Exists(progressId))
                    Progress.Finish(progressId, status);
            }
            catch (Exception)
            {
                // Nothing actionable - the entry is already gone.
            }

            progressId = -1;

            UCBatchRunner mine;
            if (running.TryGetValue(key, out mine) && mine == this) running.Remove(key);

            if (done != null) done();
        }
    }
}
