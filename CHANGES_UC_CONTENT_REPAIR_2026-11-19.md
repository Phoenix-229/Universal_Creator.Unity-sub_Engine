# UC content layout and compile/self-test repair

- [x] Final owner layout: 277 imported assets under one flat SampleNatural folder per category (Grass 34, Rocks 28, Shrubs 52, Surfaces 112, Trees 51), without deeper imported folders. Original 199 Surfaces files/folder metadata remain hash-identical. Asset GUID/data/importer hashes and five original Unity-created folder identities are preserved. Three filename collisions keep separate assets with short GUID prefixes.
- [x] Existing content owner performs bounded, explicit Unity moves with hash/dependency comparisons, literal shader-path diagnostics and exact-path texture import scope. Refreshed stale source ShaderGraph imports before dependency comparison; no missing-link check was relaxed. Existing game-content ignore/export separation remains intact.
- [x] Corrected seven imported texture settings through the existing policy, including linear sampling for observed packed masks. Added missing physical-unit declarations for appended Weather Output recipes and World Graph Input observations/search distance. Repaired stale GPU expectations after the existing CPU packing migration and invoked real OnValidate in the deliberately inactive climate fixture.
- [x] Unity 6000.5.2f1 / URP 17.5.0 compilation succeeded. Source scan: 701 C# files, zero syntax/declaration errors. Targeted diffs reviewed against exact recovery originals because the UC tree is untracked; no RCM, package/version, cache or hand-edited metadata changes.

Frozen source: `.work/UC_T06_clean_snapshot_20261119_002414415.json`, 3,101 files, SHA-256 `68A05B28D9212A9D1791BE6D95D5BD677C5FC882275AC9CD246C6E9F1C92F94C`. Freeze report: `Logs/UC_ISOLATED_SINGLE_SAMPLE_FOLDER_FINAL_FREEZE_20261119.log`. Isolated project: `.work/UC_validation_project_T06_CLEAN_20261118_213526359/`, with its Logs junction verified to main Logs.

| Gate | Checks | Failures | Exit | Launch RAM | PID | Main log |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| RunContentRepairBatch | 181 | 0 | 0 | 6.12 GiB | 24472 | Logs/UC_ISOLATED_FINAL_SAMPLE_LAYOUT_FOCUS_20261119_002414415.log |
| RunBatch | 19,183 | 0 | 0 | 6.00 GiB | 14020 | Logs/UC_ISOLATED_FINAL_SAMPLE_LAYOUT_FULL_20261119_002414415.log |
| RunSnapshotSceneReloadBatch | 9 | 0 | 0 | 6.09 GiB | 21920 | Logs/UC_ISOLATED_FINAL_SAMPLE_LAYOUT_SCENE_20261119_002414415.log |
| RunSnapshotDomainReloadBatch | 9 | 0 | 0 | 5.98 GiB | 2680 | Logs/UC_ISOLATED_FINAL_SAMPLE_LAYOUT_DOMAIN_20261119_002414415.log |

Each gate ran the following exact runner form, substituting its method and log prefix from the table:

```powershell
& 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe' -NoProfile -File 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261119_002414415.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch' -LogPrefix 'UC_ISOLATED_FINAL_SAMPLE_LAYOUT_FULL_20261119_002414415'
```

All four processes exited naturally; each shutdown reported no leaked weak pointers. Post-run VerifyOnly confirmed all source/package/version hashes, 118 recorded Unity-generated script metadata files and no foreign code. This is evidence for these lifecycle fixtures, not a universal native/GPU leak guarantee.

Final grouping report: `Logs/UC_ISOLATED_SINGLE_SAMPLE_FOLDER_MAIN_20261119_002219560.log`. Earlier dependency-verified flatten report: `Logs/UC_ISOLATED_CATEGORY_FLATTEN_IMPORT_REPAIR_20261119_001134853.log`; recovery originals remain outside Assets under `.work/UC_category_flatten_originals_20261119_001545447/`.

- [ ] Needs a manual check in the Unity Editor: imported prefab/model/material appearance, Play mode, sky/exposure/fog, Player shader/build behavior, Redmi Note 14 performance and general native/GPU resource lifetime. No visual/device result or complete Topic 8 completion is claimed.
- [ ] Next active task: UC-T08-07. Earlier clock, climate and regional weather fixtures passed this interim full suite; Topic 8 remains open until its remaining tasks and complete-topic acceptance are handled.
