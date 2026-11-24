// UC-T06-06: full identity, original-placement migration, persistence and captured production stamping.
using System;
using System.Linq;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationIdentity()
        {
            var tree = IdentityInstance("world-a", "trees:producer-a", "oak", 123.45f, -7.5f);
            var shrub = IdentityInstance("world-a", "shrubs:producer-a", "oak", 123.45f, -7.5f);
            var pine = IdentityInstance("world-a", "trees:producer-a", "pine", 123.45f, -7.5f);
            var foreign = IdentityInstance("world-b", "trees:producer-a", "oak", 123.45f, -7.5f);
            Check(tree.instanceId == shrub.instanceId && new[] { tree.identity, shrub.identity, pine.identity, foreign.identity }.Distinct().Count() == 4,
                "UC-T06-06 equal XZ with different worlds, layers or prototypes distinguishes full keys despite identical legacy IDs.");
            var zero = VegetationInstanceKey.Create("a", "b", "c", 0, 0);
            Check(zero.Equals(VegetationInstanceKey.Create("a", "b", "c", -0f, -0f)) &&
                !tree.identity.Equals(IdentityInstance("world-a", "trees:producer-a", "oak", 123.451f, -7.5f).identity),
                "UC-T06-06 signed zero is canonical while distinct sub-centimetre candidates no longer collapse to one identity.");
            bool rejected = false;
            try { VegetationInstanceKey.Create("a", "b", "c", float.NaN, 0); } catch (ArgumentException) { rejected = true; }
            Check(rejected && !default(VegetationInstanceKey).IsAssigned,
                "UC-T06-06 invalid coordinates fail explicitly and unstamped placement has no full key.");

            var store = new ScatterRemovalStore(); store.Remove(tree.identity);
            Check(store.IsRemoved(tree) && !store.IsRemoved(shrub) && !store.IsRemoved(pine) && !store.IsRemoved(foreign),
                "UC-T06-06 removing one exact plant cannot remove equal-XZ plants in another layer, species or world.");
            var collisionA = IdentityInstance("costarring", "layer", "prototype", 1, 2);
            var collisionB = IdentityInstance("liquid", "layer", "prototype", 1, 2);
            var collisionStore = new ScatterRemovalStore(); collisionStore.Remove(collisionA.identity);
            Check(collisionA.identity.GetHashCode() == collisionB.identity.GetHashCode() && !collisionA.identity.Equals(collisionB.identity) &&
                collisionStore.IsRemoved(collisionA) && !collisionStore.IsRemoved(collisionB),
                "UC-T06-06 an actual lookup-hash collision still compares full keys and cannot remove an unrelated instance.");
            var reload = JsonUtility.FromJson<ScatterRemovalStore>(JsonUtility.ToJson(store));
            Check(reload.Count == 1 && reload.IsRemoved(tree) && !reload.IsRemoved(pine) && reload.Restore(tree.identity) && reload.IsEmpty,
                "UC-T06-06 full tuple removal survives native JSON reload and exact restoration.");

            var unique = IdentityInstance("world-a", "trees:producer-a", "oak", 131.5f, 12);
            var legacy = new ScatterRemovalStore(); legacy.Remove(tree.instanceId); legacy.Remove(unique.instanceId); legacy.Remove(1729);
            var report = legacy.MigrateLegacyRemovals(new[] { tree, shrub, unique }, "world-a", true);
            Check(report.Migrated == 1 && report.Ambiguous == 1 && report.Unmatched == 1 && report.Invalid == 0 &&
                legacy.IsRemoved(unique) && legacy.IsRemoved(tree.instanceId) && legacy.IsRemoved(1729),
                "UC-T06-06 unique legacy edits migrate, colliding/unmatched edits remain serialized and counts explain each result.");
            rejected = false;
            try { legacy.IsRemoved(tree); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && legacy.ResolveLegacyRemoval(tree.instanceId, shrub.identity) && !legacy.IsRemoved(tree) && legacy.IsRemoved(shrub),
                "UC-T06-06 ambiguous legacy application reports an error until the author explicitly selects the intended instance.");
            var reverse = new ScatterRemovalStore(); reverse.Remove(tree.instanceId); reverse.Remove(unique.instanceId); reverse.Remove(1729);
            var reversedReport = reverse.MigrateLegacyRemovals(new[] { unique, shrub, tree }, "world-a", true);
            Check(reversedReport.Migrated == report.Migrated && reversedReport.Ambiguous == report.Ambiguous && reversedReport.Unmatched == report.Unmatched,
                "UC-T06-06 migration evidence is independent of original-placement enumeration order.");
            string before = JsonUtility.ToJson(reverse); rejected = false;
            try { reverse.MigrateLegacyRemovals(new[] { unique }, "world-a", false); } catch (ArgumentException) { rejected = true; }
            Check(rejected && JsonUtility.ToJson(reverse) == before,
                "UC-T06-06 incomplete/resident-only evidence cannot claim a unique legacy match or mutate saved edits.");
            rejected = false;
            try { reverse.MigrateLegacyRemovals(new[] { foreign }, "world-a", true); } catch (ArgumentException) { rejected = true; }
            Check(rejected && JsonUtility.ToJson(reverse) == before,
                "UC-T06-06 foreign-world migration is rejected without changing the store.");
            var unstamped = unique; unstamped.identity = default;
            var invalidStore = new ScatterRemovalStore(); invalidStore.Remove(tree.instanceId); invalidStore.Remove(unique.instanceId);
            string invalidBefore = JsonUtility.ToJson(invalidStore);
            var invalidReport = invalidStore.MigrateLegacyRemovals(new[] { tree, unstamped }, "world-a", true);
            Check(invalidReport.Invalid == 1 && invalidReport.Migrated == 0 && JsonUtility.ToJson(invalidStore) == invalidBefore,
                "UC-T06-06 an invalid matching identity prevents partial legacy migration.");
            TestCapturedVegetationIdentity();
        }

        private static ScatterInstance IdentityInstance(string world, string layer, string prototype, float x, float z)
        {
            return new ScatterInstance(new Vector3(x, 10, z), Quaternion.identity, Vector3.one, 0) {
                instanceId = ScatterNodeBase.StableInstanceId(x, z), identity = VegetationInstanceKey.Create(world, layer, prototype, x, z) };
        }

        [Serializable]
        private sealed class IdentityPlacementProducer : TreeScatterNode
        {
            protected override System.Collections.Generic.List<ScatterCandidate> SolvePlacement(Matrix height, Matrix mask, TileData tile, StopToken stop)
                => new System.Collections.Generic.List<ScatterCandidate> { new ScatterCandidate { worldX = 1, worldZ = 2, height = 0.25f },
                    new ScatterCandidate { worldX = 3, worldZ = 4, height = 0.25f } };
            protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile, System.Collections.Generic.List<ScatterInstance> instances, StopToken stop)
            { for (int i = 0; i < instances.Count; i++) { var instance = instances[i]; instance.prototypeIndex = i; instances[i] = instance; } }
        }

        private static void TestCapturedVegetationIdentity()
        {
            var root = new GameObject("UC captured identity acceptance"); root.SetActive(false);
            var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
            var graph = ScriptableObject.CreateInstance<Graph>();
            GenerationSnapshot first = null, reordered = null;
            TileData tile = null;
            try
            {
                world.scatterLibrary = new ScatterPrototypeLibrary();
                var oak = new TreePrototypeDef(); var pine = new TreePrototypeDef();
                world.scatterLibrary.trees.Add(oak); world.scatterLibrary.trees.Add(pine);
                world.scatterLibrary.objects.Add(new ObjectPrototypeDef());
                graph.SetDomain(UCGraphDomain.Vegetation);
                graph.nodes.Add(new UniverseCreator.Nodes.Creators.ConstantNode { id = "identity-ground", value = 0.25f });
                graph.nodes.Add(new IdentityPlacementProducer { id = "identity-producer" });
                graph.connections.Add(new Connection("identity-ground", 0, "identity-producer", 0));
                string authoringBefore = UnityEditor.EditorJsonUtility.ToJson(world);
                first = GenerationSnapshot.Capture(graph, world);
                Check(UnityEditor.EditorJsonUtility.ToJson(world) == authoringBefore,
                    "UC-T06-06 snapshot capture reads persistent identities without changing serialized world/prototype authoring.");
                tile = new TileData(new Coord(0, 0), new CoordRect(0, 0, 5, 5), Vector3.zero, new Vector3(16, 100, 16), 100);
                new GraphEvaluator(first.Graph).Evaluate(tile, new StopToken());
                var instances = tile.outputs.scatter["trees"];
                Check(instances.Count == 2 && instances.All(i => i.identity.IsAssigned) && !instances[0].identity.Equals(instances[1].identity) &&
                    !string.IsNullOrEmpty(oak.persistentId) && !string.IsNullOrEmpty(pine.persistentId),
                    "UC-T06-06 captured production scatter stamps final species with persisted world/prototype IDs even when ecology is off.");
                var captured = ScatterIdentitySnapshot.Capture(world);
                var copied = instances.ToList(); captured.Stamp("trees", "identity-producer", copied);
                Check(copied.SequenceEqual(instances), "UC-T06-06 unchanged captures retain identical candidate keys and legacy migration IDs.");
                var aquatic = new System.Collections.Generic.List<ScatterInstance> { instances[0] };
                var rock = new System.Collections.Generic.List<ScatterInstance> { instances[0] };
                captured.Stamp("underwater", "identity-producer", aquatic); captured.Stamp("rocks", "identity-producer", rock);
                Check(aquatic[0].identity.IsAssigned && rock[0].identity.IsAssigned && !aquatic[0].identity.Equals(rock[0].identity),
                    "UC-T06-06 aquatic and mixed-use rock channels use the actual object roster with separate layer keys.");
                bool duplicate = false;
                try { captured.Stamp("trees", "identity-producer", new System.Collections.Generic.List<ScatterInstance> { instances[0], instances[0] }); }
                catch (InvalidOperationException) { duplicate = true; }
                Check(duplicate, "UC-T06-06 duplicate full identities fail before a producer publishes ambiguous placement.");
                ulong hash = first.Graph.GetNodeById("identity-producer").contentHash;
                world.scatterLibrary.trees.Reverse(); reordered = GenerationSnapshot.Capture(graph, world);
                var reorderedIdentity = ScatterIdentitySnapshot.Capture(world);
                var movedSlot = instances[0]; movedSlot.prototypeIndex = 1;
                var list = new System.Collections.Generic.List<ScatterInstance> { movedSlot }; reorderedIdentity.Stamp("trees", "identity-producer", list);
                Check(list[0].identity.Equals(instances[0].identity) && hash != reordered.Graph.GetNodeById("identity-producer").contentHash,
                    "UC-T06-06 roster reordering preserves a prototype's identity while invalidating cached slot-based placement.");
                string serialized = JsonUtility.ToJson(world.scatterLibrary);
                var restored = JsonUtility.FromJson<ScatterPrototypeLibrary>(serialized);
                Check(restored.trees[1].persistentId == oak.persistentId && restored.trees[0].persistentId == pine.persistentId,
                    "UC-T06-06 persistent prototype identities survive native roster serialization.");
                oak.persistentId = null;
                string legacyBefore = UnityEditor.EditorJsonUtility.ToJson(world); bool missing = false;
                try { ScatterIdentitySnapshot.Capture(world); } catch (InvalidOperationException) { missing = true; }
                Check(missing && UnityEditor.EditorJsonUtility.ToJson(world) == legacyBefore &&
                    VegetationIdentityInitialization.Initialize(world) == 1 && VegetationIdentityInitialization.Initialize(world) == 0,
                    "UC-T06-06 missing legacy identity is diagnosed read-only; the explicit native initialization action changes only missing fields and is idempotent.");
                pine.persistentId = oak.persistentId; bool rejected = false;
                try { ScatterIdentitySnapshot.Capture(world); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "UC-T06-06 duplicated prototype identities report an error without silently replacing saved edit keys.");
            }
            catch (Exception error) { Check(false, "UC-T06-06 captured identity exception: " + error); }
            finally
            {
                tile?.ReleaseProductMemory(); first?.Release(); reordered?.Release(); // UC-T06-06: release the actual snapshot owner's installed lifetime API.
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
