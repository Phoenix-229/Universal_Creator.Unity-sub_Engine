# UC-T05 — four shared graph windows and recoverable migration

Code and automated acceptance completed on 2026-11-17. Editor interaction, graphics and device acceptance remain open.

The graph domains previously shared mixed authoring without complete companion ownership, preview retirement or a recoverable migration workflow. Node, Vegetation, Lighting and Texture windows now use the same canvas, catalog, evaluator, snapshot and scheduling services with explicit domains and Run/Apply boundaries.

- One inlet connection and multiple outgoing connections remain supported. Domain palettes expose relevant utilities, combiners and typed portals; legacy authored nodes/wires remain loadable.
- Companions consume pinned accepted ground, water, climate and material products. Missing, foreign or stale inputs preserve the last applied output.
- Selected field previews debounce, use bounded shared worker admission and retire captured inputs after cancellation or canvas closure. Actual node timings are recorded separately from previews.
- Read-only migration analysis preserves IDs, settings, seeds, prototypes and wire order. Native replacement creation switches references with Undo and rolls back failed writes. Arbitrary shared intermediates and conflicting companion ownership require explicit resolution.
- The shared four-graph starter uses existing terrain/river, scatter, environment and material owners. Buyer content slots stay empty; read-trench rivers use the existing node-placement workflow after accepted terrain generation.
- Arrays, nested prototypes and scalar keyboard edits use the shared native serialization/Undo path.

The interrupted gate had two distinct faults: migration used executable-output validation for empty authoring companions, and sample-test teardown restored an empty scene setup. Both are corrected. Added preview tests exercise actual worker cancellation and closure after admission.

Changed owners include `Core/Editor/Graph/{UCGraphView,UCNodeView}.cs`, shared window/catalog utilities, `UCWorldGraphBindings` and its product/environment owners, the graph evaluator/metrics, domain migration/preview utilities, `FourGraphSample.cs`, and registered diagnostics. This final resume changed `UCSelfTest.GraphIntegration.cs`, `UCSelfTest.GraphPreviews.cs` and the active audit/checkpoint. Originals are retained under `.work/UC_T05_gate_resume_20261117/`; earlier implementation originals remain in the directories named by the audit.

Validation used Unity 6000.5.2f1 and URP 17.5.0 in:

`C:\Users\Admin\Desktop\Universe Builder\.work\UC_validation_project_T03_20261115_232021464`

Frozen 671-file SHA-256: `7168FE719BB9109023C6217B4ACF69F011C441E71E881AE31D66998803E86974`, recorded in `.work/UC_T05_preview_lifecycle_snapshot_20261117.json`. After all runs, current/copied source inventories and every source/manifest/lock/version hash matched.

| Method on `UniverseCreator.Editor.Diagnostics.UCSelfTest` | Checks / failures / OS exit | Log under workspace `Logs/` | Launch free RAM / PID |
|---|---|---|---|
| `RunGraphDomainsBatch` | 127 / 0 / 0 | `UC_ISOLATED_T05_GRAPH_LIFECYCLE_20261117.log` | 5.40 GiB / 19012 |
| `RunBatch` | 18,167 / 0 / 0 | `UC_ISOLATED_T05_PACKAGE_LIFECYCLE_20261117.log` | 5.19 GiB / 12480 |
| `RunSnapshotSceneReloadBatch` | 9 / 0 / 0 | `UC_ISOLATED_T05_SNAPSHOT_SCENE_20261117.log` | 6.03 GiB / 26592 |
| `RunSnapshotDomainReloadBatch` | 9 / 0 / 0 | `UC_ISOLATED_T05_SNAPSHOT_DOMAIN_20261117.log` | 5.89 GiB / 31348 |

Exact complete-package executable command:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe' -batchmode -nographics -projectPath 'C:\Users\Admin\Desktop\Universe Builder\.work\UC_validation_project_T03_20261115_232021464' -executeMethod UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch -logFile 'C:\Users\Admin\Desktop\Universe Builder\Logs\UC_ISOLATED_T05_PACKAGE_LIFECYCLE_20261117.log'
```

Other commands use the same executable/options/project with the exact method and log in the table. Launches used `Start-Process -WindowStyle Hidden -PassThru`, guarded immediately by at least 5 GiB free physical RAM. OS exits were observed; no Unity process or lock was removed. Every shutdown tail reported `Found no leaked weakptrs.` The domain run also logged a UnityConnect configuration-request timeout after its passing report; the gate and OS exit remained successful.

Needs a manual check in the Unity Editor: visible keyboard/mouse editing, window/picker/clipboard navigation, preview latency and memory, real starter-content setup, and terrain/water/vegetation/material/weather appearance under graphics. Player builds and sustained Redmi Note 14-class high-detail 30/60/supported-120 FPS measurements remain separate acceptance. Headless checks do not establish these results.

RCM code, its interactive work, locked road placement, project versions and licensed content were preserved. No research downloads were added. Topic 6 continues with the vegetation ownership inventory before migration or placement changes.
