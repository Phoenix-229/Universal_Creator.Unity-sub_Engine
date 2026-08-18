// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GPUInstancingManager.cs
//  Purpose: Batches scattered instances into GPU-instanced draw calls.
//
//  Module 1 provides the API and the submit loop. Modules 3-4 (vegetation, props)
//  feed it instance matrices during chunk generation; this class groups them by
//  mesh+material and submits them through Graphics.DrawMeshInstanced.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Optimization
{
    /// <summary>
    /// Batches identical meshes into GPU instanced draw calls, used by the
    /// vegetation and prop scatter systems.
    /// </summary>
    public class GPUInstancingManager : MonoBehaviour
    {
        private class InstanceBatch
        {
            public Mesh mesh;
            public Material material;
            public List<Matrix4x4> matrices = new List<Matrix4x4>();
            public int layer;
            public UnityEngine.Rendering.ShadowCastingMode shadowCastingMode;
            public bool receiveShadows;
        }

        // Unity's per-call instancing limit. Larger sets are split across calls.
        private const int MaxInstancesPerBatch = 1023;

        private readonly Dictionary<int, InstanceBatch> batches = new Dictionary<int, InstanceBatch>();

        // Which batches each owner contributed to. Chunk scatter is the reason this
        // exists: a recycled chunk has to withdraw exactly its own instances, and
        // without an owner key the only options are leaking them or clearing the
        // whole world - one duplicates every prop on recentre, the other blanks
        // every chunk that was not part of the pass.
        private readonly Dictionary<int, List<int>> byOwner = new Dictionary<int, List<int>>();

        // Reused across submits so a full world of instances does not allocate a
        // fresh array every frame.
        private readonly Matrix4x4[] submitBuffer = new Matrix4x4[MaxInstancesPerBatch];

        /// <summary>Batch key for an owner+mesh+material+layer tuple.</summary>
        public static int BatchId(int owner, Mesh mesh, Material material, int layer)
        {
            // GetEntityId, not GetInstanceID: the latter is obsolete-as-error in
            // Unity 6.5 and fails the build outright rather than warning.
            // The ids are hashed into locals first because GetEntityId returns an
            // EntityId struct, and folding that into a ternary against 0 asks the
            // compiler to reconcile two types that convert to one another.
            int meshId = mesh != null ? mesh.GetEntityId().GetHashCode() : 0;
            int materialId = material != null ? material.GetEntityId().GetHashCode() : 0;

            int h = 17;
            h = h * 31 + owner;
            h = h * 31 + meshId;
            h = h * 31 + materialId;
            h = h * 31 + layer;
            return h;
        }

        /// <summary>Batch key for an unowned mesh+material+layer triple.</summary>
        public static int BatchId(Mesh mesh, Material material, int layer)
        {
            return BatchId(0, mesh, material, layer);
        }

        /// <summary>
        /// Registers one instance for batched rendering. Scatter systems call this
        /// per placed object; nothing is drawn until LateUpdate.
        /// </summary>
        public void AddInstance(Mesh mesh, Material material, Matrix4x4 matrix, int layer,
                                UnityEngine.Rendering.ShadowCastingMode shadowCasting =
                                    UnityEngine.Rendering.ShadowCastingMode.On,
                                bool receiveShadows = true)
        {
            AddInstance(0, mesh, material, matrix, layer, shadowCasting, receiveShadows);
        }

        /// <summary>
        /// Registers one instance against an owner, so ClearOwner can withdraw it
        /// again without touching anything else.
        /// </summary>
        public void AddInstance(int owner, Mesh mesh, Material material, Matrix4x4 matrix, int layer,
                                UnityEngine.Rendering.ShadowCastingMode shadowCasting =
                                    UnityEngine.Rendering.ShadowCastingMode.On,
                                bool receiveShadows = true)
        {
            if (mesh == null || material == null) return;

            int id = BatchId(owner, mesh, material, layer);

            InstanceBatch batch;
            if (!batches.TryGetValue(id, out batch))
            {
                batch = new InstanceBatch();
                batch.mesh = mesh;
                batch.material = material;
                batch.layer = layer;
                batch.shadowCastingMode = shadowCasting;
                batch.receiveShadows = receiveShadows;
                batches[id] = batch;

                List<int> owned;
                if (!byOwner.TryGetValue(owner, out owned))
                {
                    owned = new List<int>();
                    byOwner[owner] = owned;
                }
                owned.Add(id);
            }

            batch.matrices.Add(matrix);
        }

        /// <summary>Drops every registered instance. Call when regenerating the world.</summary>
        public void ClearAll()
        {
            batches.Clear();
            byOwner.Clear();
        }

        /// <summary>Drops every batch one owner contributed, e.g. a recycled chunk.</summary>
        public void ClearOwner(int owner)
        {
            List<int> owned;
            if (!byOwner.TryGetValue(owner, out owned)) return;

            for (int i = 0; i < owned.Count; i++) batches.Remove(owned[i]);
            byOwner.Remove(owner);
        }

        /// <summary>Drops one batch, e.g. when its source chunk is recycled.</summary>
        public void ClearBatch(Mesh mesh, Material material, int layer)
        {
            batches.Remove(BatchId(0, mesh, material, layer));
        }

        /// <summary>Total instances currently queued, across all batches.</summary>
        public int InstanceCount
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<int, InstanceBatch> kvp in batches)
                    total += kvp.Value.matrices.Count;
                return total;
            }
        }

        // LateUpdate rather than Update: scatter systems populate during Update, so
        // submitting later in the frame guarantees the batch is complete.
        private void LateUpdate()
        {
            foreach (KeyValuePair<int, InstanceBatch> kvp in batches)
            {
                InstanceBatch batch = kvp.Value;
                if (batch.mesh == null || batch.material == null) continue;

                int count = batch.matrices.Count;
                int drawn = 0;

                while (drawn < count)
                {
                    int slice = Mathf.Min(MaxInstancesPerBatch, count - drawn);
                    batch.matrices.CopyTo(drawn, submitBuffer, 0, slice);

                    Graphics.DrawMeshInstanced(batch.mesh, 0, batch.material, submitBuffer, slice,
                                               null, batch.shadowCastingMode, batch.receiveShadows,
                                               batch.layer);
                    drawn += slice;
                }
            }
        }
    }
}
