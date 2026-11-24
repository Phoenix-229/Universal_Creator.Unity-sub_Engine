// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    SunMoonRig.cs
//  Purpose: Turn the clock's sun and moon angles into directional-light rotations
//           and the direction vectors the sky and fog read. The astronomy itself
//           is computed upstream and only consumed here.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Orients the world's sun and moon directional lights from a ClockState. It
    /// holds the two lights the manager owns and points them; the elevation and
    /// azimuth it consumes were solved in the weather layer, so a saved world
    /// reopened at the same time of day lights up the same way.
    /// </summary>
    public sealed class SunMoonRig
    {
        // A Unity directional light sends its rays along +forward, so the
        // direction TO the light is -forward. A body's azimuth is the compass
        // bearing it stands at; the beam leaves it travelling the opposite way
        // round the compass, so the light's yaw is that bearing plus half a turn.
        // Pitching down by the elevation then drops the beam below the horizon by
        // the same angle the body rides above it.
        private const float BeamYawOffsetDeg = 180f;

        private readonly Light _sun;
        private readonly Light _moon;

        public SunMoonRig(Light sun, Light moon)
        {
            _sun = sun;
            _moon = moon;
        }

        public Light Sun { get { return _sun; } }
        public Light Moon { get { return _moon; } }

        /// <summary>Aim both lights along the clock's current sun and moon angles.</summary>
        public void Orient(ClockState clock)
        {
            if (_sun != null)
                _sun.transform.rotation = BodyRotation(clock.elevationDeg, clock.azimuthDeg);
            if (_moon != null)
                _moon.transform.rotation = BodyRotation(clock.lunarElevationDeg, clock.lunarAzimuthDeg);
        }

        /// <summary>
        /// Unit vector from the ground toward a body at this altitude and compass
        /// bearing: x east, y up, z north. This is where the disc is drawn and the
        /// direction shading treats as "toward the light".
        /// </summary>
        public static Vector3 DirectionToBody(float elevationDeg, float azimuthDeg)
        {
            float e = elevationDeg * Mathf.Deg2Rad;
            float a = azimuthDeg * Mathf.Deg2Rad;
            float cosE = Mathf.Cos(e);
            return new Vector3(Mathf.Sin(a) * cosE, Mathf.Sin(e), Mathf.Cos(a) * cosE);
        }

        /// <summary>
        /// Unit vector the rays travel along - the exact opposite of the direction
        /// to the body, which is what a directional light uses as its forward.
        /// </summary>
        public static Vector3 BeamDirection(float elevationDeg, float azimuthDeg)
        {
            return -DirectionToBody(elevationDeg, azimuthDeg);
        }

        /// <summary>Rotation that aims a directional light's beam for a body at these angles.</summary>
        public static Quaternion BodyRotation(float elevationDeg, float azimuthDeg)
        {
            return Quaternion.Euler(elevationDeg, azimuthDeg + BeamYawOffsetDeg, 0f);
        }

        /// <summary>
        /// Metres of penumbra a light of this angular diameter casts at a given
        /// distance from the occluder: 2 * distance * tan(halfAngle). Zero at the
        /// contact point and widening with distance, which is the soft-shadow
        /// behaviour a real source has and a hard shadowmap does not.
        /// </summary>
        public static float PenumbraWidthMeters(float discDiameterDeg, float distanceMeters)
        {
            float halfAngle = 0.5f * Mathf.Max(0f, discDiameterDeg) * Mathf.Deg2Rad;
            return 2f * Mathf.Max(0f, distanceMeters) * Mathf.Tan(halfAngle);
        }

        /// <summary>Converts a uniform celestial disc's illuminance to its mean radiance using its angular solid angle.</summary>
        /// <param name="illuminanceLux">Nonnegative finite illuminance in lux before exposure.</param>
        /// <param name="diameterDeg">Angular diameter in degrees, 0..180; zero disables the disc.</param>
        /// <returns>Radiance in cd/m²; zero for a disabled disc. Uses a stable sine formula for sub-degree diameters.</returns>
        internal static float DiscRadiance(float illuminanceLux, float diameterDeg)
        {
            if (illuminanceLux <= 0 || diameterDeg <= 0) return 0;
            double sine = System.Math.Sin(Mathf.Min(diameterDeg, 180) * System.Math.PI / 720);
            return (float)(illuminanceLux / (4 * System.Math.PI * sine * sine));
        }

        /// <summary>
        /// The sun's share of the lighting across the civil-twilight band: 0 at and
        /// below the sun 6 deg under the horizon, rising smoothly to 1 as it reaches
        /// the horizon. The moon carries the complement (1 - this), so the two never
        /// both sit at full and neither pops on. Below the band the sun light is off.
        /// </summary>
        public static float SunTwilightWeight01(float sunElevationDeg)
        {
            float band = -LightingConstants.CivilTwilightDeg;
            return Mathf.Clamp01((sunElevationDeg - LightingConstants.CivilTwilightDeg) / band);
        }
    }
}
