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

            // m. Beyond this the batch is submitted without shadows. Negative means
            // no limit. Per batch and not per instance: a batch is one species'
            // one mesh inside one chunk, so the whole batch is at much the same
            // distance, and one distance test per batch per frame is a cost that
            // does not scale with the instance count. (V-32)
            public float shadowDistanceM;

            // V-28. RenderMeshInstanced does not compute bounds; the legacy call did.
            // So the batch accumulates its own as instances arrive - which costs nothing,
            // because instances arrive during chunk generation and not per frame.
            //
            // Left at default this whole system culls wrong: a zero-size bounds at the
            // origin means Unity decides a forest is off-screen whenever the camera is
            // not looking at world zero. That reads exactly like a placement bug, which
            // is why the task warns about it and why it is tracked here rather than
            // guessed at submit time.
            public Bounds worldBounds;
            public bool hasBounds;

            public void Include(in Matrix4x4 m, Mesh source)
            {
                if (source == null) return;

                Bounds local = source.bounds;

                // The exact axis-aligned box around the transformed box. Summing the
                // absolute basis rows is the standard result and needs no corner loop:
                // each output extent is how far the box reaches along that world axis.
                Vector3 e = local.extents;
                Vector3 extent = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);

                Bounds b = new Bounds(m.MultiplyPoint3x4(local.center), extent * 2f);

                if (!hasBounds)
                {
                    worldBounds = b;
                    hasBounds = true;
                    return;
                }

                worldBounds.Encapsulate(b);
            }
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
                                bool receiveShadows = true,
                                float shadowDistanceM = -1f)
        {
            AddInstance(0, mesh, material, matrix, layer, shadowCasting, receiveShadows,
                        shadowDistanceM);
        }

        /// <summary>
        /// Registers one instance against an owner, so ClearOwner can withdraw it
        /// again without touching anything else.
        /// </summary>
        public void AddInstance(int owner, Mesh mesh, Material material, Matrix4x4 matrix, int layer,
                                UnityEngine.Rendering.ShadowCastingMode shadowCasting =
                                    UnityEngine.Rendering.ShadowCastingMode.On,
                                bool receiveShadows = true,
                                float shadowDistanceM = -1f)
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
                batch.shadowDistanceM = shadowDistanceM;
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
            batch.Include(in matrix, batch.mesh);
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

        /// <summary>
        /// Where the shadow-distance test measures from. The scene-view camera in
        /// the editor, so an author sees the same culling the player will, and
        /// Camera.main at runtime. No camera at all means no distance test rather
        /// than a guessed origin: silently measuring from the world origin would
        /// strip shadows from everything not near it. (V-32)
        /// </summary>
        private static bool TryGetViewerPosition(out Vector3 position)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.SceneView sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null)
                {
                    position = sv.camera.transform.position;
                    return true;
                }
            }
#endif
            Camera cam = Camera.main;
            if (cam != null)
            {
                position = cam.transform.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        // LateUpdate rather than Update: scatter systems populate during Update, so
        // submitting later in the frame guarantees the batch is complete.
        private void LateUpdate()
        {
            // One camera position per frame, not one per batch: every batch measures
            // against the same viewer, and Camera.main is a tagged-object search.
            Vector3 viewer;
            bool haveViewer = TryGetViewerPosition(out viewer);

            foreach (KeyValuePair<int, InstanceBatch> kvp in batches)
            {
                InstanceBatch batch = kvp.Value;
                if (batch.mesh == null || batch.material == null) continue;

                // V-32. A shadow caster is re-drawn once per shadow cascade, so
                // dropping distant casters is the highest-yield mobile saving in the
                // module. Measured to the batch bounds rather than its centre, so a
                // batch that reaches toward the camera keeps its shadow.
                UnityEngine.Rendering.ShadowCastingMode shadowMode = batch.shadowCastingMode;

                if (shadowMode != UnityEngine.Rendering.ShadowCastingMode.Off &&
                    batch.shadowDistanceM >= 0f && haveViewer)
                {
                    float nearest = Mathf.Sqrt(batch.worldBounds.SqrDistance(viewer));
                    if (nearest > batch.shadowDistanceM)
                        shadowMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                int count = batch.matrices.Count;
                int drawn = 0;

                while (drawn < count)
                {
                    int slice = Mathf.Min(MaxInstancesPerBatch, count - drawn);
                    batch.matrices.CopyTo(drawn, submitBuffer, 0, slice);

                    // V-28. RenderMeshInstanced is the Unity 6 path; the managed-array
                    // overload is the one that keeps C-01 (no NativeArray), which is also
                    // why BatchRendererGroup and the GPU Resident Drawer stay out of
                    // scope - both require NativeArray and neither can be reached from
                    // here without breaking that constraint.
                    // RenderParams lives in UnityEngine, not UnityEngine.Rendering -
                    // only the enums it carries (ShadowCastingMode, LightProbeUsage)
                    // come from Rendering.
                    UnityEngine.RenderParams rp =
                        new UnityEngine.RenderParams(batch.material);

                    rp.layer = batch.layer;
                    rp.shadowCastingMode = shadowMode;
                    rp.receiveShadows = batch.receiveShadows;

                    // RenderParams' constructor zeroes this to Off, while the legacy
                    // DrawMeshInstanced overload defaulted it to BlendProbes. Left alone,
                    // instanced vegetation loses its baked probe contribution and reads
                    // flat and dark next to the GameObject-spawned instances of the same
                    // species - which is the one difference this task must not introduce.
                    // Reflection probes are not restored alongside it: the legacy call
                    // never exposed them, so Off is what the old path already did.
                    rp.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;

                    // The batch's whole extent, not this slice's. Deliberately
                    // conservative: an over-large bounds can only make Unity draw
                    // something it might have culled, while a too-small one culls
                    // geometry that is on screen. Only one of those two failures is
                    // visible, so the error is taken in the harmless direction.
                    rp.worldBounds = batch.worldBounds;

                    Graphics.RenderMeshInstanced(rp, batch.mesh, 0, submitBuffer, slice);

                    drawn += slice;
                }
            }
        }
    }
}
