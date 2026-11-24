// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    TerrainLayerAssignmentGuard.cs
//  Purpose: The compare-before-write guard for TerrainData.terrainLayers (§ 9.7 G-46).
//           Verified Unity 6 behaviour, and it destroys user work SILENTLY: assigning
//           TerrainData.terrainLayers reallocates the alphamap textures and resets every
//           weight to zero, so any painted or previously composited data is gone the
//           instant the array is set - even to an equal array. TerrainData.SetTerrainLayers
//           does not exist in Unity 6; the setter IS the property, so there is no "safe"
//           overload to reach for. The only safe path is to compare the existing array
//           element-by-element against the desired one and write ONLY on a real difference.
//           The same caution applies to any change of the layer COUNT, including a quality
//           tier switch - which is the done-when: switching tier on a painted terrain must
//           leave the paint intact.
//
//  ORDER IS SIGNIFICANT
//  --------------------
//  A layer's index in the array is its alphamap channel, so two arrays with the same
//  members in a different order are NOT equal - reordering would remap every painted
//  weight to the wrong layer. The comparison is therefore positional (element i vs
//  element i), never a set membership test.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Generic over the element type so the self-test can pin it with plain identities and
//  no TerrainLayer asset or TerrainData: the logic is "differs iff length differs or any
//  positional element differs". The actual guarded property write - assign iff this
//  returns true - is the scene layer, where a genuine change must also re-composite the
//  paint the reallocation drops.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure compare-before-write logic for a terrain's layer array: decides whether assigning would actually
    /// change anything, so the caller can skip the destructive <c>terrainLayers</c> write that would reset every
    /// painted weight. Positional (order-significant) comparison; generic and device-free.
    /// </summary>
    public static class TerrainLayerAssignmentGuard
    {
        /// <summary>
        /// True iff <paramref name="desired"/> differs from <paramref name="current"/> - a different length, or any
        /// positional element that is not equal by the default comparer. Two null/empty arrays do not differ; a
        /// null on one side and content on the other do. This is the whole test for whether a write is warranted.
        /// </summary>
        public static bool LayersDiffer<T>(IReadOnlyList<T> current, IReadOnlyList<T> desired)
        {
            int cn = current?.Count ?? 0;
            int dn = desired?.Count ?? 0;
            if (cn != dn) return true;
            if (cn == 0) return false;

            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < cn; i++)
                if (!cmp.Equals(current[i], desired[i])) return true;
            return false;
        }

        /// <summary>
        /// Whether the caller should assign <paramref name="desired"/> to <c>terrainLayers</c> at all: only when it
        /// actually differs. Named for the call site - <c>if (ShouldAssign(...)) terrainData.terrainLayers = ...;</c>
        /// - so the destructive write never fires on an equal array. Identical to <see cref="LayersDiffer{T}"/>.
        /// </summary>
        public static bool ShouldAssign<T>(IReadOnlyList<T> current, IReadOnlyList<T> desired)
            => LayersDiffer(current, desired);
    }
}
