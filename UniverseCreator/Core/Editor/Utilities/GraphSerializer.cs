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

            GraphDTO dto = new GraphDTO();
            dto.graphName = graph.name;

            for (int i = 0; i < graph.nodes.Count; i++)
            {
                Generator node = graph.nodes[i];
                if (node == null) continue;   // a removed script leaves a null hole in the list

                NodeDTO n = new NodeDTO();
                n.type = node.GetType().FullName;
                n.json = EditorJsonUtility.ToJson(node, false);
                dto.nodes.Add(n);
            }

            for (int i = 0; i < graph.connections.Count; i++)
            {
                Connection c = graph.connections[i];
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

        /// <summary>
        /// Rebuilds a graph from .ucgraph JSON as an unsaved in-memory asset.
        /// Nodes whose type no longer exists are skipped with a warning, and any
        /// connection touching a skipped node is dropped - a dangling connection
        /// would otherwise crash evaluation on the first pull.
        /// </summary>
        public static Graph FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            GraphDTO dto = new GraphDTO();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(json, dto);
            }
            catch (Exception e)
            {
                Debug.LogError("Universe Creator: .ucgraph is not valid JSON - " + e.Message);
                return null;
            }

            if (dto.version > FormatVersion)
            {
                Debug.LogWarning("Universe Creator: .ucgraph was written by a newer version (" +
                                 dto.version + " > " + FormatVersion + "). Importing anyway; " +
                                 "unknown fields will be ignored.");
            }

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
        //  DUPLICATE / ID REMAPPING
        // =====================================================================

        /// <summary>
        /// Deep-copies a set of nodes into the same graph with fresh ids, and
        /// rewires connections that ran between two copied nodes so the copy is
        /// independently functional.
        ///
        /// Connections crossing the selection boundary are deliberately NOT
        /// copied: a duplicated node cannot share its source's single input slot
        /// without silently stealing or duplicating upstream data, and guessing
        /// which the user meant is worse than leaving the inlet open.
        ///
        /// Returns the new nodes, in the same order as the input.
        /// </summary>
        public static List<Generator> DuplicateNodes(Graph graph, IList<Generator> source,
                                                     Vector2 positionOffset)
        {
            List<Generator> copies = new List<Generator>();
            if (graph == null || source == null || source.Count == 0) return copies;

            // old id -> new id, so the connection pass can find the copies.
            Dictionary<string, string> idMap = new Dictionary<string, string>();

            for (int i = 0; i < source.Count; i++)
            {
                Generator original = source[i];
                if (original == null) continue;

                Generator copy = Clone(original);
                if (copy == null) continue;

                string oldId = original.id;

                // A fresh id is what makes this a copy rather than a second
                // reference to the same node - Graph keys everything by id.
                copy.id = Guid.NewGuid().ToString();
                copy.editorPosition = original.editorPosition + positionOffset;

                if (!string.IsNullOrEmpty(oldId) && !idMap.ContainsKey(oldId))
                    idMap[oldId] = copy.id;

                graph.nodes.Add(copy);
                copies.Add(copy);
            }

            for (int i = 0; i < graph.connections.Count; i++)
            {
                Connection c = graph.connections[i];
                if (string.IsNullOrEmpty(c.sourceNodeId)) continue;

                string newSource, newTarget;
                if (!idMap.TryGetValue(c.sourceNodeId, out newSource)) continue;
                if (!idMap.TryGetValue(c.targetNodeId, out newTarget)) continue;

                graph.AddConnection(new Connection(newSource, c.sourcePortIndex, newTarget, c.targetPortIndex));
            }

            GraphAssetUtility.MarkDirty(graph);
            return copies;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        // Round-trips a node through JSON to deep-copy it. Reflection field-copying
        // would alias nested reference types (curves, gradients), so an edit to the
        // copy would reach back into the original.
        private static Generator Clone(Generator original)
        {
            if (original == null) return null;

            Generator copy = Activator.CreateInstance(original.GetType()) as Generator;
            if (copy == null) return null;

            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(original, false), copy);
            return copy;
        }

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
