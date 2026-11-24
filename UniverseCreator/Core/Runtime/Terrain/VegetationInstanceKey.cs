// UC-T06-06: retain the complete identity tuple; a hash collision must never remove a different instance.
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>A serialized, versioned identity for one world/layer/prototype/candidate, independent of demand chunks and run revisions.</summary>
    /// <remarks>Region coordinates are canonical one-metre world cells, not residency chunk coordinates. Candidate coordinates retain exact float bits; Y, scale and appearance are excluded.</remarks>
    [Serializable]
    public struct VegetationInstanceKey : IEquatable<VegetationInstanceKey>
    {
        [SerializeField] private int version;
        [SerializeField] private string world, layer, prototype;
        [SerializeField] private long regionX, regionZ;
        [SerializeField] private int candidateX, candidateZ;

        /// <summary>Supported persisted schema; zero denotes an unstamped instance.</summary>
        public int Version => version;
        /// <summary>Stable serialized world namespace, or null for an unstamped key.</summary>
        public string World => world;
        /// <summary>UC-T06-18: stable prototype namespace, used to resolve saved manual intent after roster reordering.</summary>
        public string Prototype => prototype;
        /// <summary>True only for the supported schema and nonempty identity components.</summary>
        public bool IsAssigned => version == 1 && !string.IsNullOrEmpty(world) && !string.IsNullOrEmpty(layer) && !string.IsNullOrEmpty(prototype);

        /// <summary>Creates a full key without allocating per-candidate coordinate strings.</summary>
        /// <param name="worldId">Nonempty, persistent world namespace captured on the main thread.</param>
        /// <param name="layerId">Nonempty channel/serialized producer identity, preserved by graph migration.</param>
        /// <param name="prototypeId">Nonempty persistent prototype identity, independent of roster order.</param>
        /// <param name="worldX">Finite absolute candidate X in metres, within signed 64-bit metre cells.</param>
        /// <param name="worldZ">Finite absolute candidate Z in metres, within signed 64-bit metre cells.</param>
        /// <returns>Version 1 key; signed zero is normalized.</returns>
        /// <exception cref="ArgumentException">A component is empty, or a coordinate is nonfinite/out of range.</exception>
        public static VegetationInstanceKey Create(string worldId, string layerId, string prototypeId, float worldX, float worldZ)
        {
            if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(layerId) || string.IsNullOrEmpty(prototypeId) ||
                float.IsNaN(worldX) || float.IsInfinity(worldX) || float.IsNaN(worldZ) || float.IsInfinity(worldZ) ||
                (double)worldX <= long.MinValue || (double)worldX >= long.MaxValue ||
                (double)worldZ <= long.MinValue || (double)worldZ >= long.MaxValue)
                throw new ArgumentException("Vegetation identity requires persistent world/layer/prototype IDs and finite supported world coordinates.");
            return new VegetationInstanceKey { version = 1, world = worldId, layer = layerId, prototype = prototypeId,
                regionX = (long)Math.Floor(worldX), regionZ = (long)Math.Floor(worldZ),
                candidateX = Bits(worldX), candidateZ = Bits(worldZ) };
        }

        /// <summary>Compares every persisted component, so equal hash codes alone never establish identity.</summary>
        /// <param name="other">Candidate key, including unsupported/unassigned schemas.</param>
        /// <returns>True when the complete tuples match.</returns>
        public bool Equals(VegetationInstanceKey other) => version == other.version &&
            string.Equals(world, other.world, StringComparison.Ordinal) && string.Equals(layer, other.layer, StringComparison.Ordinal) &&
            string.Equals(prototype, other.prototype, StringComparison.Ordinal) && regionX == other.regionX && regionZ == other.regionZ &&
            candidateX == other.candidateX && candidateZ == other.candidateZ;
        /// <summary>Compares a boxed key with the same full-tuple rule.</summary>
        public override bool Equals(object obj) => obj is VegetationInstanceKey other && Equals(other);
        /// <summary>Returns a deterministic lookup hash; callers must retain full keys and use equality to resolve collisions.</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = version;
                hash = hash * 397 ^ TextHash(world); hash = hash * 397 ^ TextHash(layer); hash = hash * 397 ^ TextHash(prototype);
                hash = hash * 397 ^ regionX.GetHashCode(); hash = hash * 397 ^ regionZ.GetHashCode();
                return (hash * 397 ^ candidateX) * 397 ^ candidateZ;
            }
        }

        // UC-T06-08: variation uses the full tuple, never the collision-prone lookup hash or chunk/run order.
        internal float Variation01(uint stream)
        {
            if (!IsAssigned) throw new InvalidOperationException("Prototype variation requires a captured full instance identity.");
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                void Word(ulong value) { for (int i = 0; i < 8; i++) { hash ^= (byte)value; hash *= 1099511628211UL; value >>= 8; } }
                void Text(string value) { Word((ulong)value.Length); foreach (char c in value) { hash ^= c; hash *= 1099511628211UL; } }
                Word((uint)version); Text(world); Text(layer); Text(prototype);
                Word((ulong)regionX); Word((ulong)regionZ); Word((uint)candidateX); Word((uint)candidateZ); Word(stream);
                // Avalanche the stream suffix before selecting exactly 24 float-representable bits.
                hash ^= hash >> 30; hash *= 0xbf58476d1ce4e5b9UL;
                hash ^= hash >> 27; hash *= 0x94d049bb133111ebUL; hash ^= hash >> 31;
                return (hash >> 40) * (1f / 16777216f);
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits { [FieldOffset(0)] internal float value; [FieldOffset(0)] internal int bits; }
        private static int Bits(float value) => value == 0 ? 0 : new FloatBits { value = value }.bits;
        internal static int TextHash(string value)
        {
            unchecked { uint hash = 2166136261; if (value != null) foreach (char c in value) { hash ^= c; hash *= 16777619; } return (int)hash; }
        }
    }
}
