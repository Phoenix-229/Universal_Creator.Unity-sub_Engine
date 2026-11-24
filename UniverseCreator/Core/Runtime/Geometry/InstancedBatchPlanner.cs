// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    InstancedBatchPlanner.cs
//  Purpose: The instanced-draw plan for props, WITHIN the C-01 managed-only limit.
//           Every prototype that renders as a mesh is drawn through instancing,
//           batched by mesh-and-material and sliced into groups of at most 1023
//           matrices - the ceiling of the managed-array overload of
//           Graphics.RenderMeshInstanced with a filled RenderParams. Twenty thousand
//           rocks of six prototypes collapse to a couple of dozen draw calls, not
//           twenty thousand, and the per-frame render path reuses its matrix arrays
//           so it allocates nothing.
//
//  WHY NOT BatchRendererGroup / THE GPU RESIDENT DRAWER
//  ----------------------------------------------------
//  Both are the faster path and both are OUT OF SCOPE: they require NativeArray, which
//  C-01 excludes. That is stated in this header so nobody re-litigates it. What is
//  delivered instead: per-batch managed matrix arrays reused across frames, instances
//  grouped by a (mesh, material) key so a draw call never straddles two materials, and
//  the group order sorted so the number of material switches is the number of distinct
//  materials - not the number of instances. Per-instance data is limited to what a
//  MaterialPropertyBlock's float arrays hold.
//
//  THE ARITHMETIC THE DONE-WHEN RIDES ON
//  -------------------------------------
//  Draw calls for one batch = ceil(instanceCount / 1023). Total = the sum over the
//  distinct (mesh, material) batches. Slice sizes are 1023 until the remainder, and
//  they always sum back to the instance count - the invariant that proves no instance
//  is dropped or double-drawn. All of it is a static function of counts, so a self-test
//  reproduces the "tens of draw calls for 20 000 rocks" figure and the slice partition
//  without a GPU; the RenderParams fill and the actual reused-array dispatch are the
//  thin scene layer above.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>Identifies an instanced batch: one draw call never mixes two meshes or two materials, so the
    /// batch key is the (mesh, material) pair. Value-equal so grouping is deterministic.</summary>
    public readonly struct MeshMaterialKey : IEquatable<MeshMaterialKey>
    {
        public readonly int MeshId;
        public readonly int MaterialId;

        public MeshMaterialKey(int meshId, int materialId)
        {
            MeshId = meshId;
            MaterialId = materialId;
        }

        public bool Equals(MeshMaterialKey other) => MeshId == other.MeshId && MaterialId == other.MaterialId;
        public override bool Equals(object obj) => obj is MeshMaterialKey k && Equals(k);
        public override int GetHashCode() => unchecked((MeshId * 397) ^ MaterialId);
    }

    /// <summary>
    /// Pure arithmetic of the managed instanced-draw plan: draw-call counts, slice partition, and material-switch
    /// count. No GPU, no allocation in the counting path - the model a self-test pins the done-when against.
    /// </summary>
    public static class InstancedBatchPlanner
    {
        /// <summary>The managed-array overload of Graphics.RenderMeshInstanced draws at most this many matrices
        /// per call. Batches larger than this are sliced.</summary>
        public const int MaxInstancesPerDraw = 1023;

        /// <summary>Draw calls one (mesh, material) batch needs: ceil(instanceCount / 1023). Zero for an empty batch.</summary>
        public static int DrawCallsForInstances(int instanceCount)
        {
            if (instanceCount <= 0) return 0;
            return (instanceCount + MaxInstancesPerDraw - 1) / MaxInstancesPerDraw;
        }

        /// <summary>Total draw calls across a set of batches: the sum of each batch's sliced draw-call count. This
        /// is the "tens, not thousands" figure the done-when checks. Null list → 0.</summary>
        public static int TotalDrawCalls(IReadOnlyList<int> perBatchInstanceCounts)
        {
            if (perBatchInstanceCounts == null) return 0;
            int total = 0;
            for (int i = 0; i < perBatchInstanceCounts.Count; i++)
                total += DrawCallsForInstances(perBatchInstanceCounts[i]);
            return total;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the slice sizes for a batch: 1023 until the remainder, which is the
        /// last (smaller) slice. The sizes always sum back to <paramref name="instanceCount"/> - no instance is
        /// dropped or double-drawn. The list is cleared and reused (the caller keeps it across frames), modelling
        /// the zero-allocation render path. Returns the slice count.
        /// </summary>
        public static int FillSliceSizes(int instanceCount, List<int> into)
        {
            if (into == null) return 0;
            into.Clear();
            if (instanceCount <= 0) return 0;
            int remaining = instanceCount;
            while (remaining > 0)
            {
                int slice = remaining >= MaxInstancesPerDraw ? MaxInstancesPerDraw : remaining;
                into.Add(slice);
                remaining -= slice;
            }
            return into.Count;
        }

        /// <summary>
        /// The number of material switches a GPU incurs walking a draw order, = 1 per run of a new material id
        /// (the first draw counts as a switch). When instances are grouped by material this equals the number of
        /// DISTINCT materials; in a random order it approaches the number of draws - which is what grouping avoids.
        /// Null or empty → 0.
        /// </summary>
        public static int MaterialSwitchCount(IReadOnlyList<int> materialIdsInDrawOrder)
        {
            if (materialIdsInDrawOrder == null || materialIdsInDrawOrder.Count == 0) return 0;
            int switches = 1;
            for (int i = 1; i < materialIdsInDrawOrder.Count; i++)
                if (materialIdsInDrawOrder[i] != materialIdsInDrawOrder[i - 1])
                    switches++;
            return switches;
        }

        /// <summary>
        /// Groups a flat sequence of per-instance batch keys into total draw calls and distinct batch count. This
        /// runs at chunk-load time (it allocates a small dictionary), NOT in the per-frame render path. Returns
        /// the total sliced draw calls; <paramref name="batchCount"/> receives the number of distinct batches.
        /// </summary>
        public static int PlanDrawCalls(IReadOnlyList<MeshMaterialKey> instanceKeys, out int batchCount)
        {
            batchCount = 0;
            if (instanceKeys == null || instanceKeys.Count == 0) return 0;
            var counts = new Dictionary<MeshMaterialKey, int>();
            for (int i = 0; i < instanceKeys.Count; i++)
            {
                counts.TryGetValue(instanceKeys[i], out int c);
                counts[instanceKeys[i]] = c + 1;
            }
            batchCount = counts.Count;
            int total = 0;
            foreach (var kv in counts)
                total += DrawCallsForInstances(kv.Value);
            return total;
        }
    }
}
