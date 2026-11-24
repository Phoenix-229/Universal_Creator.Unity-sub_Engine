// -----------------------------------------------------------------------------
//  Universe Creator - Geometry, density and streaming
//
//  File:    GeometryDebugWindow.cs
//  Purpose: The support surface for this whole section. Every budget number in § 9.12
//           is derived from a law, so every number has to be readable on the machine
//           that is actually running - otherwise a question like "why did this drop to
//           Mobile Low on a flagship" has no answer but a guess. This window is that
//           answer: six panels reading the live snapshot the geometry systems publish,
//           laid out with the pure GeometryDebugView so the window itself only draws.
//
//  IT DRAWS, IT DOES NOT MEASURE
//  -----------------------------
//  The window computes nothing about the world. It reads GeometryDiagnostics.Latest -
//  filled by the scene layer while the game is PLAYING - and turns it into rows through
//  GeometryDebugView. In edit mode, before any snapshot has been published, it shows a
//  help box and no numbers, so it never invents a reading the running scene did not
//  produce and never runs simulation outside play.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>
    /// The geometry diagnostics window: six panels (derived distances, micro-relief activity, splat cache,
    /// impostor atlas, budget &amp; thermal, terrain writes) drawn from the live
    /// <see cref="GeometryDiagnostics.Latest"/> snapshot via the pure <see cref="GeometryDebugView"/>. Reads and
    /// draws only; all numbers originate in the runtime systems, so nothing here can disagree with them.
    /// </summary>
    public class GeometryDebugWindow : EditorWindow
    {
        private Vector2 _scroll;
        private readonly bool[] _open = { true, true, true, true, true, true };

        private static readonly Color BarBack = new Color(0.16f, 0.16f, 0.16f, 1f);
        private static readonly Color BarFill = new Color(0.30f, 0.62f, 0.86f, 1f);
        private static readonly Color BarAlert = new Color(0.86f, 0.32f, 0.28f, 1f);

        public static void OpenWindow()
        {
            var window = GetWindow<GeometryDebugWindow>(false, "Geometry Debug");
            window.minSize = new Vector2(360f, 440f);
            window.Show();
        }

        // Repaint ~10x/sec so a playing scene's live numbers tick without a manual focus.
        private void OnInspectorUpdate()
        {
            if (GeometryDiagnostics.HasData) Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Geometry Diagnostics", EditorStyles.boldLabel);

            var s = GeometryDiagnostics.Latest;
            if (s == null)
            {
                EditorGUILayout.HelpBox(
                    "No live geometry data. Enter play mode — the geometry systems publish a snapshot each frame.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                s.tierName + "   •   target " + s.targetFrameRate + " fps   •   frame " + s.frameCount,
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPanel(0, GeometryDebugView.DistanceModel(s));
            DrawPanel(1, GeometryDebugView.MicroRelief(s));
            DrawPanel(2, GeometryDebugView.SplatCache(s));
            DrawPanel(3, GeometryDebugView.ImpostorAtlas(s));
            DrawPanel(4, GeometryDebugView.Budget(s));
            if (_open[4]) DrawHistory(s.frameMsHistory, s.frameMsCeiling);
            DrawPanel(5, GeometryDebugView.TerrainWrites(s));
            EditorGUILayout.EndScrollView();
        }

        private void DrawPanel(int index, GeometryPanelRow[] rows)
        {
            _open[index] = EditorGUILayout.Foldout(_open[index], GeometryDebugView.PanelTitles[index], true);
            if (!_open[index]) return;

            EditorGUI.indentLevel++;
            for (int i = 0; i < rows.Length; i++) DrawRow(rows[i]);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }

        private void DrawRow(GeometryPanelRow row)
        {
            var line = EditorGUILayout.GetControlRect();
            float labelW = EditorGUIUtility.labelWidth;
            var labelRect = new Rect(line.x, line.y, labelW, line.height);
            var valueRect = new Rect(line.x + labelW, line.y, line.width - labelW, line.height);

            var prev = GUI.color;
            if (row.alert) GUI.color = BarAlert;
            EditorGUI.LabelField(labelRect, row.label);
            GUI.color = prev;

            if (row.bar01 < 0f)
            {
                EditorGUI.LabelField(valueRect, row.value);
            }
            else
            {
                // A slim bar behind the value: fill proportional to the ratio, red when the row alerts.
                EditorGUI.DrawRect(valueRect, BarBack);
                var fill = new Rect(valueRect.x, valueRect.y, valueRect.width * Mathf.Clamp01(row.bar01), valueRect.height);
                EditorGUI.DrawRect(fill, row.alert ? BarAlert : BarFill);
                var centered = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
                EditorGUI.LabelField(valueRect, row.value, centered);
            }
        }

        // The frame-time history sparkline for the budget panel: one column per sample, height scaled to the
        // ceiling, columns over the ceiling drawn red. Purely a redraw of the snapshot's ring — no measurement.
        private void DrawHistory(float[] history, float ceilingMs)
        {
            if (history == null || history.Length == 0) return;
            var area = EditorGUILayout.GetControlRect(false, 40f);
            EditorGUI.indentLevel++;
            var indented = EditorGUI.IndentedRect(area);
            EditorGUI.indentLevel--;
            EditorGUI.DrawRect(indented, BarBack);

            float scale = ceilingMs > 0f ? ceilingMs : 1f;
            float colW = indented.width / history.Length;
            for (int i = 0; i < history.Length; i++)
            {
                float f = Mathf.Clamp01(history[i] / scale);
                float hgt = f * indented.height;
                var col = new Rect(indented.x + i * colW, indented.yMax - hgt, Mathf.Max(1f, colW - 1f), hgt);
                EditorGUI.DrawRect(col, history[i] > ceilingMs && ceilingMs > 0f ? BarAlert : BarFill);
            }
        }
    }
}
