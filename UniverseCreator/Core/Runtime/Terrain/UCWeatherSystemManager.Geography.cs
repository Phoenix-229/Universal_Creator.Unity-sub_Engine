// UC-T08-10: one absolute instant, explicit finite planar geography, civil labels independent of astronomy.
using System;
using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWeatherSystemManager
    {
        /// <summary>Reference meridian of the retained clock epoch in east-positive degrees, -180..180; captured once from old assets' standardMeridianDeg.</summary>
        [Range(-180, 180), Tooltip("Clock epoch reference, degrees east. Changing this deliberately reinterprets the instant; civil timezone display uses Standard Meridian instead. Old assets retain their previous reference on first load.")]
        public float clockReferenceMeridianDeg;
        [SerializeField, HideInInspector] private bool clockReferenceCaptured;
        /// <summary>East-west longitude span across this finite world's accepted planar width, 0..360 degrees; zero retains a single site sky.</summary>
        [Range(0, 360), Tooltip("Authored longitude span over the finite world's X extent. X is east and Z north, matching ecology's latitude span. No spherical geometry, planetary rebase or Earth-scale accuracy is implied.")]
        public float longitudeSpanDeg;

        private void CaptureClockReference()
        {
            if (clockReferenceCaptured) return;
            if (!ClimateBaselineProduct.Finite(standardMeridianDeg)) throw new ArgumentOutOfRangeException(nameof(standardMeridianDeg));
            clockReferenceMeridianDeg = Mathf.Clamp(standardMeridianDeg, -180, 180); clockReferenceCaptured = true;
        }

        /// <summary>Decimal civil display hour, 0..24, from the shared instant and saved display meridian; changing it does not alter solar geometry.</summary>
        public float CivilDisplayHour
        {
            get {
                CaptureClockReference();
                if (!ClimateBaselineProduct.Finite(standardMeridianDeg)) throw new ArgumentOutOfRangeException(nameof(standardMeridianDeg));
                return Mathf.Repeat(LocalHour + (standardMeridianDeg - clockReferenceMeridianDeg) / 15, 24);
            }
        }

        /// <summary>Samples existing ClockMath at this world's local planar geography, retaining the same absolute calendar/instant for every position.</summary>
        /// <param name="positionMetres">Finite Unity world position in metres; camera Y never changes solar location.</param>
        /// <returns>Local solar/lunar directions and latitude-dependent season. Zero spans or no bound world use the authored site.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Position, authored geography or world extents are nonfinite/invalid.</exception>
        /// <remarks>One per-camera sky with X-east/Z-north linear geography; positions clamp to finite bounds. This is not a spherical planet or timezone-border simulation.</remarks>
        public ClockState ResolveClockAt(Vector3 positionMetres)
        {
            CaptureClockReference();
            if (!ClimateBaselineProduct.Finite(positionMetres.x) || !ClimateBaselineProduct.Finite(positionMetres.y) || !ClimateBaselineProduct.Finite(positionMetres.z) ||
                !ClimateBaselineProduct.Finite(clockReferenceMeridianDeg) || !ClimateBaselineProduct.Finite(latitudeDeg) || !ClimateBaselineProduct.Finite(longitudeDeg) ||
                !ClimateBaselineProduct.Finite(latitudeSpanDeg) || !ClimateBaselineProduct.Finite(longitudeSpanDeg)) throw new ArgumentOutOfRangeException(nameof(positionMetres));
            float latitude = latitudeDeg, longitude = longitudeDeg;
            var world = World;
            if (world != null && (latitudeSpanDeg > 0 || longitudeSpanDeg > 0))
            {
                Vector3 size = world.GetWorldSize(), centre = world.transform.position + new Vector3(world.worldOriginOffset.x, 0, world.worldOriginOffset.y);
                if (!ClimateBaselineProduct.Finite(size.x) || !ClimateBaselineProduct.Finite(size.z) || size.x <= 0 || size.z <= 0)
                    throw new ArgumentOutOfRangeException(nameof(positionMetres), "Finite-world geography requires positive finite extents.");
                latitude = ClockMath.LatitudeAt(latitudeDeg, latitudeSpanDeg, Mathf.Clamp01(0.5f + (positionMetres.z - centre.z) / size.z));
                longitude = Mathf.Repeat(longitudeDeg + (Mathf.Clamp01(0.5f + (positionMetres.x - centre.x) / size.x) - 0.5f) * longitudeSpanDeg + 180, 360) - 180;
            }
            return ResolveClockAtGeography(latitude, longitude);
        }
    }
}
