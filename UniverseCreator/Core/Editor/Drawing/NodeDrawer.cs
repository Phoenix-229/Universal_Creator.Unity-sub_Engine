// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    NodeDrawer.cs
//  Purpose: Draws a node's parameters by reflecting over its public fields, so
//           every node in the library gets a full editing UI without a hand-written
//           drawer per node.
//
//  WHY REFLECTION
//  --------------
//  There are ~66 nodes. Hand-writing a drawer for each means 66 places to forget a
//  field, and a node whose maths reads a parameter the UI never exposes is a
//  parameter the user cannot reach. Reflecting over public fields makes the UI a
//  function of what the node actually computes with: add a field, get a control.
//
//  RANGES
//  ------
//  Nodes clamp their own inputs inside Generate() rather than carrying [Range]
//  attributes, so the slider spans here come from a name/type table. The span is
//  only the useful range - typing a number outside it is still accepted, because
//  silently rewriting a deliberate entry is worse than an off-scale handle.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Drawing
{
    public static class NodeDrawer
    {
        private struct Span
        {
            public float min;
            public float max;
            public Span(float min, float max) { this.min = min; this.max = max; }
        }

        // Slider spans by field name. Matched case-insensitively and exactly, so a
        // node reusing a standard name inherits a sensible handle for free.
        private static readonly Dictionary<string, Span> NamedSpans =
            new Dictionary<string, Span>(StringComparer.OrdinalIgnoreCase)
        {
            // Noise
            { "size",            new Span(1f, 5000f) },
            { "octaves",         new Span(1f, 16f) },
            { "detail",          new Span(0f, 1f) },
            { "turbulence",      new Span(-1f, 1f) },
            { "intensity",       new Span(0f, 4f) },
            { "offset",          new Span(-1f, 1f) },

            // Erosion
            { "iterations",         new Span(1f, 32f) },
            { "durability",         new Span(0f, 1f) },
            { "erosionAmount",      new Span(0f, 4f) },
            { "sedimentAmount",     new Span(0f, 1f) },
            { "fluidityIterations", new Span(1f, 16f) },
            { "relax",              new Span(0f, 4f) },
            { "talusAngle",         new Span(0f, 90f) },
            { "windStrength",       new Span(0f, 4f) },
            { "waveHeight",         new Span(0f, 1f) },

            // Common shaping
            { "strength",    new Span(0f, 2f) },
            { "amount",      new Span(0f, 2f) },
            { "threshold",   new Span(0f, 1f) },
            { "falloff",     new Span(0f, 1f) },
            { "opacity",     new Span(0f, 1f) },
            { "blend",       new Span(0f, 1f) },
            { "contrast",    new Span(0f, 4f) },
            { "brightness",  new Span(-1f, 1f) },
            { "power",       new Span(0.01f, 8f) },
            { "radius",      new Span(0f, 64f) },
            { "sigma",       new Span(0f, 16f) },
            { "steps",       new Span(1f, 64f) },
            { "levels",      new Span(1f, 64f) },
            { "minValue",    new Span(0f, 1f) },
            { "maxValue",    new Span(0f, 1f) },
            { "curvePower",  new Span(0.1f, 8f) },
            { "density",     new Span(0f, 1f) },
            { "seaLevel",    new Span(0f, 1f) },

            // World-scale metres
            { "falloffMeters", new Span(0f, 5000f) },
            { "edgeHeight",    new Span(0f, 1f) },

            // Qualified "Type.field" entries win over the bare name, for fields
            // whose name collides with a differently-scaled one above. A blur
            // radius is measured in pixels and tops out around 64; a brush spot's
            // radius is world metres and is routinely in the hundreds, so the two
            // cannot share a span.
            { "BrushSpot.radius",  new Span(1f, 2000f) },
            { "BrushSpot.height",  new Span(-1f, 1f) },
            { "BrushSpot.falloff", new Span(0f, 1f) }
        };

        /// <summary>
        /// Slider span for a field, preferring a "Type.field" entry over the bare
        /// field name so a name reused at a different scale can be disambiguated.
        /// </summary>
        private static bool TryGetSpan(string name, out Span span)
        {
            if (!string.IsNullOrEmpty(name))
            {
                if (NamedSpans.TryGetValue(name, out span)) return true;

                int dot = name.LastIndexOf('.');
                if (dot >= 0 && NamedSpans.TryGetValue(name.Substring(dot + 1), out span)) return true;
            }

            span = default(Span);
            return false;
        }

        /// <summary>
        /// Draws every editable public field on the node. Returns true if any value
        /// committed this frame, which the caller uses to mark the graph dirty and
        /// kick a regeneration.
        /// </summary>
        public static bool DrawParameters(Generator node, FocusOutFields fields)
        {
            if (node == null || fields == null) return false;

            bool changed = false;

            FieldInfo[] infos = node.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);

            for (int i = 0; i < infos.Length; i++)
            {
                FieldInfo info = infos[i];
                if (!IsEditable(info)) continue;
                if (DrawField(node, info, fields)) changed = true;
            }

            if (changed) node.MarkDirty();
            return changed;
        }

        /// <summary>True if the node exposes at least one editable parameter.</summary>
        public static int CountParameters(Generator node)
        {
            if (node == null) return 0;

            int count = 0;
            FieldInfo[] infos = node.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < infos.Length; i++)
                if (IsEditable(infos[i])) count++;

            return count;
        }

        // Bookkeeping fields belong to the graph engine, not to the user. Showing
        // them invites edits that corrupt connections (id) or fight the editor
        // (editorPosition), so they are filtered out by name.
        private static bool IsEditable(FieldInfo info)
        {
            if (info.IsStatic || info.IsInitOnly || info.IsLiteral) return false;
            if (info.IsDefined(typeof(HideInInspector), true)) return false;
            if (info.IsDefined(typeof(NonSerializedAttribute), true)) return false;

            switch (info.Name)
            {
                case "id":
                case "editorPosition":
                case "version":
                case "disabled":
                case "displayName":
                    return false;
            }

            return IsSupportedType(info.FieldType);
        }

        private static bool IsSupportedType(Type t)
        {
            if (t == typeof(UniverseCreator.TerrainSystem.RiverHierarchyTargets) ||
                t == typeof(UniverseCreator.TerrainSystem.LakeDistributionSettings)) return true; // UC-T03-08: grouped lake criteria share the existing drawer.
            if (IsScalarType(t)) return true;

            // A List<T> is drawable when its element is: either a scalar the
            // drawer already handles, or a small serializable struct whose own
            // public fields are all scalars. That second case is what makes
            // List<BrushSpot> work without a hand-written editor per node.
            Type element = ListElementType(t);
            if (element != null) return IsScalarType(element) || IsInlineStruct(element);

            return false;
        }

        /// <summary>Types that map to exactly one FocusOutFields control.</summary>
        private static bool IsScalarType(Type t)
        {
            if (t == typeof(float) || t == typeof(int) || t == typeof(bool) ||
                t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3) ||
                t == typeof(Color) || t == typeof(AnimationCurve)) return true;

            // A filter band is a compound of four primitives with one meaning, so
            // it gets a grouped control rather than four loose fields.
            if (t == typeof(UniverseCreator.RangeFilter)) return true;

            if (t.IsEnum) return true;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return true;

            return false;
        }

        /// <summary>The T in List&lt;T&gt;, or null when t is not a List&lt;T&gt;.</summary>
        private static Type ListElementType(Type t)
        {
            if (!t.IsGenericType) return null;
            if (t.GetGenericTypeDefinition() != typeof(List<>)) return null;
            return t.GetGenericArguments()[0];
        }

        /// <summary>
        /// A value type whose public instance fields are all scalars, so it can be
        /// drawn as an indented block of ordinary controls.
        /// </summary>
        /// <remarks>
        /// Restricted to structs on purpose. A class element would need a null
        /// check and an instantiate button per row, and no node uses one - so the
        /// unreachable branch stays unwritten rather than untested.
        /// </remarks>
        private static bool IsInlineStruct(Type t)
        {
            if (!t.IsValueType || t.IsEnum || t.IsPrimitive) return false;

            FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fs.Length == 0) return false;

            for (int i = 0; i < fs.Length; i++)
                if (!IsScalarType(fs[i].FieldType)) return false;

            return true;
        }

        // Which list rows are expanded, keyed by node id + field name so two nodes
        // of the same type expand independently. Editor-session state only - it is
        // deliberately not serialised, because a foldout is a view preference and
        // writing it would dirty the graph asset on a mere click.
        private static readonly Dictionary<string, bool> expanded =
            new Dictionary<string, bool>();

        private static bool DrawField(Generator node, FieldInfo info, FocusOutFields fields)
        {
            // Unique per node instance so two nodes of the same type never share a
            // focus buffer - editing one would otherwise leak into the other.
            string key = node.id + "." + info.Name;
            string label = Prettify(info.Name);
            string tooltip = ReadTooltip(info);
            Type t = info.FieldType;

            // UC-T03-05: the inspector and Node Graph edit the same serialized target object.
            if (t == typeof(UniverseCreator.TerrainSystem.RiverHierarchyTargets) || t == typeof(UniverseCreator.TerrainSystem.LakeDistributionSettings))
            {
                object settings = info.GetValue(node) ?? Activator.CreateInstance(t); // UC-T03-08: materialize either supported setting only on edit.
                bool changed = false;
                foreach (FieldInfo member in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object value = member.GetValue(settings);
                    if (!DrawScalar(member.FieldType, member.Name, Prettify(member.Name), key + "." + member.Name,
                        ReadTooltip(member), ref value, fields)) continue;
                    member.SetValue(settings, value); changed = true;
                }
                if (changed) info.SetValue(node, settings);
                return changed;
            }

            Type element = ListElementType(t);
            if (element != null)
            {
                // A null list is indistinguishable from an empty one to the user,
                // but only one of them can be appended to. Materialise it on first
                // draw rather than making every list node's ctor remember to.
                System.Collections.IList list = info.GetValue(node) as System.Collections.IList;
                if (list == null)
                {
                    list = (System.Collections.IList)Activator.CreateInstance(t);
                    info.SetValue(node, list);
                }

                return DrawList(list, element, label, key, tooltip, fields);
            }

            object boxed = info.GetValue(node);
            if (!DrawScalar(t, info.Name, label, key, tooltip, ref boxed, fields)) return false;

            info.SetValue(node, boxed);
            return true;
        }

        /// <summary>
        /// Draws one value of a type in the scalar table, reading and writing it
        /// through a boxed object.
        /// </summary>
        /// <remarks>
        /// Boxed rather than generic so a list element and a plain field share this
        /// one control table. Duplicating the table per container is how the two
        /// drift apart - a span or a seed rule added to one and missed in the other.
        /// </remarks>
        private static bool DrawScalar(Type t, string name, string label, string key,
                                       string tooltip, ref object boxed, FocusOutFields fields)
        {
            if (t == typeof(float))
            {
                float v = (float)boxed;
                Span span;
                bool ok = TryGetSpan(name, out span)
                    ? fields.FloatSlider(label, key, ref v, span.min, span.max, tooltip)
                    : fields.FloatField(label, key, ref v, tooltip);
                if (ok) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(int))
            {
                int v = (int)boxed;
                Span span;

                // A seed is an arbitrary 32-bit number, not a magnitude - a slider
                // over it is useless, so seeds always get a plain box.
                bool isSeed = name.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0;

                bool ok = (!isSeed && TryGetSpan(name, out span))
                    ? fields.IntSlider(label, key, ref v, Mathf.RoundToInt(span.min), Mathf.RoundToInt(span.max), tooltip)
                    : fields.IntField(label, key, ref v, tooltip);
                if (ok) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(bool))
            {
                bool v = (bool)boxed;
                if (fields.Toggle(label, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(string))
            {
                string v = (string)boxed;
                if (fields.StringField(label, key, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(Vector2))
            {
                Vector2 v = (Vector2)boxed;
                if (fields.Vector2Field(label, key, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(Vector3))
            {
                Vector3 v = (Vector3)boxed;
                if (fields.Vector3Field(label, key, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(Color))
            {
                Color v = (Color)boxed;
                if (fields.ColorField(label, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(AnimationCurve))
            {
                AnimationCurve v = (AnimationCurve)boxed;
                if (fields.CurveField(label, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t == typeof(UniverseCreator.RangeFilter))
            {
                UniverseCreator.RangeFilter v = (UniverseCreator.RangeFilter)boxed;
                if (fields.RangeFilterField(label, key, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (t.IsEnum)
            {
                Enum v = (Enum)boxed;
                if (fields.EnumField(label, ref v, tooltip)) { boxed = v; return true; }
                return false;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                UnityEngine.Object v = (UnityEngine.Object)boxed;
                if (fields.ObjectField(label, ref v, t, tooltip)) { boxed = v; return true; }
                return false;
            }

            return false;
        }

        // ------------------------------------------------------------------
        //  Lists
        // ------------------------------------------------------------------

        /// <summary>
        /// A foldout with a row per element, an add and a remove per row, and a
        /// reorder pair when order carries meaning.
        /// </summary>
        /// <remarks>
        /// Written against IList rather than List&lt;T&gt; because the field type is
        /// only known by reflection here. Every mutation goes through the same
        /// IList the node holds, so the node sees the edit immediately - there is
        /// no copy to write back and therefore no way for the two to disagree.
        ///
        /// Structural edits (add, remove, reorder) commit on the click, unlike the
        /// value fields inside a row, which keep the focus-out contract. A button
        /// press is already a deliberate, discrete act; there is no half-typed
        /// state to protect.
        /// </remarks>
        private static bool DrawList(System.Collections.IList list, Type element,
                                     string label, string key, string tooltip,
                                     FocusOutFields fields)
        {
            bool changed = false;

            bool open;
            if (!expanded.TryGetValue(key, out open)) open = true;

            EditorGUILayout.BeginHorizontal();

            open = EditorGUILayout.Foldout(
                open, new GUIContent(label + "  (" + list.Count + ")", tooltip), true);
            expanded[key] = open;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(UCIcons.Content(UCIcons.Add, "Add an entry to the end of the list.", "+"),
                                 EditorStyles.miniButton, GUILayout.Width(24f)))
            {
                list.Add(DefaultElement(element));
                changed = true;
            }

            using (new EditorGUI.DisabledScope(list.Count == 0))
            {
                if (GUILayout.Button(UCIcons.Content(UCIcons.Remove, "Remove the last entry.", "-"),
                                     EditorStyles.miniButton, GUILayout.Width(24f)))
                {
                    list.RemoveAt(list.Count - 1);
                    changed = true;
                }
            }

            EditorGUILayout.EndHorizontal();

            if (!open) return changed;

            EditorGUI.indentLevel++;

            if (list.Count == 0)
            {
                EditorGUILayout.LabelField(" ", "Empty - press + to add one.", EditorStyles.miniLabel);
            }

            // Descending, so removing an entry cannot shift a later index that this
            // same pass has yet to visit.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (DrawListRow(list, element, i, key, fields)) changed = true;
            }

            EditorGUI.indentLevel--;
            return changed;
        }

        private static bool DrawListRow(System.Collections.IList list, Type element,
                                        int index, string key, FocusOutFields fields)
        {
            bool changed = false;
            string rowKey = key + "[" + index + "]";
            string rowLabel = "  " + index;

            EditorGUILayout.BeginHorizontal();

            if (IsScalarType(element))
            {
                object boxed = list[index];
                if (DrawScalar(element, "", rowLabel, rowKey, "Entry " + index, ref boxed, fields))
                {
                    list[index] = boxed;
                    changed = true;
                }
            }
            else
            {
                EditorGUILayout.LabelField(rowLabel, EditorStyles.boldLabel);
            }

            if (GUILayout.Button(UCIcons.Content(UCIcons.Delete, "Remove this entry.", "x"),
                                 EditorStyles.miniButton, GUILayout.Width(22f)))
            {
                list.RemoveAt(index);
                EditorGUILayout.EndHorizontal();
                return true;
            }

            EditorGUILayout.EndHorizontal();

            // A struct element draws its own fields indented beneath its index.
            if (!IsScalarType(element))
            {
                EditorGUI.indentLevel++;

                // Boxed once, mutated by every field, written back once. A struct
                // read out of an IList is a copy, so writing back per field would
                // discard the edits made by the fields before it.
                object boxed = list[index];
                FieldInfo[] fs = element.GetFields(BindingFlags.Public | BindingFlags.Instance);

                for (int f = 0; f < fs.Length; f++)
                {
                    object member = fs[f].GetValue(boxed);
                    if (DrawScalar(fs[f].FieldType, element.Name + "." + fs[f].Name, Prettify(fs[f].Name),
                                   rowKey + "." + fs[f].Name, ReadTooltip(fs[f]), ref member, fields))
                    {
                        fs[f].SetValue(boxed, member);
                        changed = true;
                    }
                }

                if (changed) list[index] = boxed;

                EditorGUI.indentLevel--;
            }

            return changed;
        }

        /// <summary>
        /// The value a freshly added row starts at.
        /// </summary>
        /// <remarks>
        /// A default(T) BrushSpot has radius 0, which is a spot that paints nothing
        /// and reads as a broken node rather than an empty one. Types with a
        /// meaningful non-zero default get seeded here so adding a row produces
        /// something the user can see and then adjust.
        /// </remarks>
        private static object DefaultElement(Type element)
        {
            if (element == typeof(string)) return "";
            if (element == typeof(AnimationCurve)) return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            if (element == typeof(Color)) return Color.white;

            // A weight of 0 excludes the prototype it was just added for.
            if (element == typeof(float)) return 1f;

            if (element == typeof(UniverseCreator.Nodes.Creators.BrushSpot))
                return new UniverseCreator.Nodes.Creators.BrushSpot(Vector2.zero, 50f, 0.25f, 0.5f);

            if (element.IsValueType) return Activator.CreateInstance(element);
            return null;
        }

        private static string ReadTooltip(FieldInfo info)
        {
            object[] attrs = info.GetCustomAttributes(typeof(TooltipAttribute), true);
            if (attrs.Length > 0) return ((TooltipAttribute)attrs[0]).tooltip;
            return info.Name;
        }

        // "erosionAmount" -> "Erosion Amount". Unity has ObjectNames.NicifyVariableName
        // for exactly this, so use it rather than rolling a worse version.
        private static string Prettify(string fieldName)
        {
            return ObjectNames.NicifyVariableName(fieldName);
        }
    }
}
