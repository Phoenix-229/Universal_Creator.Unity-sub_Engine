// UC-T06-08: opt-in prototype variation overlays existing node/species/allometry controls without reseeding placement.
using System;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Bounded scale distribution for prototype variation; neither option changes candidate positions or species selection.</summary>
    public enum ScatterScaleDistribution
    {
        /// <summary>Equal probability across the authored multiplier range.</summary>
        Uniform = 0,
        /// <summary>Average of two independent uniform draws; most sizes stay near the range midpoint.</summary>
        Triangular = 1
    }

    /// <summary>Saved, opt-in size/yaw variation, applied once from a full procedural key after existing species/allometry selection.</summary>
    [Serializable]
    public sealed class ScatterPrototypeVariation
    {
        /// <summary>False preserves existing variation; missing legacy settings also remain neutral.</summary>
        [Tooltip("Add stable prototype size/yaw variation after node and species sizing; never changes candidate positions.")]
        public bool enabled;
        /// <summary>Finite positive unitless scale multipliers; reversed endpoints are accepted and sorted.</summary>
        [Tooltip("Additional overall size multiplier range. Positive finite values; 1,1 keeps existing sizing.")]
        public Vector2 scaleRange = Vector2.one;
        /// <summary>Finite yaw offsets in degrees between -360 and 360; reversed endpoints are sorted.</summary>
        [Tooltip("Additional local yaw offset range in degrees, -360 to 360. 0,0 retains node/process heading.")]
        public Vector2 yawOffsetDegrees = Vector2.zero;
        /// <summary>Uniform or bounded triangular scale distribution; yaw uses an independent uniform draw.</summary>
        public ScatterScaleDistribution scaleDistribution;

        internal void Validate()
        {
            if (!enabled) return;
            if (!ScatterApplier.Finite(scaleRange.x) || !ScatterApplier.Finite(scaleRange.y) || scaleRange.x <= 0f || scaleRange.y <= 0f ||
                !ScatterApplier.Finite(yawOffsetDegrees.x) || !ScatterApplier.Finite(yawOffsetDegrees.y) ||
                Mathf.Abs(yawOffsetDegrees.x) > 360f || Mathf.Abs(yawOffsetDegrees.y) > 360f ||
                (scaleDistribution != ScatterScaleDistribution.Uniform && scaleDistribution != ScatterScaleDistribution.Triangular))
                throw new InvalidOperationException("Prototype variation needs positive finite scale multipliers, yaw offsets within -360..360 degrees and a supported distribution.");
        }

        // Settings are validated once before the instance loop; input/output are copies, preserving retained publications.
        internal ScatterInstance Apply(ScatterInstance instance)
        {
            if (!enabled) return instance;
            float draw = instance.identity.Variation01(8001);
            if (scaleDistribution == ScatterScaleDistribution.Triangular) draw = (draw + instance.identity.Variation01(8002)) * 0.5f;
            float factor = Mathf.Lerp(Mathf.Min(scaleRange.x, scaleRange.y), Mathf.Max(scaleRange.x, scaleRange.y), draw);
            float yaw = Mathf.Lerp(Mathf.Min(yawOffsetDegrees.x, yawOffsetDegrees.y), Mathf.Max(yawOffsetDegrees.x, yawOffsetDegrees.y), instance.identity.Variation01(8003));
            instance.scale *= factor;
            Quaternion turn = Quaternion.Euler(0, yaw, 0);
            instance.rotation *= turn;
            if (instance.ground.followsGround)
            {
                instance.ground.unalignedRotation *= turn;
                instance.ground.radiusMetres *= factor;
            }
            return instance;
        }
    }
}
