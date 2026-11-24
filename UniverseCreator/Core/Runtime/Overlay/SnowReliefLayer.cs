// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SnowReliefLayer.cs
//  Purpose: Snow relief as a TRANSIENT LAYER laid OVER the rock surface, never mixed
//           into the process sum. Snow is not a weathering product of the ground - it is
//           weather sitting on top of it - so it has its own depth (supplied by the
//           section 7 snow model), its own surface (a smooth mantle in calm air; sastrugi
//           ridges elongated along the wind once the wind can move snow; drifts piled in
//           the lee of obstacles), and its own coming and going (a cross-fade driven by
//           depth, melting off sun-facing steep ground first). The overlay cross-fades the
//           rock micro-relief and this layer by CoverageFraction, so the same slope reads
//           as rock in summer and drifted snow in winter with no rebuild.
//
//  WHY A LAYER, NOT A PROCESS (L-47)
//  ---------------------------------
//  A process in MicroReliefField competes to CLAIM the rock surface and persists in it.
//  Snow does neither: it hides the rock while present and reveals it unchanged when gone.
//  Adding it to the sum would carve a snowdrift permanently into the bedrock, which is
//  exactly the mistake this separation prevents.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// What the snow layer needs beyond the rock context: the section 7 snow depth here, the wind that
    /// shapes it, and the nearest upwind obstacle that casts a lee drift. Pure input - no snow is authored.
    /// </summary>
    public struct SnowReliefContext
    {
        /// <summary>The point's environment (slope, aspect, latitude drive melt-out; worldPos drives the pattern).</summary>
        public EnvironmentSample env;
        /// <summary>Snow depth in metres from the section 7 snow model. Zero in summer, deep in winter.</summary>
        public float snowDepthM;
        /// <summary>Wind speed in m/s. Below the transport threshold the surface is a smooth mantle.</summary>
        public float windSpeedMps;
        /// <summary>Compass bearing the wind blows TOWARD (0 = +Z/N, 90 = +X/E). Drifts pile downwind of it.</summary>
        public float windBearingDeg;
        /// <summary>Height in metres of the nearest upwind obstacle (rock, tree, wall) that shelters a drift.</summary>
        public float obstacleHeightM;
        /// <summary>Distance in metres from that obstacle to this point.</summary>
        public float distanceToObstacleM;
        /// <summary>Compass bearing FROM the obstacle TO this point - a drift forms only where this points downwind.</summary>
        public float obstacleBearingDeg;
        /// <summary>World seed, so the mantle and sastrugi are deterministic and tile across patch edges.</summary>
        public int seed;
    }

    /// <summary>
    /// Evaluates the transient snow surface at a point: how completely snow covers the ground here
    /// (<see cref="CoverageFraction"/>, the cross-fade weight) and the snow surface micro-relief in
    /// millimetres above the smooth mantle (<see cref="SurfaceReliefMm"/>). Pure and allocation-free.
    /// </summary>
    public sealed class SnowReliefLayer
    {
        /// <summary>Snow starts drifting and eroding into sastrugi above roughly this wind speed (m/s).</summary>
        public const float TransportThresholdMps = 6f;

        /// <summary>A lee drift reaches about ten obstacle heights downwind before it tapers out.</summary>
        private const float LeeLengthFactor = 10f;

        /// <summary>Depth over which snow fades from bare ground (0) to full cover (1).</summary>
        private const float FadeDepthM = 0.15f;

        /// <summary>How much sun-facing steep ground shortens the effective depth, so it melts out first (m).</summary>
        private const float MeltBiasM = 0.2f;

        /// <summary>Sastrugi ridge spacing across the wind (m).</summary>
        private const float SastrugiWavelengthM = 0.6f;

        /// <summary>
        /// Cross-fade weight in [0,1]: 0 = bare rock shows, 1 = snow fully hides it. Driven by depth, but
        /// insolation shortens the effective depth on sun-facing steep ground so that melts out first.
        /// </summary>
        public float CoverageFraction(in SnowReliefContext c)
        {
            float effectiveDepth = c.snowDepthM - MeltBiasM * InsolationFactor(c.env);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(effectiveDepth / FadeDepthM));
        }

        /// <summary>
        /// Snow surface micro-relief in millimetres above the base mantle: a lee drift piled downwind of an
        /// obstacle, plus sastrugi ridges once the wind can move snow. Calm air leaves a near-smooth mantle.
        /// </summary>
        public float SurfaceReliefMm(in SnowReliefContext c)
        {
            if (c.snowDepthM <= 0f) return 0f; // no snow, no snow surface

            float mm = MantleMm(c);
            if (c.windSpeedMps >= TransportThresholdMps)
            {
                mm += DriftMm(c);
                mm += SastrugiMm(c);
            }
            return mm;
        }

        /// <summary>How strongly the sun bears on this facet: equator-facing and steep melts fastest. [0,1].</summary>
        private static float InsolationFactor(in EnvironmentSample env)
        {
            // Equator-facing aspect is 180 deg (S) in the northern hemisphere, 0 deg (N) in the southern.
            float equatorAspect = env.latitudeDeg >= 0f ? 180f : 0f;
            float align = 0.5f * (1f + Mathf.Cos((env.aspectDeg - equatorAspect) * Mathf.Deg2Rad));
            float steep = Mathf.Sin(Mathf.Clamp(env.slopeDeg, 0f, 90f) * Mathf.Deg2Rad);
            return Mathf.Clamp01(align * steep);
        }

        /// <summary>A gentle wind-independent undulation of the settled mantle - a few millimetres at most.</summary>
        private static float MantleMm(in SnowReliefContext c)
        {
            float n = MicroReliefProcess.ValueNoise(c.env.worldPos.x, c.env.worldPos.z, 2f, c.seed, 4701);
            return 5f * n;
        }

        /// <summary>
        /// Lee drift: snow piles DOWNWIND of an obstacle. It exists only where the obstacle-to-point bearing
        /// points downwind (so the drift sits behind the obstacle, not around it) and within ten heights.
        /// </summary>
        private static float DriftMm(in SnowReliefContext c)
        {
            if (c.obstacleHeightM <= 0f) return 0f;

            // Downwind alignment: 1 directly downwind, 0 crosswind, negative upwind.
            float align = Mathf.Cos((c.obstacleBearingDeg - c.windBearingDeg) * Mathf.Deg2Rad);
            if (align <= 0.3f) return 0f; // outside the lee cone - no drift here

            float leeLenM = LeeLengthFactor * c.obstacleHeightM;
            float u = c.distanceToObstacleM / leeLenM;
            if (u < 0f || u > 1f) return 0f; // beyond the drift's reach

            float profile = (1f - u) * (1f - u);              // deepest just behind the obstacle, tapering out
            float driftM = c.obstacleHeightM * 0.5f * profile * align;
            return driftM * 1000f;
        }

        /// <summary>
        /// Sastrugi: wind-carved ridges ELONGATED ALONG the wind, so relief varies across the wind. Their
        /// amplitude grows with wind above the threshold but can never exceed the snow that is there to carve.
        /// </summary>
        private static float SastrugiMm(in SnowReliefContext c)
        {
            float availableMm = c.snowDepthM * 1000f * 0.5f;
            float amp = Mathf.Min(Mathf.Clamp((c.windSpeedMps - TransportThresholdMps) * 20f, 0f, 120f), availableMm);
            if (amp <= 0f) return 0f;

            float across = ReliefMath.AcrossSlope(c.env.worldPos.x, c.env.worldPos.z, c.windBearingDeg);
            float along = ReliefMath.AlongSlope(c.env.worldPos.x, c.env.worldPos.z, c.windBearingDeg);
            float ridge = Mathf.Sin(across / SastrugiWavelengthM * (2f * Mathf.PI));
            float jitter = 0.5f + 0.5f * MicroReliefProcess.ValueNoise(across, along * 0.2f, SastrugiWavelengthM, c.seed, 4702);
            return amp * ridge * jitter;
        }
    }
}

