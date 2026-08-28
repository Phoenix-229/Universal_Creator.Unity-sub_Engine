// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    UCRenderPipelineSetup.cs
//  Purpose: Creates the URP pipeline asset this project renders through, and
//           assigns it to Graphics + every quality level.
//
//  WHY THIS EXISTS
//  ---------------
//  Adding com.unity.render-pipelines.universal to the manifest installs the
//  package but changes nothing about how the project renders: Unity still runs
//  the Built-in pipeline until a UniversalRenderPipelineAsset is assigned in
//  GraphicsSettings. A project in that half-configured state compiles clean and
//  then draws every URP-shaded surface magenta, which reads as "the shader is
//  broken" rather than "no pipeline is assigned".
//
//  The terrain texturing work in Module 2 targets URP's TerrainLit, so the
//  assignment is a build prerequisite, not a user preference. Doing it from code
//  keeps it reproducible on a machine that is not this one - the whole point of
//  a module that ships.
//
//  IDEMPOTENT. Running it twice reuses the existing assets rather than minting a
//  second pair, so it is safe to call from a menu item, a self-test, or batch.
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UniverseCreator.Editor.Diagnostics
{
    public static class UCRenderPipelineSetup
    {
        private const string SettingsDir  = "Assets/UniverseCreator/Settings";
        private const string PipelinePath = SettingsDir + "/UC_URP_Pipeline.asset";
        private const string RendererPath = SettingsDir + "/UC_URP_Renderer.asset";

        /// <summary>URP terrain shader. Verified present in URP 17.5.</summary>
        public const string TerrainLitShader = "Universal Render Pipeline/Terrain/Lit";

        [MenuItem("Tools/Universe Creator/Setup/Configure URP Pipeline", priority = 200)]
        public static void ConfigureMenu()
        {
            string report;
            bool ok = Configure(out report);
            Debug.Log((ok ? "[UC] URP configured.\n" : "[UC] URP setup incomplete.\n") + report);
        }

        /// <summary>
        /// Ensures a URP asset exists and is the active pipeline. Returns false only
        /// when something could not be completed; <paramref name="report"/> always
        /// describes what happened either way.
        /// </summary>
        public static bool Configure(out string report)
        {
            var log = new System.Text.StringBuilder();
            bool ok = true;

            if (!Directory.Exists(SettingsDir))
            {
                Directory.CreateDirectory(SettingsDir);
                AssetDatabase.Refresh();
                log.AppendLine("created " + SettingsDir);
            }

            // ---- renderer data ----
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, RendererPath);

                // A CreateInstance'd renderer has null shader/resource fields; URP
                // fills them from the package via ResourceReloader. Skipping this is
                // how you get a renderer that throws on the first frame it draws.
                ReloadResources(renderer);
                log.AppendLine("created " + RendererPath);
            }
            else log.AppendLine("reused  " + RendererPath);

            // Ambient occlusion is URP's own Screen Space Ambient Occlusion feature,
            // configured here - never a second AO pass of our own (it would drift from
            // Unity's and compete for the same depth prepass).
            ConfigureAmbientOcclusion(renderer, log);

            // ---- pipeline asset ----
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                // Create() wires the renderer list and reloads its own resources.
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
                log.AppendLine("created " + PipelinePath);
            }
            else log.AppendLine("reused  " + PipelinePath);

            AssetDatabase.SaveAssets();

            // ---- assign as the active pipeline ----
            if (GraphicsSettings.defaultRenderPipeline != pipeline)
            {
                GraphicsSettings.defaultRenderPipeline = pipeline;
                log.AppendLine("assigned GraphicsSettings.defaultRenderPipeline");
            }
            else log.AppendLine("GraphicsSettings already pointed at it");

            // Every quality level carries its own override, and a null one there
            // silently falls back to Built-in for that level only - the kind of bug
            // that reproduces on exactly one of six quality settings.
            int levels = QualitySettings.names.Length;
            int current = QualitySettings.GetQualityLevel();
            int retargeted = 0;
            for (int i = 0; i < levels; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                if (QualitySettings.renderPipeline != pipeline)
                {
                    QualitySettings.renderPipeline = pipeline;
                    retargeted++;
                }
            }
            QualitySettings.SetQualityLevel(current, false);
            log.AppendLine("quality levels retargeted: " + retargeted + " of " + levels);

            if (Shader.Find(TerrainLitShader) == null)
            {
                log.AppendLine("WARNING: '" + TerrainLitShader + "' not found");
                ok = false;
            }
            else log.AppendLine("terrain shader resolves: " + TerrainLitShader);

            int included = EnsureAlwaysIncludedShaders();
            log.AppendLine("always-included UC shaders newly added: " + included);

            AssetDatabase.SaveAssets();
            report = log.ToString();
            return ok;
        }

        /// <summary>
        /// Calls URP's internal ResourceReloader.ReloadAllNullIn. It is not public
        /// API, so this reaches it by reflection and degrades to a warning rather
        /// than a throw - a missing resource shows up as a visible render error,
        /// which is easier to act on than a failed setup pass.
        /// </summary>
        private static void ReloadResources(ScriptableObject target)
        {
            System.Type t = System.Type.GetType(
                "UnityEngine.Rendering.ResourceReloader, Unity.RenderPipelines.Core.Runtime");
            if (t == null) { Debug.LogWarning("[UC] ResourceReloader type not found."); return; }

            var m = t.GetMethod("ReloadAllNullIn",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static,
                null, new[] { typeof(Object), typeof(string) }, null);
            if (m == null) { Debug.LogWarning("[UC] ReloadAllNullIn not found."); return; }

            try
            {
                m.Invoke(null, new object[] { target, "Packages/com.unity.render-pipelines.universal" });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[UC] ResourceReloader failed: " + e.Message);
            }
        }

        /// <summary>
        /// Adds the UC shaders to Project Settings > Graphics > Always Included Shaders
        /// so URP cannot strip them from a build. A stripped shader resolves to null
        /// through Shader.Find at runtime, which renders the white/default material
        /// (spec 4.4). The fault only shows in a build: in the editor the shader is
        /// always present. Idempotent - a shader already listed is left alone. Returns
        /// the number newly added.
        /// </summary>
        public static int EnsureAlwaysIncludedShaders()
        {
            // The one list of shaders this module ships, owned by UCShaderDiagnostics.
            // This was previously a local two-name literal array, which meant every
            // shader added after it - VegetationLit, RockLit, WindDetail and the
            // ripple sim - was silently left out of Always Included and could be
            // stripped from a build. That is the exact fault this method exists to
            // prevent, so the list must never be restated here.
            string[] names = UCShaderDiagnostics.ShipShaders;

            // GraphicsSettings exposes no scripting API for this list; it is edited
            // through the serialized settings asset, the same object the Graphics
            // inspector writes to.
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0 || assets[0] == null) return 0;

            var so = new SerializedObject(assets[0]);
            SerializedProperty list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return 0;

            int added = 0;
            for (int n = 0; n < names.Length; n++)
            {
                // Resolve rather than Shader.Find: the ripple shader is Hidden/,
                // which Shader.Find does not reliably return in batch mode, and a
                // null here silently skips the shader it was meant to protect.
                Shader shader = UCShaderDiagnostics.Resolve(names[n]);
                if (shader == null) continue;

                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    {
                        present = true;
                        break;
                    }
                }
                if (present) continue;

                int idx = list.arraySize;
                list.InsertArrayElementAtIndex(idx);
                list.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
                added++;
            }

            if (added > 0)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
            return added;
        }

        [MenuItem("Tools/Universe Creator/Setup/Always-Include UC Shaders", priority = 201)]
        public static void EnsureAlwaysIncludedMenu()
        {
            int n = EnsureAlwaysIncludedShaders();
            Debug.Log("[UC] Always-included UC shaders newly added: " + n +
                      " (0 means they were already listed).");
        }

        /// <summary>True when the renderer already carries a URP SSAO feature.</summary>
        public static bool HasAmbientOcclusion(UniversalRendererData renderer)
        {
            if (renderer == null || renderer.rendererFeatures == null) return false;
            for (int i = 0; i < renderer.rendererFeatures.Count; i++)
            {
                var f = renderer.rendererFeatures[i];
                if (f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion") return true;
            }
            return false;
        }

        // The SSAO settings this module chooses. Per-tier dialing (radius/intensity/
        // samples scaled by device class) is applied at runtime by LightingTier, which
        // reaches the live feature's settings; these are the baseline written at setup.
        private const float AoIntensity = 1.1f;
        private const float AoRadius = 0.30f;
        private const int AoSampleCount = 8;

        /// <summary>
        /// Ensures the renderer carries exactly one URP Screen Space Ambient Occlusion
        /// feature and writes this module's baseline settings onto it. Idempotent: an
        /// existing feature is reused and re-tuned, never duplicated. Every step is
        /// guarded - a URP whose SSAO type or serialized layout differs degrades to a
        /// warning line, never a throw, so setup never takes the runtime menu with it.
        /// </summary>
        public static void ConfigureAmbientOcclusion(UniversalRendererData renderer, System.Text.StringBuilder log)
        {
            if (renderer == null) { log.AppendLine("SSAO: no renderer to configure"); return; }

            System.Type aoType = System.Type.GetType(
                "UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion, Unity.RenderPipelines.Universal.Runtime");
            if (aoType == null) { log.AppendLine("SSAO: URP SSAO type not found (skipped)"); return; }

            try
            {
                ScriptableRendererFeature feature = FindOrAddFeature(renderer, aoType, log);
                if (feature == null) { log.AppendLine("SSAO: feature could not be created"); return; }

                // Settings live in the feature's private m_Settings; edit by serialized
                // path so a renamed field simply no-ops rather than failing to compile.
                var fso = new SerializedObject(feature);
                SetFloat(fso, "m_Settings.Intensity", AoIntensity);
                SetFloat(fso, "m_Settings.Radius", AoRadius);
                SetInt(fso, "m_Settings.Samples", AoSampleCount);
                SetBool(fso, "m_Settings.Downsample", true);
                fso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(feature);
                EditorUtility.SetDirty(renderer);
                AssetDatabase.SaveAssets();
            }
            catch (System.Exception e)
            {
                log.AppendLine("SSAO: configuration failed (" + e.Message + ")");
            }
        }

        // Return the renderer's SSAO feature, adding one as a sub-asset if none exists.
        // The feature list and its id map are two serialized arrays that must grow
        // together, or URP flags the renderer out of sync.
        private static ScriptableRendererFeature FindOrAddFeature(
            UniversalRendererData renderer, System.Type aoType, System.Text.StringBuilder log)
        {
            for (int i = 0; i < renderer.rendererFeatures.Count; i++)
            {
                var f = renderer.rendererFeatures[i];
                if (f != null && aoType.IsInstanceOfType(f)) { log.AppendLine("SSAO: reused existing feature"); return f; }
            }

            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(aoType);
            feature.name = "UC Ambient Occlusion";
            AssetDatabase.AddObjectToAsset(feature, renderer);

            var so = new SerializedObject(renderer);
            SerializedProperty list = so.FindProperty("m_RendererFeatures");
            SerializedProperty map = so.FindProperty("m_RendererFeatureMap");
            if (list == null || map == null) { log.AppendLine("SSAO: renderer feature arrays not found"); return feature; }

            int idx = list.arraySize;
            list.InsertArrayElementAtIndex(idx);
            list.GetArrayElementAtIndex(idx).objectReferenceValue = feature;
            map.InsertArrayElementAtIndex(idx);
            // URP keys each feature by the sub-asset's local file id (see
            // ScriptableRendererData.UpdateMap), not an instance id - that is the value this
            // map column holds, and it is the one that survives a reload where an instance id
            // would not. Resolve it the same way URP does; leave the slot 0 if it cannot be.
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId))
                map.GetArrayElementAtIndex(idx).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine("SSAO: added URP SSAO feature to the renderer");
            return feature;
        }

        private static void SetFloat(SerializedObject so, string path, float v)
        { var p = so.FindProperty(path); if (p != null) p.floatValue = v; }
        private static void SetInt(SerializedObject so, string path, int v)
        { var p = so.FindProperty(path); if (p != null) p.intValue = v; }
        private static void SetBool(SerializedObject so, string path, bool v)
        { var p = so.FindProperty(path); if (p != null) p.boolValue = v; }
        private static void SetVector2(SerializedObject so, string path, Vector2 v)
        { var p = so.FindProperty(path); if (p != null) p.vector2Value = v; }

        // ---- Shadow policy (S-55): one authored table, applied to the URP asset --------
        // The per-tier numbers live in LightingTier (§ 8.11), NOT here - this method only
        // APPLIES them, so there is a single source of truth for cascade count, distance,
        // atlas size and soft-shadow level, and no second copy to drift. Cascade split
        // fractions are derived from the count (2 -> 0.15/1.0, 3 -> 0.08/0.25/1.0), the
        // even-ish log split that keeps the near cascade tight without wasting the far one.
        public static bool ConfigureShadowPolicy(
            UniversalRenderPipelineAsset asset,
            UniverseCreator.TerrainSystem.Lighting.LightingTierSettings s,
            System.Text.StringBuilder log,
            bool overwriteAuthorEdits = true)
        {
            if (asset == null) { if (log != null) log.AppendLine("Shadows: no URP asset"); return false; }

            // S-58: this module configures a pipeline it did not author (§ 2.2). When asked
            // not to overwrite, diff first and, if the asset's shadow block already differs
            // from what this preset would write, name every changed row and write NOTHING -
            // the author's asset survives the preset change untouched.
            if (!overwriteAuthorEdits)
            {
                int pending = PreviewShadowChanges(asset, s, log);
                if (pending > 0)
                {
                    if (log != null)
                        log.AppendLine("Shadows: " + pending + " author-edited shadow setting(s) left intact " +
                                       "(preset NOT applied); nothing outside the shadow block was touched");
                    return false;
                }
            }

            var so = new SerializedObject(asset);
            SetFloat(so, "m_ShadowDistance", s.shadowDistance);
            SetInt(so, "m_ShadowCascadeCount", s.cascadeCount);
            if (s.cascadeCount <= 2)
                SetFloat(so, "m_Cascade2Split", 0.15f);
            else
                SetVector2(so, "m_Cascade3Split", new Vector2(0.08f, 0.25f));
            SetInt(so, "m_MainLightShadowmapResolution", s.shadowAtlasResolution);

            // Shadow bias (S-57). One depth+normal pair on the asset; URP scales it per
            // cascade by texel world size at runtime, so this base is authored to the tier's
            // texel coarseness. Normal bias is the grazing-angle terrain acne fix.
            SetFloat(so, "m_ShadowDepthBias", s.shadowDepthBias);
            SetFloat(so, "m_ShadowNormalBias", s.shadowNormalBias);

            bool soft = s.softShadows != UniverseCreator.TerrainSystem.Lighting.SoftShadowLevel.Off;
            SetBool(so, "m_SoftShadowsSupported", soft);
            // Soft-shadow quality is an int enum on the asset (Low/Medium/High). No-ops on
            // URP versions that do not expose it, leaving the bool toggle as the control.
            SetInt(so, "m_SoftShadowQuality", (int)s.softShadows);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            if (log != null)
                log.AppendLine("Shadows: " + s.cascadeCount + " cascades @ " +
                               (int)s.shadowDistance + " m, atlas " + s.shadowAtlasResolution +
                               ", soft " + s.softShadows +
                               ", bias " + s.shadowDepthBias + "/" + s.shadowNormalBias);
            return true;
        }

        // S-58: compare-only. Appends one "WARNING would change <row>: current -> proposed"
        // line per shadow field whose current asset value differs from what the preset would
        // write, and returns how many differ. Touches nothing: it never calls Apply, so the
        // author's asset is unchanged whether or not the caller goes on to write.
        public static int PreviewShadowChanges(
            UniversalRenderPipelineAsset asset,
            UniverseCreator.TerrainSystem.Lighting.LightingTierSettings s,
            System.Text.StringBuilder log)
        {
            if (asset == null) return 0;
            var so = new SerializedObject(asset);
            int n = 0;
            n += DiffFloat(so, "m_ShadowDistance", s.shadowDistance, "shadow distance", log);
            n += DiffInt(so, "m_ShadowCascadeCount", s.cascadeCount, "cascade count", log);
            n += DiffInt(so, "m_MainLightShadowmapResolution", s.shadowAtlasResolution, "shadow atlas", log);
            n += DiffFloat(so, "m_ShadowDepthBias", s.shadowDepthBias, "shadow depth bias", log);
            n += DiffFloat(so, "m_ShadowNormalBias", s.shadowNormalBias, "shadow normal bias", log);
            n += DiffBool(so, "m_SoftShadowsSupported",
                          s.softShadows != UniverseCreator.TerrainSystem.Lighting.SoftShadowLevel.Off,
                          "soft shadows", log);
            return n;
        }

        private static int DiffFloat(SerializedObject so, string path, float proposed, string label, System.Text.StringBuilder log)
        {
            var p = so.FindProperty(path);
            if (p == null || Mathf.Approximately(p.floatValue, proposed)) return 0;
            if (log != null) log.AppendLine("  WARNING would change " + label + ": " + p.floatValue + " -> " + proposed);
            return 1;
        }

        private static int DiffInt(SerializedObject so, string path, int proposed, string label, System.Text.StringBuilder log)
        {
            var p = so.FindProperty(path);
            if (p == null || p.intValue == proposed) return 0;
            if (log != null) log.AppendLine("  WARNING would change " + label + ": " + p.intValue + " -> " + proposed);
            return 1;
        }

        private static int DiffBool(SerializedObject so, string path, bool proposed, string label, System.Text.StringBuilder log)
        {
            var p = so.FindProperty(path);
            if (p == null || p.boolValue == proposed) return 0;
            if (log != null) log.AppendLine("  WARNING would change " + label + ": " + p.boolValue + " -> " + proposed);
            return 1;
        }

        /// <summary>Resolve the tier's settings and apply the shadow policy to the project
        /// URP asset. The one call the lighting driver makes when the device tier changes.</summary>
        public static void ApplyShadowPolicyForTier(
            UniverseCreator.Optimization.AdaptiveQuality.QualityTier tier, System.Text.StringBuilder log)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            var s = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(tier);
            ConfigureShadowPolicy(asset, s, log);
        }

        /// <summary>True when URP is the active pipeline. Used by the self-test.</summary>
        public static bool IsUrpActive()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
        }
    }
}
