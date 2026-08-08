// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ValueNoiseNode.cs
//  Purpose: Interpolated lattice (value) noise creator with fBm octave control.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    /// <summary>Reconstruction filter used between value-noise lattice points.</summary>
    public enum ValueInterpolation
    {
        /// <summary>Bilinear, no fade. Faceted; the lattice stays visible.</summary>
        Linear = 0,
        /// <summary>Bicubic over a 4x4 neighbourhood. Smoothest, and the costliest.</summary>
        Cubic = 1,
        /// <summary>Quintic-faded bilinear. The default: smooth with no creasing.</summary>
        Quintic = 2
    }

    [System.Serializable]
    public class ValueNoiseNode : NoiseCreatorBase
    {
        public override string NodeName { get { return "Value Noise"; } }
        public override string Tooltip { get { return "Interpolated lattice noise. Softer and blobbier than Perlin."; } }

        [Tooltip("Filter used between lattice points. Cubic costs roughly 4x Quintic.")]
        public ValueInterpolation interpolation = ValueInterpolation.Quintic;

        // The interpolation choice is expressed as the basis handed to the shared
        // fBm driver, so no octave-loop code is duplicated to support it.
        protected override NoiseType BasisType
        {
            get
            {
                switch (interpolation)
                {
                    case ValueInterpolation.Linear: return NoiseType.ValueLinear;
                    case ValueInterpolation.Cubic:  return NoiseType.ValueCubic;
                    default:                        return NoiseType.Value;
                }
            }
        }
    }
}
