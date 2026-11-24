// UC-T06-15/16: real saved form routing and Undo binding preserve authored values and never run placement implicitly.
using System;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Editor.Inspectors;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationCategoryAuthoring()
        {
            var root = new GameObject("UC category authoring acceptance"); root.SetActive(false);
            var graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = true;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); graph.SetDomain(UCGraphDomain.Vegetation);
                VegetationGraphAuthoring.Bind(bindings, graph); Undo.FlushUndoRecordObjects();
                Check(bindings.VegetationGraph == graph && bindings.VegetationRevision == 0 && !bindings.IsVegetationRunning,
                    "UC-T06-15 shared manager/graph binding saves authoring without an implicit placement Run.");
                Undo.PerformUndo();
                Check(bindings.VegetationGraph == null && bindings.VegetationRevision == 0,
                    "UC-T06-15 binding Undo preserves saved authoring without applying an alternate plan.");
                world.scatterLibrary.trees.Add(new TreePrototypeDef()); world.scatterLibrary.shrubs.Add(new ShrubPrototypeDef());
                world.scatterLibrary.details.Add(new DetailPrototypeDef()); world.scatterLibrary.objects.Add(new ObjectPrototypeDef());
                string before = EditorJsonUtility.ToJson(world);
                foreach (UCScatterKind kind in new[] { UCScatterKind.Tree, UCScatterKind.Shrub, UCScatterKind.Detail, UCScatterKind.Object })
                {
                    Check(UCEcoEnvelopeDrawer.CategoryDrawsField(kind, "slopeOptimumDeg") && UCEcoEnvelopeDrawer.CategoryDrawsField(kind, "allowedBiomes"),
                        "UC-T06-16 " + kind + " retains consumed habitat and biome authoring.");
                    bool woody = kind == UCScatterKind.Tree || kind == UCScatterKind.Shrub;
                    Check(UCEcoEnvelopeDrawer.CategoryDrawsField(kind, "dbhRangeM") == woody &&
                        UCEcoEnvelopeDrawer.CategoryDrawsField(kind, "crownAllometryA") == woody,
                        "UC-T06-16 " + kind + " exposes woody allometry only for actual tree/shrub consumers.");
                }
                using (var serialized = new SerializedObject(world))
                {
                    var alignment = serialized.FindProperty("scatterLibrary.details").GetArrayElementAtIndex(0).FindPropertyRelative("groundAlignStrength");
                    alignment.floatValue = 0.27f; serialized.ApplyModifiedProperties();
                    Check(Mathf.Abs(world.scatterLibrary.details[0].groundAlignStrength - 0.27f) < 0.0001f && !bindings.IsVegetationRunning,
                        "UC-T06-16 actual native-detail alignment control writes the saved production field without starting placement.");
                }
                world.scatterLibrary.details[0].groundAlignStrength = 0.6f;
                Check(EditorJsonUtility.ToJson(world) == before,
                    "UC-T06-16 category switching/filtering preserves hidden shared envelope data and prototype identities.");
            }
            catch (Exception error) { Check(false, "UC-T06-15/16 category authoring exception: " + error); }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
