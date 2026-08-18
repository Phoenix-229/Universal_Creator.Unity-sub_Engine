// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    FocusOutFields.cs
//  Purpose: Reusable focus-out input controls. Text and number boxes commit on
//           Enter or focus-out; sliders commit on drag-release. Nothing commits
//           per keystroke.
//
//  WHY THIS MATTERS
//  ----------------
//  Every commit can kick off a full terrain regeneration. A per-keystroke field
//  would queue one generation per character typed - typing "1250" would start four
//  runs and cancel three. So the value under the control is left alone while the
//  user edits, and only replaced when they signal they are done.
//
//  Escape abandons the edit and restores the committed value.
//
//  Instance state, not static: two windows editing at once each need their own
//  pending buffer, and a static one would let them clobber each other.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;

namespace UniverseCreator.Editor.Utilities
{
    public class FocusOutFields
    {
        // Which control currently owns the pending buffer, and what is in it.
        private string focusedField = "";
        private string pendingText = "";

        // A slider being dragged: its id and its uncommitted value.
        private string liveSliderField;
        private float liveSliderValue;

        /// <summary>True on frames where a control committed a new value.</summary>
        public bool Changed { get; private set; }

        /// <summary>Call once at the top of a draw pass.</summary>
        public void BeginFrame() { Changed = false; }

        // ------------------------------------------------------------------
        //  Core: a text box that reports only on commit
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the new string on the frame the user commits (Enter or focus-out),
        /// null on every other frame. This is the whole contract.
        /// </summary>
        public string TextBox(string fieldName, string committedText, params GUILayoutOption[] options)
        {
            string commit = null;
            bool hasFocus = GUI.GetNameOfFocusedControl() == fieldName;

            // Focus-out: focused last frame, not this one.
            if (!hasFocus && focusedField == fieldName)
            {
                commit = pendingText;
                focusedField = "";
                pendingText = "";
            }

            // Focus-in: seed the buffer from the real value.
            if (hasFocus && focusedField != fieldName)
            {
                focusedField = fieldName;
                pendingText = committedText;
            }

            GUI.SetNextControlName(fieldName);

            if (hasFocus)
            {
                pendingText = EditorGUILayout.TextField(pendingText, options);

                Event e = Event.current;
                if (e.type == EventType.KeyDown &&
                    (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    commit = pendingText;
                    focusedField = "";
                    pendingText = "";
                    GUI.FocusControl(null);
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    focusedField = "";
                    pendingText = "";
                    GUI.FocusControl(null);
                    e.Use();
                }
            }
            else
            {
                EditorGUILayout.TextField(committedText, options);
            }

            return commit;
        }

        // ------------------------------------------------------------------
        //  Typed wrappers
        // ------------------------------------------------------------------

        public bool FloatField(string label, string fieldName, ref float value,
                               string tooltip, string format = "0.###")
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));
            string commit = TextBox(fieldName, value.ToString(format));
            EditorGUILayout.EndHorizontal();

            if (commit == null) return false;

            float parsed;
            if (!float.TryParse(commit, out parsed)) return false;
            if (Mathf.Approximately(parsed, value)) return false;

            value = parsed;
            Changed = true;
            return true;
        }

        public bool IntField(string label, string fieldName, ref int value, string tooltip)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));
            string commit = TextBox(fieldName, value.ToString());
            EditorGUILayout.EndHorizontal();

            if (commit == null) return false;

            // Parse as double first: "9999999999" would otherwise fail outright
            // rather than clamping to something sane.
            double parsed;
            if (!double.TryParse(commit, out parsed)) return false;
            parsed = System.Math.Max(int.MinValue, System.Math.Min(int.MaxValue, parsed));
            if ((int)parsed == value) return false;

            value = (int)parsed;
            Changed = true;
            return true;
        }

        /// <summary>Slider plus number box. Commits on drag-release or Enter.</summary>
        public bool FloatSlider(string label, string fieldName, ref float value,
                                float min, float max, string tooltip, string format = "0.###")
        {
            if (max < min) max = min;

            bool dragging = liveSliderField == fieldName;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            EditorGUI.BeginChangeCheck();
            float next = GUILayout.HorizontalSlider(dragging ? liveSliderValue : value, min, max,
                                                    GUILayout.MinWidth(40));
            if (EditorGUI.EndChangeCheck())
            {
                liveSliderField = fieldName;
                liveSliderValue = next;
                dragging = true;
            }

            if (dragging)
            {
                // Greyed label, not a field: the number is visibly not applied yet.
                GUILayout.Label(liveSliderValue.ToString(format), EditorStyles.miniLabel, GUILayout.Width(56));
                EditorGUILayout.EndHorizontal();

                // hotControl returns to 0 the instant the mouse button is released.
                if (GUIUtility.hotControl == 0)
                {
                    float committed = Mathf.Clamp(liveSliderValue, min, max);
                    liveSliderField = null;

                    if (!Mathf.Approximately(committed, value))
                    {
                        value = committed;
                        Changed = true;
                        return true;
                    }
                }
                return false;
            }

            string commit = TextBox(fieldName + "#num", value.ToString(format), GUILayout.Width(56));
            EditorGUILayout.EndHorizontal();

            if (commit == null) return false;

            float parsed;
            if (!float.TryParse(commit, out parsed)) return false;

            // Typed values are not clamped to the slider range: the slider shows the
            // useful span, but the maths often accepts more, and silently rewriting
            // what someone deliberately typed is worse than an off-scale handle.
            if (Mathf.Approximately(parsed, value)) return false;

            value = parsed;
            Changed = true;
            return true;
        }

        public bool IntSlider(string label, string fieldName, ref int value,
                              int min, int max, string tooltip)
        {
            if (max < min) max = min;

            bool dragging = liveSliderField == fieldName;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            EditorGUI.BeginChangeCheck();
            float next = GUILayout.HorizontalSlider(dragging ? liveSliderValue : value, min, max,
                                                    GUILayout.MinWidth(40));
            if (EditorGUI.EndChangeCheck())
            {
                liveSliderField = fieldName;
                liveSliderValue = Mathf.Round(next);
                dragging = true;
            }

            if (dragging)
            {
                GUILayout.Label(Mathf.RoundToInt(liveSliderValue).ToString(),
                                EditorStyles.miniLabel, GUILayout.Width(56));
                EditorGUILayout.EndHorizontal();

                if (GUIUtility.hotControl == 0)
                {
                    int committed = Mathf.Clamp(Mathf.RoundToInt(liveSliderValue), min, max);
                    liveSliderField = null;

                    if (committed != value)
                    {
                        value = committed;
                        Changed = true;
                        return true;
                    }
                }
                return false;
            }

            string commit = TextBox(fieldName + "#num", value.ToString(), GUILayout.Width(56));
            EditorGUILayout.EndHorizontal();

            if (commit == null) return false;

            int parsed;
            if (!int.TryParse(commit, out parsed)) return false;
            if (parsed == value) return false;

            value = parsed;
            Changed = true;
            return true;
        }

        /// <summary>Two focus-out number boxes on one row.</summary>
        public bool Vector2Field(string label, string fieldName, ref Vector2 value, string tooltip)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            bool changed = false;
            Vector2 v = value;

            GUILayout.Label("X", EditorStyles.miniLabel, GUILayout.Width(12));
            string cx = TextBox(fieldName + "#x", v.x.ToString("0.###"));
            GUILayout.Label("Y", EditorStyles.miniLabel, GUILayout.Width(12));
            string cy = TextBox(fieldName + "#y", v.y.ToString("0.###"));

            EditorGUILayout.EndHorizontal();

            float parsed;
            if (cx != null && float.TryParse(cx, out parsed) && !Mathf.Approximately(parsed, v.x))
            { v.x = parsed; changed = true; }
            if (cy != null && float.TryParse(cy, out parsed) && !Mathf.Approximately(parsed, v.y))
            { v.y = parsed; changed = true; }

            if (changed) { value = v; Changed = true; }
            return changed;
        }

        public bool Vector3Field(string label, string fieldName, ref Vector3 value, string tooltip)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));

            bool changed = false;
            Vector3 v = value;

            GUILayout.Label("X", EditorStyles.miniLabel, GUILayout.Width(12));
            string cx = TextBox(fieldName + "#x", v.x.ToString("0.###"));
            GUILayout.Label("Y", EditorStyles.miniLabel, GUILayout.Width(12));
            string cy = TextBox(fieldName + "#y", v.y.ToString("0.###"));
            GUILayout.Label("Z", EditorStyles.miniLabel, GUILayout.Width(12));
            string cz = TextBox(fieldName + "#z", v.z.ToString("0.###"));

            EditorGUILayout.EndHorizontal();

            float parsed;
            if (cx != null && float.TryParse(cx, out parsed) && !Mathf.Approximately(parsed, v.x))
            { v.x = parsed; changed = true; }
            if (cy != null && float.TryParse(cy, out parsed) && !Mathf.Approximately(parsed, v.y))
            { v.y = parsed; changed = true; }
            if (cz != null && float.TryParse(cz, out parsed) && !Mathf.Approximately(parsed, v.z))
            { v.z = parsed; changed = true; }

            if (changed) { value = v; Changed = true; }
            return changed;
        }

        /// <summary>Free-text string field, focus-out committed.</summary>
        public bool StringField(string label, string fieldName, ref string value, string tooltip)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));
            string commit = TextBox(fieldName, value ?? "");
            EditorGUILayout.EndHorizontal();

            if (commit == null || commit == value) return false;

            value = commit;
            Changed = true;
            return true;
        }

        // ------------------------------------------------------------------
        //  Immediate controls
        //
        //  A toggle, dropdown or object slot is one discrete decision, not a
        //  stream of intermediate states, so there is nothing to defer.
        // ------------------------------------------------------------------

        public bool Toggle(string label, ref bool value, string tooltip)
        {
            bool next = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (next == value) return false;

            value = next;
            Changed = true;
            return true;
        }

        public bool EnumField(string label, ref System.Enum value, string tooltip)
        {
            System.Enum next = EditorGUILayout.EnumPopup(new GUIContent(label, tooltip), value);
            if (Equals(next, value)) return false;

            value = next;
            Changed = true;
            return true;
        }

        public bool ColorField(string label, ref Color value, string tooltip)
        {
            Color next = EditorGUILayout.ColorField(new GUIContent(label, tooltip), value);
            if (next == value) return false;

            value = next;
            Changed = true;
            return true;
        }

        public bool ObjectField(string label, ref Object value, System.Type type, string tooltip)
        {
            Object next = EditorGUILayout.ObjectField(new GUIContent(label, tooltip), value, type, false);
            if (next == value) return false;

            value = next;
            Changed = true;
            return true;
        }

        /// <summary>
        /// One RangeFilter band: an enable toggle, then min/max/falloff/invert
        /// indented beneath it.
        ///
        /// The sub-fields only draw when the band is enabled. A disabled band
        /// contributes a weight of 1 to everything, so its numbers have no effect,
        /// and showing four inert boxes per band would triple the height of a
        /// composite filter node for no information.
        ///
        /// Deliberately number boxes rather than sliders: each band filters a
        /// different quantity - normalised height is 0-1, slope is 0-90 degrees,
        /// aspect is 0-360 - and this control cannot tell which, so a single
        /// assumed span would be wrong for two of the three.
        /// </summary>
        public bool RangeFilterField(string label, string fieldName,
                                     ref UniverseCreator.RangeFilter value, string tooltip)
        {
            bool changed = false;
            UniverseCreator.RangeFilter v = value;

            bool enabled = v.enabled;
            if (Toggle(label, ref enabled, tooltip)) { v.enabled = enabled; changed = true; }

            if (v.enabled)
            {
                EditorGUI.indentLevel++;

                float min = v.min;
                if (FloatField("Min", fieldName + "#min", ref min, "Lower bound of the accepted band."))
                { v.min = min; changed = true; }

                float max = v.max;
                if (FloatField("Max", fieldName + "#max", ref max, "Upper bound of the accepted band."))
                { v.max = max; changed = true; }

                float falloff = v.falloff;
                if (FloatField("Falloff", fieldName + "#fall", ref falloff,
                               "Fade width outside each bound, in the same unit as Min/Max. 0 is a hard cut."))
                { v.falloff = falloff; changed = true; }

                bool invert = v.invert;
                if (Toggle("Invert", ref invert, "Turns the band into an exclusion zone."))
                { v.invert = invert; changed = true; }

                EditorGUI.indentLevel--;
            }

            if (changed) { value = v; Changed = true; }
            return changed;
        }

        public bool CurveField(string label, ref AnimationCurve value, string tooltip)
        {
            AnimationCurve current = value ?? AnimationCurve.Linear(0, 0, 1, 1);

            EditorGUI.BeginChangeCheck();
            AnimationCurve next = EditorGUILayout.CurveField(new GUIContent(label, tooltip), current);
            if (!EditorGUI.EndChangeCheck()) return false;

            value = next;
            Changed = true;
            return true;
        }
    }
}
