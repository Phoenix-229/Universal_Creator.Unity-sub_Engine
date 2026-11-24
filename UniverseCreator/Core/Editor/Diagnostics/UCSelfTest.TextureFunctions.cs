// UC-T07-08: actual function capture/call, portable references, Undo and typed preview resource ownership.
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTextureFunctions()
        {
            string folder = "Assets/__UCTextureFunctions_" + Guid.NewGuid().ToString("N");
            var function = ScriptableObject.CreateInstance<Graph>();
            var first = ScriptableObject.CreateInstance<Graph>(); var second = ScriptableObject.CreateInstance<Graph>();
            Graph roundtrip = null, pasted = null; Texture2D borrowed = null;
            var memory = new ProductMemoryBudget(16L * 1024 * 1024);
            var tiles = new List<TileData>(); var snapshots = new List<GenerationSnapshot>();
            var context = new SnapshotRetirementContext(); var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                function.SetDomain(UCGraphDomain.TextureMaterialShader); first.SetDomain(UCGraphDomain.TextureMaterialShader); second.SetDomain(UCGraphDomain.TextureMaterialShader); // UC-T07-08: actual serialized domain.
                var input = new TextureFunctionInputNode { id = "function-scale", parameterName = "Scale", scalar = 2 };
                var multiply = new TextureScalarMathNode { id = "function-multiply", b = 3 };
                var output = new TextureFunctionOutputNode { id = "function-result", resultName = "Scaled" };
                var otherOutput = new TextureFunctionOutputNode { id = "function-other-result", resultName = "Shared result" };
                function.nodes.Add(input); function.nodes.Add(multiply); function.nodes.Add(output); function.nodes.Add(otherOutput);
                function.connections.Add(new Connection(input.id, 0, multiply.id, 0));
                function.connections.Add(new Connection(multiply.id, 0, output.id, 0));
                function.connections.Add(new Connection(multiply.id, 0, otherOutput.id, 0));
                AssetDatabase.CreateFolder("Assets", folder.Substring(7)); AssetDatabase.CreateAsset(function, folder + "/Function.asset");
                var callA = new TextureFunctionCallNode { id = "function-call-a", function = function }; callA.RefreshPorts();
                var callB = new TextureFunctionCallNode { id = "function-call-b", function = function }; callB.RefreshPorts();
                var parameterA = new ShaderParameterNode { id = "function-material-a", propertyName = UCShaderProps.Brightness };
                var parameterB = new ShaderParameterNode { id = "function-material-b", propertyName = UCShaderProps.Brightness };
                var independent = new TextureScalarMathNode { id = "function-unrelated", a = 7, b = 1 };
                var callerValue = new TextureScalarParameterNode { id = "function-caller-value", value = 5 };
                first.nodes.Add(callA); first.nodes.Add(parameterA); first.nodes.Add(independent);
                second.nodes.Add(callB); second.nodes.Add(parameterB); second.nodes.Add(callerValue);
                first.connections.Add(new Connection(callA.id, 0, parameterA.id, 1));
                second.connections.Add(new Connection(callerValue.id, 0, callB.id, 0));
                second.connections.Add(new Connection(callB.id, 0, parameterB.id, 1));
                var retained = new Dictionary<string, RetainedProduct>();
                var snapA = GenerationSnapshot.CaptureForDomains(first, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread);
                var snapB = GenerationSnapshot.CaptureForDomains(second, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread);
                snapshots.Add(snapA); snapshots.Add(snapB);
                TileData tileA = MemoryTestTile(memory), tileB = MemoryTestTile(memory); tiles.Add(tileA); tiles.Add(tileB);
                tileA.RetainGenerationInputs(snapA); tileB.RetainGenerationInputs(snapB); tileA.retained = retained;
                var evalA = new GraphEvaluator(snapA.Graph); var evalB = new GraphEvaluator(snapB.Graph); tileA.Bind(evalA); tileB.Bind(evalB);
                var surfaceA = (SurfaceProduct)evalA.PullNode(snapA.Graph.GetNodeById(parameterA.id), 0, tileA, new StopToken());
                var surfaceB = (SurfaceProduct)evalB.PullNode(snapB.Graph.GetNodeById(parameterB.id), 0, tileB, new StopToken());
                Check(surfaceA.floats[UCShaderProps.Brightness] == 6 && surfaceB.floats[UCShaderProps.Brightness] == 15,
                    "UC-T07-08 one named typed function serves two graphs with distinct parameter values and actual shader parameter consumers.");
                object oldIndependent = evalA.PullNode(snapA.Graph.GetNodeById(independent.id), 0, tileA, new StopToken());
                ulong oldCallHash = snapA.Graph.GetNodeById(callA.id).contentHash;
                Check((float)evalA.PullNode(snapA.Graph.GetNodeById(callA.id), 1, tileA, new StopToken()) == 6,
                    "UC-T07-08 multiple function outputs share one child tile and its cached upstream products.");
                Undo.RegisterCompleteObjectUndo(function, "Edit function multiplier"); multiply.b = 4; EditorUtility.SetDirty(function); Undo.FlushUndoRecordObjects();
                var changed = GenerationSnapshot.CaptureForDomains(first, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread); snapshots.Add(changed);
                TileData changedTile = MemoryTestTile(memory); tiles.Add(changedTile); changedTile.retained = retained; changedTile.RetainGenerationInputs(changed);
                var changedEval = new GraphEvaluator(changed.Graph); changedTile.Bind(changedEval);
                var changedSurface = (SurfaceProduct)changedEval.PullNode(changed.Graph.GetNodeById(parameterA.id), 0, changedTile, new StopToken());
                object reused = changedEval.PullNode(changed.Graph.GetNodeById(independent.id), 0, changedTile, new StopToken());
                Check(changedSurface.floats[UCShaderProps.Brightness] == 8 && changed.Graph.GetNodeById(callA.id).contentHash != oldCallHash &&
                    ReferenceEquals(oldIndependent, reused), "UC-T07-08 function body edits invalidate dependent products while the unrelated retained branch reuses its actual product.");
                snapA.Release();
                Check((float)evalA.PullNode(snapA.Graph.GetNodeById(callA.id), 1, tileA, new StopToken()) == 6,
                    "UC-T07-08 releasing the parent snapshot owner preserves frozen function values through the tile's final consumer lease.");
                Undo.PerformUndo();
                Check(((TextureScalarMathNode)function.GetNodeById(multiply.id)).b == 3,
                    "UC-T07-08 function body parameter Undo restores the shared authored dependency.");
                Undo.PerformRedo();
                AssetDatabase.SaveAssets();
                roundtrip = GraphSerializer.FromJson(GraphSerializer.ToJson(first));
                pasted = ScriptableObject.CreateInstance<Graph>(); pasted.SetDomain(UCGraphDomain.TextureMaterialShader);
                var pastedNodes = GraphSerializer.PasteJson(pasted, GraphSerializer.SelectionToJson(first, new Generator[] { callA }));
                var copiedCall = (TextureFunctionCallNode)roundtrip.GetNodeById(callA.id);
                Check(copiedCall.function == function && copiedCall.inputPorts[0].endpointId == input.id && copiedCall.outputPorts[1].endpointId == otherOutput.id &&
                    pastedNodes.Count == 1 && ((TextureFunctionCallNode)pastedNodes[0]).function == function && pastedNodes[0].id != callA.id,
                    "UC-T07-08 portable reload/copy-paste preserves function asset references and endpoint slots while assigning a fresh caller ID.");
                var reloaded = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Function.asset");
                Check(reloaded == copiedCall.function && GraphValidationReport.Analyze(roundtrip, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread, requireOutput: false).IsValid, // UC-T07-08: this sampled function caller has no world/material terminal.
                    "UC-T07-08 persisted function references restore a valid callable signature after graph reload.");
                ((TextureFunctionInputNode)function.GetNodeById(input.id)).type = PortType.Color;
                Check(callA.SettingsDiagnostic != null && callA.GetInputs()[0].type == PortType.Scalar,
                    "UC-T07-08 retyped parameters diagnose incompatible saved ports without silently changing their wire currency.");
                ((TextureFunctionInputNode)function.GetNodeById(input.id)).type = PortType.Scalar;
                function.nodes.Remove(function.GetNodeById(output.id));
                Check(callA.SettingsDiagnostic != null && callA.GetOutputs().Length == 2,
                    "UC-T07-08 removed function results leave stable saved slots and a recoverable diagnostic.");
                function.nodes.Add(output);
                function.nodes.Add(new TextureFunctionCallNode { function = function });
                Check(callA.SettingsDiagnostic != null && !GraphValidationReport.Analyze(first, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread, requireOutput: false).IsValid, // UC-T07-08: prove recursion fails without a missing-terminal false positive.
                    "UC-T07-08 recursive function assets are diagnosed before capture instead of overflowing evaluation.");
                function.nodes.RemoveAt(function.nodes.Count - 1);
                Check(callA.SettingsDiagnostic == null, "UC-T07-08 restoring endpoints and removing recursion recovers the saved caller signature.");
                float previewA = 0, previewB = 0;
                using (var schedulerA = new GraphPreviewScheduler((id, samples, status) => { }, (id, preview, status) =>
                    { if (preview?.Surface != null) previewA = preview.Surface.floats[UCShaderProps.Brightness]; preview?.Dispose(); }))
                using (var schedulerB = new GraphPreviewScheduler((id, samples, status) => { }, (id, preview, status) =>
                    { if (preview?.Surface != null) previewB = preview.Surface.floats[UCShaderProps.Brightness]; preview?.Dispose(); }))
                {
                    schedulerA.Request(first, parameterA.id); schedulerB.Request(second, parameterB.id);
                    WaitGraphPreview(schedulerA, context); WaitGraphPreview(schedulerB, context);
                    Check(previewA == 8 && previewB == 20, "UC-T07-08 two canvases preview distinct consumers of the same saved function.");
                    ((TextureScalarMathNode)function.GetNodeById(multiply.id)).b = 5; EditorUtility.SetDirty(function);
                    schedulerA.Pump(); schedulerB.Pump(); WaitGraphPreview(schedulerA, context); WaitGraphPreview(schedulerB, context);
                    Check(previewA == 10 && previewB == 25 && surfaceA.floats[UCShaderProps.Brightness] == 6,
                        "UC-T07-08 function dependency edits refresh both actual previews while previously accepted material descriptions remain unchanged.");
                }
                var descriptor = new TextureImageDescriptor(2, 2, TextureImageColourSpace.SRgb, TextureImageSemantic.Colour);
                var image = tileB.CreateTextureImage(descriptor, new[] { Color.red, Color.green, Color.blue, Color.white });
                using (var preview = GraphTexturePreview.Sample(image, tileB))
                    Check(preview.Pixels.Length == 1024 && preview.Pixels[0] == Color.red && preview.Encoding == TextureImageColourSpace.SRgb &&
                        image[0] == Color.red, "UC-T07-08 typed image thumbnails sample real pixels and retain their encoding without modifying the source.");
                borrowed = new Texture2D(2, 2); borrowed.filterMode = FilterMode.Point; borrowed.wrapMode = TextureWrapMode.Repeat;
                var layer = new LayerProduct { albedo = new TextureRef(borrowed), metallic = 0.25f, smoothness = 0.7f };
                using (var preview = GraphTexturePreview.Sample(layer, changedTile))
                {
                    preview.CreateMaterial();
                    Check(preview.Material != null && preview.MaterialEditor != null && preview.Material.GetTexture("_Splat0") == borrowed &&
                        preview.Material.GetFloat("_Metallic0") == 0.25f && borrowed.filterMode == FilterMode.Point && borrowed.wrapMode == TextureWrapMode.Repeat,
                        "UC-T07-08 lit material previews use actual PBR properties and borrowed textures without changing imported sampling or applying assets.");
                }
            }
            finally
            {
                foreach (var tile in tiles) tile.ReleaseProductMemory(); foreach (var snapshot in snapshots) snapshot.Release(); context.Drain();
                SynchronizationContext.SetSynchronizationContext(previous);
                if (borrowed != null) UnityEngine.Object.DestroyImmediate(borrowed);
                if (roundtrip != null) UnityEngine.Object.DestroyImmediate(roundtrip); if (pasted != null) UnityEngine.Object.DestroyImmediate(pasted);
                UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                else if (function != null) UnityEngine.Object.DestroyImmediate(function);
                Check(memory.ManagedBytes == 0, "UC-T07-08 function snapshots/views and native previews retire all admitted resources after their last consumers.");
            }
        }
    }
}
