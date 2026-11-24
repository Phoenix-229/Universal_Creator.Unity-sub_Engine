// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.NodeCache.cs
//  Purpose: Pins §7 T1 - the content-hash incremental-invalidation cache. Proves,
//           over the real GraphEvaluator, that a node whose content hash is unchanged
//           is FORWARDED from the per-chunk retained store (not recomputed) across
//           bakes, and that changing a param recomputes it. This is the correctness
//           guarantee that the cache can never serve STALE output: the hash is the
//           key, so a changed node misses and recomputes.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators; // UC-O01-CACHE: real constant-field regression inputs.
using UniverseCreator.Nodes.Biome; // UC-O01-REGIONS: real scene-region preparation and classification.

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // A trivial node that counts how many times it actually computes, so the test
        // can tell a forwarded (cached) pull from a recomputed one. One serialized
        // param (p) whose change must flip the content hash.
        [System.Serializable]
        private class CacheCountingNode : Generator
        {
            public float p = 1f;
            public static int generateCount;

            public override string NodeName { get { return "Cache Counting (test)"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            private static readonly Port[] s_ins = new Port[0];
            private static readonly Port[] s_outs = { new Port("Out", PortType.Matrix, PortDirection.Output) };
            public override Port[] GetInputs() { return s_ins; }
            public override Port[] GetOutputs() { return s_outs; }

            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                generateCount++;
                return new float[1];   // non-null product; its content is irrelevant to the cache test
            }
        }

        private static TileData NewCacheTile(System.Collections.Generic.Dictionary<string, RetainedProduct> store,
                                             GraphEvaluator ev)
        {
            TileData t = new TileData(default(Coord), default(CoordRect), Vector3.zero, Vector3.one, 1f);
            t.retained = store;   // opt into the T1 cross-bake cache with the shared store
            t.Bind(ev);
            return t;
        }

        /// <summary>
        /// T1: a node unchanged since the last bake is forwarded from the retained cache
        /// (compute count does not rise); changing a param flips its content hash and it
        /// recomputes. Each "bake" is a fresh TileData sharing one retained store, mirroring
        /// how a Chunk keeps its store across regenerations.
        /// </summary>
        private static void TestContentHashIncrementalInvalidation()
        {
            // UC-O01 / UC-A01: exercise real retained fields after an upstream fan-in edit.
            TestRetainedFanInInvalidation();
            TestRetainedPortalInvalidation(false);
            TestRetainedPortalInvalidation(true);
            TestPreparedBiomeRegionInvalidation();
            TestPreparedRasterInvalidation(); // UC-T01-07: in-place texel edits must invalidate retained imports.
            TestGenerationSnapshots(); // UC-T01-06/07: edits, cancellation and final-consumer ownership.
            Graph g = ScriptableObject.CreateInstance<Graph>();
            try
            {
                CacheCountingNode node = new CacheCountingNode();
                g.nodes.Add(node);

                GraphEvaluator ev = new GraphEvaluator(g);
                var store = new System.Collections.Generic.Dictionary<string, RetainedProduct>();

                CacheCountingNode.generateCount = 0;

                // Bake 1: first pull computes the node once and retains it.
                GraphContentHasher.ComputeHashes(g);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 1,
                    "First bake computes the node once (was " + CacheCountingNode.generateCount + ")");

                // Bake 2: fresh tile, SAME store, node unchanged -> forwarded, not recomputed.
                GraphContentHasher.ComputeHashes(g);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 1,
                    "Unchanged node is forwarded from the retained cache, not recomputed (was "
                    + CacheCountingNode.generateCount + ")");

                // Edit a param -> content hash changes -> recompute.
                node.p = 2f;
                GraphContentHasher.ComputeHashes(g);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 2,
                    "A changed param recomputes the node (hash changed, was "
                    + CacheCountingNode.generateCount + ")");

                // And once more unchanged -> forwarded again (no further compute).
                GraphContentHasher.ComputeHashes(g);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 2,
                    "After the edit, an unchanged re-bake forwards again (was "
                    + CacheCountingNode.generateCount + ")");
            }
            finally
            {
                Object.DestroyImmediate(g);
            }
        }

        // UC-O01 / UC-A01: edits to any effective edge must invalidate its retained merge.
        private static void TestRetainedFanInInvalidation()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ConstantNode first = new ConstantNode { value = 0.2f };
                ConstantNode last = new ConstantNode { value = 0.7f };
                var combine = new UniverseCreator.Nodes.Combiners.CombineNode { op = MergeOp.Sum };
                graph.nodes.Add(first);
                graph.nodes.Add(last);
                graph.nodes.Add(combine);
                Connection firstEdge = new Connection(first.id, 0, combine.id, 0);
                graph.AddConnection(firstEdge);
                graph.AddConnection(new Connection(last.id, 0, combine.id, 0));
                var evaluator = new GraphEvaluator(graph);
                var retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();

                Matrix original = BakeRetainedCacheMatrix(graph, evaluator, combine, retained);
                CheckCacheField(original, 0.9f, "UC-A01: initial fan-in");
                Check(object.ReferenceEquals(original, BakeRetainedCacheMatrix(graph, evaluator, combine, retained)),
                    "UC-A01: unchanged fan-in reuses its retained product");
                first.value = 0.4f;
                Matrix edited = BakeRetainedCacheMatrix(graph, evaluator, combine, retained);
                CheckCacheField(edited, 1.1f, "UC-A01: non-last source edit");
                Check(!object.ReferenceEquals(original, edited), "UC-A01: stale merge is replaced");
                Matrix cold = BakeRetainedCacheMatrix(graph, evaluator, combine, null);
                bool matchesCold = edited != null && cold != null && edited.arr.Length == cold.arr.Length;
                if (matchesCold)
                    for (int i = 0; i < edited.arr.Length; i++)
                        if (float.IsNaN(edited.arr[i]) || Mathf.Abs(edited.arr[i] - cold.arr[i]) > 1e-5f)
                        { matchesCold = false; break; }
                Check(matchesCold, "UC-A01: edited retained fan-in matches a cold bake");
                Check(object.ReferenceEquals(edited, BakeRetainedCacheMatrix(graph, evaluator, combine, retained)),
                    "UC-A01: unchanged edited fan-in resumes reuse");
                graph.RemoveConnection(firstEdge);
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, combine, retained), 0.7f,
                    "UC-A01: removed non-last edge");
                graph.AddConnection(firstEdge);
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, combine, retained), 1.1f,
                    "UC-A01: added edge");
                ulong orderedHash = combine.contentHash;
                graph.connections.Reverse();
                GraphContentHasher.ComputeHashes(graph);
                Check(combine.contentHash != orderedHash, "UC-A01: fan-in order participates in the hash");

                var single = new UniverseCreator.Nodes.Combiners.MaximumNode();
                graph.nodes.Add(single);
                graph.AddConnection(new Connection(first.id, 0, single.id, 0));
                graph.AddConnection(new Connection(last.id, 0, single.id, 0));
                GraphContentHasher.ComputeHashes(graph);
                ulong singleHash = single.contentHash;
                first.value = 0.8f;
                GraphContentHasher.ComputeHashes(graph);
                Check(single.contentHash == singleHash, "UC-A01: evicted single-input edge is ignored");
                last.value = 0.6f;
                GraphContentHasher.ComputeHashes(graph);
                Check(single.contentHash != singleHash, "UC-A01: effective single-input edge invalidates");

                // UC-A05: simulate an invalid deserialized edge bypassing the authoring guard.
                Connection cycle = new Connection(combine.id, 0, combine.id, 0);
                graph.connections.Add(cycle);
                GraphContentHasher.ComputeHashes(graph, 123UL);
                Check(combine.contentHash == 0UL, "UC-A05: explicit fan-in cycle stays unretainable with salt");
                graph.RemoveConnection(cycle);
                GraphContentHasher.ComputeHashes(graph, 123UL);
                Check(combine.contentHash != 0UL, "UC-A05: removing an explicit cycle restores a usable hash");
            }
            finally { Object.DestroyImmediate(graph); }
        }

        // UC-O01 / UC-A02: both portal families must invalidate their retained downstream chain.
        private static void TestRetainedPortalInvalidation(bool texture)
        {
            string label = "UC-A02: " + (texture ? "texture" : "ordinary") + " portal ";
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                var source = new CacheDualFieldNode();
                var alternate = new ConstantNode { value = 0.7f };
                Generator foreign = NewCachePortalSender(!texture, "default");
                Generator sender = NewCachePortalSender(texture, "default");
                Generator duplicate = NewCachePortalSender(texture, "default");
                Generator receiver = texture
                    ? (Generator)new UniverseCreator.Nodes.Texturing.TexturePortalInNode()
                    : new UniverseCreator.Nodes.Utilities.PortalInNode();
                var sink = new UniverseCreator.Nodes.Combiners.CombineNode { op = MergeOp.Sum };
                graph.nodes.AddRange(new Generator[] { source, alternate, foreign, sender, duplicate, receiver, sink });
                graph.AddConnection(new Connection(alternate.id, 0, foreign.id, 0));
                graph.AddConnection(new Connection(source.id, 0, sender.id, 0));
                graph.AddConnection(new Connection(alternate.id, 0, duplicate.id, 0));
                graph.AddConnection(new Connection(receiver.id, 0, sink.id, 0));
                var evaluator = new GraphEvaluator(graph);
                var retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();

                Matrix original = BakeRetainedCacheMatrix(graph, evaluator, sink, retained);
                CheckCacheField(original, 0.2f, label + "selects first matching family sender");
                alternate.value = 0.8f;
                Check(object.ReferenceEquals(original, BakeRetainedCacheMatrix(graph, evaluator, sink, retained)),
                    label + "ignores unselected and other-family sender edits");
                source.value = 0.4f;
                Matrix edited = BakeRetainedCacheMatrix(graph, evaluator, sink, retained);
                CheckCacheField(edited, 0.4f, label + "upstream edit reaches retained downstream");
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, sink, null), 0.4f,
                    label + "cold bake agrees after edit");
                Check(!object.ReferenceEquals(original, edited), label + "replaces stale output");
                Check(object.ReferenceEquals(edited, BakeRetainedCacheMatrix(graph, evaluator, sink, retained)),
                    label + "unchanged chain reuses output");
                graph.AddConnection(new Connection(source.id, 1, sender.id, 0));
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, sink, retained), 0.9f,
                    label + "source output-port rewire invalidates");
                sender.disabled = true;
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, sink, retained), 0.8f,
                    label + "disabled first sender selects next enabled sender");
                duplicate.disabled = true;
                object missing = BakeRetainedCacheProduct(graph, evaluator, receiver, retained);
                Check(texture ? missing is LayerProduct : missing is Matrix,
                    label + "missing sender returns its family blank type");
                sender.disabled = false;
                BakeRetainedCacheProduct(graph, evaluator, receiver, retained);
                SetCachePortalChannel(receiver, "");
                object empty = BakeRetainedCacheProduct(graph, evaluator, receiver, retained);
                Check(texture ? empty is LayerProduct : empty is Matrix,
                    label + "empty channel resets the previously carried type");
                SetCachePortalChannel(receiver, "late");
                object absent = BakeRetainedCacheProduct(graph, evaluator, receiver, retained);
                Check(texture ? absent is LayerProduct : absent is Matrix,
                    label + "new channel starts with a typed blank");
                Generator lateSender = NewCachePortalSender(texture, "late");
                graph.nodes.Add(lateSender);
                graph.AddConnection(new Connection(alternate.id, 0, lateSender.id, 0));
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, sink, retained), 0.8f,
                    label + "new sender invalidates a previously cached missing channel");
                SetCachePortalChannel(receiver, "default");
                CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, sink, retained), 0.9f,
                    label + "switching back restores the original sender");
                TestPortalCacheCycleRecovery(graph, evaluator, source, sender, receiver, sink, label);
            }
            finally { Object.DestroyImmediate(graph); }
        }

        // UC-O01 / UC-A05: hidden cycles must not retain the evaluator's partial fallback.
        private static void TestPortalCacheCycleRecovery(Graph graph, GraphEvaluator evaluator,
            CacheDualFieldNode source, Generator sender, Generator receiver, Generator sink, string label)
        {
            Connection sourceEdge = graph.GetInputConnection(sender.id, 0);
            graph.RemoveConnection(sourceEdge);
            Connection cycle = new Connection(receiver.id, 0, sender.id, 0);
            graph.connections.Add(cycle); // simulate a loaded graph, including invalid implicit edges
            GraphContentHasher.ComputeHashes(graph, 123UL);
            Check(sender.contentHash == 0UL && receiver.contentHash == 0UL && sink.contentHash == 0UL,
                label + "cycle and downstream remain unretainable even with world salt");
            var retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();
            BakeRetainedCacheProduct(graph, evaluator, sink, retained);
            Check(!retained.ContainsKey(receiver.id + ":0") && !retained.ContainsKey(sink.id + ":0"),
                label + "cyclic fallback is not stored across bakes");
            graph.RemoveConnection(cycle);
            graph.AddConnection(sourceEdge);
            CheckCacheField(BakeRetainedCacheMatrix(graph, evaluator, sink, retained),
                source.value + sourceEdge.sourcePortIndex * 0.5f, label + "cycle repair restores output");
            Check(receiver.contentHash != 0UL && retained.ContainsKey(receiver.id + ":0")
                && retained.ContainsKey(sink.id + ":0"), label + "repaired chain can retain again");
        }

        // UC-O01 / UC-A03: scene-only edits must invalidate prepared and downstream fields.
        private static void TestPreparedBiomeRegionInvalidation()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            GameObject root = new GameObject("UC prepared-region cache test");
            try
            {
                var classifier = new BiomeClassifierNode();
                var sink = new UniverseCreator.Nodes.Combiners.CombineNode { op = MergeOp.Sum };
                graph.nodes.Add(classifier);
                graph.nodes.Add(sink);
                graph.AddConnection(new Connection(classifier.id, 0, sink.id, 0));
                var evaluator = new GraphEvaluator(graph);
                var retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();
                Matrix procedural = BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained);
                ulong noRegionsHash = classifier.contentHash;
                string authoredState = JsonUtility.ToJson(classifier);

                GameObject regionObject = new GameObject("region");
                regionObject.transform.SetParent(root.transform, false);
                regionObject.transform.position = new Vector3(5f, 0f, 5f);
                UCBiomeRegion region = regionObject.AddComponent<UCBiomeRegion>();
                region.halfExtentsM = new Vector2(20f, 20f);
                region.biome = BiomeType.Ice;
                float ice = UniverseCreator.Ecology.WhittakerBiome.Encode((int)BiomeType.Ice);
                float desert = UniverseCreator.Ecology.WhittakerBiome.Encode((int)BiomeType.Desert);
                Matrix covered = BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained);
                CheckCacheField(covered, ice, "UC-A03: scene region replaces a retained procedural field");
                Check(object.ReferenceEquals(covered,
                    BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained)),
                    "UC-A03: recollected unchanged footprints retain the same output");
                region.biome = BiomeType.Desert;
                Matrix changedBiome = BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained);
                CheckCacheField(changedBiome, desert, "UC-A03: biome-only scene edit invalidates");
                regionObject.transform.position = new Vector3(1000f, 0f, 1000f);
                CheckCacheFieldsMatch(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                    procedural, "UC-A03: moving the region away restores procedural classification");
                regionObject.transform.position = new Vector3(5f, 0f, 5f);
                BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained);
                region.halfExtentsM = Vector2.zero;
                Matrix resized = BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained);
                CheckCacheFieldsMatch(resized, BakeRetainedCacheMatrix(graph, evaluator, sink, null),
                    "UC-A03: extent-only edit agrees with a cold bake");

                TestBiomeRegionPriorityAndRemoval(graph, evaluator, classifier, sink, root, region,
                    retained, procedural, ice, desert);
                classifier.SetRegionOverrides(new System.Collections.Generic.List<BiomeRegionFootprint>());
                GraphContentHasher.ComputeHashes(graph);
                Check(classifier.contentHash == noRegionsHash,
                    "UC-A03: null and empty overrides share the procedural hash");
                Check(JsonUtility.ToJson(classifier) == authoredState,
                    "UC-A03: scene footprints are not serialized into the authored node");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(graph);
            }
        }

        // UC-O01 / UC-A03: precedence and scene lifecycle must participate in invalidation too.
        private static void TestBiomeRegionPriorityAndRemoval(Graph graph, GraphEvaluator evaluator,
            BiomeClassifierNode classifier, Generator sink, GameObject root, UCBiomeRegion region,
            System.Collections.Generic.Dictionary<string, RetainedProduct> retained,
            Matrix procedural, float ice, float desert)
        {
            region.halfExtentsM = new Vector2(20f, 20f);
            GameObject secondObject = new GameObject("overlapping region");
            secondObject.transform.SetParent(root.transform, false);
            secondObject.transform.position = new Vector3(5f, 0f, 5f);
            UCBiomeRegion second = secondObject.AddComponent<UCBiomeRegion>();
            second.halfExtentsM = new Vector2(20f, 20f);
            second.biome = BiomeType.Ice;
            second.priority = -1;
            CheckCacheField(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                ice, "UC-A03: added higher-precedence region wins");
            region.priority = -2;
            CheckCacheField(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                desert, "UC-A03: priority-only edit invalidates");
            region.priority = second.priority = 0;
            CheckCacheField(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                desert, "UC-A03: equal priorities preserve collection order");
            secondObject.transform.SetSiblingIndex(0);
            CheckCacheField(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                ice, "UC-A03: equal-priority region reorder invalidates");
            second.enabled = false;
            CheckCacheField(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                desert, "UC-A03: disabled region no longer overrides");
            region.gameObject.SetActive(false);
            CheckCacheFieldsMatch(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                procedural, "UC-A03: inactive regions restore procedural classification");
            region.gameObject.SetActive(true);
            CheckCacheField(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                desert, "UC-A03: reactivated region returns");
            Object.DestroyImmediate(region.gameObject);
            CheckCacheFieldsMatch(BakeBiomeRegionCache(graph, evaluator, sink, root.transform, retained),
                procedural, "UC-A03: removed region clears its retained override");
            Check(classifier.regionOverrides == null, "UC-A03: collection clears absent active footprints");
        }

        private static Matrix BakeBiomeRegionCache(Graph graph, GraphEvaluator evaluator, Generator sink,
            Transform root, System.Collections.Generic.Dictionary<string, RetainedProduct> retained)
        {
            UCBiomeRegion.PushToGraph(graph, root);
            return BakeRetainedCacheMatrix(graph, evaluator, sink, retained);
        }

        private static void CheckCacheFieldsMatch(Matrix actual, Matrix expected, string label)
        {
            bool equal = actual != null && expected != null && actual.arr.Length == expected.arr.Length;
            if (equal)
                for (int i = 0; i < actual.arr.Length; i++)
                    if (float.IsNaN(actual.arr[i]) || float.IsNaN(expected.arr[i])
                        || Mathf.Abs(actual.arr[i] - expected.arr[i]) > 1e-5f) { equal = false; break; }
            Check(equal, label);
        }

        // UC-O01 / UC-A02: distinguish source-port rewiring with real mutable field products.
        [System.Serializable]
        private class CacheDualFieldNode : Generator
        {
            public float value = 0.2f;
            private static readonly Port[] outputs = {
                new Port("A", PortType.Matrix, PortDirection.Output),
                new Port("B", PortType.Matrix, PortDirection.Output)
            };
            public override string NodeName { get { return "Cache Dual Field (test)"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }
            public override Port[] GetInputs() { return SharedPorts.None; }
            public override Port[] GetOutputs() { return outputs; }
            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                Matrix field = tile.CreateMatrix();
                for (int i = 0; i < field.arr.Length; i++) field.arr[i] = value + outputIndex * 0.5f;
                return field;
            }
        }

        private static Generator NewCachePortalSender(bool texture, string channel)
        {
            if (texture) return new UniverseCreator.Nodes.Texturing.TexturePortalOutNode { channel = channel };
            return new UniverseCreator.Nodes.Utilities.PortalOutNode { channel = channel };
        }

        private static void SetCachePortalChannel(Generator receiver, string channel)
        {
            var ordinary = receiver as UniverseCreator.Nodes.Utilities.PortalInNode;
            if (ordinary != null) ordinary.channel = channel;
            else ((UniverseCreator.Nodes.Texturing.TexturePortalInNode)receiver).channel = channel;
        }

        // UC-O01-CACHE: fresh nonempty tiles share only the retained cross-bake store.
        private static Matrix BakeRetainedCacheMatrix(Graph graph, GraphEvaluator evaluator, Generator node,
            System.Collections.Generic.Dictionary<string, RetainedProduct> retained)
        {
            return BakeRetainedCacheProduct(graph, evaluator, node, retained) as Matrix;
        }

        private static object BakeRetainedCacheProduct(Graph graph, GraphEvaluator evaluator, Generator node,
            System.Collections.Generic.Dictionary<string, RetainedProduct> retained)
        {
            GraphContentHasher.ComputeHashes(graph);
            TileData tile = new TileData(default(Coord), new CoordRect(0, 0, 3, 3),
                Vector3.zero, new Vector3(10f, 100f, 10f), 100f);
            tile.retained = retained;
            tile.Bind(evaluator);
            return evaluator.PullNode(node, 0, tile, new StopToken());
        }

        private static void CheckCacheField(Matrix field, float expected, string label)
        {
            bool valid = field != null && field.arr.Length == 9;
            if (valid)
                for (int i = 0; i < field.arr.Length; i++)
                    if (float.IsNaN(field.arr[i]) || Mathf.Abs(field.arr[i] - expected) > 1e-5f)
                    { valid = false; break; }
            Check(valid, label + " produces the expected field");
        }

        /// <summary>
        /// CTL-5 (`CTL-CRC1`): the world salt. A node's JSON holds none of the world facts -
        /// seed, world size, world height, resolution - so changing one of them leaves every
        /// content hash identical and the retained cross-bake cache forwards products that
        /// were solved under the OLD numbers. This proves the salt makes that miss instead,
        /// over the real GraphEvaluator and the real retained store, using the same counting
        /// node the T1 test uses.
        ///
        /// It also pins the other half, which matters just as much: an UNCHANGED world must
        /// still HIT. A salt that moved every bake would re-solve the whole graph forever and
        /// turn the cache off while looking like it was on.
        /// </summary>
        private static void TestWorldSaltInvalidation()
        {
            // ---- the salt function itself -------------------------------------------------
            ulong a = GraphContentHasher.WorldSalt(12345, 257, 600f, 4f);
            ulong again = GraphContentHasher.WorldSalt(12345, 257, 600f, 4f);

            Check(a == again,
                  "CTL-CRC1: the world salt must be deterministic - an unchanged world has to "
                  + "produce the same key or nothing is ever cached");
            Check(a != 0UL,
                  "CTL-CRC1: the world salt must never be 0 - that is the no-salt sentinel, so a "
                  + "world folding to zero would silently stop salting");

            Check(GraphContentHasher.WorldSalt(12346, 257, 600f, 4f) != a,
                  "CTL-CRC1: a different seed must change the world salt");
            Check(GraphContentHasher.WorldSalt(12345, 513, 600f, 4f) != a,
                  "CTL-CRC1: a different resolution must change the world salt");
            Check(GraphContentHasher.WorldSalt(12345, 257, 601f, 4f) != a,
                  "CTL-CRC1: a different world height must change the world salt");
            Check(GraphContentHasher.WorldSalt(12345, 257, 600f, 8f) != a,
                  "CTL-CRC1: a different world size must change the world salt");

            // ---- the hashes it produces ---------------------------------------------------
            Graph g = ScriptableObject.CreateInstance<Graph>();
            try
            {
                CacheCountingNode node = new CacheCountingNode();
                g.nodes.Add(node);

                GraphContentHasher.ComputeHashes(g);
                ulong unsalted = node.contentHash;

                GraphContentHasher.ComputeHashes(g, 0UL);
                Check(node.contentHash == unsalted,
                      "CTL-CRC1: a salt of 0 must reproduce the unsalted hash exactly - the "
                      + "legacy overload has to stay byte-for-byte what it was");

                GraphContentHasher.ComputeHashes(g, a);
                ulong salted = node.contentHash;
                Check(salted != unsalted,
                      "CTL-CRC1: a real salt must change the hash or it is a dead parameter");

                GraphContentHasher.ComputeHashes(g, a);
                Check(node.contentHash == salted,
                      "CTL-CRC1: the SAME world must give the same hash - otherwise every bake "
                      + "misses and the cache is off while appearing to be on");

                ulong b = GraphContentHasher.WorldSalt(12345, 257, 900f, 4f);
                GraphContentHasher.ComputeHashes(g, b);
                Check(node.contentHash != salted,
                      "CTL-CRC1: a changed world height must change the hash - this is the "
                      + "stale-product hole the salt exists to close");
            }
            finally
            {
                Object.DestroyImmediate(g);
            }

            // ---- end to end, over the real evaluator and retained store -------------------
            Graph g2 = ScriptableObject.CreateInstance<Graph>();
            try
            {
                CacheCountingNode node = new CacheCountingNode();
                g2.nodes.Add(node);

                GraphEvaluator ev = new GraphEvaluator(g2);
                var store = new System.Collections.Generic.Dictionary<string, RetainedProduct>();

                ulong saltA = GraphContentHasher.WorldSalt(7, 257, 600f, 4f);
                ulong saltB = GraphContentHasher.WorldSalt(7, 257, 900f, 4f);   // only the height moved

                CacheCountingNode.generateCount = 0;

                GraphContentHasher.ComputeHashes(g2, saltA);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 1,
                      "CTL-CRC1: the first salted bake computes the node once (was "
                      + CacheCountingNode.generateCount + ")");

                GraphContentHasher.ComputeHashes(g2, saltA);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 1,
                      "CTL-CRC1: an unchanged world still forwards from the cache - the salt must "
                      + "not cost the saving T1 exists for (was "
                      + CacheCountingNode.generateCount + ")");

                GraphContentHasher.ComputeHashes(g2, saltB);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 2,
                      "CTL-CRC1: changing ONLY the world height - no node touched - must recompute. "
                      + "Before the salt this forwarded a product solved at the old height (was "
                      + CacheCountingNode.generateCount + ")");

                GraphContentHasher.ComputeHashes(g2, saltB);
                ev.PullNode(node, 0, NewCacheTile(store, ev), null);
                Check(CacheCountingNode.generateCount == 2,
                      "CTL-CRC1: the new world then caches like any other (was "
                      + CacheCountingNode.generateCount + ")");
            }
            finally
            {
                Object.DestroyImmediate(g2);
            }

            // ---- the manager's own accessor, so the wiring is pinned and not just the maths --
            GameObject host = new GameObject("UC_SelfTest_WorldFacts");
            try
            {
                UniverseCreator.TerrainSystem.UniverseCreatorObject w =
                    host.AddComponent<UniverseCreator.TerrainSystem.UniverseCreatorObject>();

                ulong before = w.ComputeWorldFactsHash();
                Check(before != 0UL,
                      "CTL-CRC1: a world's facts hash must be non-zero or the bake would pass the "
                      + "no-salt sentinel and silently keep the old behaviour");

                w.maxHeightMeters = w.maxHeightMeters + 100f;
                Check(w.ComputeWorldFactsHash() != before,
                      "CTL-CRC1: raising the world height must change the world's facts hash - this "
                      + "is the accessor the bake actually calls");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// CTL-5 RECURRENCE GUARD (`CTL-CRC2`). The bug CTL-5 fixed was NOT "the salt was
        /// computed wrongly". It was that **a world dial existed which the cache key did not
        /// know about** - so the cache hit when it should have missed, and the world silently
        /// kept stale geometry. Fixing the four known dials does nothing to stop a fifth being
        /// added later and reintroducing exactly the same silence.
        ///
        /// So this gate does not check the four fields. It checks the COUNT: every override
        /// toggle on <c>UCWorldPreset</c> is, by definition, a world dial that can change output
        /// without touching the graph, and therefore MUST be in the salt. If someone adds
        /// `overrideSeaLevel` and does not extend <c>WorldSalt</c>, this fails and says so,
        /// instead of a world quietly serving products solved under the old value.
        ///
        /// It is deliberately a count and not a name list: a name list would need editing for a
        /// rename and would then pass while meaning nothing.
        /// </summary>
        private static void TestWorldSaltCoversEveryWorldDial()
        {
            // How many world dials a preset can drive, counted off the preset type itself.
            System.Reflection.FieldInfo[] fields =
                typeof(UniverseCreator.TerrainSystem.UCWorldPreset).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            int toggles = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(bool)) continue;
                if (!fields[i].Name.StartsWith("override")) continue;
                toggles++;
            }

            Check(toggles == SaltedWorldFactCount,
                  "CTL-CRC2: UCWorldPreset can drive " + toggles + " world dial(s) but the cache "
                  + "salt covers " + SaltedWorldFactCount + ". A dial a preset can change without "
                  + "touching the graph MUST be in GraphContentHasher.WorldSalt, or the cross-bake "
                  + "cache will hit and serve products solved under the old value - the exact "
                  + "silent-stale bug CTL-5 fixed. Add the field to WorldSalt and to "
                  + "ComputeWorldFactsHash, then raise SaltedWorldFactCount to match.");

            // And the salt must actually be sensitive to every one of them, not merely wide
            // enough to count. Each fact is varied alone, from one shared baseline.
            ulong baseline = GraphContentHasher.WorldSalt(1, 129, 100f, 2f);

            Check(GraphContentHasher.WorldSalt(2, 129, 100f, 2f) != baseline
                  && GraphContentHasher.WorldSalt(1, 257, 100f, 2f) != baseline
                  && GraphContentHasher.WorldSalt(1, 129, 200f, 2f) != baseline
                  && GraphContentHasher.WorldSalt(1, 129, 100f, 4f) != baseline,
                  "CTL-CRC2: every salted fact must move the salt on its own - a parameter the "
                  + "salt ignores is a dial the cache cannot see");
        }

        private static void TestPreparedRasterInvalidation()
        {
            // UC-T01-07: exercise real imports and retained/cold fields, with the texture identity unchanged.
            for (int kind = 0; kind < 3; kind++)
            {
                Graph graph = ScriptableObject.CreateInstance<Graph>();
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    Generator node = kind == 0 ? (Generator)new ImportHeightmapNode() :
                        kind == 1 ? new RealWorldImportNode() : new UniverseCreator.Nodes.Selectors.TextureImportMaskNode();
                    node.id = "raster";
                    ConstantNode source = new ConstantNode { id = "unchanged", value = 0.43f };
                    graph.nodes.Add(source);
                    graph.nodes.Add(node);
                    if (kind == 2) graph.connections.Add(new Connection(source.id, 0, node.id, 0));
                    System.Action<Texture2D> assign = value =>
                    {
                        if (node is ImportHeightmapNode heightmap) heightmap.heightmap = value;
                        else if (node is RealWorldImportNode dem) dem.demTexture = value;
                        else ((UniverseCreator.Nodes.Selectors.TextureImportMaskNode)node).maskTexture = value;
                    };
                    assign(texture);
                    Color low = new Color(0.25f, 0.25f, 0.25f, 1f);
                    texture.SetPixels(new[] { low, low, low, low });
                    texture.Apply(false, false);
                    node.PrepareMainThread();
                    GraphContentHasher.ComputeHashes(graph);
                    ulong firstHash = node.contentHash;
                    ulong sourceHash = source.contentHash;
                    GraphEvaluator evaluator = new GraphEvaluator(graph);
                    var retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();
                    Matrix first = PullRasterCacheField(node, evaluator, retained);
                    node.PrepareMainThread();
                    GraphContentHasher.ComputeHashes(graph);
                    Matrix unchanged = PullRasterCacheField(node, evaluator, retained);
                    string label = "UC-T01-07 " + node.NodeName;
                    Check(node.contentHash == firstHash && ReferenceEquals(first, unchanged),
                        label + " forwards unchanged captured pixels.");
                    var textureIdentity = texture.GetEntityId();
                    Color high = new Color(0.82f, 0.82f, 0.82f, 1f);
                    texture.SetPixels(new[] { high, high, high, high });
                    texture.Apply(false, false);
                    node.PrepareMainThread();
                    GraphContentHasher.ComputeHashes(graph);
                    Check(texture.GetEntityId().Equals(textureIdentity) && node.contentHash != firstHash && source.contentHash == sourceHash,
                        label + " hashes in-place texel edits without invalidating an unchanged source.");
                    Matrix changed = PullRasterCacheField(node, evaluator, retained);
                    Matrix cold = PullRasterCacheField(node, evaluator, null);
                    Check(RasterFieldsEqual(changed, cold), label + " retained output equals cold output after repaint.");
                    Check(first != null && changed != null && first.arr.Length > 0 && changed.arr.Length > 0 &&
                          Mathf.Abs(first.arr[0] - changed.arr[0]) > 1e-3f,
                        label + " repaint changes the actual field.");
                    ulong firstCornerHash = 0UL;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        Color[] corners = { low, low, low, low };
                        corners[corner] = high;
                        texture.SetPixels(corners);
                        texture.Apply(false, false);
                        node.PrepareMainThread();
                        GraphContentHasher.ComputeHashes(graph);
                        Check(node.contentHash != firstHash, label + " includes texel " + corner + " in its dependency.");
                        Check(RasterFieldsEqual(PullRasterCacheField(node, evaluator, retained), PullRasterCacheField(node, evaluator, null)),
                            label + " single-texel repaint matches cold output at corner " + corner + ".");
                        if (corner == 0) firstCornerHash = node.contentHash;
                        else if (corner == 3) Check(node.contentHash != firstCornerHash, label + " preserves sample order, not only the histogram.");
                    }
                    assign(null);
                    node.PrepareMainThread();
                    GraphContentHasher.ComputeHashes(graph);
                    Check(RasterFieldsEqual(PullRasterCacheField(node, evaluator, retained), PullRasterCacheField(node, evaluator, null)),
                        label + " removal matches a cold empty input.");
                }
                catch (System.Exception error) { Check(false, "UC-T01-07 raster fixture threw: " + error); }
                finally
                {
                    Object.DestroyImmediate(graph);
                    Object.DestroyImmediate(texture);
                }
            }
        }

        private static Matrix PullRasterCacheField(Generator node, GraphEvaluator evaluator,
            System.Collections.Generic.Dictionary<string, RetainedProduct> retained)
        {
            TileData tile = new TileData(default(Coord), new CoordRect(0, 0, 3, 3),
                Vector3.zero, new Vector3(10f, 100f, 10f), 100f);
            tile.retained = retained;
            tile.Bind(evaluator);
            return evaluator.PullNode(node, 0, tile, new StopToken()) as Matrix;
        }

        private static bool RasterFieldsEqual(Matrix a, Matrix b)
        {
            if (a == null || b == null || a.arr.Length != b.arr.Length || a.arr.Length == 0) return false;
            for (int i = 0; i < a.arr.Length; i++)
                if (float.IsNaN(a.arr[i]) || float.IsNaN(b.arr[i]) || Mathf.Abs(a.arr[i] - b.arr[i]) > 1e-5f) return false;
            return true;
        }

        /// <summary>
        /// How many world facts <c>GraphContentHasher.WorldSalt</c> folds in. Raised ONLY together
        /// with the salt itself; `CTL-CRC2` compares it against the preset's own dial count so the
        /// two cannot drift apart silently.
        /// </summary>
        private const int SaltedWorldFactCount = 4;
    }
}
