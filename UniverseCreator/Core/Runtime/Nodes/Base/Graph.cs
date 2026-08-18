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
    /// <summary>
    /// A procedural content graph: nodes + connections. Saved as a project asset,
    /// and read by GraphEvaluator to run the pull-based evaluation.
    /// </summary>
    public class Graph : ScriptableObject, ISerializationCallbackReceiver
    {
        /// <summary>
        /// Flat list of all nodes in the graph. Unity's serialization system stores
        /// each Generator subclass via [SerializeReference] so polymorphism works.
        /// </summary>
        [SerializeReference]
        public List<Generator> nodes = new List<Generator>();

        /// <summary>
        /// All connections between nodes. Each entry links one output port to one
        /// input port. The evaluator walks backward from output nodes along these
        /// edges to pull dependencies.
        /// </summary>
        public List<Connection> connections = new List<Connection>();

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
        public int PruneMissingNodes()
        {
            if (nodes == null)
            {
                nodes = new List<Generator>();
                return 0;
            }

            // Fast path first. OnBeforeSerialize runs on every serialise, including
            // the one the inspector does per repaint, so a healthy graph has to cost
            // one scan and no allocation.
            int holes = 0;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] == null) holes++;
            if (holes == 0) return 0;

            nodes.RemoveAll(n => n == null);

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

        // Both directions. OnAfterDeserialize so nothing downstream ever meets a
        // hole; OnBeforeSerialize so a save writes the graph without one. Pure
        // managed list work, which is all these callbacks may do - no touching the
        // UnityEngine.Object side, because OnAfterDeserialize can run off the main
        // thread.
        //
        // Pruning here is not by itself enough to stop the warning: Unity keeps the
        // missing type in a record beside the list, so the file still declares it.
        // Tools > Universe Creator > Diagnostics > Repair Graphs clears that record;
        // see GraphAssetUtility.RepairAll.
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            PruneMissingNodes();
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            PruneMissingNodes();
        }

        // ------------------------------------------------------------------
        //  Node lookup
        // ------------------------------------------------------------------

        /// <summary>Finds a node by id, or null if not found.</summary>
        public Generator GetNodeById(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
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
            for (int i = connections.Count - 1; i >= 0; i--)
            {
                Connection c = connections[i];
                if (c.targetNodeId == targetNodeId && c.targetPortIndex == targetPortIndex)
                    return c;
            }
            return default(Connection);
        }

        /// <summary>
        /// Finds all connections originating from the given output port. Used when
        /// the evaluator needs to invalidate downstream nodes after a parameter change.
        /// </summary>
        public List<Connection> GetOutputConnections(string sourceNodeId, int sourcePortIndex)
        {
            List<Connection> result = new List<Connection>();
            foreach (Connection c in connections)
            {
                if (c.sourceNodeId == sourceNodeId && c.sourcePortIndex == sourcePortIndex)
                    result.Add(c);
            }
            return result;
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
            nodes.Add(node);
            return node;
        }

        /// <summary>Removes a node and all connections touching it.</summary>
        public void RemoveNode(string nodeId)
        {
            // The `n == null` arm sweeps missing-type holes out at the same time -
            // free, and it stops the lambda throwing on one.
            nodes.RemoveAll(n => n == null || n.id == nodeId);
            connections.RemoveAll(c => c.sourceNodeId == nodeId || c.targetNodeId == nodeId);
        }

        /// <summary>
        /// Adds a connection. If the target input already has a connection (Module 1
        /// inputs are single-connection), the old one is removed first. Rejects
        /// connections that would create a cycle and returns false.
        /// </summary>
        public bool AddConnection(Connection conn)
        {
            if (conn.sourceNodeId == conn.targetNodeId) return false;
            if (WouldCreateCycle(conn.sourceNodeId, conn.targetNodeId)) return false;

            // Remove any existing connection to the same input
            connections.RemoveAll(c => c.targetNodeId == conn.targetNodeId &&
                                       c.targetPortIndex == conn.targetPortIndex);
            connections.Add(conn);
            return true;
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
            connections.Remove(conn);
        }

        /// <summary>Clears all nodes and connections.</summary>
        public void Clear()
        {
            nodes.Clear();
            connections.Clear();
        }

        // ------------------------------------------------------------------
        //  Validation
        // ------------------------------------------------------------------

        /// <summary>
        /// Validates the graph and returns a list of error messages. Called by the
        /// inspector and the chunking system before starting generation.
        /// </summary>
        public List<string> Validate()
        {
            List<string> errors = new List<string>();

            // Check for duplicate node ids
            HashSet<string> ids = new HashSet<string>();
            foreach (Generator n in nodes)
            {
                if (n == null)
                {
                    errors.Add("A node slot is empty because its script was deleted or " +
                               "renamed. Tools > Universe Creator > Diagnostics > " +
                               "Repair Graphs clears it.");
                    continue;
                }
                if (string.IsNullOrEmpty(n.id))
                {
                    errors.Add("Node has null or empty id.");
                    continue;
                }
                if (ids.Contains(n.id))
                    errors.Add("Duplicate node id: " + n.id);
                ids.Add(n.id);
            }

            // Check for dangling connections
            foreach (Connection c in connections)
            {
                Generator src = GetNodeById(c.sourceNodeId);
                Generator tgt = GetNodeById(c.targetNodeId);

                if (src == null)
                {
                    errors.Add("Connection references missing source node: " + c.sourceNodeId);
                    continue;
                }
                if (tgt == null)
                {
                    errors.Add("Connection references missing target node: " + c.targetNodeId);
                    continue;
                }

                Port[] srcOutputs = src.GetOutputs();
                Port[] tgtInputs = tgt.GetInputs();

                if (c.sourcePortIndex < 0 || c.sourcePortIndex >= srcOutputs.Length)
                {
                    errors.Add("Connection has out-of-range source port index: " + c);
                    continue;
                }
                if (c.targetPortIndex < 0 || c.targetPortIndex >= tgtInputs.Length)
                {
                    errors.Add("Connection has out-of-range target port index: " + c);
                    continue;
                }

                // Type check
                if (srcOutputs[c.sourcePortIndex].type != tgtInputs[c.targetPortIndex].type)
                {
                    errors.Add("Type mismatch: " + c + " (" +
                               srcOutputs[c.sourcePortIndex].type + " -> " +
                               tgtInputs[c.targetPortIndex].type + ")");
                }
            }

            // Check for at least one output node
            bool hasOutputNode = false;
            foreach (Generator n in nodes)
            {
                if (n is IOutputNode)
                {
                    hasOutputNode = true;
                    break;
                }
            }
            if (!hasOutputNode && nodes.Count > 0)
                errors.Add("Graph has no output nodes (HeightOutput, SplatOutput, etc.).");

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
