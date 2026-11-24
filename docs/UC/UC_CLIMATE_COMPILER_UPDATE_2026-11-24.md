# Matching the climate tests and runtime — 2026-11-24

The eight reported CS1061/CS0117 errors come from a mixed installed revision. `UCSelfTest.ClimateRestore.cs` calls three methods that are missing from the installed runtime but already exist in the published repair. No duplicate climate script was found and none should be removed to address these errors.

The verified source revision is `dbbd58ab16cd0d951bcf49ee23df855e6d393292` on [fix/uc-biome-celestial-2026-11-24](https://github.com/Phoenix-229/Universal_Creator.Unity-sub_Engine/tree/fix/uc-biome-celestial-2026-11-24). Later record-only commits do not change these source bytes.

## Apply the matched update

The local update folder is `C:/Users/Admin/Desktop/Universe Builder/.work/UC_climate_compiler_update_20261124`. Its `Assets/UniverseCreator` folder contains the complete 17-file climate/memory/celestial follow-up, unchanged from publication, with `manifest.json` outside Assets recording SHA-256 hashes. This folder is a delivery copy, not another Unity project. It is ignored by Git; the source files remain tracked on the repair branch.

1. Close the Unity Editor before updating the game project. Preserve a copy of the current files so any deliberate local edits can be reviewed.
2. Merge the contents of the update's `Assets/UniverseCreator` into the game's existing `Assets/UniverseCreator`, replacing the same named files listed below. Do not add a second `UniverseCreator` folder alongside it. If using GitHub instead, map repository `UniverseCreator/...` to game `Assets/UniverseCreator/...`.
3. Keep the game's existing `.meta` files, scenes, prefabs, saved graph assets and Content. The update includes none of those files. Do not delete Library or remove the climate self-test. All four `UCWorldGraphBindings` partial source files belong to the same class and remain in place.
4. Open Unity and allow compilation to finish. Confirm the eight missing-member errors are gone before running terrain generation. If another compiler error appears, retain its exact message; do not remove a script or disable a test based on its filename.
5. Check accepted terrain after scene reload and Play entry, then deliberately regenerate once to check early climate preparation. Compiler acceptance is separate from the actual host's visual terrain/lighting result.

The complete follow-up is supplied because changing only the new test or two runtime declarations would leave the related recovery, memory and rendering callers/tests on different revisions. No API stub or relaxed prerequisite is used. Existing height, river and sculpting behavior remains covered by the real regression owners.

## Files in the update

Every path below is relative to `Assets/UniverseCreator`. Each file replaces its corresponding existing file; no alternative copy is installed.

| File | Role |
|---|---|
| `Core/Runtime/Terrain/UCWorldGraphBindings.Lighting.cs` | Defines `PrepareTerrainClimateAsync`; persists and restores accepted early climate. |
| `Core/Runtime/Weather/ClimateBaselineProduct.cs` | Defines `SaveAccepted` and `RestoreAccepted`, with schema, finite-value and hash validation. |
| `Core/Runtime/Terrain/ChunkManager.cs` | Prepares the climate prerequisite before replacement and retires failed work honestly. |
| `Core/Editor/Utilities/EditModeChunkDriver.cs` | Resumes the existing cold-recovery pump. |
| `Core/Runtime/Nodes/Base/ProductMemoryFootprint.cs` | Preserves bounded accounting for shared river identity arrays. |
| `Core/Runtime/Nodes/Selectors/RiverNetworkNodes.cs` | Charges scratch by actual reaches and shares immutable reach IDs. |
| `Core/Runtime/Lighting/StarSky.cs` | Retains the accepted bounded catalog sky sampling. |
| `Core/Runtime/Terrain/UCLightingSystemManager.Driver.cs` | Publishes the matching captured atmospheric star parameters. |
| `Shaders/UC_Skybox.shader` | Consumes those matching atmospheric star parameters. |
| `Resources/UniverseCreator/BSC5/README.md` | Records catalog provenance, memory and rendering limits. |
| `Core/Editor/Diagnostics/UCSelfTest.ClimateRestore.cs` | Tests actual preparation, saved acceptance and cold recovery. |
| `Core/Editor/Diagnostics/UCSelfTest.ProductMemory.cs` | Tests the matching bounded memory behavior. |
| `Core/Editor/Diagnostics/UCSelfTest.SnapshotDomainReload.cs` | Tests real domain reload with accepted climate. |
| `Core/Editor/Diagnostics/UCSelfTest.Stars.cs` | Tests matching catalog and atmospheric publication. |
| `Core/Editor/Diagnostics/UCSelfTest.StreamingReadiness.cs` | Registers climate and combined output regressions. |
| `Core/Editor/Diagnostics/UCSelfTest.TerrainRiverGate.cs` | Tests real scene reload and climate acceptance. |
| `Core/Editor/Diagnostics/UCSelfTest.cs` | Registers the corresponding full-suite checks. |

## Verification boundary

Fresh isolated verification passed: compilation plus climate/streaming **193 checks, zero failures**, then full suite **19,882 checks, zero failures**; both Unity processes exited 0. Final hash verification confirmed 3,134 unchanged sources/package pins and 156 generated metadata hashes. The package matches those tested source bytes. The first Unity launch had a startup delay; its cause is unconfirmed, so uninterrupted startup is not guaranteed by these results.

This session never writes to `Grand World Transit Simulator`. The frozen isolated copy verifies the matching source set; the local package is checked against that source and publication. Actual game-project compilation after installation, Scene/Game appearance and device behavior need a manual check in the Unity Editor. Detailed current evidence and task status are in [repair tasks 26–28](UC_SCENE_PLAY_LOD_REPAIR_2026-11-22.md). There are no new Inspector/node fields, so another PDF rebuild is unnecessary for this installation correction.
