# UC Topic 8 — Lighting Graph automated acceptance

- [x] ✅ UC-T08-01…13 implemented and covered by the Topic 8/source/full-suite acceptance below. Existing clock, climate, weather, lighting and wind owners remain authoritative.
- [x] ✅ UC-T08-14 automated code/native gate completed on 2026-11-19. Assembled-game visual and device acceptance remains open.
- The sky LUT stores physical radiance with a texture-wide power-of-two exponent, avoiding RGBAHalf overflow without clipping daylight brightness. The shared sky/reflection reader decodes it. GPU LUT: 4 KB; retained CPU bake buffer: 8 KB per bounded cache.
- The final full-suite failure was an inactive graph fixture with no native sun. Its authored directional light now receives actual accepted graph exposure; the consumer assertion and production creation restrictions remain intact.

## Executed checks

Unity 6000.5.2f1, URP 17.5.0. All logs and console reports are directly in the main `Logs` folder.

| Method | Checks | Failures | Exit | Launch RAM GiB | PID | Log prefix |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| RunLightingBatch | 1,373 | 0 | 0 | 6.29 | 22116 | UC_ISOLATED_T08_SKY_STORAGE_TOPIC_20261119 |
| RunGraphDomainsBatch | 137 | 0 | 0 | 6.89 | 1272 | UC_ISOLATED_T08_FINAL_FIXTURE_GRAPH_20261119 |
| RunBatch | 19,402 | 0 | 0 | 6.92 | 14928 | UC_ISOLATED_T08_FINAL_FIXTURE_FULL_20261119 |
| RunSnapshotSceneReloadBatch | 9 | 0 | 0 | 7.08 | 12060 | UC_ISOLATED_T08_FINAL_FIXTURE_SCENE_20261119 |
| RunSnapshotDomainReloadBatch | 9 | 0 | 0 | 6.77 | 17812 | UC_ISOLATED_T08_FINAL_FIXTURE_DOMAIN_20261119 |

The lighting gate tested snapshot `.work/UC_T06_clean_snapshot_20261119_202644885.json`, digest `27EF81B41C7BBF71F7C2B2649CEEAA4E644657E4E22806A5A4CD790DD3F4ADCA`. Only the inactive graph fixture changed before the affected graph/full/reload gates: snapshot `.work/UC_T06_clean_snapshot_20261119_205253245.json`, 3,114 files, digest `A7BA0454534DBE5D6115052A25E68F95B2F7F84E95DD9D1F9AE5EDE4C3D8D2CD`. Source scan: 714 files, zero syntax/declaration errors. VerifyOnly passed unchanged source/package/version hashes after all runs. Processes exited naturally and reported no leaked weak pointers; this is not proof of all native/GPU/device leak freedom.

Exact launch form for each table row (replace method, snapshot and prefix with the corresponding values above):

```powershell
& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261119_205253245.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch' -LogPrefix 'UC_ISOLATED_T08_FINAL_FIXTURE_FULL_20261119' | Tee-Object -FilePath 'Logs/UC_ISOLATED_T08_FINAL_FIXTURE_FULL_20261119_CONSOLE.log'
```

## Remaining manual acceptance

- [ ] Needs a manual check in the Unity Editor: rendered dawn/noon/night, polar skies, regional clouds/rain/fog/wind, overlapping camera/world restoration and GPU histories.
- [ ] Player build and sustained Redmi Note 14+ performance/memory captures with declared settings.
- [ ] Existing obsolete Light.cookieSize warnings remain; batch success does not remove them.

## UC-T08-15 closure — 2026-11-20

- [x] ✅ Existing hydrology/carve owners reject occupied basins before native placement; freeboard preserves accepted water interiors and river relief. Eleven cached formation families, manual selection, saved edits, graph Hand/Pointer modes and vegetation consumer repairs retain existing authority.
- Code checks: terrain/river 530/0; full 19,498/0, both exit 0 on digest `B3FBFDA95D152355F6C43E71B68FFBE9F9B60B7A2BB2E47B0EC133AFB63A518C`. Affected water 1,518/0, vegetation 430/0, graph 142/0 and unchanged-production terrain scene 24/0, snapshot scene/domain 9/0 each are recorded with their snapshots in the Task 15 evidence ledger. No leaked weak pointers reported; no complete GPU/device leak claim.
- Guides: `docs/WorldGuides/UniverseCreator_World_Build_V5.5.pdf` (255 pages, Green Coast) and `UniverseCreator_World_Build_V6.5.pdf` (264 pages, Rift Highlands) build independent worlds. New `guide_chapters.cjs` supplies all four graphs, inspectors, surfaces, licensed content, exact wiring and scaled-clock/Scene-preview steps; the existing builder now preserves identical recipe bytes and paginates long tables. `render_world_guides.cjs` uses installed Edge and closes its owned browser; `check_world_guides.py` verifies the resulting PDFs.
- Unity recipe command: `& 'C:/tmp/run_UC_T06_clean_gate_20261117.ps1' -Snapshot 'C:/Users/Admin/Desktop/Universe Builder/.work/UC_T06_clean_snapshot_20261119_234607144.json' -ExecuteMethod 'UniverseCreator.Editor.Diagnostics.UCWorldGuideContracts.RunRecipeBatch' -LogPrefix 'UC_ISOLATED_T08_15_GUIDE_RECIPES_20261119_J' -MinimumFreeGiB 4.7`: 2,005/0/exit 0, RAM 6.91 GiB, digest `82E95F2BA032D450335C8F138532E6ADF21837207CAE2A357EAC202E640F4787`.
- Document checks: Node syntax checks passed; `node docs/WorldGuides/build_world_guides.cjs` 1,675/0; `node docs/WorldGuides/render_world_guides.cjs` exit 0; `python -X utf8 docs/WorldGuides/check_world_guides.py` 431 coverage checks passed with no blank pages, missing footers or text outside page bounds. Representative pages inspected visually. All reports are .log files directly under main Logs. Final Edge PID 15436 exited naturally, launch RAM 6.43 GiB.
- Matched tectonic sample table cost 47,807 versus 48,571 analytic ticks at 65,536 samples, maximum fraction error 0.00001549721, sampling allocation zero bytes; no general throughput/device improvement claimed.

Needs a manual check in the Unity Editor: assembled river/lake/coast appearance, eight-layer URP AddPass material response, gameplay traversal and renderer histories. Player builds, real-device memory and sustained thermal performance remain open. Original V5.4/V6.4 PDFs remain unchanged. No RCM, package/version or locked road/tunnel changes. Next authorized work: UC-T09-01 through the existing device/performance owners.
