// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    TurbidityModel.cs
//  Purpose: The one place a turbidity value is decided. Turbidity is not authored
//           as its own curve - it begins at a clean-air default and the weather
//           moves it: humidity swells the aerosols and lifts it, falling rain
//           washes them out and drops it. Every sky reads the single number this
//           produces, so the air, the rain and the haze are one physics, not three.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Resolves the atmospheric turbidity the sky lights by: one scalar in the real
    /// optical span (clear mountain air to heavy haze), driven by weather rather than
    /// painted. Humidity raises it, rain scrubs it down, and an authored bias nudges
    /// it per place - the only authored input, wired through the weather output's
    /// turbidity-bias port. Pure functions, so the value is identical on every machine.
    /// </summary>
    public static class TurbidityModel
    {
        /// <summary>
        /// Physical turbidity from the clean-air default, moved by weather and an
        /// optional authored bias. Humidity (0..1) adds haze, wetness (0..1) washes
        /// it out, biasAdd shifts it per location; the result is clamped to the real
        /// optical span so no input can drive the air clearer than a mountain or
        /// hazier than a dust storm.
        /// </summary>
        public static float Resolve(float baseTurbidity, float humidity01, float wetness01, float biasAdd)
        {
            float h = Mathf.Clamp01(humidity01);
            float w = Mathf.Clamp01(wetness01);
            float t = baseTurbidity
                    + LightingConstants.TurbidityHumidityGain * h
                    - LightingConstants.TurbidityRainScrub * w
                    + biasAdd;
            return Mathf.Clamp(t, LightingConstants.TurbidityClearMountain,
                                  LightingConstants.TurbidityHeavyHaze);
        }

        /// <summary>
        /// The same resolve from the module's clean-air default, for the common case
        /// where no per-world base has been authored - a lightly-hazed lowland day
        /// that humidity and rain then move.
        /// </summary>
        public static float Resolve(float humidity01, float wetness01, float biasAdd)
        {
            return Resolve(LightingConstants.TurbidityDefault, humidity01, wetness01, biasAdd);
        }

        /// <summary>
        /// A 0..1 authoring dial mapped onto the optical span, so a preset that carries
        /// its air as a normalised number lands on a real turbidity. Zero is mountain-
        /// clear, one is heavy haze; it is a base for <see cref="Resolve"/>, not a
        /// second place turbidity is decided.
        /// </summary>
        public static float FromNormalized01(float turbidity01)
        {
            return Mathf.Lerp(LightingConstants.TurbidityClearMountain,
                              LightingConstants.TurbidityHeavyHaze,
                              Mathf.Clamp01(turbidity01));
        }

        /// <summary>The inverse of <see cref="FromNormalized01"/>: a physical turbidity back to its 0..1 place on the span.</summary>
        public static float ToNormalized01(float turbidity)
        {
            float span = LightingConstants.TurbidityHeavyHaze - LightingConstants.TurbidityClearMountain;
            return Mathf.Clamp01((turbidity - LightingConstants.TurbidityClearMountain) / Mathf.Max(span, 1e-4f));
        }
    }
}
