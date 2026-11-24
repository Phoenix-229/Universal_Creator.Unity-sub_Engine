// UC-T04-01: both inspectors edit the same body mode; migration/recovery participate in Undo.
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor
{
    internal static class UCRiverTerrainModeGUI
    {
        internal static bool Draw(UCWaterBody body)
        {
            if (body == null) return false;
            bool changed = false;
            if (body.NeedsRiverTerrainModeMigration)
            {
                Undo.RecordObject(body, "Migrate river terrain mode");
                body.EnsureRiverTerrainMode();
                EditorUtility.SetDirty(body);
            }
            EditorGUI.BeginChangeCheck();
            var mode = (RiverTerrainMode)EditorGUILayout.EnumPopup(new GUIContent("River Terrain Mode",
                "Read Procedural Trench fits accepted ground. Carve Manual Course owns this authored river's excavation."), body.TerrainMode);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(body, "Change river terrain mode");
                body.SetRiverTerrainMode(mode);
                EditorUtility.SetDirty(body);
                changed = true;
            }
            string diagnostic = body.RiverTerrainModeDiagnostic;
            if (body.RiverSurfaceDiagnostic.Length > 0)
                EditorGUILayout.HelpBox(body.RiverSurfaceDiagnostic, MessageType.Warning); // UC-T04-04: parent and child show the same invalid-fit reason.
            if (body.WaterJunctionDiagnostic.Length > 0)
                EditorGUILayout.HelpBox(body.WaterJunctionDiagnostic, MessageType.Warning); // UC-T04-05: both inspectors show connection ownership failures.
            if (diagnostic.Length > 0)
            {
                EditorGUILayout.HelpBox(diagnostic, MessageType.Warning);
                if (GUILayout.Button("Restore Original Legacy Mode Flags"))
                {
                    var points = body.OwnedRiverNodes(); // UC-T04-05B: nested rivers retain their own recovery/Undo ownership.
                    var originals = new Object[points.Length + 1]; originals[0] = body;
                    for (int i = 0; i < points.Length; i++) originals[i + 1] = points[i];
                    Undo.RecordObjects(originals, "Restore legacy river mode flags");
                    changed |= body.RestoreLegacyRiverTerrainSettings();
                    EditorUtility.SetDirty(body);
                    foreach (var point in points) EditorUtility.SetDirty(point);
                }
            }
            return changed;
        }
    }
}
