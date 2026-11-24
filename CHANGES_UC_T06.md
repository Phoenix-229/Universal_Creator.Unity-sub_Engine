# Universe Creator — Topic 6 vegetation upgrade

Code-side completion: [x] ✅ **UC-T06-01…18**. Unity 6000.5.2f1 / URP 17.5.0. Rendered/device acceptance remains open.

Vegetation needed coherent graph/world inputs, stable identities, final-ground contact, bounded rendering ownership and persistent manual intent. The existing scatter/ecology/terrain owners now provide these through the same accepted graph and native commit path.

The topic preserves serialized legacy sampling/removals and supplies explicit migration; it adds deterministic world candidates and region masks, validated habitat/accepted-water inputs, prototype variation/collision routing, appearance-only refresh, spatial LOD/submesh batches, staged native rollback and exact retirement. Manager/graph authoring shares explicit Run/Cancel and Undo. Fractional grass/m² survives final quantization. Full-key manual poses, exclusions and detail patches persist separately from procedural outputs; invalid ground/water contact is diagnosed rather than silently moving a locked pose.

Main owners and their changes:

- `Core/Runtime/Nodes/Scatter/` and `Math/Spatial/`: regional/habitat/candidate rules, stable sampling and retained placement reports.
- `Core/Runtime/Terrain/VegetationInstanceKey.cs`, `ScatterIdentitySnapshot.cs`, `ScatterRemovalStore*`: full identities, explicit legacy migration and authored intent.
- `Core/Runtime/Terrain/ScatterApplier*`, `ScatterGroundBinding` and prototype/collider/resource types: final contact, bounded native placement, staged ownership and exact retirement.
- `Core/Runtime/Terrain/UCWorldGraphBindings*`, graph vegetation products and manager/chunk owners: accepted whole/region/patch inputs, explicit authoring and independent appearance refresh.
- `Core/Runtime/Optimization/GPUInstancingManager*`: spatial groups, authored LOD/submesh/material routing, bounded submits and ownership retirement.
- `Core/Editor/Inspectors/`, `Utilities/Vegetation*`, `Diagnostics/UCSelfTest.Vegetation*`: shared category controls, prototype/reference guidance, ownership inventory and registered production regressions.
- `UC_FULL_SYSTEM_AUDIT_2026-11-14.md`: per-task evidence, exact verification and the next continuation checkpoint.

Final verification used the **5,652-file** complete-content snapshot `.work/UC_T06_clean_snapshot_20261117_175402099.json`, digest `6982569F1EAB7A3F638E4A36F9ACBA7D99F1C0E13B6718E05A65381079C7D057`. The isolated project contains current UC plus unchanged package pins/settings; no RCM, main-project cache or live scene state was copied.

| Gate | Checks / failures / OS exit | Workspace log | Launch RAM |
|---|---|---|---|
| `RunBatch` | **18,545 / 0 / 0** | `Logs/UC_ISOLATED_T06_COMPLETE_PACKAGE_20261117.log` | 5.21 GiB |
| `RunSnapshotSceneReloadBatch` | **9 / 0 / 0** | `Logs/UC_ISOLATED_T06_COMPLETE_SCENE_20261117.log` | 5.51 GiB |
| `RunSnapshotDomainReloadBatch` | **9 / 0 / 0** | `Logs/UC_ISOLATED_T06_COMPLETE_DOMAIN_20261117.log` | 5.45 GiB |

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe' -batchmode -nographics -projectPath "C:\Users\Admin\Desktop\Universe Builder\.work\UC_validation_project_T06_CLEAN_20261117_172424678" -executeMethod UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch -logFile "C:\Users\Admin\Desktop\Universe Builder\Logs\UC_ISOLATED_T06_COMPLETE_PACKAGE_20261117.log"
```

The two native commands use the same executable/project with their table methods and logs. Exact launch/exit JSON records are under `.work/UC_ISOLATED_T06_COMPLETE_*_20261117_*`. After every process exited naturally, the final source/copied inventories, all source hashes and package/version pins matched. Unity generated 68 metadata files for scripts lacking source metadata; their hashes are recorded separately and remain stable. Shutdown tails report `Found no leaked weakptrs.` This is not a general proof that every native allocation or graphics path is leak-free.

Recovery history remains in the audit: the first clean copy incorrectly honored Git ignore rules and omitted existing sample content. Its focused gate passed **406/0/exit 0**, while its package ran **18,545/2/exit 1** on the two content scans. The helper inventory was corrected and current content copied unchanged; the complete package passed without modifying or skipping the assertions. Product sources and assets were not changed during this gate recovery.

Needs a manual check in the Unity Editor: parent/manager/window usability, native authored-intent save/reload, real prototype root/collider contact, wind/materials, canopy/grass coverage, near/far LOD and rendered ground/water-change behavior. Graphics/audio/player-build and sustained Redmi Note 14-class performance require separate measurements. No RCM type, locked node-placement rule or borrowed prototype asset was changed. Next coding task: UC-T07-01/02, Texture Graph capability matrix and explicit product contracts.
