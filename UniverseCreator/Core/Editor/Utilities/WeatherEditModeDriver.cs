// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WeatherEditModeDriver.cs
//  Purpose: Makes the one clock visibly run in the Scene view while the editor is
//           NOT playing, so an author can watch a day pass without entering play.
//
//  WHY THIS EXISTS
//  ---------------
//  The weather manager is [ExecuteAlways], so its Update advances the clock in edit
//  mode as well as play. But a bare editor tick does not run the player loop, and it
//  does not redraw the viewport: the accumulator would creep forward only when some
//  unrelated event happened to pump Unity, which reads as a clock that runs when the
//  mouse moves and freezes when it stops. This driver closes that gap - while a clock
//  is actually running it asks Unity for one player-loop iteration (which is what runs
//  the ExecuteAlways Updates) and then repaints, so time advances smoothly on its own.
//
//  IDLE COSTS ONE BOOLEAN PER WORLD. The same rule the terrain edit-mode driver is
//  built around: a settled or paused world falls through on ClockIsRunning and the
//  editor tick costs nothing. Continuous repaints happen only while the author has the
//  clock unpaused, never as a background drain on a still scene.
//
//  THE SUBSCRIPTION CANNOT DANGLE. [InitializeOnLoad] re-runs this static constructor
//  on every assembly reload with the static fields wiped, so the += lands on a fresh
//  delegate rather than stacking a second copy onto a surviving one - which is the
//  classic way an editor pump doubles its tick rate after a recompile. Same lifecycle
//  the terrain driver already relies on.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    [InitializeOnLoad]
    public static class WeatherEditModeDriver
    {
        private static UCWeatherSystemManager[] managers = new UCWeatherSystemManager[0];

        static WeatherEditModeDriver()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.hierarchyChanged += Rescan;

            // The static constructor runs during the reload, where the scene is not
            // reliably loaded yet. delayCall fires on the first editor tick after
            // everything is up, which is when a FindObjects sweep is meaningful.
            EditorApplication.delayCall += Rescan;
        }
        // Refreshed only when the hierarchy changes, never per tick: an idle scene with
        // a still clock never re-sweeps, so the driver adds nothing to a quiet editor.
        // Inactive managers are excluded because a disabled one cannot tick anyway and
        // ClockIsRunning would reject it on isActiveAndEnabled regardless.
        private static void Rescan()
        {
            managers = Object.FindObjectsByType<UCWeatherSystemManager>(
                FindObjectsInactive.Exclude);
        }

        private static void OnEditorUpdate()
        {
            // Play mode runs the real player loop and repaints itself; pumping it here
            // would double the tick. This driver is strictly the edit-mode stand-in.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (managers == null || managers.Length == 0) return;

            // The one boolean per world: fall straight through unless some clock is
            // actually advancing. A paused or zero-scale world costs a null check each.
            bool anyRunning = false;
            for (int i = 0; i < managers.Length; i++)
            {
                UCWeatherSystemManager m = managers[i];
                if (m != null && m.ClockIsRunning) { anyRunning = true; break; }
            }
            if (!anyRunning) return;

            // One player-loop iteration runs every ExecuteAlways Update (the manager's
            // clock among them), then the repaint shows the advance. Together they make
            // edit-mode time move on its own rather than only when the editor is nudged.
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
