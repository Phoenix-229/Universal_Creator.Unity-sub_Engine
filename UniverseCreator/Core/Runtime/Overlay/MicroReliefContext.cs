// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefContext.cs
//  Purpose: The complete environmental state a single micro-relief sample point sees,
//           as one value type passed by "in". This is the INPUT contract for the
//           process evaluator: every named process reads from here and nothing else,
//           so a process is a pure function of the ground's real physical state and
//           cannot reach for a global or a random number.
//
//  IT REUSES THE STATE THAT ALREADY EXISTS
//  ---------------------------------------
//  The bulk of the state - slope, aspect, curvature, the thermal and water-balance
//  climate, salt, soil moisture, canopy light and leaf area, wind exposure and snow
//  load - is exactly Module 3's EnvironmentSample, so this carries one by value rather
//  than restating thirty-seven fields that could drift out of sync. The § 9.4 bedrock
//  and surficial-cover samples ride alongside it, and a small tail carries the handful
//  of geomorphic drivers the ecology sampler does not: the overlay's own vertex spacing
//  (which the Nyquist clamp needs), the periglacial freeze-thaw rate, the upslope
//  contributing area for channelised flow, the boundary shear stress for entrainment,
//  and the wave run-up height at the shore. Whoever fills those wires § 5/6/7 in; this
//  file only fixes what a process is allowed to know.
// -----------------------------------------------------------------------------

using UniverseCreator.Ecology;
using UniverseCreator.Geology;

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// Everything one micro-relief sample point knows, in real units. Passed by <c>in</c> to every
    /// process so the evaluation stays allocation-free and each process reads a pure snapshot.
    /// </summary>
    public struct MicroReliefContext
    {
        /// <summary>The shared Module 3 environment snapshot: form, climate, water, soil, disturbance, canopy.</summary>
        public EnvironmentSample env;

        /// <summary>§ 9.4 bedrock unit resolved at the surface, with its erodibility, joints, solubility, grain size.</summary>
        public LithoSample litho;

        /// <summary>§ 9.4 surficial cover at the point: its type, depth in metres and grain size.</summary>
        public CoverSample cover;

        /// <summary>m between adjacent overlay vertices. The Nyquist clamp (L-50) compares process wavelength to twice this.</summary>
        public float vertexSpacingM;

        /// <summary>cycles/yr the surface crosses 0 degC - the master rate for sorted ground and solifluction (L-36, L-37).</summary>
        public float freezeThawCyclesPerYr;

        /// <summary>m2 upslope area draining through this point - the A in the rill stream-power law A^m S^n (L-44).</summary>
        public float contributingAreaM2;

        /// <summary>Pa boundary shear stress on the surface, tested against the Shields entrainment threshold (L-33, L-41).</summary>
        public float shearStressPa;

        /// <summary>m height the § 7 waves run up the shore, defining the berm/notch band (L-48).</summary>
        public float waveRunupHeightM;

        /// <summary>World seed, so any sub-cell placement a process needs is deterministic across tiles.</summary>
        public int seed;
    }
}
