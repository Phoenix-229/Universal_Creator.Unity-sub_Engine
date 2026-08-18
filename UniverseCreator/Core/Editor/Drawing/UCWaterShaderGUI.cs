// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterShaderGUI.cs
//  Purpose: Material inspector for UniverseCreator/Water.
//
//  Same visual language as UCTerrainShaderGUI - coloured header bars over an
//  indented body, sections in the order the fragment shader applies them, so
//  reading the inspector top to bottom walks the same path a pixel takes. A user
//  who has learned the terrain material inspector already knows this one.
//
//  The section order is not cosmetic. Depth drives the colour ramp, the ramp is
//  what refraction tints, foam sits on top of that, and opacity is computed last
//  from all three. Listing foam above depth would make the depth controls look
//  like they had no effect on foam, when in fact they set the band foam appears in.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UniverseCreator.Editor.Drawing
{
    public class UCWaterShaderGUI : ShaderGUI
    {
        private static readonly Dictionary<string, bool> expanded = new Dictionary<string, bool>();

        // Deep cyan, matching NodeCategoryColors for the Water category so a water
        // material and a water node read as the same subsystem.
        private static readonly Color HeaderTint = new Color(0.18f, 0.55f, 0.72f);

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
        {
            Material material = editor.target as Material;
            if (material == null) return;

            DrawPipelineWarning();

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "Water is drawn by how much of it the view ray passes through. Depth " +
                "Fade sets that scale in metres, and everything below reads from it - " +
                "the colour ramp, the opacity, the refraction and the shoreline foam.",
                MessageType.None);
            EditorGUILayout.Space(4);

            if (Section("Colour and Depth", "water_col", true))
            {
                Prop(editor, props, "_ShallowColor", "Shallow Colour",
                    "Colour where the water is thin. Its alpha is the opacity there.");
                Prop(editor, props, "_DeepColor", "Deep Colour",
                    "Colour the water reaches at full depth. Its alpha is the opacity there.");
                Prop(editor, props, "_DepthFadeMetres", "Depth Fade (m)",
                    "Metres of water needed to reach the deep colour. Absorption is " +
                    "exponential, so this is a scale, not a hard cut-off.");
                Prop(editor, props, "_Transparency", "Transparency",
                    "How much of the water's own colour replaces what is behind it. " +
                    "0 is glass, 1 is opaque paint at full depth.");
                Prop(editor, props, "_Refraction", "Refraction",
                    "Screen-space bend of what is seen through the surface. Scaled " +
                    "down automatically in shallow water so shorelines do not halo.");
                Prop(editor, props, "_EdgeSoftenMetres", "Shoreline Soften (m)",
                    "Water fades out over this depth at the bank, so the surface does " +
                    "not end on a hard line where it meets the ground.");
            }

            if (Section("Surface and Lighting", "water_lit", true))
            {
                Prop(editor, props, "_Smoothness", "Smoothness",
                    "0.9 and above is calm water. Lower it for a wind-roughened surface.");
                Prop(editor, props, "_FresnelPower", "Fresnel Power",
                    "How quickly the surface turns from clear to mirror as the view " +
                    "angle flattens. 5 is close to real water.");
                Prop(editor, props, "_SpecularTint", "Specular Tint");
            }

            if (Section("Waves", "water_wave", true))
            {
                Prop(editor, props, "_WaveNormalMap", "Wave Normal");
                Prop(editor, props, "_WaveTileMetres", "Wave Tile (m)",
                    "World size of one normal map tile. Larger reads as open ocean, " +
                    "smaller as a pond.");
                Prop(editor, props, "_NormalScale", "Normal Strength");
                Prop(editor, props, "_SecondaryScale", "Secondary Tile Ratio",
                    "The second wave layer's tile size relative to the first. Keep it " +
                    "off simple ratios or the two beat visibly.");

                EditorGUILayout.Space(2);
                Prop(editor, props, "_WaveHeight", "Vertex Wave Height (m)",
                    "Vertical displacement of the mesh. Buoyancy mirrors this exactly, " +
                    "so raising it makes floating objects rise with the swell.");
                Prop(editor, props, "_WaveLength", "Vertex Wave Length (m)");
                Prop(editor, props, "_WaveSpeed", "Vertex Wave Speed");

                if (material.HasProperty("_WaveHeight") && material.GetFloat("_WaveHeight") > 0.6f)
                    EditorGUILayout.HelpBox(
                        "Above about 0.6m the surface can lift clear of a shallow bank. " +
                        "Raise Shoreline Soften with it, or use a deeper bed.",
                        MessageType.Info);
            }

            if (Section("Flow", "water_flow", true))
            {
                Prop(editor, props, "_UseVertexFlow", "Use Mesh Flow",
                    "On, the surface follows the per-vertex flow direction the mesh " +
                    "carries - which is what makes a river run downhill and turn with " +
                    "the channel. Off, everything drifts uniformly.");
                Prop(editor, props, "_FlowSpeed", "Uniform Flow (m/s XZ)",
                    "Used when mesh flow is off, and as the fallback on a mesh that " +
                    "carries no flow data.");
                Prop(editor, props, "_FlowStrength", "Mesh Flow Scale");
            }

            if (Section("Foam", "water_foam", false, material, "_FoamEnabled"))
            {
                Prop(editor, props, "_FoamEnabled", "Foam");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_FoamEnabled")))
                {
                    Prop(editor, props, "_FoamTexture", "Foam Texture");
                    Prop(editor, props, "_FoamColor", "Foam Colour");
                    Prop(editor, props, "_FoamIntensity", "Intensity");
                    Prop(editor, props, "_FoamEdgeWidth", "Edge Width (m)",
                        "Depth over which shoreline foam appears. This is where waves " +
                        "break, so it should be about the height of the swell.");
                    Prop(editor, props, "_FoamScrollSpeed", "Scroll");
                    Prop(editor, props, "_FoamTileMetres", "Tile (m)");
                    Prop(editor, props, "_FlowFoam", "Whitewater From Flow",
                        "Turns fast-flowing water white. This is what makes rapids read " +
                        "as rapids rather than as a fast blue ribbon.");
                }
            }

            if (Section("Caustics", "water_caust", false, material, "_CausticsEnabled"))
            {
                Prop(editor, props, "_CausticsEnabled", "Caustics");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_CausticsEnabled")))
                {
                    Prop(editor, props, "_CausticsTexture", "Caustics Texture");
                    Prop(editor, props, "_CausticsColor", "Colour");
                    Prop(editor, props, "_CausticsIntensity", "Intensity");
                    Prop(editor, props, "_CausticsTileMetres", "Tile (m)");
                    Prop(editor, props, "_CausticsSpeed", "Speed");
                    Prop(editor, props, "_CausticsDepthFade", "Depth Fade (m)",
                        "Caustics are light focused onto the bed, so they fade out as " +
                        "the bed gets further away.");
                }
            }

            if (Section("Interactive Ripples", "water_ripple", false))
            {
                bool hasSim = TerrainSystem.UCWaterRippleSim.Active != null;

                if (!hasSim)
                    EditorGUILayout.HelpBox(
                        "No Water Ripple Simulation is active in the scene. These " +
                        "controls do nothing until one exists. Add the Water Ripple " +
                        "Simulation component to any GameObject - one per scene drives " +
                        "every water body.",
                        MessageType.Info);

                using (new EditorGUI.DisabledScope(!hasSim))
                {
                    Prop(editor, props, "_RippleStrength", "Ripple Strength",
                        "How strongly this body responds to splashes, wakes and rain. " +
                        "Per material on purpose: a footstep is a dramatic event in a " +
                        "puddle and invisible in an ocean swell.");
                    Prop(editor, props, "_RippleFoam", "Ripple Foam",
                        "Whiteness produced where the simulated surface is moving " +
                        "fastest - splash rings, boat wakes, the patch under a waterfall.");
                }
            }

            EditorGUILayout.Space(6);
            editor.RenderQueueField();
            editor.DoubleSidedGIField();
        }

        // ------------------------------------------------------------------
        //  Pipeline check
        // ------------------------------------------------------------------

        // Reads the URP asset every repaint rather than caching. The user can
        // switch pipeline assets or untick the box at any moment, and a cached
        // answer would keep telling them the water is fine while it is not.
        private static void DrawPipelineWarning()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null)
            {
                EditorGUILayout.HelpBox(
                    "No Universal Render Pipeline asset is active. This shader is URP " +
                    "only and will not render.", MessageType.Error);
                return;
            }

            bool depth = asset.supportsCameraDepthTexture;
            bool opaque = asset.supportsCameraOpaqueTexture;

            if (depth && opaque) return;

            string missing = !depth && !opaque ? "Depth Texture and Opaque Texture"
                           : !depth ? "Depth Texture" : "Opaque Texture";

            EditorGUILayout.HelpBox(
                missing + " is disabled on the active URP asset. Without it the water " +
                "renders as a flat sheet with no shoreline, refraction or caustics.",
                MessageType.Warning);

            if (GUILayout.Button("Enable on URP Asset"))
                Utilities.UCWaterApplier.EnsurePipelineFeatures();

            EditorGUILayout.Space(4);
        }

        // ------------------------------------------------------------------
        //  Layout
        // ------------------------------------------------------------------

        private static bool Section(string title, string key, bool defaultOpen = true,
                                    Material material = null, string enableProp = null)
        {
            bool open;
            if (!expanded.TryGetValue(key, out open)) open = defaultOpen;

            Rect r = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));

            Color bg = EditorGUIUtility.isProSkin
                ? new Color(HeaderTint.r * 0.42f, HeaderTint.g * 0.42f, HeaderTint.b * 0.42f)
                : new Color(HeaderTint.r * 0.82f, HeaderTint.g * 0.82f, HeaderTint.b * 0.82f);
            EditorGUI.DrawRect(r, bg);

            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f),
                new Color(1f, 1f, 1f, EditorGUIUtility.isProSkin ? 0.10f : 0.35f));

            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;

            EditorGUI.LabelField(new Rect(r.x + 18f, r.y + 2f, r.width - 70f, 18f), title, style);
            EditorGUI.LabelField(new Rect(r.x + 4f, r.y + 3f, 14f, 14f), open ? "▼" : "▶", style);

            if (material != null && !string.IsNullOrEmpty(enableProp) &&
                material.HasProperty(enableProp))
            {
                bool on = material.GetFloat(enableProp) > 0.5f;

                var pill = new Rect(r.xMax - 44f, r.y + 5f, 36f, 13f);
                EditorGUI.DrawRect(pill, on
                    ? new Color(0.36f, 0.72f, 0.42f, 1f)
                    : new Color(0f, 0f, 0f, EditorGUIUtility.isProSkin ? 0.22f : 0.10f));

                var pillStyle = new GUIStyle(EditorStyles.miniLabel);
                pillStyle.alignment = TextAnchor.MiddleCenter;
                pillStyle.normal.textColor = on
                    ? Color.white
                    : new Color(1f, 1f, 1f, EditorGUIUtility.isProSkin ? 0.45f : 0.6f);
                EditorGUI.LabelField(pill, on ? "ON" : "OFF", pillStyle);
            }

            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            {
                open = !open;
                expanded[key] = open;
                Event.current.Use();
            }
            else expanded[key] = open;

            if (open) EditorGUILayout.Space(2);
            return open;
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

        private static bool IsOn(Material material, string prop)
        {
            return material.HasProperty(prop) && material.GetFloat(prop) > 0.5f;
        }
    }
}
