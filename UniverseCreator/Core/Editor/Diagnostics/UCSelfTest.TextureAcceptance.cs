// UC-T07-12: numerical/native control acceptance and explicit graphics compiler gates remain separate evidence.
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static readonly string[] TextureShaderFeatures = {
            "_UC_SLOPEBLEND_ON", "_UC_SNOW_ON", "_UC_GRADIENT_ON", "_UC_MUD_ON", "_UC_DETAIL_ON", "_UC_BEACH_ON", "_UC_AOMAP_ON", "_UC_EMISSION_ON" };

        /// <summary>Runs the complete registered Texture Graph code/native fixtures and exits Unity with zero only when they pass.</summary>
        /// <remarks>Unity batch entry point, main thread. Does not certify rendered quality, target-device performance or Player shader builds.</remarks>
        public static void RunTextureBatch()
        {
            checks = 0; failures.Clear();
            var groups = new (string id, Action run)[] {
                ("shader schema", TestShaderPropertyRegistry), ("shipped shaders", TestAllShippedShadersCompile), ("material writers", TestTextureNodesWriteRealProperties),
                ("UC-T07-02", TestTextureImageContracts), ("UC-T07-03", TestTextureImageInputs), ("UC-T07-04", TestTextureImageOperations),
                ("UC-T07-05", TestTextureNormals), ("UC-T07-06", TestMaterialLayerParameters), ("UC-T07-07", TestShaderParameters),
                ("UC-T07-08", TestTextureFunctions), ("UC-T07-09", TestTextureBakes), ("UC-T07-10", TestTextureLifetimes),
                ("UC-T07-11", TestTextureSurfaceContext), ("UC-T07-12", TestTextureAcceptance) };
            foreach (var group in groups)
                try { group.run(); } catch (Exception error) { Check(false, group.id + " texture acceptance exception: " + error); }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        /// <summary>Synchronously compiles all 256 authored TerrainLit feature combinations in each material pass on the active graphics backend.</summary>
        /// <remarks>Main-thread batch entry point requiring graphics. At most eight passes; exits nonzero on missing graphics or compilation errors.
        /// Records active backend/build target. This is Editor backend evidence; mobile/console Player builds remain separate gates.</remarks>
        public static void RunTextureShaderBatch()
        {
            checks = 0; failures.Clear(); Material material = null;
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("This shader gate requires a graphics backend. Run without -nographics; headless numerical tests do not compile rendered passes.");
                Shader shader = Shader.Find(UCShaderProps.ShaderName);
                if (shader == null) throw new InvalidOperationException("TerrainLit shader is missing.");
                material = new Material(shader);
                if (material.passCount == 0 || material.passCount > 8) throw new InvalidOperationException("TerrainLit is outside the 1…8 pass verification envelope.");
                string[] features = TextureShaderFeatures; // UC-T07-12: Editor and Player gates include the same authored feature combinations.
                Debug.Log("UC-T07-12 compiler backend=" + SystemInfo.graphicsDeviceType + ", active target=" + EditorUserBuildSettings.activeBuildTarget + ", passes=" + material.passCount);
                for (int variant = 0; variant < 256; variant++)
                {
                    for (int bit = 0; bit < features.Length; bit++)
                        if ((variant & (1 << bit)) != 0) material.EnableKeyword(features[bit]); else material.DisableKeyword(features[bit]);
                    for (int pass = 0; pass < material.passCount; pass++)
                    {
                        ShaderUtil.CompilePass(material, pass, true);
                        Check(ShaderUtil.IsPassCompiled(material, pass) && !ShaderUtil.ShaderHasError(shader),
                            "UC-T07-12 TerrainLit feature combination " + variant + " pass " + pass + " compiles on " + SystemInfo.graphicsDeviceType + ".");
                    }
                }
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                        Check(false, "UC-T07-12 shader compiler: " + message.message);
            }
            catch (Exception error) { Check(false, "UC-T07 graphics compiler gate exception: " + error); }
            finally { if (material != null) UnityEngine.Object.DestroyImmediate(material); }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        private static void TestTextureAcceptance()
        {
            TestTextureBoundaryUploads(); TestTextureWeatherParameters(); TestAppliedSurfaceReference();
        }

        private static void TestAppliedSurfaceReference()
        {
            // UC-T07-12: test the actual authoring owner with accepted native ground and borrowed material content, not an invented rendered reference.
            GameObject root = null;
            var startup = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool untitled = string.IsNullOrEmpty(startup.path);
            string startupFolder = "Assets/UCTextureReferenceStartup_" + Guid.NewGuid().ToString("N");
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.TerrainWater);
            TerrainData data = null; TerrainLayer first = null, second = null; Material material = null;
            Texture2D texture = null, overrideTexture = null; TextureSurfaceReference.Result reference = null;
            string borrowedFolder = null, savedFolder = null; UnityEngine.SceneManagement.Scene reloaded = default;
            bool referenceParentExisted = AssetDatabase.IsValidFolder("Assets/UniverseCreator/ReferenceScenes");
            try
            {
                // UC-T07-12: Unity permits additive saved references beside a saved scene. Save only the verified empty test startup, never an author's unsaved scene.
                if (untitled)
                {
                    if (!startup.IsValid() || UnityEngine.SceneManagement.SceneManager.sceneCount != 1 || startup.GetRootGameObjects().Length != 0)
                        throw new InvalidOperationException("Run this native reference fixture with a saved scene or one empty untitled startup scene.");
                    AssetDatabase.CreateFolder("Assets", startupFolder.Substring(7));
                    if (!EditorSceneManager.SaveScene(startup, startupFolder + "/Startup.unity")) throw new InvalidOperationException("Could not save the empty reference-test startup scene.");
                }
                root = new GameObject("UC applied surface reference fixture"); root.SetActive(false);
                var world = root.AddComponent<UniverseCreatorObject>(); world.graph = graph;
                var run = world.GenerationScheduler.BeginRun();
                var position = new Vector3(-16, 10, -32); var size = new Vector3(16, 100, 16);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                overrideTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                // UC-T07-12: initialize native image data before saving these textures as reusable input assets.
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white }); texture.Apply(false, false);
                overrideTexture.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red }); overrideTexture.Apply(false, false);
                first = new TerrainLayer { diffuseTexture = texture }; second = new TerrainLayer { diffuseTexture = texture };
                data = new TerrainData { heightmapResolution = 33, alphamapResolution = 16, size = size, terrainLayers = new[] { first, second } };
                var heights = new float[33, 33]; var controls = new float[16, 16, 2];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.2f + x / 160f + z / 320f;
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) { controls[z, x, 0] = x / 15f; controls[z, x, 1] = 1 - x / 15f; }
                data.SetHeights(0, 0, heights); data.SetAlphamaps(0, 0, controls);
                var terrainRoot = UnityEngine.Terrain.CreateTerrainGameObject(data); terrainRoot.transform.SetParent(root.transform, false); terrainRoot.transform.position = position;
                var source = terrainRoot.GetComponent<UnityEngine.Terrain>(); material = new Material(Shader.Find(UCShaderProps.ShaderName)); source.materialTemplate = material;
                material.SetFloat(UCShaderProps.WetnessAmount, 0.25f);
                var block = new MaterialPropertyBlock(); block.SetFloat(UCShaderProps.WetnessAmount, 0.75f);
                block.SetColor(UCShaderProps.MacroVariationColor, Color.red); block.SetVector(UCShaderProps.LayerOpacity, Vector4.one * 0.5f);
                block.SetTexture("_Splat0", overrideTexture); block.SetTexture(UCShaderProps.DetailTex, overrideTexture); source.SetSplatMaterialPropertyBlock(block); // UC-T07-12: test both native layer samplers and an ordinary persistent material texture override.
                var bounds = new Rect(position.x, position.z, size.x, size.z);
                var ground = new RiverChannelSnapshot(run, "surface-reference", bounds, Array.Empty<ChannelGraphPublication>(), data.GetHeights(0, 0, 33, 33), position.y, size.y);
                Check(RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, "surface-reference", bounds, Array.Empty<ChannelGraphPublication>(), null, ground, world),
                    "UC-T07-12 applied reference fixture publishes the source terrain's accepted native ground.");
                long managed = world.ProductMemory.ManagedBytes, native = world.ProductMemory.NativeBytes;
                bool rejected = false;
                try { using var invalid = TextureSurfaceReference.Create(source, true); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("generated texture inputs"); }
                Check(rejected && world.ProductMemory.ManagedBytes == managed && world.ProductMemory.NativeBytes == native,
                    "UC-T07-12 saving a reusable reference rejects unpersisted borrowed texture inputs before creating scene assets or retaining memory.");
                reference = TextureSurfaceReference.Create(source, false);
                var referenceRoot = reference.Scene.GetRootGameObjects()[0];
                var terrains = referenceRoot.GetComponentsInChildren<UnityEngine.Terrain>(true); bool faithful = terrains.Length == 3;
                for (int variant = 0; variant < terrains.Length; variant++)
                {
                    var terrain = terrains[variant]; var copy = terrain.terrainData;
                    float[,,] uploaded = copy.GetAlphamaps(0, 0, copy.alphamapResolution, copy.alphamapResolution);
                    faithful &= terrain.transform.position == position && copy.size == size && copy.heightmapResolution == (32 << variant) + 1 &&
                        copy.alphamapResolution == (16 << variant) && terrain.gameObject.activeSelf == (variant == 0) &&
                        terrain.materialTemplate != material && terrain.materialTemplate.GetFloat(UCShaderProps.WetnessAmount) == 0.75f &&
                        terrain.materialTemplate.GetColor(UCShaderProps.MacroVariationColor) == Color.red &&
                        terrain.materialTemplate.GetVector(UCShaderProps.LayerOpacity) == Vector4.one * 0.5f && terrain.materialTemplate.GetTexture("_Splat0") == overrideTexture &&
                        copy.terrainLayers[0] != first && copy.terrainLayers[0].diffuseTexture == overrideTexture && copy.terrainLayers[1].diffuseTexture == texture;
                    for (int z = 0; z < copy.alphamapResolution; z++) for (int x = 0; x < copy.alphamapResolution; x++)
                        faithful &= Mathf.Abs(uploaded[z, x, 0] - x / (float)(copy.alphamapResolution - 1)) < 0.01f &&
                            Mathf.Abs(uploaded[z, x, 0] + uploaded[z, x, 1] - 1) < 0.005f;
                    ground.TryGround(position.x + 8, position.z + 8, out float expected);
                    faithful &= Mathf.Abs(terrain.SampleHeight(position + new Vector3(8, 0, 8)) + position.y - expected) < 0.01f;
                }
                var metadata = reference.Owned.Find(item => item is TextAsset) as TextAsset;
                Check(faithful && referenceRoot.GetComponentsInChildren<Camera>(true).Length == 3 && metadata != null &&
                    metadata.text.Contains("Material _Splat0") && metadata.text.Contains("2x2") && metadata.text.Contains(overrideTexture.graphicsFormat.ToString()),
                    "UC-T07-12 actual reference creation preserves accepted world heights, metre mapping, continuous controls, authored layer inputs and scalar/colour/vector/texture overrides across three resolutions.");
                var owned = reference.Owned.ToArray(); var scene = reference.Scene; reference.Dispose(); reference.Dispose(); reference = null;
                bool retired = !scene.isLoaded; foreach (var item in owned) retired &= item == null;
                source.GetSplatMaterialPropertyBlock(block);
                Check(retired && texture != null && overrideTexture != null && first != null && material != null && source.terrainData == data &&
                    material.GetFloat(UCShaderProps.WetnessAmount) == 0.25f && block.GetFloat(UCShaderProps.WetnessAmount) == 0.75f &&
                    world.ProductMemory.ManagedBytes == managed && world.ProductMemory.NativeBytes == native,
                    "UC-T07-12 reference scene disposal retires generated native objects and reservations exactly once while borrowed source content and live per-terrain overrides survive.");
                // UC-T07-12: real asset/scene reload must retain frozen overrides and borrowed texture identities.
                borrowedFolder = "Assets/UCTextureReferenceInputs_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(borrowedFolder));
                AssetDatabase.CreateAsset(texture, borrowedFolder + "/LayerTexture.asset");
                AssetDatabase.CreateAsset(overrideTexture, borrowedFolder + "/OverrideTexture.asset");
                AssetDatabase.SaveAssets();
                string overridePath = borrowedFolder + "/OverrideTexture.asset";
                string overrideGuid = AssetDatabase.AssetPathToGUID(overridePath);
                // UC-T07-12: asset creation imports the native textures; bind the actual persisted inputs before capturing a saved reference.
                overrideTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(overridePath);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(borrowedFolder + "/LayerTexture.asset");
                first.diffuseTexture = texture; second.diffuseTexture = texture;
                data.terrainLayers = new[] { first, second };
                block.SetTexture("_Splat0", overrideTexture); block.SetTexture(UCShaderProps.DetailTex, overrideTexture);
                source.SetSplatMaterialPropertyBlock(block);
                source.GetSplatMaterialPropertyBlock(block);
                Check(overrideTexture != null && texture != null && AssetDatabase.GetAssetPath(overrideTexture) == overridePath &&
                    overrideTexture.GetPixel(0, 0) == Color.red && texture.GetPixel(0, 0) == Color.white &&
                    block.GetTexture("_Splat0") == overrideTexture && block.GetTexture(UCShaderProps.DetailTex) == overrideTexture,
                    "UC-T07-12 saved reference fixture binds imported persistent texture inputs before capture.");
                reference = TextureSurfaceReference.Create(source, true);
                string savedPath = reference.SavedPath; savedFolder = System.IO.Path.GetDirectoryName(savedPath).Replace('\\', '/');
                var savedTerrain = reference.Scene.GetRootGameObjects()[0].GetComponentInChildren<UnityEngine.Terrain>(true);
                Check(AssetDatabase.GetAssetPath(savedTerrain.materialTemplate.GetTexture(UCShaderProps.DetailTex)) == overridePath &&
                    AssetDatabase.GetAssetPath(savedTerrain.terrainData.terrainLayers[0].diffuseTexture) == overridePath,
                    "UC-T07-12 persisted reference material and native layer point to the actual saved override asset before scene close. Expected=" + overridePath +
                    "; material=" + AssetDatabase.GetAssetPath(savedTerrain.materialTemplate.GetTexture(UCShaderProps.DetailTex)) +
                    "; layer=" + AssetDatabase.GetAssetPath(savedTerrain.terrainData.terrainLayers[0].diffuseTexture) + ".");
                reference.Dispose(); reference = null;
                reloaded = EditorSceneManager.OpenScene(savedPath, OpenSceneMode.Additive);
                // UC-T07-12: native scene close may unload an asset held only through non-serialized property blocks. Compare restored references to its saved asset identity, not an old native instance.
                overrideTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(overridePath);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(borrowedFolder + "/LayerTexture.asset");
                Check(overrideTexture != null && texture != null && !string.IsNullOrEmpty(overrideGuid) &&
                    AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(overrideTexture)) == overrideGuid,
                    "UC-T07-12 scene reload reacquires the same persisted borrowed texture GUIDs without regenerating inputs.");
                terrains = reloaded.GetRootGameObjects()[0].GetComponentsInChildren<UnityEngine.Terrain>(true);
                bool restored = terrains.Length == 3;
                foreach (var terrain in terrains)
                {
                    bool matches = terrain.transform.position == position && terrain.materialTemplate.GetFloat(UCShaderProps.WetnessAmount) == 0.75f &&
                        terrain.materialTemplate.GetColor(UCShaderProps.MacroVariationColor) == Color.red &&
                        terrain.materialTemplate.GetVector(UCShaderProps.LayerOpacity) == Vector4.one * 0.5f && terrain.materialTemplate.GetTexture(UCShaderProps.DetailTex) == overrideTexture &&
                        terrain.terrainData.terrainLayers[0].diffuseTexture == overrideTexture && terrain.terrainData.terrainLayers[1].diffuseTexture == texture && first.diffuseTexture == texture;
                    restored &= matches;
                    Check(matches, "UC-T07-12 saved reference region " + terrain.name + " matches accepted position/material inputs: position=" + terrain.transform.position +
                        "; wetness=" + terrain.materialTemplate.GetFloat(UCShaderProps.WetnessAmount) + "; macro=" + terrain.materialTemplate.GetColor(UCShaderProps.MacroVariationColor) +
                        "; opacity=" + terrain.materialTemplate.GetVector(UCShaderProps.LayerOpacity) + "; material texture=" + (terrain.materialTemplate.GetTexture(UCShaderProps.DetailTex) == overrideTexture) +
                        "; layer texture=" + (terrain.terrainData.terrainLayers[0].diffuseTexture == overrideTexture) + ".");
                }
                Check(restored && world.ProductMemory.ManagedBytes == managed && world.ProductMemory.NativeBytes == native,
                    "UC-T07-12 saved native reference reload preserves world coordinates, material overrides and borrowed texture references without keeping the authoring reservation. Managed=" +
                    world.ProductMemory.ManagedBytes + "/" + managed + "; native=" + world.ProductMemory.NativeBytes + "/" + native + ".");
                EditorSceneManager.CloseScene(reloaded, true); reloaded = default;
                run = world.GenerationScheduler.BeginRun(); rejected = false;
                try { using var invalid = TextureSurfaceReference.Create(source, false); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("publish current accepted ground"); }
                Check(rejected, "UC-T07-12 reference creation rejects a source terrain whose accepted ground belongs to a superseded run.");
                bounds = new Rect(position.x + 4, position.z + 4, 8, 8);
                ground = new RiverChannelSnapshot(run, "surface-reference", bounds, Array.Empty<ChannelGraphPublication>(), new float[,] { { 0.2f, 0.2f }, { 0.2f, 0.2f } }, position.y, size.y);
                RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, "surface-reference", bounds, Array.Empty<ChannelGraphPublication>(), null, ground, world);
                rejected = false;
                try { using var invalid = TextureSurfaceReference.Create(source, false); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("complete reference terrain"); }
                Check(rejected && world.ProductMemory.ManagedBytes == managed && world.ProductMemory.NativeBytes == native,
                    "UC-T07-12 incomplete accepted ground fails after admission without leaving a generated reference scene or memory residency.");
            }
            finally
            {
                reference?.Dispose(); if (reloaded.IsValid() && reloaded.isLoaded) EditorSceneManager.CloseScene(reloaded, true);
                if (savedFolder != null && AssetDatabase.IsValidFolder(savedFolder)) AssetDatabase.DeleteAsset(savedFolder);
                if (!referenceParentExisted && AssetDatabase.IsValidFolder("Assets/UniverseCreator/ReferenceScenes") &&
                    System.IO.Directory.GetFileSystemEntries("Assets/UniverseCreator/ReferenceScenes").Length == 0)
                    AssetDatabase.DeleteAsset("Assets/UniverseCreator/ReferenceScenes");
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(graph);
                if (data != null) UnityEngine.Object.DestroyImmediate(data); if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (first != null) UnityEngine.Object.DestroyImmediate(first); if (second != null) UnityEngine.Object.DestroyImmediate(second);
                if (borrowedFolder != null && AssetDatabase.IsValidFolder(borrowedFolder)) AssetDatabase.DeleteAsset(borrowedFolder);
                if (texture != null && !AssetDatabase.Contains(texture)) UnityEngine.Object.DestroyImmediate(texture);
                if (overrideTexture != null && !AssetDatabase.Contains(overrideTexture)) UnityEngine.Object.DestroyImmediate(overrideTexture);
                if (untitled && AssetDatabase.IsValidFolder(startupFolder))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    AssetDatabase.DeleteAsset(startupFolder);
                }
            }
        }

        private static void TestTextureBoundaryUploads()
        {
            TerrainData data = null; TerrainLayer first = null, second = null;
            try
            {
                first = new TerrainLayer(); second = new TerrainLayer();
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 100, 16), terrainLayers = new[] { first, second } };
                for (int shape = 0; shape < 2; shape++)
                foreach (int sourceResolution in new[] { 17, 33 })
                foreach (int controlResolution in new[] { 16, 32, 64 })
                {
                    data.alphamapResolution = controlResolution; float[,,] previous = null;
                    for (int cell = 0; cell < 2; cell++)
                    {
                        var a = new Matrix(new CoordRect(-7, 3, sourceResolution, sourceResolution)) {
                            worldPos = new Vector3(cell * 16, 0, 0), worldSize = data.size };
                        var b = a.Blank();
                        float Expected(float x, float z)
                        {
                            float signed = shape == 0 ? (x + z - 24) / Mathf.Sqrt(2) : new Vector2(x - 16, z - 8).magnitude - 6;
                            float t = Mathf.Clamp01(0.5f - signed / 4); // Four-metre transition, independent of both resolutions.
                            return t * t * (3 - 2 * t);
                        }
                        for (int z = 0; z < sourceResolution; z++) for (int x = 0; x < sourceResolution; x++)
                        {
                            int i = z * sourceResolution + x; a.arr[i] = Expected(cell * 16 + x * 16f / (sourceResolution - 1), z * 16f / (sourceResolution - 1)); b.arr[i] = 1 - a.arr[i];
                        }
                        var surface = new SurfaceProduct { maxLayers = 2, splat = new SplatProduct() };
                        surface.splat.layers[0] = new LayerProduct(); surface.splat.layers[1] = new LayerProduct();
                        surface.splat.coverage[0] = a; surface.splat.coverage[1] = b;
                        data.SetAlphamaps(0, 0, UCTextureGraphApplier.BuildCoverage(surface, data, a.worldPos));
                        float[,,] uploaded = data.GetAlphamaps(0, 0, controlResolution, controlResolution); bool correct = true, border = true;
                        float spacing = 16f / (sourceResolution - 1);
                        // UC-T07-12: a finer native control texture interpolates the captured lattice, not unsampled analytic curvature.
                        // This four-metre smoothstep has |d²membership/dx²| and |d²membership/dz²| <= 3/8 m^-2;
                        // bilinear source error is at most (3/32)*spacing², plus one native 8-bit coverage step.
                        float analyticBound = 3f / 32 * spacing * spacing + 0.005f;
                        float LatticeExpectation(float px, float pz)
                        {
                            int ix = Mathf.Min(Mathf.FloorToInt(px), sourceResolution - 2), iz = Mathf.Min(Mathf.FloorToInt(pz), sourceResolution - 2);
                            float x0 = cell * 16 + ix * spacing, z0 = iz * spacing;
                            return Mathf.Lerp(Mathf.Lerp(Expected(x0, z0), Expected(x0 + spacing, z0), px - ix),
                                Mathf.Lerp(Expected(x0, z0 + spacing), Expected(x0 + spacing, z0 + spacing), px - ix), pz - iz);
                        }
                        for (int z = 0; z < controlResolution; z++) for (int x = 0; x < controlResolution; x++)
                            correct &= uploaded[z, x, 0] >= 0 && uploaded[z, x, 1] >= 0 && Mathf.Abs(uploaded[z, x, 0] + uploaded[z, x, 1] - 1) < 0.005f &&
                                Mathf.Abs(uploaded[z, x, 0] - LatticeExpectation(x * (sourceResolution - 1f) / (controlResolution - 1), z * (sourceResolution - 1f) / (controlResolution - 1))) < 0.005f &&
                                Mathf.Abs(uploaded[z, x, 0] - Expected(cell * 16 + x * 16f / (controlResolution - 1), z * 16f / (controlResolution - 1))) < analyticBound;
                        Check(correct, "UC-T07-12 " + (shape == 0 ? "diagonal" : "curved") + " final native memberships retain metre width and normalized coverage: source=" + sourceResolution + ", control=" + controlResolution + ", cell=" + cell + ".");
                        if (previous != null)
                        {
                            for (int z = 0; z < controlResolution; z++) border &= Mathf.Abs(previous[z, controlResolution - 1, 0] - uploaded[z, 0, 0]) < 0.005f;
                            Check(border, "UC-T07-12 adjacent native controls retain the same shared physical boundary: shape=" + shape + ", source=" + sourceResolution + ", control=" + controlResolution + ".");
                        }
                        previous = uploaded;
                    }
                }
            }
            finally
            {
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (first != null) UnityEngine.Object.DestroyImmediate(first); if (second != null) UnityEngine.Object.DestroyImmediate(second);
            }
        }

        private static void TestTextureWeatherParameters()
        {
            var graph = ScriptableObject.CreateInstance<Graph>(); var memory = new ProductMemoryBudget(16 * 1024 * 1024); TileData tile = null;
            try
            {
                graph.SetDomain(UCGraphDomain.TextureMaterialShader);
                var weather = new TextureScalarParameterNode { id = "surface-weather", value = 0.25f };
                var snowline = new TextureScalarParameterNode { id = "surface-snowline", value = 500 };
                var wet = new WetnessNode { id = "surface-wet" }; var snow = new SnowCoverNode { id = "surface-snow" };
                graph.nodes.Add(weather); graph.nodes.Add(snowline); graph.nodes.Add(wet); graph.nodes.Add(snow);
                graph.connections.Add(new Connection(weather.id, 0, wet.id, 1)); graph.connections.Add(new Connection(wet.id, 0, snow.id, 0));
                graph.connections.Add(new Connection(weather.id, 0, snow.id, 2)); graph.connections.Add(new Connection(snowline.id, 0, snow.id, 3));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                foreach (float amount in new[] { 0.25f, 0.75f })
                {
                    weather.value = amount; tile = MemoryTestTile(memory);
                    var evaluator = new GraphEvaluator(graph);
                    tile.Bind(evaluator); // UC-T07-12: direct fixture pulls must execute their wired weather and snowline parameters.
                    var surface = (SurfaceProduct)evaluator.PullNode(snow, 0, tile, new StopToken());
                    Check(surface.floats[UCShaderProps.WetnessAmount] == amount && surface.floats[UCShaderProps.SnowCoverage] == amount &&
                        surface.floats[UCShaderProps.SnowAltitude] == 500 && surface.bakedMaps.Count == 0,
                        "UC-T07-12 authored weather scalar " + amount + " reaches wet/snow material uniforms without height/image baking.");
                    tile.ReleaseProductMemory(); tile = null;
                }
            }
            finally { tile?.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
