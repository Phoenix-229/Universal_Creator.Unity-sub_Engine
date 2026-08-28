// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    IrradianceProbe.cs
//  Purpose: One point's ambient light, stored as a six-direction ambient cube rather
//           than spherical harmonics: one incoming-radiance colour for each of the six
//           axial directions. An ambient cube is chosen over L1/L2 SH deliberately - it
//           evaluates in three lerps against a squared normal, it cannot ring the way SH
//           does when a bright light meets a dark one, and it interpolates trivially
//           across a grid of these. The struct is pure value data: six float-triples,
//           seventy-two bytes, no references, so a grid of a hundred thousand is a flat
//           managed array of a few megabytes and nothing to garbage-collect.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// A six-direction ambient cube: incoming radiance along +X, -X, +Y, -Y, +Z, -Z, each
    /// a linear-RGB <see cref="Vector3"/>. A blittable struct (no reference fields) so a
    /// grid of probes is one contiguous array. Evaluated by weighting the three axis pairs
    /// by the squared normal, the same rule the shader's sampler uses, so CPU and GPU agree.
    /// </summary>
    public struct IrradianceProbe
    {
        // Managed heap cost of one probe: six Vector3 faces, three floats each. Named
        // so a memory readout prices the resident probe array off the struct's own
        // layout rather than a hand-copied byte count that could drift from it.
        public const int ManagedBytes = 6 * 3 * sizeof(float);

        public Vector3 posX;
        public Vector3 negX;
        public Vector3 posY;   // the sky-facing face; ground bounce lands on negY
        public Vector3 negY;
        public Vector3 posZ;
        public Vector3 negZ;

        /// <summary>A probe with every face set to one uniform radiance - a flat fill.</summary>
        public static IrradianceProbe Uniform(Vector3 radiance)
        {
            return new IrradianceProbe
            {
                posX = radiance, negX = radiance,
                posY = radiance, negY = radiance,
                posZ = radiance, negZ = radiance,
            };
        }

        /// <summary>
        /// The radiance a surface with this normal receives: each axis pair chosen by the
        /// sign of the normal's component and weighted by its square. The squared
        /// components of a unit normal sum to one, which is why the three-term blend is a
        /// correct convex combination with no normalisation and no discontinuity as the
        /// normal swings across an axis (the weight there is zero).
        /// </summary>
        public Vector3 Evaluate(Vector3 normal)
        {
            Vector3 w = new Vector3(normal.x * normal.x, normal.y * normal.y, normal.z * normal.z);
            Vector3 x = normal.x >= 0f ? posX : negX;
            Vector3 y = normal.y >= 0f ? posY : negY;
            Vector3 z = normal.z >= 0f ? posZ : negZ;
            return w.x * x + w.y * y + w.z * z;
        }

        /// <summary>
        /// Component-wise blend of two probes - the operation the grid's trilinear
        /// interpolation is built from. t is not clamped; the grid passes it in [0,1].
        /// </summary>
        public static IrradianceProbe Lerp(IrradianceProbe a, IrradianceProbe b, float t)
        {
            return new IrradianceProbe
            {
                posX = Vector3.LerpUnclamped(a.posX, b.posX, t),
                negX = Vector3.LerpUnclamped(a.negX, b.negX, t),
                posY = Vector3.LerpUnclamped(a.posY, b.posY, t),
                negY = Vector3.LerpUnclamped(a.negY, b.negY, t),
                posZ = Vector3.LerpUnclamped(a.posZ, b.posZ, t),
                negZ = Vector3.LerpUnclamped(a.negZ, b.negZ, t),
            };
        }
    }
}
