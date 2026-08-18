// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCLightingSystemManager.cs
//  Purpose: The LightingSystem child's control surface. DECLARED ONLY - no field
//           here is read by anything yet.
//
//  WHY A FILE THAT DOES NOTHING
//  ----------------------------
//  The lighting and time-of-day module is not written. This component exists so
//  that when it is written it has an agreed shape to fill, and so it inherits the
//  constraints the rest of the system already imposes:
//
//   - URP only. Every shader and every render feature the toolkit ships targets
//     the Universal Render Pipeline. A lighting pass must not reach for built-in
//     or HDRP-only API.
//   - The sun is one directional light, owned here. Water, vegetation and terrain
//     all read the same one; a second sun would double-light the world.
//   - Time of day drives the sun rather than the other way round, so a saved world
//     reopened at the same time of day looks the same.
//   - Weather owns cloud and fog. Lighting reads them. Overcast dimming belongs to
//     whichever of the two runs later, not to both.
//
//  Nothing acts on these fields. A stub that half-worked would be worse than one
//  that does nothing: it would look implemented.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level lighting and time-of-day control. Declared for a later module;
    /// no field here is read by anything yet.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Lighting System Manager")]
    [DisallowMultipleComponent]
    public class UCLightingSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  TIME OF DAY
        // =====================================================================

        [Header("Time of Day  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Master switch for the lighting pass.")]
        public bool lightingEnabled = true;

        [Tooltip("PLACEHOLDER. Hour of the day, 0 to 24. 12 is solar noon.")]
        [Range(0f, 24f)]
        public float timeOfDayHours = 12f;

        [Tooltip("PLACEHOLDER. Time advances by itself.")]
        public bool advanceTime;

        [Tooltip("PLACEHOLDER. Real minutes one in-world day takes.")]
        public float dayLengthMinutes = 20f;

        [Tooltip("PLACEHOLDER. Latitude in degrees. Decides how high the sun climbs " +
                 "and how long twilight lasts - the difference between a tropical " +
                 "world and a polar one.")]
        [Range(-90f, 90f)]
        public float latitudeDegrees = 45f;

        [Tooltip("PLACEHOLDER. Compass bearing of world north, in degrees, so the " +
                 "sun rises where the world says east is.")]
        [Range(0f, 360f)]
        public float northBearingDegrees;

        // =====================================================================
        //  SUN AND MOON
        // =====================================================================

        [Header("Sun and Moon  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. The one directional light this world is lit by. Left " +
                 "empty, the module will adopt the scene's sun rather than add a " +
                 "second one.")]
        public Light sun;

        [Tooltip("PLACEHOLDER. Sun intensity at noon.")]
        public float sunIntensity = 1.2f;

        [Tooltip("PLACEHOLDER. Sun colour across the day: left is dawn, middle is " +
                 "noon, right is dusk.")]
        public Gradient sunColour = new Gradient();

        [Tooltip("PLACEHOLDER. The moon lights the world after sunset.")]
        public bool moonEnabled = true;

        [Tooltip("PLACEHOLDER. Moon intensity at its height.")]
        public float moonIntensity = 0.08f;

        // =====================================================================
        //  AMBIENT AND SKY
        // =====================================================================

        [Header("Ambient and Sky  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Ambient light at noon.")]
        [Range(0f, 4f)]
        public float ambientIntensity = 1f;

        [Tooltip("PLACEHOLDER. Sky tint across the day.")]
        public Gradient skyColour = new Gradient();

        [Tooltip("PLACEHOLDER. Ground bounce colour across the day.")]
        public Gradient groundColour = new Gradient();

        [Tooltip("PLACEHOLDER. The skybox is re-rendered as the sun moves, rather " +
                 "than baked once. Off is cheaper and static.")]
        public bool dynamicSkybox = true;

        [Tooltip("PLACEHOLDER. Weather's cloud coverage dims the sun and lifts the " +
                 "ambient. Off keeps lighting independent of weather.")]
        public bool respondToWeather = true;

        // =====================================================================
        //  SHADOWS
        // =====================================================================

        [Header("Shadows  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Metres from the camera at which shadows stop. On a " +
                 "world this size this is the setting that decides the frame cost.")]
        public float shadowDistanceMetres = 250f;

        [Tooltip("PLACEHOLDER. Shadow cascade count. URP allows 1 to 4.")]
        [Range(1, 4)]
        public int shadowCascades = 4;

        [Tooltip("PLACEHOLDER. Shadow strength, 0 none to 1 black.")]
        [Range(0f, 1f)]
        public float shadowStrength = 0.8f;

        [Tooltip("PLACEHOLDER. Terrain cells cast shadows on each other. Off saves a " +
                 "pass over a large grid and flattens distant relief.")]
        public bool terrainCastsShadows = true;

        // =====================================================================
        //  ATMOSPHERE
        // =====================================================================

        [Header("Atmosphere  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Distant terrain fades toward the sky colour, which is " +
                 "what gives a large world its sense of scale.")]
        public bool aerialPerspective = true;

        [Tooltip("PLACEHOLDER. Metres at which aerial perspective is fully applied.")]
        public float aerialPerspectiveMetres = 4000f;

        [Tooltip("PLACEHOLDER. Strength of the light shafts around the sun.")]
        [Range(0f, 1f)]
        public float lightShaftStrength = 0.25f;

        [Tooltip("PLACEHOLDER. Exposure applied to the whole world, in stops.")]
        [Range(-4f, 4f)]
        public float exposureStops;

        /// <summary>The world this lighting belongs to, or null when loose in a scene.</summary>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        private void OnValidate()
        {
            timeOfDayHours = Mathf.Repeat(timeOfDayHours, 24f);
            dayLengthMinutes = Mathf.Max(0.1f, dayLengthMinutes);
            sunIntensity = Mathf.Max(0f, sunIntensity);
            moonIntensity = Mathf.Max(0f, moonIntensity);
            shadowDistanceMetres = Mathf.Max(0f, shadowDistanceMetres);
            aerialPerspectiveMetres = Mathf.Max(1f, aerialPerspectiveMetres);
        }
    }
}
