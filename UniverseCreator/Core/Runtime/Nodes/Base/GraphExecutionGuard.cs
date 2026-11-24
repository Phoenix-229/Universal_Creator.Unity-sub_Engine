using System;
using System.Threading;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T02-05: enforces Unity thread and graphics readiness before graph execution or direct GPU helpers.</summary>
    public static class GraphExecutionGuard
    {
        private static int mainThread;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeRuntime() => Volatile.Write(ref mainThread, Thread.CurrentThread.ManagedThreadId);
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeEditor() => InitializeRuntime();
#endif
        /// <summary>True only on the thread registered by Unity's lifecycle hooks; worker synchronization contexts confer no permission.</summary>
        public static bool IsMainThread => Volatile.Read(ref mainThread) == Thread.CurrentThread.ManagedThreadId;
        /// <summary>True when called on Unity's main thread with an actual graphics device; false in headless execution.</summary>
        public static bool HasGraphicsDevice => IsMainThread && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
        /// <summary>Whether a synchronous readback may run here. Workers, headless processes and Editor repaints are excluded.</summary>
        public static bool CanReadbackSynchronously
        {
            get
            {
                if (!HasGraphicsDevice) return false;
#if UNITY_EDITOR
                if (Event.current != null && Event.current.type == EventType.Repaint) return false;
#endif
                return true;
            }
        }

        /// <summary>Rejects direct use of a Unity resource API outside the main thread before touching its native handle.</summary>
        /// <param name="operation">Operation name for the diagnostic.</param>
        /// <exception cref="InvalidOperationException">The caller is outside Unity's registered main thread.</exception>
        public static void RequireMainThread(string operation)
        {
            if (!IsMainThread) throw new InvalidOperationException(operation + " requires Unity's main thread.");
        }

        internal static void Require(Generator node, NodeExecutionDomain domain, bool cpuFallback)
        {
            if (domain == NodeExecutionDomain.Worker) return;
            if (!IsMainThread) RequireMainThread("Graph node '" + node.id + "' (" + domain + ")");
            if (domain == NodeExecutionDomain.MainThread) return;
            if (domain != NodeExecutionDomain.MainThreadGpu)
                throw new InvalidOperationException("Graph node '" + node.id + "' declares an unsupported execution domain.");
            if (!HasGraphicsDevice && !cpuFallback)
                throw new InvalidOperationException("Graph node '" + node.id + "' requires a graphics device and has no CPU fallback.");
        }
    }
}
