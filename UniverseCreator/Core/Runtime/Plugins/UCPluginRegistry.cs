// -----------------------------------------------------------------------------
//  Universe Creator - Plugin System  (PLG-2)
//  File:    UCPluginRegistry.cs
//  Purpose: Discovers every connected plugin by reflecting over the loaded
//           assemblies, and serves the host the extension implementations each one
//           registered. This is the single mechanism that replaces the Linker: the
//           sub-engine stops NAMING any external system and instead iterates
//           whatever announced itself with [UCPlugin] - so the host depends on no
//           plugin, and a world with no plugin discovers an empty set and behaves
//           exactly as it does today.
//
//  WHY THIS GENERALISES NodeCatalog
//  --------------------------------
//  NodeCatalog scans ONE assembly (the one that defines Generator) for node types.
//  The plugin registry widens that to EVERY loaded assembly - but only the ones
//  carrying a [UCPlugin] descriptor, so the scan stays cheap - and sorts each
//  plugin's types by the four extension interfaces instead of by Generator. The
//  guard discipline is identical: a plugin whose assembly only partly loads, or
//  whose type throws in its constructor, is skipped, never allowed to take the whole
//  discovery down (the exact ReflectionTypeLoadException pattern NodeCatalog uses).
//
//  ENABLED STATE
//  -------------
//  Each plugin starts Enabled = its descriptor's EnabledByDefault. The editor's
//  Connected Plugins window (PLG-6) persists a per-plugin override and pushes it back
//  through SetEnabled; in a player build there is no editor prefs store, so the
//  descriptor default governs. The bake and the catalog consume only the Enabled*
//  views, so disabling a plugin removes its contribution without unloading its code.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniverseCreator.PluginContract;

namespace UniverseCreator.Plugins
{
    /// <summary>
    /// One discovered plugin: its descriptor identity plus the extension
    /// implementations it registered, already sorted by interface. Instances are
    /// created once at discovery and reused.
    /// </summary>
    public sealed class UCPluginInfo
    {
        /// <summary>The assembly that carried the [UCPlugin] descriptor.</summary>
        public Assembly Assembly;

        /// <summary>Stable key (the assembly full name), used by the enable toggle.</summary>
        public string Id;

        public string FriendlyName;
        public string Version;
        public string Author;
        public string Category;
        public string Description;

        /// <summary>The descriptor's first-run default.</summary>
        public bool EnabledByDefault;

        /// <summary>Live enabled state. Only Enabled plugins contribute to a bake.</summary>
        public bool Enabled;

        public readonly List<IUCNodeProvider> NodeProviders = new List<IUCNodeProvider>();
        public readonly List<IUCTerrainShapeProvider> TerrainShapeProviders = new List<IUCTerrainShapeProvider>();
        public readonly List<IUCWorldBakeHook> BakeHooks = new List<IUCWorldBakeHook>();
        public readonly List<IUCWorldDataConsumer> WorldDataConsumers = new List<IUCWorldDataConsumer>();

        public bool ProvidesNodes { get { return NodeProviders.Count > 0; } }
        public bool ProvidesTerrainShapes { get { return TerrainShapeProviders.Count > 0; } }
        public bool HasBakeHooks { get { return BakeHooks.Count > 0; } }
        public bool ConsumesWorldData { get { return WorldDataConsumers.Count > 0; } }
    }

    /// <summary>
    /// The host-side plugin discovery. Static and cached like <c>NodeCatalog</c>;
    /// the cache clears on domain reload, exactly when new plugin assemblies can
    /// appear. Every member is safe when no plugin is present (empty set, no throw).
    /// </summary>
    public static class UCPluginRegistry
    {
        private static List<UCPluginInfo> cache;

        /// <summary>Every discovered plugin, enabled or not. Built once, cached.</summary>
        public static IReadOnlyList<UCPluginInfo> All
        {
            get { if (cache == null) cache = Build(); return cache; }
        }

        /// <summary>Drop the cache so the next access rediscovers (e.g. after a toggle rebuild).</summary>
        public static void Invalidate() { cache = null; }

        /// <summary>The plugin with this id, or null.</summary>
        public static UCPluginInfo Find(string id)
        {
            IReadOnlyList<UCPluginInfo> all = All;
            for (int i = 0; i < all.Count; i++) if (all[i].Id == id) return all[i];
            return null;
        }

        /// <summary>Set a plugin's enabled state. Returns false if the id is unknown.</summary>
        public static bool SetEnabled(string id, bool on)
        {
            UCPluginInfo info = Find(id);
            if (info == null) return false;
            info.Enabled = on;
            return true;
        }

        // ------------------------------------------------------------------
        //  Enabled-only views - the catalog and the bake consume ONLY these,
        //  so a disabled plugin contributes nothing though its code is loaded.
        // ------------------------------------------------------------------

        /// <summary>Node types contributed by every enabled node-provider, flattened and null-safe.</summary>
        public static IEnumerable<Type> EnabledNodeTypes()
        {
            foreach (UCPluginInfo p in All)
            {
                if (!p.Enabled) continue;
                for (int i = 0; i < p.NodeProviders.Count; i++)
                {
                    IEnumerable<Type> ts = null;
                    try { ts = p.NodeProviders[i].ProvideNodeTypes(); }
                    catch { ts = null; }
                    if (ts == null) continue;
                    foreach (Type t in ts) if (t != null) yield return t;
                }
            }
        }

        public static IEnumerable<IUCTerrainShapeProvider> EnabledTerrainShapeProviders()
        {
            foreach (UCPluginInfo p in All)
                if (p.Enabled)
                    for (int i = 0; i < p.TerrainShapeProviders.Count; i++)
                        yield return p.TerrainShapeProviders[i];
        }

        public static IEnumerable<IUCWorldBakeHook> EnabledBakeHooks()
        {
            foreach (UCPluginInfo p in All)
                if (p.Enabled)
                    for (int i = 0; i < p.BakeHooks.Count; i++)
                        yield return p.BakeHooks[i];
        }

        public static IEnumerable<IUCWorldDataConsumer> EnabledWorldDataConsumers()
        {
            foreach (UCPluginInfo p in All)
                if (p.Enabled)
                    for (int i = 0; i < p.WorldDataConsumers.Count; i++)
                        yield return p.WorldDataConsumers[i];
        }

        // ------------------------------------------------------------------
        //  Discovery
        // ------------------------------------------------------------------

        private static List<UCPluginInfo> Build()
        {
            List<UCPluginInfo> result = new List<UCPluginInfo>();

            Assembly[] assemblies;
            try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return result; }   // no plugin can be found, but never throw

            for (int a = 0; a < assemblies.Length; a++)
            {
                Assembly asm = assemblies[a];
                if (asm == null) continue;

                // The HOST IS NOT ITS OWN PLUGIN. Universe Creator's own assemblies are skipped
                // outright, which is both correct in principle and load-bearing in practice: the
                // editor assembly contains self-test fixtures, and a fixture carrying a [UCPlugin]
                // descriptor would otherwise be adopted as a live plugin and handed real work -
                // a fixture that returns a terrain cut would put a hole in the author's world.
                if (IsHostAssembly(asm)) continue;

                UCPluginAttribute asmAttr;
                try { asmAttr = asm.GetCustomAttribute<UCPluginAttribute>(); }
                catch { asmAttr = null; }

                // An engine/BCL assembly can carry neither form of descriptor, so it is skipped
                // without a GetTypes() - that is what keeps a whole-domain scan cheap.
                if (asmAttr == null && IsSystemAssembly(asm)) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { types = Type.EmptyTypes; }

                result.AddRange(DiscoverFrom(asm, asmAttr, types));
            }

            return result;
        }

        /// <summary>
        /// Turns one assembly's descriptor(s) into plugin entries. Both descriptor forms are
        /// resolved here:
        ///
        ///  FORM 1 - an assembly-level descriptor claims the whole assembly as ONE plugin, holding
        ///           every contract implementation in it.
        ///  FORM 2 - class-level descriptors (PLG-2b, universality). A PCG with no asmdef compiles
        ///           into Unity's shared predefined assembly, so it announces itself per class.
        ///           Each marked class becomes its own plugin keyed by type name, carrying only the
        ///           contracts that class implements - so two unrelated tools sharing
        ///           Assembly-CSharp stay two separately-toggleable plugins. Skipped entirely when
        ///           the assembly already carried its own descriptor, so nothing is listed twice.
        ///
        /// Exposed (rather than inlined in Build) as the testable seam: a gate can drive both forms
        /// with fixture types instead of planting a real [UCPlugin] in shipped code, which the live
        /// registry would then discover and hand real work to.
        /// </summary>
        public static List<UCPluginInfo> DiscoverFrom(Assembly asm, UCPluginAttribute asmAttr, IEnumerable<Type> types)
        {
            List<UCPluginInfo> found = new List<UCPluginInfo>();

            if (asmAttr != null)
            {
                found.Add(BuildInfo(asmAttr, asm, types));
                return found;   // FORM 1 wins; class descriptors inside it are not separate plugins.
            }

            if (types == null) return found;

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;

                UCPluginAttribute typeAttr;
                try { typeAttr = type.GetCustomAttribute<UCPluginAttribute>(false); }
                catch { continue; }
                if (typeAttr == null) continue;

                UCPluginInfo info = BuildInfo(typeAttr, asm, new Type[] { type });

                // Keyed by the TYPE, not the assembly: the assembly is shared, the class is not.
                info.Id = type.FullName;
                if (string.IsNullOrEmpty(typeAttr.FriendlyName)) info.FriendlyName = type.Name;

                found.Add(info);
            }

            return found;
        }

        /// <summary>
        /// True for one of Universe Creator's OWN assemblies. The host cannot be its own plugin,
        /// and excluding them also keeps editor-only test fixtures out of the live registry.
        /// </summary>
        public static bool IsHostAssembly(Assembly asm)
        {
            string n;
            try { n = asm != null ? asm.GetName().Name : null; }
            catch { return false; }
            if (string.IsNullOrEmpty(n)) return false;

            return n == "UniverseCreator"
                || n == "UniverseCreator.Editor"
                || n == "UniverseCreator.PluginContract";
        }

        /// <summary>
        /// True for an assembly that cannot be a plugin - .NET's own and Unity's own. Used only to
        /// skip a GetTypes() we know is pointless; a false negative costs a little scan time, never
        /// correctness, and a third-party tool would not take these names.
        /// </summary>
        private static bool IsSystemAssembly(Assembly asm)
        {
            string n;
            try { n = asm.GetName().Name; }
            catch { return false; }
            if (string.IsNullOrEmpty(n)) return false;

            return n == "mscorlib" || n == "netstandard" || n == "System"
                || n.StartsWith("System.", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.", StringComparison.Ordinal)
                || n.StartsWith("Mono.", StringComparison.Ordinal)
                || n.StartsWith("UnityEngine", StringComparison.Ordinal)
                || n.StartsWith("UnityEditor", StringComparison.Ordinal)
                || n.StartsWith("Unity.", StringComparison.Ordinal)
                || n.StartsWith("nunit", StringComparison.Ordinal)
                || n.StartsWith("ExCSS", StringComparison.Ordinal)
                || n.StartsWith("JetBrains", StringComparison.Ordinal)
                || n.StartsWith("Bee.", StringComparison.Ordinal)
                || n.StartsWith("PlayerBuildProgram", StringComparison.Ordinal);
        }

        /// <summary>
        /// Build one plugin's info from its descriptor and candidate types: sort the
        /// types by the four extension interfaces and instantiate each once. Exposed
        /// as the testable seam Build() uses per assembly - a type with no public
        /// parameterless constructor, or whose constructor throws, is skipped.
        /// </summary>
        public static UCPluginInfo BuildInfo(UCPluginAttribute attr, Assembly owningAssembly, IEnumerable<Type> candidateTypes)
        {
            UCPluginInfo info = new UCPluginInfo
            {
                Assembly = owningAssembly,
                Id = owningAssembly != null ? owningAssembly.FullName
                                            : (attr != null ? attr.FriendlyName : string.Empty),
                FriendlyName = (attr != null && !string.IsNullOrEmpty(attr.FriendlyName))
                               ? attr.FriendlyName
                               : (owningAssembly != null ? owningAssembly.GetName().Name : "Plugin"),
                Version = attr != null ? (attr.Version ?? string.Empty) : string.Empty,
                Author = attr != null ? (attr.Author ?? string.Empty) : string.Empty,
                Category = attr != null ? (attr.Category ?? string.Empty) : string.Empty,
                Description = attr != null ? (attr.Description ?? string.Empty) : string.Empty,
                EnabledByDefault = attr != null && attr.EnabledByDefault,
                Enabled = attr != null && attr.EnabledByDefault
            };

            if (candidateTypes != null)
            {
                foreach (Type t in candidateTypes)
                {
                    if (t == null || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;

                    bool isNode = typeof(IUCNodeProvider).IsAssignableFrom(t);
                    bool isShape = typeof(IUCTerrainShapeProvider).IsAssignableFrom(t);
                    bool isHook = typeof(IUCWorldBakeHook).IsAssignableFrom(t);
                    bool isConsumer = typeof(IUCWorldDataConsumer).IsAssignableFrom(t);
                    if (!(isNode || isShape || isHook || isConsumer)) continue;

                    // Plain class with a public parameterless ctor, same discipline as NodeCatalog.
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                    object inst;
                    try { inst = Activator.CreateInstance(t); }
                    catch { continue; }
                    if (inst == null) continue;

                    if (isNode) info.NodeProviders.Add((IUCNodeProvider)inst);
                    if (isShape) info.TerrainShapeProviders.Add((IUCTerrainShapeProvider)inst);
                    if (isHook) info.BakeHooks.Add((IUCWorldBakeHook)inst);
                    if (isConsumer) info.WorldDataConsumers.Add((IUCWorldDataConsumer)inst);
                }
            }

            return info;
        }
    }
}
