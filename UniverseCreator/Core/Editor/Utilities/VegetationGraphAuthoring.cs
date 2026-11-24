// UC-T06-15: shared Editor Run/Apply tracking delegates all placement and cancellation to the existing world binding.
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEditor;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UnityEngine;

namespace UniverseCreator.Editor.Utilities
{
    internal static class VegetationGraphAuthoring
    {
        private sealed class Applied
        {
            internal Graph graph;
            internal int graphDirty, worldDirty;
            internal UCVegetationSystemManager manager;
            internal long settingsRevision, acceptedRevision;
        }

        private static readonly ConditionalWeakTable<UCWorldGraphBindings, Applied> applied = new ConditionalWeakTable<UCWorldGraphBindings, Applied>();

        /// <summary>Binds saved vegetation authoring with Undo, without running or changing the accepted world.</summary>
        /// <param name="bindings">Existing non-null world authority.</param>
        /// <param name="graph">Explicit Vegetation Graph, or null to unbind; unsupported domains throw before mutation.</param>
        internal static void Bind(UCWorldGraphBindings bindings, Graph graph)
        {
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            if (graph != null && (!graph.HasSupportedDomainMetadata || graph.Domain != UCGraphDomain.Vegetation))
                throw new ArgumentException("Select an explicit Vegetation Graph.", nameof(graph));
            if (bindings.VegetationGraph == graph) return;
            Undo.RecordObject(bindings, "Bind Vegetation Graph");
            bindings.VegetationGraph = graph; EditorUtility.SetDirty(bindings);
        }

        /// <summary>Captures Editor authoring stamps and runs the existing whole-world, region or patch application.</summary>
        /// <param name="bindings">Existing non-null world authority.</param>
        /// <param name="cell">Optional integer region; ignored when a world-metre patch is supplied.</param>
        /// <param name="patch">Optional finite positive X/Z rectangle in world metres.</param>
        /// <returns>The existing application result; missing inputs and cancellation retain prior planting. Runtime exceptions propagate.</returns>
        internal static async Task<bool> Run(UCWorldGraphBindings bindings, Coord? cell = null, Rect? patch = null)
        {
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            var world = bindings.Owner;
            var manager = world != null ? UCVegetationSystemManager.ForWorld(world.transform) : null;
            long expectedRevision = checked(bindings.VegetationRevision + 1); // UC-T06-15: an older continuation cannot overwrite a later Run's authoring stamp.
            var stamp = new Applied { graph = bindings.VegetationGraph, manager = manager,
                graphDirty = bindings.VegetationGraph != null ? EditorUtility.GetDirtyCount(bindings.VegetationGraph) : 0,
                worldDirty = world != null ? EditorUtility.GetDirtyCount(world) : 0,
                settingsRevision = manager != null ? manager.PlacementRevision : 0 };
            bool accepted = await (patch.HasValue ? bindings.RunVegetationPatchAsync(patch.Value) :
                cell.HasValue ? bindings.RunVegetationRegionAsync(cell.Value) : bindings.RunVegetationAsync());
            if (accepted && bindings.VegetationRevision == expectedRevision)
            {
                stamp.acceptedRevision = expectedRevision;
                // A partial Run captures future streaming settings; other resident regions still require their own Apply.
                if (cell.HasValue || patch.HasValue) stamp.acceptedRevision = -1;
                applied.Remove(bindings); applied.Add(bindings, stamp);
            }
            return accepted;
        }

        /// <summary>Reports unapplied Editor inputs using bounded dirty/revision checks; never serializes a graph on repaint.</summary>
        /// <param name="bindings">World binding, or null before a binding exists.</param>
        /// <returns>True when no tracked whole-world acceptance matches current graph/world/manager inputs; reloads require explicit recapture.</returns>
        internal static bool HasPendingChanges(UCWorldGraphBindings bindings)
        {
            if (bindings == null || !applied.TryGetValue(bindings, out var stamp) || stamp.acceptedRevision != bindings.VegetationRevision) return true;
            var world = bindings.Owner;
            var manager = world != null ? UCVegetationSystemManager.ForWorld(world.transform) : null;
            return stamp.graph != bindings.VegetationGraph || stamp.graph == null || EditorUtility.GetDirtyCount(stamp.graph) != stamp.graphDirty ||
                world == null || EditorUtility.GetDirtyCount(world) != stamp.worldDirty || manager != stamp.manager ||
                (manager != null ? manager.PlacementRevision : 0) != stamp.settingsRevision;
        }
    }
}
