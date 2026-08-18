// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWeatherSystemManager.cs
//  Purpose: The WeatherSystem child's control surface. Wind is real and shipping;
//           everything else here is a declared placeholder for a later module.
//
//  WIND IS THE PART THAT WORKS
//  ---------------------------
//  UCWindManager already drives grass, shrubs, trees, water and the sway maps from
//  one source. This component owns it: it makes sure exactly one exists, keeps it
//  on this object, and is where the wind settings are edited from.
//
//  Keeping it on this object is not cosmetic. A WindZone blows along its own
//  forward axis, so UCWindManager rotates the transform it sits on. On the world
//  root that would rotate the root - and every terrain cell parented under it -
//  every time the wind direction changed. On the WeatherSystem child it rotates a
//  child that owns nothing but weather.
//
//  EVERYTHING ELSE IS A PLACEHOLDER, AND SAYS SO
//  ---------------------------------------------
//  Precipitation, cloud, fog, storms and the seasonal cycle are named here with
//  the ranges and units they will use, and nothing reads them yet. They exist so
//  the module that implements them has a shape to fill rather than a blank file to
//  invent from - which is how a later pass ends up with its own parallel weather
//  system that does not agree with this one about which way the wind is blowing.
//
//  Nothing in this file acts on the placeholder fields. A stub that half-worked
//  would be worse than one that does nothing: it would look implemented.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// World-level weather control. Wind is live; the remaining sections are
    /// declared for a later module and are not read by anything yet.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Weather System Manager")]
    [DisallowMultipleComponent]
    public class UCWeatherSystemManager : MonoBehaviour
    {
        // =====================================================================
        //  WIND  -  IMPLEMENTED
        // =====================================================================

        [Header("Wind")]

        [Tooltip("Wind is on. Off removes the wind manager's effect: grass, shrubs, " +
                 "trees and water all go still, and the sway maps stop being uploaded.")]
        public bool windEnabled = true;

        // Serialized so the link survives a domain reload. Found or created by
        // EnsureWind rather than assigned by hand.
        [SerializeField, HideInInspector] private UCWindManager wind;

        /// <summary>
        /// The wind manager for this world, creating it on this object if there is
        /// none. Never returns null while this component is alive.
        /// </summary>
        public UCWindManager Wind
        {
            get { return EnsureWind(); }
        }

        /// <summary>The wind manager if one already exists, without creating it.</summary>
        public UCWindManager WindOrNull
        {
            get
            {
                if (wind != null) return wind;

                // A world built before this component existed has its wind manager
                // somewhere else in the hierarchy. Adopt it rather than adding a
                // second one that would fight it for the same shader globals.
                UniverseCreatorObject world = World;
                Transform search = world != null ? world.transform : transform;
                wind = search.GetComponentInChildren<UCWindManager>(true);
                return wind;
            }
        }

        /// <summary>The world this weather belongs to, or null when loose in a scene.</summary>
        public UniverseCreatorObject World
        {
            get { return GetComponentInParent<UniverseCreatorObject>(); }
        }

        /// <summary>
        /// Guarantees one wind manager, on this object.
        ///
        /// Moving an adopted manager is not attempted: a component cannot change
        /// GameObject, and destroying and recreating it would drop the composited
        /// sway map the graph has already baked into it. So an older world keeps
        /// its manager where it is and this component drives it from here.
        /// </summary>
        public UCWindManager EnsureWind()
        {
            UCWindManager existing = WindOrNull;
            if (existing != null) return existing;

            wind = gameObject.AddComponent<UCWindManager>();
            return wind;
        }

        // =====================================================================
        //  PRECIPITATION  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Precipitation  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Rain intensity, 0 dry to 1 downpour.")]
        [Range(0f, 1f)]
        public float rainIntensity;

        [Tooltip("PLACEHOLDER. Snow intensity, 0 clear to 1 blizzard.")]
        [Range(0f, 1f)]
        public float snowIntensity;

        [Tooltip("PLACEHOLDER. Metres above which precipitation falls as snow rather " +
                 "than rain.")]
        public float snowLineMetres = 1200f;

        [Tooltip("PLACEHOLDER. How wet surfaces read, 0 dry to 1 streaming.")]
        [Range(0f, 1f)]
        public float surfaceWetness;

        [Tooltip("PLACEHOLDER. Seconds a surface takes to dry once precipitation stops.")]
        public float dryingSeconds = 90f;

        // =====================================================================
        //  CLOUD AND SKY  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Cloud and Sky  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Sky coverage, 0 clear to 1 overcast.")]
        [Range(0f, 1f)]
        public float cloudCoverage = 0.35f;

        [Tooltip("PLACEHOLDER. Metres above sea level the cloud base sits at.")]
        public float cloudBaseMetres = 900f;

        [Tooltip("PLACEHOLDER. Cloud thickness in metres. Drives how dark an " +
                 "overcast sky reads.")]
        public float cloudThicknessMetres = 400f;

        [Tooltip("PLACEHOLDER. Clouds drift with the wind rather than on their own " +
                 "vector, so a storm front and the grass agree on the direction.")]
        public bool cloudsFollowWind = true;

        // =====================================================================
        //  FOG AND VISIBILITY  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Fog and Visibility  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Fog density, 0 clear to 1 whiteout.")]
        [Range(0f, 1f)]
        public float fogDensity;

        [Tooltip("PLACEHOLDER. Metres at which visibility falls to nothing.")]
        public float visibilityMetres = 3000f;

        [Tooltip("PLACEHOLDER. Fog pools in low ground rather than filling the world " +
                 "uniformly - the difference between valley mist and a grey filter.")]
        public bool groundFog = true;

        [Tooltip("PLACEHOLDER. Metres above the terrain that ground fog reaches.")]
        public float groundFogHeightMetres = 30f;

        // =====================================================================
        //  STORMS  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Storms  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Storms can build and pass on their own.")]
        public bool stormsEnabled;

        [Tooltip("PLACEHOLDER. Average minutes between storms.")]
        public float stormIntervalMinutes = 45f;

        [Tooltip("PLACEHOLDER. Minutes one storm takes to build, hold and clear.")]
        public float stormDurationMinutes = 8f;

        [Tooltip("PLACEHOLDER. Wind multiplier at the height of a storm.")]
        [Range(1f, 5f)]
        public float stormWindMultiplier = 2.5f;

        [Tooltip("PLACEHOLDER. Lightning strikes per minute at the height of a storm.")]
        public float lightningPerMinute = 4f;

        // =====================================================================
        //  CLIMATE AND SEASON  -  NOT IMPLEMENTED
        // =====================================================================

        [Header("Climate and Season  (declared, not implemented)")]

        [Tooltip("PLACEHOLDER. Air temperature at sea level, degrees Celsius.")]
        public float temperatureCelsius = 15f;

        [Tooltip("PLACEHOLDER. Degrees Celsius lost per kilometre of altitude. 6.5 " +
                 "is the standard atmospheric lapse rate.")]
        public float lapseRatePerKm = 6.5f;

        [Tooltip("PLACEHOLDER. Relative humidity, 0 arid to 1 saturated.")]
        [Range(0f, 1f)]
        public float humidity = 0.5f;

        [Tooltip("PLACEHOLDER. Position in the year, 0 to 1. 0 is midwinter.")]
        [Range(0f, 1f)]
        public float seasonPhase;

        [Tooltip("PLACEHOLDER. The season advances by itself.")]
        public bool advanceSeason;

        [Tooltip("PLACEHOLDER. Real minutes one full year takes.")]
        public float yearLengthMinutes = 120f;

        // =====================================================================
        //  LIFECYCLE
        // =====================================================================

        private void OnEnable()
        {
            ApplyWindEnabled();
        }

        private void OnValidate()
        {
            snowLineMetres = Mathf.Max(0f, snowLineMetres);
            dryingSeconds = Mathf.Max(0f, dryingSeconds);
            cloudBaseMetres = Mathf.Max(0f, cloudBaseMetres);
            cloudThicknessMetres = Mathf.Max(0f, cloudThicknessMetres);
            visibilityMetres = Mathf.Max(1f, visibilityMetres);
            groundFogHeightMetres = Mathf.Max(0f, groundFogHeightMetres);
            stormIntervalMinutes = Mathf.Max(0.1f, stormIntervalMinutes);
            stormDurationMinutes = Mathf.Max(0.1f, stormDurationMinutes);
            lightningPerMinute = Mathf.Max(0f, lightningPerMinute);
            yearLengthMinutes = Mathf.Max(0.1f, yearLengthMinutes);

            if (isActiveAndEnabled) ApplyWindEnabled();
        }

        /// <summary>
        /// Matches the wind manager's enabled state to the toggle above. Disabling
        /// the component rather than destroying it keeps its settings and its baked
        /// sway map, so turning wind back on restores the world it had.
        /// </summary>
        public void ApplyWindEnabled()
        {
            UCWindManager w = windEnabled ? EnsureWind() : WindOrNull;
            if (w == null) return;
            if (w.enabled != windEnabled) w.enabled = windEnabled;
        }
    }
}
