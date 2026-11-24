using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Utilities
{
    // UC-T01-11: one explicit repair owner; analysis never mutates authored data.
    [InitializeOnLoad]
    internal static class GraphRepairPreview
    {
        [Serializable]
        private sealed class UndoTarget
        {
            public string graphGuid;
            public string backupPath;
            public bool nativeMissing;
        }

        [Serializable]
        private sealed class UndoRecord
        {
            public List<UndoTarget> targets = new List<UndoTarget>();
        }

        private const string UndoPrefix = "Repair UC graphs ";

        static GraphRepairPreview()
        {
            // Unity's normal Undo omits hidden missing-type records. Native copies are
            // the authority for undoing this one explicit repair operation. SessionState
            // keeps only the small lookup across script reload, never the node payloads.
            Undo.undoRedoEvent += RestoreNativeUndo;
        }

        internal sealed class Entry
        {
            internal Graph graph;
            internal string signature;
            internal readonly List<string> details = new List<string>();
            internal int holes;
            internal int wires;
            internal bool nativeMissing;
            internal bool HasChanges => holes > 0 || nativeMissing;
        }

        /// <summary>Describes the exact missing slots/types and wires an explicit repair would discard.</summary>
        internal static Entry Analyze(Graph graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            Entry result = new Entry { graph = graph, signature = Signature(graph) };
            HashSet<string> live = new HashSet<string>();
            HashSet<Graph> originals = new HashSet<Graph>();
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                if (graph.nodes[i] != null) { live.Add(graph.nodes[i].id); continue; }
                result.holes++;
                string type = "native missing/empty node";
                foreach (UnresolvedNodeRecord record in graph.unresolvedNodes)
                    if (record.slot == i)
                    {
                        type = record.type ?? "empty slot";
                        // UC-T01-11: a native original's type list is authoritative;
                        // Unity's null slots cannot identify which missing type occupied each one.
                        if (record.nativeRecovery && record.nativeOriginal == null)
                            result.details.Add("Native recovery original is unavailable for slot " + i);
                        else if (record.nativeRecovery && originals.Add(record.nativeOriginal))
                        {
                            result.details.Add("Recovery original: " + AssetDatabase.GetAssetPath(record.nativeOriginal));
                            foreach (ManagedReferenceMissingType missing in SerializationUtility.GetManagedReferencesWithMissingTypes(record.nativeOriginal))
                                result.details.Add("Type retained in original: " + missing.namespaceName + "." + missing.className + " (" + missing.assemblyName + ")");
                        }
                        break;
                    }
                result.details.Add("Slot " + i + ": " + type);
            }
            foreach (ManagedReferenceMissingType missing in SerializationUtility.GetManagedReferencesWithMissingTypes(graph))
            {
                result.nativeMissing = true;
                result.details.Add("Native type: " + missing.namespaceName + "." + missing.className + " (" + missing.assemblyName + ")");
            }
            if (result.holes > 0)
                foreach (Connection wire in graph.connections)
                    if (!live.Contains(wire.sourceNodeId) || !live.Contains(wire.targetNodeId))
                    {
                        result.wires++;
                        result.details.Add("Wire: " + wire.sourceNodeId + ":" + wire.sourcePortIndex +
                                           " → " + wire.targetNodeId + ":" + wire.targetPortIndex);
                    }
            return result;
        }

        /// <summary>Applies explicitly selected, unchanged previews with Undo and native originals; no selection is a no-op.</summary>
        /// <remarks>Main thread. Only saved graph assets are accepted. Throws before mutation for stale previews or backup failure.</remarks>
        internal static List<string> ApplySelected(IList<Entry> entries)
        {
            List<string> backups = new List<string>();
            if (entries == null || entries.Count == 0) return backups;
            HashSet<Graph> seen = new HashSet<Graph>();
            List<Entry> selected = new List<Entry>();
            foreach (Entry entry in entries)
            {
                if (entry == null || entry.graph == null || !entry.HasChanges || !seen.Add(entry.graph)) continue;
                if (entry.signature != Signature(entry.graph))
                    throw new InvalidOperationException("Graph changed after preview; refresh the preview before repairing.");
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(entry.graph)))
                    throw new InvalidOperationException("Save the graph as an asset before repair so a native recovery original can be kept.");
                selected.Add(entry);
            }
            // Complete all native backups before any destructive change. Saving a dirty source
            // preserves the author's current values; copy the native bytes without reserializing them.
            foreach (Entry entry in selected)
            {
                backups.Add(entry.graph.CreateNativeRecoveryOriginal());
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            string undoName = UndoPrefix + Guid.NewGuid().ToString("N");
            Undo.SetCurrentGroupName(undoName);
            UndoRecord record = new UndoRecord();
            for (int i = 0; i < selected.Count; i++)
            {
                Entry entry = selected[i];
                record.targets.Add(new UndoTarget { graphGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.graph)), backupPath = backups[i], nativeMissing = entry.nativeMissing });
                Undo.RegisterCompleteObjectUndo(entry.graph, undoName);
                entry.graph.PruneMissingNodes();
                if (entry.nativeMissing) SerializationUtility.ClearAllManagedReferencesWithMissingTypes(entry.graph);
                EditorUtility.SetDirty(entry.graph);
            }
            SessionState.SetString(Application.dataPath + "/" + undoName, JsonUtility.ToJson(record));
            Undo.CollapseUndoOperations(group);
            foreach (Entry entry in selected) AssetDatabase.SaveAssetIfDirty(entry.graph);
            return backups;
        }

        private static void RestoreNativeUndo(in UndoRedoInfo info)
        {
            if (string.IsNullOrEmpty(info.undoName) || !info.undoName.StartsWith(UndoPrefix, StringComparison.Ordinal)) return;
            string json = SessionState.GetString(Application.dataPath + "/" + info.undoName, string.Empty);
            if (string.IsNullOrEmpty(json)) return;
            UndoRecord record = JsonUtility.FromJson<UndoRecord>(json);
            foreach (UndoTarget target in record.targets)
            {
                Graph graph = AssetDatabase.LoadAssetAtPath<Graph>(AssetDatabase.GUIDToAssetPath(target.graphGuid));
                Graph backup = AssetDatabase.LoadAssetAtPath<Graph>(target.backupPath);
                if (graph == null || backup == null)
                {
                    Debug.LogError("UC graph repair Undo could not find its recovery original: " + target.backupPath);
                    continue;
                }
                if (info.isRedo)
                {
                    graph.PruneMissingNodes();
                    SerializationUtility.ClearAllManagedReferencesWithMissingTypes(graph);
                }
                else
                {
                    string name = graph.name;
                    // UC-T01-11: CopySerialized restores known fields; missing fields
                    // retain their native original so a subsequent save remains lossless.
                    EditorUtility.CopySerialized(backup, graph);
                    if (target.nativeMissing) graph.PreserveNativeMissingSlots(backup);
                    graph.name = name;
                }
                EditorUtility.SetDirty(graph);
            }
        }

        private static string Signature(Graph graph)
        {
            StringBuilder signature = new StringBuilder(EditorJsonUtility.ToJson(graph));
            foreach (ManagedReferenceMissingType missing in SerializationUtility.GetManagedReferencesWithMissingTypes(graph))
                signature.Append(missing.referenceId).Append(missing.assemblyName).Append(missing.namespaceName)
                         .Append(missing.className).Append(missing.serializedData);
            return signature.ToString();
        }
    }

    // UC-T01-11: opening this window and cancelling it cannot repair anything.
    internal sealed class GraphRepairWindow : EditorWindow
    {
        private readonly List<GraphRepairPreview.Entry> entries = new List<GraphRepairPreview.Entry>();
        private Vector2 scroll;
        private string result;

        internal static void OpenSelected()
        {
            GraphRepairWindow window = GetWindow<GraphRepairWindow>("UC Graph Recovery");
            window.RefreshSelection();
            window.Show();
        }

        private void RefreshSelection()
        {
            entries.Clear();
            result = null;
            foreach (UnityEngine.Object item in Selection.objects)
                if (item is Graph graph) entries.Add(GraphRepairPreview.Analyze(graph));
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Select Graph assets in the Project window, then refresh. Restoring the original plugin is preferred; discard only data you no longer need.", MessageType.Info);
            if (GUILayout.Button("Refresh selected graphs")) RefreshSelection();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            bool changes = false;
            foreach (GraphRepairPreview.Entry entry in entries)
            {
                if (entry.graph == null) continue;
                EditorGUILayout.LabelField(entry.graph.name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(entry.holes + " missing slots; " + entry.wires + " affected wires");
                foreach (string detail in entry.details) EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedLabel);
                changes |= entry.HasChanges;
            }
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(result)) EditorGUILayout.HelpBox(result, MessageType.Info);
            using (new EditorGUI.DisabledScope(entries.Count == 0))
                if (GUILayout.Button("Retry restoring imported node types"))
                {
                    int restored = 0;
                    foreach (GraphRepairPreview.Entry entry in entries) restored += GraphSerializer.RestoreUnresolved(entry.graph);
                    RefreshSelection();
                    result = restored + " imported node(s) restored. Save the graphs to persist restoration.";
                }
            using (new EditorGUI.DisabledScope(!changes))
                if (GUILayout.Button("Discard missing data in these graphs…") &&
                    EditorUtility.DisplayDialog("Discard missing UC node data?",
                        "This removes the listed missing slots, their payloads and affected wires from the selected graphs. Native recovery copies will be saved beside the originals. Undo is available in this session.", "Create backups and repair", "Cancel"))
                {
                    try
                    {
                        List<string> backups = GraphRepairPreview.ApplySelected(entries);
                        RefreshSelection();
                        result = "Repair saved. Recovery originals:\n" + string.Join("\n", backups);
                    }
                    catch (Exception error) { result = error.Message; }
                }
        }
    }
}
