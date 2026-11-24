using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Combiners;

namespace UniverseCreator.Editor.Diagnostics
{
    // UC-T01-10: test-only type. The three-process native recovery gate temporarily
    // renames this declaration to RecoveryPluginUnavailable, then restores it.
    // It is in the Editor assembly, outside the runtime node catalog.
    /// <summary>UC-T01-10: Editor-only managed-reference fixture for real missing-type persistence tests.</summary>
    [Serializable]
    public sealed class RecoveryPluginProbe : Generator
    {
        /// <summary>Fixture payload used to prove nested values and persistent references survive missing types.</summary>
        public float scalar = 0.731f;
        /// <summary>Ordered fixture samples, with deliberately nonuniform values.</summary>
        public float[] samples = { 3.25f, -1.5f, 0.125f };
        /// <summary>Persistent test texture, created through AssetDatabase by the owning fixture.</summary>
        public Texture2D texture;
        /// <inheritdoc/>
        public override string NodeName => "Recovery test probe";
        /// <inheritdoc/>
        public override NodeCategory Category => NodeCategory.Creator;
        /// <inheritdoc/>
        public override Port[] GetInputs() => Array.Empty<Port>();
        /// <inheritdoc/>
        public override Port[] GetOutputs() => Array.Empty<Port>();
        /// <inheritdoc/>
        public override object Generate(int outputIndex, TileData tile, StopToken stopToken) => null;
    }

    public static partial class UCSelfTest
    {
        private const string NativeRecoveryFolder = "Assets/UCNativeRecoverySelfTest7";
        private const string NativeRecoveryBackup = NativeRecoveryFolder + "/Native Recovery/Native.asset";
        private const string ProbeTypeName = "UniverseCreator.Editor.Diagnostics.RecoveryPluginProbe";

        private static void TestGraphRecovery()
        {
            Graph original = ScriptableObject.CreateInstance<Graph>();
            List<Graph> transient = new List<Graph> { original };
            string folder = "Assets/UCRecoveryTest_" + Guid.NewGuid().ToString("N");
            try
            {
                AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                ConstantNode a = new ConstantNode { id = "a", value = 0.327f };
                ConstantNode b = new ConstantNode { id = "b", value = 0.718f };
                original.nodes.Add(a);
                original.nodes.Add(b);
                original.connections.Add(new Connection(a.id, 0, b.id, 7)); // Preserve even invalid wire data for repair.
                string json = GraphSerializer.ToJson(original);
                string missingType = "Missing.Plugin.UCRecoveryNode";
                string unknown = json.Replace(typeof(ConstantNode).FullName, missingType);
                Graph imported = GraphSerializer.FromJson(unknown);
                transient.Add(imported);
                Check(imported != null && imported.nodes.Count == 2 && imported.nodes[0] == null && imported.nodes[1] == null,
                    "UC-T01-10 unknown nodes preserve both original slots.");
                Check(imported.unresolvedNodes.Count == 2 && imported.connections.Count == 1,
                    "UC-T01-10 unknown nodes retain opaque payloads and incident wires.");
                string raw = imported.unresolvedNodes[0].json;
                Graph twice = GraphSerializer.FromJson(GraphSerializer.ToJson(imported));
                transient.Add(twice);
                Check(twice.unresolvedNodes[0].json == raw && twice.unresolvedNodes[0].type == missingType,
                    "UC-T01-10 unknown payload and type survive exact JSON round trip.");
                Check(twice.connections[0].sourceNodeId == "a" && twice.connections[0].targetPortIndex == 7,
                    "UC-T01-10 invalid authored wires are retained for diagnosis, not rewritten.");

                // Restoring the original type emulates a plugin becoming available to the JSON resolver.
                imported.unresolvedNodes[0].type = typeof(ConstantNode).FullName;
                Check(GraphSerializer.RestoreUnresolved(imported) == 1 && imported.nodes[0].id == "a" &&
                      ((ConstantNode)imported.nodes[0]).value == a.value,
                    "UC-T01-10 resolved type restores its exact slot, ID and value.");
                imported.RemoveNode("a");
                Check(imported.nodes.Count == 1 && imported.nodes[0] == null && imported.unresolvedNodes.Count == 1 &&
                      imported.unresolvedNodes[0].slot == 0 && imported.unresolvedNodes[0].json.Contains("b"),
                    "UC-T01-10 deleting a healthy neighbor preserves and reindexes the missing node.");
                string beforePaste = GraphSerializer.ToJson(original);
                Check(GraphSerializer.PasteJson(original, unknown).Count == 0 && GraphSerializer.ToJson(original) == beforePaste,
                    "UC-T01-10 failed unknown-type paste is atomic.");
                Check(!GraphSerializer.IsPasteableJson(unknown), "UC-T01-10 unavailable-type clipboard is not advertised as pasteable.");
                Check(!GraphSerializer.IsPasteableJson("{not JSON"), "UC-T01-10 malformed clipboard is rejected quietly.");
                Check(GraphSerializer.FromJson("{}") == null && GraphSerializer.FromJson("{\"version\":99,\"nodes\":[],\"connections\":[]}") == null,
                    "UC-T01-10 incomplete and future graph schemas are rejected without an asset.");
                Graph legacy = GraphSerializer.FromJson(json.Replace("\"version\": " + GraphSerializer.FormatVersion, "\"version\": 1")); // UC-T05-01: retain an actual version-1 fixture as the current container evolves.
                transient.Add(legacy);
                Check(legacy != null && ((ConstantNode)legacy.nodes[0]).value == a.value,
                    "UC-T01-10 supported version 1 values remain readable.");
                Graph invalidPayload = GraphSerializer.FromJson(unknown.Replace(missingType, "System.String"));
                transient.Add(invalidPayload);
                Check(invalidPayload.unresolvedNodes.Count == 2,
                    "UC-T01-10 a type outside Generator is preserved without being constructed.");

                // Native persistence of recovery records is independent of JSON round-tripping.
                string path = folder + "/Imported.asset";
                AssetDatabase.CreateAsset(twice, path);
                transient.Remove(twice);
                AssetDatabase.SaveAssetIfDirty(twice);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Graph saved = AssetDatabase.LoadAssetAtPath<Graph>(path);
                Check(saved.unresolvedNodes.Count == 2 && saved.unresolvedNodes[0].json == raw,
                    "UC-T01-10 recovery records survive native asset save/reimport.");
                GraphRepairPreview.Entry preview = GraphRepairPreview.Analyze(saved);
                Check(preview.holes == 2 && preview.wires == 1 && preview.details.Count == 3,
                    "UC-T01-11 preview enumerates exact discarded slots and affected wire.");
                string before = EditorJsonUtility.ToJson(saved);
                Check(GraphRepairPreview.ApplySelected(null).Count == 0 && EditorJsonUtility.ToJson(saved) == before,
                    "UC-T01-11 no selection leaves authored data untouched.");
                saved.nodes.Add(new ConstantNode());
                bool staleRejected = false;
                try { GraphRepairPreview.ApplySelected(new[] { preview }); }
                catch (InvalidOperationException) { staleRejected = true; }
                Check(staleRejected && saved.unresolvedNodes.Count == 2,
                    "UC-T01-11 stale preview refuses repair before payload removal.");
                saved.nodes.RemoveAt(saved.nodes.Count - 1);
                List<string> backups = GraphRepairPreview.ApplySelected(new[] { GraphRepairPreview.Analyze(saved) });
                Check(backups.Count == 1 && saved.nodes.Count == 0 && saved.connections.Count == 0 && saved.unresolvedNodes.Count == 0,
                    "UC-T01-11 explicit selected repair clears only reviewed missing data.");
                Graph backup = AssetDatabase.LoadAssetAtPath<Graph>(backups[0]);
                Check(backup.nodes.Count == 2 && backup.unresolvedNodes[0].json == raw && backup.connections.Count == 1,
                    "UC-T01-11 native recovery original retains payloads and wires.");
                Check(GraphSerializer.ToJson(original) == beforePaste,
                    "UC-T01-11 selected repair does not mutate an unselected graph.");
                Undo.PerformUndo();
                Check(saved.nodes.Count == 2 && saved.unresolvedNodes.Count == 2 && saved.connections.Count == 1,
                    "UC-T01-11 Undo restores missing slots, opaque payloads and wires.");
                AssetDatabase.SaveAssetIfDirty(saved);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Check(AssetDatabase.LoadAssetAtPath<Graph>(path).unresolvedNodes.Count == 2,
                    "UC-T01-11 repaired-then-undone data survives save/reimport.");
                Graph unavailableOriginal = ScriptableObject.CreateInstance<Graph>();
                transient.Add(unavailableOriginal);
                unavailableOriginal.nodes.Add(null);
                unavailableOriginal.unresolvedNodes.Add(new UnresolvedNodeRecord { slot = 0, nativeRecovery = true });
                Check(GraphSerializer.ToJson(unavailableOriginal) == null &&
                      GraphSerializer.RestoreUnresolved(unavailableOriginal) == 0 && unavailableOriginal.unresolvedNodes.Count == 1,
                    "UC-T01-10 absent native original refuses lossy export and preserves the unresolved slot.");
            }
            catch (Exception error) { Check(false, "UC-T01-RECOVERY threw: " + error); }
            finally
            {
                foreach (Graph graph in transient)
                    if (graph != null && !EditorUtility.IsPersistent(graph)) UnityEngine.Object.DestroyImmediate(graph);
                // This unique folder was created by this fixture, never an owner graph folder.
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        /// <summary>UC-T01-10: creates an owned native and JSON fixture before making only its test node type unavailable.</summary>
        /// <remarks>Batch-only, refuses an existing folder; use the missing/restore phases in later Editor processes.</remarks>
        public static void RunNativeRecoverySeed()
        {
            RunRecoveryPhase(() =>
            {
                if (AssetDatabase.IsValidFolder(NativeRecoveryFolder)) throw new InvalidOperationException("Recovery fixture already exists; it was not overwritten.");
                Type type = typeof(UCSelfTest).Assembly.GetType(ProbeTypeName, true);
                AssetDatabase.CreateFolder("Assets", NativeRecoveryFolder.Substring("Assets/".Length));
                Texture2D texture = new Texture2D(2, 2) { name = "Recovery texture" };
                AssetDatabase.CreateAsset(texture, NativeRecoveryFolder + "/Texture.asset");
                Generator probe = (Generator)Activator.CreateInstance(type);
                probe.id = "probe";
                type.GetField("texture").SetValue(probe, texture);
                Graph graph = ScriptableObject.CreateInstance<Graph>();
                graph.nodes.Add(new ConstantNode { id = "neighbor", value = 0.25f });
                graph.nodes.Add(probe);
                graph.nodes.Add(new MaximumNode { id = "output" });
                graph.connections.Add(new Connection("neighbor", 0, "output", 0));
                graph.connections.Add(new Connection("probe", 0, "output", 1));
                AssetDatabase.CreateAsset(graph, NativeRecoveryFolder + "/Native.asset");
                AssetDatabase.SaveAssetIfDirty(graph);
                string json = GraphSerializer.ToJson(graph);
                Graph repair = UnityEngine.Object.Instantiate(graph);
                AssetDatabase.CreateAsset(repair, NativeRecoveryFolder + "/Repair.asset");
                AssetDatabase.SaveAssetIfDirty(repair);
                Check(json != null && json.Contains("globalId"), "UC-T01-NATIVE seed writes stable object identity.");
                File.WriteAllText(NativeRecoveryFolder + "/Original.ucgraph", json);
                Check(!SerializationUtility.HasManagedReferencesWithMissingTypes(graph), "UC-T01-NATIVE seed type exists.");
            });
        }

        /// <summary>UC-T01-10: verifies real native missing-type persistence in a later process where the fixture class is unavailable.</summary>
        /// <remarks>Touches only Assets/UCNativeRecoverySelfTest created by the seed phase; never removes actual plugins.</remarks>
        public static void RunNativeRecoveryMissing()
        {
            RunRecoveryPhase(() =>
            {
                Check(typeof(UCSelfTest).Assembly.GetType(ProbeTypeName) == null, "UC-T01-NATIVE probe type is actually unavailable.");
                Graph graph = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Native.asset");
                int missingSlot = graph.GetNodeById("neighbor") != null ? 1 : 0;
                Check(SerializationUtility.HasManagedReferencesWithMissingTypes(graph) && graph.nodes[missingSlot] == null,
                    "UC-T01-NATIVE native loader retains an actual missing managed-reference record.");
                if (!SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                    throw new InvalidOperationException("The fixture has no native missing-type payload; no repair was attempted.");
                Check(GraphSerializer.ToJson(graph) == null, "UC-T01-NATIVE lossy native JSON export is refused.");
                EditorUtility.SetDirty(graph);
                AssetDatabase.SaveAssetIfDirty(graph);
                AssetDatabase.ImportAsset(NativeRecoveryFolder + "/Native.asset", ImportAssetOptions.ForceUpdate);
                graph = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Native.asset");
                Check(SerializationUtility.HasManagedReferencesWithMissingTypes(graph),
                    "UC-T01-NATIVE saving an unchanged missing-type graph preserves hidden payload on disk.");
                graph.RemoveNode("neighbor");
                EditorUtility.SetDirty(graph);
                AssetDatabase.SaveAssetIfDirty(graph);
                AssetDatabase.ImportAsset(NativeRecoveryFolder + "/Native.asset", ImportAssetOptions.ForceUpdate);
                graph = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Native.asset");
                Check(graph.nodes.Count == 2 && graph.nodes[0] == null && graph.connections.Count == 1 &&
                      graph.unresolvedNodes.Count == 1 && graph.unresolvedNodes[0].slot == 0 &&
                      graph.unresolvedNodes[0].nativeSlot == 1 &&
                      SerializationUtility.HasManagedReferencesWithMissingTypes(graph.unresolvedNodes[0].nativeOriginal),
                    "UC-T01-NATIVE unrelated deletion/save preserves missing payload and surviving wire.");
                Check(GraphSerializer.ToJson(graph) == null,
                    "UC-T01-NATIVE backed-up native payload also refuses lossy JSON export.");
                Graph imported = GraphSerializer.FromJson(File.ReadAllText(NativeRecoveryFolder + "/Original.ucgraph"));
                Check(imported.unresolvedNodes.Count == 1 && imported.unresolvedNodes[0].objectReferences.Count == 1,
                    "UC-T01-NATIVE unavailable JSON type retains stable texture identity.");
                AssetDatabase.CreateAsset(imported, NativeRecoveryFolder + "/Imported.asset");
                AssetDatabase.SaveAssetIfDirty(imported);
                File.WriteAllText(NativeRecoveryFolder + "/Unknown.ucgraph", GraphSerializer.ToJson(imported));
                Graph repair = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Repair.asset");
                if (!SerializationUtility.HasManagedReferencesWithMissingTypes(repair))
                    throw new InvalidOperationException("The repair fixture has no native missing-type payload; no repair was attempted.");
                List<string> backups = GraphRepairPreview.ApplySelected(new[] { GraphRepairPreview.Analyze(repair) });
                Check(backups.Count == 1 && !SerializationUtility.HasManagedReferencesWithMissingTypes(repair),
                    "UC-T01-NATIVE explicit fixture repair clears native missing record after backup.");
                Undo.PerformUndo();
                repair = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Repair.asset");
                Check(repair.nodes.Count == 3 && repair.unresolvedNodes.Count == 1 &&
                      SerializationUtility.HasManagedReferencesWithMissingTypes(repair.unresolvedNodes[0].nativeOriginal),
                    "UC-T01-NATIVE Undo retains the missing slot and its native recovery original.");
                Undo.PerformRedo();
                Check(repair.nodes.Count == 2 && !SerializationUtility.HasManagedReferencesWithMissingTypes(repair),
                    "UC-T01-NATIVE Redo reapplies only the selected repair.");
                Undo.PerformUndo();
                repair = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Repair.asset");
                EditorUtility.SetDirty(repair);
                AssetDatabase.SaveAssetIfDirty(repair);
            });
        }

        /// <summary>UC-T01-10: verifies native and JSON restoration in a third process after the fixture class returns.</summary>
        /// <remarks>Leaves fixture assets available for review; cleanup is a separate owned-fixture operation.</remarks>
        public static void RunNativeRecoveryRestored()
        {
            RunRecoveryPhase(() =>
            {
                Type type = typeof(UCSelfTest).Assembly.GetType(ProbeTypeName, true);
                Graph graph = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Native.asset");
                Check(GraphSerializer.RestoreUnresolved(graph) == 1 && graph.unresolvedNodes.Count == 0,
                    "UC-T01-NATIVE archived native slot restores after type return/restart.");
                Check(!SerializationUtility.HasManagedReferencesWithMissingTypes(graph), "UC-T01-NATIVE original type resolves after restoration/restart.");
                CheckRecoveryProbe(graph.nodes[0], type, "native");
                Check(graph.nodes.Count == 2 && graph.connections.Count == 1 && graph.connections[0].sourceNodeId == "probe",
                    "UC-T01-NATIVE saved node order and incident wire survive missing-type phase.");
                Graph backup = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryBackup);
                CheckRecoveryProbe(backup.nodes[1], type, "native backup");
                Graph repair = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Repair.asset");
                Check(GraphSerializer.RestoreUnresolved(repair) == 1,
                    "UC-T01-NATIVE native repair Undo restores its archived node after restart.");
                CheckRecoveryProbe(repair.nodes[1], type, "native repair Undo");
                Check(repair.nodes.Count == 3 && repair.connections.Count == 2,
                    "UC-T01-NATIVE repair Undo retains original order and wires after restart.");
                Graph imported = AssetDatabase.LoadAssetAtPath<Graph>(NativeRecoveryFolder + "/Imported.asset");
                Check(GraphSerializer.RestoreUnresolved(imported) == 1 && imported.unresolvedNodes.Count == 0,
                    "UC-T01-NATIVE persisted JSON recovery record restores after restart.");
                CheckRecoveryProbe(imported.nodes[1], type, "persisted JSON");
                Undo.PerformUndo();
                Check(imported.nodes[1] == null && imported.unresolvedNodes.Count == 1,
                    "UC-T01-NATIVE imported-node restoration supports Undo.");
                Graph roundTrip = GraphSerializer.FromJson(File.ReadAllText(NativeRecoveryFolder + "/Unknown.ucgraph"));
                try
                {
                    CheckRecoveryProbe(roundTrip.nodes[1], type, "exported unknown JSON");
                    Check(roundTrip.connections.Count == 2, "UC-T01-NATIVE unknown JSON export retained all original wires.");
                }
                finally { UnityEngine.Object.DestroyImmediate(roundTrip); }
            });
        }

        private static void CheckRecoveryProbe(Generator probe, Type type, string source)
        {
            Check(probe != null && probe.GetType() == type && probe.id == "probe", "UC-T01-NATIVE " + source + " restores type/identity.");
            if (probe == null) return;
            Check((float)type.GetField("scalar").GetValue(probe) == 0.731f, "UC-T01-NATIVE " + source + " restores scalar.");
            float[] samples = (float[])type.GetField("samples").GetValue(probe);
            Check(samples.Length == 3 && samples[0] == 3.25f && samples[1] == -1.5f && samples[2] == 0.125f,
                "UC-T01-NATIVE " + source + " restores array order and values.");
            Check((Texture2D)type.GetField("texture").GetValue(probe) == AssetDatabase.LoadAssetAtPath<Texture2D>(NativeRecoveryFolder + "/Texture.asset"),
                "UC-T01-NATIVE " + source + " restores persistent texture reference.");
        }

        private static void RunRecoveryPhase(Action phase)
        {
            checks = 0;
            failures.Clear();
            try { phase(); }
            catch (Exception error) { Check(false, "UC-T01-NATIVE phase threw: " + error); }
            Console.WriteLine(Report(failures.Count == 0));
            EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }
    }
}
