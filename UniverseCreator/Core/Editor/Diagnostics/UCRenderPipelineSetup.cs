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
            // UC_Water's declared name and the UC terrain shader. Literals rather than
            // a reference to UCWaterProps so this editor setup file needs no runtime
            // using; they match the names in UC_Water.shader and TerrainBuilder.
            string[] names = { "UniverseCreator/Water", "UniverseCreator/TerrainLit" };

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
                Shader shader = Shader.Find(names[n]);
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

        /// <summary>True when URP is the active pipeline. Used by the self-test.</summary>
        public static bool IsUrpActive()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
        }
    }
}
