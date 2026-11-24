// UC-T06-18: saved manual edits are sparse world/key intent, composed with the accepted procedural result at staged application.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public partial class ScatterRemovalStore
    {
        [Serializable]
        private sealed class ManualIntent
        {
            public VegetationInstanceKey key;
            public string channel;
            public ScatterInstance placement;
            public Rect bounds;
            public float detailDensity;
            public int kind; // 0 locked pose; 1 exclusion; 2 native-detail density patch.
        }
        [SerializeField, HideInInspector] private List<ManualIntent> manualIntent = new List<ManualIntent>();
        /// <summary>Number of saved manual poses/exclusions/detail patches; independent of removed-instance count.</summary>
        public int ManualIntentCount => manualIntent?.Count ?? 0;

        // UC-T06-18: bounded copies for explicit Editor selection; no mutable serialized entries escape the owner.
        internal readonly struct ManualEdit
        {
            internal readonly VegetationInstanceKey Key;
            internal readonly string Channel;
            internal readonly ScatterInstance Pose;
            internal readonly Rect Bounds;
            internal readonly float Density;
            internal readonly int Kind;
            internal ManualEdit(VegetationInstanceKey key, string channel, ScatterInstance pose, Rect bounds, float density, int kind)
            { Key = key; Channel = channel; Pose = pose; Bounds = bounds; Density = density; Kind = kind; }
        }
        internal List<ManualEdit> CaptureManualEdits(string worldId)
        {
            var result = new List<ManualEdit>();
            if (manualIntent != null) foreach (var entry in manualIntent)
                if (entry != null && entry.key.World == worldId) result.Add(new ManualEdit(entry.key, entry.channel, entry.placement, entry.bounds, entry.detailDensity, entry.kind));
            return result;
        }

        /// <summary>Saves one exact manual pose with a stable prototype key; explicit removals remain authoritative until restored.</summary>
        /// <param name="key">Supported persistent world/layer/prototype key; use a unique authored layer ID for a new placement.</param>
        /// <param name="channel">trees, shrubs or objects; native details use SetManualDetailPatch.</param>
        /// <param name="pose">Finite world-metre position, normalized rotation and positive scale multipliers relative to the prototype; trees require upright yaw and equal X/Z scale.</param>
        /// <exception cref="ArgumentException">Key/category/pose is invalid.</exception>
        /// <exception cref="InvalidOperationException">The 4,096-edit persistence envelope is exceeded.</exception>
        /// <remarks>Owner thread between application passes. Regeneration never moves this pose; ground changes require manual inspection and explicit correction.</remarks>
        public void SetManualPlacement(VegetationInstanceKey key, string channel, ScatterInstance pose)
        {
            ValidateManualKey(key);
            if (channel != "trees" && channel != "shrubs" && channel != "objects") throw new ArgumentException("Manual poses support trees, shrubs and objects; use a detail patch for grass.");
            float rotationLength = pose.rotation.x * pose.rotation.x + pose.rotation.y * pose.rotation.y + pose.rotation.z * pose.rotation.z + pose.rotation.w * pose.rotation.w;
            if (!ScatterApplier.Finite(pose.position) || !ScatterApplier.Finite(pose.scale) || pose.scale.x <= 0 || pose.scale.y <= 0 || pose.scale.z <= 0 || !ScatterApplier.ValidRotation(pose.rotation) || Mathf.Abs(rotationLength - 1f) > 0.001f ||
                !ScatterApplier.Finite(pose.footprintRadius) || pose.footprintRadius < 0 || pose.footprintRadius > 64)
                throw new ArgumentException("Manual pose requires finite world metres, positive scale and a normalized rotation.");
            if (channel == "trees" && (Vector3.Angle(pose.rotation * Vector3.up, Vector3.up) > 0.01f || Mathf.Abs(pose.scale.x - pose.scale.z) > 0.0001f))
                throw new ArgumentException("Native tree poses require upright yaw and equal X/Z scale.");
            pose.identity = key; pose.manualPose = true; pose.ground = default; pose.targetHeightM = 0;
            float radius = Mathf.Max(0.01f, pose.footprintRadius);
            UpsertManual(new ManualIntent { key = key, channel = channel, placement = pose,
                bounds = new Rect(pose.position.x - radius, pose.position.z - radius, radius * 2, radius * 2) });
        }

        /// <summary>Saves a half-open world-metre rectangle that excludes procedural instances and grass, without deleting their source outputs.</summary>
        /// <param name="key">Unique persistent world edit key.</param>
        /// <param name="worldBounds">Finite positive X/Z rectangle; each extent is at most 128 m.</param>
        /// <param name="channel">Optional exact procedural channel; null/empty applies to all channels and native details.</param>
        /// <exception cref="ArgumentException">Key or rectangle is invalid.</exception>
        public void SetManualExclusion(VegetationInstanceKey key, Rect worldBounds, string channel = null)
        { ValidateManualKey(key); ValidateManualBounds(worldBounds); UpsertManual(new ManualIntent { key = key, kind = 1, bounds = worldBounds, channel = channel }); }

        /// <summary>Saves an authored grass patch, addressed by stable detail prototype identity and applied after procedural exclusions.</summary>
        /// <param name="key">Persistent key whose prototype component names the intended detail library slot.</param>
        /// <param name="worldBounds">Finite positive half-open X/Z rectangle in world metres, at most 128 m per extent.</param>
        /// <param name="densityPerSquareMetre">Finite density from zero to 64 blades/m²; final native/resource budgets still apply.</param>
        /// <exception cref="ArgumentException">Key, rectangle or density is invalid.</exception>
        public void SetManualDetailPatch(VegetationInstanceKey key, Rect worldBounds, float densityPerSquareMetre)
        {
            ValidateManualKey(key); ValidateManualBounds(worldBounds);
            if (!ScatterApplier.Finite(densityPerSquareMetre) || densityPerSquareMetre < 0 || densityPerSquareMetre > 64) throw new ArgumentException("Manual grass density must be finite, 0–64 blades/m².");
            UpsertManual(new ManualIntent { key = key, kind = 2, bounds = worldBounds, detailDensity = densityPerSquareMetre, channel = "grass" });
        }

        /// <summary>Removes only the addressed manual override, restoring procedural authority on the next explicit application.</summary>
        /// <param name="key">Exact saved edit identity.</param>
        /// <returns>True when an override existed; explicit removals and other worlds' edits remain unchanged.</returns>
        public bool ClearManualIntent(VegetationInstanceKey key)
        { if (manualIntent == null) return false; int index = manualIntent.FindIndex(item => item != null && item.key.Equals(key)); if (index < 0) return false; manualIntent.RemoveAt(index); return true; }

        private static void ValidateManualKey(VegetationInstanceKey key)
        { if (!key.IsAssigned) throw new ArgumentException("Manual intent requires a supported full world/layer/prototype key."); }
        private static void ValidateManualBounds(Rect bounds)
        { GraphVegetationPatch.ValidateBounds(bounds); if (bounds.width > 128 || bounds.height > 128) throw new ArgumentException("Use separate manual rectangles within the 128 m extent envelope."); }
        private void UpsertManual(ManualIntent entry)
        {
            if (manualIntent == null) manualIntent = new List<ManualIntent>();
            int index = manualIntent.FindIndex(item => item != null && item.key.Equals(entry.key));
            if (index >= 0) manualIntent[index] = entry;
            else { if (manualIntent.Count >= 4096) throw new InvalidOperationException("Manual intent exceeds its 4,096-edit envelope."); manualIntent.Add(entry); }
        }

        internal OutputBag ComposeManualIntent(OutputBag source, ScatterPrototypeLibrary library, string worldId, Rect region, int detailResolution)
        {
            if (ManualIntentCount == 0) return source;
            GraphVegetationPatch.ValidateBounds(region);
            var edits = new List<ManualIntent>(); var replacements = new HashSet<VegetationInstanceKey>();
            var seen = new HashSet<VegetationInstanceKey>();
            foreach (var edit in manualIntent)
            {
                if (edit == null) throw new InvalidOperationException("Saved manual intent contains an unresolved entry; repair it explicitly.");
                ValidateManualKey(edit.key);
                if (edit.kind < 0 || edit.kind > 2 || !seen.Add(edit.key)) throw new InvalidOperationException("Saved manual intent has an unsupported kind or duplicate full key; resolve it explicitly.");
                ValidateManualBounds(edit.bounds);
                if (edit.kind == 0 && (!ScatterApplier.Finite(edit.placement.position) || !ScatterApplier.Finite(edit.placement.scale) ||
                    edit.placement.scale.x <= 0 || edit.placement.scale.y <= 0 || edit.placement.scale.z <= 0 || !ScatterApplier.ValidRotation(edit.placement.rotation) ||
                    (edit.channel != "trees" && edit.channel != "shrubs" && edit.channel != "objects"))) throw new InvalidOperationException("Saved manual pose/category is invalid; repair its authored values explicitly.");
                if (edit.kind == 0)
                {
                    var pose = edit.placement;
                    float norm = pose.rotation.x * pose.rotation.x + pose.rotation.y * pose.rotation.y + pose.rotation.z * pose.rotation.z + pose.rotation.w * pose.rotation.w;
                    if (Mathf.Abs(norm - 1f) > 0.001f || !ScatterApplier.Finite(pose.footprintRadius) || pose.footprintRadius < 0 || pose.footprintRadius > 64 ||
                        (edit.channel == "trees" && (Vector3.Angle(pose.rotation * Vector3.up, Vector3.up) > 0.01f || Mathf.Abs(pose.scale.x - pose.scale.z) > 0.0001f)))
                        throw new InvalidOperationException("Saved manual pose has an invalid rotation, footprint or native tree scale; repair it explicitly."); // UC-T06-18: serialized files have the same boundary as authoring APIs.
                }
                if (edit.kind == 2 && (!ScatterApplier.Finite(edit.detailDensity) || edit.detailDensity < 0 || edit.detailDensity > 64)) throw new InvalidOperationException("Saved manual detail density is invalid.");
                if (edit.key.World != worldId) continue;
                if (edit.kind == 0) replacements.Add(edit.key);
                if (region.Overlaps(edit.bounds)) edits.Add(edit);
            }
            if (edits.Count > 128) throw new InvalidOperationException("Select a smaller region: more than 128 authored edits intersect this application.");
            int manualShrubs = 0, manualObjects = 0;
            foreach (var edit in edits) if (edit.kind == 0 && region.Contains(new Vector2(edit.placement.position.x, edit.placement.position.z)))
            { if (edit.channel == "shrubs") manualShrubs++; else if (edit.channel == "objects") manualObjects++; }
            if (manualShrubs > library.maxShrubsPerChunk || manualShrubs > library.shrubColliderBudget || manualObjects > library.maxObjectsPerChunk)
                throw new InvalidOperationException("The selected native/collider budget cannot retain its locked manual poses; increase the authored budget or reduce the region's overrides.");
            for (int i = 0; i < edits.Count; i++) for (int j = 0; j < i; j++)
                if (edits[i].kind == 2 && edits[j].kind == 2 && edits[i].key.Prototype == edits[j].key.Prototype && edits[i].bounds.Overlaps(edits[j].bounds) && edits[i].detailDensity != edits[j].detailDensity)
                    throw new InvalidOperationException("Overlapping manual grass patches specify conflicting densities; resolve the authored overlap explicitly.");
            var result = new OutputBag { RunId = source.RunId, zoneHistogram = source.zoneHistogram };
            int comparisons = 0;
            foreach (var channel in source.scatter)
            {
                var list = new List<ScatterInstance>(channel.Value?.Count ?? 0);
                if (channel.Value != null) foreach (var instance in channel.Value)
                {
                    if (replacements.Contains(instance.identity)) continue;
                    bool excluded = false;
                    foreach (var edit in edits)
                    {
                        if (++comparisons > 4000000) throw new InvalidOperationException("Manual exclusion application exceeds its bounded work envelope; use a smaller region.");
                        // UC-T06-18: an authored footprint excludes conflicting fill across categories, while explicit exclusions can select a channel.
                        if ((edit.kind == 0 || edit.kind == 1) && (edit.kind == 0 || string.IsNullOrEmpty(edit.channel) || edit.channel == channel.Key) && edit.bounds.Contains(new Vector2(instance.position.x, instance.position.z))) { excluded = true; break; }
                    }
                    if (!excluded) list.Add(instance);
                }
                result.scatter[channel.Key] = list;
            }
            foreach (var layer in source.detailLayers) result.detailLayers[layer.Key] = layer.Value;
            foreach (var edit in edits)
            {
                if (edit.kind == 0)
                {
                    if (!region.Contains(new Vector2(edit.placement.position.x, edit.placement.position.z))) continue; // UC-T06-18: one half-open region owns the pose while neighbours honor its exclusion footprint.
                    int slot = ResolveManualSlot(library, edit.channel, edit.key.Prototype);
                    if (slot < 0) throw new InvalidOperationException("A saved manual " + edit.channel + " prototype is missing; restore that persistent library entry or explicitly remove the override.");
                    GameObject prefab = edit.channel == "trees" ? library.trees[slot].prefab : edit.channel == "shrubs" ? library.shrubs[slot].prefab : library.objects[slot].prefab;
                    if (prefab == null) throw new InvalidOperationException("Assign usable content to the saved manual prototype before applying its pose.");
                    if (!result.scatter.TryGetValue(edit.channel, out var list)) result.scatter[edit.channel] = list = new List<ScatterInstance>();
                    var instance = edit.placement; instance.prototypeIndex = slot; instance.manualPose = true; instance.ground = default; list.Add(instance);
                }
            }
            // Explicit authored patches win over procedural exclusions, consistently independent of serialized edit order.
            foreach (var edit in edits)
            {
                if (edit.kind != 2) continue;
                int slot = ResolveManualSlot(library, "grass", edit.key.Prototype);
                if (slot < 0) throw new InvalidOperationException("A saved manual detail prototype is missing from the persistent roster.");
                if (library.details[slot].prefabMesh == null && library.details[slot].billboardTexture == null) throw new InvalidOperationException("Assign usable content to the saved manual detail prototype before applying its patch.");
                int nativeLayer = 0; for (int i = 0; i < slot; i++) if (library.details[i] != null && (library.details[i].prefabMesh != null || library.details[i].billboardTexture != null)) nativeLayer++;
                if (detailResolution <= 0 || (long)detailResolution * detailResolution > 4000000) throw new InvalidOperationException("Manual grass patches require an initialized native detail grid within the four-million-cell envelope.");
                // UC-T06-18: paint on the final native grid after procedural density scaling/resampling, so quality cannot erase authored grass.
                if (!result.detailLayers.ContainsKey(nativeLayer)) result.detailLayers[nativeLayer] = new int[detailResolution, detailResolution];
            }
            return result;
        }

        internal void ApplyManualDetails(int[,] grid, string worldId, string prototype, Rect region)
        {
            if (manualIntent == null) return;
            int work = 0, edits = 0;
            foreach (var edit in manualIntent)
            {
                if (edit.key.World != worldId || !edit.bounds.Overlaps(region)) continue;
                if (++edits > 128) throw new InvalidOperationException("Manual grass composition exceeds its 128-edit region envelope.");
                if (edit.kind == 0 || (edit.kind == 1 && (string.IsNullOrEmpty(edit.channel) || edit.channel == "grass")))
                    PaintManualDetail(grid, region, edit.bounds, 0, edit.key, ref work);
            }
            foreach (var edit in manualIntent)
                if (edit.key.World == worldId && edit.kind == 2 && edit.key.Prototype == prototype && edit.bounds.Overlaps(region))
                    PaintManualDetail(grid, region, edit.bounds, IsRemoved(new ScatterInstance { identity = edit.key }) ? 0 : edit.detailDensity, edit.key, ref work);
        }

        private static int ResolveManualSlot(ScatterPrototypeLibrary library, string channel, string prototype)
        {
            if (channel == "trees") return library.trees.FindIndex(item => item != null && item.persistentId == prototype);
            if (channel == "shrubs") return library.shrubs.FindIndex(item => item != null && item.persistentId == prototype);
            if (channel == "grass") return library.details.FindIndex(item => item != null && item.persistentId == prototype);
            return library.objects.FindIndex(item => item != null && item.persistentId == prototype);
        }
        private static void PaintManualDetail(int[,] grid, Rect region, Rect bounds, float density, VegetationInstanceKey key, ref int work)
        {
            int width = grid.GetLength(1), depth = grid.GetLength(0); if (width == 0 || depth == 0) return;
            float dx = region.width / width, dz = region.height / depth;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((bounds.xMin - region.xMin) / dx), 0, width), x1 = Mathf.Clamp(Mathf.CeilToInt((bounds.xMax - region.xMin) / dx), 0, width);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((bounds.yMin - region.yMin) / dz), 0, depth), z1 = Mathf.Clamp(Mathf.CeilToInt((bounds.yMax - region.yMin) / dz), 0, depth);
            for (int z = z0; z < z1; z++) for (int x = x0; x < x1; x++)
            {
                if (++work > 4000000) throw new InvalidOperationException("Manual detail painting exceeds its four-million-cell work envelope; use a smaller region."); // UC-T06-18: count actual visited cells across patches/exclusions.
                float wx = region.xMin + (x + 0.5f) * dx, wz = region.yMin + (z + 0.5f) * dz;
                if (!bounds.Contains(new Vector2(wx, wz))) continue;
                float exact = density * dx * dz; if (exact >= int.MaxValue - 1024f) throw new InvalidOperationException("Manual detail cell density exceeds native integer counts.");
                int count = Mathf.FloorToInt(exact); var positionKey = VegetationInstanceKey.Create(key.World, "manual-detail", key.Prototype, wx, wz);
                if (exact - count > positionKey.Variation01(18)) count++; grid[z, x] = count;
            }
        }
    }
}
