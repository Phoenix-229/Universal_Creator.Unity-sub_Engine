// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1)
//  File:    UCPluginManifest.cs
//  Purpose: The DATA-ONLY plugin descriptor. Unreal allows a plugin that "does not
//           require code at all" (content-only plugins); the Unity-native form is a
//           ScriptableObject asset carrying the same identity fields as the
//           [UCPlugin] attribute plus a payload list of plain Unity objects
//           (palettes, presets, prototype sets). No Generator code needed.
//
//  WHY A SCRIPTABLEOBJECT
//  ----------------------
//  A data pack has nothing to compile, so an assembly attribute has no assembly to
//  live on. A ScriptableObject is Unity's serialisable, code-free asset, discovered
//  the same way other UC assets are and shown in the Connected Plugins window beside
//  code plugins. It ships EMPTY (the asset-path-free rule): the buyer fills the
//  payload; an empty manifest contributes nothing and changes no existing world.
//
//  Lives in the CONTRACT assembly so a data pack references only the contract.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// A code-free Universe Creator plugin: identity fields plus a payload of Unity
    /// objects the host can hand to whatever consumes them. Create via
    /// Assets &gt; Create &gt; Universe Creator &gt; Plugin Manifest. Ships empty.
    /// </summary>
    [CreateAssetMenu(fileName = "UCPluginManifest",
                     menuName = "Universe Creator/Plugin Manifest", order = 400)]
    public sealed class UCPluginManifest : ScriptableObject
    {
        [Tooltip("Human-readable name shown in the Connected Plugins window.")]
        public string friendlyName = string.Empty;

        [Tooltip("Version string the author stamps, e.g. \"1.0.0\".")]
        public string version = string.Empty;

        [Tooltip("Who made it.")]
        public string author = string.Empty;

        [Tooltip("Category path for grouping, e.g. \"Content/Palettes\".")]
        public string category = string.Empty;

        [Tooltip("One-line description shown under the plugin's row.")]
        [TextArea] public string description = string.Empty;

        [Tooltip("Whether this data pack starts enabled the first time it is discovered.")]
        public bool enabledByDefault = true;

        [Tooltip("The payload: palettes, presets, prototype sets. SHIPPED EMPTY - the buyer fills it. " +
                 "An empty payload contributes nothing, so an unfilled manifest changes no existing world.")]
        public List<Object> payload = new List<Object>();

        /// <summary>True when the manifest actually carries a payload to apply.</summary>
        public bool HasPayload { get { return payload != null && payload.Count > 0; } }
    }
}
