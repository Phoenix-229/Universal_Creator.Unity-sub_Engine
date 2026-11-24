// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GPUInstancingManager.cs
//  Purpose: Batches scattered instances into GPU-instanced draw calls.
//
//  Module 1 provides the API and the submit loop. Modules 3-4 (vegetation, props)
//  feed it instance matrices during chunk generation; this class groups them by
//  mesh+material and submits them through Graphics.RenderMeshInstanced. // UC-T06-10
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Optimization
{
    /// <summary>
    /// Batches identical meshes into GPU instanced draw calls, used by the
    /// vegetation and prop scatter systems.
    /// </summary>
    public partial class GPUInstancingManager : MonoBehaviour // UC-T06-10: spatial submission remains in this resource owner.
    {
        // UC-T06-09: appearance/resource identity belongs to compatible batching; hashes alone are not equality.
        internal readonly struct BatchKey : System.IEquatable<BatchKey> // UC-T06-12: same key is retained by unpublished owner transactions.
        {
            internal readonly int owner, layer;
            internal readonly Mesh mesh;
            internal readonly Material material;
            private readonly Vector3Int cell; // UC-T06-10: absolute 32 m cells prevent chunk-wide culling bounds.
            private readonly int submesh;
            private readonly Vector2 lodRange;
            private readonly UnityEngine.Rendering.ShadowCastingMode shadows;
            private readonly bool receive;
            private readonly float shadowDistance, cullDistance;
            private readonly UniverseCreator.TerrainSystem.ShrubPrototypeDef appearance;
            private readonly int hash; // UC-T06-09: asset destruction cannot change a registered key's withdrawal hash.

            internal BatchKey(int owner, Mesh mesh, Material material, int layer, UnityEngine.Rendering.ShadowCastingMode shadows,
                bool receive, float shadowDistance, float cullDistance, UniverseCreator.TerrainSystem.ShrubPrototypeDef appearance,
                Vector3Int cell, int submesh, Vector2 lodRange)
            { this.owner = owner; this.mesh = mesh; this.material = material; this.layer = layer; this.shadows = shadows;
                this.receive = receive; this.shadowDistance = shadowDistance; this.cullDistance = cullDistance; this.appearance = appearance;
                this.cell = cell; this.submesh = submesh; this.lodRange = lodRange; // UC-T06-10
                unchecked { int value = BatchId(owner, mesh, material, layer); value = value * 31 + (int)shadows; value = value * 31 + (receive ? 1 : 0);
                    value = value * 31 + shadowDistance.GetHashCode(); value = value * 31 + cullDistance.GetHashCode();
                    value = value * 31 + cell.GetHashCode(); value = value * 31 + submesh; value = value * 31 + lodRange.GetHashCode(); // UC-T06-10
                    hash = value * 31 + (appearance != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(appearance) : 0); } }

            public bool Equals(BatchKey other) => owner == other.owner && layer == other.layer && ReferenceEquals(mesh, other.mesh) &&
                ReferenceEquals(material, other.material) && shadows == other.shadows && receive == other.receive &&
                shadowDistance.Equals(other.shadowDistance) && cullDistance.Equals(other.cullDistance) && ReferenceEquals(appearance, other.appearance) &&
                cell == other.cell && submesh == other.submesh && lodRange.Equals(other.lodRange); // UC-T06-10
            public override bool Equals(object other) => other is BatchKey key && Equals(key);
            public override int GetHashCode() => hash;
        }

        private static readonly int IdVegClass = Shader.PropertyToID("_VegetationClass"), IdResponse = Shader.PropertyToID("_WindResponse"),
            IdFlutter = Shader.PropertyToID("_Flutter"), IdPivot = Shader.PropertyToID("_PivotHeight"), IdTouch = Shader.PropertyToID("_TouchResponse");

        internal class InstanceBatch // UC-T06-12: staged records share the existing renderer representation.
        {
            public Mesh mesh;
            public Material material;
            public List<InstanceRecord> records = new List<InstanceRecord>(); // UC-T06-10: persistent linear transform/LOD data, never rebuilt in LateUpdate.
            public int submesh;
            public Vector2 lodRange;
            public int layer;
            public UnityEngine.Rendering.ShadowCastingMode shadowCastingMode;
            public bool receiveShadows;
            public UniverseCreator.TerrainSystem.ShrubPrototypeDef appearance; // UC-T06-09: borrowed shared authoring, never a material clone.
            public MaterialPropertyBlock properties;
            private Vector4 previousAppearance;
            private bool hasAppearance;

            internal void UpdateAppearance()
            {
                if (appearance == null) return;
                var current = new Vector4(appearance.windResponse, appearance.flutter, appearance.meshHeight, appearance.squeeze);
                if (!UniverseCreator.TerrainSystem.ScatterApplier.Finite(current.x) || !UniverseCreator.TerrainSystem.ScatterApplier.Finite(current.y) ||
                    !UniverseCreator.TerrainSystem.ScatterApplier.Finite(current.z) || current.z <= 0 || !UniverseCreator.TerrainSystem.ScatterApplier.Finite(current.w))
                    throw new System.ArgumentException("Instanced shrub appearance requires finite parameters and positive mesh height.");
                if (hasAppearance && current.x == previousAppearance.x && current.y == previousAppearance.y &&
                    current.z == previousAppearance.z && current.w == previousAppearance.w) return;
                previousAppearance = current; hasAppearance = true;
                properties.SetFloat(IdVegClass, (float)UniverseCreator.TerrainSystem.VegetationClass.Shrub);
                properties.SetFloat(IdResponse, Mathf.Max(0, current.x)); properties.SetFloat(IdFlutter, Mathf.Max(0, current.y));
                properties.SetFloat(IdPivot, current.z); properties.SetFloat(IdTouch, Mathf.Max(0, current.w));
            }

            // m. Beyond this the batch is submitted without shadows. Negative means
            // no limit. Per batch and not per instance: a batch is one species'
            // one mesh inside one chunk, so the whole batch is at much the same
            // distance, and one distance test per batch per frame is a cost that
            // does not scale with the instance count. (V-32)
            public float shadowDistanceM;

            // m. Beyond this the batch is not drawn at ALL (per-type view cull, §4O item 5),
            // measured to the batch bounds exactly as the shadow test above. Negative means
            // never cull, the default, so a world that authors nothing is unchanged. One
            // distance test per batch per frame, the same cost model as the shadow test.
            public float cullDistanceM;

            // PERF-1. The largest single instance in this batch, in metres: the mesh's own
            // biggest extent times the biggest instance scale seen. This is the FEATURE SIZE
            // the acuity law needs to answer "past what range is this prop sub-pixel" - and it
            // has to be the per-instance size, not the batch's worldBounds, because a batch of
            // 500 rocks spans a whole chunk while each rock is the thing the eye resolves.
            // Accumulated as instances arrive (chunk generation), never per frame.
            public float featureSizeM;

            // V-28. RenderMeshInstanced does not compute bounds; the legacy call did.            // So the batch accumulates its own as instances arrive - which costs nothing,
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

        // UC-T06-10: conservative slices support nonuniform transforms without relying on an assumeuniformscaling shader variant.
        private const int MaxInstancesPerBatch = 256;

        [Tooltip("PERF-1. The tier sampler whose quality tier decides the DEFAULT cull distance "
               + "for props that author none. Leave empty and nothing changes: no sampler resolves "
               + "to the Desktop tier, which culls nothing, exactly as today.")]
        public AdaptiveQuality qualitySource;

        /// <summary>
        /// PERF-1. The cull distance a batch actually uses this frame.
        ///
        /// THE GAP THIS CLOSES. A prop's cull distance comes from
        /// <c>EcoEnvelope.ResolveCullDistanceM()</c>, which returns -1 - never cull - when the
        /// author set none. So on a phone every instanced rock, shrub and prop in the world was
        /// drawn at any distance, while trees, grass and the basemap were already being culled
        /// by the acuity law (<c>GeometrySettings.DerivedTreeDrawDistanceM</c> and friends).
        /// Props were simply not wired to it.
        ///
        /// SO THIS IS NOT A NEW DISTANCE TABLE. A second per-tier table of metres would be a
        /// second source of truth competing with the acuity budget that already moves every
        /// other draw distance. It is the SAME law - feature size against the tier's tolerated
        /// pixel budget - finally applied to props.
        ///
        /// Precedence, in order:
        ///  1. An AUTHORED distance always wins. The author said "not past here" and a tier
        ///     default must never argue with that.
        ///  2. Desktop and flagship keep -1, byte for byte what they do today. The acuity budget
        ///     at those tiers is already one pixel, so there is no coarser tolerance to spend.
        ///  3. Coarser than desktop - the mobile tiers, which are the performance floor - derive
        ///     the cutoff from the prop's own size.
        ///
        /// FAILS SAFE, and this matters more than the saving: a mesh with no usable bounds gives
        /// a feature size of 0, and the law fails closed at 0 metres. Returned as-is that would
        /// mean "cull always", and every prop in the world would vanish. Unknown size therefore
        /// returns -1 (never cull): the cost of not culling is a slow frame, the cost of the
        /// other direction is an empty world.
        /// </summary>
        public static float EffectiveCullDistanceM(float authoredCullDistanceM,
                                                   float featureSizeM, float acuityPixels)
        {
            if (authoredCullDistanceM >= 0f) return authoredCullDistanceM;

            float desktopAcuity = UniverseCreator.TerrainSystem.Geometry.AcuityTier.Resolve(
                AdaptiveQuality.QualityTier.High);

            if (acuityPixels <= desktopAcuity) return -1f;
            if (featureSizeM <= 0f) return -1f;

            float derived = UniverseCreator.TerrainSystem.Geometry.AngularError
                .FeatureCullDistanceReference(featureSizeM, acuityPixels);

            return derived > 0f ? derived : -1f;
        }

        private readonly Dictionary<BatchKey, InstanceBatch> batches = new Dictionary<BatchKey, InstanceBatch>(); // UC-T06-09

        // Which batches each owner contributed to. Chunk scatter is the reason this
        // exists: a recycled chunk has to withdraw exactly its own instances, and
        // without an owner key the only options are leaking them or clearing the
        // whole world - one duplicates every prop on recentre, the other blanks
        // every chunk that was not part of the pass.
        private readonly Dictionary<int, List<BatchKey>> byOwner = new Dictionary<int, List<BatchKey>>(); // UC-T06-09

        // Reused across submits so a full world of instances does not allocate a
        // fresh array every frame.
        private readonly Matrix4x4[] submitBuffer = new Matrix4x4[MaxInstancesPerBatch];

        /// <summary>UC-T06-09: legacy lookup hash for an owner/mesh/material/layer; actual compatible batches compare their full tuple.</summary>
        /// <param name="owner">Runtime resource owner; zero denotes unowned instances.</param>
        /// <param name="mesh">Borrowed mesh; null contributes zero.</param>
        /// <param name="material">Borrowed material; null contributes zero.</param>
        /// <param name="layer">Rendering layer index.</param>
        /// <returns>Nonunique runtime hash; collisions do not imply equal batches.</returns>
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

        /// <summary>UC-T06-09: legacy lookup hash for unowned mesh/material/layer registration.</summary>
        /// <param name="mesh">Borrowed mesh; null contributes zero.</param>
        /// <param name="material">Borrowed material; null contributes zero.</param>
        /// <param name="layer">Rendering layer index.</param>
        /// <returns>Nonunique runtime hash with owner zero.</returns>
        public static int BatchId(Mesh mesh, Material material, int layer)
        {
            return BatchId(0, mesh, material, layer);
        }

        /// <summary>
        /// Registers one instance for batched rendering. Scatter systems call this
        /// per placed object; nothing is drawn until LateUpdate.
        /// </summary>
        /// <param name="mesh">Borrowed source mesh; null leaves registration unchanged.</param>
        /// <param name="material">Borrowed source material; null leaves registration unchanged.</param>
        /// <param name="matrix">Instance transform from mesh-local coordinates to world metres.</param>
        /// <param name="layer">Unity rendering layer index, normally 0..31.</param>
        /// <param name="shadowCasting">Authored Unity shadow policy.</param>
        /// <param name="receiveShadows">Whether the batch receives shadows.</param>
        /// <param name="shadowDistanceM">Shadow cutoff in metres; negative is unlimited.</param>
        /// <param name="cullDistanceM">Geometry cutoff in metres; negative uses the existing tier fallback.</param>
        /// <param name="shrubAppearance">Optional borrowed shrub parameters; finite inputs and positive mesh-local height are required.</param>
        /// <exception cref="System.ArgumentException">Appearance, transform, layer, bounds or distance inputs are invalid.</exception>
        public void AddInstance(Mesh mesh, Material material, Matrix4x4 matrix, int layer,
                                UnityEngine.Rendering.ShadowCastingMode shadowCasting =
                                    UnityEngine.Rendering.ShadowCastingMode.On,
                                bool receiveShadows = true,
                                float shadowDistanceM = -1f,
                                float cullDistanceM = -1f,
                                UniverseCreator.TerrainSystem.ShrubPrototypeDef shrubAppearance = null)
        {
            AddInstance(0, mesh, material, matrix, layer, shadowCasting, receiveShadows,
                        shadowDistanceM, cullDistanceM, shrubAppearance);
        }

        /// <summary>
        /// Registers one instance against an owner, so ClearOwner can withdraw it
        /// again without touching anything else.
        /// </summary>
        /// <param name="owner">Runtime resource owner; zero denotes unowned registration.</param>
        /// <param name="mesh">Borrowed source mesh; null leaves registration unchanged.</param>
        /// <param name="material">Borrowed source material; null leaves registration unchanged.</param>
        /// <param name="matrix">Instance transform from mesh-local coordinates to world metres.</param>
        /// <param name="layer">Unity rendering layer index, normally 0..31.</param>
        /// <param name="shadowCasting">Authored Unity shadow policy.</param>
        /// <param name="receiveShadows">Whether the batch receives shadows.</param>
        /// <param name="shadowDistanceM">Shadow cutoff in metres; negative is unlimited.</param>
        /// <param name="cullDistanceM">Geometry cutoff in metres; negative uses the existing tier fallback.</param>
        /// <param name="shrubAppearance">Optional borrowed shrub parameters; later edits update cached shader properties without changing matrices.</param>
        /// <exception cref="System.ArgumentException">Appearance, transform, layer, bounds or distance inputs are invalid.</exception>
        public void AddInstance(int owner, Mesh mesh, Material material, Matrix4x4 matrix, int layer,
                                UnityEngine.Rendering.ShadowCastingMode shadowCasting =
                                    UnityEngine.Rendering.ShadowCastingMode.On,
                                bool receiveShadows = true,
                                float shadowDistanceM = -1f,
                                float cullDistanceM = -1f,
                                UniverseCreator.TerrainSystem.ShrubPrototypeDef shrubAppearance = null)
        {
            // UC-T06-10: legacy callers remain an unfiltered LOD band; prefab templates use the same registration owner.
            RegisterInstance(owner, mesh, material, matrix, layer, shadowCasting, receiveShadows,
                shadowDistanceM, cullDistanceM, shrubAppearance, 0, Vector2.zero, Vector3.zero, 0f);
        }

        internal void RegisterInstance(int owner, Mesh mesh, Material material, Matrix4x4 matrix, int layer,
            UnityEngine.Rendering.ShadowCastingMode shadowCasting, bool receiveShadows, float shadowDistanceM,
            float cullDistanceM, UniverseCreator.TerrainSystem.ShrubPrototypeDef shrubAppearance,
            int submesh, Vector2 lodRange, Vector3 lodCentre, float lodSize)
        {
            if (mesh == null || material == null) return;
            ValidateRecord(matrix, layer, shadowDistanceM, cullDistanceM, mesh, submesh, lodRange, lodCentre, lodSize); // UC-T06-10
            var cell = SpatialCell(matrix);
            var id = new BatchKey(owner, mesh, material, layer, shadowCasting, receiveShadows, shadowDistanceM, cullDistanceM,
                shrubAppearance, cell, submesh, lodRange);

            // UC-T06-12: staging uses the same full owner/resource key without exposing records to RenderFrame.
            replacements.TryGetValue(owner, out var replacement);
            var targetBatches = replacement != null ? replacement.staged : batches;

            InstanceBatch batch;
            if (!targetBatches.TryGetValue(id, out batch))
            {
                batch = new InstanceBatch();
                batch.mesh = mesh;
                batch.material = material;
                batch.layer = layer;
                batch.shadowCastingMode = shadowCasting;
                batch.receiveShadows = receiveShadows;
                batch.shadowDistanceM = shadowDistanceM;
                batch.cullDistanceM = cullDistanceM;
                batch.submesh = submesh; batch.lodRange = lodRange; // UC-T06-10: material slots and authored LOD bands cannot merge.
                if (shrubAppearance != null)
                {
                    batch.appearance = shrubAppearance; batch.properties = new MaterialPropertyBlock();
                    batch.UpdateAppearance(); // UC-T06-09: validate/publish before registering the batch.
                }
                targetBatches[id] = batch;

                List<BatchKey> owned;
                if (replacement != null) owned = replacement.keys;
                else if (!byOwner.TryGetValue(owner, out owned))
                {
                    owned = new List<BatchKey>();
                    byOwner[owner] = owned;
                }
                owned.Add(id);
            }

            batch.records.Add(new InstanceRecord(matrix, lodCentre, lodSize)); // UC-T06-10
            batch.Include(in matrix, batch.mesh);

            // PERF-1. Track the biggest instance so the tier's acuity budget can answer
            // "past what range is one of these sub-pixel". Cheap and generation-time only.
            if (mesh != null)
            {
                Vector3 ms = mesh.bounds.size;
                Vector3 sc = matrix.lossyScale;

                float meshExtent = Mathf.Max(ms.x, Mathf.Max(ms.y, ms.z));
                float scale = Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));

                float size = meshExtent * scale;
                if (size > batch.featureSizeM) batch.featureSizeM = size;
            }
        }

        // UC-T06-09: explicit owner-scoped refresh reuses cached blocks and leaves matrices/native material assets unchanged.
        internal void RefreshAppearance(int owner)
        {
            if (!byOwner.TryGetValue(owner, out var owned)) return;
            foreach (var key in owned) if (batches.TryGetValue(key, out var batch)) batch.UpdateAppearance();
        }

        /// <summary>Drops every registered instance. Call when regenerating the world.</summary>
        public void ClearAll()
        {
            // UC-T06-12: an explicit global withdrawal also abandons unpublished records.
            foreach (var replacement in new List<OwnerReplacement>(replacements.Values)) replacement.Dispose();
            suspendedOwners.Clear();
            batches.Clear();
            byOwner.Clear();
        }

        /// <summary>Drops every batch one owner contributed, e.g. a recycled chunk.</summary>
        public void ClearOwner(int owner)
        {
            List<BatchKey> owned;
            if (!byOwner.TryGetValue(owner, out owned)) return;

            for (int i = 0; i < owned.Count; i++) batches.Remove(owned[i]);
            byOwner.Remove(owner);
        }

        /// <summary>UC-T06-09: drops all unowned batches matching the mesh/material/layer, including distinct appearance/shadow policies.</summary>
        /// <param name="mesh">Borrowed mesh identifying the batches to withdraw.</param>
        /// <param name="material">Borrowed material identifying the batches to withdraw.</param>
        /// <param name="layer">Rendering layer index; other layers and explicit owners are retained.</param>
        public void ClearBatch(Mesh mesh, Material material, int layer)
        {
            if (!byOwner.TryGetValue(0, out var owned)) return;
            for (int i = owned.Count - 1; i >= 0; i--)
                if (ReferenceEquals(owned[i].mesh, mesh) && ReferenceEquals(owned[i].material, material) && owned[i].layer == layer)
                { batches.Remove(owned[i]); owned.RemoveAt(i); }
            if (owned.Count == 0) byOwner.Remove(0);
        }

        /// <summary>UC-T06-10: resident renderer/submesh/LOD records across all owners; a plant can contribute multiple records.</summary>
        public int InstanceCount
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<BatchKey, InstanceBatch> kvp in batches)
                    total += kvp.Value.records.Count; // UC-T06-10: renderer/submesh/LOD records, not unique plants.
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
        private Camera ResolveViewer() // UC-T06-10: one explicit camera owns culling, LOD and its submitted draws.
        {
            if (targetCamera != null) return targetCamera;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.SceneView sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null)
                {
                    return sv.camera;
                }
            }
#endif
            if (cachedViewer == null) cachedViewer = Camera.main;
            return cachedViewer;
        }

        // LateUpdate rather than Update: scatter systems populate during Update, so
        // submitting later in the frame guarantees the batch is complete.
        private void LateUpdate()
        {
            RenderFrame(ResolveViewer(), true); // UC-T06-10: same culling/submesh/LOD plan is exercised by production fixtures.
        }
    }
}
