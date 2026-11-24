// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1, extended PLG-2b)
//  File:    UCPluginAttribute.cs
//  Purpose: The code-plugin DESCRIPTOR. Announces a plugin to Universe Creator and
//           carries the same identity fields Unreal's `.uplugin` descriptor does
//           (FriendlyName, Version, Author, Category, Description, EnabledByDefault).
//           The host discovers it by reflection - it never names any plugin.
//
//  TWO PLACES IT CAN GO, AND WHY BOTH ARE NEEDED
//  ---------------------------------------------
//  On an ASSEMBLY:  [assembly: UCPlugin(...)]  - one descriptor for a whole .dll.
//      The cheapest possible scan (one attribute read per assembly), and the right
//      form for a PCG that ships its own asmdef or a pre-compiled managed plugin
//      dropped into Plugins/.
//
//  On a CLASS:      [UCPlugin(...)] public class MyPcgPlugin : IUCNodeProvider
//      Required for UNIVERSALITY. A great many Unity tools - including ones that
//      ship as loose .cs files - have no Assembly Definition at all, so their code
//      lands in Unity's predefined assembly (Assembly-CSharp) together with every
//      other loose script in the project. Marking that shared assembly would claim
//      the whole project as one plugin, which is wrong and un-toggleable. A class
//      descriptor lets such a tool announce ITSELF, with no asmdef and no project
//      restructuring: drop in one file and it is a plugin.
//
//  Unreal has the same split in spirit - a descriptor names the plugin, a module
//  names the code inside it - but Unity's compilation model makes the class form the
//  one that works for every tool rather than only the well-packaged ones.
//
//  This type lives in the CONTRACT assembly, which references no other UC assembly:
//  a plugin depends only on the contract (same-or-higher level, Unreal's rule), so
//  the sub-engine can be built with no plugin present and never the other way round.
// -----------------------------------------------------------------------------

using System;

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// Marks an assembly OR a single class as a Universe Creator plugin and declares
    /// its identity. Discovered by <c>UCPluginRegistry</c> via reflection; mirrors the
    /// fields of an Unreal <c>.uplugin</c> descriptor.
    ///
    /// Put it on the assembly when the plugin owns its own assembly; put it on a class
    /// when it does not (loose scripts compiled into Unity's predefined assembly).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class,
                    AllowMultiple = false, Inherited = false)]
    public sealed class UCPluginAttribute : Attribute
    {
        /// <summary>Human-readable name shown in the Connected Plugins window.</summary>
        public string FriendlyName { get; set; } = string.Empty;

        /// <summary>Semantic version string the plugin author stamps, e.g. "4.0.0".</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Who made it, shown in the window. Free text.</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Category path for grouping, e.g. "World/Infrastructure".</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>One-line description shown under the plugin's row.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Whether the plugin starts enabled the first time it is discovered. The
        /// per-plugin toggle persisted by the host overrides this on later sessions;
        /// this is only the first-run default (Unreal's EnabledByDefault).
        /// </summary>
        public bool EnabledByDefault { get; set; } = true;
    }
}
