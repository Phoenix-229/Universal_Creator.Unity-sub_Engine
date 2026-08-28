// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    AtmosphereState.cs
//  Purpose: The single per-frame hand-off from the weather layer to the sky. It
//           carries only resolved values - the clouds, the air and the clock as
//           section 7 already decided them - and derives, in one place, the lighting
//           that reads from them: the weather-driven turbidity, the sun and moon
//           directions and colour, the two key illuminances and the display
//           exposure. It samples no pressure field, runs no condensation model and
//           decides no precipitation. If a value is missing it is added upstream in
//           the weather layer, never guessed here - a sky that guesses is a sky that
//           disagrees with the rain falling out of it.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// An immutable snapshot of everything the sky needs for one frame, built once by
    /// <see cref="From"/> from a <see cref="WeatherState"/> and a <see cref="ClockState"/>.
    /// Cloud cover, base, type, the air's stability and the surface reflectance come
    /// straight across as resolved values; turbidity, the body directions and colours,
    /// the key illuminances and the exposure are the lighting the sky owns, derived
    /// here so there is exactly one place they are decided each frame.
    /// </summary>
    public readonly struct AtmosphereState
    {
        // ---- Clouds, per layer (low / mid / high): resolved upstream ---------
        public readonly float cloudCoverLow;
        public readonly float cloudCoverMid;
        public readonly float cloudCoverHigh;

        /// <summary>
        /// The three layer covers stacked as independent screens, 1 - (1-low)(1-mid)(1-high):
        /// the single fraction the sun is occluded by. The per-layer covers stay separate above
        /// for the three-sheet render; this is the one number the light is dimmed by, so the sun
        /// and the drawn sheets cannot disagree.
        /// </summary>
        public readonly float cloudCoverCombined01;
        public readonly float cloudBaseLowM;
        public readonly float cloudBaseMidM;
        public readonly float cloudBaseHighM;
        public readonly float cloudThicknessLowM;
        public readonly float cloudThicknessMidM;
        public readonly float cloudThicknessHighM;
        public readonly CloudCover.CloudType cloudTypeLow;
        public readonly CloudCover.CloudType cloudTypeMid;
        public readonly CloudCover.CloudType cloudTypeHigh;

        /// <summary>
        /// The genus of each deck, decided per layer by the cloud-type table from the
        /// same stability, front and humidity the rest of the sky reads. This is the
        /// silhouette the three sheets draw - cirrus leading a warm front, cumulonimbus
        /// over unstable air, stratus on a calm humid morning - never an authored field.
        /// </summary>
        public readonly CloudGenus cloudGenusLow;
        public readonly CloudGenus cloudGenusMid;
        public readonly CloudGenus cloudGenusHigh;

        // ---- Air and surface: resolved upstream ------------------------------
        public readonly float stabilityLapseCPerKm;
        public readonly float turbidity;
        public readonly float precipitationMmPerHour;
        public readonly float snowLineM;

        /// <summary>
        /// Linear-RGB diffuse reflectance of the ground under the point. Not a weather
        /// value: it is the terrain and its snow cover, handed in so the sky's ambient
        /// fill can gain the light the ground bounces back - the indirect term that a
        /// snowfield brightens and a wet road darkens. Zero is inert.
        /// </summary>
        public readonly Color groundAlbedo;

        // ---- Sun and moon: the lighting derived here -------------------------
        public readonly Vector3 sunDir;
        public readonly Vector3 moonDir;
        public readonly Color sunColor;
        public readonly float sunIlluminanceLux;
        public readonly float moonIlluminanceLux;
        public readonly Color moonColor;
        public readonly float exposureEv;

        // One door in: the derivations run once, in the constructor, so no consumer
        // can rebuild a value a different way. Cloud layer base/thickness/type carry
        // what the weather resolves today; the per-parcel altitudes and the per-layer
        // type table are filled by their own steps and land through this same field.
        private AtmosphereState(in WeatherState w, in ClockState clock,
                                Color groundAlbedoIn, float turbidityBias)
        {
            cloudCoverLow = w.cloudCoverLow;
            cloudCoverMid = w.cloudCoverMid;
            cloudCoverHigh = w.cloudCoverHigh;
            cloudCoverCombined01 = CloudCover.CombinedCover01(
                w.cloudCoverLow, w.cloudCoverMid, w.cloudCoverHigh);
            cloudBaseLowM = w.cloudBaseM;      // the surface lifting condensation level
            cloudBaseMidM = w.cloudBaseMidM;   // the mid deck's own parcel condensation level
            cloudBaseHighM = w.cloudBaseHighM; // the high deck's own parcel condensation level
            cloudThicknessLowM = 0f;
            cloudThicknessMidM = 0f;
            cloudThicknessHighM = 0f;
            cloudTypeLow = w.cloudType;
            cloudTypeMid = w.cloudType;
            cloudTypeHigh = w.cloudType;

            // The genus of each deck from the same physics: the low deck reads stability
            // and the close front, the sheets above hang from the front's leading edge.
            // Orographic lift is terrain, not weather - false until the point-sampler feeds
            // it in, so a lenticular waits on that plumbing rather than being guessed here.
            cloudGenusLow = CloudTypeTable.Classify(
                CloudLayer.Low, w.cloudCoverLow, w.stabilityLapseCPerKm,
                w.frontKind, w.frontDistanceKm, w.relativeHumidity01, w.windSpeed, false);
            cloudGenusMid = CloudTypeTable.Classify(
                CloudLayer.Mid, w.cloudCoverMid, w.stabilityLapseCPerKm,
                w.frontKind, w.frontDistanceKm, w.relativeHumidity01, w.windSpeed, false);
            cloudGenusHigh = CloudTypeTable.Classify(
                CloudLayer.High, w.cloudCoverHigh, w.stabilityLapseCPerKm,
                w.frontKind, w.frontDistanceKm, w.relativeHumidity01, w.windSpeed, false);
            stabilityLapseCPerKm = w.stabilityLapseCPerKm;
            precipitationMmPerHour = w.precipitationMmPerHour;
            snowLineM = w.snowLineM;
            groundAlbedo = groundAlbedoIn;

            // Turbidity is the weather-driven scalar; the sky then reads it, so a front
            // that raises humidity or falls as rain moves the whole sky with no change here.
            turbidity = TurbidityModel.Resolve(w.humidity, w.wetness, turbidityBias);

            sunDir = SunMoonRig.DirectionToBody(clock.elevationDeg, clock.azimuthDeg);
            moonDir = SunMoonRig.DirectionToBody(clock.lunarElevationDeg, clock.lunarAzimuthDeg);

            sunColor = SkyModel.SunColorLinear(clock.elevationDeg, clock.airMass, turbidity);
            sunIlluminanceLux = SkyModel.SunIlluminanceLux(
                clock.elevationDeg, clock.airMass, turbidity, cloudCoverCombined01);
            moonIlluminanceLux = ExposureModel.MoonIlluminanceLux(
                clock.lunarElevationDeg, clock.lunarIlluminatedFraction);

            // Moonlight is reflected sunlight: neutral grey at source, never blue. The
            // perceptual blue of night is the eye (S-45), applied to the image downstream.
            moonColor = MoonLight.ColorLinear();

            float keyLux = ExposureModel.SceneKeyIlluminanceLux(
                clock.elevationDeg, clock.airMass, turbidity, cloudCoverCombined01,
                clock.lunarElevationDeg, clock.lunarIlluminatedFraction);
            exposureEv = ExposureModel.Ev100FromLux(keyLux);
        }

        /// <summary>
        /// Gather one frame's sky inputs. <paramref name="groundAlbedo"/> is the linear
        /// reflectance of the ground under the point (terrain plus snow), and
        /// <paramref name="turbidityBias"/> the authored per-place haze offset; every
        /// other value is read from the two resolved snapshots.
        /// </summary>
        public static AtmosphereState From(in WeatherState weather, in ClockState clock,
                                           Color groundAlbedo, float turbidityBias)
        {
            return new AtmosphereState(in weather, in clock, groundAlbedo, turbidityBias);
        }
    }
}
