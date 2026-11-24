// UC-T06-01: inventory completeness, source evidence, native references and unchanged authoring.
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationOwnershipInventory()
        {
            string folder = "Assets/UC_T06_Inventory_" + Guid.NewGuid().ToString("N");
            var root = new GameObject("UC vegetation inventory acceptance"); root.SetActive(false);
            var graph = ScriptableObject.CreateInstance<Graph>();
            var texture = new Texture2D(2, 2);
            try
            {
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
                graph.SetDomain(UCGraphDomain.Vegetation);
                graph.nodes.Add(new TreeScatterNode { id = "inventory-trees", seed = 412 });
                graph.nodes.Add(new UnderwaterScatterNode { id = "inventory-aquatic", seed = 614 });
                AssetDatabase.CreateAsset(graph, folder + "/Vegetation.asset");
                AssetDatabase.CreateAsset(texture, folder + "/DetailTexture.asset");
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.scatterLibrary = new ScatterPrototypeLibrary();
                world.scatterLibrary.trees.Add(new TreePrototypeDef { prefab = root });
                world.scatterLibrary.shrubs.Add(new ShrubPrototypeDef());
                world.scatterLibrary.details.Add(new DetailPrototypeDef { billboardTexture = texture });
                world.scatterLibrary.objects.Add(new ObjectPrototypeDef { prefab = root });
                string before = EditorJsonUtility.ToJson(world), graphBefore = GraphSerializer.ToJson(graph);
                bool dirty = EditorUtility.IsDirty(graph);
                var report = UCVegetationOwnershipInventory.Capture(world, new[] { folder });
                Check(report.Complete, "UC-T06-01 inventory reports every shipped owner/source without gaps: " + string.Join("; ", report.Diagnostics));
                Check(NodeCatalog.All.Where(e => e.category == NodeCategory.Scatter || GraphDomainRules.Supports(e.graphDomains, UCGraphDomain.Vegetation))
                    .All(e => report.Owners.Any(o => o.Type == e.type)), "UC-T06-01 actual catalog vegetation/shared nodes all have ownership destinations.");
                Check(report.Owners.All(o => o.Sources.Count > 0 && !string.IsNullOrEmpty(o.Destination) && !string.IsNullOrEmpty(o.Regression) &&
                    !string.IsNullOrEmpty(o.Decision) && o.Tasks.StartsWith("UC-T06-", StringComparison.Ordinal)),
                    "UC-T06-01 every owner has located declarations, a retained destination, a decision and a regression plan.");
                foreach (Type type in new[] { typeof(PoissonDisc), typeof(SpatialHash), typeof(ScatterFilter), typeof(UCShrubInstance),
                    typeof(ScatterInstance), typeof(ScatterRemovalStore), typeof(VegetationInstanceKey), typeof(ScatterIdentitySnapshot), // UC-T06-06: full key/capture owners join the retained migration inventory.
                    typeof(ScatterGroundBinding), // UC-T06-07: procedural contact survives worker/native ownership.
                    typeof(ScatterPrototypeVariation), typeof(ScatterScaleDistribution), typeof(UCScatterColliderPart), typeof(ScatterCollisionCategory), // UC-T06-08: variation/physical owners need located destinations.
                    typeof(ScatterApplier.AppearanceUpdate), // UC-T06-09: native appearance refresh retains exact producer/native ownership.
                    typeof(ScatterApplier), typeof(UCVegetationSystemManager) })
                    Check(report.Owners.Any(o => o.Type == type), "UC-T06-01 includes actual shared placement/consumer owner " + type.FullName);
                Check(report.Graphs.Count == 2 && report.Graphs.All(g => g.Asset == folder + "/Vegetation.asset" && g.Domain == UCGraphDomain.Vegetation) &&
                    report.Graphs.Any(g => g.NodeId == "inventory-aquatic"), "UC-T06-01 native graph use inventory preserves aquatic and tree identities/domains.");
                Check(report.References.Any(r => r.Property == "scatterLibrary.details.Array.data[0].billboardTexture" && r.Value == texture && r.Asset == folder + "/DetailTexture.asset") &&
                    report.References.Any(r => r.Property == "scatterLibrary.trees.Array.data[0].prefab" && r.Value == root && string.IsNullOrEmpty(r.Asset)) &&
                    report.References.Any(r => r.Property == "scatterLibrary.shrubs.Array.data[0].prefab" && r.Value == null),
                    "UC-T06-01 native serialization distinguishes saved assets, unsaved prototype references and empty buyer slots.");
                Check(EditorJsonUtility.ToJson(world) == before && GraphSerializer.ToJson(graph) == graphBefore && EditorUtility.IsDirty(graph) == dirty &&
                    AssetDatabase.FindAssets("t:Graph", new[] { folder }).Length == 1,
                    "UC-T06-01 inventory never resolves ecology, edits seeds/prototypes, changes dirty state or creates replacement graphs.");
                var repeated = UCVegetationOwnershipInventory.Capture(world, new[] { folder });
                Check(report.Owners.Select(o => o.Type).SequenceEqual(repeated.Owners.Select(o => o.Type)) &&
                    report.Owners.SelectMany(o => o.Sources).SequenceEqual(repeated.Owners.SelectMany(o => o.Sources)),
                    "UC-T06-01 repeated read-only inventory orders owners and source evidence deterministically.");
                Check(!UCVegetationOwnershipInventory.Capture(null, new[] { folder }).Complete,
                    "UC-T06-01 absent selected world leaves actual prototype coverage explicitly incomplete.");
                bool rejected = false;
                try { UCVegetationOwnershipInventory.Capture(world, new[] { "Packages" }); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "UC-T06-01 inventory rejects non-project asset roots before inspection.");
            }
            catch (Exception error) { Check(false, "UC-T06-01 native inventory acceptance exception: " + error); }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (graph != null) UnityEngine.Object.DestroyImmediate(graph);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
