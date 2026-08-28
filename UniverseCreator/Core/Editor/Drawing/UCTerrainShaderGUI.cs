// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    UCTerrainShaderGUI.cs
//  Purpose: Material inspector for UniverseCreator/TerrainLit.
//
//  WHY A CUSTOM GUI
//  ----------------
//  Two reasons, and the second is the load-bearing one.
//
//  1. The default inspector lists thirty-odd properties flat, most of which are
//     engine-written splat slots the user must never touch. Grouping the ones
//     they should touch is the difference between a control surface and a dump.
//
//  2. A [ToggleUI] float does not set a shader keyword. Most UC features are
//     uniform branches on their _Enable float, so for those the float alone is
//     enough. Two - slope blending and snow - are behind real keywords, because
//     each costs six extra texture fetches that a not-taken uniform branch would
//     still reserve registers for. For those two, ticking the box changes the
//     float and nothing else happens unless something syncs the keyword. That
//     sync is this class's real job; the layout is the part you can see.
//
//  Sections match the node graph's visual language - a coloured header bar over
//  an indented body - so a material reads like the node that produced it.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Drawing
{
    public class UCTerrainShaderGUI : ShaderGUI
    {
        // Toggle property -> the keyword it gates.
        //
        // Deliberately only two. Every other _Enable* is read directly by the
        // fragment shader as a uniform float; adding it here would do nothing
        // except enable a keyword no #ifdef tests. See the variant-budget note
        // at the top of UC_TerrainLitInput.hlsl for why the split falls here.
        // Property -> keyword. Every entry here MUST have a matching
        // shader_feature_local_fragment in UC_TerrainLit.shader and an #ifdef
        // around its sample site in UC_TerrainLitPasses.hlsl, or the toggle
        // silently stops doing anything.
        //
        // The five below slope/snow were uniform branches until the shader hit
        // "19 texture parameters, more than the 16 supported": a uniform branch
        // still binds its texture, so an optional map cost a texture unit even
        // when switched off. Gating them takes the forward pass to 14.
        private static readonly Dictionary<string, string> ToggleKeywords =
            new Dictionary<string, string>
            {
                { "_EnableSlopeBlend", "_UC_SLOPEBLEND_ON" },
                { "_EnableSnow",       "_UC_SNOW_ON"       },
                { "_EnableGradient",   "_UC_GRADIENT_ON"   },
                { "_EnableMud",        "_UC_MUD_ON"        },
                { "_EnableDetail",     "_UC_DETAIL_ON"     },
                { "_EnableAOMap",      "_UC_AOMAP_ON"      },
                { "_EnableEmission",   "_UC_EMISSION_ON"   }
            };

        // Foldout state is per-session view preference, not material data.
        // Writing it to the material would dirty the asset on a mere click.
        private static readonly Dictionary<string, bool> expanded =
            new Dictionary<string, bool>();

        private static readonly Color HeaderTint = new Color(0.38f, 0.55f, 0.72f);

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
        {
            var material = editor.target as Material;
            if (material == null) return;

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "Layer textures, tiling and per-layer settings are edited on the " +
                "Terrain component's Paint Terrain > Terrain Layers list. The " +
                "controls below are applied on top of that result, in the order " +
                "the sections are listed.",
                MessageType.None);
            EditorGUILayout.Space(4);

            // Section order deliberately mirrors the fragment shader's order of
            // operations. Reading the inspector top to bottom walks the same
            // path a pixel takes, which is the only ordering that lets someone
            // predict what a change will do to something further down.

            if (Section("Blending", "blend", true, material, "_EnableHeightBlend"))
            {
                Toggle(editor, props, material, "_EnableHeightBlend", "Height Blend",
                    "Rebalances layer weights by each layer's mask height so the " +
                    "taller material wins at the boundary instead of cross-fading.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableHeightBlend")))
                    Prop(editor, props, "_HeightTransition", "Transition",
                        "0 is a hard cut between layers, 1 is a soft blend.");
            }

            if (Section("Colour Gradient", "grad", false, material, "_EnableGradient"))
            {
                Toggle(editor, props, material, "_EnableGradient", "Colour Gradient",
                    "Tints the painted ground from a gradient LUT, indexed by " +
                    "altitude or by slope angle. Applied before cliff and snow, so " +
                    "those keep their own authored colour.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableGradient")))
                {
                    Prop(editor, props, "_GradientTex", "Gradient LUT",
                        "A 256x1 ramp. The Colour Gradient node bakes this.");
                    Prop(editor, props, "_GradientMode", "Source",
                        "Altitude reads world Y in metres. Slope reads degrees from flat.");
                    Prop(editor, props, "_GradientMin", "Range Min",
                        "Value mapped to the left end of the ramp - metres, or degrees.");
                    Prop(editor, props, "_GradientMax", "Range Max",
                        "Value mapped to the right end of the ramp.");
                    Prop(editor, props, "_GradientBlendMode", "Blend");
                    Prop(editor, props, "_GradientStrength", "Strength");
                }
            }

            if (Section("Mud and Dirt", "mud", false, material, "_EnableMud"))
            {
                Toggle(editor, props, material, "_EnableMud", "Mud",
                    "Collects mud where the ground is flat enough to hold it and " +
                    "recessed enough for water to have carried it there. Runs before " +
                    "cliffs, because mud on a vertical face would have run off.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableMud")))
                {
                    Prop(editor, props, "_MudTex", "Mud Albedo");
                    Prop(editor, props, "_MudColor", "Mud Tint");
                    Prop(editor, props, "_MudScale", "Scale (m)",
                        "World size of one mud texture tile, in metres.");
                    Prop(editor, props, "_MudBlend", "Amount",
                        "Overall strength. 0 disables the effect without turning it off.");
                    Prop(editor, props, "_MudThreshold", "Cavity Threshold",
                        "Mask height below which the ground counts as a recess.");
                    Prop(editor, props, "_MudSlopeLimit", "Slope Limit (deg)",
                        "Steepest ground mud will still cling to.");
                    Prop(editor, props, "_MudSmoothness", "Mud Smoothness");
                }
            }

            if (Section("Slope Blending", "slope", true, material, "_EnableSlopeBlend"))
            {
                Toggle(editor, props, material, "_EnableSlopeBlend", "Slope Blend",
                    "Replaces the painted layers with a cliff material past a slope " +
                    "angle. Projected triplanar from world space, so steep faces do " +
                    "not show stretched terrain UVs.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableSlopeBlend")))
                {
                    Prop(editor, props, "_SlopeTex", "Cliff Albedo");
                    Prop(editor, props, "_SlopeNormal", "Cliff Normal");
                    Prop(editor, props, "_SlopeColor", "Cliff Tint");
                    Prop(editor, props, "_SlopeAngleStart", "Start Angle",
                        "Degrees from flat where the cliff material begins to appear.");
                    Prop(editor, props, "_SlopeAngleEnd", "Full Angle",
                        "Degrees from flat where the cliff material fully replaces the layers.");
                    Prop(editor, props, "_SlopeTriplanarScale", "Triplanar Scale (m)",
                        "World size of one cliff texture tile, in metres.");
                    Prop(editor, props, "_SlopeNormalStrength", "Cliff Normal Strength");
                    Prop(editor, props, "_SlopeSmoothness", "Cliff Smoothness");
                    Prop(editor, props, "_SlopeMetallic", "Cliff Metallic");
                }
            }

            if (Section("Snow", "snow", true, material, "_EnableSnow"))
            {
                Toggle(editor, props, material, "_EnableSnow", "Snow Cover",
                    "Settles snow above a soft altitude line, on ground shallow " +
                    "enough to hold it. Runs after cliffs, so snow lies on exposed " +
                    "rock as readily as on grass.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableSnow")))
                {
                    Prop(editor, props, "_SnowTex", "Snow Albedo");
                    Prop(editor, props, "_SnowNormal", "Snow Normal");
                    Prop(editor, props, "_SnowColor", "Snow Tint");
                    Prop(editor, props, "_SnowAltitude", "Snow Line (m)",
                        "World height in metres at which snow reaches half coverage.");
                    Prop(editor, props, "_SnowFalloff", "Line Falloff (m)",
                        "Vertical width of the transition. A real snow line is a " +
                        "temperature gradient; too small a value draws a contour " +
                        "ring around the mountain.");
                    Prop(editor, props, "_SnowSlopeLimit", "Slope Limit (deg)",
                        "Angle past which snow stops holding on the face.");
                    Prop(editor, props, "_SnowSlopeFalloff", "Slope Falloff (deg)");
                    Prop(editor, props, "_SnowCoverage", "Coverage",
                        "Global multiplier. Drive this from a season or weather curve.");
                    Prop(editor, props, "_SnowTriplanarScale", "Triplanar Scale (m)");
                    Prop(editor, props, "_SnowSmoothness", "Snow Smoothness");
                    Prop(editor, props, "_SnowNormalStrength", "Snow Normal Strength");
                }
            }

            if (Section("Detail Overlay", "detail", false, material, "_EnableDetail"))
            {
                Toggle(editor, props, material, "_EnableDetail", "Detail Overlay",
                    "A fine second texture over everything, faded out with distance. " +
                    "Its job is close-range breakup; past the fade range it is " +
                    "sub-pixel noise and would only alias.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableDetail")))
                {
                    Prop(editor, props, "_DetailTex", "Detail Texture");
                    Prop(editor, props, "_DetailScale", "Scale (m)");
                    Prop(editor, props, "_DetailStrength", "Strength");
                    Prop(editor, props, "_DetailBlendMode", "Blend Mode",
                        "Overlay keeps mid grey neutral. Multiply darkens, Add lifts.");
                    Prop(editor, props, "_DetailFadeStart", "Fade Start (m)");
                    Prop(editor, props, "_DetailFadeEnd", "Fade End (m)");
                }
            }

            if (Section("Macro Variation", "macro", false, material, "_EnableMacroVariation"))
            {
                Toggle(editor, props, material, "_EnableMacroVariation", "Macro Variation",
                    "Large-scale colour drift across the world. Breaks up the " +
                    "repetition that makes tiled terrain textures read as tiled.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableMacroVariation")))
                {
                    Prop(editor, props, "_MacroVariationScale", "Scale (m)",
                        "Wavelength of the variation in metres. Set it well above " +
                        "the layer tiling or it reads as noise rather than as terrain.");
                    Prop(editor, props, "_MacroVariationAmount", "Amount");
                    Prop(editor, props, "_MacroVariationColor", "Tint");
                }
            }

            if (Section("Wetness", "wet", false, material, "_EnableWetness"))
            {
                Toggle(editor, props, material, "_EnableWetness", "Wetness",
                    "A thin water film: less diffuse escapes so the surface darkens, " +
                    "and the flat water boundary reflects sharply so specular tightens. " +
                    "Applied last, over snow, cliff and mud alike.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableWetness")))
                {
                    Prop(editor, props, "_WetnessAmount", "Global Wetness",
                        "Uniform wetness everywhere. Drive from a rain curve.");
                    Prop(editor, props, "_WetDarkening", "Darkening",
                        "How much diffuse the water film swallows.");
                    Prop(editor, props, "_WetSmoothness", "Wet Smoothness",
                        "Smoothness a fully wet surface reaches. Never reduces the " +
                        "underlying value - water does not roughen a polished surface.");
                    Prop(editor, props, "_WetSpecBoost", "Puddle Gloss Boost");
                    Prop(editor, props, "_PuddleLevel", "Puddle Level (m)",
                        "World height below which standing water collects.");
                    Prop(editor, props, "_PuddleFalloff", "Puddle Depth (m)",
                        "Metres below the level at which puddles reach full strength.");
                    Prop(editor, props, "_PuddleThreshold", "Cavity Threshold",
                        "Mask height below which the ground counts as a basin. " +
                        "Together with the level, this is what keeps a low ridge " +
                        "crest dry while the hollow beside it fills.");
                }
            }

            if (Section("Occlusion", "occ", false))
            {
                Prop(editor, props, "_OcclusionStrength", "AO Strength",
                    "How much of the layer mask's green channel reaches lighting. " +
                    "0 removes ambient occlusion entirely.");
                Prop(editor, props, "_CavityStrength", "Cavity Darkening",
                    "Extra darkening in recesses, driven by the mask height channel. " +
                    "Adds contact shadow where materials sink into each other.");

                EditorGUILayout.Space(2);
                Toggle(editor, props, material, "_EnableAOMap", "Baked AO Map",
                    "A terrain-wide occlusion map raymarched from the heightmap. " +
                    "Expresses landform shadowing - a valley floor sitting in its own " +
                    "shade - which a per-layer mask cannot, because the mask describes " +
                    "the material and not the shape it sits on.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableAOMap")))
                {
                    Prop(editor, props, "_AOMap", "Occlusion Map");
                    Prop(editor, props, "_AOMapStrength", "Map Strength");
                }
            }

            if (Section("PBR Remap", "pbr", false))
            {
                Prop(editor, props, "_SmoothnessRemapMin", "Smoothness Min",
                    "Remaps every layer's smoothness into this range. Raising the " +
                    "minimum is the fastest way to stop a whole terrain reading as chalk.");
                Prop(editor, props, "_SmoothnessRemapMax", "Smoothness Max");
                Prop(editor, props, "_MetallicScale", "Metallic Scale");
                Prop(editor, props, "_RoughnessVariationAmount", "Roughness Drift",
                    "Low-frequency noise on smoothness. Real ground is not uniformly " +
                    "rough, and a constant value is one of the stronger tells that a " +
                    "surface is procedural.");
                Prop(editor, props, "_RoughnessVariationScale", "Drift Scale (m)");
            }

            if (Section("Emission", "emis", false, material, "_EnableEmission"))
            {
                Toggle(editor, props, material, "_EnableEmission", "Emission",
                    "Lava, bioluminescence, magic. Sampled in control-map UV space, " +
                    "so the texture graph can paint it per world rather than per tile.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableEmission")))
                {
                    Prop(editor, props, "_EmissionMap", "Emission Map");
                    Prop(editor, props, "_EmissionColor", "Colour");
                    Prop(editor, props, "_EmissionIntensity", "Intensity");
                }
            }

            if (Section("Distance", "dist", false, material, "_EnableDistanceFade"))
            {
                Toggle(editor, props, material, "_EnableDistanceFade", "Distance Fade",
                    "Settles detail normals toward flat with distance. Removes the " +
                    "shimmer a normal map produces once its detail is sub-pixel.");

                using (new EditorGUI.DisabledScope(!IsOn(material, "_EnableDistanceFade")))
                {
                    Prop(editor, props, "_DistanceFadeStart", "Fade Start (m)");
                    Prop(editor, props, "_DistanceFadeEnd", "Fade End (m)");
                }
            }

            if (Section("Colour Grade", "grade", false))
            {
                Prop(editor, props, "_Saturation", "Saturation",
                    "Desaturates toward Rec. 709 luma, so it does not darken greens " +
                    "and lighten blues on the way.");
                Prop(editor, props, "_Contrast", "Contrast",
                    "Pivots on mid grey, so raising contrast does not also brighten.");
                Prop(editor, props, "_Brightness", "Brightness");
                Prop(editor, props, "_TintColor", "Global Tint");
            }

            EditorGUILayout.Space(6);
            if (Section("Advanced", "adv", false))
            {
                Prop(editor, props, "_EnableInstancedPerPixelNormal", "Instanced Per-Pixel Normal");
                editor.RenderQueueField();
                editor.EnableInstancingField();
                editor.DoubleSidedGIField();
            }

            // Re-assert every keyword each pass. Cheap, and it repairs a
            // material whose keywords were lost - which happens on any material
            // authored before this GUI existed, or edited by a script that set
            // the float directly.
            SyncAllKeywords(material);
        }

        // ------------------------------------------------------------------
        //  Layout
        // ------------------------------------------------------------------

        /// <summary>
        /// Coloured header bar with a foldout. Returns true when open.
        /// Pass <paramref name="enableProp"/> to show an on/off badge on the
        /// right - with fourteen sections, being able to see which features are
        /// live without expanding all of them is the difference between a
        /// control surface and a filing cabinet.
        /// </summary>
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

            // A brighter one-pixel cap reads as a lit edge and separates
            // consecutive headers without a full border.
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f),
                new Color(1f, 1f, 1f, EditorGUIUtility.isProSkin ? 0.10f : 0.35f));

            var label = new Rect(r.x + 18f, r.y + 2f, r.width - 70f, 18f);
            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;
            EditorGUI.LabelField(label, title, style);

            var arrow = new Rect(r.x + 4f, r.y + 3f, 14f, 14f);
            EditorGUI.LabelField(arrow, open ? "▼" : "▶", style);

            if (material != null && !string.IsNullOrEmpty(enableProp) &&
                material.HasProperty(enableProp))
            {
                bool on = material.GetFloat(enableProp) > 0.5f;

                // A filled pill for on, a hollow-looking dim one for off. Colour
                // alone would not survive a colour-blind user, so the label
                // carries the state too.
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
            if (p == null) return;   // property removed from the shader; skip quietly

            EditorGUI.indentLevel++;
            editor.ShaderProperty(p, new GUIContent(label, tooltip));
            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// A toggle that also drives its keyword. Only writes on an actual change
        /// so it does not mark the material dirty on every repaint.
        /// </summary>
        private static void Toggle(MaterialEditor editor, MaterialProperty[] props,
                                   Material material, string name, string label, string tooltip)
        {
            MaterialProperty p = FindProperty(name, props, false);
            if (p == null) return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            bool value = p.floatValue > 0.5f;
            value = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (EditorGUI.EndChangeCheck())
            {
                p.floatValue = value ? 1f : 0f;
                SetKeyword(material, name, value);
            }
            EditorGUI.indentLevel--;
        }

        private static bool IsOn(Material material, string prop)
        {
            return material.HasProperty(prop) && material.GetFloat(prop) > 0.5f;
        }

        private static void SetKeyword(Material material, string prop, bool on)
        {
            string keyword;
            if (!ToggleKeywords.TryGetValue(prop, out keyword)) return;

            if (on) material.EnableKeyword(keyword);
            else    material.DisableKeyword(keyword);
        }

        /// <summary>
        /// Brings every keyword back in line with its float. Public so runtime
        /// code that sets these properties from a graph can call it too - the
        /// same desync applies there.
        /// </summary>
        public static void SyncAllKeywords(Material material)
        {
            if (material == null) return;

            foreach (var kv in ToggleKeywords)
            {
                if (!material.HasProperty(kv.Key)) continue;
                bool on = material.GetFloat(kv.Key) > 0.5f;

                if (on == material.IsKeywordEnabled(kv.Value)) continue;
                if (on) material.EnableKeyword(kv.Value);
                else    material.DisableKeyword(kv.Value);
            }
        }
    }
}
