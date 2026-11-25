// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCPlannedModuleInspector.cs
//  Purpose: The inspector shared by every manager whose module is not written yet
//           - vegetation and lighting today, whatever comes next tomorrow.
//
//  WHY THESE DRAW DIFFERENTLY
//  --------------------------
//  A field that looks editable but is read by nothing costs the user real time:
//  they set it, nothing happens, and they go looking for the bug in their scene.
//  So a planned module's fields are drawn disabled under a notice that says what
//  they are. The information is all there - names, units, ranges, tooltips - and
//  the inspector never claims they do anything.
//
//  When one of these modules is built, its manager gets its own inspector and
//  drops out of here. Nothing needs deleting for that to happen.
//
//  NO DIALOGS.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>
    /// Shared drawing for a manager whose module has not been implemented.
    /// </summary>
    public static class UCPlannedModule
    {
        /// <summary>
        /// Header, notice, and every serialized field drawn disabled.
        /// moduleName is what the notice calls the missing module.
        /// </summary>
        public static void Draw(SerializedObject serialized, string title, string icon,
                                Color accent, string moduleName, string inherits)
        {
            UCInspectorUI.HeaderBar(title, icon, accent, "not implemented");

            EditorGUILayout.Space(4);

            EditorGUILayout.HelpBox(
                "The " + moduleName + " module is not written yet. Every field below is " +
                "saved on this component and read by nothing, so changing one has no " +
                "effect on the scene.\n\n" +
                "They are here because " + moduleName + " cannot be designed apart from " +
                "the terrain it sits on, and those constraints are already fixed: " +
                inherits,
                MessageType.Warning);

            EditorGUILayout.Space(4);

            serialized.Update();

            using (new EditorGUI.DisabledScope(true))
            {
                // Walked by hand rather than through Editor.DrawPropertiesExcluding,
                // which is protected on Editor and so unreachable from a plain static
                // class. Enter/NextVisible is what that helper does anyway.
                SerializedProperty p = serialized.GetIterator();
                bool enterChildren = true;
                while (p.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (p.propertyPath == "m_Script") continue;
                    EditorGUILayout.PropertyField(p, true);
                }
            }

            // Nothing is written back. A disabled scope already blocks input; not
            // applying is the second half of the same promise, so a script that
            // pokes these values cannot have them silently committed from here.
        }
    }

    [CustomEditor(typeof(UCVegetationSystemManager))]
    public class UCVegetationSystemManagerInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UCPlannedModule.Draw(
                serializedObject,
                "Vegetation System",
                "d_Terrain Icon",
                UCInspectorUI.HeaderGreen,
                "vegetation",
                "placement must be a pure function of world position and seed so a " +
                "cell that streams out and back returns the same trees; density comes " +
                "from the graph's surface masks rather than fresh noise; instances " +
                "belong to each cell's native TerrainData; and wind comes from the " +
                "weather system's single wind manager, never a second one.");
        }
    }

    [CustomEditor(typeof(UCLightingSystemManager))]
    public class UCLightingSystemManagerInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UCPlannedModule.Draw(
                serializedObject,
                "Lighting System",
                "d_Light Icon",
                UCInspectorUI.HeaderAmber,
                "lighting and time-of-day",
                "URP only, since every shader this toolkit ships targets it; one " +
                "directional sun that water, vegetation and terrain all read, never " +
                "two; time of day drives the sun rather than the reverse, so a saved " +
                "world reopens looking the same; and weather owns cloud and fog while " +
                "lighting reads them.");
        }
    }
}
