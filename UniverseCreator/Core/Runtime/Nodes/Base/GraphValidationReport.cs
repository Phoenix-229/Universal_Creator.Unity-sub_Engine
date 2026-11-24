using System;
using System.Collections.Generic;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T01-09: severity of a graph diagnostic; errors prevent snapshot publication.</summary>
    public enum GraphDiagnosticSeverity
    {
        /// <summary>Supported fallback or incomplete authoring that does not block this executor.</summary>
        Warning,
        /// <summary>Invalid topology or unsupported execution; no result may be published.</summary>
        Error
    }

    /// <summary>UC-T01-09: immutable, actionable diagnostic tied to an authored node/port when available.</summary>
    public sealed class GraphDiagnostic
    {
        /// <summary>Stable diagnostic identifier for tooling and regression checks.</summary>
        public readonly string Code;
        /// <summary>Authored node reference; null for a missing slot or graph-wide problem.</summary>
        public readonly Generator Node;
        /// <summary>Serialized node ID, including the missing endpoint ID when available.</summary>
        public readonly string NodeId;
        /// <summary>Zero-based authored slot; -1 when no existing node can be located.</summary>
        public readonly int NodeSlot;
        /// <summary>Zero-based affected port; -1 for a node-wide problem.</summary>
        public readonly int PortIndex;
        /// <summary>Affected socket direction; null for a node-wide problem.</summary>
        public readonly PortDirection? Direction;
        /// <summary>Errors reject execution; warnings describe supported fallback behavior.</summary>
        public readonly GraphDiagnosticSeverity Severity;
        /// <summary>Cause and suggested correction suitable for displaying on the canvas.</summary>
        public readonly string Message;

        internal GraphDiagnostic(string code, Generator node, string id, int slot, int port,
                                 PortDirection? direction, string message, GraphDiagnosticSeverity severity)
        {
            Code = code; Node = node; NodeId = id; NodeSlot = slot; PortIndex = port;
            Direction = direction; Message = message; Severity = severity;
        }
    }

    /// <summary>UC-T01-09: validates authored topology and execution domains without generating, repairing or hashing it.</summary>
    /// <remarks>Run once on the main thread before capture, or after an Editor edit. Collections are read-only afterward.</remarks>
    public sealed class GraphValidationReport
    {
        private readonly List<GraphDiagnostic> entries = new List<GraphDiagnostic>();
        private readonly Dictionary<Generator, int> slots = new Dictionary<Generator, int>();
        private readonly Dictionary<Generator, Port[]> inputs = new Dictionary<Generator, Port[]>();
        private readonly Dictionary<Generator, Port[]> outputs = new Dictionary<Generator, Port[]>();
        private readonly Dictionary<string, Generator> byId = new Dictionary<string, Generator>(StringComparer.Ordinal);
        private readonly Dictionary<Generator, List<Generator>> forward = new Dictionary<Generator, List<Generator>>();
        private readonly Dictionary<Generator, List<Generator>> reverse = new Dictionary<Generator, List<Generator>>();
        private readonly Dictionary<Generator, Connection> portalInputs = new Dictionary<Generator, Connection>();
        private readonly Dictionary<Generator, Generator> senders = new Dictionary<Generator, Generator>();
        // UC-T01-09: cycles follow effective single-inlet wires; type resolution never trusts stale portal UI caches.
        private readonly Dictionary<Generator, Dictionary<int, Connection>> lastInputs = new Dictionary<Generator, Dictionary<int, Connection>>();
        private readonly Dictionary<Generator, PortType> portalTypes = new Dictionary<Generator, PortType>();
        private readonly HashSet<Generator> typeVisited = new HashSet<Generator>();
        private readonly List<Generator> typePath = new List<Generator>();

        /// <summary>Ordered, immutable diagnostics; graph/node order is preserved within each validation phase.</summary>
        public IReadOnlyList<GraphDiagnostic> Diagnostics { get; private set; }
        /// <summary>True when no error prevents execution; supported portal fallback warnings do not reject it.</summary>
        public bool IsValid { get; private set; } = true;

        private GraphValidationReport() { }

        /// <summary>Checks containers, IDs, ports, types, supported domains, output roots and explicit/wireless cycles.</summary>
        /// <param name="graph">Authored graph; absent containers produce diagnostics without mutation.</param>
        /// <param name="allowedDomains">Bit mask of domains admitted by this executor; terrain workers admit Worker only.</param>
        /// <param name="requireOutput">True for complete world/material execution; false for explicit single-node sampling.</param>
        /// <returns>Report containing actionable errors; analysis never publishes terrain or changes authored fields.</returns>
        /// <remarks>Disconnected nodes are checked too. Newest single-inlet edges and first enabled same-family senders retain their existing semantics.</remarks>
        public static GraphValidationReport Analyze(Graph graph,
            NodeExecutionDomain allowedDomains = NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu,
            bool requireOutput = true)
        {
            var report = new GraphValidationReport();
            if (graph == null || graph.nodes == null || graph.connections == null)
                report.Add("CONTAINERS", null, "Restore the graph's missing node/connection containers before execution.");
            else
                report.Inspect(graph, allowedDomains, requireOutput);
            report.Diagnostics = report.entries.AsReadOnly();
            return report;
        }

        /// <summary>Refuses an invalid plan before workers or products are created.</summary>
        /// <exception cref="InvalidOperationException">The report has errors; the exception includes their actionable messages.</exception>
        public void ThrowIfInvalid()
        {
            if (IsValid) return;
            var messages = new List<string>();
            foreach (GraphDiagnostic entry in entries)
                if (entry.Severity == GraphDiagnosticSeverity.Error) messages.Add(entry.Message);
            throw new InvalidOperationException("Graph execution rejected: " + string.Join("\n", messages));
        }

        private void Add(string code, Generator node, string message, int port = -1,
                         PortDirection? direction = null, string missingId = null,
                         GraphDiagnosticSeverity severity = GraphDiagnosticSeverity.Error, int missingSlot = -1)
        {
            int slot;
            if (node == null || !slots.TryGetValue(node, out slot)) slot = missingSlot;
            entries.Add(new GraphDiagnostic(code, node, node != null ? node.id : missingId, slot,
                                            port, direction, message, severity));
            if (severity == GraphDiagnosticSeverity.Error) IsValid = false;
        }

        private void Inspect(Graph graph, NodeExecutionDomain allowed, bool requireOutput)
        {
            if (!graph.HasSupportedDomainMetadata) Add("GRAPH_DOMAIN", null, graph.DomainDiagnostic); // UC-T05-01: reject unknown metadata before capture or publication without discarding it.
            bool hasOutput = false;
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null)
                {
                    Add("MISSING_TYPE", null, "A node slot is empty (slot " + i + ") because its script is unavailable. Restore its original script/plugin or preview scoped graph repair.", missingSlot: i);
                    continue;
                }
                if (slots.ContainsKey(node))
                {
                    Add("DUPLICATE_NODE", node, "The same node occupies multiple slots. Remove the extra slot through graph editing.");
                    continue;
                }
                slots.Add(node, i);
                string settingsDiagnostic = node.SettingsDiagnostic;
                if (!string.IsNullOrEmpty(settingsDiagnostic)) Add("NODE_SETTINGS", node, settingsDiagnostic); // UC-T07-07: unsupported shader/function settings appear on their actual graph node.
                if (graph.HasSupportedDomainMetadata && !GraphDomainRules.Supports(node.GraphDomains, graph.Domain))
                    Add("NODE_GRAPH_DOMAIN", node, node.NodeName + " is outside " + graph.Domain + ". Preserve it and preview graph migration before changing world bindings."); // UC-T05-03: scripts and drag/drop cannot bypass palette admission.
                forward.Add(node, new List<Generator>()); reverse.Add(node, new List<Generator>());
                lastInputs.Add(node, new Dictionary<int, Connection>());
                if (string.IsNullOrEmpty(node.id)) Add("EMPTY_ID", node, "Node has null or empty id. Restore a unique node ID before execution.");
                else if (byId.ContainsKey(node.id))
                {
                    Add("DUPLICATE_ID", node, "Duplicate node id: " + node.id + ". Restore unique IDs and reconnect the affected nodes.");
                    Add("DUPLICATE_ID", byId[node.id], "Duplicate node id: " + node.id + ". Restore unique IDs and reconnect the affected nodes.");
                }
                else byId.Add(node.id, node);
                NodeExecutionDomain domain = node.ExecutionDomain;
                if ((domain != NodeExecutionDomain.Worker && domain != NodeExecutionDomain.MainThread && domain != NodeExecutionDomain.MainThreadGpu) || (allowed & domain) != domain)
                    Add("DOMAIN", node, node.NodeName + " requires " + domain + " execution. Use an executor supporting that domain before generating.");
                if (node is IOutputNode && !node.disabled) hasOutput = true;
                ReadPorts(node, true); ReadPorts(node, false);
            }
            if (requireOutput && !hasOutput) Add("NO_OUTPUT", null, "Graph has no enabled output nodes. Add or enable the appropriate output before execution.");
            // Recovery records remain data, even if a malformed import put a live node in their former slot.
            if (graph.unresolvedNodes != null && graph.unresolvedNodes.Count > 0)
                Add("UNRESOLVED_PAYLOAD", null, "Restore unresolved node payloads before execution; preserve their original recovery data.");

            foreach (Connection edge in graph.connections)
            {
                Generator source, target;
                byId.TryGetValue(edge.sourceNodeId ?? string.Empty, out source);
                byId.TryGetValue(edge.targetNodeId ?? string.Empty, out target);
                if (source == null) Add("MISSING_SOURCE", target, "Connection references missing source node: " + edge.sourceNodeId + ". Restore or reconnect it.", edge.targetPortIndex, PortDirection.Input, edge.sourceNodeId);
                if (target == null) Add("MISSING_TARGET", source, "Connection references missing target node: " + edge.targetNodeId + ". Restore or reconnect it.", edge.sourcePortIndex, PortDirection.Output, edge.targetNodeId);
                if (source == null || target == null) continue;
                bool sourceValid = CheckIndex(source, edge.sourcePortIndex, false);
                bool targetValid = CheckIndex(target, edge.targetPortIndex, true);
                if (!sourceValid || !targetValid) continue;
                lastInputs[target][edge.targetPortIndex] = edge;
                if (target is IPortalOut && edge.targetPortIndex == 0) portalInputs[target] = edge;
            }
            foreach (Generator node in graph.nodes)
            {
                IPortalIn receiver = node as IPortalIn;
                if (receiver == null || node.disabled) continue;
                Generator sender = receiver.ResolveSender(graph);
                if (sender != null && forward.ContainsKey(sender)) { senders[node] = sender; Link(sender, node); }
                else Add("PORTAL_EMPTY", node, "No enabled sender matches this portal family/channel; execution uses its typed blank. Connect a matching sender for data.",
                         severity: GraphDiagnosticSeverity.Warning);
            }
            foreach (Connection edge in graph.connections)
            {
                Generator source, target;
                if (!byId.TryGetValue(edge.sourceNodeId ?? string.Empty, out source) ||
                    !byId.TryGetValue(edge.targetNodeId ?? string.Empty, out target) ||
                    !HasIndex(source, edge.sourcePortIndex, false) || !HasIndex(target, edge.targetPortIndex, true)) continue;
                if (inputs[target][edge.targetPortIndex].allowMultiple || lastInputs[target][edge.targetPortIndex].Equals(edge))
                    Link(source, target);
                PortType from = EffectiveType(source, edge.sourcePortIndex, false);
                PortType to = EffectiveType(target, edge.targetPortIndex, true);
                if (from != to)
                    Add("TYPE", target, "Type mismatch: " + from + " -> " + to + ". Reconnect compatible ports.", edge.targetPortIndex, PortDirection.Input);
            }
            if (graph.Domain == UCGraphDomain.Vegetation)
                foreach (Generator producer in forward.Keys)
                    foreach (Generator consumer in forward.Keys)
                        if (CompiledGraphPlan.IsCanopyDependency(producer, consumer)) Link(producer, consumer); // UC-T06-05: canopy feedback must be rejected before worker execution.
            InspectCycles();
        }

        private void ReadPorts(Generator node, bool input)
        {
            Port[] ports;
            try { ports = input ? node.GetInputs() : node.GetOutputs(); }
            catch (Exception exception)
            {
                Add("PORT_DECLARATION", node, "Cannot read node ports: " + exception.Message + ". Correct the node's port declaration.");
                ports = null;
            }
            if (ports == null) Add("PORT_DECLARATION", node, "Node returned absent port metadata. Declare an empty array when it has no ports.");
            (input ? inputs : outputs)[node] = ports ?? Array.Empty<Port>();
            if (ports == null) return;
            for (int i = 0; i < ports.Length; i++)
                if (!Enum.IsDefined(typeof(PortType), ports[i].type) || ports[i].direction != (input ? PortDirection.Input : PortDirection.Output))
                    Add("PORT_DECLARATION", node, "Port metadata has an unsupported type/direction. Correct its declaration.", i, input ? PortDirection.Input : PortDirection.Output);
        }

        private bool HasIndex(Generator node, int index, bool input)
        {
            Port[] ports;
            return (input ? inputs : outputs).TryGetValue(node, out ports) && index >= 0 && index < ports.Length;
        }

        private bool CheckIndex(Generator node, int index, bool input)
        {
            if (HasIndex(node, index, input)) return true;
            Add("PORT_INDEX", node, "Connection has out-of-range " + (input ? "target" : "source") + " port index " + index + ". Reconnect an existing socket.",
                index, input ? PortDirection.Input : PortDirection.Output);
            return false;
        }

        // UC-T01-09: memoize portal chains using current wires and the shipped families' empty-channel defaults.
        // Unknown plugin portals retain their declared fallback; no constructor or mutable ResolveType hook runs here.
        private PortType EffectiveType(Generator node, int index, bool input)
        {
            PortType resolved = (input ? inputs : outputs)[node][index].type;
            typeVisited.Clear(); typePath.Clear();
            while (typeVisited.Add(node))
            {
                if (!(node is IPortalTyped)) { resolved = (input ? inputs : outputs)[node][index].type; break; }
                if (portalTypes.TryGetValue(node, out resolved)) break;
                typePath.Add(node);
                resolved = node is Utilities.PortalInNode || node is Utilities.PortalOutNode ? PortType.Matrix
                    : node is Texturing.TexturePortalInNode || node is Texturing.TexturePortalOutNode ? PortType.Layer
                    : (input ? inputs : outputs)[node][index].type;
                Generator sender;
                Connection edge;
                if (node is IPortalIn && senders.TryGetValue(node, out sender) && HasIndex(sender, 0, true))
                {
                    node = sender; index = 0; input = true; continue;
                }
                if (node is IPortalOut && portalInputs.TryGetValue(node, out edge) &&
                    byId.TryGetValue(edge.sourceNodeId, out sender) && HasIndex(sender, edge.sourcePortIndex, false))
                {
                    node = sender; index = edge.sourcePortIndex; input = false; continue;
                }
                break;
            }
            foreach (Generator portal in typePath) portalTypes[portal] = resolved;
            return resolved; // The cycle phase reports the offending component separately.
        }

        private void Link(Generator source, Generator target)
        {
            forward[source].Add(target); reverse[target].Add(source);
        }

        private struct Visit
        {
            internal Generator node;
            internal int next;
            internal Visit(Generator node, int next = 0) { this.node = node; this.next = next; }
        }

        // Iterative SCC detection avoids recursion/stack overflow on imported deep graphs and labels only cycle members.
        private void InspectCycles()
        {
            var visited = new HashSet<Generator>();
            var order = new List<Generator>();
            var stack = new Stack<Visit>();
            foreach (Generator root in forward.Keys)
            {
                if (!visited.Add(root)) continue;
                stack.Push(new Visit(root));
                while (stack.Count > 0)
                {
                    Visit current = stack.Pop();
                    if (current.next == forward[current.node].Count) { order.Add(current.node); continue; }
                    Generator child = forward[current.node][current.next];
                    stack.Push(new Visit(current.node, current.next + 1));
                    if (visited.Add(child)) stack.Push(new Visit(child));
                }
            }
            visited.Clear();
            var pending = new Stack<Generator>();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                Generator root = order[i];
                if (!visited.Add(root)) continue;
                var component = new List<Generator>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    Generator node = pending.Pop(); component.Add(node);
                    foreach (Generator parent in reverse[node]) if (visited.Add(parent)) pending.Push(parent);
                }
                if (component.Count == 1 && !forward[root].Contains(root)) continue;
                foreach (Generator node in component)
                    Add("CYCLE", node, "This node participates in an explicit, portal or vegetation-canopy dependency cycle. Disconnect an edge or change the portal/habitat family to break it."); // UC-T06-05
            }
        }
    }
}
