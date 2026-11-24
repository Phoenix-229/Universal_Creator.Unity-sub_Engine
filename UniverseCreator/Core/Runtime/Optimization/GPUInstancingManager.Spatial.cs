// UC-T06-10: bounded spatial groups, persistent linear records and allocation-free camera planning.
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.TerrainSystem;
using Geometry = UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Optimization
{
    public partial class GPUInstancingManager
    {
        private const float CellMetres = 32f;
        private readonly Plane[] viewPlanes = new Plane[6];
        private Camera cachedViewer;

        /// <summary>Camera owning batched rendering, LOD and culling; null resolves SceneView in edit mode or the cached main camera.</summary>
        [Tooltip("Camera owning batched rendering and LOD. Empty uses SceneView in edit mode or the main camera at runtime.")]
        public Camera targetCamera;

        /// <summary>Enables the GPU path; false makes new scatter commits use their bounded GameObject fallback.</summary>
        [Tooltip("Disable to apply new scatter as GameObjects. Reapply placement after changing this switch.")]
        public bool enableBatchedRendering = true;

        /// <summary>Finite nonnegative world-metre padding around spatial draw bounds; calibrate against actual shader deformation.</summary>
        [Min(0f), Tooltip("World metres added around mesh bounds for wind/deformation. Validate against your prototype's maximum motion.")]
        public float boundsPaddingMetres = 5f;

        /// <summary>True when this active manager and device can accept instanced placement; unsupported materials still use the prefab fallback.</summary>
        public bool CanAcceptInstances => isActiveAndEnabled && enableBatchedRendering && SystemInfo.supportsInstancing;

        internal readonly struct InstanceRecord // UC-T06-12: staged batches retain the existing immutable payload.
        {
            internal readonly Matrix4x4 matrix;
            internal readonly Vector3 lodCentre;
            internal readonly float lodSize;
            internal InstanceRecord(Matrix4x4 matrix, Vector3 centre, float size)
            { this.matrix = matrix; lodCentre = centre; lodSize = size; }
        }

        /// <summary>CPU-side statistics for one camera plan; GPU time, driver upload bytes and native allocations require a graphics/device capture.</summary>
        public readonly struct FrameStatistics
        {
            /// <summary>Resident renderer/submesh/LOD records examined; this is not a unique-plant count.</summary>
            public readonly int residentRecords;
            /// <summary>Selected records passed to submission, including shadow-only records; a dry plan predicts this count.</summary>
            public readonly int submittedRecords;
            /// <summary>Selected offscreen caster records; these must remain eligible for visible shadows.</summary>
            public readonly int shadowOnlyRecords;
            /// <summary>Instanced calls issued, or predicted by a dry plan; excludes shadow cascade expansion in the pipeline.</summary>
            public readonly int drawCalls;
            /// <summary>Records rejected by authored/tier geometry distance.</summary>
            public readonly int distanceRejectedRecords;
            /// <summary>Offscreen records rejected after shadow eligibility.</summary>
            public readonly int frustumRejectedRecords;
            /// <summary>Records rejected by their authored screen-height LOD band.</summary>
            public readonly int lodRejectedRecords;
            /// <summary>Records unavailable because the device, manager or borrowed render resources cannot submit them.</summary>
            public readonly int unavailableRecords;
            /// <summary>CPU planning/gather time in milliseconds, excluding graphics API calls; dry plans are not device benchmarks.</summary>
            public readonly double cullCpuMilliseconds;
            /// <summary>Resident record-array capacity times its 80-byte scalar payload; excludes object headers, assets and native memory.</summary>
            public readonly long residentPayloadBytes;
            /// <summary>64-byte matrices presented to submission; this is not a measurement of driver uploads.</summary>
            public readonly long submittedMatrixBytes;

            internal FrameStatistics(int resident, int submitted, int shadowOnly, int calls, int distance, int frustum,
                int lod, int unavailable, long ticks, long payload)
            { residentRecords = resident; submittedRecords = submitted; shadowOnlyRecords = shadowOnly; drawCalls = calls;
                distanceRejectedRecords = distance; frustumRejectedRecords = frustum; lodRejectedRecords = lod;
                unavailableRecords = unavailable; cullCpuMilliseconds = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                residentPayloadBytes = payload; submittedMatrixBytes = submitted * 64L; }
        }

        /// <summary>Most recent production or dry camera plan; cleared statistics are zero before the first plan.</summary>
        public FrameStatistics LastFrameStatistics { get; private set; }

        private static Vector3Int SpatialCell(Matrix4x4 matrix) => new Vector3Int(
            Mathf.FloorToInt(matrix.m03 / CellMetres), Mathf.FloorToInt(matrix.m13 / CellMetres), Mathf.FloorToInt(matrix.m23 / CellMetres));

        internal static void ValidateRecord(Matrix4x4 matrix, int layer, float shadowDistance, float cullDistance,
            Mesh mesh, int submesh, Vector2 band, Vector3 centre, float size)
        {
            for (int i = 0; i < 16; i++) if (!ScatterApplier.Finite(matrix[i])) throw new ArgumentException("Instanced transform must be finite.");
            if (layer < 0 || layer > 31 || !ScatterApplier.Finite(shadowDistance) || !ScatterApplier.Finite(cullDistance) ||
                submesh < 0 || submesh >= mesh.subMeshCount || !ScatterApplier.Finite(mesh.bounds.center) ||
                !ScatterApplier.Finite(mesh.bounds.extents) || !ScatterApplier.Finite(centre) || !ScatterApplier.Finite(size) || size < 0f ||
                !ScatterApplier.Finite(band.x) || !ScatterApplier.Finite(band.y) || band.x < 0f || band.x > 1f ||
                (size > 0f && band.y <= band.x) || Math.Abs((double)matrix.m03 / CellMetres) >= int.MaxValue ||
                Math.Abs((double)matrix.m13 / CellMetres) >= int.MaxValue || Math.Abs((double)matrix.m23 / CellMetres) >= int.MaxValue)
                throw new ArgumentException("Instanced bounds, submesh, layer, distances or LOD record are invalid.");
        }

        private readonly struct LodView // UC-T06-10: native camera/projection reads happen once per plan, not per plant/LOD record.
        {
            internal readonly bool hasCamera, orthographic;
            private readonly Vector3 position;
            private readonly float denominator, bias;
            internal LodView(Camera camera, float bias)
            {
                hasCamera = camera != null; orthographic = hasCamera && camera.orthographic; this.bias = bias;
                position = hasCamera ? camera.transform.position : Vector3.zero;
                denominator = !hasCamera ? 1f : orthographic ? 2f * camera.orthographicSize : 2f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            }
            internal float Height(Vector3 centre, float size) => !hasCamera ? float.MaxValue :
                size * bias / Mathf.Max(1e-5f, denominator * (orthographic ? 1f : Vector3.Distance(position, centre)));
        }

        internal static float ScreenHeight(Camera camera, Vector3 centre, float size, float bias) => new LodView(camera, bias).Height(centre, size);

        // A null camera deliberately plans only the highest authored LOD, rather than drawing every LOD on top of itself.
        private static bool SelectLod(InstanceBatch batch, InstanceRecord record, LodView view)
        {
            if (record.lodSize <= 0f) return true;
            if (!view.hasCamera) return batch.lodRange.y == float.MaxValue;
            float height = view.Height(record.lodCentre, record.lodSize);
            return height >= batch.lodRange.x && (batch.lodRange.y == float.MaxValue || height < batch.lodRange.y);
        }

        // Fixtures use submit=false to exercise actual planning without claiming GPU execution in headless Unity.
        internal FrameStatistics RenderFrame(Camera camera, bool submit)
        {
            if (!ScatterApplier.Finite(boundsPaddingMetres) || boundsPaddingMetres < 0f)
                throw new InvalidOperationException("Instanced bounds padding must be finite and nonnegative world metres.");
            long cameraStart = System.Diagnostics.Stopwatch.GetTimestamp(); // UC-T06-10: include camera/frustum preparation in measured CPU planning work.
            bool haveCamera = camera != null;
            Vector3 viewer = haveCamera ? camera.transform.position : Vector3.zero;
            if (haveCamera) GeometryUtility.CalculateFrustumPlanes(camera, viewPlanes);
            float bias = Mathf.Max(0.0001f, QualitySettings.lodBias);
            var lodView = new LodView(camera, bias);
            bool canSubmit = !submit || CanAcceptInstances;
            float acuity = Geometry.AcuityTier.Resolve(qualitySource);
            int resident = 0, submitted = 0, shadowOnly = 0, calls = 0, distanceRejected = 0, frustumRejected = 0, lodRejected = 0, unavailable = 0;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - cameraStart, payload = 0;
            foreach (var pair in batches)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                var batch = pair.Value;
                resident += batch.records.Count; payload += batch.records.Capacity * 80L;
                if (suspendedOwners.Contains(pair.Key.owner)) continue; // UC-T06-12: parked resources remain resident without submitting colour/shadows.
                if (batch.mesh == null || batch.material == null || batch.submesh >= batch.mesh.subMeshCount ||
                    (submit && (!canSubmit || !batch.material.enableInstancing)))
                { unavailable += batch.records.Count; ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start; continue; }

                Bounds padded = batch.worldBounds; padded.Expand(boundsPaddingMetres * 2f);
                float nearest = haveCamera ? Mathf.Sqrt(padded.SqrDistance(viewer)) : -1f;
                float cull = EffectiveCullDistanceM(batch.cullDistanceM, batch.featureSizeM, acuity);
                if (haveCamera && cull >= 0f && nearest > cull)
                { distanceRejected += batch.records.Count; ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start; continue; }
                float shadowDistance = batch.shadowDistanceM;
                if (shadowDistance > 0f && cull > 0f) shadowDistance = Geometry.ShadowLodBudget.EffectiveShadowCastDistanceM(shadowDistance, cull);
                ShadowCastingMode shadows = batch.shadowCastingMode;
                if (haveCamera && shadowDistance >= 0f && nearest > shadowDistance) shadows = ShadowCastingMode.Off;
                bool visible = !haveCamera || ((camera.cullingMask & (1 << batch.layer)) != 0 && OcclusionManager.TestFrustum(viewPlanes, batch.worldBounds, boundsPaddingMetres));
                if (!visible)
                {
                    // Camera frustum exclusion does not prove a caster cannot shadow a visible receiver. The pipeline owns light frusta.
                    if (shadows == ShadowCastingMode.Off || (haveCamera && (camera.cullingMask & (1 << batch.layer)) == 0))
                    { frustumRejected += batch.records.Count; ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start; continue; }
                    shadows = ShadowCastingMode.ShadowsOnly;
                }
                var parameters = new RenderParams(batch.material) { camera = camera, layer = batch.layer,
                    shadowCastingMode = shadows, receiveShadows = batch.receiveShadows, matProps = batch.properties,
                    lightProbeUsage = LightProbeUsage.BlendProbes, worldBounds = padded };
                int gathered = 0;
                for (int i = 0; i < batch.records.Count; i++)
                {
                    var record = batch.records[i];
                    if (!SelectLod(batch, record, lodView)) { lodRejected++; continue; }
                    submitBuffer[gathered++] = record.matrix;
                    if (gathered < MaxInstancesPerBatch && i + 1 < batch.records.Count) continue;
                    ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                    if (submit) Graphics.RenderMeshInstanced(parameters, batch.mesh, batch.submesh, submitBuffer, gathered);
                    submitted += gathered; calls++; if (shadows == ShadowCastingMode.ShadowsOnly) shadowOnly += gathered;
                    gathered = 0; start = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                if (gathered > 0)
                {
                    ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                    if (submit) Graphics.RenderMeshInstanced(parameters, batch.mesh, batch.submesh, submitBuffer, gathered);
                    submitted += gathered; calls++; if (shadows == ShadowCastingMode.ShadowsOnly) shadowOnly += gathered;
                    start = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            }
            LastFrameStatistics = new FrameStatistics(resident, submitted, shadowOnly, calls, distanceRejected, frustumRejected,
                lodRejected, unavailable, ticks, payload);
            if (submit) Geometry.GeometryComplexity.Report(submitted, calls);
            return LastFrameStatistics;
        }
    }
}
