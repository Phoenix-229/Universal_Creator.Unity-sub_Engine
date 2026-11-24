// UC-T05-09: read-only migration inventories exact serialized content and all four world references.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>Read-only partition of a legacy graph into consumer domains; unresolved or ambiguous chains prevent unsafe application.</summary>
    internal sealed class GraphMigrationPlan
    {
        internal static readonly UCGraphDomain[] Domains = { UCGraphDomain.TerrainWater, UCGraphDomain.Vegetation,
            UCGraphDomain.LightingClimateWeather, UCGraphDomain.TextureMaterialShader };
        internal Graph Source { get; private set; }
        internal UniverseCreatorObject World { get; private set; }
        internal string SourceJson { get; private set; }
        internal readonly Dictionary<string, UCGraphDomain> Destinations = new Dictionary<string, UCGraphDomain>();
        // UC-T06-02: only the exact published height outlet may become accepted ground; arbitrary intermediates keep the explicit-resolution gate.
        internal readonly HashSet<Connection> AcceptedGroundConnections = new HashSet<Connection>();
        internal readonly List<string> Details = new List<string>();
        internal readonly List<string> Errors = new List<string>();
        internal bool CanApply => Errors.Count == 0;
        private UCWorldGraphBindings bindings;
        private Graph vegetation, lighting, texture;

        internal static GraphMigrationPlan Analyze(Graph source, UniverseCreatorObject world)
        {
            var plan = new GraphMigrationPlan { Source = source, World = world };
            if (source == null || source.nodes == null || source.connections == null)
            { plan.Errors.Add("Select an intact legacy graph."); return plan; }
            if (!source.HasSupportedDomainMetadata || source.Domain != UCGraphDomain.LegacyMixed)
                plan.Errors.Add("Only supported LegacyMixed assets require this migration; explicit or future domains remain unchanged.");
            if (source.nodes.Count > 2048 || source.connections.Count > 8192)
            { plan.Errors.Add("Migration analysis limit: split authoring blocks above 2,048 nodes or 8,192 wires first."); return plan; }
            if (world == null || world.graph != source) plan.Errors.Add("Choose the world whose main reference is this exact legacy asset.");
            else
            {
                plan.bindings = world.GetComponent<UCWorldGraphBindings>();
                plan.vegetation = plan.bindings != null ? plan.bindings.VegetationGraph : null;
                plan.lighting = plan.bindings != null ? plan.bindings.LightingGraph : null;
                plan.texture = plan.bindings != null ? plan.bindings.TextureGraph : null;
                plan.Details.Add("Main: " + AssetDatabase.GetAssetPath(source) + " → new TerrainWater asset.");
                plan.Details.Add("Vegetation / Lighting / Texture references: " + AssetDatabase.GetAssetPath(plan.vegetation) + " / " +
                    AssetDatabase.GetAssetPath(plan.lighting) + " / " + AssetDatabase.GetAssetPath(plan.texture) + ". Existing separate companions are retained.");
            }
            foreach (var diagnostic in GraphValidationReport.Analyze(source,
                NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu).Diagnostics)
                if (diagnostic.Severity == GraphDiagnosticSeverity.Error) plan.Errors.Add(diagnostic.Code + ": " + diagnostic.Message);
            if (source.unresolvedNodes != null) foreach (var missing in source.unresolvedNodes)
                plan.Details.Add("Preserved unresolved slot " + missing.slot + ": " + missing.type + "; restore the type before migrating its chain.");
            plan.SourceJson = GraphSerializer.ToJson(source);
            var nodes = source.nodes.Where(n => n != null && !string.IsNullOrEmpty(n.id)).GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
            var heightOutputs = nodes.Values.OfType<HeightOutputNode>().Where(n => !n.disabled).ToArray();
            if (heightOutputs.Length == 1)
            {
                var publication = source.connections.Where(c => c.targetNodeId == heightOutputs[0].id && c.targetPortIndex == 0).ToArray();
                if (publication.Length == 1)
                    foreach (var edge in source.connections)
                        if (edge.sourceNodeId == publication[0].sourceNodeId && edge.sourcePortIndex == publication[0].sourcePortIndex &&
                            edge.targetPortIndex == 0 && nodes.TryGetValue(edge.targetNodeId, out var target) && target is ScatterNodeBase)
                        {
                            plan.AcceptedGroundConnections.Add(edge);
                            plan.Details.Add("Accepted final-ground boundary: " + edge.sourceNodeId + ":" + edge.sourcePortIndex + " → " + edge.targetNodeId +
                                ":0; placement reads the published world surface after native commit, without rerunning terrain.");
                        }
            }
            var upstream = new Dictionary<string, List<Connection>>();
            foreach (var id in nodes.Keys) upstream[id] = new List<Connection>();
            foreach (var edge in source.connections)
                if (!string.IsNullOrEmpty(edge.targetNodeId) && !string.IsNullOrEmpty(edge.sourceNodeId) &&
                    upstream.TryGetValue(edge.targetNodeId, out var list) && nodes.ContainsKey(edge.sourceNodeId)) list.Add(edge);
            foreach (var node in nodes.Values)
                if (node is IPortalIn receiver && receiver.ResolveSender(source) is Generator sender && nodes.ContainsKey(sender.id))
                    upstream[node.id].Add(new Connection(sender.id, -1, node.id, -1)); // Wireless dependencies stay with the actual receiving chain.
            var uses = new Dictionary<string, HashSet<UCGraphDomain>>();
            foreach (var id in nodes.Keys) uses[id] = new HashSet<UCGraphDomain>();
            foreach (var terminal in nodes.Values.Where(n => n is IOutputNode))
            {
                var admitted = Domains.Where(d => GraphDomainRules.Supports(terminal.GraphDomains, d)).ToArray();
                if (admitted.Length != 1)
                { plan.Errors.Add("Terminal " + terminal.id + " has ambiguous consumer ownership; declare its graph domain before migration."); continue; }
                var queue = new Queue<string>(); queue.Enqueue(terminal.id);
                while (queue.Count > 0)
                {
                    string id = queue.Dequeue(); if (!uses[id].Add(admitted[0])) continue;
                    foreach (var edge in upstream[id])
                        if (admitted[0] != UCGraphDomain.Vegetation || !plan.AcceptedGroundConnections.Contains(edge)) queue.Enqueue(edge.sourceNodeId);
                }
            }
            foreach (var node in nodes.Values)
            {
                var destinations = uses[node.id];
                if (destinations.Count > 1)
                {
                    plan.Errors.Add("Shared node " + node.id + " (" + node.NodeName + ") feeds " + string.Join(", ", destinations.OrderBy(d => d)) +
                        ". Introduce an explicit accepted World Graph Input at this boundary; arbitrary intermediates cannot be silently duplicated or treated as final ground.");
                    continue;
                }
                UCGraphDomain destination = destinations.Count == 1 ? destinations.First() :
                    Domains.FirstOrDefault(d => GraphDomainRules.Supports(node.GraphDomains, d));
                if (destination == UCGraphDomain.LegacyMixed || !GraphDomainRules.Supports(node.GraphDomains, destination))
                { plan.Errors.Add("Node " + node.id + " cannot execute in its consumer's domain."); continue; }
                plan.Destinations[node.id] = destination;
                plan.Details.Add(node.id + " | " + node.NodeName + " → " + destination + "; serialized settings, seed, prototype references and layout retained.");
                if (node is WorldGraphInputNode input)
                {
                    plan.Details.Add("Shared accepted input " + node.id + ": " + (input.mainGraph == null ? "bound world main" : AssetDatabase.GetAssetPath(input.mainGraph)) + ".");
                    if (input.mainGraph != null && input.mainGraph != source)
                        plan.Errors.Add("World Graph Input " + node.id + " selects a foreign main graph.");
                }
            }
            foreach (var domain in Domains)
            {
                if (domain == UCGraphDomain.Vegetation && plan.AcceptedGroundConnections.Count > 0 && plan.Destinations.Count(e => e.Value == domain) >= 2048)
                    plan.Errors.Add("Accepted-ground migration needs one input node; split this 2,048-node vegetation block first.");
                plan.Details.Add("Replacement " + domain + ".asset: " + plan.Destinations.Count(e => e.Value == domain) + " nodes; stable IDs and internal wire order retained.");
                if (domain != UCGraphDomain.TerrainWater && plan.Destinations.Any(e => e.Value == domain) && !plan.ReplacesCompanion(domain))
                    plan.Errors.Add("The world already has a separate " + domain + " companion and this legacy asset also supplies that consumer. Resolve the merge explicitly before switching references.");
            }
            plan.Details.Add("Original legacy asset remains available for Undo/recovery; analysis creates no asset or world run.");
            return plan;
        }

        internal bool IsCurrent()
        {
            if (!CanApply || Source == null || World == null || World.graph != Source || GraphSerializer.ToJson(Source) != SourceJson) return false;
            var current = World.GetComponent<UCWorldGraphBindings>();
            return current == bindings && (current == null || (current.VegetationGraph == vegetation && current.LightingGraph == lighting && current.TextureGraph == texture));
        }

        internal bool ReplacesCompanion(UCGraphDomain domain) => domain == UCGraphDomain.Vegetation ? vegetation == null :
            domain == UCGraphDomain.LightingClimateWeather ? lighting == null : domain == UCGraphDomain.TextureMaterialShader && texture == null;
    }
}
