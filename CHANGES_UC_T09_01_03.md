# UC-T09-01–03 — verification and world-guide update

Continuation note, 2026-11-20: the owner subsequently authorized continued ordered coding. Historical stop statements in this completed verification record are superseded; active work is UC-T09-04 in the audit checkpoint. The executed results below remain evidence only for their tested snapshot.

- [x] ✅ UC-T09-01: selected-world capture/context implementation and automated contracts verified.
- [x] ✅ UC-T09-02: bounded generation/performance capture implementation and automated contracts verified.
- [x] ✅ UC-T09-03: accepted native readiness and asynchronous host landing implementation verified.
- [ ] Whole Topic 9: **3/11 code tasks complete**; tasks 04–11 and separate manual/device acceptance remain open. Owner requests stopping after task 03.

- [x] Owner-requested focused/full verification and PDF update completed on 2026-11-20. Stop here; UC-T09-04 is not part of this review.
- [ ] Topic 9 and task release acceptance remain open for actual Editor/Player traversal and device measurements. Universe Creator is the PCG subengine; the host owns actors, loading and whole-application frame pacing.

## Corrections and guide changes

The first focused launch exposed two invalid fixture constructions of Unity's native `TerrainLayer` and a conflicting local name. `UCSelfTest.ChunkReadiness.cs` and `UCSelfTest.FramePerformance.cs` now use `new TerrainLayer()`; the readiness fixture uses `cellGround` for its loop-local object. No checks were skipped or validation loosened.

Reviewed the existing selected-world detached capture and native chunk-readiness implementation. Both independent guides need its operational workflows: select Capture World and bounded report labels, use Generation Metrics and Play-mode timing controls, and integrate asynchronous landing through the host game. `guide_chapters.cjs` adds chapters 19a–19c; `build_world_guides.cjs` records current acceptance; `check_world_guides.py` verifies the new coverage and exports representative pages. Native timing collection is automatic when supported, not an inspector toggle. Recipes and their graph connections are unchanged.

The audit checkpoint, Topic 9 evidence record and Topic 8 guide ledger now distinguish automated code verification from outstanding device/visual acceptance and record the owner's stop instruction. Historical 721-file/no-execution checkpoint is superseded by the evidence below.

## Executed checks

Unity **6000.5.2f1**, URP **17.5.0**. Isolated project: `.work/UC_validation_project_T06_CLEAN_20261119_032459778`. No RCM, dependency, content, quality or build-setting changes; no Library deletion. Snapshot `.work/UC_T06_clean_snapshot_20261120_010838425.json`, **3,124 source files**, SHA-256 **C14C7CE1925FE7A0A8DE71A7754F4B9E3C0188BC0E4D06410E3C1B4DA79C63BD**.

| Check | Checks / failures / OS exit | Launch RAM / owned PID | Main Logs file |
|---|---|---|---|
| Initial focused compilation | No check count; compiler failure | 6.84 GiB / 3040 | `UC_ISOLATED_T09_01_03_FOCUS_20261120_0105.log` |
| Repaired RunStreamingReadinessBatch | **94 / 0 / 0** | **6.19 GiB / 12896** | `UC_ISOLATED_T09_01_03_FOCUS_REPAIR_20261120_0108.log` |
| Full RunBatch | **19,530 / 0 / 0** | **6.70 GiB / 15724** | `UC_ISOLATED_T09_01_03_FULL_20261120_0111.log` |
| Final VerifyOnly after PDF generation | **3,124 unchanged source files/pins**, 143 recorded metadata hashes; exit 0 | No Unity launch | `UC_ISOLATED_T09_01_03_FINAL_VERIFY_20261120.log` |

The passing focused gate exercises capture-world ownership, native terrain/collision/water readiness, transaction publication, failure holds, sparse landing demand, restart and generation-metrics contracts. Source scan: **723 C# files / zero syntax/declaration errors**, retained source-helper log `UC_ISOLATED_T08_SOURCE_REVIEW_20261118.log`. Both passing Unity processes exited normally and reported `Found no leaked weakptrs.` This does not establish general native/GPU leak freedom. Pre-existing obsolete `Light.cookieSize` warnings remain; current snapshot has no compiler errors.

Exact focused and full commands:

```powershell
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261120_010838425.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunStreamingReadinessBatch' -LogPrefix 'UC_ISOLATED_T09_01_03_FOCUS_REPAIR_20261120_0108' -MinimumFreeGiB 4
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261120_010838425.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch' -LogPrefix 'UC_ISOLATED_T09_01_03_FULL_20261120_0111' -MinimumFreeGiB 4
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261120_010838425.json' -VerifyOnly -LogPrefix 'UC_ISOLATED_T09_01_03_FINAL_VERIFY_20261120'
```

## Final PDFs

| Separate world | Pages / coverage checks | PDF SHA-256 |
|---|---|---|
| [V5.5 Green Coast](docs/WorldGuides/UniverseCreator_World_Build_V5.5.pdf) | **258 / 225** | `6ECA467737BBA06C6F41D278989775BE5EDE7814E20108F49BB3BFB3CCBF955E` |
| [V6.5 Rift Highlands](docs/WorldGuides/UniverseCreator_World_Build_V6.5.pdf) | **267 / 236** | `456122DFF15636B4393319B5CD13E800D6CE9A1772980FE99434C18D2DDAC8D9` |

`node --check` passed for the edited chapter and builder scripts. `node docs/WorldGuides/build_world_guides.cjs` passed **1,675 offline recipe contracts**. `node docs/WorldGuides/render_world_guides.cjs` generated both PDFs with no horizontal overflow or tall sections; RAM **6.62 GiB**, C: **6.47 GiB** free before rendering. Owned Edge PID **6920** exited normally with code 0. `python docs/WorldGuides/check_world_guides.py` passed both PDFs with no blank pages, missing footers, outside-page text or missing coverage terms. Capture/metrics/landing representative pages were visually inspected. Logs remain directly under main `Universe Builder/Logs`: `UC_ISOLATED_T08_15_GUIDE_PDF_RENDER_20261120.log` and `UC_ISOLATED_T08_15_GUIDE_PDF_INSPECTION_20261120.log`.

The unchanged two recipes retain historical Unity recipe acceptance **2,005/0/exit 0**, log `UC_ISOLATED_T08_15_GUIDE_RECIPES_20261119_J.log`; that result does not demonstrate a rendered full-world build. Originals V5.4/V6.4 in Downloads remain unchanged.

## Manual acceptance still required

- [ ] Needs a manual check in the Unity Editor: inspector/Undo, Play-mode capture and host landing hold, stationary/re-entry/restart traversal, coherent water/vegetation over accepted collision, graphless ground and multiple-world scenes.
- [ ] Needs a manual check in the Unity Editor: assembled terrain/material/sky/day-night appearance, reserved surface layers 4–7 through URP AddPass, native/GPU lifecycle and visual realism.
- [ ] Actual Player/device checks: build support, correct runtime hardware/API identity, CPU/GPU/memory telemetry, matched-content cold/warm/edit runs and sustained thermal performance. Headless batch tests do not certify the owner's declared Redmi device or a complete gameplay-ready world.

No locked RCM rule was changed. The next ordered implementation would be UC-T09-04, but the owner requests stopping now.

## Subsequent cookie-size warning repair — 2026-11-20

Task-status alignment after that repair: the audit and Topic 9 ledger now tick tasks 01–03 as verified code completion. Focused contracts passed **94/0** and the latest full suite passed **19,531/0**, both exits 0. This documentation-only alignment adds no execution evidence and does not claim real-device or complete-topic acceptance; no unchanged Unity gate was rerun.

- [x] ✅ Replaced all four obsolete `Light.cookieSize` calls using the installed Unity `Light.cookieSize2D` API; no deprecated cookieSize references remain in UC C# sources.
- `UCLightingSystemManager.Clouds.cs`: keeps the existing square cloud projection and scale clamp through a two-dimensional size.
- `UCLightingSystemManager.Views.cs`: captures/restores both dimensions of an incoming light cookie, including rectangular authored projections.
- `UCSelfTest.EnvironmentEffects.cs`: checks both square cloud dimensions and actual view-end restoration of a 384 × 768 incoming projection.

Snapshot `.work/UC_T06_clean_snapshot_20261120_015957065.json`, **3,124 files**, SHA-256 `01AD03885B207DE3A4CF978E3CA559401F8AE457ADFCAC34BBED43FA0FA2B2B6`.

| Check | Checks / failures / OS exit | Launch RAM / owned PID | Main Logs file |
|---|---|---|---|
| RunLightingBatch | **1,379 / 0 / 0** | **6.10 GiB / 19136** | `UC_ISOLATED_COOKIE_SIZE_LIGHTING_20261120_0201.log` |
| Full RunBatch | **19,531 / 0 / 0** | **6.37 GiB / 22460** | `UC_ISOLATED_COOKIE_SIZE_FULL_20261120_0203.log` |
| Final VerifyOnly | **3,124 unchanged source files/pins**, 143 generated metadata hashes; exit 0 | No Unity launch | `UC_ISOLATED_COOKIE_SIZE_FINAL_VERIFY_20261120.log` |

Exact commands:

```powershell
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261120_015957065.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunLightingBatch' -LogPrefix 'UC_ISOLATED_COOKIE_SIZE_LIGHTING_20261120_0201' -MinimumFreeGiB 4
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261120_015957065.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch' -LogPrefix 'UC_ISOLATED_COOKIE_SIZE_FULL_20261120_0203' -MinimumFreeGiB 4
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261120_015957065.json' -VerifyOnly | Tee-Object -FilePath 'Logs/UC_ISOLATED_COOKIE_SIZE_FINAL_VERIFY_20261120.log'
```

Both owned Unity processes exited normally with `Found no leaked weakptrs.` No `warning CS` or `error CS` diagnostics appeared in either checked 4,000-line log tail. Targeted diff review found only the intended API and regression-check changes; original encoding/BOM and line endings are preserved. Freeze report: `Logs/UC_ISOLATED_COOKIE_SIZE_FREEZE_20261120.log`. This API repair does not change guide-facing controls or world-build steps; both PDFs remain current for it.

The owner reports manually closing import workers before transport errors 10054; no product defect has been established for those messages. Needs a manual check in the Unity Editor: visible cloud shadows and multi-camera light restoration. General native/GPU leak freedom and Player/device results are not established by these batch passes. No locked rule changed; historical obsolete-warning results above refer to the earlier snapshot. Stop after this verified repair as instructed.
