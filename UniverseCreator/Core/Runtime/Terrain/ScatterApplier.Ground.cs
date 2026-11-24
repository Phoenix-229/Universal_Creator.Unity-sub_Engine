// UC-T06-07: final native-surface contact without changing procedural identity or authored XZ.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Worker-safe procedural contact: vertical offset/radius in metres and the attitude before surface inclination.</summary>
    [Serializable]
    public struct ScatterGroundBinding
    {
        /// <summary>True only for procedural placement that should follow accepted ground; default preserves manual transforms.</summary>
        public bool followsGround;
        /// <summary>Signed world-metre offset from the conformed worker surface, including authored embedding.</summary>
        public float offsetMetres;
        /// <summary>Nonnegative world-metre radius for the existing eight-sample footprint rule; zero uses centre contact.</summary>
        public float radiusMetres;
        /// <summary>Authored rotation with the worker surface's alignment removed, so reseating cannot compound tilt.</summary>
        public Quaternion unalignedRotation;
        /// <summary>Finite 0–1 process alignment; used when deposit shaping owns the resting attitude.</summary>
        public float processAlignment;
        /// <summary>True lets prototype alignment control the final tilt; false retains a deposit process's authored rule.</summary>
        public bool usesPrototypeAlignment;
        /// <summary>Land-only producer's water guard; aquatic producers retain their separate habitat contract.</summary>
        public bool excludesWater;
        /// <summary>Finite authored fallback still-water plane in world metres, used only without accepted water surfaces.</summary>
        public float fallbackWaterY;
        /// <summary>UC-T06-11: true requires final root contact inside the producer's aquatic depth band.</summary>
        public bool requiresAquaticWater;
        /// <summary>UC-T06-11: true prevents an originally observed aquatic contact from falling back to a legacy plane after water retirement.</summary>
        public bool requiresAcceptedWater;
        /// <summary>UC-T06-11: finite nonnegative minimum and ordered maximum root depth in metres; actual aquatic contact must be submerged, excluding waves.</summary>
        public Vector2 aquaticDepthRangeMetres;

        /// <summary>Captures the existing worker attitude and contact offsets for a later final-ground commit.</summary>
        /// <param name="offset">Signed vertical offset in world metres; finite.</param>
        /// <param name="radius">Finite nonnegative footprint radius in world metres.</param>
        /// <param name="rotation">Existing finite nonzero instance rotation.</param>
        /// <param name="normal">Existing finite upward surface normal.</param>
        /// <param name="alignment">Worker surface alignment, clamped to 0–1.</param>
        /// <param name="excludeWater">Whether this land-only producer excludes submerged final contact.</param>
        /// <param name="waterY">Finite authored fallback water surface in world metres.</param>
        /// <exception cref="ArgumentException">Contact or active water parameters are nonfinite or unusable.</exception>
        public ScatterGroundBinding(float offset, float radius, Quaternion rotation, Vector3 normal, float alignment, bool excludeWater = false, float waterY = 0f)
        {
            if (!ScatterApplier.Finite(offset) || !ScatterApplier.Finite(radius) || radius < 0f ||
                !ScatterApplier.ValidRotation(rotation) || !ScatterApplier.Finite(normal) || normal.y <= 0f || !ScatterApplier.Finite(alignment) || !ScatterApplier.Finite(waterY))
                throw new ArgumentException("Procedural ground contact requires finite offsets, radius, rotation and upward normal.");
            followsGround = true; offsetMetres = offset; radiusMetres = radius;
            processAlignment = Mathf.Clamp01(alignment); usesPrototypeAlignment = true;
            excludesWater = excludeWater; fallbackWaterY = waterY;
            requiresAquaticWater = false; requiresAcceptedWater = false; aquaticDepthRangeMetres = default; // UC-T06-11: explicitly initialize every field under the installed C# 9 contract.
            unalignedRotation = Quaternion.Inverse(Quaternion.Slerp(Quaternion.identity,
                Quaternion.FromToRotation(Vector3.up, normal), Mathf.Clamp01(alignment))) * rotation;
        }
    }

    public static partial class ScatterApplier
    {
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        internal static bool ValidRotation(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w) &&
            value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 1e-8f;

        // Main-thread, apply-scoped cache: at most the referenced roster's prefabs, with no retained Unity object roots.
        internal sealed partial class FinalGroundContext // UC-T06-08: contact and collision templates share one commit lifetime.
        {
            private readonly TerrainData data;
            private readonly Vector3 origin, size;
            private readonly WorldGraphWaterInput water;
            private readonly Dictionary<GameObject, float> baseY = new Dictionary<GameObject, float>();
            private readonly Terrain region; // UC-T06-12: cached once; scene resources retain exact world/region references across reload.
            private readonly UniverseCreatorObject resourceWorld;
            private readonly GenerationRunId resourceRun;
            internal int ManualGroundConflicts { get; private set; } // UC-T06-18: apply-scoped observations; never alter a saved pose.
            internal int ManualWaterConflicts { get; private set; }

            internal void ReportManualConflicts()
            {
                if (ManualGroundConflicts == 0 && ManualWaterConflicts == 0) return;
                Debug.LogWarning("[UC vegetation] Locked manual poses retained: " + ManualGroundConflicts +
                    " ground contacts differ by more than 0.25 m or are unavailable; " + ManualWaterConflicts +
                    " contacts are below accepted still water. Inspect and explicitly move/re-lock or remove these overrides; regeneration never reseats them.", resourceWorld);
            }

            internal FinalGroundContext(Terrain terrain, TerrainData committed, WorldGraphWaterInput acceptedWater = null, UniverseCreatorObject worldOwner = null)
            {
                data = committed; origin = terrain.GetPosition(); size = committed.size; water = acceptedWater;
                region = terrain; resourceWorld = worldOwner; resourceRun = worldOwner != null ? worldOwner.GenerationScheduler.CurrentRun : default;
            }

            internal void BindResource(GameObject instance, ScatterInstance placement)
            {
                var tag = instance.GetComponent<UCScatterRegionResource>();
                if (tag == null) tag = instance.AddComponent<UCScatterRegionResource>();
                tag.Bind(resourceWorld, region, resourceRun, placement.identity);
            }

            internal bool TrySurface(Vector3 position, out float y, out Vector3 normal)
            {
                y = 0f; normal = Vector3.up;
                if (!Finite(position) || !Finite(origin) || !Finite(size) || size.x <= 0f || size.y <= 0f || size.z <= 0f) return false;
                float u = (position.x - origin.x) / size.x, v = (position.z - origin.z) / size.z;
                if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
                int holes = data.holesResolution;
                if (holes > 0 && data.IsHole(Mathf.Min(holes - 1, (int)(u * holes)), Mathf.Min(holes - 1, (int)(v * holes)))) return false;
                y = origin.y + data.GetInterpolatedHeight(u, v);
                normal = data.GetInterpolatedNormal(u, v);
                return Finite(y) && Finite(normal) && normal.y > 0f;
            }

            internal bool Admits(Vector3 position, float minAltitude, float maxAltitude, float maxSlope)
            {
                if (!Finite(minAltitude) || !Finite(maxAltitude) || !Finite(maxSlope) || minAltitude > maxAltitude || maxSlope < 0f || maxSlope > 90f ||
                    !TrySurface(position, out float y, out Vector3 normal)) return false;
                float altitude = (y - origin.y) / size.y;
                return altitude >= minAltitude && altitude <= maxAltitude && Vector3.Angle(Vector3.up, normal) <= maxSlope;
            }

            internal bool TrySeat(ScatterInstance instance, GameObject prefab, Vector3 instanceScale, float alignment, float embedMetres,
                out Vector3 position, out Quaternion rotation)
            {
                position = instance.position; rotation = instance.rotation;
                if (!Finite(position) || !ValidRotation(rotation) || !Finite(instanceScale) ||
                    instanceScale.x <= 0f || instanceScale.y <= 0f || instanceScale.z <= 0f || !Finite(alignment) || !Finite(embedMetres) || embedMetres < 0f)
                    return false;
                if (instance.manualPose)
                {
                    // UC-T06-18: diagnose changed contact/water without inferring an automatic correction to explicit authoring.
                    if (prefab != null && !baseY.ContainsKey(prefab)) baseY.Add(prefab, MeasureRootBaseY(prefab));
                    float root = prefab != null ? baseY[prefab] : 0f;
                    float manualContact = position.y + (rotation * Vector3.Scale(new Vector3(0, root, 0),
                        prefab != null ? Vector3.Scale(prefab.transform.localScale, instanceScale) : instanceScale)).y;
                    if (!TrySurface(position, out float groundY, out _) || Mathf.Abs(manualContact - groundY) > 0.25f) ManualGroundConflicts++;
                    if (water != null && water.HasSurfaces && water.TryStillSurface(position.x, position.z, out float manualWaterY) && manualWaterY > manualContact + 0.02f) ManualWaterConflicts++;
                    return true;
                }
                if (!instance.ground.followsGround) return true; // Legacy/manual transforms retain explicit position and rotation.
                var binding = instance.ground;
                if (!Finite(binding.offsetMetres) || !Finite(binding.radiusMetres) || binding.radiusMetres < 0f ||
                    !ValidRotation(binding.unalignedRotation) || !TrySurface(position, out float y, out Vector3 normal)) return false;
                if (binding.excludesWater)
                {
                    if (!Finite(binding.fallbackWaterY)) return false;
                    bool wet = water != null && water.HasSurfaces
                        ? water.TryStillSurface(position.x, position.z, out float waterY) && waterY > y
                        : binding.fallbackWaterY > y;
                    if (wet) return false; // A re-cut cannot lower land-only placement into accepted water.
                }
                float strength = binding.usesPrototypeAlignment ? alignment : binding.processAlignment;
                if (!Finite(strength)) return false;
                rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, normal), Mathf.Clamp01(strength)) * binding.unalignedRotation;
                if (binding.radiusMetres > 0.001f)
                {
                    // Same ring as the worker owner; at a resident boundary use available committed edge samples.
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = i * Mathf.PI * 0.25f;
                        var probe = new Vector3(Mathf.Clamp(position.x + Mathf.Cos(angle) * binding.radiusMetres, origin.x, origin.x + size.x), position.y,
                            Mathf.Clamp(position.z + Mathf.Sin(angle) * binding.radiusMetres, origin.z, origin.z + size.z));
                        if (TrySurface(probe, out float sample, out _)) y = Mathf.Min(y, sample);
                    }
                }
                if (binding.requiresAquaticWater) // UC-T06-11: validate the final footprint ground, after conformance, against the captured aquatic depth band.
                {
                    Vector2 range = binding.aquaticDepthRangeMetres;
                    if (!Finite(range.x) || !Finite(range.y) || range.x < 0f || range.y <= 0f || range.y < range.x || !Finite(binding.fallbackWaterY)) return false;
                    float waterY = binding.fallbackWaterY;
                    if (binding.requiresAcceptedWater && (water == null || !water.HasSurfaces)) return false;
                    if (water != null && water.HasSurfaces && !water.TryStillSurface(position.x, position.z, out waterY)) return false;
                    float depth = waterY - y;
                    if (depth <= 0f || depth < range.x || depth > range.y) return false;
                }
                if (prefab != null && !baseY.ContainsKey(prefab)) baseY.Add(prefab, MeasureRootBaseY(prefab));
                float rootBase = prefab != null ? baseY[prefab] : 0f;
                Vector3 contact = rotation * Vector3.Scale(new Vector3(0f, rootBase, 0f),
                    prefab != null ? Vector3.Scale(prefab.transform.localScale, instanceScale) : instanceScale);
                position.y = y + binding.offsetMetres - embedMetres * instanceScale.y - contact.y;
                return Finite(position) && ValidRotation(rotation);
            }
        }

        // Prefab-local lower mesh bound: root scale is applied exactly once by the actual render/spawn transform.
        /// <summary>UC-T06-13: measures the lowest prefab-local mesh bound used by actual final-ground seating.</summary>
        /// <param name="prefab">Borrowed prototype with finite mesh bounds; root scale remains for the placement consumer to apply once.</param>
        /// <returns>Lowest local Y in metres, including child transforms; zero for a meshless prototype.</returns>
        /// <exception cref="InvalidOperationException">A transformed mesh bound has a nonfinite coordinate.</exception>
        internal static float MeasureRootBaseY(GameObject prefab)
        {
            float minY = float.PositiveInfinity;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || filter.sharedMesh == null) continue;
                Bounds bounds = filter.sharedMesh.bounds;
                Matrix4x4 matrix = prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    float y = matrix.MultiplyPoint3x4(corner).y;
                    if (!Finite(y)) throw new InvalidOperationException("Prototype mesh bounds contain a nonfinite contact coordinate.");
                    minY = Mathf.Min(minY, y);
                }
            }
            // Meshless prefabs use their explicitly authored pivot, not an invented model size.
            return Finite(minY) ? minY : 0f;
        }
    }
}
