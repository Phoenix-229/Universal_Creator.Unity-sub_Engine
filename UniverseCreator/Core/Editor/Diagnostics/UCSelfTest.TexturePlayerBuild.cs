// UC-T07-12: build actual shipped shaders and authored TerrainLit combinations through the installed target's Player compiler.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Builds a shader-reference Player for the active installed target and exits Unity with zero only after a successful build.</summary>
        /// <remarks>Main-thread batch entry point restricted to a frozen .work/UC_validation_project_* project.
        /// Select the target with Unity's -buildTarget option before invoking this method. No modules, versions, build settings or signing credentials are changed.
        /// References every shipped shader and all 256 TerrainLit feature combinations; retains the Player artifact below the validation project's .work folder.
        /// Missing target support and compiler/build errors fail explicitly. A successful build does not certify rendered appearance or device performance.</remarks>
        public static void RunTexturePlayerBuildBatch()
        {
            checks = 0; failures.Clear(); string folder = null;
            var owned = new List<Material>();
            try
            {
                string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string parent = Path.GetFileName(Path.GetDirectoryName(project));
                if (!Application.isBatchMode || parent != ".work" || !Path.GetFileName(project).StartsWith("UC_validation_project_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Run this build gate in a frozen isolated UC validation project; the shared project and interactive scenes are not build fixtures.");
                BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                if (!BuildPipeline.IsBuildTargetSupported(group, target))
                    throw new InvalidOperationException("The active Player target is not installed: " + target + ". Install its module separately before claiming supported-target compilation.");
                string filename = target == BuildTarget.StandaloneWindows64 ? "TextureShaderReference.exe" :
                    target == BuildTarget.Android ? (EditorUserBuildSettings.buildAppBundle ? "TextureShaderReference.aab" : "TextureShaderReference.apk") :
                    target == BuildTarget.StandaloneOSX ? "TextureShaderReference.app" : "TextureShaderReference";
                string stamp = Guid.NewGuid().ToString("N"); folder = "Assets/UCTexturePlayerBuild_" + stamp;
                if (!AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)).Equals(string.Empty))
                {
                    var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    var root = new GameObject("UC shader compiler references — no appearance claim");
                    Shader terrainShader = Shader.Find(UCShaderProps.ShaderName);
                    if (terrainShader == null) throw new InvalidOperationException("TerrainLit shader is missing.");
                    for (int variant = 0; variant < 256; variant++)
                    {
                        var material = new Material(terrainShader); owned.Add(material);
                        for (int bit = 0; bit < TextureShaderFeatures.Length; bit++)
                            if ((variant & (1 << bit)) != 0) material.EnableKeyword(TextureShaderFeatures[bit]); else material.DisableKeyword(TextureShaderFeatures[bit]);
                        AddTextureBuildReference(root, material, folder, owned.Count - 1);
                    }
                    string[] shaders = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/UniverseCreator" });
                    if (shaders.Length == 0 || shaders.Length > 128) throw new InvalidOperationException("The shipped shader inventory is outside this bounded 1…128 shader build envelope.");
                    foreach (string guid in shaders)
                    {
                        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                        if (shader == null) throw new InvalidOperationException("A shipped shader asset could not be loaded: " + guid);
                        if (shader == terrainShader) continue;
                        var material = new Material(shader); owned.Add(material); AddTextureBuildReference(root, material, folder, owned.Count - 1);
                    }
                    string scenePath = folder + "/ShaderReferences.unity";
                    if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new InvalidOperationException("Could not save the temporary shader-reference build scene.");
                    AssetDatabase.SaveAssets();
                    string output = Path.Combine(project, ".work", "UCTexturePlayerBuild_" + stamp, filename);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    Debug.Log("UC-T07-12 Player target=" + target + ", backend=" + SystemInfo.graphicsDeviceType + ", shader assets=" + shaders.Length +
                        ", material references=" + owned.Count + ", output=" + output);
                    BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                        scenes = new[] { scenePath }, target = target, locationPathName = output, options = BuildOptions.None });
                    Check(report != null && report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0,
                        "UC-T07-12 actual Player compiler accepts shipped shader references and all TerrainLit material combinations on " + target + ".");
                    if (report != null) Debug.Log("UC-T07-12 Player result=" + report.summary.result + ", errors=" + report.summary.totalErrors +
                        ", warnings=" + report.summary.totalWarnings + ", bytes=" + report.summary.totalSize + ", duration=" + report.summary.totalTime);
                }
                else throw new InvalidOperationException("Could not create the unique shader-reference asset folder.");
            }
            catch (Exception error) { Check(false, "UC-T07 Player build gate exception: " + error); }
            finally
            {
                if (folder != null && AssetDatabase.IsValidFolder(folder))
                { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); AssetDatabase.DeleteAsset(folder); }
                foreach (Material material in owned) if (material != null && !AssetDatabase.Contains(material)) UnityEngine.Object.DestroyImmediate(material);
            }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        private static void AddTextureBuildReference(GameObject root, Material material, string folder, int index)
        {
            AssetDatabase.CreateAsset(material, folder + "/Material_" + index + ".mat");
            var reference = GameObject.CreatePrimitive(PrimitiveType.Quad); reference.transform.SetParent(root.transform, false);
            reference.transform.localPosition = new Vector3(index % 16 * 2, index / 16 * 2, 0);
            reference.GetComponent<MeshRenderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(reference.GetComponent<Collider>());
        }
    }
}
