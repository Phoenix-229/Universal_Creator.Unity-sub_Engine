// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WeatherEditModeDriver.cs
//  Purpose: Makes the one clock visibly run in the Scene view while the editor is
//           NOT playing, so an author can watch a day pass without entering play.
//
//  WHY THIS EXISTS
//  ---------------
//  The weather manager advances its clock through this editor driver in edit
//  mode and through Update in play. The editor supplies monotonic elapsed time, and it
//  does not redraw the viewport: the accumulator would creep forward only when some
//  unrelated event happened to pump Unity, which reads as a clock that runs when the
//  mouse moves and freezes when it stops. This driver closes that gap - while a clock
//  is actually running it steps the existing manager and asks Unity for a player-loop
//  iteration and repaint, so other ExecuteAlways consumers see the same instant.
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
        private static UCWeatherSystemManager preview;
        private static double previewStartInstant;
        private static double lastEditorTime;
        private static double nextRepaint;
        private static readonly PreviewViewState[] previewViews = new PreviewViewState[8];

        private struct PreviewViewState
        {
            internal SceneView View;
            internal Camera Camera;
            internal bool Lighting, Skybox, ImageEffects, EffectsEnabled;
        }

        /// <summary>The existing world clock temporarily previewed in the Scene view; null when stopped.</summary>
        internal static UCWeatherSystemManager PreviewOwner => preview;

        /// <summary>Starts a reversible Scene view preview using the world's saved game rate.</summary>
        /// <param name="manager">Enabled scene weather owner with a finite positive timeScale; persistent assets are rejected.</param>
        /// <returns>True when preview starts; false for Play mode or an unavailable/invalid owner.</returns>
        internal static bool StartPreview(UCWeatherSystemManager manager)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || manager == null || !manager.isActiveAndEnabled ||
                EditorUtility.IsPersistent(manager) || float.IsNaN(manager.timeScale) || float.IsInfinity(manager.timeScale) || manager.timeScale <= 0) return false;
            StopPreview();
            preview = manager; previewStartInstant = manager.WorldInstantSeconds;
            lastEditorTime = EditorApplication.timeSinceStartup;
            foreach (SceneView view in SceneView.sceneViews) PreparePreviewView(view);
            SceneView.RepaintAll();
            return true;
        }

        /// <summary>Stops preview and restores its starting instant; authored pause/rate settings are never replaced.</summary>
        /// <remarks>Also called before assembly reload, scene save, quitting and Play mode entry. Restores Scene lighting/skybox toggles and retires borrowed camera resources.</remarks>
        internal static void StopPreview()
        {
            var previous = preview; preview = null;
            if (previous != null) previous.ScenePreviewCamera = null;
            for (int i = previewViews.Length - 1; i >= 0; i--)
            {
                var saved = previewViews[i]; previewViews[i] = default;
                if (previous != null) previous.RetireScenePreviewCamera(saved.Camera);
                if (saved.View == null) continue;
                saved.View.sceneLighting = saved.Lighting;
                saved.View.sceneViewState.showSkybox = saved.Skybox;
                saved.View.sceneViewState.showImageEffects = saved.ImageEffects;
                saved.View.sceneViewState.fxEnabled = saved.EffectsEnabled;
            }
            if (previous != null) previous.SeekWorldInstant(previewStartInstant);
            SceneView.RepaintAll();
        }

        /// <summary>Connects an editor Scene view to the current preview and temporarily enables its light and sky recipients.</summary>
        /// <param name="view">Scene window about to render; null or a missing camera is rejected without changing its settings.</param>
        /// <returns>True for an admitted view; false outside preview/inside Play mode or when eight borrowed windows are occupied.</returns>
        internal static bool PreparePreviewView(SceneView view)
        {
            if (preview == null || !preview.isActiveAndEnabled || view == null ||
                EditorApplication.isPlayingOrWillChangePlaymode || view.camera == null) return false;
            for (int i = 0; i < previewViews.Length; i++)
            {
                if (previewViews[i].View != null && previewViews[i].View != view) continue;
                if (previewViews[i].View == null)
                {
                    if (previewViews[i].Camera != null) preview.RetireScenePreviewCamera(previewViews[i].Camera);
                    previewViews[i] = new PreviewViewState {
                        View = view, Camera = view.camera, Lighting = view.sceneLighting, Skybox = view.sceneViewState.showSkybox,
                        ImageEffects = view.sceneViewState.showImageEffects, EffectsEnabled = view.sceneViewState.fxEnabled
                    };
                }
                // UC-T08-15: use the existing per-camera weather/lighting renderer instead of an always-running edit-mode lighting Update.
                preview.ScenePreviewCamera = view.camera;
                view.sceneLighting = true; view.sceneViewState.showSkybox = true;
                // UC midday repair: opted-in HDR tonemapping needs the Scene postprocessing recipient; Stop restores both toggles.
                bool tonemapping = preview.ScenePreviewUsesModuleTonemapping;
                view.sceneViewState.showImageEffects = tonemapping || previewViews[i].ImageEffects;
                view.sceneViewState.fxEnabled = tonemapping || previewViews[i].EffectsEnabled;
                return true;
            }
            return false;
        }

        /// <summary>Advances only the previewed owner's existing clock/consumers, temporarily releasing its pause gate.</summary>
        /// <param name="realSeconds">Finite nonnegative elapsed real seconds, validated by the shared clock before mutation.</param>
        internal static void StepPreview(double realSeconds)
        {
            if (preview == null) return;
            bool paused = preview.pauseTime;
            try { preview.pauseTime = false; preview.StepEnvironmentClock(realSeconds); }
            finally { if (preview != null) preview.pauseTime = paused; }
        }

        static WeatherEditModeDriver()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.hierarchyChanged += Rescan;
            lastEditorTime = EditorApplication.timeSinceStartup;
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
            EditorApplication.quitting += StopPreview;
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingEditMode) StopPreview(); };
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += (scene, path) => StopPreview();
            SceneView.beforeSceneGui += view => PreparePreviewView(view);

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
            double now = EditorApplication.timeSinceStartup;
            double elapsed = System.Math.Max(0, now - lastEditorTime); lastEditorTime = now;
            // Play mode runs the real player loop and repaints itself; pumping it here
            // would double the tick. This driver is strictly the edit-mode stand-in.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!ReferenceEquals(preview, null) && (preview == null || !preview.isActiveAndEnabled)) StopPreview();

            // The one boolean per world: fall straight through unless some clock is
            // actually advancing. A paused or zero-scale world costs a null check each.
            bool anyRunning = preview != null;
            if (preview != null) StepPreview(elapsed);
            for (int i = 0; i < managers.Length; i++)
            {
                UCWeatherSystemManager m = managers[i];
                if (m != null && m != preview && m.ClockIsRunning) { m.StepEnvironmentClock(elapsed); anyRunning = true; }
            }
            if (!anyRunning || now < nextRepaint) return;
            nextRepaint = now + 1d / 30; // UC-T08-15: bounded editor refresh; elapsed time is accumulated independently of repaint cadence.

            // The clock has already advanced. The existing per-camera renderer publishes
            // preview lighting during the repaint; lighting's idle edit-mode Update stays gated.
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
