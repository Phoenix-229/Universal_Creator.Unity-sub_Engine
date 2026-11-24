// UC-T04-08/09A: optional static-rock/current cues through the existing bounded ripple owner.
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Adds bounded surface ripple/foam cues where a collider intersects accepted moving water; existing hull/character emitters keep their own ownership.</summary>
    /// <remarks>Play-mode main thread only. This is a flow-separation cue, not a fluid solver or automatic obstacle-mask bake.</remarks>
    [DisallowMultipleComponent, RequireComponent(typeof(Collider))]
    [AddComponentMenu("Universe Creator/Water Obstacle")]
    public sealed class UCWaterObstacle : MonoBehaviour
    {
        [Tooltip("Maximum ripple/foam emission rate in events per second, 1…8. Static obstacles in still water emit nothing.")]
        [Range(1, 8)] public float eventsPerSecond = 4;
        [Tooltip("Maximum cue radius in metres, 0.05…10; also limited by the collider's plan footprint.")]
        [Range(0.05f, 10)] public float maxRadiusMetres = 2;
        /// <summary>UC-T04-09: optional world binding; empty infers the nearest parent, excluding other overlapping worlds.</summary>
        [Tooltip("Optional UC water world. Empty infers the nearest parent; bind external obstacles explicitly when worlds overlap.")]
        public UniverseCreatorObject waterWorld;
        private Collider obstacle;
        private Rigidbody motion;
        private bool hasMovementEmitter;
        private float nextEvent;

        private void OnEnable()
        {
            obstacle = GetComponent<Collider>(); motion = GetComponent<Rigidbody>();
            if (waterWorld == null) waterWorld = GetComponentInParent<UniverseCreatorObject>(true); // UC-T04-09: scope is captured outside emission loops.
            hasMovementEmitter = GetComponent<UCBuoyancy>() != null || GetComponent<UCWaterCharacter>() != null;
            nextEvent = 0;
        }

        private void OnDisable() { nextEvent = float.PositiveInfinity; }

        private void Update()
        {
            if (hasMovementEmitter || obstacle == null || !obstacle.enabled || Time.time < nextEvent) return;
            nextEvent = Time.time + 1 / (RiverChannelSnapshot.Finite(eventsPerSecond) ? Mathf.Clamp(eventsPerSecond, 1, 8) : 4);
            var sim = UCWaterRippleSim.Active;
            if (sim == null || sim.tier == UCWaterRippleSim.RippleTier.Off || sim.gameObject.scene != gameObject.scene) return;
            Bounds bounds = obstacle.bounds;
            Vector3 point = bounds.center; point.y = bounds.max.y;
            var sample = UCWaterVolume.Sample(point, Time.time, waterWorld);
            if (sample.volume == null || sample.surfaceY < bounds.min.y || sample.surfaceY > bounds.max.y || sample.waterDepth <= 0) return;
            Vector3 relative = (motion != null ? motion.GetPointVelocity(point) : Vector3.zero) - sample.velocity;
            relative.y = 0;
            float speed = relative.magnitude;
            if (speed < 0.15f) return;
            Vector2 downstream = new Vector2(sample.velocity.x, sample.velocity.z).normalized;
            float radius = Mathf.Clamp(Mathf.Min(bounds.extents.x, bounds.extents.z), 0.05f, Mathf.Clamp(maxRadiusMetres, 0.05f, 10));
            sim.Emit(new WaterImpulse
            {
                worldXZ = new Vector2(point.x, point.z) + downstream * radius,
                radius = radius, strength = -Mathf.Min(0.05f, speed * 0.01f),
                ringness = 0.25f, sharpness = 1.5f, foam = Mathf.Clamp01(speed * 0.2f)
            });
        }
    }
}
