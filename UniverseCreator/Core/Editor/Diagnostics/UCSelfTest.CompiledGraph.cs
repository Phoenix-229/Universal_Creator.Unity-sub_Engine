using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Combiners;
using UniverseCreator.Nodes.Outputs;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestCompiledGraphPlans()
        {
            TestCompiledGraphSemantics(false);
            TestCompiledGraphSemantics(true);
            TestCompiledExecutionDomains(); // UC-T01-08: metadata must reflect actual Unity/GPU resource paths.
            ProfileCompiledGraph(32);
            ProfileCompiledGraph(1024);
        }

        // UC-T01-08: compare actual products/hashes with the same graph's original list-based evaluator.
        private static void TestCompiledGraphSemantics(bool texture)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var source = new ConstantNode { id = "source", value = 0.2f };
                var alternate = new ConstantNode { id = "alternate", value = 0.7f };
                Generator foreign = NewCachePortalSender(!texture, "shared");
                Generator disabledSender = NewCachePortalSender(texture, "shared"); disabledSender.disabled = true;
                Generator sender = NewCachePortalSender(texture, "shared");
                Generator duplicate = NewCachePortalSender(texture, "shared");
                Generator receiver = texture
                    ? (Generator)new UniverseCreator.Nodes.Texturing.TexturePortalInNode { channel = "shared" }
                    : new UniverseCreator.Nodes.Utilities.PortalInNode { channel = "shared" };
                var bypass = new CurveNode { id = "bypass", disabled = true };
                var combine = new CombineNode { id = "combine", op = MergeOp.Sum };
                var height = new HeightOutputNode { id = "height", enforceSeaLevelFloor = false,
                    enforceContinentalRise = false, enforceCoastalShelf = false };
                graph.nodes.AddRange(new[] { source, alternate, foreign, disabledSender, sender, duplicate,
                    receiver, bypass, combine, height });
                graph.AddConnection(new Connection(source.id, 0, foreign.id, 0));
                graph.AddConnection(new Connection(alternate.id, 0, sender.id, 0));
                graph.AddConnection(new Connection(source.id, 0, duplicate.id, 0));
                graph.AddConnection(new Connection(receiver.id, 0, bypass.id, 0));
                Connection firstEdge = new Connection(source.id, 0, combine.id, 0);
                graph.AddConnection(firstEdge);
                graph.AddConnection(new Connection(bypass.id, 0, combine.id, 0));
                graph.AddConnection(new Connection(combine.id, 0, height.id, 0));
                TileData legacy = SnapshotTile(graph);
                GraphContentHasher.ComputeHashes(graph);
                ulong[] hashes = new ulong[graph.nodes.Count];
                for (int i = 0; i < hashes.Length; i++) hashes[i] = graph.nodes[i].contentHash;
                new GraphEvaluator(graph).Evaluate(legacy, null);

                CompiledGraphPlan plan = CompiledGraphPlan.Build(graph);
                graph.compiledPlan = plan;
                GraphContentHasher.ComputeHashes(graph);
                bool hashEquality = true;
                for (int i = 0; i < hashes.Length; i++) hashEquality &= hashes[i] == graph.nodes[i].contentHash;
                Check(hashEquality, "UC-T01-08 indexed " + (texture ? "texture" : "ordinary") + " portal/fan-in hashes match legacy.");
                TileData indexed = SnapshotTile(graph);
                new GraphEvaluator(graph).Evaluate(indexed, null);
                bool equal = legacy.outputs.heightmap != null && indexed.outputs.heightmap != null;
                if (equal)
                    for (int z = 0; z < legacy.outputs.heightmap.GetLength(0); z++)
                        for (int x = 0; x < legacy.outputs.heightmap.GetLength(1); x++)
                            equal &= legacy.outputs.heightmap[z, x] == indexed.outputs.heightmap[z, x];
                Check(equal && Mathf.Abs(indexed.outputs.heightmap[0, 0] - 0.9f) < 1e-6f,
                    "UC-T01-08 indexed portal/disabled-bypass/fan-in output matches legacy samples.");
                Check(ReferenceEquals(((IPortalIn)receiver).ResolveSender(graph), sender),
                    "UC-T01-08 portal indexes retain family, disabled-sender and first-duplicate selection.");
                Check(plan.GetDependents(sender.id).Count == 1 && ReferenceEquals(plan.GetDependents(sender.id)[0], receiver),
                    "UC-T01-08 reverse dependencies include the selected wireless receiver.");
                Check(plan.Outputs.Count == 1 && ReferenceEquals(plan.Outputs[0].Node, height),
                    "UC-T01-08 compiled roots retain authored output-node order.");
                Check(plan.GetInputEdges(combine.id, 0).Count == 2 &&
                      plan.GetInputEdges(combine.id, 0)[0].Equals(firstEdge) &&
                      graph.GetInputConnection(combine.id, 0).sourceNodeId == bypass.id,
                    "UC-T01-08 inlet indexes retain fan-in ordering and newest single-edge lookup.");
                var ownedList = graph.GetInputConnections(combine.id, 0);
                ownedList.Clear();
                Check(plan.GetInputEdges(combine.id, 0).Count == 2,
                    "UC-T01-08 clearing a public connection-list result cannot change compiled edges.");
                bool readOnly = false;
                try { ((IList<Connection>)plan.GetInputEdges(combine.id, 0))[0] = default(Connection); }
                catch (NotSupportedException) { readOnly = true; }
                Check(readOnly, "UC-T01-08 indexed connection collections reject caller mutation.");
                graph.RemoveConnection(firstEdge);
                Check(graph.compiledPlan == null && plan.GetInputEdges(combine.id, 0).Count == 2 &&
                      CompiledGraphPlan.Build(graph).GetInputEdges(combine.id, 0).Count == 1,
                    "UC-T01-08 a supported topology edit invalidates the graph attachment and builds fresh edges.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestCompiledExecutionDomains()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var source = new ConstantNode { id = "source" };
                graph.nodes.AddRange(new Generator[] {
                    source, new HeightOutputNode { id = "height" }, null,
                    new UniverseCreator.Nodes.Texturing.TerrainHeightSourceNode { id = "terrain" },
                    new UniverseCreator.Nodes.Texturing.MaskMapPackerNode { id = "mask", disabled = true },
                    new UniverseCreator.Nodes.Texturing.NormalMapLayerNode { id = "normal" }
                });
                string before = JsonUtility.ToJson(graph);
                CompiledGraphPlan plan = CompiledGraphPlan.Build(graph);
                Check(plan.ExecutionSteps.Count == 5 && plan.ExecutionSteps[2].NodeSlot == 3 &&
                      plan.ExecutionSteps[4].NodeId == "normal",
                    "UC-T01-08 domain metadata preserves authored slots, disabled nodes and disconnected nodes.");
                Check(plan.ExecutionSteps[0].Domain == NodeExecutionDomain.Worker &&
                      plan.ExecutionSteps[1].Domain == NodeExecutionDomain.Worker,
                    "UC-T01-08 CPU fields and managed output bags remain worker computations.");
                Check(plan.ExecutionSteps[2].Domain == NodeExecutionDomain.MainThread &&
                      plan.ExecutionSteps[3].Domain == NodeExecutionDomain.MainThread && plan.ExecutionSteps[3].HasCpuFallback &&
                      plan.ExecutionSteps[4].Domain == NodeExecutionDomain.MainThread && plan.ExecutionSteps[4].HasCpuFallback,
                    "UC-T01-08/T07-10 live terrain reads require Unity's main thread and both readable CPU mask-packers retain their declared fallback.");
                bool readOnly = false;
                try { ((IList<CompiledGraphPlan.ExecutionStep>)plan.ExecutionSteps).Clear(); }
                catch (NotSupportedException) { readOnly = true; }
                Check(readOnly && JsonUtility.ToJson(graph) == before,
                    "UC-T01-08 domain compilation is read-only and does not migrate serialized authoring data.");
                source.id = "changed";
                Check(plan.ExecutionSteps[0].NodeId == "source" &&
                      CompiledGraphPlan.Build(graph).ExecutionSteps[0].NodeId == "changed",
                    "UC-T01-08 captured metadata stays fixed while a new revision observes an ID edit.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void ProfileCompiledGraph(int pairs)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                for (int i = 0; i < pairs; i++)
                {
                    var source = new ConstantNode { id = "source" + i };
                    var target = new CurveNode { id = "target" + i };
                    graph.nodes.Add(source); graph.nodes.Add(target);
                    graph.connections.Add(new Connection(source.id, 0, target.id, 0));
                }
                string sourceId = "source" + (pairs - 1), targetId = "target" + (pairs - 1);
                const int requests = 4096;
                Stopwatch timer = new Stopwatch();
                // UC-T01-08: Unity Mono returned zero from GC.GetAllocatedBytesForCurrentThread even for new lists.
                // Record immediate main-thread GC.Alloc samples, and calibrate before trusting a zero result.
                using var allocations = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 32768,
                    ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                Check(allocations.Valid, "UC-T01-08 the Editor exposes the GC.Alloc profiler marker.");
                if (!allocations.Valid) return;
                GC.KeepAlive(new byte[8192]);
                allocations.Stop();
                int calibrationSamples = RecordedAllocationSamples(allocations);
                Check(calibrationSamples > 0, "UC-T01-08 the allocation recorder detects a known managed allocation.");
                if (calibrationSamples <= 0) return;
                // Warm method/JIT paths before measuring their allocations and elapsed time.
                graph.GetNodeById(sourceId); graph.GetInputConnections(targetId, 0);
                allocations.Reset(); allocations.Start();
                timer.Start();
                int legacyCount = 0;
                for (int i = 0; i < requests; i++)
                    legacyCount += graph.GetNodeById(sourceId) != null ? graph.GetInputConnections(targetId, 0).Count : 0;
                timer.Stop(); double legacyMs = timer.Elapsed.TotalMilliseconds;
                allocations.Stop();
                int legacySamples = RecordedAllocationSamples(allocations);
                allocations.Reset(); allocations.Start(); timer.Restart();
                CompiledGraphPlan plan = CompiledGraphPlan.Build(graph);
                timer.Stop(); double compileMs = timer.Elapsed.TotalMilliseconds;
                allocations.Stop();
                int compileSamples = RecordedAllocationSamples(allocations);
                plan.GetNode(sourceId); plan.GetInputEdges(targetId, 0);
                allocations.Reset(); allocations.Start(); timer.Restart();
                int indexedCount = 0;
                for (int i = 0; i < requests; i++)
                    indexedCount += plan.GetNode(sourceId) != null ? plan.GetInputEdges(targetId, 0).Count : 0;
                timer.Stop();
                allocations.Stop();
                int indexedSamples = RecordedAllocationSamples(allocations);
                Check(legacyCount == requests && indexedCount == requests,
                    "UC-T01-08 profile query results agree for " + graph.nodes.Count + " nodes.");
                Check(indexedSamples == 0 && legacySamples > 0,
                    "UC-T01-08 indexed node/inlet queries record zero GC.Alloc samples for " + graph.nodes.Count + " nodes.");
                UnityEngine.Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "UC-T01-08 PROFILE nodes={0} edges={1} requests={2} legacyMs={3:F3} legacyAllocSamples={4} compileMs={5:F3} compileAllocSamples={6} indexedMs={7:F3} indexedAllocSamples={8}",
                    graph.nodes.Count, graph.connections.Count, requests, legacyMs, legacySamples,
                    compileMs, compileSamples, timer.Elapsed.TotalMilliseconds, indexedSamples));
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static int RecordedAllocationSamples(ProfilerRecorder recorder)
        {
            // GC.Alloc is a marker; its sample Value is not a reliable byte total. Count immediate samples instead.
            Check(recorder.Count < 32768, "UC-T01-08 allocation recording capacity is not exhausted.");
            return recorder.Count;
        }
    }
}
