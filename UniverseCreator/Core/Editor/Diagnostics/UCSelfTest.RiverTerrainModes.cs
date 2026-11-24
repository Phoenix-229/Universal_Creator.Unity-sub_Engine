// UC-T04-01: original legacy intent, body authority, serialized recovery and Undo.
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestRiverTerrainModes()
        {
            for (int bodyFlag = 0; bodyFlag < 2; bodyFlag++)
                for (int digFlag = 0; digFlag < 2; digFlag++)
                    foreach (int pointFlags in new[] { 0, 1, 3 })
                    {
                        var root = new GameObject("UC legacy river mode"); root.SetActive(false);
                        try
                        {
                            var body = root.AddComponent<UCWaterBody>(); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                            body.followProceduralTrench = bodyFlag != 0; body.digTerrain = digFlag != 0;
                            var first = RiverModePoint(root.transform, new Vector3(-3, 7, 11), (pointFlags & 1) != 0);
                            var last = RiverModePoint(root.transform, new Vector3(9, 2, 37), (pointFlags & 2) != 0);
                            var expected = bodyFlag != 0 || pointFlags != 0 ? RiverTerrainMode.ReadProceduralTrench : RiverTerrainMode.CarveManualCourse;
                            bool conflict = bodyFlag != 0 ? pointFlags != 3 : pointFlags != 0;
                            body.EnsureRiverTerrainMode();
                            Check(body.TerrainMode == expected && !body.NeedsRiverTerrainModeMigration &&
                                body.RiverTerrainModeDiagnostic.Contains("conflict") == conflict,
                                $"UC-T04-01 legacy body={bodyFlag}, dig={digFlag}, points={pointFlags} selects the previous effective mode and reports ambiguity.");
                            Check(body.digTerrain == (digFlag != 0) && body.followProceduralTrench == (bodyFlag != 0) &&
                                first.followProceduralTrench == ((pointFlags & 1) != 0) && last.followProceduralTrench == ((pointFlags & 2) != 0) &&
                                first.transform.localPosition == new Vector3(-3, 7, 11) && last.transform.localPosition == new Vector3(9, 2, 37) &&
                                first.halfWidthM == 4 && last.depthM == 0.7f && last.dischargeM3PerS == 12 && first.velocityMps == 1.3f,
                                "UC-T04-01 migration preserves original flags, excavation permission, point positions and hydraulic values.");
                            first.followProceduralTrench = !first.followProceduralTrench;
                            last.followProceduralTrench = !last.followProceduralTrench;
                            body.followProceduralTrench = !body.followProceduralTrench;
                            Check(body.TerrainMode == expected, "UC-T04-01 later legacy body/child edits cannot override the authoritative mode.");
                            // UC-T04-01: Editor JSON writes unsaved scene references as instanceID 0; use native component serialization here and saved-scene reload for persistence.
                            var savedRoot = new GameObject("Serialized river mode record"); savedRoot.SetActive(false); savedRoot.transform.SetParent(root.transform, false);
                            var saved = savedRoot.AddComponent<UCWaterBody>(); EditorUtility.CopySerialized(body, saved);
                            var other = expected == RiverTerrainMode.CarveManualCourse ? RiverTerrainMode.ReadProceduralTrench : RiverTerrainMode.CarveManualCourse;
                            body.SetRiverTerrainMode(other); EditorUtility.CopySerialized(saved, body);
                            var recovery = new SerializedObject(body).FindProperty("legacyRiverTerrainSettings"); // UC-T04-01: verify references before recovery, independently of resulting flags.
                            var references = recovery.FindPropertyRelative("nodes");
                            Check(references.arraySize == 2 && references.GetArrayElementAtIndex(0).objectReferenceValue == first &&
                                references.GetArrayElementAtIndex(1).objectReferenceValue == last,
                                "UC-T04-01 native component serialization retains the original recovery point references. count=" + references.arraySize);
                            bool loadedMode = body.TerrainMode == expected;
                            bool restored = loadedMode && body.RestoreLegacyRiverTerrainSettings();
                            Check(loadedMode && restored &&
                                first.followProceduralTrench == ((pointFlags & 1) != 0) && last.followProceduralTrench == ((pointFlags & 2) != 0) &&
                                body.followProceduralTrench == (bodyFlag != 0) && body.digTerrain == (digFlag != 0) && body.TerrainMode == expected,
                                $"UC-T04-01 serialized mode and recovery record restore original surviving flags without altering the course. loaded={loadedMode}, restored={restored}, body={body.followProceduralTrench}, points={first.followProceduralTrench}/{last.followProceduralTrench}, expected={bodyFlag}/{pointFlags}.");
                        }
                        finally { UnityEngine.Object.DestroyImmediate(root); }
                    }
            TestRiverTerrainModeUndoAndOwnership();
        }

        private static UCRiverNode RiverModePoint(Transform parent, Vector3 local, bool read)
        {
            var point = new GameObject("River mode point"); point.transform.SetParent(parent, false); point.transform.localPosition = local;
            var node = point.AddComponent<UCRiverNode>(); node.followProceduralTrench = read;
            node.halfWidthM = 4; node.depthM = 0.7f; node.dischargeM3PerS = 12; node.velocityMps = 1.3f;
            return node;
        }

        private static void TestRiverTerrainModeUndoAndOwnership()
        {
            var root = new GameObject("UC river mode ownership"); root.SetActive(false);
            try
            {
                var body = root.AddComponent<UCWaterBody>(); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                var point = RiverModePoint(root.transform, new Vector3(2, 8, 10), false);
                var nested = new GameObject("Nested river"); nested.transform.SetParent(root.transform, false); nested.AddComponent<UCWaterBody>();
                RiverModePoint(nested.transform, Vector3.zero, true);
                Check(body.TerrainMode == RiverTerrainMode.CarveManualCourse, "UC-T04-01 a nested body's checked point cannot change the parent river mode.");
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
                Undo.RecordObject(body, "Test river terrain mode"); body.SetRiverTerrainMode(RiverTerrainMode.ReadProceduralTrench);
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); Undo.PerformUndo();
                Check(body.TerrainMode == RiverTerrainMode.CarveManualCourse && point.transform.localPosition == new Vector3(2, 8, 10),
                    "UC-T04-01 Undo restores the body mode without moving its manually authored point.");
                bool rejected = false;
                try { body.SetRiverTerrainMode((RiverTerrainMode)37); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && body.TerrainMode == RiverTerrainMode.CarveManualCourse, "UC-T04-01 unsupported enum input cannot change the existing mode.");
                var serialized = new SerializedObject(body); serialized.FindProperty("riverTerrainModeVersion").intValue = 99;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Check(body.TerrainMode == RiverTerrainMode.ReadProceduralTrench && body.RiverTerrainModeDiagnostic.Contains("Unsupported") &&
                    !body.RestoreLegacyRiverTerrainSettings(), "UC-T04-01 future schemas remain recoverable and cannot silently enable manual excavation.");
                serialized.Update();
                Check(serialized.FindProperty("riverTerrainModeVersion").intValue == 99, "UC-T04-01 querying a future mode schema leaves its serialized version intact.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>Checks an ambiguous legacy river migration and recovery after an actual saved-scene reload in an isolated batch.</summary>
        /// <remarks>Creates and removes only its temporary Unity-authored fixture assets; requires one empty unsaved or saved startup scene.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunRiverTerrainModeSceneReloadBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("River mode reload requires isolated batch mode.");
            checks = 0; failures.Clear();
            Scene original = SceneManager.GetActiveScene(); bool single = string.IsNullOrEmpty(original.path);
            Scene fixture = default; string folder = "Assets/UCRiverModeReload_" + Guid.NewGuid().ToString("N");
            try
            {
                if (SceneManager.sceneCount != 1 || (single && original.GetRootGameObjects().Length > 0))
                    throw new InvalidOperationException("River mode reload requires a saved or empty startup scene.");
                AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
                var root = new GameObject("UC saved legacy river"); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, fixture);
                var body = root.AddComponent<UCWaterBody>(); body.shape = UCWaterBody.ShapeMode.RiverSpline;
                RiverModePoint(root.transform, new Vector3(2, 9, 7), true); RiverModePoint(root.transform, new Vector3(9, 4, 30), false);
                body.EnsureRiverTerrainMode(); body.SetRiverTerrainMode(RiverTerrainMode.CarveManualCourse);
                Check(EditorSceneManager.SaveScene(fixture, folder + "/World.unity"), "UC-T04-01 the mode/recovery fixture saves through Unity serialization.");
                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); else EditorSceneManager.CloseScene(fixture, true);
                fixture = EditorSceneManager.OpenScene(folder + "/World.unity", single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                body = fixture.GetRootGameObjects()[0].GetComponent<UCWaterBody>();
                var points = body.GetComponentsInChildren<UCRiverNode>(true);
                Check(body.TerrainMode == RiverTerrainMode.CarveManualCourse && body.RiverTerrainModeDiagnostic.Contains("conflict") &&
                    points[0].transform.localPosition == new Vector3(2, 9, 7) && points[1].transform.localPosition == new Vector3(9, 4, 30),
                    "UC-T04-01 scene reload retains the chosen authoritative mode, conflict provenance and exact authored positions.");
                Check(body.RestoreLegacyRiverTerrainSettings() && body.TerrainMode == RiverTerrainMode.ReadProceduralTrench &&
                    points[0].followProceduralTrench && !points[1].followProceduralTrench,
                    "UC-T04-01 the ambiguous original legacy settings remain recoverable after saved-scene reload.");
            }
            catch (Exception error) { Check(false, "UC-T04-01 scene reload exception: " + error); }
            finally
            {
                if (fixture.IsValid() && fixture.isLoaded)
                { if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); else EditorSceneManager.CloseScene(fixture, true); }
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
            ExitTerrainRiverGate();
        }
    }
}
