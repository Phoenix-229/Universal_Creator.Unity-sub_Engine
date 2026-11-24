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
        /// <summary>UC-T05-03: runtime-declared authoring domains, captured by the existing cached type probe.</summary>
        public UCGraphDomainMask graphDomains;

        // "Creator/Perlin Noise" - the slash is what makes Unity's GenericMenu
        // build a submenu rather than one flat 66-item wall.
        public string MenuPath { get { return category + "/" + nodeName; } }
    }

    public static partial class NodeCatalog // UC-T03-03: the contract inventory uses this same discovered catalog.
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
            Generator node = Activator.CreateInstance(entry.type) as Generator;
            // VEG-6: this is the ONE genuine "user added a new node" path (the load path runs
            // through GraphSerializer, and the menu's type probe uses Activator directly), so
            // first-time-only defaults are applied here and nowhere else.
            if (node != null) node.OnCreatedFresh();
            return node;
        }

        private static List<NodeCatalogEntry> Build()
        {
            // PLG-3: the shipped scan, then node types contributed by enabled plugins. With no
            // plugin present EnabledNodeTypes() yields nothing, so the catalog is byte-identical
            // to the single-assembly scan it has always been.
            return BuildCatalog(UniverseCreator.Plugins.UCPluginRegistry.EnabledNodeTypes());
        }

        /// <summary>
        /// Builds the catalog from the shipped node assembly plus any extra node types (a plugin's
        /// contribution). Exposed so a test can supply the extra types directly; Build() supplies
        /// the live enabled-plugin set. Dedupes by type, so a plugin cannot double-list a shipped
        /// node, and applies the SAME validation to shipped and plugin types alike.
        /// </summary>
        public static List<NodeCatalogEntry> BuildCatalog(IEnumerable<Type> extraNodeTypes)
        {
            List<NodeCatalogEntry> found = new List<NodeCatalogEntry>();
            HashSet<Type> seen = new HashSet<Type>();

            // Scan the assembly that defines Generator rather than all loaded
            // assemblies: every shipped node lives beside it, and a full AppDomain
            // sweep would drag in editor and third-party types for nothing. Plugin
            // nodes arrive through extraNodeTypes instead, already filtered to enabled.
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

            for (int i = 0; i < types.Length; i++) TryAddType(types[i], found, seen);

            if (extraNodeTypes != null)
                foreach (Type t in extraNodeTypes) TryAddType(t, found, seen);

            // Category order matches the pipeline (create -> modify -> select ->
            // combine -> output), which is the order a user builds a graph in.
            return found
                .OrderBy(e => (int)e.category)
                .ThenBy(e => e.nodeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Validates one type and, if it is a concrete constructible Generator not already
        /// catalogued, probes it for its menu metadata and adds an entry. The probe is wrapped
        /// because a node whose constructor throws must not take the whole menu down with it.
        /// </summary>
        private static void TryAddType(Type t, List<NodeCatalogEntry> found, HashSet<Type> seen)
        {
            if (t == null) return;
            if (t.IsAbstract || t.IsGenericTypeDefinition) return;
            if (!typeof(Generator).IsAssignableFrom(t)) return;

            // Needs a public parameterless constructor: the menu builds these
            // with no arguments and Unity needs it for deserialization too.
            if (t.GetConstructor(Type.EmptyTypes) == null) return;

            if (seen.Contains(t)) return;   // already catalogued (shipped, or another provider)

            // NodeName and Category are instance properties, so reading them
            // requires an instance. Nodes are plain classes with cheap field
            // defaults, so this probe is safe.
            Generator probe;
            try
            {
                probe = Activator.CreateInstance(t) as Generator;
            }
            catch (Exception)
            {
                return;
            }
            if (probe == null) return;

            seen.Add(t);
            found.Add(new NodeCatalogEntry
            {
                type = t,
                nodeName = string.IsNullOrEmpty(probe.NodeName) ? t.Name : probe.NodeName,
                category = probe.Category,
                tooltip = probe.Tooltip,
                graphDomains = probe.GraphDomains // UC-T05-03: palette filtering never repeatedly constructs every discovered node.
            });
        }
    }
}
