# UC Biome Splat and celestial repair — 2026-11-24

Tasks 13–18 in [the repair record](docs/UC/UC_SCENE_PLAY_LOD_REPAIR_2026-11-22.md) have verified implementation, automated acceptance, PDF delivery and requested Git publication. The user's separate game project remains read-only; UC-T09-04 is not advanced. Stop after this delivery.

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
