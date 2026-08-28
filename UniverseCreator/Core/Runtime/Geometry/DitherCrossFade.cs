// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    DitherCrossFade.cs
//  Purpose: The screen-space dither that hides a LOD->LOD or LOD->impostor switch
//           WITHOUT alpha blending. Across a transition band the two meshes both
//           render, each clipping its fragments against a per-fragment blue-noise
//           threshold: the incoming mesh keeps the fragments whose threshold is below
//           the fade fraction t, the outgoing mesh keeps the rest. The two keep-sets
//           are exact complements, so at every t the silhouette is covered once - no
//           hole, no double-draw - and both meshes stay in the OPAQUE queue, so there
//           is no sort, no blend and no order-dependent artefact; the ordinary depth
//           test composites them.
//
//  WHY BLUE NOISE, AND WHY A FRACTIONAL BAND
//  -----------------------------------------
//  The threshold is sampled from a blue-noise field, not a Bayer grid: Bayer's regular
//  lattice reads as a visible texture crawling across a large silhouette, whereas blue
//  noise has no low-frequency structure so the dissolve looks like fine grain. The
//  correctness that math CAN pin is the coverage law - for thresholds spread evenly over
//  [0,1), the kept FRACTION equals t - so the dissolve trades area linearly and hits full
//  coverage exactly at the band ends. The band itself is a FRACTION of the switch
//  distance, never a fixed metre count: a distant, physically larger switch gets a
//  proportionally wider band, so the dissolve subtends the same on-screen angle at any
//  scale - the same reason the LOD choice rides a relative height rather than pixels.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Fade fraction and clip decision are static functions of numbers - distance, band,
//  a sampled threshold - so a self-test pins the partition (complementary, monotone in
//  t, correct coverage) without a camera or a shader. The shader mirrors KeepIncoming /
//  KeepOutgoing per fragment against a blue-noise sample; the queue assignment that keeps
//  both meshes opaque is the scene/material layer this feeds.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure reference for the dithered cross-fade between two LODs (or a LOD and its impostor): the fade
    /// fraction across a switch-relative band, and the complementary per-fragment clip decision the shader
    /// mirrors. No blend, no camera, no allocation - the arithmetic the opaque-queue dither rides on.
    /// </summary>
    public static class DitherCrossFade
    {
        /// <summary>Default transition band width as a fraction of the switch distance: the fade runs from the
        /// switch point out to switchDistance × (1 + this).</summary>
        public const float DefaultBandFraction = 0.15f;

        /// <summary>
        /// The far end of the transition band, in metres: <paramref name="bandStartM"/> × (1 + bandFraction). The
        /// band is a proportion of the switch distance, so a farther switch gets a wider band and the dissolve
        /// subtends a constant on-screen angle. Fails closed to bandStart on degenerate input.
        /// </summary>
        public static float TransitionBandEndM(float bandStartM, float bandFraction = DefaultBandFraction)
        {
            if (bandStartM <= 0f || bandFraction <= 0f) return bandStartM;
            return bandStartM * (1f + bandFraction);
        }

        /// <summary>
        /// The fade fraction t across the band: 0 at and before the switch point (full outgoing mesh), rising
        /// linearly to 1 at the band's far end (full incoming mesh). Because the band scales with the switch
        /// distance, the same physical recession maps to the same t at any scale. Clamped to [0,1].
        /// </summary>
        public static float FadeFraction(float distanceM, float bandStartM, float bandFraction = DefaultBandFraction)
        {
            if (bandStartM <= 0f) return 0f;
            float end = TransitionBandEndM(bandStartM, bandFraction);
            float width = end - bandStartM;
            if (width <= 0f) return distanceM >= bandStartM ? 1f : 0f;
            return Mathf.Clamp01((distanceM - bandStartM) / width);
        }

        /// <summary>
        /// Whether the INCOMING mesh keeps this fragment: its blue-noise threshold is below the fade fraction.
        /// As t rises from 0 to 1 the kept set only grows (a fragment once incoming stays incoming), so the
        /// dissolve never flickers a fragment back. At t = 0 none are kept; approaching t = 1 all are.
        /// </summary>
        public static bool KeepIncoming(float ditherThreshold01, float t)
        {
            return ditherThreshold01 < Mathf.Clamp01(t);
        }

        /// <summary>
        /// Whether the OUTGOING mesh keeps this fragment: the exact complement of <see cref="KeepIncoming"/>, so
        /// every fragment is covered by exactly one mesh at any t - no hole, no double-draw. At t = 0 all are
        /// kept (full outgoing); at t = 1 none are (handed fully to the incoming mesh).
        /// </summary>
        public static bool KeepOutgoing(float ditherThreshold01, float t)
        {
            return !KeepIncoming(ditherThreshold01, t);
        }

        /// <summary>
        /// The fraction of the silhouette the incoming mesh covers at fade fraction t, for blue-noise thresholds
        /// spread evenly over [0,1): exactly t. This is the coverage law - the dissolve trades area linearly and
        /// reaches full coverage precisely at the band ends - and it is what a Bayer grid's clustering would break.
        /// </summary>
        public static float IncomingCoverageFraction(float t)
        {
            return Mathf.Clamp01(t);
        }
    }
}
