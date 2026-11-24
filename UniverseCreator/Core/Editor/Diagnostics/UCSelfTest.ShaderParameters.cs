// UC-T07-07: installed shader schema, supported expressions/stages and cheap actual material updates.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestShaderParameters()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>(); var memory = new ProductMemoryBudget(8 * 1024 * 1024);
            var tile = MemoryTestTile(memory); Material material = null; Texture2D borrowed = null;
            try
            {
                var expression = new TextureScalarMathNode { id = "shader-scalar-expression", a = 2, b = 3, operation = ColorMathOp.Multiply };
                var parameter = new ShaderParameterNode { id = "shader-uniform", propertyName = UCShaderProps.Brightness };
                var output = new ShaderOutputNode { id = "shader-parameter-output" };
                graph.nodes.Add(expression); graph.nodes.Add(parameter); graph.nodes.Add(output);
                graph.connections.Add(new Connection(expression.id, 0, parameter.id, 1));
                graph.connections.Add(new Connection(parameter.id, 0, output.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph); new GraphEvaluator(graph).Evaluate(tile, new StopToken());
                var surface = tile.outputs.surface;
                Check(surface != null && surface.floats[UCShaderProps.Brightness] == 6 && surface.ShaderOutput.Stage == ShaderExpressionStage.SurfaceFragment,
                    "UC-T07-07 typed scalar expressions reach supported fragment uniforms through the real shader terminal.");
                Shader shader = Shader.Find(UCShaderProps.ShaderName); material = new Material(shader);
                borrowed = new Texture2D(2, 2); material.SetTexture("_Splat0", borrowed);
                long bytes = memory.ManagedBytes; int keywords = material.shaderKeywords.Length;
                ShaderOutputDescriptor.ApplyParameters(surface, material);
                Check(material.GetFloat(UCShaderProps.Brightness) == 6 && material.GetTexture("_Splat0") == borrowed &&
                    memory.ManagedBytes == bytes && material.shaderKeywords.Length == keywords,
                    "UC-T07-07 actual runtime parameter update preserves textures, keyword variants and admitted image bytes.");
                bool stageRejected = false, typeRejected = false, keywordRejected = false;
                try { new ShaderOutputDescriptor(ShaderTarget.UniverseCreatorTerrainLit, ShaderExpressionStage.Vertex); } catch (ArgumentOutOfRangeException) { stageRejected = true; }
                var wrong = new SurfaceProduct(); wrong.SetColor(UCShaderProps.Brightness, Color.white);
                try { ShaderOutputDescriptor.ApplyParameters(wrong, material); } catch (ArgumentException) { typeRejected = true; }
                wrong = new SurfaceProduct(); wrong.SetKeyword("_UC_UNSUPPORTED_FEATURE");
                try { ShaderOutputDescriptor.ValidateFeatureKeywords(wrong); } catch (ArgumentException) { keywordRejected = true; }
                Check(stageRejected && typeRejected && keywordRejected && material.GetFloat(UCShaderProps.Brightness) == 6,
                    "UC-T07-07 unsupported stages, parameter types and feature variants are diagnosed before material mutation.");
                output.stage = ShaderExpressionStage.Vertex;
                Check(!GraphValidationReport.Analyze(graph).IsValid, "UC-T07-07 unsupported shader stage produces a graph-node diagnostic before capture.");
                output.stage = ShaderExpressionStage.SurfaceFragment;
                material.SetFloat("_EnableBeach", 1);
                UniverseCreator.Editor.Drawing.UCTerrainShaderGUI.SyncAllKeywords(material);
                Check(material.IsKeywordEnabled("_UC_BEACH_ON"), "UC-T07-07 native beach feature toggle activates its declared shader variant.");
                var missing = new SurfaceProduct(); missing.SetFloat("_UC_UnavailableParameter", 1);
                ShaderOutputDescriptor.ValidateProperties(missing, shader, true);
                Check(missing.notices.Count == 1, "UC-T07-07 missing custom-material dependencies produce an actionable notice.");
            }
            finally
            {
                tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (borrowed != null) UnityEngine.Object.DestroyImmediate(borrowed);
                Check(memory.ManagedBytes == 0, "UC-T07-07 shader parameter fixtures retire their admitted products.");
            }
        }
    }
}
