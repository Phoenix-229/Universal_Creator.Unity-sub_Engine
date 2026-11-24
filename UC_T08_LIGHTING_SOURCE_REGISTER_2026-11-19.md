# UC-T08-13 — lighting and material comparison register

- [x] Public Epic/SCS comparison completed from the sources below. This compares published behaviour, not private engine internals.
- [ ] Latest Unity semantic compilation and Topic 8 execution: pending UC-T08-14 gates.
- [ ] Rendered calibration, GPU cost, refresh latency and target-phone acceptance: manual checks remain.

## Retrieved evidence

Retrieval date: 2026-11-19. All three requests returned HTTP 200. Captured responses are `.log` files directly in the main `Logs` folder. The pages are live documentation/articles, not source-code licences or exact release compatibility guarantees. No external implementation, shader, texture or material was copied.

| Source | Inspected passage | Captured response and SHA-256 |
|---|---|---|
| [Epic Material Inputs](https://dev.epicgames.com/documentation/en-us/unreal-engine/material-inputs-in-unreal-engine), Base Color / Metallic / Roughness | Base color represents diffuse reflection without specular highlights. Metallic is 0 for pure nonmetals and 1 for pure metals; mixed materials may use intermediate values. Roughness controls scattering of reflected light. | `UC_ISOLATED_T08_13_EPIC_MATERIAL_20261119_031541465.log`; `864ECEE8D02A3D3E348682D28DE51BD1E80D9E31325CEBC6DEEA3194B31BD51B` |
| [Epic Lumen Technical Details](https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine), Surface Cache / Lumen Platform Support | Surface-cache direct/indirect updates are amortized over multiple frames. The cited approximately 8 ms at 1080p is for next-generation consoles. Android support requires Vulkan desktop renderer SM5; the Mobile Renderer does not support Lumen. | `UC_ISOLATED_T08_13_EPIC_LUMEN_20261119_032134055.log`; `4139EA2CD8F9521B600A8AA812E76F93BECC88F9200EB47E873253A5CEDA14AF` |
| [SCS Under the Hood — New Lighting System](https://blog.scssoft.com/2021/03/under-hood-new-lighting-system.html), What's new? / material and summary passages, March 2021 | HDR precision, inverse-square attenuation, physical intensity calibration, dynamic exposure and appropriate albedo must work together. The article explicitly says this release is not yet full physically based rendering. | `UC_ISOLATED_T08_13_SCS_LIGHTING_20261119_032130162.log`; `ABF2A89E218C1DCE453DC6CCDCCABA1A1432FD1F724696DA40D58D9927ADC115` |

The first sandbox Epic request failed at transport level and created an empty response file. That empty file is not evidence; the successful response above supersedes it. The approved retrieval did not disable certificate validation.

## Independent UC implementation and limits

| Published idea / decision | Existing UC owner and actual connection | Cost, ownership and fallback |
|---|---|---|
| Calibrate material channels together with exposure; retain the physical separation of albedo, metallic and roughness. | `MaterialLayerParameterNodes.cs` converts authored finite roughness to `smoothness = 1 - roughness`; existing `SurfaceProduct`/material bindings deliver channels to UC shaders. `ExposureModel` and the lighting driver's captured environment apply one multiplier to sky, lights and fog. | No imported material assets or new graph authority. Surface work stays in the existing snapshot/commit lifecycle. Material rendering adds texture sampling/bandwidth; scalar defaults remain available. Artistic calibration needs rendered reference scenes. |
| Amortize changing indirect-light data rather than refill unchanged grids every frame. | UC-T08-12 retains `ProbeRefreshScheduler`, `RefreshCadence` and `UCLightingSystemManager.Irradiance.cs`. Accepted terrain publication stamps dirty the existing storage; observer distance orders the capped work. | UC uses a heightfield/ambient probe approximation, not Lumen's ray tracing or Surface Cache. Main-thread fills are bounded to 1…256 authored probes/frame, 1800 probes/real second, with up to three feedback sweeps. Volume packing/uploads remain indivisible and paced at 0.25 seconds when dirty. |
| Keep a cheaper tier and explicit platform limits. | Existing `LightingTier`, camera-owned SSR/SSGI/contact passes, and cached sky/ambient. Two cameras resolve their own world settings without mutating a shared pipeline asset. | Low tier disables screen-space effects. Whole-grid limits are 4096/8192/16384 probes; missing 3D/half formats, texture limits or excessive grids retain ambient. Native histories belong to at most eight cameras per feature. Quality transitions dispose disabled native histories. Shared pipeline shadow settings are not advertised as independent per-world assets. |
| Use one coherent environment instant for illumination and materials. | Existing weather clock, `EnvironmentSampleProduct`, `SunMoonRig`, sky caches and view adapters. Regional precipitation/wind/fog and wetness/snow reach existing runtime recipients. | Civil timezone display does not change the retained solar epoch. Weather sampling does not rewrite the early climate baseline or trigger stable vegetation placement. Bounded surface stores are local approximations; shelter geometry and continental weather simulation are not fabricated. |

## Evidence boundaries

The SCS July 2026 material article, Godot HLOD guidance and SIGGRAPH abstracts remain the inspected historical excerpts recorded in section 17 of the active plan; they are not newly fetched/hash-verified here. They support calibration/profiling principles, not reusable shader equations. R1–R4 keep their existing evidence; this register completes the selected R5 public lighting/material comparison.

Private SCS regional weather/sky algorithms, detailed Call of Duty implementations, exact external engine release contracts, and equivalence to Lumen remain unverified. Page availability does not grant permission to redistribute engine code or sample content. UC changes use existing independent code and the project's pinned Unity 6000.5.2f1 / URP 17.5.0 contracts. Semantic API compatibility must be established by the isolated Unity gate, not inferred from another engine's documentation.

No device performance claim follows from these sources. Target-phone API support, visual response after camera cuts/seeks, upload spikes, native/GPU residency and sustained thermal behaviour require assembled-game captures with declared settings.
