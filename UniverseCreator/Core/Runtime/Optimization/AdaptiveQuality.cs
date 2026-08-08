// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    AdaptiveQuality.cs
//  Purpose: Scales generation and render quality to hold a target frame rate on mobile.
//
//  Module 1 establishes the hooks. Modules 2-4 (vegetation, props, weather) consume
//  the quality tier to scale their own density and effect complexity.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Optimization
{
    /// <summary>
    /// Samples frame time and recommends a quality tier so vegetation, props, and
    /// effects can scale down on constrained hardware.
    /// </summary>
    public class AdaptiveQuality : MonoBehaviour
    {
        public enum QualityTier
        {
            Ultra = 0,
            High = 1,
            Medium = 2,
            Low = 3
        }

        [Tooltip("Target frame time in milliseconds (16.67ms = 60fps, 33.33ms = 30fps).")]
        public float targetFrameTime = 16.67f;

        [Tooltip("How many frames to average before adjusting quality.")]
        public int sampleWindow = 60;

        private float[] frameTimes;
        private int frameIndex;
        private QualityTier currentTier = QualityTier.High;

        /// <summary>Current recommended tier. Modules 2-4 read this to scale their work.</summary>
        public QualityTier CurrentTier { get { return currentTier; } }

        private void Start()
        {
            frameTimes = new float[sampleWindow];
        }

        private void Update()
        {
            if (frameTimes == null) return;

            frameTimes[frameIndex] = Time.unscaledDeltaTime * 1000f;
            frameIndex = (frameIndex + 1) % sampleWindow;

            if (frameIndex == 0) EvaluateTier();
        }

        private void EvaluateTier()
        {
            float avg = 0f;
            for (int i = 0; i < frameTimes.Length; i++) avg += frameTimes[i];
            avg /= frameTimes.Length;

            if (avg > targetFrameTime * 1.5f) currentTier = QualityTier.Low;
            else if (avg > targetFrameTime * 1.2f) currentTier = QualityTier.Medium;
            else if (avg > targetFrameTime * 1.05f) currentTier = QualityTier.High;
            else currentTier = QualityTier.Ultra;
        }
    }
}
