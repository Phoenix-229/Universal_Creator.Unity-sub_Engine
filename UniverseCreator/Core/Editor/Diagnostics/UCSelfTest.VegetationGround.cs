// UC-T06-07: exercise the actual native contact owner with a nested, scaled, offset-pivot prototype.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationGroundContact()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 40, 32) };
            var terrainRoot = Terrain.CreateTerrainGameObject(data);
            var terrain = terrainRoot.GetComponent<Terrain>(); terrainRoot.transform.position = new Vector3(100, 5, -50);
            var prefab = new GameObject("UC ground contact prototype");
            var nested = new GameObject("Nested prototype transform"); nested.transform.SetParent(prefab.transform, false);
            var mesh = GameObject.CreatePrimitive(PrimitiveType.Cube); mesh.transform.SetParent(nested.transform, false);
            mesh.transform.localPosition = new Vector3(0, 2, 0); prefab.transform.localScale = new Vector3(1, 2, 1);
            prefab.SetActive(false);
            var spawned = new List<GameObject>();
            try
            {
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.25f;
                data.SetHeights(0, 0, heights);
                var instance = IdentityInstance("ground-world", "objects:ground", "prototype", 116, -34);
                instance.scale = Vector3.one * 2f;
                instance.ground = new ScatterGroundBinding(-0.05f, 0f, Quaternion.Euler(0, 37, 0), Vector3.up, 0f);
                var context = new ScatterApplier.FinalGroundContext(terrain, data);
                bool placed = context.TrySeat(instance, prefab, instance.scale, 0f, 0.1f, out Vector3 position, out Quaternion rotation);
                Check(placed && Mathf.Abs(position.y - 8.75f) < 0.001f && position.x == instance.position.x && position.z == instance.position.z,
                    "UC-T06-07 nested offset pivot and root/instance scales seat at final world-metre ground, preserving XZ and authored embedding.");
                Check(Quaternion.Angle(rotation, Quaternion.Euler(0, 37, 0)) < 0.01f && instance.identity.Equals(IdentityInstance("ground-world", "objects:ground", "prototype", 116, -34).identity),
                    "UC-T06-07 seating preserves yaw and the full procedural identity.");

                var lib = new ScatterPrototypeLibrary { maxObjectsPerChunk = 4 };
                lib.objects.Add(new ObjectPrototypeDef { prefab = prefab, embedDepth = 0.1f, normalAlignment = 0f });
                lib.objects[0].eco.instancingPolicy = Ecology.InstancingPolicy.Never;
                lib.objects[0].eco.colliderPolicy = Ecology.ColliderPolicy.Solid;
                var outputs = new OutputBag(); outputs.AddScatter("objects", new List<ScatterInstance> { instance });
                ScatterApplier.Apply(terrain, data, outputs, lib, null, null, 7007, null, spawned);
                if (spawned.Count == 1) spawned[0].SetActive(true); // Native bounds need an active fixture instance, independent of the inactive source prefab.
                Check(spawned.Count == 1 && Mathf.Abs(spawned[0].transform.position.y - position.y) < 0.001f &&
                    Mathf.Abs(spawned[0].GetComponentInChildren<BoxCollider>(true).bounds.min.y - 14.75f) < 0.005f,
                    "UC-T06-07 real spawned prototype/collider root contact matches the final native surface and intentional embed.");
                ScatterApplier.Release(spawned, null, 7007);

                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.4f + x * 0.005f;
                data.SetHeights(0, 0, heights); context = new ScatterApplier.FinalGroundContext(terrain, data);
                context.TrySurface(instance.position, out float finalY, out Vector3 normal);
                placed = context.TrySeat(instance, prefab, instance.scale, 1f, 0f, out position, out rotation);
                float contactY = (rotation * new Vector3(0, 6, 0)).y;
                Check(placed && Mathf.Abs(position.y + contactY - (finalY - 0.05f)) < 0.001f && Vector3.Angle(rotation * Vector3.up, normal) < 0.01f,
                    "UC-T06-07 terrain/plugin edits resample final height and normal, with permitted full slope alignment and correct pivot contact.");
                context.TrySeat(instance, prefab, instance.scale, 0.35f, 0f, out _, out Quaternion partial);
                float slope = Vector3.Angle(Vector3.up, normal);
                Check(Vector3.Angle(Vector3.up, partial * Vector3.up) < slope && Vector3.Angle(Vector3.up, partial * Vector3.up) > 0f &&
                    !context.Admits(instance.position, 0, 1, slope * 0.5f) && context.Admits(instance.position, 0, 1, 90),
                    "UC-T06-07 partial shrub alignment leans only within its rule and final-slope admission rejects steep sites.");
                context.TrySeat(instance, prefab, instance.scale, 1f, 0f, out Vector3 repeat, out Quaternion repeatRotation);
                Check((position - repeat).sqrMagnitude < 1e-8f && Quaternion.Angle(rotation, repeatRotation) < 0.01f,
                    "UC-T06-07 repeated contact cannot compound embedding or surface tilt.");
                var deposit = instance;
                deposit.ground = new ScatterGroundBinding(0f, 0f, Quaternion.Slerp(Quaternion.identity,
                    Quaternion.FromToRotation(Vector3.up, normal), 0.2f), normal, 0.2f);
                deposit.ground.usesPrototypeAlignment = false;
                context.TrySeat(deposit, prefab, deposit.scale, 1f, 0f, out _, out Quaternion depositRotation);
                Check(Mathf.Abs(Vector3.Angle(Vector3.up, depositRotation * Vector3.up) - slope * 0.2f) < 0.01f,
                    "UC-T06-07 deposit process alignment remains authoritative and is not doubled by prototype alignment.");

                var manual = instance; manual.ground = default;
                Check(context.TrySeat(manual, prefab, manual.scale, 1f, 0.1f, out Vector3 locked, out Quaternion lockedRotation) &&
                    locked == manual.position && lockedRotation == manual.rotation,
                    "UC-T06-07 unowned/manual transforms are not silently dragged by ground or prototype changes.");
                var invalid = instance; invalid.scale.y = float.NaN;
                Check(!context.TrySeat(invalid, prefab, invalid.scale, 1f, 0f, out _, out _) &&
                    !context.TrySurface(new Vector3(99, 0, -34), out _, out _),
                    "UC-T06-07 nonfinite scale and foreign terrain coordinates fail contact without a zero-height fallback.");
                var holes = new bool[data.holesResolution, data.holesResolution];
                for (int z = 0; z < data.holesResolution; z++) for (int x = 0; x < data.holesResolution; x++) holes[z, x] = true;
                holes[16, 16] = false; data.SetHoles(0, 0, holes);
                Check(!context.TrySeat(instance, prefab, instance.scale, 1f, 0f, out _, out _),
                    "UC-T06-07 a committed terrain opening has no plant contact, including aquatic/object channels.");
                bool rejected = false;
                try { new ScatterGroundBinding(float.NaN, 0f, Quaternion.identity, Vector3.up, 0f); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "UC-T06-07 invalid worker ground-binding data is explicitly rejected.");
                var land = instance; land.ground.excludesWater = true; land.ground.fallbackWaterY = finalY + 1;
                // Restore the centre's solid ground so water rejection is checked independently of holes.
                holes[16, 16] = true; data.SetHoles(0, 0, holes);
                Check(!context.TrySeat(land, prefab, land.scale, 0f, 0f, out _, out _) &&
                    context.TrySeat(instance, prefab, instance.scale, 0f, 0f, out _, out _),
                    "UC-T06-07 a final-ground re-cut rejects submerged land-only contact while the aquatic/object contact contract remains available.");
            }
            finally
            {
                ScatterApplier.Release(spawned, null, 7007);
                UnityEngine.Object.DestroyImmediate(terrainRoot); UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }
    }
}
