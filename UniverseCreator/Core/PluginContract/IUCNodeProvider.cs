// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract  (PLG-1)
//  File:    IUCNodeProvider.cs
//  Purpose: Extension point: a plugin contributes extra procedural node types into
//           the host's node catalog. The host's NodeCatalog scans its own assembly
//           for Generator subclasses; a plugin living in ANOTHER assembly announces
//           its node types here so they appear in the add-node menu too.
//
//  WHY System.Type AND NOT Generator
//  ---------------------------------
//  The base node class Generator lives in the sub-engine (core) assembly, which the
//  contract must not reference (one-way rule). So a provider returns raw Types; the
//  host validates each is a concrete Generator subclass with a public parameterless
//  constructor - exactly the checks NodeCatalog.Build() already applies to its own
//  assembly - and rejects any that is not, rather than trusting the plugin.
//
//  A node-providing plugin therefore references BOTH the contract (for this
//  interface) AND the core (to subclass Generator). That is allowed: the plugin is
//  the lowest level and may depend on anything above it. The core still never
//  depends on the plugin.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace UniverseCreator.PluginContract
{
    /// <summary>
    /// A plugin implements this to add node types to the host's catalog. Called once
    /// when the catalog is (re)built. Each returned <see cref="Type"/> must be a
    /// concrete Generator subclass with a public parameterless constructor; the host
    /// validates and skips any that is not.
    /// </summary>
    public interface IUCNodeProvider
    {
        /// <summary>
        /// Return the node types this plugin contributes. Return an empty sequence
        /// (never null) to contribute none. The host does not cache the Types across
        /// a catalog rebuild, so this may be recomputed.
        /// </summary>
        IEnumerable<Type> ProvideNodeTypes();
    }
}
