// UC-T06-08: meaningful distribution/identity checks and actual spawned collider routing.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Inspectors;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationVariationAndCollision()
        {
            var variation = new ScatterPrototypeVariation { enabled = true, scaleRange = new Vector2(0.6f, 1.4f), yawOffsetDegrees = new Vector2(-45, 45) };
            variation.Validate();
            double uniformSum = 0, uniformSquared = 0, triangularSum = 0, triangularSquared = 0;
            bool bounded = true, repeated = true, unchanged = true;
            const int count = 4096;
            for (int i = 0; i < count; i++)
            {
                var original = IdentityInstance("variation-world", "objects:producer", "rock", i * 0.5f, -7);
                original.scale = Vector3.one * 2;
                original.ground = new ScatterGroundBinding(-0.02f, 0.25f, Quaternion.identity, Vector3.up, 0);
                variation.scaleDistribution = ScatterScaleDistribution.Uniform;
                var varied = variation.Apply(original); var again = variation.Apply(original);
                float u = (varied.scale.x / 2 - 0.6f) / 0.8f;
                uniformSum += u; uniformSquared += u * u;
                bounded &= varied.scale.x >= 1.2f && varied.scale.x <= 2.8f && Quaternion.Angle(Quaternion.identity, varied.rotation) <= 45.01f;
                repeated &= varied.scale == again.scale && varied.rotation == again.rotation;
                unchanged &= original.scale == Vector3.one * 2 && varied.position == original.position && varied.identity.Equals(original.identity) &&
                    Mathf.Abs(varied.ground.radiusMetres - 0.25f * varied.scale.x / 2) < 1e-6f && original.ground.radiusMetres == 0.25f;
                variation.scaleDistribution = ScatterScaleDistribution.Triangular;
                float t = (variation.Apply(original).scale.x / 2 - 0.6f) / 0.8f;
                triangularSum += t; triangularSquared += t * t;
            }
            double uniformMean = uniformSum / count, triangularMean = triangularSum / count;
            double uniformVariance = uniformSquared / count - uniformMean * uniformMean;
            double triangularVariance = triangularSquared / count - triangularMean * triangularMean;
            Check(bounded && repeated && unchanged, "UC-T06-08 variation is bounded and repeatable, preserves full keys/positions and scales the contact radius without mutating retained input.");
            Check(Math.Abs(uniformMean - 0.5) < 0.025 && Math.Abs(triangularMean - 0.5) < 0.025 &&
                Math.Abs(uniformVariance - 1.0 / 12) < 0.01 && Math.Abs(triangularVariance - 1.0 / 24) < 0.01,
                "UC-T06-08 known uniform/triangular distributions retain the expected mean/spread at matched sample counts.");
            var collisionA = IdentityInstance("costarring", "layer", "prototype", 1, 2);
            var collisionB = IdentityInstance("liquid", "layer", "prototype", 1, 2);
            Check(collisionA.identity.GetHashCode() == collisionB.identity.GetHashCode() && collisionA.identity.Variation01(8001) != collisionB.identity.Variation01(8001),
                "UC-T06-08 equal lookup hashes do not force unrelated full-world identities to share the same variation stream.");
            var reloaded = JsonUtility.FromJson<ScatterPrototypeVariation>(JsonUtility.ToJson(variation)); reloaded.Validate();
            Check(reloaded.Apply(collisionA).scale == variation.Apply(collisionA).scale && reloaded.Apply(collisionA).rotation == variation.Apply(collisionA).rotation,
                "UC-T06-08 saved variation settings survive native JSON reload without changing the chosen transform.");
            variation.enabled = false;
            Check(variation.Apply(collisionA).scale == collisionA.scale && variation.Apply(collisionA).rotation == collisionA.rotation,
                "UC-T06-08 disabled/missing legacy variation preserves existing node/species sizing and orientation.");
            variation.enabled = true; variation.scaleRange.x = float.NaN;
            bool rejected = false; try { variation.Validate(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "UC-T06-08 nonfinite variation ranges fail validation before native prototype or instance writes.");
            TestVegetationColliderParts();
            TestAppliedPrototypeVariation();
        }

        private static void TestVegetationColliderParts()
        {
            var root = new GameObject("UC collider parts acceptance"); root.transform.localScale = new Vector3(2, 3, 4);
            var leaves = new GameObject("Explicit visual part"); leaves.transform.SetParent(root.transform, false); leaves.SetActive(false);
            leaves.AddComponent<UCScatterColliderPart>().category = ScatterCollisionCategory.LeafCard; leaves.AddComponent<BoxCollider>();
            var branch = new GameObject("Explicit structural part"); branch.transform.SetParent(root.transform, false);
            branch.AddComponent<UCScatterColliderPart>().category = ScatterCollisionCategory.MajorBranch; branch.AddComponent<BoxCollider>();
            var data = new TerrainData();
            try
            {
                var shrub = root.AddComponent<UCShrubInstance>();
                var definition = new ShrubPrototypeDef { colliderRadius = 0.5f, colliderHeight = 2f };
                shrub.Configure(definition, 3, true);
                var capsule = root.GetComponent<CapsuleCollider>();
                Check(capsule.radius == 0.5f && capsule.height == 2 && capsule.center.y == 1,
                    "UC-T06-08 shrub trigger geometry stays prefab-local; the actual root transform applies instance scale once.");
                var issues = UCVegetationValidation.InspectFoliageCollision(root, out int total, out _, out _);
                Check(total == 3 && (issues & UCVegetationValidation.FoliageCollisionIssue.NonCollidingPart) != 0,
                    "UC-T06-08 read-only validation identifies explicit visual collision, including inactive children and structural parts.");
                var lib = new ScatterPrototypeLibrary(); lib.trees.Add(new TreePrototypeDef { prefab = root });
                bool rejected = false;
                try { ScatterApplier.ApplyPrototypes(data, lib); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && data.treePrototypes.Length == 0 && leaves.GetComponent<BoxCollider>() != null,
                    "UC-T06-08 native trees reject forbidden source collision before native writes and leave the buyer's prefab untouched.");
                int retained = ScatterApplier.ApplyColliderParts(root, Ecology.ColliderPolicy.Trigger);
                Check(retained == 2 && leaves.GetComponent<BoxCollider>() == null && branch.GetComponent<BoxCollider>().isTrigger && capsule.isTrigger,
                    "UC-T06-08 commit filtering keeps trunk/major-branch shapes, removes leaf cards and honors the trigger policy.");
                shrub.Configure(definition, 3, false);
                Check(root.GetComponentsInChildren<Collider>(true).Length == 0,
                    "UC-T06-08 a depleted/disabled shrub collider policy removes inherited inactive/child shapes as well as the generated trigger.");
                foreach (float badScale in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                {
                    bool invalid = false;
                    try { shrub.Configure(definition, badScale, true); } catch (ArgumentOutOfRangeException) { invalid = true; }
                    Check(invalid && root.GetComponentsInChildren<Collider>(true).Length == 0,
                        "UC-T06-08 invalid shrub scale is rejected before native writes: " + badScale);
                }
                definition.colliderRadius = float.NaN;
                bool invalidTrigger = false;
                try { shrub.Configure(definition, 1, true); } catch (ArgumentOutOfRangeException) { invalidTrigger = true; }
                shrub.Configure(definition, 1, false);
                Check(invalidTrigger && root.GetComponentsInChildren<Collider>(true).Length == 0,
                    "UC-T06-08 invalid active trigger dimensions fail before writes; inactive dimensions do not prevent collider-free rendering.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(data); }
        }

        private static void TestAppliedPrototypeVariation()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 40, 16) };
            var terrainRoot = Terrain.CreateTerrainGameObject(data);
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube); prefab.SetActive(false);
            var leaf = new GameObject("Visual child"); leaf.transform.SetParent(prefab.transform, false);
            leaf.AddComponent<UCScatterColliderPart>().category = ScatterCollisionCategory.LeafCard; leaf.AddComponent<BoxCollider>();
            var spawned = new List<GameObject>();
            try
            {
                var variation = new ScatterPrototypeVariation { enabled = true, scaleRange = new Vector2(0.8f, 1.2f), yawOffsetDegrees = new Vector2(-30, 30) };
                var lib = new ScatterPrototypeLibrary { maxObjectsPerChunk = 1 };
                var definition = new ObjectPrototypeDef { prefab = prefab, variation = variation, embedDepth = 0 };
                definition.eco.instancingPolicy = Ecology.InstancingPolicy.Never; definition.eco.colliderPolicy = Ecology.ColliderPolicy.Solid;
                lib.objects.Add(definition);
                var instance = IdentityInstance("variation-apply", "objects:producer", "rock", 8, 8);
                var outputs = new OutputBag(); outputs.AddScatter("objects", new List<ScatterInstance> { instance });
                variation.Validate(); var expected = variation.Apply(instance);
                ScatterApplier.Apply(terrainRoot.GetComponent<Terrain>(), data, outputs, lib, null, null, 8008, null, spawned);
                Check(spawned.Count == 1 && spawned[0].transform.localScale == expected.scale &&
                    Quaternion.Angle(spawned[0].transform.rotation, expected.rotation) < 0.01f &&
                    outputs.scatter["objects"][0].scale == instance.scale && outputs.scatter["objects"][0].identity.Equals(instance.identity),
                    "UC-T06-08 actual native object application consumes prototype variation once while preserving the accepted placement product.");
                Check(spawned[0].GetComponentsInChildren<Collider>(true).Length == 1 && prefab.GetComponentsInChildren<Collider>(true).Length == 2,
                    "UC-T06-08 cached prototype collision categories filter actual clones while preserving source shapes.");
                var ground = new ScatterApplier.FinalGroundContext(terrainRoot.GetComponent<Terrain>(), data);
                var clone = UnityEngine.Object.Instantiate(prefab); spawned.Add(clone);
                var extra = new GameObject("Runtime visual child"); extra.transform.SetParent(clone.transform, false);
                extra.AddComponent<UCScatterColliderPart>().category = ScatterCollisionCategory.Grass; extra.AddComponent<BoxCollider>();
                Check(ground.ApplyCollisionParts(prefab, clone, Ecology.ColliderPolicy.Solid) == 1 &&
                    clone.GetComponentsInChildren<Collider>(true).Length == 1,
                    "UC-T06-08 a changed clone hierarchy falls back to its actual explicit categories without retaining new grass collision.");
            }
            finally
            {
                ScatterApplier.Release(spawned, null, 8008);
                UnityEngine.Object.DestroyImmediate(prefab); UnityEngine.Object.DestroyImmediate(terrainRoot); UnityEngine.Object.DestroyImmediate(data);
            }
        }
    }
}
