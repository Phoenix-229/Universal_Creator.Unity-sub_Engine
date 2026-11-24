// -----------------------------------------------------------------------------
//  Universe Creator - Plugin System  (PLG-4)
//  File:    UCPluginBakeBridge.cs
//  Purpose: The host-side glue that invokes plugin extension points at the bake's
//           boundaries. The sub-engine calls these four methods; they iterate the
//           registry's ENABLED views and route each call to every plugin that
//           registered, isolating a throwing plugin so one bad extension can never
//           fail the whole bake. With no plugin present every loop runs zero times,
//           so a world without a plugin bakes exactly as it did before.
//
//  WHY A BRIDGE AND NOT CALLS INLINE IN ChunkManager
//  -------------------------------------------------
//  Two reasons. First, the per-plugin try/catch + partial-rollback discipline lives
//  in one place instead of being copy-pasted at each bake seam. Second, each method
//  has an overload that takes an explicit enumerable, so the self-test drives the
//  exact same code path with fixture plugins without needing a real [UCPlugin]
//  assembly loaded - the registry-backed overload just forwards to it.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.PluginContract;
using UniverseCreator.Integration;

namespace UniverseCreator.Plugins
{
    /// <summary>
    /// Fires plugin bake hooks and collects plugin terrain shapes. Every method is
    /// null-safe and swallows (with a warning) a per-plugin exception.
    /// </summary>
    public static class UCPluginBakeBridge
    {
        // --- registry-backed entry points the sub-engine calls ---

        public static void FireBakeBegin(UCPluginContext ctx)
        {
            FireBakeBegin(UCPluginRegistry.EnabledBakeHooks(), ctx);
        }

        public static void FireBakeEnd(UCPluginContext ctx)
        {
            FireBakeEnd(UCPluginRegistry.EnabledBakeHooks(), ctx);
        }

        public static void FireWorldDataReady(UCPluginContext ctx)
        {
            FireWorldDataReady(UCPluginRegistry.EnabledWorldDataConsumers(), ctx);
        }

        public static int CollectShapes(UCPluginContext ctx, List<TerrainShapeRequest> into)
        {
            return CollectShapes(UCPluginRegistry.EnabledTerrainShapeProviders(), ctx, into);
        }

        // --- explicit-enumerable overloads (the testable core) ---

        public static void FireBakeBegin(IEnumerable<IUCWorldBakeHook> hooks, UCPluginContext ctx)
        {
            if (hooks == null) return;
            foreach (IUCWorldBakeHook h in hooks)
            {
                if (h == null) continue;
                try { h.OnBakeBegin(ctx); }
                catch (Exception e) { Warn("OnBakeBegin", h, e); }
            }
        }

        public static void FireBakeEnd(IEnumerable<IUCWorldBakeHook> hooks, UCPluginContext ctx)
        {
            if (hooks == null) return;
            foreach (IUCWorldBakeHook h in hooks)
            {
                if (h == null) continue;
                try { h.OnBakeEnd(ctx); }
                catch (Exception e) { Warn("OnBakeEnd", h, e); }
            }
        }

        public static void FireWorldDataReady(IEnumerable<IUCWorldDataConsumer> consumers, UCPluginContext ctx)
        {
            if (consumers == null) return;
            foreach (IUCWorldDataConsumer c in consumers)
            {
                if (c == null) continue;
                try { c.OnWorldDataReady(ctx); }
                catch (Exception e) { Warn("OnWorldDataReady", c, e); }
            }
        }

        /// <summary>
        /// Append every provider's shapes to <paramref name="into"/> and return how many
        /// were added. A provider that throws has its partial adds rolled back so the
        /// shared list is never left half-filled by a failure.
        /// </summary>
        public static int CollectShapes(IEnumerable<IUCTerrainShapeProvider> providers,
                                        UCPluginContext ctx, List<TerrainShapeRequest> into)
        {
            if (providers == null || into == null) return 0;
            int added = 0;
            foreach (IUCTerrainShapeProvider p in providers)
            {
                if (p == null) continue;
                int before = into.Count;
                try { p.CollectTerrainShapes(ctx, into); }
                catch (Exception e)
                {
                    Warn("CollectTerrainShapes", p, e);
                    if (into.Count > before) into.RemoveRange(before, into.Count - before);
                    continue;
                }
                added += into.Count - before;
            }
            return added;
        }

        private static void Warn(string where, object plugin, Exception e)
        {
            Debug.LogWarning("Universe Creator: plugin '" +
                (plugin != null ? plugin.GetType().FullName : "?") +
                "' threw in " + where + ": " + (e != null ? e.Message : "null"));
        }
    }
}
