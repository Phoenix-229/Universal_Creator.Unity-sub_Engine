// UC-T08-11: bounded per-feature camera ownership and temporal keys, shared by existing URP passes.
#if UC_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>Main-thread owner of at most eight camera-specific disposable passes; active cameras never evict each other's native histories.</summary>
    /// <typeparam name="T">Existing render pass owning its own ping-pong buffers.</typeparam>
    internal sealed class LightingCameraPasses<T> : IDisposable where T : class, IDisposable
    {
        private readonly Camera[] cameras = new Camera[8];
        private readonly T[] passes = new T[8];
        private readonly Func<T> create;
        private readonly Func<bool> enabled;
        private bool disposed;
        /// <summary>Creates a bounded owner; the factory runs only for a newly admitted camera, never for every frame.</summary>
        internal LightingCameraPasses(Func<T> create, Func<bool> enabled = null)
        {
            this.create = create ?? throw new ArgumentNullException(nameof(create)); this.enabled = enabled;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            RenderPipelineManager.endContextRendering += OnContextEnded; // UC-T08-11: retire after rendering, even when a disabled feature no longer enqueues.
        }
        /// <summary>Returns a camera's existing pass or admits it to a vacant slot; null means the eight-camera budget is full.</summary>
        /// <param name="camera">Live camera identity, including scene-view cameras; null produces no pass.</param>
        internal T For(Camera camera)
        {
            if (disposed || camera == null) return null;
            RetireInactive();
            if (enabled != null && !enabled()) return null;
            for (int i = 0; i < cameras.Length; i++) if (cameras[i] == camera) return passes[i];
            for (int i = 0; i < cameras.Length; i++) if (passes[i] == null) { cameras[i] = camera; return passes[i] = create(); }
            return null;
        }
        private void OnSceneUnloaded(Scene scene) => Clear(); // Conservative reset also prevents histories crossing additive scene replacement.
        private void OnContextEnded(ScriptableRenderContext context, List<Camera> views) => RetireInactive();
        /// <summary>Releases disabled cameras/features after a render context; also callable by lifetime diagnostics without submitting GPU work.</summary>
        internal void RetireInactive()
        {
            if (disposed) return;
            if (enabled != null && !enabled()) { Clear(); return; }
            for (int i = 0; i < passes.Length; i++)
                if (passes[i] != null && (cameras[i] == null || !cameras[i].isActiveAndEnabled))
                { passes[i].Dispose(); passes[i] = null; cameras[i] = null; }
        }
        /// <summary>Retires all native pass histories while retaining the cache for later camera admission.</summary>
        internal void Clear()
        {
            for (int i = 0; i < passes.Length; i++) { passes[i]?.Dispose(); passes[i] = null; cameras[i] = null; }
        }
        /// <summary>Releases this owner's passes and scene callback, idempotently, on renderer recreation/disposal.</summary>
        public void Dispose() { if (disposed) return; disposed = true; SceneManager.sceneUnloaded -= OnSceneUnloaded; RenderPipelineManager.endContextRendering -= OnContextEnded; Clear(); }
    }

    /// <summary>Camera-local temporal validity key; resizing, cuts, projection/settings/world revisions and skipped frames reject stale samples.</summary>
    internal sealed class LightingHistoryKey
    {
        private Camera camera;
        private Transform parent;
        private UniverseCreatorObject world;
        private UCWorldGraphBindings bindings;
        private UCWeatherSystemManager weather;
        private UCLightingSystemManager lighting;
        private bool hadWorld;
        private bool hadLighting;
        private float nextOwnerDiscovery;
        private bool ready;
        private int lastFrame;
        private RenderTextureDescriptor previousDescriptor;
        private Matrix4x4 previousProjection;
        private Vector3 previousPosition;
        private Quaternion previousRotation;
        private Vector4 previousSettings;
        private long graphRevision, clockRevision;

        // UC-T08-12: reuse temporal ownership discovery for per-camera quality instead of changing a shared URP asset.
        private bool CaptureOwner(Camera view)
        {
            bool identity = camera != view || parent != view.transform.parent || hadWorld && world == null || hadLighting && lighting == null || world != null && !view.transform.IsChildOf(world.transform);
            if (identity || Time.realtimeSinceStartup >= nextOwnerDiscovery)
            {
                var currentWorld = view.GetComponentInParent<UniverseCreatorObject>(true);
                var currentBindings = currentWorld != null ? currentWorld.GetComponent<UCWorldGraphBindings>() : null;
                var currentWeather = UCWeatherSystemManager.FindForWorld(view.transform);
                var currentLighting = currentWorld != null ? UCWeatherSystemManager.FindOwnedEnvironmentManager<UCLightingSystemManager>(currentWorld.transform) : null;
                identity |= !ReferenceEquals(world, currentWorld) || !ReferenceEquals(bindings, currentBindings) ||
                    !ReferenceEquals(weather, currentWeather) || !ReferenceEquals(lighting, currentLighting);
                camera = view; parent = view.transform.parent; world = currentWorld;
                hadWorld = world != null;
                bindings = currentBindings; weather = currentWeather; lighting = currentLighting; hadLighting = lighting != null;
                nextOwnerDiscovery = Time.realtimeSinceStartup + 1; // Late-added/sibling-reparented managers are discovered at most once per second, without per-frame traversal.
            }
            return identity;
        }
        /// <summary>Resolves the existing camera-world lighting owner with cached hierarchy discovery; foreign/unbound views have no expensive effects.</summary>
        internal UCLightingSystemManager LightingFor(Camera view) { if (view == null) return null; if (CaptureOwner(view)) ready = false; return lighting; }

        /// <summary>Checks and captures one rendered view key without advancing clock or allocating native buffers.</summary>
        /// <param name="view">Current camera identity; reparent/destruction refresh immediately, dynamic owner additions within one real second.</param>
        /// <param name="descriptor">History dimensions, format, array depth and sample count.</param>
        /// <param name="projection">Non-jittered projection, so TAA jitter alone does not invalidate accumulation.</param>
        /// <param name="frame">Rendered Unity frame number; repeats and gaps invalidate history.</param>
        /// <param name="settings">The pass's four effective scalar settings; exact changes invalidate history.</param>
        /// <returns>True when the previous camera sample cannot be reused; 32 m / 45 degree cuts are conservative artistic thresholds.</returns>
        internal bool Changed(Camera view, in RenderTextureDescriptor descriptor, Matrix4x4 projection, int frame, Vector4 settings)
        {
            bool identity = CaptureOwner(view);
            long graph = bindings != null ? bindings.LightingRevision : 0, clock = weather != null ? weather.EnvironmentHistoryRevision : 0;
            bool changed = !ready || identity || frame != lastFrame + 1 || graph != graphRevision || clock != clockRevision ||
                descriptor.width != previousDescriptor.width || descriptor.height != previousDescriptor.height || descriptor.graphicsFormat != previousDescriptor.graphicsFormat ||
                descriptor.dimension != previousDescriptor.dimension || descriptor.volumeDepth != previousDescriptor.volumeDepth || descriptor.msaaSamples != previousDescriptor.msaaSamples ||
                projection != previousProjection || !settings.Equals(previousSettings) ||
                (view.transform.position - previousPosition).sqrMagnitude > 32 * 32 || Quaternion.Angle(view.transform.rotation, previousRotation) > 45;
            ready = true; lastFrame = frame; previousDescriptor = descriptor; previousProjection = projection;
            previousPosition = view.transform.position; previousRotation = view.transform.rotation; previousSettings = settings;
            graphRevision = graph; clockRevision = clock;
            return changed;
        }
    }
}
#endif
