// UC-T04-01: one body-level mode, preserving original serialized flags for recoverable migration.
using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Defines whether a manually authored river reads accepted procedural ground or owns its excavation.</summary>
    public enum RiverTerrainMode
    {
        /// <summary>Carves the authored course's own bed and banks; the legacy unchecked default.</summary>
        CarveManualCourse = 0,
        /// <summary>Fits water to committed procedural bed/banks without emitting terrain cuts.</summary>
        ReadProceduralTrench = 1
    }

    public partial class UCWaterBody
    {
        private const int RiverTerrainModeSchema = 1;
        [NonSerialized] private bool hasObservedRiverMode;
        [NonSerialized] private RiverTerrainMode observedRiverMode;
        [SerializeField, HideInInspector] private RiverTerrainMode riverTerrainMode;
        [SerializeField, HideInInspector] private int riverTerrainModeVersion;
        [SerializeField, HideInInspector] private LegacyRiverTerrainSettings legacyRiverTerrainSettings;

        [Serializable]
        private sealed class LegacyRiverTerrainSettings
        {
            public bool bodyRead, dig;
            public UCRiverNode[] nodes;
            public bool[] nodeRead;
            public int readCount;
            public bool conflict;
        }

        /// <summary>The single river-body mode. Reading it migrates legacy flags once on the Unity main thread.</summary>
        /// <remarks>Does not move authored points, change their hydraulic values or write terrain. Conflicting flags retain
        /// the old effective behavior, with a persistent diagnostic and serialized recovery data.</remarks>
        public RiverTerrainMode TerrainMode
        {
            get
            {
                EnsureRiverTerrainMode();
                var effective = riverTerrainModeVersion == RiverTerrainModeSchema &&
                    (riverTerrainMode == RiverTerrainMode.CarveManualCourse || riverTerrainMode == RiverTerrainMode.ReadProceduralTrench)
                    ? riverTerrainMode : RiverTerrainMode.ReadProceduralTrench; // Unsupported data must never enable excavation.
                bool changed = hasObservedRiverMode && observedRiverMode != effective;
                observedRiverMode = effective; hasObservedRiverMode = true;
                if (changed) InvalidateRiverModeProducts(); // UC-T04-02: includes Undo/serialized changes; update the witness before cancellation callbacks.
                return effective;
            }
        }

        /// <summary>True until this body's original legacy flags have been captured by the versioned migration.</summary>
        public bool NeedsRiverTerrainModeMigration => riverTerrainModeVersion == 0;

        /// <summary>Reports the original legacy choice, conflicts and disabled excavation; empty for a consistent migration.</summary>
        public string RiverTerrainModeDiagnostic
        {
            get
            {
                EnsureRiverTerrainMode();
                if (riverTerrainModeVersion != RiverTerrainModeSchema ||
                    (riverTerrainMode != RiverTerrainMode.CarveManualCourse && riverTerrainMode != RiverTerrainMode.ReadProceduralTrench))
                    return "Unsupported river terrain-mode schema; procedural read mode is used until a supported migration is available.";
                var legacy = legacyRiverTerrainSettings;
                if (legacy == null) return string.Empty;
                string message = legacy.conflict
                    ? $"Legacy mode conflict: body Read Trench={legacy.bodyRead}, {legacy.readCount}/{legacy.nodes?.Length ?? 0} child flags checked. " +
                      "Migration retained the previous effective Read Procedural Trench behavior. Child flags are now recovery data; the body mode above is authoritative."
                    : string.Empty;
                if (!legacy.dig)
                    message += (message.Length == 0 ? "" : " ") + "Legacy Dig Terrain was disabled and remains disabled; migration does not enable excavation.";
                return message;
            }
        }

        /// <summary>Captures original body/owned-child flags once and selects the legacy effective mode without changing authored data.</summary>
        /// <remarks>Must run on the Unity main thread. Nested bodies' points are excluded. Unknown future schemas use read mode
        /// without rewriting their serialized data. Existing flags, positions, route and hydraulic values remain intact.</remarks>
        public void EnsureRiverTerrainMode()
        {
            if (riverTerrainModeVersion != 0) return;
            UniverseCreator.Nodes.GraphExecutionGuard.RequireMainThread("River terrain-mode migration");
            var children = GetComponentsInChildren<UCRiverNode>(true);
            var owned = new System.Collections.Generic.List<UCRiverNode>(children.Length);
            foreach (var node in children)
                if (node != null && node.GetComponentInParent<UCWaterBody>(true) == this) owned.Add(node);
            var legacy = new LegacyRiverTerrainSettings {
                bodyRead = followProceduralTrench, dig = digTerrain, nodes = owned.ToArray(), nodeRead = new bool[owned.Count] };
            for (int i = 0; i < owned.Count; i++)
            {
                legacy.nodeRead[i] = owned[i].followProceduralTrench;
                if (legacy.nodeRead[i]) legacy.readCount++;
            }
            legacy.conflict = owned.Count > 0 && (legacy.bodyRead ? legacy.readCount != owned.Count : legacy.readCount > 0);
            // Preserve the historical result explicitly; subsequent point edits can never select a whole-body mode.
            riverTerrainMode = legacy.bodyRead || legacy.readCount > 0
                ? RiverTerrainMode.ReadProceduralTrench : RiverTerrainMode.CarveManualCourse;
            legacyRiverTerrainSettings = legacy;
            riverTerrainModeVersion = RiverTerrainModeSchema;
            observedRiverMode = riverTerrainMode; hasObservedRiverMode = true;
            if (legacy.conflict) Debug.LogWarning(RiverTerrainModeDiagnostic, this); // Report the actual ambiguity once, including outside the Inspector.
        }

        /// <summary>Selects the body's authoritative mode and invalidates the previous fitted profile without moving points or writing terrain.</summary>
        /// <param name="mode">ReadProceduralTrench or CarveManualCourse; other enum values are rejected.</param>
        /// <exception cref="ArgumentOutOfRangeException">The mode is not supported.</exception>
        /// <exception cref="InvalidOperationException">The stored schema is unsupported and must not be overwritten.</exception>
        /// <remarks>Main thread only. Retains original migration data and supersedes frozen world work carrying the old carve.
        /// Call the existing Build/Rebuild path to fit the selected mode; accepted terrain remains until a new commit.</remarks>
        public void SetRiverTerrainMode(RiverTerrainMode mode)
        {
            if (mode != RiverTerrainMode.ReadProceduralTrench && mode != RiverTerrainMode.CarveManualCourse)
                throw new ArgumentOutOfRangeException(nameof(mode));
            UniverseCreator.Nodes.GraphExecutionGuard.RequireMainThread("River terrain-mode selection");
            EnsureRiverTerrainMode();
            if (riverTerrainModeVersion != RiverTerrainModeSchema)
                throw new InvalidOperationException("Cannot overwrite an unsupported river terrain-mode schema.");
            if (riverTerrainMode == mode) return;
            riverTerrainMode = mode;
            observedRiverMode = mode; hasObservedRiverMode = true;
            InvalidateRiverModeProducts(); // UC-T04-02: old graph snapshots and direct carve requests must retire together.
        }

        /// <summary>Restores the original migration flags on the body and surviving original owned points; positions and profiles are unchanged.</summary>
        /// <returns>False if the original record is absent/malformed or its schema is unsupported; otherwise true.</returns>
        /// <remarks>Main thread only. Deleted or reparented points are not recreated or modified. The next fit migrates the restored flags again.
        /// Editor callers must record Undo for the body and points before calling, then rebuild explicitly.</remarks>
        public bool RestoreLegacyRiverTerrainSettings()
        {
            UniverseCreator.Nodes.GraphExecutionGuard.RequireMainThread("River terrain-mode recovery");
            var legacy = legacyRiverTerrainSettings;
            if (legacy == null || riverTerrainModeVersion != RiverTerrainModeSchema || legacy.nodes == null ||
                legacy.nodeRead == null || legacy.nodes.Length != legacy.nodeRead.Length) return false;
            followProceduralTrench = legacy.bodyRead; digTerrain = legacy.dig;
            for (int i = 0; i < legacy.nodes.Length; i++)
                if (legacy.nodes[i] != null && legacy.nodes[i].GetComponentInParent<UCWaterBody>(true) == this)
                    legacy.nodes[i].followProceduralTrench = legacy.nodeRead[i];
            riverTerrainModeVersion = 0;
            InvalidateRiverModeProducts(); // UC-T04-02: restored flags may select a different mode on the next fit.
            return true;
        }

        private void InvalidateRiverModeProducts()
        {
            UniverseCreator.Nodes.GraphExecutionGuard.RequireMainThread("River terrain-mode invalidation");
            cachedBedY = null; _forceResolveBed = true; carve?.Clear();
            riverSurfaceFit = null; riverShoreline = null; RiverSurfaceDiagnostic = "River mode changed; rebuild the surface."; // UC-T04-06: retire both bank extents with the previous-mode fit.
            UniverseCreator.Nodes.Creators.RiverCourseInputNode.UnpublishBodyCourse(GetEntityId().GetHashCode());
            var world = CarveWorld;
            if (world != null)
            {
                world.CancelGeneration();
                world.GenerationScheduler.BeginRun(); // Frozen prior-mode workers cannot commit; accepted procedural ground stays available.
            }
        }
    }
}
