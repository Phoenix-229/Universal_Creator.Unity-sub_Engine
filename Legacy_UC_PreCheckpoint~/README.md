# Preserved scripts from the earlier UC checkpoint

These eight scripts and their original `.meta` companions were preserved byte-for-byte on 2026-11-25 because they are absent from the accepted current UC source. Keep this folder outside `Assets/UniverseCreator`; its trailing tilde prevents Unity from importing the archive. This archive is evidence for migration review, not a second runtime system.

`UCPlannedModuleInspector.cs` duplicates the current `UCLightingSystemManagerInspector` and `UCVegetationSystemManagerInspector` declarations. Current UC C# callers do not reference the other archived drawing, road-link stub or river-planner types. Restoring these scripts into the active toolkit would invalidate the tested source checkpoint and can reintroduce duplicate declarations.

Preserved relative paths, each with its original metadata:

- `UniverseCreator/Core/Editor/Drawing/ConnectionDrawer.cs`
- `UniverseCreator/Core/Editor/Drawing/GroupDrawer.cs`
- `UniverseCreator/Core/Editor/Drawing/PortDrawer.cs`
- `UniverseCreator/Core/Editor/Inspectors/RoadMasterLinkerInspector.cs`
- `UniverseCreator/Core/Editor/Inspectors/UCPlannedModuleInspector.cs`
- `UniverseCreator/Core/Runtime/Integration/RoadMasterLinker.cs`
- `UniverseCreator/Core/Runtime/Integration/TerrainHealer.cs`
- `UniverseCreator/Core/Runtime/Terrain/RiverNetworkPlanner.cs`

Resolved source/destination paths and matching before/after SHA-256 hashes were recorded in the workspace's `Logs/UC_ISOLATED_DELIVERY_HASHES_20261125.log`. No metadata content or GUID was edited. Saved scenes using the obsolete road-link stub need a manual migration review; isolated acceptance does not establish their compatibility. The current UC toolkit and RCM remain independent.
