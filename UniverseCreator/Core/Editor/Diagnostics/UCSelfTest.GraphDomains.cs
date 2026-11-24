// UC-T05-01: native/portable preservation, metadata admission and real preset creation.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestGraphDomainMetadata()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var source = new ConstantNode { id = "domain-source", value = 0.327f };
            var output = new HeightOutputNode { id = "domain-height", enforceSeaLevelFloor = false,
                enforceCoastalShelf = false, enforceContinentalRise = false };
            graph.nodes.Add(source); graph.nodes.Add(output); graph.connections.Add(new Connection(source.id, 0, output.id, 0));
            var transient = new List<Graph> { graph };
            string folder = "Assets/UCGraphDomains_" + Guid.NewGuid().ToString("N");
            try
            {
                Check(graph.Domain == UCGraphDomain.LegacyMixed && graph.DomainSchemaVersion == 0 && graph.HasSupportedDomainMetadata,
                    "UC-T05-01 an unstamped native graph retains schema-zero LegacyMixed without load-time migration.");
                AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                string originalSource = graph.nodes[0].id, originalTarget = graph.nodes[1].id;
                foreach (UCGraphDomain domain in Enum.GetValues(typeof(UCGraphDomain)))
                {
                    graph.SetDomain(domain);
                    Graph portable = GraphSerializer.FromJson(GraphSerializer.ToJson(graph)); transient.Add(portable);
                    Check(portable != null && portable.Domain == domain && portable.DomainSchemaVersion == Graph.CurrentDomainSchemaVersion &&
                        portable.nodes.Count == 2 && portable.nodes[0].id == originalSource && portable.nodes[1].id == originalTarget &&
                        portable.connections.Count == 1 && portable.connections[0].sourceNodeId == originalSource && portable.connections[0].targetNodeId == originalTarget,
                        "UC-T05-01 portable " + domain + " round-trip preserves schema, actual node types/IDs and ordered wires.");
                    Graph native = UnityEngine.Object.Instantiate(graph); transient.Add(native);
                    var saved = GraphAssetUtility.SaveAsset(native, folder, domain.ToString());
                    AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(saved), ImportAssetOptions.ForceUpdate);
                    var reloaded = AssetDatabase.LoadAssetAtPath<Graph>(AssetDatabase.GetAssetPath(saved));
                    Check(reloaded != null && reloaded.Domain == domain && reloaded.DomainSchemaVersion == Graph.CurrentDomainSchemaVersion &&
                        reloaded.nodes[0].GetType() == graph.nodes[0].GetType() && reloaded.nodes[0].id == originalSource &&
                        reloaded.connections[0].targetNodeId == originalTarget,
                        "UC-T05-01 native copy/save/reimport preserves " + domain + " metadata and authored node/wire identity.");
                }

                string current = GraphSerializer.ToJson(graph);
                for (int version = 1; version <= 2; version++)
                {
                    var legacy = GraphSerializer.FromJson(current.Replace("\"version\": " + GraphSerializer.FormatVersion, "\"version\": " + version));
                    transient.Add(legacy);
                    Check(legacy != null && legacy.Domain == UCGraphDomain.LegacyMixed && legacy.DomainSchemaVersion == 0 &&
                        legacy.nodes[0].id == originalSource && legacy.connections[0].targetNodeId == originalTarget,
                        "UC-T05-01 version " + version + " ignores newer domain hints and retains its original mixed semantics and wires.");
                }

                bool rejected = false; var before = graph.Domain;
                try { graph.SetDomain((UCGraphDomain)99); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && graph.Domain == before, "UC-T05-01 undeclared domain assignment fails before changing the asset.");
                graph.RestoreDomainMetadata((UCGraphDomain)97, 8);
                var future = GraphSerializer.FromJson(GraphSerializer.ToJson(graph)); transient.Add(future);
                Check(future != null && (int)future.Domain == 97 && future.DomainSchemaVersion == 8 && !future.HasSupportedDomainMetadata &&
                    future.nodes[0].id == originalSource && future.connections[0].targetNodeId == originalTarget &&
                    GraphValidationReport.Analyze(future).Diagnostics.Any(d => d.Code == "GRAPH_DOMAIN"),
                    "UC-T05-01 future domain metadata survives losslessly and is explicitly rejected before execution.");

                graph.SetDomain(UCGraphDomain.TerrainWater);
                string opaque = GraphSerializer.ToJson(graph).Replace(graph.nodes[0].GetType().FullName, "Missing.Plugin.DomainProbe");
                var missing = GraphSerializer.FromJson(opaque); transient.Add(missing);
                var twice = GraphSerializer.FromJson(GraphSerializer.ToJson(missing)); transient.Add(twice);
                Check(twice != null && twice.Domain == UCGraphDomain.TerrainWater && twice.nodes[0] == null &&
                    twice.unresolvedNodes.Count == 1 && twice.unresolvedNodes[0].json == missing.unresolvedNodes[0].json &&
                    twice.connections[0].sourceNodeId == originalSource,
                    "UC-T05-01 metadata round-trip never removes missing-plugin slots, opaque settings or their wires.");

                var captured = GenerationSnapshot.Capture(graph);
                try
                {
                    graph.SetDomain(UCGraphDomain.LegacyMixed);
                    Check(captured.Graph.Domain == UCGraphDomain.TerrainWater && captured.Graph.DomainSchemaVersion == Graph.CurrentDomainSchemaVersion &&
                        captured.Graph.nodes[0].id == originalSource && captured.Graph.connections[0].targetNodeId == originalTarget,
                        "UC-T05-01 the actual generation snapshot owns copied metadata unaffected by subsequent authoring changes.");
                }
                finally { captured.Release(); }

                graph.RestoreDomainMetadata(UCGraphDomain.LegacyMixed, 0);
                Undo.IncrementCurrentGroup(); int domainGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(graph, "Set graph domain"); graph.SetDomain(UCGraphDomain.TerrainWater);
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(domainGroup); Undo.IncrementCurrentGroup();
                Undo.PerformUndo();
                Check(graph.Domain == UCGraphDomain.LegacyMixed && graph.DomainSchemaVersion == 0 && graph.nodes[0].id == originalSource,
                    "UC-T05-01 native Undo restores legacy schema and node identity without pruning authored content.");
                Undo.PerformRedo();
                Check(graph.Domain == UCGraphDomain.TerrainWater && graph.DomainSchemaVersion == Graph.CurrentDomainSchemaVersion &&
                    graph.connections[0].targetNodeId == originalTarget,
                    "UC-T05-01 native Redo restores explicit metadata and retained wires.");

                var stable = ScriptableObject.CreateInstance<Graph>(); transient.Add(stable);
                var constant = new ConstantNode { id = "domain-constant", value = 0.327f }; stable.nodes.Add(constant);
                var tile = new TileData(default, new CoordRect(0, 0, 5, 5), Vector3.zero, new Vector3(4, 100, 4), 100);
                try
                {
                    var evaluator = new GraphEvaluator(stable);
                    Matrix original = evaluator.PullNode(constant, 0, tile, new StopToken()) as Matrix;
                    stable.SetDomain(UCGraphDomain.TerrainWater);
                    var nextTile = new TileData(default, tile.rect, tile.worldPos, tile.worldSize, tile.worldHeight);
                    try
                    {
                        Matrix changed = new GraphEvaluator(stable).PullNode(constant, 0, nextTile, new StopToken()) as Matrix;
                        Check(original != null && changed != null && original.arr.SequenceEqual(changed.arr),
                            "UC-T05-01 explicit main-domain metadata leaves actual generated matrix values unchanged.");
                    }
                    finally { nextTile.ReleaseProductMemory(); } // UC-T05-01: TileData releases through its existing ownership API.
                }
                finally { tile.ReleaseProductMemory(); } // UC-T05-01: no IDisposable contract exists on TileData.

                foreach (GraphTemplates.Template template in Enum.GetValues(typeof(GraphTemplates.Template)))
                {
                    var created = GraphTemplates.Create(template); transient.Add(created);
                    Check(created.Domain == UCGraphDomain.TerrainWater && created.DomainSchemaVersion == Graph.CurrentDomainSchemaVersion &&
                        !GraphAssetUtility.LooksLikeTextureGraph(created), "UC-T05-01 real main preset " + template + " carries explicit main metadata.");
                }
                foreach (TextureGraphTemplates.Template template in Enum.GetValues(typeof(TextureGraphTemplates.Template)))
                {
                    var created = TextureGraphTemplates.Create(template); transient.Add(created);
                    Check(created.Domain == UCGraphDomain.TextureMaterialShader && GraphAssetUtility.LooksLikeTextureGraph(created),
                        "UC-T05-01 real texture preset " + template + " carries explicit texture metadata.");
                }
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                foreach (var item in transient) if (item != null && !EditorUtility.IsPersistent(item)) UnityEngine.Object.DestroyImmediate(item);
            }
        }
    }
}
