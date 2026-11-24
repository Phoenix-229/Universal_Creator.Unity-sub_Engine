using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UniverseCreator.Editor.GraphUI;
using UniverseCreator.Editor.Windows;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Utilities;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T01-09: editor-only fixture; counters distinguish admission from preparation, execution and publication.
        [Serializable]
        private sealed class ValidationProbeNode : Generator
        {
            internal ValidationProbeNode() { }
            internal static int prepared, generated;
            public PortType inputType = PortType.Matrix, outputType = PortType.Matrix;
            public NodeExecutionDomain domain = NodeExecutionDomain.Worker;
            public bool multiple, throwPorts, absentPorts, wrongDirection;
            public override string NodeName { get { return "Validation probe"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }
            public override NodeExecutionDomain ExecutionDomain { get { return domain; } }
            /// <inheritdoc />
            public override Port[] GetInputs()
            {
                if (throwPorts) throw new InvalidOperationException("UC-T01-09 deliberate port failure");
                if (absentPorts) return null;
                return new[] { new Port("Input", inputType, wrongDirection ? PortDirection.Output : PortDirection.Input, multiple) };
            }
            /// <inheritdoc />
            public override Port[] GetOutputs() { return new[] { new Port("Output", outputType, PortDirection.Output, true) }; }
            /// <inheritdoc />
            public override void PrepareMainThread() { prepared++; }
            /// <inheritdoc />
            public override object Generate(int outputIndex, TileData tile, StopToken stopToken)
            {
                System.Threading.Interlocked.Increment(ref generated);
                Matrix input = tile.PullInput(this, 0, stopToken) as Matrix;
                if (input != null) return new Matrix(input);
                Matrix result = tile.CreateMatrix(); result.Fill(0.375f); return result;
            }
        }

        private static Graph ValidationGraph(out ValidationProbeNode source, out HeightOutputNode output)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            source = new ValidationProbeNode { id = "source" };
            output = new HeightOutputNode { id = "height", enforceSeaLevelFloor = false,
                enforceContinentalRise = false, enforceCoastalShelf = false };
            graph.nodes.Add(source); graph.nodes.Add(output);
            graph.connections.Add(new Connection(source.id, 0, output.id, 0));
            return graph;
        }

        private static GraphDiagnostic ValidationDiagnostic(GraphValidationReport report, string code, Generator node = null)
        {
            return report.Diagnostics.FirstOrDefault(d => d.Code == code && (node == null || ReferenceEquals(d.Node, node)));
        }

        private static void TestGraphValidation()
        {
            TestGraphValidationMetadata();
            TestGraphValidationCycles();
            TestGraphValidationPortals(false);
            TestGraphValidationPortals(true);
            TestGraphValidationPublication();
            TestGraphValidationCanvas();
        }

        private static void TestGraphValidationMetadata()
        {
            Graph graph = ValidationGraph(out ValidationProbeNode source, out HeightOutputNode output);
            try
            {
                string original = JsonUtility.ToJson(graph);
                GraphValidationReport report = GraphValidationReport.Analyze(graph, NodeExecutionDomain.Worker);
                Check(report.IsValid && report.Diagnostics.Count == 0 && JsonUtility.ToJson(graph) == original,
                    "UC-T01-09 a valid graph is analyzed without changing authored data.");
                bool immutable = false;
                try { ((IList<GraphDiagnostic>)report.Diagnostics).Clear(); }
                catch (NotSupportedException) { immutable = true; }
                Check(immutable, "UC-T01-09 diagnostic collections reject caller mutation.");
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(null), "CONTAINERS") != null,
                    "UC-T01-09 an absent graph reports missing containers.");
                var edges = graph.connections; graph.connections = null;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "CONTAINERS") != null && graph.connections == null,
                    "UC-T01-09 absent containers are diagnosed without inventing authored data.");
                graph.connections = edges;
                graph.nodes.Add(null);
                report = GraphValidationReport.Analyze(graph);
                Check(ValidationDiagnostic(report, "MISSING_TYPE")?.NodeSlot == 2 && graph.nodes.Count == 3 && graph.connections.Count == 1,
                    "UC-T01-09 missing types retain their slot and wires with actionable diagnostics.");
                graph.nodes.RemoveAt(2);
                var duplicate = new ConstantNode { id = source.id }; graph.nodes.Add(duplicate);
                report = GraphValidationReport.Analyze(graph);
                Check(ValidationDiagnostic(report, "DUPLICATE_ID", source) != null && ValidationDiagnostic(report, "DUPLICATE_ID", duplicate) != null,
                    "UC-T01-09 duplicate IDs attach errors to both affected nodes.");
                graph.nodes.RemoveAt(2); graph.nodes.Add(source);
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "DUPLICATE_NODE", source) != null,
                    "UC-T01-09 duplicate object slots are rejected without a dictionary exception.");
                graph.nodes.RemoveAt(2); source.id = null;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "EMPTY_ID", source) != null,
                    "UC-T01-09 null IDs are diagnosed without losing the node.");
                source.id = "source";
                graph.connections[0] = new Connection("missing", 0, output.id, 0);
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "MISSING_SOURCE", output)?.Direction == PortDirection.Input,
                    "UC-T01-09 a missing source marks the surviving consumer socket.");
                graph.connections[0] = new Connection(source.id, 0, "missing", 0);
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "MISSING_TARGET", source)?.Direction == PortDirection.Output,
                    "UC-T01-09 a missing target marks the surviving source socket.");
                graph.connections[0] = new Connection(source.id, -1, output.id, 999);
                report = GraphValidationReport.Analyze(graph);
                Check(ValidationDiagnostic(report, "PORT_INDEX", source)?.PortIndex == -1 && ValidationDiagnostic(report, "PORT_INDEX", output)?.PortIndex == 999,
                    "UC-T01-09 negative and oversized socket indexes are both rejected.");
                graph.connections[0] = new Connection(source.id, 0, output.id, 0);
                source.outputType = PortType.Color;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "TYPE", output)?.PortIndex == 0,
                    "UC-T01-09 incompatible currencies attach a type error to the receiving socket.");
                source.outputType = PortType.Matrix;
                for (int mode = 0; mode < 4; mode++)
                {
                    source.throwPorts = mode == 0; source.absentPorts = mode == 1; source.wrongDirection = mode == 2;
                    source.outputType = mode == 3 ? (PortType)999 : PortType.Matrix;
                    Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "PORT_DECLARATION", source) != null,
                        "UC-T01-09 malformed port declaration mode " + mode + " becomes a diagnostic.");
                }
                source.throwPorts = source.absentPorts = source.wrongDirection = false; source.outputType = PortType.Matrix;
                foreach (NodeExecutionDomain domain in new[] { NodeExecutionDomain.MainThread, NodeExecutionDomain.MainThreadGpu })
                {
                    source.domain = domain;
                    Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph, NodeExecutionDomain.Worker), "DOMAIN", source) != null &&
                          GraphValidationReport.Analyze(graph).IsValid,
                        "UC-T01-09 " + domain + " nodes require a matching executor.");
                }
                source.domain = NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "DOMAIN", source) != null,
                    "UC-T01-09 a node cannot declare an ambiguous combination of execution domains.");
                source.domain = NodeExecutionDomain.Worker;
                output.disabled = true;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "NO_OUTPUT") != null &&
                      GraphValidationReport.Analyze(graph, requireOutput: false).IsValid,
                    "UC-T01-09 whole-world runs require an enabled output while explicit node sampling does not.");
                output.disabled = false;
                var unresolved = new UnresolvedNodeRecord { slot = 0 }; graph.unresolvedNodes.Add(unresolved);
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "UNRESOLVED_PAYLOAD") != null && ReferenceEquals(graph.unresolvedNodes[0], unresolved),
                    "UC-T01-09 unresolved recovery payloads prevent execution and remain untouched.");
                graph.unresolvedNodes.Clear();
                Check(GraphValidationReport.Analyze(graph).IsValid && JsonUtility.ToJson(graph) == original,
                    "UC-T01-09 repairing all fixture faults restores valid execution without validation rewriting the graph.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestGraphValidationCycles()
        {
            Graph graph = ValidationGraph(out ValidationProbeNode source, out HeightOutputNode output);
            try
            {
                var second = new ValidationProbeNode { id = "second" }; graph.nodes.Add(second);
                graph.connections.Add(new Connection(source.id, 0, second.id, 0));
                graph.connections.Add(new Connection(second.id, 0, source.id, 0));
                GraphValidationReport report = GraphValidationReport.Analyze(graph);
                Check(ValidationDiagnostic(report, "CYCLE", source) != null && ValidationDiagnostic(report, "CYCLE", second) != null &&
                      ValidationDiagnostic(report, "CYCLE", output) == null,
                    "UC-T01-09 explicit cycles mark members without blaming downstream output nodes.");
                var replacement = new ConstantNode { id = "replacement" }; graph.nodes.Add(replacement);
                graph.connections.Add(new Connection(replacement.id, 0, source.id, 0));
                Check(GraphValidationReport.Analyze(graph).IsValid,
                    "UC-T01-09 the newest single-inlet wire can supersede an otherwise cyclic old edge.");
                source.multiple = true;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "CYCLE", source) != null,
                    "UC-T01-09 fan-in retains every effective edge when detecting cycles.");
                graph.connections.Clear(); graph.nodes.Clear();
                // The acceptance is bounded iterative traversal, not a machine-dependent speed threshold.
                for (int i = 0; i < 2048; i++)
                {
                    graph.nodes.Add(new ValidationProbeNode { id = "chain" + i });
                    if (i > 0) graph.connections.Add(new Connection("chain" + (i - 1), 0, "chain" + i, 0));
                }
                Check(GraphValidationReport.Analyze(graph, requireOutput: false).IsValid,
                    "UC-T01-09 a deep disconnected chain is validated without recursive cycle traversal.");
                graph.connections.Add(new Connection("chain2047", 0, "chain2047", 0));
                report = GraphValidationReport.Analyze(graph, requireOutput: false);
                Check(report.Diagnostics.Count(d => d.Code == "CYCLE") == 1,
                    "UC-T01-09 a self-loop at the end of a deep graph marks only its own node.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestGraphValidationPortals(bool texture)
        {
            Graph graph = ValidationGraph(out ValidationProbeNode source, out HeightOutputNode output);
            try
            {
                Generator sender = texture ? (Generator)new TexturePortalOutNode { channel = "test" } : new PortalOutNode { channel = "test" };
                Generator receiver = texture ? (Generator)new TexturePortalInNode { channel = "test" } : new PortalInNode { channel = "test" };
                sender.id = "sender"; receiver.id = "receiver";
                var consumer = new ValidationProbeNode { id = "consumer", inputType = PortType.Color };
                source.outputType = PortType.Color;
                graph.nodes.AddRange(new[] { sender, receiver, consumer }); graph.connections.Clear();
                graph.connections.Add(new Connection(source.id, 0, sender.id, 0));
                graph.connections.Add(new Connection(receiver.id, 0, consumer.id, 0));
                graph.connections.Add(new Connection(consumer.id, 0, output.id, 0));
                Check(GraphValidationReport.Analyze(graph).IsValid,
                    "UC-T01-09 " + (texture ? "texture" : "ordinary") + " portals derive types from current upstream ports.");
                ((IPortalTyped)sender).ResolveType(graph); ((IPortalTyped)receiver).ResolveType(graph);
                sender.disabled = true;
                GraphValidationReport report = GraphValidationReport.Analyze(graph);
                Check(ValidationDiagnostic(report, "PORTAL_EMPTY", receiver)?.Severity == GraphDiagnosticSeverity.Warning &&
                      ValidationDiagnostic(report, "TYPE", consumer) != null,
                    "UC-T01-09 a lost sender uses its family default instead of a stale cached Color socket.");
                consumer.inputType = texture ? PortType.Layer : PortType.Matrix;
                Check(GraphValidationReport.Analyze(graph).IsValid,
                    "UC-T01-09 compatible missing-sender blanks remain supported with a warning.");
                sender.disabled = false; graph.connections[0] = new Connection(receiver.id, 0, sender.id, 0);
                report = GraphValidationReport.Analyze(graph);
                Check(ValidationDiagnostic(report, "CYCLE", sender) != null && ValidationDiagnostic(report, "CYCLE", receiver) != null,
                    "UC-T01-09 wireless dependencies participate in cycle rejection for both portal families.");
                receiver.disabled = true;
                Check(ValidationDiagnostic(GraphValidationReport.Analyze(graph), "CYCLE") == null,
                    "UC-T01-09 disabled receivers do not create a wireless dependency that execution never pulls.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestGraphValidationPublication()
        {
            Graph graph = ValidationGraph(out ValidationProbeNode source, out HeightOutputNode output);
            GameObject root = new GameObject("UC validation admission fixture"); root.SetActive(false);
            TerrainData terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
            TextureGraphWindow textureWindow = null;
            UniverseCreatorObject world = null;
            try
            {
                world = root.AddComponent<UniverseCreatorObject>(); world.graph = graph; world.resolution = 33;
                var managerObject = new GameObject("ChunkManager"); managerObject.transform.SetParent(root.transform, false);
                ChunkManager manager = managerObject.AddComponent<ChunkManager>(); manager.Initialize(world);
                var terrainObject = new GameObject("Accepted terrain"); terrainObject.transform.SetParent(root.transform, false);
                UnityEngine.Terrain terrain = terrainObject.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = terrainData;
                var heights = new float[33, 33]; heights[0, 0] = 0.625f; terrainData.SetHeights(0, 0, heights);
                int childCount = root.GetComponentsInChildren<Transform>(true).Length;
                source.domain = NodeExecutionDomain.MainThreadGpu;
                ValidationProbeNode.prepared = ValidationProbeNode.generated = 0;
                bool rejected = false;
                try { GenerationSnapshot.Capture(graph, world, requireOutput: true).Release(); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("requires MainThreadGpu"); }
                Check(rejected && ValidationProbeNode.prepared == 0,
                    "UC-T01-09 snapshot rejection occurs before node preparation.");
                Check(!world.RegenerateAsync() && world.CurrentTask == null && world.LastTile == null,
                    "UC-T01-09 the actual world path refuses invalid graphs before dispatch or tile publication.");
                Check(!world.RebuildChunks(), "UC-T01-09 the world chunk rebuild entry rejects unsupported worker domains.");
                manager.RebuildAll();
                Check(!manager.IsRebuildingAll && root.GetComponentsInChildren<Transform>(true).Length == childCount &&
                      Mathf.Abs(terrainData.GetHeights(0, 0, 1, 1)[0, 0] - 0.625f) < 1e-4f,
                    "UC-T01-09 direct chunk rejection preserves existing terrain and creates no pooled cells.");
                source.domain = NodeExecutionDomain.Worker;
                source.outputType = PortType.Color;
                textureWindow = ScriptableObject.CreateInstance<TextureGraphWindow>(); textureWindow.SetGraph(graph);
                typeof(TextureGraphWindow).GetField("targetTerrain", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(textureWindow, terrain);
                typeof(TextureGraphWindow).GetMethod("ApplyToTerrain", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(textureWindow, null);
                Check(ValidationProbeNode.prepared == 0 && ValidationProbeNode.generated == 0 && terrainData.terrainLayers.Length == 0,
                    "UC-T01-09 actual texture Apply rejects bad wiring before node/resource generation or terrain layer commits.");
                source.outputType = PortType.Matrix;
                bool started = world.RegenerateAsync();
                Check(started && world.CurrentTask != null && world.CurrentTask.Wait(5000),
                    "UC-T01-09 repairing the graph admits a fresh actual world worker.");
                TileData accepted = world.LastTile;
                Check(accepted != null && accepted.outputs.heightmap != null &&
                      Mathf.Abs(accepted.outputs.heightmap[0, 0] - 0.375f) < 1e-6f,
                    "UC-T01-09 repaired publication contains the newly generated expected field.");
                var completed = world.CurrentTask;
                source.outputType = PortType.Color;
                int generatedBefore = ValidationProbeNode.generated;
                Check(!world.RegenerateAsync() && ReferenceEquals(world.CurrentTask, completed) && ReferenceEquals(world.LastTile, accepted) &&
                      ValidationProbeNode.generated == generatedBefore,
                    "UC-T01-09 subsequent rejection leaves the last accepted tile intact and starts no replacement worker.");
            }
            finally
            {
                if (world != null)
                {
                    world.CancelGeneration();
                    if (world.CurrentTask != null) world.CurrentTask.Wait(5000);
                    if (world.LastTile != null) world.LastTile.ReleaseGenerationInputs();
                }
                if (textureWindow != null) UnityEngine.Object.DestroyImmediate(textureWindow);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(terrainData);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestGraphValidationCanvas()
        {
            Graph graph = ValidationGraph(out ValidationProbeNode source, out HeightOutputNode output);
            UCGraphView view = null;
            try
            {
                var badPorts = new ValidationProbeNode { id = "bad-ports", throwPorts = true }; graph.nodes.Add(badPorts);
                source.outputType = PortType.Color; graph.nodes.Add(null);
                string original = JsonUtility.ToJson(graph);
                view = new UCGraphView(); view.Populate(graph);
                var nodeViews = view.nodes.ToList().OfType<UCNodeView>().ToList();
                Label outputError = nodeViews.First(n => ReferenceEquals(n.Node, output)).Q<Label>("uc-validation");
                Label portError = nodeViews.First(n => ReferenceEquals(n.Node, badPorts)).Q<Label>("uc-validation");
                Label summary = view.Q<Label>("uc-graph-validation");
                Check(outputError.text.Contains("Type mismatch") && portError.text.Contains("Cannot read node ports") &&
                      summary.text.Contains("node slot is empty") && JsonUtility.ToJson(graph) == original,
                    "UC-T01-09 the canvas presents socket, plugin and missing-slot errors without changing authored data.");
                graph.connections[0] = new Connection(badPorts.id, 0, output.id, 0); view.RefreshValidation();
                Check(outputError.style.display.value == DisplayStyle.None && portError.style.display.value == DisplayStyle.Flex &&
                      summary.style.display.value == DisplayStyle.Flex,
                    "UC-T01-09 fixing one socket clears only its diagnostic while unrelated faults remain visible.");
                badPorts.throwPorts = false; graph.nodes.RemoveAt(3); view.Populate(graph);
                Check(view.Validation.IsValid && view.Q<Label>("uc-graph-validation").style.display.value == DisplayStyle.None,
                    "UC-T01-09 repaired nodes and missing slots clear their canvas errors on repopulation.");
                source.domain = NodeExecutionDomain.MainThread;
                Check(!view.RefreshValidation().IsValid, "UC-T01-09 the terrain canvas rejects main-thread-only generators.");
                view.AllowedExecutionDomains = NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu;
                Check(view.RefreshValidation().IsValid, "UC-T01-09 the texture canvas admits its supported synchronous execution domains.");
                view.Populate(null);
                Check(view.Validation == null && view.Q<Label>("uc-graph-validation").style.display.value == DisplayStyle.None,
                    "UC-T01-09 unloading a graph clears the previous validation display.");
            }
            finally
            {
                if (view != null)
                {
                    view.Populate(null);
                    view.Dispose(); // UC-T05-02: the same lifecycle releases native search and edit subscriptions in tests/windows.
                }
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
