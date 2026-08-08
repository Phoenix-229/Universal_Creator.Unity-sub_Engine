// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    NodeCatalog.cs
//  Purpose: Discovers every concrete Generator subclass by reflection and serves
//           them to the add-node menu, grouped by category.
//
//  WHY REFLECTION
//  --------------
//  A hand-written registry means every new node needs two edits: the node file and
//  the list. That list drifts - a node exists but cannot be created, which reads as
//  a missing feature. Scanning the assembly instead makes the menu a function of
//  what actually compiles, so dropping a new .cs into Nodes/ is the whole job.
//
//  Cached because the scan touches every type in the runtime assembly. Cleared on
//  domain reload automatically, which is exactly when new node types can appear.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Utilities
{
    public class NodeCatalogEntry
    {
        public Type type;
        public string nodeName;
        public NodeCategory category;
        public string tooltip;

        // "Creator/Perlin Noise" - the slash is what makes Unity's GenericMenu
        // build a submenu rather than one flat 66-item wall.
        public string MenuPath { get { return category + "/" + nodeName; } }
    }

    public static class NodeCatalog
    {
        private static List<NodeCatalogEntry> cache;

        public static List<NodeCatalogEntry> All
        {
            get
            {
                if (cache == null) cache = Build();
                return cache;
            }
        }

        /// <summary>Entries in one category, alphabetical.</summary>
        public static List<NodeCatalogEntry> InCategory(NodeCategory category)
        {
            return All.Where(e => e.category == category).ToList();
        }

        /// <summary>Case-insensitive substring match on name and tooltip.</summary>
        public static List<NodeCatalogEntry> Search(string query)
        {
            if (string.IsNullOrEmpty(query)) return All;
            string q = query.ToLowerInvariant();
            return All.Where(e =>
                e.nodeName.ToLowerInvariant().Contains(q) ||
                (e.tooltip != null && e.tooltip.ToLowerInvariant().Contains(q))).ToList();
        }

        public static void Invalidate() { cache = null; }

        /// <summary>Creates a fresh instance of a catalogued node type.</summary>
        public static Generator Instantiate(NodeCatalogEntry entry)
        {
            if (entry == null || entry.type == null) return null;
            return Activator.CreateInstance(entry.type) as Generator;
        }

        private static List<NodeCatalogEntry> Build()
        {
            List<NodeCatalogEntry> found = new List<NodeCatalogEntry>();

            // Scan the assembly that defines Generator rather than all loaded
            // assemblies: every shipped node lives beside it, and a full AppDomain
            // sweep would drag in editor and third-party types for nothing.
            Assembly asm = typeof(Generator).Assembly;

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A partially-loadable assembly still yields the types that did
                // load; better a slightly short menu than an empty one.
                types = ex.Types.Where(t => t != null).ToArray();
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];

                if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                if (!typeof(Generator).IsAssignableFrom(t)) continue;

                // Needs a public parameterless constructor: the menu builds these
                // with no arguments and Unity needs it for deserialization too.
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                // NodeName and Category are instance properties, so reading them
                // requires an instance. Nodes are plain classes with cheap field
                // defaults, so this probe is safe - but a node whose constructor
                // throws must not take the whole menu down with it.
                Generator probe;
                try
                {
                    probe = Activator.CreateInstance(t) as Generator;
                }
                catch (Exception)
                {
                    continue;
                }
                if (probe == null) continue;

                found.Add(new NodeCatalogEntry
                {
                    type = t,
                    nodeName = string.IsNullOrEmpty(probe.NodeName) ? t.Name : probe.NodeName,
                    category = probe.Category,
                    tooltip = probe.Tooltip
                });
            }

            // Category order matches the pipeline (create -> modify -> select ->
            // combine -> output), which is the order a user builds a graph in.
            return found
                .OrderBy(e => (int)e.category)
                .ThenBy(e => e.nodeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
