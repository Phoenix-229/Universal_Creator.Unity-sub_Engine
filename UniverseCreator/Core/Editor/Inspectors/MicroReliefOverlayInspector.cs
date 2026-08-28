// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//  File:    MicroReliefOverlayInspector.cs
//  Purpose: Authoring surface for the micro-relief patch ring, and the place two
//           facts the runtime decides are made visible to the author: which
//           geometry path the ring built (hardware tessellation or the
//           pre-subdivided fallback, with the reason in words), and the standing
//           limit that the overlay is a picture with no collider behind it.
//
//  WHY THE PATH AND THE LIMIT ARE SHOWN HERE
//  -----------------------------------------
//  The tessellation path is chosen once, on the GPU it happens to run on, so an
//  author staring at flat ground on a test device has no way to know whether the
//  fallback engaged or the shader failed - unless the toolkit says so. And the
//  no-collider limit is not a bug to be hunted in a profiler: it is a design
//  consequence of displacing on the GPU, so it is stated in plain sight rather
//  than left for footsteps in the air to reveal.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Overlay;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(MicroReliefOverlay))]
    public class MicroReliefOverlayInspector : UnityEditor.Editor
    {
        private static bool settingsFoldout = true;
        private static bool geometryFoldout = true;

        public override void OnInspectorGUI()
        {
            var overlay = (MicroReliefOverlay)target;

            UCInspectorUI.HeaderBar("Micro-Relief Ring", "d_Terrain Icon",
                                    UCInspectorUI.HeaderTeal, StatusText(overlay));

            EditorGUILayout.Space(6);
            DrawSettings();
            EditorGUILayout.Space(4);
            DrawGeometry(overlay);
            EditorGUILayout.Space(6);
            DrawColliderLimit();
        }

        private static string StatusText(MicroReliefOverlay overlay)
        {
            if (!Application.isPlaying) return "play-mode only";
            return overlay.ActiveTessPath == OverlayTessPath.HardwareTessellation
                ? "tessellating"
                : "fallback mesh";
        }

        // =====================================================================
        //  SETTINGS  (the plain serialized fields, drawn through SerializedObject)
        // =====================================================================

        private void DrawSettings()
        {
            settingsFoldout = UCInspectorUI.Section(settingsFoldout, "Ring Settings",
                                                    UCInspectorUI.HeaderTeal);
            if (!settingsFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                serializedObject.Update();
                DrawField("overlayPatchSizeM");
                DrawField("overlayPatchResolution");
                DrawField("targetCamera");
                DrawField("quality");
                DrawField("patchMaterial");
                DrawField("forceNonTessellatedFallback");
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawField(string propertyName)
        {
            var prop = serializedObject.FindProperty(propertyName);
            if (prop != null) EditorGUILayout.PropertyField(prop);
        }

        // =====================================================================
        //  GEOMETRY PATH  (decided once at build; read-only read-out)
        // =====================================================================

        private void DrawGeometry(MicroReliefOverlay overlay)
        {
            geometryFoldout = UCInspectorUI.Section(geometryFoldout, "Geometry Path",
                                                    UCInspectorUI.HeaderBlue);
            if (!geometryFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox(
                        "The ring builds in play mode only, so the geometry path is decided then. " +
                        "Enter play mode to see whether this GPU tessellates or draws the fixed " +
                        "fallback mesh, and why.", MessageType.None);
                    return;
                }

                EditorGUILayout.LabelField("Active path",
                    overlay.ActiveTessPath == OverlayTessPath.HardwareTessellation
                        ? "Hardware tessellation"
                        : "Pre-subdivided fallback",
                    EditorStyles.boldLabel);

                // The reason string is authored by OverlayTessellationSupport so the author reads the
                // same words the runtime decided on - not a paraphrase that could drift from the code.
                EditorGUILayout.HelpBox(overlay.ActiveTessPathReason,
                    overlay.ActiveTessPath == OverlayTessPath.HardwareTessellation
                        ? MessageType.Info
                        : MessageType.Warning);
            }
        }

        // =====================================================================
        //  THE NO-COLLIDER LIMIT  (stated in plain sight, not left to be found)
        // =====================================================================

        private void DrawColliderLimit()
        {
            EditorGUILayout.HelpBox(
                "This overlay is a picture, not a physics surface. Displacement happens in the " +
                "shader, so the collider under the player is still the native terrain's " +
                "un-displaced heightfield - feet land in the air over a shader mound and sink into " +
                "a shader pit. No collider ships with the ring (one that tracked the displaced " +
                "surface is not affordable on a phone).\n\n" +
                "The fix is an analytic height query (MicroReliefField.SampleOffset) that returns " +
                "the same millimetres the shader applies, so footsteps, foot IK, decals, prop " +
                "seating and camera collision can add it to the terrain height and read the surface " +
                "the picture shows. Until that query is wired, gameplay contact uses the native terrain.",
                MessageType.Warning);
        }
    }
}
