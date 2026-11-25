// UC repair 29: rendered forward-pass regression for the actual terrain SH contract.
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using System.Threading.Tasks;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#if UC_URP
using UnityEngine.Rendering.Universal;
#endif

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Renders an actual native Terrain through a bounded URP camera in daylight, moonlit night and zero illumination.</summary>
        /// <remarks>Isolated graphics batch entry; uses unchanged 513 height samples and a 64-square camera target.
        /// Checks finite pixels, light response, height/collider identity and hidden-terrain control, then restores the scene/pipeline
        /// and destroys owned resources. This is a controlled camera check, not host-world appearance or device performance.</remarks>
        /// <exception cref="InvalidOperationException">Not in graphics batch mode or the installed URP cannot render the request.</exception>
        public static async void RunNativeTerrainGraphicsBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Native terrain graphics acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
#if UC_URP
            Scene previousScene = SceneManager.GetActiveScene(), fixture = default;
            bool untitled = string.IsNullOrEmpty(previousScene.path);
            string startupFolder = "Assets/UCNativeTerrainStartup_" + Guid.NewGuid().ToString("N");
            RenderPipelineAsset previousDefault = GraphicsSettings.defaultRenderPipeline, previousQuality = QualitySettings.renderPipeline;
            RenderTexture previousTarget = RenderTexture.active;
            Vector4 previousGrid = Shader.GetGlobalVector("_IrradianceGridDims");
            float previousReflection = Shader.GetGlobalFloat("_UCEnvReflectionStrength"), previousWater = Shader.GetGlobalFloat("_UCUnderwaterOn");
            UniversalRendererData renderer = null; UniversalRenderPipelineAsset pipeline = null;
            TerrainData data = null; TerrainLayer layer = null; Texture2D albedo = null, pixels = null;
            Material material = null; RenderTexture target = null;
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new InvalidOperationException("Native terrain needs a graphics backend.");
                // UC repair 32: Unity permits an additive fixture only beside a saved scene; never replace authored startup content.
                if (untitled)
                {
                    if (!previousScene.IsValid() || SceneManager.sceneCount != 1 || previousScene.GetRootGameObjects().Length != 0)
                        throw new InvalidOperationException("Run native terrain graphics with a saved scene or one empty untitled startup scene.");
                    AssetDatabase.CreateFolder("Assets", startupFolder.Substring(7));
                    if (!EditorSceneManager.SaveScene(previousScene, startupFolder + "/Startup.unity"))
                        throw new InvalidOperationException("Could not save the empty native-terrain-test startup scene.");
                }
                fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(fixture);
                // UC repair 32: transient installed URP resources; no project assets/settings are authored by this fixture.
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                Type reloader = Type.GetType("UnityEngine.Rendering.ResourceReloader, Unity.RenderPipelines.Core.Runtime");
                var reload = reloader?.GetMethod("ReloadAllNullIn", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static, null, new[] { typeof(UnityEngine.Object), typeof(string) }, null);
                if (reload == null) throw new InvalidOperationException("Installed URP resource reloader is unavailable.");
                reload.Invoke(null, new object[] { renderer, "Packages/com.unity.render-pipelines.universal" });
                pipeline = UniversalRenderPipelineAsset.Create(renderer); pipeline.msaaSampleCount = 1;
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                Shader shader = Shader.Find("UniverseCreator/TerrainLit");
                if (shader == null) throw new InvalidOperationException("TerrainLit is unavailable.");
                material = new Material(shader) { enableInstancing = true };
                material.SetFloat("_OcclusionStrength", 0); material.SetFloat("_SmoothnessRemapMax", 0);
                albedo = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                albedo.SetPixel(0, 0, new Color(0.4f, 0.25f, 0.12f, 1)); albedo.Apply(false, false);
                layer = new TerrainLayer { diffuseTexture = albedo, tileSize = new Vector2(8, 8), smoothness = 0 };
                data = new TerrainData { heightmapResolution = 513, alphamapResolution = 32, size = new Vector3(32, 100, 32) };
                var heights = new float[513, 513];
                for (int z = 0; z < 513; z++) for (int x = 0; x < 513; x++) heights[z, x] = 0.1f;
                data.SetHeights(0, 0, heights); data.terrainLayers = new[] { layer };
                var weights = new float[32, 32, 1];
                for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) weights[z, x, 0] = 1;
                data.SetAlphamaps(0, 0, weights);
                GameObject ground = Terrain.CreateTerrainGameObject(data); ground.layer = 31;
                var terrain = ground.GetComponent<Terrain>(); terrain.materialTemplate = material;
                terrain.drawInstanced = true; terrain.heightmapPixelError = 1; terrain.Flush();
                var camera = new GameObject("UC native terrain camera").AddComponent<Camera>(); camera.enabled = false;
                camera.transform.SetPositionAndRotation(new Vector3(16, 40, 16), Quaternion.Euler(90, 0, 0));
                camera.orthographic = true; camera.orthographicSize = 12; camera.nearClipPlane = 0.1f; camera.farClipPlane = 100;
                camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = true;
                var light = new GameObject("UC native terrain light").AddComponent<Light>(); light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(90, 0, 0); light.cullingMask = 1 << 31; light.shadows = LightShadows.None;
                RenderSettings.sun = light; RenderSettings.ambientMode = AmbientMode.Custom; RenderSettings.ambientProbe = new SphericalHarmonicsL2();
                RenderSettings.ambientIntensity = 0; RenderSettings.reflectionIntensity = 0; RenderSettings.fog = false;
                Shader.SetGlobalVector("_IrradianceGridDims", Vector4.zero); Shader.SetGlobalFloat("_UCEnvReflectionStrength", 0); Shader.SetGlobalFloat("_UCUnderwaterOn", 0);
                target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                if (!target.Create()) throw new InvalidOperationException("Native terrain camera target allocation failed.");
                pixels = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                // Allow installed pipeline creation and native Terrain renderer registration on Editor ticks, without blocking the UI.
                await Task.Delay(100);
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new InvalidOperationException("Installed pipeline rejects the camera render request.");
                float day = 0, night = 0;
                for (int mode = 0; mode < 4; mode++)
                {
                    light.intensity = mode == 0 || mode == 3 ? 1 : mode == 1 ? 0.05f : 0;
                    light.color = mode == 1 ? new Color(0.7f, 0.8f, 1) : Color.white;
                    terrain.drawHeightmap = mode != 3;
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    RenderTexture.active = target; pixels.ReadPixels(new Rect(32, 32, 1, 1), 0, 0, false); pixels.Apply(false, false);
                    Color sample = pixels.GetPixel(0, 0); float peak = Mathf.Max(sample.r, Mathf.Max(sample.g, sample.b));
                    Console.WriteLine("UC_NATIVE_TERRAIN mode=" + mode + " rgba=" + sample.ToString("F6"));
                    Check(!float.IsNaN(peak) && !float.IsInfinity(peak), "UC repair 32: native camera pixels are finite, mode " + mode);
                    if (mode == 0) { day = peak; Check(day > 0.02f, "UC repair 32: native Terrain is visible in daylight."); }
                    else if (mode == 1) { night = peak; Check(night > 0.0001f && night < day, "UC repair 32: native Terrain remains visible and dimmer under moonlight."); }
                    else Check(peak < 0.0001f, "UC repair 32: native " + (mode == 2 ? "zero-light" : "hidden-terrain") + " control has no artificial brightness.");
                }
                terrain.drawHeightmap = true;
                Check(data.heightmapResolution == 513 && Mathf.Abs(data.GetHeight(256, 256) - 10) < 0.01f &&
                    terrain.terrainData == data && ground.GetComponent<TerrainCollider>().terrainData == data,
                    "UC repair 32: camera/light transitions preserve full-resolution native heights and collider identity.");
                Check(!ShaderUtil.ShaderHasError(shader), "UC repair 32: native Terrain variants compile without shader errors.");
            }
            catch (Exception error) { Check(false, "UC native terrain graphics exception: " + error); }
            finally
            {
                RenderTexture.active = previousTarget;
                Shader.SetGlobalVector("_IrradianceGridDims", previousGrid); Shader.SetGlobalFloat("_UCEnvReflectionStrength", previousReflection); Shader.SetGlobalFloat("_UCUnderwaterOn", previousWater);
                GraphicsSettings.defaultRenderPipeline = previousDefault; QualitySettings.renderPipeline = previousQuality;
                if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
                if (fixture.IsValid()) EditorSceneManager.CloseScene(fixture, true);
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (layer != null) UnityEngine.Object.DestroyImmediate(layer);
                if (albedo != null) UnityEngine.Object.DestroyImmediate(albedo);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (pipeline != null) UnityEngine.Object.DestroyImmediate(pipeline);
                if (renderer != null) UnityEngine.Object.DestroyImmediate(renderer);
                if (untitled && AssetDatabase.IsValidFolder(startupFolder))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    AssetDatabase.DeleteAsset(startupFolder);
                }
            }
#else
            Check(false, "UC native Terrain camera acceptance requires installed URP.");
#endif
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        /// <summary>Renders bounded TerrainLit pixels with ambient alone in pixel, mixed and vertex SH modes.</summary>
        /// <remarks>Isolated graphics batch entry; uses a 32-square target and uniform-normal test geometry.
        /// Checks nonzero L0/L2 ambient, matching modes and a zero-ambient control. Restores globals and releases native resources.
        /// This verifies the shader forward pass, not the owner's native terrain, camera or device appearance.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunTerrainAmbientBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Terrain ambient acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            try { TestTerrainAmbientRendering(); }
            catch (Exception error) { Check(false, "UC terrain ambient graphics exception: " + error); }
            Console.WriteLine(Report(failures.Count == 0));
            EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        private static void TestTerrainAmbientRendering()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Terrain ambient pixel acceptance requires a graphics backend.");
            string[] vectors = { "unity_SHAr", "unity_SHAg", "unity_SHAb", "unity_SHBr", "unity_SHBg", "unity_SHBb", "unity_SHC",
                "_MainLightColor", "_MainLightPosition", "_AdditionalLightsCount", "_GlossyEnvironmentColor", "_WorldSpaceCameraPos", "_IrradianceGridDims" };
            string[] scalars = { "_UCEnvReflectionStrength", "_UCUnderwaterOn" };
            var previousVectors = new Vector4[vectors.Length];
            var previousScalars = new float[scalars.Length];
            for (int i = 0; i < vectors.Length; i++) previousVectors[i] = Shader.GetGlobalVector(vectors[i]);
            for (int i = 0; i < scalars.Length; i++) previousScalars[i] = Shader.GetGlobalFloat(scalars[i]);
            Texture previousProbe = Shader.GetGlobalTexture("unity_SpecCube0");
            RenderTexture previousTarget = RenderTexture.active;
            Material material = null; Mesh mesh = null; RenderTexture target = null; Texture2D pixels = null; Cubemap blackProbe = null;
            var commands = new CommandBuffer { name = "UC bounded terrain ambient pixels" };
            try
            {
                Shader shader = Shader.Find("UniverseCreator/TerrainLit");
                if (shader == null) throw new InvalidOperationException("TerrainLit shader is unavailable.");
                material = new Material(shader);
                int pass = material.FindPass("ForwardLit");
                if (pass < 0) throw new InvalidOperationException("TerrainLit forward pass is unavailable.");
                material.SetTexture("_Splat0", Texture2D.whiteTexture);
                // UC repair 29: native Terrain supplies these non-ShaderLab layer remaps; a plain fixture Material does not.
                for (int layer = 0; layer < 4; layer++) material.SetVector("_DiffuseRemapScale" + layer, Vector4.one);
                material.SetFloat("_Smoothness0", 0);
                material.SetFloat("_SmoothnessRemapMax", 0);
                material.SetFloat("_OcclusionStrength", 0);
                mesh = new Mesh { name = "UC ambient uniform-normal fixture" };
                mesh.vertices = new[] { new Vector3(-1, -1, 0.5f), new Vector3(-1, 1, 0.5f), new Vector3(1, 1, 0.5f), new Vector3(1, -1, 0.5f) };
                mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
                mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                target = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                if (!target.Create()) throw new InvalidOperationException("Terrain ambient target allocation failed.");
                pixels = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                blackProbe = new Cubemap(1, TextureFormat.RGBAFloat, false);
                for (int face = 0; face < 6; face++) blackProbe.SetPixel((CubemapFace)face, 0, 0, Color.clear);
                blackProbe.Apply(false, false);
                Shader.SetGlobalTexture("unity_SpecCube0", blackProbe);
                foreach (string vector in vectors) Shader.SetGlobalVector(vector, Vector4.zero);
                foreach (string scalar in scalars) Shader.SetGlobalFloat(scalar, 0);
                Shader.SetGlobalVector("_WorldSpaceCameraPos", new Vector4(0, 0, -2, 1));
                Shader.SetGlobalVector("_MainLightPosition", new Vector4(0, 1, 0, 0));
                var samples = new Color[3];
                for (int mode = 0; mode < 3; mode++)
                {
                    material.DisableKeyword("EVALUATE_SH_VERTEX"); material.DisableKeyword("EVALUATE_SH_MIXED");
                    if (mode == 1) material.EnableKeyword("EVALUATE_SH_MIXED");
                    if (mode == 2) material.EnableKeyword("EVALUATE_SH_VERTEX");
                    ShaderUtil.CompilePass(material, pass, true);
                    Check(ShaderUtil.IsPassCompiled(material, pass) && !ShaderUtil.ShaderHasError(shader),
                        "UC repair 29: actual terrain ambient variant compiles, mode " + mode);
                    for (int enabled = 0; enabled < 2; enabled++)
                    {
                        // Uniform normals make all three evaluation paths equivalent; nonzero L2 catches missing/doubled terms.
                        Shader.SetGlobalVector("unity_SHAr", new Vector4(0, 0, 0, enabled * 0.25f));
                        Shader.SetGlobalVector("unity_SHAg", new Vector4(0, 0, 0, enabled * 0.20f));
                        Shader.SetGlobalVector("unity_SHAb", new Vector4(0, 0, 0, enabled * 0.15f));
                        Shader.SetGlobalVector("unity_SHC", new Vector4(enabled * 0.04f, enabled * 0.03f, enabled * 0.02f, 0));
                        commands.Clear(); commands.SetRenderTarget(target); commands.ClearRenderTarget(false, true, Color.clear);
                        commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                        commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, pass);
                        Graphics.ExecuteCommandBuffer(commands);
                        RenderTexture.active = target; pixels.ReadPixels(new Rect(16, 16, 1, 1), 0, 0, false); pixels.Apply(false, false);
                        Color sample = pixels.GetPixel(0, 0);
                        Console.WriteLine("UC_TERRAIN_AMBIENT mode=" + mode + " enabled=" + enabled + " rgba=" + sample.ToString("F6"));
                        Check(sample.a > 0.99f && !float.IsNaN(sample.r) && !float.IsInfinity(sample.r) &&
                            !float.IsNaN(sample.g) && !float.IsInfinity(sample.g) && !float.IsNaN(sample.b) && !float.IsInfinity(sample.b),
                            "UC repair 29: terrain forward pass draws finite pixels, mode " + mode + " ambient " + enabled);
                        if (enabled == 0) Check(Mathf.Max(sample.r, Mathf.Max(sample.g, sample.b)) < 0.0001f,
                            "UC repair 29: zero ambient/direct illumination remains dark without an artificial floor, mode " + mode);
                        else
                        {
                            samples[mode] = sample;
                            Check(sample.r > 0.05f && sample.g > 0.04f && sample.b > 0.03f,
                                "UC repair 29: real nonzero ambient reaches terrain pixels, mode " + mode);
                        }
                    }
                }
                for (int mode = 1; mode < 3; mode++)
                    Check(Mathf.Abs(samples[mode].r - samples[0].r) < 0.002f && Mathf.Abs(samples[mode].g - samples[0].g) < 0.002f &&
                        Mathf.Abs(samples[mode].b - samples[0].b) < 0.002f,
                        "UC repair 29: uniform-normal terrain preserves L0/L2 ambient across SH modes, mode " + mode);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                for (int i = 0; i < vectors.Length; i++) Shader.SetGlobalVector(vectors[i], previousVectors[i]);
                for (int i = 0; i < scalars.Length; i++) Shader.SetGlobalFloat(scalars[i], previousScalars[i]);
                Shader.SetGlobalTexture("unity_SpecCube0", previousProbe);
                commands.Release();
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                if (blackProbe != null) UnityEngine.Object.DestroyImmediate(blackProbe);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
