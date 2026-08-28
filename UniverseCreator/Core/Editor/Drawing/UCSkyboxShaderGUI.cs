// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UCSkyboxShaderGUI.cs
//  Purpose: Material inspector for UniverseCreator/Skybox.
//
//  The skybox is unusual among the material inspectors: almost everything it draws
//  is published by the lighting controller at runtime, not authored on the material.
//  The sky table, the sun and moon directions, their colours and their angular
//  sizes all arrive as globals each frame. So this inspector holds only the two
//  things a user legitimately sets by hand - which table the material samples and
//  an exposure trim - and spends the rest of its space telling the reader why the
//  material looks black until a controller drives it, rather than leaving them to
//  guess that an empty sky is a bug.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UniverseCreator.Editor.Drawing
{
    public class UCSkyboxShaderGUI : ShaderGUI
    {
        // Warm sky-blue header, distinct from the water and terrain material bars so
        // the sky material reads as its own subsystem at a glance.
        private static readonly Color HeaderTint = new Color(0.36f, 0.52f, 0.78f);

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
        {
            Material material = editor.target as Material;
            if (material == null) return;

            DrawPipelineWarning();

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "This sky is driven by the scene's lighting controller. The sky table, " +
                "the sun and moon and their sizes are published at runtime, so an " +
                "unconfigured material renders black - that is empty, not broken. The " +
                "two fields below are the only ones set by hand.",
                MessageType.None);
            EditorGUILayout.Space(4);

            DrawHeader("Sky Table");
            Prop(editor, props, "_SkyLUT", "Sky Table",
                "The small precomputed table of sky colour the controller bakes and " +
                "binds. Left unset it reads as black; the controller replaces it each " +
                "time the sun or the air changes enough to matter.");
            Prop(editor, props, "_SkyExposure", "Exposure",
                "A plain multiplier on the sampled sky colour. 1 is the table as baked; " +
                "raise or lower it only to trim against the rest of the scene's exposure.");

            EditorGUILayout.Space(6);
            editor.RenderQueueField();
        }

        // Reads the active pipeline every repaint rather than caching - the user can
        // swap the render pipeline asset at any moment, and a stale answer would keep
        // claiming the sky is fine after it had stopped rendering.
        private static void DrawPipelineWarning()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset != null) return;

            EditorGUILayout.HelpBox(
                "No Universal Render Pipeline asset is active. This skybox is URP only " +
                "and will not render.", MessageType.Error);
        }

        private static void DrawHeader(string title)
        {
            Rect r = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));

            Color bg = EditorGUIUtility.isProSkin
                ? new Color(HeaderTint.r * 0.42f, HeaderTint.g * 0.42f, HeaderTint.b * 0.42f)
                : new Color(HeaderTint.r * 0.82f, HeaderTint.g * 0.82f, HeaderTint.b * 0.82f);
            EditorGUI.DrawRect(r, bg);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f),
                new Color(1f, 1f, 1f, EditorGUIUtility.isProSkin ? 0.10f : 0.35f));

            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;
            EditorGUI.LabelField(new Rect(r.x + 8f, r.y + 2f, r.width - 16f, 18f), title, style);

            EditorGUILayout.Space(2);
        }

        private static void Prop(MaterialEditor editor, MaterialProperty[] props,
                                 string name, string label, string tooltip = null)
        {
            MaterialProperty p = FindProperty(name, props, false);
            if (p == null) return;

            EditorGUI.indentLevel++;
            editor.ShaderProperty(p, new GUIContent(label, tooltip));
            EditorGUI.indentLevel--;
        }
    }
}
