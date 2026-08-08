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
            { "edgeHeight",    new Span(0f, 1f) }
        };

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
            if (t == typeof(float) || t == typeof(int) || t == typeof(bool) ||
                t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3) ||
                t == typeof(Color) || t == typeof(AnimationCurve)) return true;

            if (t.IsEnum) return true;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return true;

            // Arrays and lists need bespoke UI; the nodes that use them build their
            // own editors rather than getting a broken generic one.
            return false;
        }

        private static bool DrawField(Generator node, FieldInfo info, FocusOutFields fields)
        {
            // Unique per node instance so two nodes of the same type never share a
            // focus buffer - editing one would otherwise leak into the other.
            string key = node.id + "." + info.Name;
            string label = Prettify(info.Name);
            string tooltip = ReadTooltip(info);
            Type t = info.FieldType;

            if (t == typeof(float))
            {
                float v = (float)info.GetValue(node);
                Span span;
                bool ok = NamedSpans.TryGetValue(info.Name, out span)
                    ? fields.FloatSlider(label, key, ref v, span.min, span.max, tooltip)
                    : fields.FloatField(label, key, ref v, tooltip);
                if (ok) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(int))
            {
                int v = (int)info.GetValue(node);
                Span span;

                // A seed is an arbitrary 32-bit number, not a magnitude - a slider
                // over it is useless, so seeds always get a plain box.
                bool isSeed = info.Name.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0;

                bool ok = (!isSeed && NamedSpans.TryGetValue(info.Name, out span))
                    ? fields.IntSlider(label, key, ref v, Mathf.RoundToInt(span.min), Mathf.RoundToInt(span.max), tooltip)
                    : fields.IntField(label, key, ref v, tooltip);
                if (ok) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(bool))
            {
                bool v = (bool)info.GetValue(node);
                if (fields.Toggle(label, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(string))
            {
                string v = (string)info.GetValue(node);
                if (fields.StringField(label, key, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(Vector2))
            {
                Vector2 v = (Vector2)info.GetValue(node);
                if (fields.Vector2Field(label, key, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(Vector3))
            {
                Vector3 v = (Vector3)info.GetValue(node);
                if (fields.Vector3Field(label, key, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(Color))
            {
                Color v = (Color)info.GetValue(node);
                if (fields.ColorField(label, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t == typeof(AnimationCurve))
            {
                AnimationCurve v = (AnimationCurve)info.GetValue(node);
                if (fields.CurveField(label, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (t.IsEnum)
            {
                Enum v = (Enum)info.GetValue(node);
                if (fields.EnumField(label, ref v, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                UnityEngine.Object v = (UnityEngine.Object)info.GetValue(node);
                if (fields.ObjectField(label, ref v, t, tooltip)) { info.SetValue(node, v); return true; }
                return false;
            }

            return false;
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
