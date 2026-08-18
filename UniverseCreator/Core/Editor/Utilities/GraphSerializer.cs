// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GraphSerializer.cs
//  Purpose: Portable import/export of graphs as .ucgraph text files, plus node-id
//           remapping so a duplicated subgraph does not collide with the original.
//
//  WHY A SEPARATE FORMAT
//  ---------------------
//  GraphAssetUtility handles the in-project case: graphs live as .asset files
//  because Unity will not import a custom extension as a ScriptableObject without
//  a ScriptedImporter. That works inside one project and nowhere else - an .asset
//  carries binary type GUIDs, so dropping one into a different project gives a
//  broken script reference.
//
//  .ucgraph is the transport format: plain JSON, human-readable, diffable in git,
//  and resolved by type NAME rather than GUID at import time. That is what makes a
//  graph shareable between projects and reviewable in a pull request.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Reads and writes graphs as .ucgraph JSON, and remaps node ids when a
    /// selection is duplicated.
    /// </summary>
    public static class GraphSerializer
    {
        public const string Extension = "ucgraph";

        // Bump when the on-disk shape changes so an old file can be detected
        // rather than half-parsed into a broken graph.
        public const int FormatVersion = 1;

        // ---------------------------------------------------------------------
        //  DTOs. Unity's JsonUtility cannot serialise [SerializeReference]
        //  polymorphic lists, so each node is written as an explicit
        //  (type name, json payload) pair and reconstructed by name on import.
        // ---------------------------------------------------------------------

        [Serializable]
        private class NodeDTO
        {
            public string type;   // assembly-qualified-free type name, e.g. UniverseCreator.Nodes.Creators.PerlinNoiseNode
            public string json;   // EditorJsonUtility payload for that node instance
        }

        [Serializable]
        private class ConnectionDTO
        {
            public string sourceNodeId;
            public int sourcePort;
            public string targetNodeId;
            public int targetPort;
        }

        [Serializable]
        private class GraphDTO
        {
            public int version = FormatVersion;
            public string graphName;
            public List<NodeDTO> nodes = new List<NodeDTO>();
            public List<ConnectionDTO> connections = new List<ConnectionDTO>();
        }

        // =====================================================================
        //  EXPORT
        // =====================================================================

        /// <summary>
        /// Serialises a graph to .ucgraph JSON text. Returns null if there is
        /// nothing to write.
        /// </summary>
        public static string ToJson(Graph graph)
        {
            if (graph == null) return null;
            return Write(graph.name, graph.nodes, graph.connections);
        }

        /// <summary>
        /// Serialises just the given nodes, plus the connections that run between two
        /// of them. Deliberately the same format as a whole-graph export, so a copied
        /// selection pastes into any graph window - and an exported .ucgraph pastes
        /// back as a selection.
        ///
        /// Connections crossing the selection boundary are left out on purpose: a
        /// pasted node cannot share its source's single input slot without silently
        /// stealing or duplicating upstream data, and guessing which the user meant is
        /// worse than leaving the inlet open.
        /// </summary>
        public static string SelectionToJson(Graph graph, IList<Generator> selection)
        {
            if (graph == null || selection == null || selection.Count == 0) return null;

            HashSet<string> inside = new HashSet<string>();
            for (int i = 0; i < selection.Count; i++)
            {
                Generator node = selection[i];
                if (node != null && !string.IsNullOrEmpty(node.id)) inside.Add(node.id);
            }

            List<Connection> internalLinks = new List<Connection>();
            for (int i = 0; i < graph.connections.Count; i++)
            {
                Connection c = graph.connections[i];
                if (inside.Contains(c.sourceNodeId) && inside.Contains(c.targetNodeId))
                    internalLinks.Add(c);
            }

            return Write(graph.name, selection, internalLinks);
        }

        // The one place a graph or a selection becomes .ucgraph text: ToJson hands over
        // the whole graph, SelectionToJson a subset. A second writer would be a second
        // format to keep in step with the single reader below.
        private static string Write(string graphName, IList<Generator> nodes,
                                    IList<Connection> connections)
        {
            GraphDTO dto = new GraphDTO();
            dto.graphName = graphName;

            for (int i = 0; i < nodes.Count; i++)
            {
                Generator node = nodes[i];
                if (node == null) continue;   // a removed script leaves a null hole in the list

                NodeDTO n = new NodeDTO();
                n.type = node.GetType().FullName;
                n.json = EditorJsonUtility.ToJson(node, false);
                dto.nodes.Add(n);
            }

            for (int i = 0; i < connections.Count; i++)
            {
                Connection c = connections[i];
                if (string.IsNullOrEmpty(c.sourceNodeId)) continue;

                ConnectionDTO cd = new ConnectionDTO();
                cd.sourceNodeId = c.sourceNodeId;
                cd.sourcePort = c.sourcePortIndex;
                cd.targetNodeId = c.targetNodeId;
                cd.targetPort = c.targetPortIndex;
                dto.connections.Add(cd);
            }

            return EditorJsonUtility.ToJson(dto, true);
        }

        /// <summary>
        /// Writes a graph to an absolute file path. Returns false and logs on
        /// failure rather than throwing, because this runs off a menu click.
        /// </summary>
        public static bool ExportToFile(Graph graph, string absolutePath)
        {
            string json = ToJson(graph);
            if (json == null || string.IsNullOrEmpty(absolutePath)) return false;

            try
            {
                File.WriteAllText(absolutePath, json, Encoding.UTF8);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("Universe Creator: could not write " + absolutePath + " - " + e.Message);
                return false;
            }
        }

        /// <summary>Prompts for a location and exports. Returns the chosen path, or null if cancelled.</summary>
        public static string ExportWithDialog(Graph graph)
        {
            if (graph == null) return null;

            string path = EditorUtility.SaveFilePanel("Export Graph", "",
                                                      graph.name + "." + Extension, Extension);
            if (string.IsNullOrEmpty(path)) return null;

            return ExportToFile(graph, path) ? path : null;
        }

        // =====================================================================
        //  IMPORT
        // =====================================================================

        // The one place .ucgraph text becomes DTOs, shared by import and paste.
        //
        // `logErrors` is off for the clipboard probe: IsPasteableJson runs on every
        // Ctrl+V and on every context-menu build, and the copy buffer normally holds
        // ordinary text, so logging there would be console spam rather than a
        // diagnostic. Returns null when the text is not a graph payload at all.
        private static GraphDTO ReadDto(string json, bool logErrors)
        {
            if (string.IsNullOrEmpty(json)) return null;

            GraphDTO dto = new GraphDTO();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(json, dto);
            }
            catch (Exception e)
            {
                if (logErrors)
                    Debug.LogError("Universe Creator: .ucgraph is not valid JSON - " + e.Message);
                return null;
            }

            if (dto.version > FormatVersion && logErrors)
            {
                Debug.LogWarning("Universe Creator: .ucgraph was written by a newer version (" +
                                 dto.version + " > " + FormatVersion + "). Importing anyway; " +
                                 "unknown fields will be ignored.");
            }

            return dto;
        }

        /// <summary>
        /// Rebuilds a graph from .ucgraph JSON as an unsaved in-memory asset.
        /// Nodes whose type no longer exists are skipped with a warning, and any
        /// connection touching a skipped node is dropped - a dangling connection
        /// would otherwise crash evaluation on the first pull.
        /// </summary>
        public static Graph FromJson(string json)
        {
            GraphDTO dto = ReadDto(json, true);
            if (dto == null) return null;

            Graph graph = ScriptableObject.CreateInstance<Graph>();
            graph.name = string.IsNullOrEmpty(dto.graphName) ? "Imported Graph" : dto.graphName;

            HashSet<string> liveIds = new HashSet<string>();

            for (int i = 0; i < dto.nodes.Count; i++)
            {
                NodeDTO n = dto.nodes[i];
                Generator node = Instantiate(n);
                if (node == null) continue;

                graph.nodes.Add(node);
                if (!string.IsNullOrEmpty(node.id)) liveIds.Add(node.id);
            }

            for (int i = 0; i < dto.connections.Count; i++)
            {
                ConnectionDTO cd = dto.connections[i];
                if (!liveIds.Contains(cd.sourceNodeId) || !liveIds.Contains(cd.targetNodeId)) continue;

                graph.AddConnection(new Connection(cd.sourceNodeId, cd.sourcePort,
                                                   cd.targetNodeId, cd.targetPort));
            }

            return graph;
        }

        /// <summary>Reads a .ucgraph from disk. Returns null on any failure.</summary>
        public static Graph ImportFromFile(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return null;

            try
            {
                return FromJson(File.ReadAllText(absolutePath, Encoding.UTF8));
            }
            catch (Exception e)
            {
                Debug.LogError("Universe Creator: could not read " + absolutePath + " - " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Prompts for a .ucgraph, imports it, and saves it as a project .asset so
        /// it survives the session. Returns the saved asset or null if cancelled.
        /// </summary>
        public static Graph ImportWithDialog()
        {
            string path = EditorUtility.OpenFilePanel("Import Graph", "", Extension);
            if (string.IsNullOrEmpty(path)) return null;

            Graph imported = ImportFromFile(path);
            if (imported == null) return null;

            return GraphAssetUtility.SaveAsset(imported, GraphAssetUtility.DefaultFolder, imported.name);
        }

        // =====================================================================
        //  CLIPBOARD  (paste / duplicate)
        // =====================================================================

        /// <summary>
        /// True if the text is a .ucgraph payload holding at least one node. Drives
        /// the Paste command's enabled state, so it has to be cheap and must never
        /// log - the copy buffer usually holds something else entirely.
        /// </summary>
        public static bool IsPasteableJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            // Cheap reject first. Parsing every clipboard string on every menu build
            // would be work spent on ordinary copied text.
            if (json.IndexOf("\"nodes\"", StringComparison.Ordinal) < 0) return false;

            GraphDTO dto = ReadDto(json, false);
            return dto != null && dto.nodes.Count > 0;
        }

        /// <summary>
        /// Pastes .ucgraph text into an existing graph as a fresh, independent set of
        /// nodes, and returns them in payload order.
        ///
        /// Positions come straight from the payload; where the block finally lands is
        /// the caller's call, because only the view knows where the pointer is.
        /// </summary>
        public static List<Generator> PasteJson(Graph graph, string json)
        {
            List<Generator> pasted = new List<Generator>();
            if (graph == null) return pasted;

            GraphDTO dto = ReadDto(json, true);
            if (dto == null) return pasted;

            List<Generator> incoming = new List<Generator>();
            for (int i = 0; i < dto.nodes.Count; i++)
            {
                Generator node = Instantiate(dto.nodes[i]);
                if (node != null) incoming.Add(node);
            }

            List<Connection> links = new List<Connection>();
            for (int i = 0; i < dto.connections.Count; i++)
            {
                ConnectionDTO cd = dto.connections[i];
                links.Add(new Connection(cd.sourceNodeId, cd.sourcePort,
                                         cd.targetNodeId, cd.targetPort));
            }

            return Adopt(graph, incoming, links);
        }

        // Takes nodes that are already independent instances - freshly deserialised,
        // so nothing is shared with whatever they were copied from - and makes them
        // members of `graph` under fresh ids, rewiring the connections that ran
        // between two of them.
        //
        // A fresh id is what makes this a paste rather than a second reference to the
        // same node: Graph keys everything by id, so reusing the payload's ids would
        // make the copy indistinguishable from the original.
        private static List<Generator> Adopt(Graph graph, IList<Generator> incoming,
                                             IList<Connection> links)
        {
            List<Generator> adopted = new List<Generator>();
            if (graph == null || incoming == null || incoming.Count == 0) return adopted;

            // old id -> new id, so the connection pass can find the copies.
            Dictionary<string, string> idMap = new Dictionary<string, string>();

            for (int i = 0; i < incoming.Count; i++)
            {
                Generator node = incoming[i];
                if (node == null) continue;

                string oldId = node.id;
                node.id = Guid.NewGuid().ToString();

                if (!string.IsNullOrEmpty(oldId) && !idMap.ContainsKey(oldId))
                    idMap[oldId] = node.id;

                graph.AddNode(node);
                adopted.Add(node);
            }

            // Portals pair by channel name, so a plain copy would keep the source
            // channel and collide with the original (both PortalIns then pull the same
            // feed - the "terrain goes flat" bug). Remap the pasted portals' channels
            // as a group: a copied In+Out pair stays paired, but on a fresh channel
            // unused elsewhere.
            RechannelDuplicatedPortals(graph, adopted);

            if (links != null)
            {
                for (int i = 0; i < links.Count; i++)
                {
                    Connection c = links[i];
                    if (string.IsNullOrEmpty(c.sourceNodeId) ||
                        string.IsNullOrEmpty(c.targetNodeId)) continue;

                    string newSource, newTarget;
                    if (!idMap.TryGetValue(c.sourceNodeId, out newSource)) continue;
                    if (!idMap.TryGetValue(c.targetNodeId, out newTarget)) continue;

                    graph.AddConnection(new Connection(newSource, c.sourcePortIndex,
                                                       newTarget, c.targetPortIndex));
                }
            }

            GraphAssetUtility.MarkDirty(graph);
            return adopted;
        }

        // Give each distinct portal channel among the copies a fresh channel that
        // no portal elsewhere in the graph uses, then apply that mapping to every
        // copy. Copied pairs share one old channel, so they map to one new channel
        // and stay paired; a lone copied end just gets its own unused channel.
        static void RechannelDuplicatedPortals(Graph graph, List<Generator> copies)
        {
            if (graph == null || copies == null || copies.Count == 0) return;

            // Channels in use by portals that are NOT among the copies.
            HashSet<string> used = new HashSet<string>();
            foreach (Generator g in graph.nodes)
            {
                if (copies.Contains(g)) continue;
                string ch = PortalChannelOf(g);
                if (!string.IsNullOrEmpty(ch)) used.Add(ch);
            }

            Dictionary<string, string> remap = new Dictionary<string, string>();
            int n = 1;
            foreach (Generator g in copies)
            {
                string old = PortalChannelOf(g);
                if (string.IsNullOrEmpty(old)) continue;

                string fresh;
                if (!remap.TryGetValue(old, out fresh))
                {
                    string candidate = "Channel " + n;
                    while (used.Contains(candidate)) { n++; candidate = "Channel " + n; }
                    used.Add(candidate);
                    remap[old] = candidate;
                    fresh = candidate;
                    n++;
                }
                SetPortalChannel(g, fresh);
            }
        }

        static string PortalChannelOf(Generator g)
        {
            UniverseCreator.Nodes.Utilities.PortalInNode pin = g as UniverseCreator.Nodes.Utilities.PortalInNode;
            if (pin != null) return pin.channel;
            UniverseCreator.Nodes.Utilities.PortalOutNode pout = g as UniverseCreator.Nodes.Utilities.PortalOutNode;
            if (pout != null) return pout.channel;
            return null;
        }

        static void SetPortalChannel(Generator g, string value)
        {
            UniverseCreator.Nodes.Utilities.PortalInNode pin = g as UniverseCreator.Nodes.Utilities.PortalInNode;
            if (pin != null) { pin.channel = value; return; }
            UniverseCreator.Nodes.Utilities.PortalOutNode pout = g as UniverseCreator.Nodes.Utilities.PortalOutNode;
            if (pout != null) pout.channel = value;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        // Resolves a type name across the loaded assemblies and rehydrates it.
        // Type.GetType alone only searches the calling assembly plus mscorlib, so
        // it misses every node type defined in the runtime assembly.
        private static Generator Instantiate(NodeDTO dto)
        {
            if (dto == null || string.IsNullOrEmpty(dto.type)) return null;

            Type type = ResolveType(dto.type);
            if (type == null)
            {
                Debug.LogWarning("Universe Creator: node type '" + dto.type +
                                 "' no longer exists; skipping it and any connection that used it.");
                return null;
            }

            Generator node = Activator.CreateInstance(type) as Generator;
            if (node == null) return null;

            if (!string.IsNullOrEmpty(dto.json))
                EditorJsonUtility.FromJsonOverwrite(dto.json, node);

            // A file hand-edited to drop an id, or two files merged, would leave
            // collisions the graph cannot key on. Mint one rather than refuse.
            if (string.IsNullOrEmpty(node.id)) node.id = Guid.NewGuid().ToString();

            return node;
        }

        private static Type ResolveType(string fullName)
        {
            Type direct = Type.GetType(fullName);
            if (direct != null) return direct;

            System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType(fullName);
                if (t != null && typeof(Generator).IsAssignableFrom(t)) return t;
            }
            return null;
        }
    }
}
