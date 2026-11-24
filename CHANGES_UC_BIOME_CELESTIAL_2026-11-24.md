# UC Biome Splat and celestial repair — 2026-11-24

**Climate compiler follow-up — code/package and publication verified:** repair tasks 26/27 confirm that the new host test is paired with older runtime files lacking three methods already shipped on the repair branch; no duplicate climate script should be removed. The complete 17-file follow-up is supplied with a hash manifest and [matching installation instructions](docs/UC/UC_CLIMATE_COMPILER_UPDATE_2026-11-24.md). Fresh isolated compilation/streaming passed 193/0/exit 0; full suite passed 19,882/0/exit 0. Snapshot `UC_T06_clean_snapshot_20261124_132440741.json`, digest `DD01504E1956EE1551ED8795E9F6725CD7092B5B9CB2931A2BD87A68AF5EA0F8`, passed final VerifyOnly for 3,134 sources/pins and 156 generated metadata hashes. Exact commands/logs/RAM and the unconfirmed first-start delay are in the repair MD. Task 28 is complete: documentation commit `abe03ecc6e2de6a2bae7ef2dfc7dbac89dd5c49c` was pushed to `fix/uc-biome-celestial-2026-11-24` and independently confirmed with `git ls-remote --heads`; main remains `30f8dcd92751180de2b5f55db25ebdff7c3c0735`. Completion records use that same repair branch. No new production API, test suppression, metadata/host write or PDF change is introduced. Owner installation/host compilation remain manual acceptance.

Tasks 13–18 in [the repair record](docs/UC/UC_SCENE_PLAY_LOD_REPAIR_2026-11-22.md) describe the earlier verified repair and publication below. The later climate/memory/celestial tasks 19–25 are complete with automated acceptance, existing PDF refresh and verified repair-branch publication; their evidence follows the historical record. The user's separate game project remains read-only; UC-T09-04 is not advanced. Owner stop condition reached.

Biome Splat Rules mixed node-drag pointer handling with nested serialized-list controls; inherited label widths clipped controls, and generic new rows could have duplicated or unusable defaults. Explicit scrollable rows now use existing Undo/dirty/generation dispatch, preserve independent struct edits, guard stale callbacks and support add/remove/reorder/reload. Worker-local validation handles invalid/nonfinite values and bounded normalization without modifying saved rules. Palette precedence and duplicate-biome semantics remain intact. Native terrain regressions protect heights, holes, sculpting, rivers, collision and cancellation/readiness ownership; this repair did not change production ChunkManager. The host's reported disappearing surface/crash was not reproduced and is not claimed as proven deletion.

Sun/full/crescent moon edges now use chord-distance antialiasing; saved angular diameters remain independent of world size. Daylight is filtered once by the existing atmospheric transmission instead of an extra altitude-dependent temperature ramp. Catalog-backed night stars use the existing game clock/location, twilight, moonlit exposure and cloud composite. Actual Inspector location: Lighting System Manager → Sky and Atmosphere → Enable Stars / Star Brightness (default 1, range 0–4). Stars remain absent in daylight. BSC5 provenance, memory ownership and sky-model limits are documented with the retained catalog.

Changed owners: `UCNodeView.cs`, `UCGraphStyles.uss`, `BiomeSplatOutputNode.cs`, `UC_Skybox.shader`, `SkyModel.cs`, `StarSky.cs`, `ClockMath.cs`, `LightingSettings.cs`, Lighting System driver/view owners and `UCSystemManagerInspectors.cs`, with registered graph/texture/streaming/lighting/star regressions. Exact findings and failed-attempt corrections are retained in the repair record. No RCM production code, host scenes or manual metadata edits belong to this repair.

Final frozen snapshot: `.work/UC_T06_clean_snapshot_20261124_071735329.json`, 3,133 source files, SHA-256 digest `EA396784A2EFED23EFA7E05800E25D45D93755E7E955DD4023FC429B86AE49F5`. Final source/package pins and 155 Unity-generated metadata hashes passed verification.

| Gate | Checks / failures | Exit |
|---|---:|---:|
| Graph | 155 / 0 | 0 |
| Texture | 513 / 0 | 0 |
| Lighting | 1,597 / 0 | 0 |
| Streaming | 180 / 0 | 0 |
| Full self-test | 19,862 / 0 | 0 |
| Scene reload | 18 / 0 | 0 |
| Domain reload | 9 / 0 | 0 |
| Sky shader, Direct3D 12 / feature level 12.1 | 3 / 0 | 0 |

All launches had fresh free RAM above 4 GiB and bounded isolated execution. Exact helper commands, log names and launch RAM are in the repair record. Focused graph/texture/lighting checks preceded fixture-only corrections; production was unchanged afterward, and the final full suite includes these fixtures. Completed shutdowns report no leaked weak pointers. Existing unequal-resolution restoration, legacy migration and locked-pose fixture warnings remain intentional; no warning/assertion was hidden to obtain success. Reviewed tails have no C# or sky-shader compiler errors/warnings.

Both updated PDFs are together in `docs/WorldGuides/Updated_PDFs/2026-11-24`: Green Coast V5.5, 75 pages; Rift Highlands V6.5, 79 pages. All 1,915 recipe checks and PDF content/port/layout checks passed. Updated lighting and Rules pages were visually inspected; delivered hashes match checked candidates. The distinct 59/64-node recipes retain four complete port-labelled flowcharts per world. No HTML is delivered.

Needs a manual check in the Unity Editor: host Rules UI at actual zoom/window sizes, terrain/camera/material visibility after edits and reload, sunrise/noon/sunset brightness, sun/moon discs and night stars under clear/full-moon/cloud conditions in Scene and Game views. Player/GPU/device behavior and general leak freedom are not certified by these passes.

Publication retains the supplied repository's history and maps `Assets/UniverseCreator/` to its existing `UniverseCreator/` root. Its remote baseline is older than the workspace; publication is a verified current UC checkpoint containing earlier upgrades, not a claim that thousands of differences were introduced by this repair. Secrets, RCM, logs, caches and unrelated projects are excluded.

The owner's `.gitignore` audit caught an error in the initial unpublished checkpoint: ignored game-only `Content/` assets had been included (762 files / 605.91 MiB, plus folder metadata). The upload was stopped before the branch appeared remotely. The correction removed only their Git index entries, retained local assets and installed mapped ignore rules. Corrected checkpoint `5bc785937c5168383568fb9e5372c2775fc58c81` contains 1,504 changed files; retained UC/PDF files total 19.37 MiB. No ignored files remain tracked. Product source, delivered PDF hashes and passing Unity gates are unchanged.

`git push --set-upstream origin fix/uc-biome-celestial-2026-11-24` exited 0. `git ls-remote --heads` confirmed that checkpoint on [the requested repair branch](https://github.com/Phoenix-229/Universal_Creator.Unity-sub_Engine/tree/fix/uc-biome-celestial-2026-11-24), with main unchanged at `30f8dcd92751180de2b5f55db25ebdff7c3c0735`. Final evidence records are committed on that same branch. No force-push, merge or main commit was performed.

## Climate recovery, river memory and smaller stars — verified follow-up

Strict climate existed only in transient accepted fields; after reload a required terrain capture could fail after native replacement had begun. A drained failed queue could still run water/hydrology completion. Accepted climate now has a saved immutable record with schema/sample/hash validation. `ChunkManager` prepares/captures prerequisites before replacing terrain or clearing accepted vegetation, records one failure per bake, retires completion callbacks, and resumes cold recovery through its existing pump. Public explicit rebuilding retries after correction. Saved acceptance remains independent of pending graph edits. Unity's empty serialization of absent masks is accepted only with zero metadata; corrupt populated masks remain rejected.

River hierarchy scratch previously charged every solve pixel, and reference-array accounting rejected valid identity grids. Selection now charges actual reaches and releases its scratch before publication; one immutable ID is shared per reach. Bounded string-array traversal preserves recursive object/depth limits. A real 512² solve and combined native height/biome/channel upload pass within the unchanged 256 MiB budget, preserve heights and release admitted memory. Procedural trenches and manual river-water ownership remain separate and unchanged.

Stars use 2048×1024 sampling and captured atmospheric extinction near the horizon. Retained nominal pixels are 16 MiB; temporary colours are 32 MiB and not retained. Catalog positions/magnitudes, Enable Stars, Star Brightness, twilight/day suppression, moonlit exposure and existing cloud compositing remain intact. Sun/moon angular defaults remain 0.53°/0.52°; moving directional lights farther away does not change apparent size or ground lighting. No speculative intensity/size reset or terrain recolouring was made. Exact host white glare and celestial appearance remain manual checks.

Changed owners for this follow-up: `ClimateBaselineProduct.cs`, `UCWorldGraphBindings.Lighting.cs`, `ChunkManager.cs`, `EditModeChunkDriver.cs`, `ProductMemoryFootprint.cs`, `RiverNetworkNodes.cs`, `StarSky.cs`, `UCLightingSystemManager.Driver.cs`, `UC_Skybox.shader`, catalog README, registered climate/memory/star/streaming/reload tests and matching guide chapters. No RCM, host project, dependency/version or manually authored metadata change.

| Gate | Checks / failures / OS exit |
|---|---|
| Streaming | 193 / 0 / 0 |
| Lighting | 1,602 / 0 / 0 |
| Full self-test | 19,882 / 0 / 0 |
| Actual scene reload | 26 / 0 / 0 |
| Actual domain reload | 11 / 0 / 0 |
| Direct3D sky compilation | 3 / 0 / 0 |

Exact commands/logs/RAM/PIDs and failed-fixture corrections are in the repair MD. First four gates used snapshot `UC_T06_clean_snapshot_20261124_103710297.json`, digest `747E848265D5AE1194BC9F3D06625898A19EF8D3E19707958D28D909AD9F52DC`. Final domain/graphics snapshot `UC_T06_clean_snapshot_20261124_111445663.json`, digest `DD01504E1956EE1551ED8795E9F6725CD7092B5B9CB2931A2BD87A68AF5EA0F8`, differs only in the isolated domain fixture. All 3,134 source files/pins and 156 generated metadata hashes passed final VerifyOnly. Launch RAM was 6.22–6.91 GiB; all processes exited naturally and reported no leaked weak pointers. Expected negative-test errors remain visible; no compiler/test assertion was suppressed.

The original host disappearance remains unreproduced; the logs prove failed recovery rather than terrain deletion or fighting output nodes. Auto Generate's original state is uncertain. Needs a manual check in the Unity Editor: host Rules/terrain recovery, Scene/Game camera/material visibility, sunrise/noon/sunset glare, sun/moon saved overrides, clear/full-moon/cloud/haze stars, Player/GPU/device performance. Epic exposure and earlier SCS/Epic references support checking shared metering, tone mapping and material albedo together; their recorded limitations remain intact.

Both existing PDFs were refreshed in their related chapters and delivered together in `docs/WorldGuides/Updated_PDFs/2026-11-24_Climate_Recovery`: V5.5 75 pages and V6.5 79 pages. Initial recipe checks 1,915/0; final manifest 2,251 builder contract checks. PDF inspection passed 663/699 collected coverage terms (349/365 unique), all four port-labelled diagrams per world and visual inspection of updated lighting/climate/port pages. Delivered SHA-256: V5.5 `417A39F2745A6511E92928835AF07F01C569AADBA7F2C441F2723AA03DFC472D`; V6.5 `21C7C82787AE39CA0434DF8D4BFDD960D6998FAAB5D06611AB4D638C19F831E4`. The older V5.5 copy was viewer-locked; this new folder is the authoritative pair.

Exactly 23 hash-matched verified UC/document/PDF files were explicitly staged and committed as `dbbd58ab16cd0d951bcf49ee23df855e6d393292` on the existing `fix/uc-biome-celestial-2026-11-24` branch. Staged whitespace checks passed and no ignored paths were tracked. Git's supported `--sparse` option handled new document paths outside the checkout definition. `git push origin HEAD:refs/heads/fix/uc-biome-celestial-2026-11-24` exited 0; `git ls-remote` verified that exact commit and unchanged main `30f8dcd92751180de2b5f55db25ebdff7c3c0735`. Final evidence records are delivered on the same repair branch. No Unity rerun is needed for this documentation-only finish. Manual host visual/device limits above remain open; all requested implementation/automated delivery tasks are complete, and work stops here.
