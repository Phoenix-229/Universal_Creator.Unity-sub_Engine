// UC-T05-10: replacement assets are new native assets; one Undo transaction switches references only after validation.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    internal static class GraphMigrationExecutor
    {
        internal sealed class Result
        {
            internal string Folder;
            internal readonly Dictionary<UCGraphDomain, Graph> Assets = new Dictionary<UCGraphDomain, Graph>();
        }

        // Main thread, Edit mode only. A stale/read-only report is rejected before creating anything.
        // Failure removes only this transaction's uniquely owned new folder; legacy assets are never changed.
        internal static Result Apply(GraphMigrationPlan plan, string parentFolder, Action<int> afterAssetCreated = null)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Migrate graph assets in Edit mode.");
            if (plan == null || !plan.IsCurrent()) throw new InvalidOperationException("Re-analyze the current legacy asset and world references before migration.");
            if (string.IsNullOrEmpty(parentFolder) || !parentFolder.StartsWith("Assets/", StringComparison.Ordinal) ||
                parentFolder.Contains("..") || !AssetDatabase.IsValidFolder(parentFolder))
                throw new ArgumentException("Choose an existing project folder below Assets.", nameof(parentFolder));
            string path = AssetDatabase.GenerateUniqueAssetPath(parentFolder.TrimEnd('/') + "/UCGraphMigration");
            string folderGuid = AssetDatabase.CreateFolder(parentFolder.TrimEnd('/'), System.IO.Path.GetFileName(path));
            if (string.IsNullOrEmpty(folderGuid)) throw new InvalidOperationException("Unity could not create the unique migration folder.");
            var result = new Result { Folder = AssetDatabase.GUIDToAssetPath(folderGuid) };
            Undo.IncrementCurrentGroup(); int undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Migrate UC graph domains");
            try
            {
                foreach (var domain in GraphMigrationPlan.Domains)
                {
                    Graph replacement = UnityEngine.Object.Instantiate(plan.Source);
                    try
                    {
                        replacement.name = domain.ToString(); replacement.hideFlags = HideFlags.None;
                        replacement.nodes.RemoveAll(n => plan.Destinations[n.id] != domain);
                        var ids = new HashSet<string>(replacement.nodes.Select(n => n.id));
                        replacement.connections.RemoveAll(c => !ids.Contains(c.sourceNodeId) || !ids.Contains(c.targetNodeId));
                        replacement.SetDomain(domain);
                        // Exact settings/ID/topology preservation is checked before changing the planned main selectors.
                        foreach (var node in replacement.nodes)
                            if (EditorJsonUtility.ToJson(node) != EditorJsonUtility.ToJson(plan.Source.GetNodeById(node.id)))
                                throw new InvalidOperationException("Migration changed serialized settings on " + node.id + ".");
                        var expected = plan.Source.connections.Where(c => ids.Contains(c.sourceNodeId) && ids.Contains(c.targetNodeId));
                        if (!replacement.connections.SequenceEqual(expected)) throw new InvalidOperationException("Migration changed internal wire ordering.");
                        if (domain == UCGraphDomain.Vegetation && plan.AcceptedGroundConnections.Count > 0)
                        {
                            // UC-T06-02: retain every original node setting/ID; one typed input replaces only reviewed final-height crossings in authored wire order.
                            string inputId = "uc-migrated-accepted-ground";
                            for (int suffix = 1; plan.Destinations.ContainsKey(inputId); suffix++) inputId = "uc-migrated-accepted-ground-" + suffix;
                            replacement.nodes.Add(new WorldGraphInputNode { id = inputId, mainGraph = plan.Source });
                            replacement.connections.Clear();
                            foreach (var edge in plan.Source.connections)
                                if (plan.AcceptedGroundConnections.Contains(edge))
                                    replacement.connections.Add(new Connection(inputId, 0, edge.targetNodeId, edge.targetPortIndex));
                                else if (ids.Contains(edge.sourceNodeId) && ids.Contains(edge.targetNodeId)) replacement.connections.Add(edge);
                        }
                        GraphValidationReport.Analyze(replacement, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu,
                            requireOutput: false).ThrowIfInvalid(); // Empty authoring replacements remain valid; Run still requires an executable output.
                        AssetDatabase.CreateAsset(replacement, result.Folder + "/" + domain + ".asset");
                        result.Assets.Add(domain, replacement); replacement = null;
                    }
                    finally { if (replacement != null && !AssetDatabase.Contains(replacement)) UnityEngine.Object.DestroyImmediate(replacement); }
                    afterAssetCreated?.Invoke(result.Assets.Count); // Registered failure-injection fixture uses the same transaction path.
                }
                if (!plan.IsCurrent()) throw new InvalidOperationException("Source or world references changed during migration; replacements were not bound.");
                Graph main = result.Assets[UCGraphDomain.TerrainWater];
                foreach (var asset in result.Assets.Values)
                {
                    foreach (var node in asset.nodes)
                        if (node is WorldGraphInputNode input && input.mainGraph == plan.Source) input.mainGraph = main;
                    EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset);
                }
                Undo.RecordObject(plan.World, "Migrate UC main graph");
                var bindings = plan.World.GetComponent<UCWorldGraphBindings>();
                if (bindings == null) bindings = Undo.AddComponent<UCWorldGraphBindings>(plan.World.gameObject);
                Undo.RecordObject(bindings, "Migrate UC companion graphs");
                plan.World.graph = main;
                if (plan.ReplacesCompanion(UCGraphDomain.Vegetation)) bindings.VegetationGraph = result.Assets[UCGraphDomain.Vegetation];
                if (plan.ReplacesCompanion(UCGraphDomain.LightingClimateWeather)) bindings.LightingGraph = result.Assets[UCGraphDomain.LightingClimateWeather];
                if (plan.ReplacesCompanion(UCGraphDomain.TextureMaterialShader)) bindings.TextureGraph = result.Assets[UCGraphDomain.TextureMaterialShader];
                EditorUtility.SetDirty(plan.World); EditorUtility.SetDirty(bindings);
                if (plan.World.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(plan.World.gameObject.scene);
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undoGroup);
                plan.World.CancelGeneration(); // Retire frozen old authoring; already applied native output remains until an explicit Run.
                return result;
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                if (AssetDatabase.AssetPathToGUID(result.Folder) == folderGuid && !AssetDatabase.DeleteAsset(result.Folder))
                    UnityEngine.Debug.LogError("Migration rollback could not remove its newly created folder: " + result.Folder);
                throw;
            }
        }
    }
}
