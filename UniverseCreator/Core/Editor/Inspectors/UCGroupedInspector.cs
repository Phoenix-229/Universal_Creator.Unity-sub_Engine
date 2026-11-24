// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCGroupedInspector.cs
//  Purpose: Shared drawing for the world system managers, whose inspectors are
//           part live and part declared.
//
//  THE PROBLEM THIS SOLVES
//  -----------------------
//  These managers are large on purpose: they are where a world's physics is
//  authored, so the author needs every dial. But they are also being built in
//  passes, so at any moment some groups are read and some are not.
//
//  Drawing the whole component disabled - which is what the inspector this
//  replaces did - takes the author's control away the moment any part of the
//  component goes live. Drawing it all editable claims placeholders work. Neither
//  is honest, so groups carry their own status: a live group is editable, a
//  declared group is disabled and says so, and the two sit in one inspector.
//
//  THE UNLISTED-FIELD RULE
//  -----------------------
//  Anything not named in a group is drawn at the bottom under "Unlisted", not
//  dropped. That is deliberate: fields get appended to these managers often, and
//  the failure mode of a hand-written group list is a field the author can no
//  longer see and no error anywhere. Degrading to "shown in the wrong place" is
//  recoverable; degrading to invisible is not.
//
//  A name in a group that no longer matches a field draws a warning where the
//  field would have been, for the same reason: a renamed field must be loud.
//
//  NO DIALOGS.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>
    /// One collapsible group of fields in a system manager inspector, and whether
    /// anything reads them yet.
    /// </summary>
    public struct UCFieldGroup
    {
        public string title;

        /// <summary>Something reads these fields. False draws them disabled.</summary>
        public bool live;

        /// <summary>One line under the group title. Empty draws nothing.</summary>
        public string note;

        public string[] fields;

        public UCFieldGroup(string title, bool live, string note, params string[] fields)
        {
            this.title = title;
            this.live = live;
            this.note = note;
            this.fields = fields;
        }
    }

    /// <summary>
    /// Draws a system manager as a stack of live and declared groups.
    /// </summary>
    public static class UCGroupedInspector
    {
        private const string PrefsRoot = "UniverseCreator.Inspector.";

        /// <summary>deg. Left indent for the fields inside a group.</summary>
        private const float GroupIndent = 6f;

        /// <summary>
        /// Header bar, optional notice, then every group. Applies changes, so the
        /// live groups are genuinely editable; the disabled scope is what keeps
        /// the declared ones from being written.
        /// </summary>
        public static void Draw(SerializedObject serialized, string prefsKey,
                                string title, string icon, Color accent, string status,
                                string notice, UCFieldGroup[] groups)
        {
            UCInspectorUI.HeaderBar(title, icon, accent, status);

            if (!string.IsNullOrEmpty(notice))
            {
                EditorGUILayout.Space(3);
                EditorGUILayout.HelpBox(notice, MessageType.Info);
            }

            EditorGUILayout.Space(3);

            serialized.Update();

            // Built once, up front, from the group table alone - NOT accumulated as the
            // groups draw. That matters twice. It is what lets a collapsed group still
            // claim its fields without each drawing path having to remember to (the bug
            // the old accumulate-while-drawing version had to carry a comment about), and
            // it is what lets the self-test ask the same question headlessly: "given these
            // groups, what would fall through to Unlisted" has one definition, used by the
            // inspector and the gate, so a green gate cannot mean something different from
            // what the author sees.
            HashSet<string> listed = ListedFieldNames(groups);

            for (int g = 0; g < groups.Length; g++)
            {
                DrawGroup(serialized, prefsKey, accent, groups[g]);
            }

            DrawUnlisted(serialized, prefsKey, listed);

            serialized.ApplyModifiedProperties();
        }

        /// <summary>
        /// Every field name the group table accounts for. The single definition of "listed",
        /// shared by the inspector and by the self-test's census of ungrouped fields.
        /// </summary>
        internal static HashSet<string> ListedFieldNames(UCFieldGroup[] groups)
        {
            HashSet<string> listed = new HashSet<string>();
            if (groups == null) return listed;

            for (int g = 0; g < groups.Length; g++)
            {
                string[] fields = groups[g].fields;
                if (fields == null) continue;
                for (int i = 0; i < fields.Length; i++) listed.Add(fields[i]);
            }

            return listed;
        }

        /// <summary>
        /// The visible serialized properties no group accounts for - what the amber "Unlisted"
        /// section shows. Pure: it walks the SerializedObject and touches no GUI, so the
        /// self-test can call it headless, where the editor skin is not safe to touch.
        ///
        /// `NextVisible` is the right iterator and also the honest one: it skips
        /// [HideInInspector] fields, so a field deliberately hidden is not reported as
        /// ungrouped, while every field an author can actually see is.
        /// </summary>
        internal static List<SerializedProperty> CollectUnlisted(SerializedObject serialized,
                                                                 HashSet<string> listed)
        {
            List<SerializedProperty> extras = new List<SerializedProperty>();
            if (serialized == null) return extras;

            SerializedProperty p = serialized.GetIterator();
            bool enterChildren = true;
            while (p.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (p.name == "m_Script") continue;
                if (listed != null && listed.Contains(p.name)) continue;

                extras.Add(p.Copy());
            }

            return extras;
        }

        private static void DrawGroup(SerializedObject serialized, string prefsKey,
                                      Color accent, UCFieldGroup group)
        {
            // A declared group reads grey rather than the component's accent, so
            // the eye can sort live from not-live without reading a word of it.
            Color groupAccent = group.live ? accent : UCInspectorUI.HeaderGrey;
            string heading = group.live ? group.title : group.title + "   (declared)";

            string key = PrefsRoot + prefsKey + "." + group.title;

            // Live groups start open and declared ones closed: the author came here
            // to edit what works, not to scroll past what does not.
            bool open = EditorPrefs.GetBool(key, group.live);
            bool next = UCInspectorUI.Section(open, heading, groupAccent);
            if (next != open) EditorPrefs.SetBool(key, next);

            if (!next)
            {
                // Nothing to claim here: ListedFieldNames already accounted for every
                // field in every group, open or not, before the first one drew.
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(GroupIndent);
            EditorGUILayout.BeginVertical();

            if (!group.live)
            {
                EditorGUILayout.LabelField(
                    "Saved on this component and read by nothing yet.",
                    EditorStyles.miniLabel);
            }
            else if (!string.IsNullOrEmpty(group.note))
            {
                EditorGUILayout.LabelField(group.note, EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(!group.live))
            {
                for (int i = 0; i < group.fields.Length; i++)
                {
                    string name = group.fields[i];

                    SerializedProperty p = serialized.FindProperty(name);
                    if (p == null)
                    {
                        // Loud on purpose. A silent skip here is how a renamed
                        // field disappears from the inspector for a whole release.
                        EditorGUILayout.HelpBox(
                            "No serialized field named \"" + name + "\". The inspector's " +
                            "group list is out of date with the component.",
                            MessageType.Error);
                        continue;
                    }

                    EditorGUILayout.PropertyField(p, true);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
        }

        /// <summary>
        /// Everything the group lists did not account for. Editable, because an
        /// unlisted field is a field whose status this inspector does not know,
        /// and guessing "disabled" would silently lock out a working dial.
        /// </summary>
        private static void DrawUnlisted(SerializedObject serialized, string prefsKey,
                                         HashSet<string> listed)
        {
            List<SerializedProperty> extras = CollectUnlisted(serialized, listed);

            if (extras.Count == 0) return;

            string key = PrefsRoot + prefsKey + ".Unlisted";
            bool open = EditorPrefs.GetBool(key, true);
            bool next = UCInspectorUI.Section(open, "Unlisted   (" + extras.Count + ")",
                                              UCInspectorUI.HeaderAmber);
            if (next != open) EditorPrefs.SetBool(key, next);
            if (!next) return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(GroupIndent);
            EditorGUILayout.BeginVertical();

            EditorGUILayout.LabelField(
                "Fields this inspector has no group for. Shown so nothing is hidden.",
                EditorStyles.miniLabel);

            for (int i = 0; i < extras.Count; i++)
            {
                EditorGUILayout.PropertyField(extras[i], true);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Runs one optional panel of an inspector and reports a throw as a message
        /// instead of letting it take the window down.
        ///
        /// WHY THIS EXISTS. An exception thrown out of OnInspectorGUI does not just
        /// lose the panel that threw: IMGUI abandons the layout mid-tree, so the NEXT
        /// repaint has fewer controls than the layout pass promised and the ENTIRE
        /// component draws blank. The author sees an empty inspector, with no clue
        /// that a readout in the last third of it is the reason, and no field to edit
        /// to get out of it. One bad null in a diagnostic panel silently costs the
        /// whole editing surface for that component.
        ///
        /// Naming the failed panel and carrying on is strictly better: the fields
        /// stay editable, and the defect is stated rather than presented as a blank
        /// window. ExitGUIException is re-thrown untouched - Unity uses it as control
        /// flow to leave the GUI (an object picker, a dialog), and swallowing it
        /// breaks those instead.
        /// </summary>
        public static void Panel(string label, System.Action draw)
        {
            if (draw == null) return;

            try
            {
                draw();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (System.Exception e)
            {
                EditorGUILayout.HelpBox(
                    "The " + label + " panel could not be drawn: " + e.GetType().Name +
                    ": " + e.Message + "\n\nThe fields above are unaffected. The full " +
                    "stack trace is in the Console.",
                    MessageType.Error);

                Debug.LogException(e);
            }
        }

        /// <summary>
        /// A read-only computed line: label on the left, derived value on the
        /// right. What turns these inspectors from a field dump into something
        /// that shows the author what their numbers actually mean.
        /// </summary>
        public static void Readout(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            GUIStyle right = new GUIStyle(EditorStyles.miniLabel);
            right.alignment = TextAnchor.MiddleRight;
            EditorGUILayout.LabelField(value, right);
            EditorGUILayout.EndHorizontal();
        }
    }
}
