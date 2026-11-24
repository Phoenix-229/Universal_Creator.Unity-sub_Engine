// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    CloudRenderBinding.cs
//  Purpose: Turn one deck's resolved genus, coverage and wind into the handful of
//           scalars the sheet shader draws it with. The genus decides the silhouette
//           - a cumulus gets a hard, high-contrast edge, a stratus flattens toward an
//           even deck, a cirrus stretches along the wind into streaks - and the wind
//           and elapsed time decide how far the field has drifted. Everything the
//           shader needs is derived here, in one place, from values section 7 and the
//           cloud-type table already produced, so nothing about the drawn sheet is
//           authored on top of the weather.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Weather;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The per-sheet draw parameters for one cloud deck. Plain data: a driver copies
    /// these into the shader's per-layer globals. Coverage drives the density
    /// threshold; the three shaping terms bend the one baked field into this genus's
    /// silhouette; the scroll is the field's advected offset for this frame.
    /// </summary>
    public readonly struct CloudSheetParams
    {
        public readonly float coverage01;
        public readonly float edgeSharpness;   // > 1 hardens the edge into cauliflower
        public readonly float flatten01;        // toward a uniform, structureless deck
        public readonly float stretch;          // along-wind streak ratio, 1 = round
        public readonly float selfShadowDepth;  // lit-to-shaded spread across a heap
        public readonly float baseAltitudeM;
        public readonly Vector2 scrollUv;       // advected field offset this frame

        public CloudSheetParams(float coverage01, float edgeSharpness, float flatten01,
            float stretch, float selfShadowDepth, float baseAltitudeM, Vector2 scrollUv)
        {
            this.coverage01 = coverage01;
            this.edgeSharpness = edgeSharpness;
            this.flatten01 = flatten01;
            this.stretch = stretch;
            this.selfShadowDepth = selfShadowDepth;
            this.baseAltitudeM = baseAltitudeM;
            this.scrollUv = scrollUv;
        }
    }

    /// <summary>
    /// Resolves a deck's <see cref="CloudSheetParams"/> from its genus, coverage,
    /// altitude and the wind at that altitude. Pure - the same inputs give the same
    /// sheet on every machine.
    /// </summary>
    public static class CloudRenderBinding
    {
        /// <summary>
        /// The silhouette family a genus draws with, before coverage and wind are
        /// applied: heaps are hard-edged and self-shadow deeply, sheets flatten, and
        /// the high ice clouds stretch into wind-drawn streaks.
        /// </summary>
        public static CloudSheetParams Resolve(CloudGenus genus, float coverage01,
            float baseAltitudeM, Vector2 windMPerS, float elapsedSeconds)
        {
            float edge = 1f, flatten = 0f, stretch = 1f, shadow = 0f;

            switch (genus)
            {
                // Convective heaps: hard cauliflower edges and a deep sun-to-base shade.
                case CloudGenus.CumulusHumilis:
                case CloudGenus.CumulusCongestus:
                case CloudGenus.Cumulonimbus:
                case CloudGenus.Stratocumulus:
                    edge = LightingConstants.CloudCumulusEdgeSharpness;
                    shadow = LightingConstants.CloudSelfShadowDepth;
                    break;

                // Flat layered sheets: little internal structure, little self-shadow.
                case CloudGenus.Stratus:
                case CloudGenus.Nimbostratus:
                case CloudGenus.Altostratus:
                case CloudGenus.Cirrostratus:
                    flatten = 1f - LightingConstants.CloudStratusFlatten;
                    break;

                // High ice and lens shapes: drawn out along the wind into streaks.
                case CloudGenus.Cirrus:
                case CloudGenus.Altocumulus:
                case CloudGenus.Lenticular:
                    stretch = LightingConstants.CloudCirrusStretch;
                    break;
            }

            return new CloudSheetParams(
                Mathf.Clamp01(coverage01), edge, flatten, stretch, shadow,
                baseAltitudeM, AdvectionUv(windMPerS, elapsedSeconds));
        }

        /// <summary>
        /// The field offset a deck's wind has carried it this frame: metres of drift
        /// (wind times elapsed time) expressed in the sheet's UV, so a faster wind
        /// aloft visibly outruns a slow surface breeze on the same clock.
        /// </summary>
        public static Vector2 AdvectionUv(Vector2 windMPerS, float elapsedSeconds)
        {
            return windMPerS * elapsedSeconds / LightingConstants.CloudAdvectionMetresPerUv;
        }

        /// <summary>UC-T08-08: worker-safe repeating field phase from the shared double-precision world instant, without a float timer.</summary>
        /// <param name="windMPerS">Finite XZ wind in metres/second.</param>
        /// <param name="elapsedSeconds">Finite absolute simulated seconds since model year 1; nonnegative.</param>
        /// <returns>UV phase in [0,1) per axis; whole tile translations are equivalent in the repeat-wrapped noise.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">Time or wind is nonfinite, or time is negative.</exception>
        public static Vector2 AdvectionUv(Vector2 windMPerS, double elapsedSeconds)
        {
            if (!ClimateBaselineProduct.Finite(windMPerS.x) || !ClimateBaselineProduct.Finite(windMPerS.y) ||
                double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
                throw new System.ArgumentOutOfRangeException(nameof(elapsedSeconds));
            return new Vector2(RepeatPhase(windMPerS.x, elapsedSeconds), RepeatPhase(windMPerS.y, elapsedSeconds));
        }

        private static float RepeatPhase(float speed, double seconds)
        {
            if (speed == 0) return 0;
            double period = LightingConstants.CloudAdvectionMetresPerUv / System.Math.Abs((double)speed);
            double phase = (seconds % period) / period;
            if (speed < 0) phase = -phase;
            // A double just below one can round to float one; preserve the repeat interval at large instants.
            return Mathf.Min((float)(phase - System.Math.Floor(phase)), 0.99999994f);
        }

        /// <summary>
        /// The field UV a world point samples at a given moment: the spatial UV plus the
        /// drift the wind has carried by that moment. The moment is passed as the absolute
        /// simulated clock time - never <c>Time.time</c> and never an accumulating offset -
        /// so the field is a pure function of (world position, simulated time, seed via the
        /// baked texture): the same world, day and hour reproduces the identical sky after a
        /// reload, and a clock jumped forward and back lands on exactly the value it left.
        /// </summary>
        public static Vector2 SampleUv(Vector2 worldXZ, Vector2 windMPerS,
                                       float simulatedTimeSeconds, float metresPerUv)
        {
            float scale = metresPerUv > 0f ? metresPerUv : LightingConstants.CloudAdvectionMetresPerUv;
            return worldXZ / scale + AdvectionUv(windMPerS, simulatedTimeSeconds);
        }
    }
}
