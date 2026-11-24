// UC-T07-06: reusable layer parameters, independent siblings and actual ordered shader-property publication.
using UnityEngine;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private sealed class MaterialLayerProbe : TextureNode
        {
            internal LayerProduct value;
            public override string NodeName => "Material layer fixture";
            public override Port[] GetInputs() => SharedPorts.None; // UC-T07-06: constant fixture satisfies the same node contract as production sources.
            public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
            public override Port[] GetOutputs() => TexturePorts.LayerOut;
            public override object Generate(int outputIndex, TileData tile, StopToken stop) => value;
        }

        private static void TestMaterialLayerParameters()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>(); var memory = new ProductMemoryBudget(8 * 1024 * 1024);
            var tile = MemoryTestTile(memory);
            try
            {
                var original = new LayerProduct { smoothness = 0.1f, uv = new UVTransform { tileSizeMetres = new Vector2(2, 3) } };
                var source = new MaterialLayerProbe { id = "material-layer-source", value = original };
                var edit = new MaterialLayerParametersNode { id = "material-layer-edit", roughness = 0.2f, opacity = 0.4f,
                    occlusion = 0.7f, emission = new Color(2, 1, 0, 1), albedoAlphaIsOpacity = true };
                graph.nodes.Add(source); graph.nodes.Add(edit); graph.connections.Add(new Connection(source.id, 0, edit.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); var evaluator = new GraphEvaluator(graph);
                tile.Bind(evaluator); // UC-T07-06: bind direct pulls so the fixture consumes its real upstream layer.
                var layer = evaluator.PullNode(edit, 0, tile, new StopToken()) as LayerProduct;
                Check(layer != null && Mathf.Abs(layer.smoothness - 0.8f) < 0.00001f && original.smoothness == 0.1f && original.opacity == 1 &&
                    !ReferenceEquals(original, layer) && !ReferenceEquals(original.uv, layer.uv) && layer.uv.tileSizeMetres == new Vector2(2, 3),
                    "UC-T07-06 reusable layer roughness converts once while unchanged maps/UVs and sibling metadata remain intact.");
                var surface = new SurfaceProduct { splat = new SplatProduct() }; surface.splat.layers[0] = layer; surface.PublishLayerParameters();
                Check(surface.vectors[UCShaderProps.LayerOpacity] == new Vector4(0.4f, 1, 1, 1) &&
                    surface.vectors[UCShaderProps.LayerAlphaOpacity] == new Vector4(1, 0, 0, 0) &&
                    surface.vectors[UCShaderProps.LayerOcclusion] == new Vector4(0.7f, 1, 1, 1) && surface.colors[UCShaderProps.LayerEmissionPrefix + "0"].r == 2,
                    "UC-T07-06 emission, occlusion and explicit opacity/alpha publish in control-map channel order with opaque missing-slot defaults.");
                var sibling = surface.Copy(); sibling.SetVector(UCShaderProps.LayerOpacity, Vector4.zero);
                Check(surface.vectors[UCShaderProps.LayerOpacity].x == 0.4f && ReferenceEquals(surface.splat.layers[0].albedo, sibling.splat.layers[0].albedo),
                    "UC-T07-06 packed material parameter edits preserve sibling descriptors without image work.");
                Vector4 weights = MaterialWeightMath.HeightBlend(new Vector4(0.5f, 0.5f, 0, 0), new Vector4(0.1f, 0.2f, 10, 20), 0.2f);
                Check(weights.z == 0 && weights.w == 0 && Mathf.Abs(weights.x + weights.y - 1) < 0.00001f && weights.y > weights.x && weights.x > 0,
                    "UC-T07-06 absent tall materials never suppress existing coverage in normalized height blending.");
                Check(MaterialWeightMath.Normalize(Vector4.zero) == new Vector4(1, 0, 0, 0), "UC-T07-06 empty material membership has an explicit first-layer fallback.");
                TestMaterialLayerApply(surface, memory);
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); Check(memory.ManagedBytes == 0, "UC-T07-06 material fixtures retire admitted products."); }
        }

        private static void TestMaterialLayerApply(SurfaceProduct surface, ProductMemoryBudget memory)
        {
            string folder = "Assets/UC_MaterialLayer_" + System.Guid.NewGuid().ToString("N");
            Material material = null; TerrainData data = null; GameObject root = null;
            try
            {
                Shader shader = Shader.Find(UCShaderProps.ShaderName);
                Check(shader != null, "UC-T07-06 layer apply resolves the actual supported terrain shader."); if (shader == null) return;
                AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                material = new Material(shader); AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 10, 32) };
                root = UnityEngine.Terrain.CreateTerrainGameObject(data); var terrain = root.GetComponent<UnityEngine.Terrain>(); terrain.materialTemplate = material;
                var outputs = new OutputBag { surface = surface, surfaceShaderTarget = (int)ShaderTarget.ExistingMaterial,
                    surfaceOutputFolder = folder, surfaceWriteLayers = false };
                surface.SetFloat(UCShaderProps.EnableSurfaceClip, 1); surface.SetFloat(UCShaderProps.SurfaceCutoff, 0.25f);
                bool applied = UniverseCreator.Editor.Utilities.UCTextureGraphApplier.Apply(outputs, terrain, memory, out SurfaceProduct accepted);
                Check(applied && accepted != null && material.GetVector(UCShaderProps.LayerOpacity).x == 0.4f &&
                    material.GetColor(UCShaderProps.LayerEmissionPrefix + "0").r == 2 && material.GetFloat(UCShaderProps.SurfaceCutoff) == 0.25f,
                    "UC-T07-06 production Apply delivers ordered PBR/emission/opacity parameters to real TerrainLit consumers.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                if (material != null && !AssetDatabase.Contains(material)) UnityEngine.Object.DestroyImmediate(material);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
