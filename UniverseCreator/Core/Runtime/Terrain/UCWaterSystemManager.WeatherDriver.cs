// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    UCWaterSystemManager.WeatherDriver.cs  (partial of UCWaterSystemManager)
//  Purpose: THE ONE PER-FRAME WIRE FROM THE WEATHER TO THE WATER. The coupling math
//           (rain density, storm swell, freeze fraction, murk) already lived in
//           UCWaterWeatherCoupling, and every consumer - the ripple sim, the surface
//           shader, the buoyancy solve, the underwater view - already read what it
//           produces. Nothing called it. So a downpour left the lakes dead flat, a
//           gale never raised the swell, a hard frost never skinned a pond, and diving
//           in a storm was as clear as diving at noon. This file is the missing caller.
//
//  IT OWNS NO WEATHER MATH. Every value comes from the same snapshot the sky and the
//  ground read - UCWeatherSystemManager.ResolveWeatherFor - and this only hands it to
//  UCWaterWeatherCoupling.Apply. One reader, one snapshot: the water cannot disagree
//  with the sky about whether it is storming.
//
//  EDIT MODE IS EVENT-DRIVEN, exactly as the lighting driver is (UCLightingSystemManager
//  .Driver.cs): the apply runs from OnEnable and OnValidate, never a per-frame
//  ExecuteAlways Update, so the scene view shows the stormed or frozen surface after a
//  change without keeping the editor loop hot over water nothing is looking at. Play
//  mode runs it per frame, where the weather actually advances with the clock.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWaterSystemManager
    {
        [Header("Weather Drive")]

        [Tooltip("Let the weather drive the water: rain dimples the surface, storms " +
                 "raise the swell, a hard freeze skins still lakes and pools over, and " +
                 "murk closes the underwater view down in a storm.\n\n" +
                 "Off leaves every body at its own calm authored values, and returns " +
                 "the world to calm the moment it is switched off. On is the living " +
                 "world; the numbers all come from the same weather the sky reads.")]
        public bool weatherDriveEnabled = true;

        // Play mode only: the weather advances with the clock, so the water is driven
        // every frame. Gated off in edit mode - ExecuteAlways would otherwise re-walk
        // every water body once per editor frame to answer a question nothing is asking;
        // the event path below covers edit mode instead.
        private void Update()
        {
            if (!Application.isPlaying) return;

            // The quality tier can change at runtime (AdaptiveQuality lowers it under load),
            // so check it every frame - but it only re-applies when the resolved tier
            // actually changed, so a steady tier costs one enum compare, not a walk.
            ApplyQualityIfChanged();

            if (!weatherDriveEnabled) return;

            ApplyWeather();
        }
        // Returned to calm when the manager stops driving, so a surface disabled mid-storm
        // does not stay stormed with nothing feeding it. Domain reloads and scene unloads
        // reach here too, where the walk is a harmless no-op on an empty registry.
        private void OnDisable()
        {
            UCWaterWeatherCoupling.Calm(World); // UC-T08-05: retiring this world's adapter never clears another world's water.
        }

        // Edit-mode entry, called from OnEnable and OnValidate in the main file. Applies
        // the current weather once, or calms the water when the drive is switched off, so
        // toggling the box in the inspector takes effect immediately in the scene view.
        private void ApplyWeatherEvent()
        {
            if (weatherDriveEnabled) ApplyWeather();
            else UCWaterWeatherCoupling.Calm(World);
        }

        /// <summary>
        /// Reads the weather at this manager and drives every drop of water in the world
        /// from it. Exposed through the context menu so an editor preview or a cutscene
        /// can force a frame.
        /// </summary>
        private void ApplyWeather()
        {
            UCWaterWeatherCoupling.Apply(World, UCWeatherSystemManager.FindForWorld(transform));
        }

        /// <summary>Editor entry point: storm, freeze and murk the water now, from the
        /// current world weather.</summary>
        [ContextMenu("Apply Weather Now")]
        private void ApplyWeatherNow() => ApplyWeatherEvent();
    }
}
