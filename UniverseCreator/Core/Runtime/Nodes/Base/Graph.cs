// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Graph.cs
//  Purpose: Asset container for a node graph: the flat list of Generator
//           instances plus the Connection[] that wires them together. This is
//           what UniverseCreatorObject.graph points at.
//
//  WHY ScriptableObject
//  --------------------
//  A graph has to be a first-class project asset, not inline component data:
//  the inspector's object-picker circle, the "Select Graph" dialog, and
//  double-click-to-open all require a UnityEngine.Object with a GUID. It also
//  lets one graph drive several worlds. Storage is a .asset file (Unity will not
//  import a custom .ucgraph extension without a ScriptedImporter, which is out
//  of Module 1 scope) - see GraphAssetUtility.
//
//  THREADING
//  ---------
//  nodes/connections are plain managed fields, so a worker thread may read them.
//  What it must NOT do is touch the UnityEngine.Object side - `name`, or a
//  `graph == null` comparison, both marshal into native code. Callers snapshot
//  and null-check on the main thread before dispatching; see
//  UniverseCreatorObject.RegenerateAsync.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T01-10: portable Unity object identity for one serialized field in an imported node.</summary>
    [System.Serializable]
    public sealed class NodeObjectReference
    {
        /// <summary>Serialized field path relative to the node, including array indices.</summary>
        public string path;
        /// <summary>Unity GlobalObjectId text, resolved only by the Editor on import.</summary>
        public string globalId;
    }

    /// <summary>UC-T01-10: opaque imported node data retained until its original type is available.</summary>
    /// <remarks>This is recovery data, never an executable replacement node. Payload units and fields belong to the original type.</remarks>
    [System.Serializable]
    public sealed class UnresolvedNodeRecord
    {
        /// <summary>Zero-based index of the preserved null slot in Graph.nodes.</summary>
        public int slot;
        /// <summary>Exact imported type identifier; empty for an explicitly empty slot.</summary>
        public string type;
        /// <summary>Unmodified node JSON, including unknown fields and object-reference information.</summary>
        public string json;
        /// <summary>Stable Unity asset/scene identities carried alongside the opaque payload.</summary>
        public List<NodeObjectReference> objectReferences;
        /// <summary>UC-T01-10: verified native original for a missing slot; null for JSON recovery.</summary>
        public Graph nativeOriginal;
        /// <summary>True for native recovery, including when its original asset is unavailable.</summary>
        public bool nativeRecovery;
        /// <summary>Zero-based slot in nativeOriginal before ordinary deletion moved the current slot.</summary>
        public int nativeSlot;
    }

    /// <summary>
    /// A procedural content graph: nodes + connections. Saved as a project asset,
    /// and read by GraphEvaluator to run the pull-based evaluation.
    /// </summary>
    public class Graph : ScriptableObject, ISerializationCallbackReceiver
    {
        /// <summary>Current graph-domain metadata schema; zero denotes an unchanged legacy asset.</summary>
        public const int CurrentDomainSchemaVersion = 1; // UC-T05-01: independent of the portable JSON container version.

        [SerializeField, HideInInspector] private UCGraphDomain graphDomain = UCGraphDomain.LegacyMixed;
        [SerializeField, HideInInspector] private int graphDomainSchemaVersion;

        /// <summary>Saved domain identity, including unknown future numeric values retained for recovery.</summary>
        public UCGraphDomain Domain => graphDomain;

        /// <summary>Saved domain schema: zero is legacy, one is supported, and other values require recovery.</summary>
        public int DomainSchemaVersion => graphDomainSchemaVersion;

        /// <summary>Whether the saved metadata can be interpreted without guessing or changing authored data.</summary>
        public bool HasSupportedDomainMetadata =>
            (graphDomainSchemaVersion == 0 && graphDomain == UCGraphDomain.LegacyMixed) ||
            (graphDomainSchemaVersion == CurrentDomainSchemaVersion && IsKnownDomain(graphDomain));

        /// <summary>Empty for supported metadata; otherwise explains the retained schema/domain requiring recovery.</summary>
        public string DomainDiagnostic => HasSupportedDomainMetadata ? string.Empty :
            "Unsupported graph-domain metadata: schema " + graphDomainSchemaVersion + ", domain " + (int)graphDomain +
            ". Preserve this asset and open it with a compatible UC version before execution or migration.";

        /// <summary>Assigns supported domain metadata without changing nodes, wires, settings or world bindings.</summary>
        /// <param name="domain">A declared UCGraphDomain value; LegacyMixed retains the mixed workflow.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">The requested domain is undeclared.</exception>
        /// <exception cref="System.InvalidOperationException">Called outside the Unity main thread.</exception>
        /// <remarks>UC-T05-01: main-thread authoring operation; callers own Undo and persistence. Never runs implicitly on load.</remarks>
        public void SetDomain(UCGraphDomain domain)
        {
            GraphExecutionGuard.RequireMainThread("Graph domain authoring"); // UC-T05-01: metadata/index mutation belongs to the authoring thread.
            if (!IsKnownDomain(domain)) throw new System.ArgumentOutOfRangeException(nameof(domain));
            RestoreDomainMetadata(domain, CurrentDomainSchemaVersion);
        }

        // UC-T05-01: serializers preserve unsupported metadata verbatim; validation refuses to interpret it.
        internal void RestoreDomainMetadata(UCGraphDomain domain, int schemaVersion)
        {
            graphDomain = domain; graphDomainSchemaVersion = schemaVersion;
            compiledPlan = null;
        }

        private static bool IsKnownDomain(UCGraphDomain domain)
        {
            return domain == UCGraphDomain.LegacyMixed || domain == UCGraphDomain.TerrainWater ||
                domain == UCGraphDomain.Vegetation || domain == UCGraphDomain.LightingClimateWeather ||
                domain == UCGraphDomain.TextureMaterialShader;
        }

        /// <summary>
        /// Flat list of all nodes in the graph. Unity's serialization system stores
        /// each Generator subclass via [SerializeReference] so polymorphism works.
        /// </summary>
        [SerializeReference]
        public List<Generator> nodes = new List<Generator>();

        // UC-T01-08: only captured revisions attach indexes; live authoring keeps its mutable-list queries.
        [System.NonSerialized] internal CompiledGraphPlan compiledPlan;

        /// <summary>
        /// All connections between nodes. Each entry links one output port to one
        /// input port. The evaluator walks backward from output nodes along these
        /// edges to pull dependencies.
        /// </summary>
        public List<Connection> connections = new List<Connection>();

        /// <summary>UC-T01-10: unresolved JSON payloads associated with null node slots; never used for generation.</summary>
        [HideInInspector] public List<UnresolvedNodeRecord> unresolvedNodes = new List<UnresolvedNodeRecord>();

        // The display name comes from ScriptableObject.name (i.e. the asset name),
        // so there is deliberately no `name` field here - declaring one would hide
        // the inherited member and desync from the file on disk.

        // ------------------------------------------------------------------
        //  Missing-type repair
        // ------------------------------------------------------------------

        /// <summary>
        /// Drops list entries whose node class no longer exists, and any connection
        /// left pointing at nothing. Returns how many entries were dropped.
        ///
        /// WHY A GRAPH CAN HAVE HOLES IN IT
        /// --------------------------------
        /// nodes is [SerializeReference]. Delete a node class and Unity does not
        /// remove its entry - it keeps the slot, hands the managed side a null, and
        /// logs "Missing types referenced from component Graph on game object ...".
        /// That is deliberate on Unity's part: if the script comes back, the node's
        /// data is still there. Until it does, though, the list has a hole in it, and
        /// every loop below that reads n.id would throw on the hole instead of
        /// reporting a graph problem. The water Output nodes the rework removed are
        /// the case that found this - a graph authored while they existed still
        /// carries their slots.
        ///
        /// WHY CONNECTIONS GO BY SURVIVOR SET AND NOT BY ID
        /// -----------------------------------------------
        /// A hole's id went with its class, so there is nothing left to match edges
        /// against. What we can say is that an edge naming no node we still have is
        /// dead, whichever end it is. That sweep also collects edges left dangling by
        /// anything else, which Validate could report but not fix.
        /// </summary>
        /// <returns>The number of missing node slots explicitly removed.</returns>
        /// <remarks>
        /// UC-O01 / UC-A06: destructive repair, never a serialization callback.
        /// Call only after the author chooses to discard the missing nodes and their wires.
        /// Ordinary saving/loading must preserve them so a restored node type can recover.
        /// </remarks>
        public int PruneMissingNodes()
        {
            if (nodes == null)
            {
                nodes = new List<Generator>();
                return 0;
            }

            // UC-O01 / UC-A06: explicit repair of a healthy graph needs no allocation.
            int holes = 0;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] == null) holes++;
            if (holes == 0) return 0;

            compiledPlan = null; // UC-T01-08: explicit repairs replace the indexed revision.
            nodes.RemoveAll(n => n == null);
            // UC-T01-11: only explicit repair discards the recoverable payloads.
            unresolvedNodes.Clear();

            if (connections != null)
            {
                HashSet<string> alive = new HashSet<string>();
                for (int i = 0; i < nodes.Count; i++)
                    if (!string.IsNullOrEmpty(nodes[i].id)) alive.Add(nodes[i].id);

                connections.RemoveAll(c => !alive.Contains(c.sourceNodeId) ||
                                           !alive.Contains(c.targetNodeId));
            }

            return holes;
        }

        // UC-O01 / UC-A06: retain null slots and all wires during normal serialization.
        // Unity keeps missing managed-reference payloads separately for type restoration.
        // Removing their slots here makes a routine save/copy destructive. Validation
        // already reports holes; only an explicitly chosen repair may discard them.
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            // Deliberately preserve authored data; this callback may run off the main thread.
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            compiledPlan = null; // UC-T01-08: indexes belong to a prepared revision, never serialized state.
            // Normalize absent containers only; never infer that an unresolved node was deleted.
            if (nodes == null) nodes = new List<Generator>();
            if (connections == null) connections = new List<Connection>();
            if (unresolvedNodes == null) unresolvedNodes = new List<UnresolvedNodeRecord>();
        }

        // ------------------------------------------------------------------
        //  Node lookup
        // ------------------------------------------------------------------

        /// <summary>Finds a node by id, or null if not found.</summary>
        public Generator GetNodeById(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            if (compiledPlan != null) return compiledPlan.GetNode(nodeId);
            foreach (Generator n in nodes)
                if (n != null && n.id == nodeId) return n;
            return null;
        }

        /// <summary>
        /// Finds all nodes of a given type. Used by the evaluator to locate IOutputNode
        /// roots when starting the pull.
        /// </summary>
        public List<T> GetNodesOfType<T>() where T : Generator
        {
            List<T> result = new List<T>();
            foreach (Generator n in nodes)
                if (n is T) result.Add((T)n);
            return result;
        }

        // ------------------------------------------------------------------
        //  Connection queries
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds the single connection feeding the given input port, or returns
        /// default(Connection) if none exists. Module 1 inputs accept only one
        /// connection (last write wins if multiple are serialized).
        /// </summary>
        public Connection GetInputConnection(string targetNodeId, int targetPortIndex)
        {
            if (compiledPlan != null) return compiledPlan.GetInputConnection(targetNodeId, targetPortIndex);
            for (int i = connections.Count - 1; i >= 0; i--)
            {
                Connection c = connections[i];
                if (c.targetNodeId == targetNodeId && c.targetPortIndex == targetPortIndex)
                    return c;
            }
            return default(Connection);
        }

        /// <summary>
        /// Finds EVERY connection feeding the given input port, in list order. For a
        /// single-connection port this is the one edge; for a multi-input (fan-in)
        /// port it is all of them, which <see cref="GraphEvaluator.PullInputAll"/>
        /// folds. Returns an empty list when the port is unconnected.
        /// </summary>
        public List<Connection> GetInputConnections(string targetNodeId, int targetPortIndex)
        {
            // UC-T01-08: public callers retain ownership of their mutable result list.
            if (compiledPlan != null) return new List<Connection>(compiledPlan.GetInputEdges(targetNodeId, targetPortIndex));
            List<Connection> result = new List<Connection>();
            for (int i = 0; i < connections.Count; i++)
            {
                Connection c = connections[i];
                if (c.targetNodeId == targetNodeId && c.targetPortIndex == targetPortIndex)
                    result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// Finds all connections originating from the given output port. Used when
        /// the evaluator needs to invalidate downstream nodes after a parameter change.
        /// </summary>
        public List<Connection> GetOutputConnections(string sourceNodeId, int sourcePortIndex)
        {
            if (compiledPlan != null) return new List<Connection>(compiledPlan.GetOutputEdges(sourceNodeId, sourcePortIndex));
            List<Connection> result = new List<Connection>();
            foreach (Connection c in connections)
            {
                if (c.sourceNodeId == sourceNodeId && c.sourcePortIndex == sourcePortIndex)
                    result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// Counts the connections leaving one output port, without allocating a list. ENG-4
        /// uses this to tell a SOLE consumer (safe to hand the product by reference for in-place
        /// mutation) from a fan-out (must copy, since the same Matrix feeds other nodes).
        /// </summary>
        public int CountOutputConnections(string sourceNodeId, int sourcePortIndex)
        {
            if (compiledPlan != null) return compiledPlan.GetOutputEdges(sourceNodeId, sourcePortIndex).Count;
            int n = 0;
            for (int i = 0; i < connections.Count; i++)
                if (connections[i].sourceNodeId == sourceNodeId &&
                    connections[i].sourcePortIndex == sourcePortIndex) n++;
            return n;
        }

        /// <summary>Returns true if any connection touches the given node.</summary>
        public bool IsNodeConnected(string nodeId)
        {
            foreach (Connection c in connections)
                if (c.sourceNodeId == nodeId || c.targetNodeId == nodeId)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------
        //  Modification
        // ------------------------------------------------------------------

        /// <summary>Adds a node to the graph and returns it.</summary>
        public T AddNode<T>() where T : Generator, new()
        {
            T node = new T();
            AddNode(node);
            return node;
        }

        /// <summary>
        /// Adds an already-constructed node. The add-node menu builds instances by
        /// reflection, so it cannot go through the generic overload; both funnel
        /// here to keep one insertion path.
        /// </summary>
        public Generator AddNode(Generator node)
        {
            if (node == null) return null;
            compiledPlan = null; // UC-T01-08: supported topology edits invalidate indexes before querying.
            nodes.Add(node);
            return node;
        }

        /// <summary>Removes the named live node and its wires, preserving unrelated missing nodes and recovery data.</summary>
        /// <param name="nodeId">Exact authored node ID; null or empty is a no-op.</param>
        /// <remarks>In the Editor, native missing types are retained in a verified recovery asset before moving slots.</remarks>
        /// <exception cref="System.InvalidOperationException">A native original cannot be saved or verified; no node is removed.</exception>
        public void RemoveNode(string nodeId)
        {
            // UC-T01-10: deleting a healthy node must not destroy a missing plugin's slot.
            if (string.IsNullOrEmpty(nodeId)) return;
            compiledPlan = null; // UC-T01-08: deletion creates a new topology revision.
#if UNITY_EDITOR
            // UC-T01-10: Unity 6000.5 loses hidden type data on disk when a null slot
            // moves, even through SerializedProperty. Preserve native bytes first.
            if (UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(this))
            {
                if (GetNodeById(nodeId) == null) return;
                PreserveNativeMissingSlots(UnityEditor.AssetDatabase.LoadAssetAtPath<Graph>(CreateNativeRecoveryOriginal()));
            }
#endif
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                if (nodes[i] == null || nodes[i].id != nodeId) continue;
                nodes.RemoveAt(i);
                for (int r = 0; r < unresolvedNodes.Count; r++)
                    if (unresolvedNodes[r].slot > i) unresolvedNodes[r].slot--;
            }
            connections.RemoveAll(c => c.sourceNodeId == nodeId || c.targetNodeId == nodeId);
        }

#if UNITY_EDITOR
        // UC-T01-10: missing serializedData is a diagnostic summary, not a portable
        // payload. A native asset retains binary/text formats and Unity references.
        /// <summary>UC-T01-10/11: saves and verifies a native recovery original without moving missing slots.</summary>
        /// <returns>Project-relative path of the new native asset, with the source filename retained.</returns>
        /// <exception cref="System.InvalidOperationException">The graph is unsaved, or the copy cannot be verified.</exception>
        /// <remarks>Editor main thread only. Saves current authored values; throws on filesystem/import failure before graph mutation.</remarks>
        public string CreateNativeRecoveryOriginal()
        {
            string path = UnityEditor.AssetDatabase.GetAssetPath(this);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
                throw new System.InvalidOperationException("Save the graph as an asset before deleting a node beside a missing native type.");
            UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
            string folder = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(path.Substring(0, path.Length - 6) + " Recovery");
            string guid = UnityEditor.AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(folder).Replace('\\', '/'), System.IO.Path.GetFileName(folder));
            if (string.IsNullOrEmpty(guid)) throw new System.InvalidOperationException("Could not create a native graph recovery folder; no node was removed.");
            string backup = folder + "/" + System.IO.Path.GetFileName(path);
            System.IO.File.Copy(path, backup, false);
            UnityEditor.AssetDatabase.ImportAsset(backup, UnityEditor.ImportAssetOptions.ForceUpdate);
            Graph original = UnityEditor.AssetDatabase.LoadAssetAtPath<Graph>(backup);
            if (original == null || original.nodes.Count != nodes.Count ||
                UnityEditor.SerializationUtility.GetManagedReferencesWithMissingTypes(original).Length !=
                UnityEditor.SerializationUtility.GetManagedReferencesWithMissingTypes(this).Length)
                throw new System.InvalidOperationException("Native recovery verification failed; no node was removed from " + path);
            return backup;
        }

        /// <summary>UC-T01-10/11: retains missing slots through a verified original instead of Unity's transient hidden records.</summary>
        /// <param name="original">Saved native recovery graph with the same slot order; must differ from this graph.</param>
        /// <exception cref="System.ArgumentException">The original is absent, unsaved or this graph itself.</exception>
        /// <remarks>Editor main thread only, before deletion or after Undo. Marks this graph dirty; unavailable types remain non-executable.</remarks>
        public void PreserveNativeMissingSlots(Graph original)
        {
            if (original == null || original == this || !UnityEditor.EditorUtility.IsPersistent(original) || original.nodes.Count != nodes.Count)
                throw new System.ArgumentException("A separate saved native original is required.", nameof(original));
            if (!UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(original)) return;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != null || unresolvedNodes.Exists(record => record.slot == i)) continue;
                unresolvedNodes.Add(new UnresolvedNodeRecord { slot = i, type = "Native recovery slot",
                    nativeOriginal = original, nativeSlot = i, nativeRecovery = true });
            }
            UnityEditor.SerializationUtility.ClearAllManagedReferencesWithMissingTypes(this);
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        /// <summary>
        /// Adds a connection. A single-connection input keeps only the newest edge
        /// (the old one is removed first - Module 1 last-write-wins); a multi-input
        /// port (<see cref="Port.allowMultiple"/>) accumulates edges instead, so a
        /// field can fan several sources into one merge. Rejects a connection that
        /// would create a cycle, and an exact duplicate edge on a multi-input port,
        /// returning false.
        /// </summary>
        public bool AddConnection(Connection conn)
        {
            compiledPlan = null; // UC-T01-08: connection replacement changes ordering and consumer counts.
            if (conn.sourceNodeId == conn.targetNodeId) return false;
            if (WouldCreateCycle(conn.sourceNodeId, conn.targetNodeId)) return false;

            if (TargetInputAllowsMultiple(conn))
            {
                // Fan-in: keep every distinct edge, but never the exact same wire twice.
                for (int i = 0; i < connections.Count; i++)
                    if (connections[i].Equals(conn)) return false;
            }
            else
            {
                // Single-input: evict any existing edge into this port first.
                connections.RemoveAll(c => c.targetNodeId == conn.targetNodeId &&
                                           c.targetPortIndex == conn.targetPortIndex);
            }

            connections.Add(conn);
            return true;
        }

        /// <summary>
        /// True when the connection's target input port is declared as accepting
        /// multiple incoming wires. Resolved from the live node's GetInputs(), so a
        /// node that never opts in keeps the single-connection behaviour unchanged.
        /// </summary>
        private bool TargetInputAllowsMultiple(Connection conn)
        {
            Generator tgt = GetNodeById(conn.targetNodeId);
            if (tgt == null) return false;

            Port[] ins = tgt.GetInputs();
            if (ins == null || conn.targetPortIndex < 0 || conn.targetPortIndex >= ins.Length)
                return false;

            return ins[conn.targetPortIndex].allowMultiple;
        }

        /// <summary>
        /// True if wiring source -> target would close a loop. The evaluator is
        /// recursive, so an undetected cycle means a stack overflow rather than a
        /// polite error; this is the gate that keeps that from happening.
        /// </summary>
        public bool WouldCreateCycle(string sourceNodeId, string targetNodeId)
        {
            if (sourceNodeId == targetNodeId) return true;

            // Walk downstream from target. If we reach source, the edge closes a loop.
            HashSet<string> seen = new HashSet<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(targetNodeId);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (current == sourceNodeId) return true;
                if (!seen.Add(current)) continue;

                foreach (Connection c in connections)
                    if (c.sourceNodeId == current) pending.Push(c.targetNodeId);
            }
            return false;
        }

        /// <summary>Removes a specific connection.</summary>
        public void RemoveConnection(Connection conn)
        {
            compiledPlan = null; // UC-T01-08: disconnects invalidate captured indexes.
            connections.Remove(conn);
        }

        /// <summary>Clears all nodes and connections.</summary>
        public void Clear()
        {
            compiledPlan = null; // UC-T01-08: no indexed node survives a clear.
            nodes.Clear();
            connections.Clear();
            unresolvedNodes.Clear();
        }

        // ------------------------------------------------------------------
        //  Validation
        // ------------------------------------------------------------------

        /// <summary>
        /// UC-T01-09: returns topology/type/cycle errors without repairing authored data.
        /// </summary>
        /// <returns>Caller-owned error messages; empty for a valid graph. Portal fallback warnings are omitted.</returns>
        /// <remarks>Admits all declared execution domains; dispatchers use GraphValidationReport with their narrower domain mask.</remarks>
        public List<string> Validate()
        {
            // UC-T01-09: the legacy string API uses the same authoritative report as execution and the canvas.
            GraphValidationReport report = GraphValidationReport.Analyze(this);
            List<string> errors = new List<string>();
            foreach (GraphDiagnostic diagnostic in report.Diagnostics)
                if (diagnostic.Severity == GraphDiagnosticSeverity.Error) errors.Add(diagnostic.Message);
            return errors;
        }
    }

    /// <summary>
    /// Marker interface for output nodes (HeightOutput, SplatOutput, etc.). The
    /// evaluator starts the pull from nodes implementing this.
    /// </summary>
    public interface IOutputNode
    {
        // No methods required; just a tag so GraphEvaluator knows where to start.
    }
}
