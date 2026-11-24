// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    GeometryComplexity.cs
//  Purpose: The live scene-complexity signal the thermal governor needs, published once per
//           frame by the one system that actually knows how much is being drawn.
//
//  WHY THIS EXISTS (closing the ThermalGovernor tri/draw feed)
//  -----------------------------------------------------------
//  ThermalGovernor detects SoC throttling as frame-time drift AT MATCHED COMPLEXITY: it only
//  trusts a frame-time rise as throttling when it is measured against an earlier reading at the
//  SAME workload. That requires a per-frame complexity number. Unity exposes no GPU triangle or
//  draw-call count at runtime outside the editor (UnityEditor.UnityStats is editor-only), so the
//  honest signal is the one the toolkit computes itself: the post-cull instanced workload the
//  GPUInstancingManager submits each frame - the variable part of the mobile draw cost. This is
//  NOT a GPU triangle count and does not claim to be; it is a cheap, deterministic, monotone
//  proxy for "how much is on screen", which is exactly what the governor's bucketing needs.
//
//  The producer (GPUInstancingManager.LateUpdate) reports its drawn instance total and its
//  instanced draw-call count here after submitting; the consumer (AdaptiveQuality's governor
//  loop) reads them and feeds both the GeometryBudget measurement and the ThermalGovernor
//  bucket. One writer, many readers, no cross-manager reference, allocation-free.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// The live instanced-geometry workload for this frame: the post-cull instance count and the
    /// instanced draw-call count the GPUInstancingManager submitted. Published by that manager each
    /// frame and read by AdaptiveQuality's governor loop as the ThermalGovernor's complexity bucket.
    /// Not a GPU triangle count (none is available at runtime) - a cheap deterministic workload
    /// proxy. Zero when nothing is drawn. Static, allocation-free.
    /// </summary>
    public static class GeometryComplexity
    {
        /// <summary>Post-cull instanced count submitted this frame - a workload proxy, not triangles.</summary>
        public static float InstancedUnits { get; private set; }

        /// <summary>Instanced draw calls submitted this frame (one per RenderMeshInstanced slice).</summary>
        public static float DrawCalls { get; private set; }

        /// <summary>Publish this frame's drawn workload. Called once per frame by the instancing
        /// manager after it submits; negatives are floored to zero so a reader never sees garbage.</summary>
        public static void Report(float instancedUnits, float drawCalls)
        {
            InstancedUnits = instancedUnits < 0f ? 0f : instancedUnits;
            DrawCalls = drawCalls < 0f ? 0f : drawCalls;
        }

        /// <summary>Clear the published workload (e.g. when instancing stops or on teardown), so a
        /// stale heavy reading cannot linger into an empty scene.</summary>
        public static void Reset()
        {
            InstancedUnits = 0f;
            DrawCalls = 0f;
        }
    }
}
