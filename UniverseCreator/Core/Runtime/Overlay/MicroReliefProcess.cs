// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefProcess.cs
//  Purpose: The contract every micro-relief process obeys, and the shared deterministic
//           noise it draws its sub-cell pattern from. A process is a NAMED class tied to
//           a NAMED law that, given the ground's physical state, answers three questions:
//           am I active here, what is my characteristic wavelength, and how many
//           millimetres of relief do I build (signed - some processes REMOVE relief).
//           Nothing else. The evaluator, not the process, decides whether that
//           displacement becomes geometry or texture (the Nyquist clamp, L-50).
//
//  WHY AMPLITUDES ARE MILLIMETRES, NOT MULTIPLIERS
//  -----------------------------------------------
//  A process contributes what it physically builds - a 60 mm sand ripple, a 300 mm
//  solifluction riser - not a unit-less weight to be tuned by feel. That is what lets
//  a granite ridge and a limestone pavement come out visibly different for a physical
//  reason rather than an authored one, and it is why the field can be summed at all:
//  millimetres add, multipliers do not.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Overlay
{
    /// <summary>Which channel a process's contribution lands in once the Nyquist clamp has judged it.</summary>
    public enum ReliefChannel
    {
        /// <summary>Coarse enough to displace vertices without aliasing.</summary>
        Displacement = 0,
        /// <summary>Finer than the vertex grid can resolve - belongs in the normal map / splat, not geometry.</summary>
        Texture = 1
    }

    /// <summary>
    /// One named geomorphic process. Pure: its output depends only on the <see cref="MicroReliefContext"/>
    /// passed in, so the same point always yields the same relief and tiles agree across their shared edge.
    /// </summary>
    public abstract class MicroReliefProcess
    {
        /// <summary>Stable identifier, e.g. "Karren". Used by the debug view to solo a process.</summary>
        public abstract string ProcessId { get; }

        /// <summary>The law this process reproduces, e.g. "L-40". Documentation, and a check that every process cites one.</summary>
        public abstract string Law { get; }

        /// <summary>True where this process actually operates, from the point's physical state.</summary>
        public abstract bool IsEligible(in MicroReliefContext c);

        /// <summary>The process's characteristic wavelength in metres, which the Nyquist clamp compares to twice the vertex spacing.</summary>
        public abstract float CharacteristicWavelengthM(in MicroReliefContext c);

        /// <summary>Signed displacement in millimetres at this point. Positive builds up, negative planes down.</summary>
        public abstract float DisplacementMm(in MicroReliefContext c);

        /// <summary>
        /// How fast this process is currently reworking the surface, in millimetres per year, read from
        /// its OWN law and the point's state - a chemical weathering front creeps, a wave berm is rebuilt
        /// with every tide. The field weights each contribution by this rate so the fastest process claims
        /// the surface (the honest alternative to summing everything at full strength). Return &lt;= 0 to sit
        /// out the competition even where <see cref="IsEligible"/> was true at its threshold.
        /// </summary>
        public abstract float ActivityRateMmPerYr(in MicroReliefContext c);

        // ---- Shared deterministic noise ---------------------------------------
        //
        // Every sub-cell pattern (ripple crests, polygon centres, pit positions) comes
        // from this one integer-hash value noise, so it is bit-identical across tiles and
        // allocates nothing. A process picks its own cell size (its wavelength) and a
        // salt so two processes at the same point do not share a pattern.

        /// <summary>Integer hash to a well-mixed uint. Deterministic, branch-free, no allocation.</summary>
        protected static uint Hash(int x, int z, int seed, int salt)
        {
            unchecked
            {
                uint h = (uint)seed * 2166136261u;
                h = (h ^ (uint)x) * 16777619u;
                h = (h ^ (uint)z) * 16777619u;
                h = (h ^ (uint)salt) * 16777619u;
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                return h;
            }
        }

        /// <summary>Hashed lattice value in [0,1) at integer cell (x,z).</summary>
        private static float Lattice01(int x, int z, int seed, int salt)
        {
            return (Hash(x, z, seed, salt) & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>
        /// Smooth value noise in [-1, 1] at world XZ, one bump per <paramref name="cellM"/> metres.
        /// Bilinear interpolation with a smoothstep fade - continuous, so it never seams. Internal so the
        /// transient snow layer (which is not a process) can draw from the same deterministic field.
        /// </summary>
        internal static float ValueNoise(float x, float z, float cellM, int seed, int salt)
        {
            if (cellM < 1e-4f) cellM = 1e-4f;
            float fx = x / cellM, fz = z / cellM;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            float ux = tx * tx * (3f - 2f * tx);
            float uz = tz * tz * (3f - 2f * tz);
            float v00 = Lattice01(x0, z0, seed, salt);
            float v10 = Lattice01(x0 + 1, z0, seed, salt);
            float v01 = Lattice01(x0, z0 + 1, seed, salt);
            float v11 = Lattice01(x0 + 1, z0 + 1, seed, salt);
            float a = Mathf.Lerp(v00, v10, ux);
            float b = Mathf.Lerp(v01, v11, ux);
            return Mathf.Lerp(a, b, uz) * 2f - 1f;
        }
    }
}
