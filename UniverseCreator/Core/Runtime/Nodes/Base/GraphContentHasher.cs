// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GraphContentHasher.cs
//  Purpose: T1 - fills every node's Generator.contentHash with a value that changes
//           if and only if the node's own serialized state OR anything upstream of
//           it changed. A per-chunk retained product cache then forwards an unchanged
//           node's output and recomputes only the subtree an edit actually touched -
//           incremental invalidation (§4c G1) without re-baking the whole graph for
//           every chunk on every edit.
//
//  WHY WHOLE-NODE JSON, NOT HAND-PICKED FIELDS
//  -------------------------------------------
//  A hand-written per-field hash is the classic way this goes wrong: miss one field
//  and an edit to it never invalidates, so the cache serves STALE terrain. Hashing
//  JsonUtility.ToJson(node) instead captures EVERY serialized field automatically, so
//  no field can be forgotten. It over-counts a little (editor position is serialized
//  too, so nudging a node re-bakes it) - but over-recompute is only wasted work, never
//  wrong output, which is the safe side to err on.
//
//  WHY MAIN THREAD
//  ---------------
//  JsonUtility is a Unity API, so ComputeHashes runs on the main thread, before a bake
//  dispatches to workers. The hash is per-node (chunk-independent), so it is computed
//  ONCE per bake and read by every chunk's worker as a plain ulong.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// UC-O01 / UC-A03: opt-in dependency data prepared outside a node's serialized fields.
    /// The hasher reads it on the main thread before dispatch; it is not a worker callback.
    /// </summary>
    public interface IPreparedGraphInputs
    {
        /// <summary>Provides the actual plain-data inputs consumed by this node's next bake.</summary>
        /// <returns>A Unity-serializable data object, or null when no external inputs apply.</returns>
        /// <remarks>
        /// Public serialized fields must cover every prepared input affecting output. Do not
        /// return live scene objects. Keep the data stable while hashing and consuming the run.
        /// A reusable wrapper is permitted; this method must not change generation inputs.
        /// </remarks>
        object GetPreparedInputsForHash();
    }

    /// <summary>UC-T01-07: optional allocation-free hashing for large prepared inputs such as raster pixels.</summary>
    /// <remarks>Main thread after preparation. Hash every consumed value, its order/dimensions and absence; do not read live Unity objects.</remarks>
    public interface IPreparedGraphInputHash
    {
        /// <summary>Returns a stable fingerprint of the actual captured inputs without serializing a large payload.</summary>
        /// <returns>A deterministic 64-bit content hash; equal prepared data must return the same value.</returns>
        ulong GetPreparedInputHash();
    }

    /// <summary>
    /// Computes each node's combined content hash (own serialized state + all upstream
    /// hashes) for the T1 incremental-invalidation cache. Call on the main thread.
    /// </summary>
    public static class GraphContentHasher
    {
        /// <summary>
        /// Fills <see cref="Generator.contentHash"/> for every node in the graph. A node
        /// whose params and whole upstream chain are unchanged keeps the same hash across
        /// bakes; any change anywhere upstream changes it. Cyclic dependency chains
        /// receive the zero sentinel, disabling cross-bake retention for those nodes.
        /// UC-O01 / UC-A01: multi-input ports include every edge in evaluation order;
        /// single-input ports retain their last-connection semantics.
        /// UC-A02: portal receivers also include their resolved wireless sender.
        /// UC-A03: opt-in prepared inputs participate before downstream propagation.
        /// </summary>
        /// <param name="graph">Graph to hash on the main thread; null is a no-op.</param>
        public static void ComputeHashes(Graph graph)
        {
            ComputeHashes(graph, 0UL);
        }

        /// <summary>
        /// CTL-5. The same thing, plus a WORLD SALT folded into every node's hash.
        ///
        /// WHY A SALT IS NEEDED AT ALL - this closes a real staleness hole:
        /// a node's output depends on world facts that live on the manager and appear in
        /// NO node's serialized state - the seed, the world size, the world height in
        /// metres, the resolution. Change the world height and not one node's JSON moves,
        /// so every contentHash is unchanged, so the cross-bake retained cache forwards
        /// products that were computed against the OLD height. The tile is built with the
        /// new height while its contents were solved under the old one, which is the
        /// "I changed a dial and nothing changed" symptom with a cache behind it rather
        /// than a graph.
        ///
        /// A preset (CTL-2) is exactly a thing that changes those dials without touching
        /// the graph, which is why the salt belongs with the preset work and not later.
        ///
        /// The salt is mixed ONCE per node, after the upstream fold, so it shifts every
        /// hash uniformly and the memo stays salt-independent within a call. A salt of 0
        /// is left out entirely, so <see cref="ComputeHashes(Graph)"/> produces exactly
        /// the same unsalted dependency hashes as the overload without a salt.
        /// </summary>
        /// <param name="graph">Graph to hash on the main thread; null is a no-op.</param>
        /// <param name="worldSalt">World-context revision hash; zero omits this additional salt.</param>
        public static void ComputeHashes(Graph graph, ulong worldSalt)
        {
            if (graph == null || graph.nodes == null) return;

            Dictionary<string, ulong> memo = new Dictionary<string, ulong>();
            HashSet<string> visiting = new HashSet<string>();

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator n = graph.nodes[i];
                if (n == null) continue;

                ulong h = HashOf(n, graph, memo, visiting);

                // A cycle-bailed node hashes to 0 and MUST stay 0: the evaluator reads 0 as
                // "do not retain this", and salting it would hand the cache a live-looking
                // key for a node whose product is not trustworthy.
                if (worldSalt != 0UL && h != 0UL) h = Mix(h, worldSalt);

                n.contentHash = h;
            }
        }

        /// <summary>
        /// CTL-5. The world facts a node reads but never serializes, folded into one value.
        /// Deterministic across runs and machines: the floats go in by their exact bit
        /// pattern, so no rounding decides whether a cache hits.
        ///
        /// These four are precisely the fields <c>UCWorldPreset</c> can drive, which is the
        /// point - a preset swap must be able to invalidate a cache the graph cannot.
        /// </summary>
        public static ulong WorldSalt(int seed, int resolution, float maxHeightMetres,
                                      float worldSizeKm)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                h = Mix(h, (ulong)(uint)seed);
                h = Mix(h, (ulong)(uint)resolution);
                h = Mix(h, (ulong)(uint)FloatBits(maxHeightMetres));
                h = Mix(h, (ulong)(uint)FloatBits(worldSizeKm));

                // Never return 0: that is the "no salt" sentinel, and a world whose facts
                // happened to fold to zero would silently stop salting.
                return h != 0UL ? h : 1UL;
            }
        }

        /// <summary>
        /// A float's exact bits, so two values that differ by a hair still differ here.
        /// NaN is normalised, because NaN != NaN would make a hash unstable between bakes
        /// for an unchanged world.
        /// </summary>
        private static int FloatBits(float f)
        {
            if (float.IsNaN(f)) return unchecked((int)0x7FC00000);
            if (f == 0f) return 0;                      // -0f and 0f are the same world
            return System.BitConverter.ToInt32(System.BitConverter.GetBytes(f), 0);
        }

        private static ulong HashOf(Generator node, Graph graph,
                                    Dictionary<string, ulong> memo, HashSet<string> visiting)
        {
            if (node == null) return 0UL;

            ulong cached;
            if (memo.TryGetValue(node.id, out cached)) return cached;

            // Cycle: a node already on the stack contributes 0 rather than recursing
            // forever - the same bail Graph.Validate / the evaluator use for a bad edge.
            if (!visiting.Add(node.id)) return 0UL;

            ulong combined = FnvString(JsonUtility.ToJson(node));
            // UC-T01-07: ecology settings/profiles invalidate their consumers and downstream only.
            if (node.preparedContextHash != 0UL) combined = Mix(combined, node.preparedContextHash);
            // UC-O01 / UC-A03: scene-prepared data is deliberately absent from node JSON.
            IPreparedGraphInputHash directPreparedHash = node as IPreparedGraphInputHash;
            IPreparedGraphInputs preparedProvider = node as IPreparedGraphInputs;
            if (directPreparedHash != null)
                combined = Mix(combined, directPreparedHash.GetPreparedInputHash());
            else if (preparedProvider != null)
            {
                object prepared = preparedProvider.GetPreparedInputsForHash();
                if (prepared != null) combined = Mix(combined, FnvString(JsonUtility.ToJson(prepared)));
            }
            bool cyclicDependency = false;

            // UC-O01 / UC-A02: portals consume a sender without a visible input wire.
            IPortalIn receiver = node as IPortalIn;
            if (receiver != null)
            {
                Generator sender = receiver.ResolveSender(graph);
                ulong senderHash = HashOf(sender, graph, memo, visiting);
                cyclicDependency = sender != null && senderHash == 0UL;
                combined = Mix(combined, senderHash);
            }

            Port[] ins = node.GetInputs();
            int count = ins != null ? ins.Length : 0;
            for (int i = 0; i < count; i++)
            {
                // UC-O01 / UC-A01: match fan-in evaluation without a per-inlet list allocation.
                if (ins[i].allowMultiple)
                {
                    int edgeCount = 0;
                    // UC-T01-08: captured graphs visit this inlet's ordered edges; live graphs keep allocation-free scanning.
                    IReadOnlyList<Connection> edges = graph.compiledPlan != null
                        ? graph.compiledPlan.GetInputEdges(node.id, i) : graph.connections;
                    for (int j = 0; j < edges.Count; j++)
                    {
                        Connection edge = edges[j];
                        if (edge.targetNodeId != node.id || edge.targetPortIndex != i) continue;
                        Generator source = graph.GetNodeById(edge.sourceNodeId);
                        ulong upstream = HashOf(source, graph, memo, visiting);
                        if (source != null && upstream == 0UL) cyclicDependency = true;
                        combined = Mix(combined, upstream ^ (ulong)(edge.sourcePortIndex + 1));
                        edgeCount++;
                    }
                    // Keep inlet boundaries when a graph redistributes the same source edges.
                    combined = Mix(combined, (ulong)edgeCount);
                    continue;
                }

                Connection c = graph.GetInputConnection(node.id, i);
                if (string.IsNullOrEmpty(c.sourceNodeId))
                {
                    combined = Mix(combined, 0UL);   // an unconnected inlet is part of the state too
                    continue;
                }

                Generator src = graph.GetNodeById(c.sourceNodeId);
                ulong up = HashOf(src, graph, memo, visiting);
                if (src != null && up == 0UL) cyclicDependency = true;
                // Fold in which source PORT feeds this inlet, so a rewire to a different
                // output of the same node still changes the hash.
                combined = Mix(combined, up ^ (ulong)(c.sourcePortIndex + 1));
            }

            if (graph.Domain == UCGraphDomain.Vegetation)
                foreach (Generator producer in graph.nodes)
                    if (CompiledGraphPlan.IsCanopyDependency(producer, node))
                    {
                        // UC-T06-05: changing a canopy producer invalidates lower-layer placement and habitat masks, including warm retained paths.
                        ulong canopyHash = HashOf(producer, graph, memo, visiting);
                        if (canopyHash == 0) cyclicDependency = true;
                        combined = Mix(combined, canopyHash);
                    }
            visiting.Remove(node.id);
            // UC-O01 / UC-A05: never turn the cycle sentinel into a retainable hash.
            if (cyclicDependency) combined = 0UL;
            memo[node.id] = combined;
            return combined;
        }

        /// <summary>FNV-1a 64-bit over a string's chars. Deterministic across runs/machines.</summary>
        private static ulong FnvString(string s)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;   // FNV offset basis
                if (s != null)
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= s[i];
                        h *= 1099511628211UL;        // FNV prime
                    }
                return h;
            }
        }

        /// <summary>Order-dependent 64-bit mix (boost::hash_combine shape) so the fold is not commutative.</summary>
        private static ulong Mix(ulong a, ulong b)
        {
            unchecked
            {
                a ^= b + 0x9E3779B97F4A7C15UL + (a << 6) + (a >> 2);
                return a;
            }
        }
    }
}
