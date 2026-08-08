# Universe Creator — Module 2 Task List

Status: **specification only. Do not implement any item in this file yet.**
Written 2026-08-07, alongside `UniverseCreator_Corrections_TaskList.md`.

Module 1 (Terrain Core) plus the corrections list must be closed and verified in
the Editor before a single line of Module 2 is written. This document exists so
the scope is agreed in advance and nothing is invented mid-build.

Sources of truth:
- `UniverseCreator_PCG_TechnicalDesignDocument.pdf` — Section 7 (Texture Graph,
  24 nodes), Section 8 (pipeline stages), Section 11 (vegetation and biome
  scattering), Section 21 (module plan).
- The Landmass Generator tree at `C:\Users\Admin\Desktop\Landmass Generator\`,
  studied for **architecture and UI approach only**. No code, identifier, or
  comment text is copied. Universe Creator ships its own implementations under
  its own names.

---

## M2-0 — Entry conditions

Module 2 does not start until all of the following hold:

- [ ] Every item in `UniverseCreator_Corrections_TaskList.md` is closed
- [ ] Console is clean in a fresh Editor session
- [ ] A graph sculpts the native terrain in the Scene view with no Play mode
- [ ] Wires drag, zoom is stable, nodes carry their own input fields
- [ ] `ChunkManager` has no children while authoring

---

## M2-1 — PCGEx capability port

The reference tree carries **32** `PCGEx*` source folders (counted on disk;
if the intended figure is 41, the extra nine are elsewhere in that tree and the
count is reconciled in M2-1.1 before any porting begins).

**These are Unreal-lineage point/graph/cluster tools. They are not terrain nodes.**
Porting them wholesale would add thousands of files with no visible benefit to a
terrain system. The task is therefore a **capability port, not a file port**:
identify which folders provide something the terrain pipeline actually needs, and
re-implement only that, in our own idiom, against our own `Generator` base.

### M2-1.1 — Inventory and triage
Produce a table of all `PCGEx*` folders with: what it does, whether Universe
Creator needs it, and if so which of our nodes would consume it. Reconcile the
32-vs-41 count. **Output is a document, not code.**

### M2-1.2 — Tier 1 (port; the terrain pipeline needs these)
| Reference area | What we need it for | Our target |
|---|---|---|
| `PCGExNoise3D` | 3-D noise basis for caves, overhangs, and volumetric warping | extend `Noise` with a 3-D variant |
| `PCGExElementsPaths` | polyline resample / smooth / offset for river and shoreline splines | `Splines/` utility set |
| `PCGExElementsSampling` | sample heightfield and texture at arbitrary points for scatter | scatter sampling helpers |
| `PCGExElementsSpatial` | spatial hash / nearest-neighbour queries for min-spacing scatter | Poisson-disc acceleration |
| `PCGExBlending` | per-channel blend operator set | already partly covered by our Blend node; fill gaps |
| `PCGExFilters` | predicate filtering of point sets | scatter exclusion filters |

### M2-1.3 — Tier 2 (port only when a consumer exists)
`PCGExElementsClusters*` (Delaunay / Voronoi diagram / convex hull / dual graph)
is genuinely useful for tectonic plates, watershed basins, and rock strata. Port
the diagram builders when the Tectonic Plate and Watershed nodes are implemented
in M2-3, and not before.

### M2-1.4 — Tier 3 (do not port)
`PCGExElementsPathfindingNavmesh`, `PCGExElementsBridges`,
`PCGExElementsClipper2`, `PCGExHeuristics`, `PCGExMatching`, `PCGExPickers`,
`PCGExElementsActions`, `PCGExElementsMeta`, `PCGExElementsTensors`,
`PCGExElementsTopology`, `PCGExElementsProbing`, `PCGExElementsFloodFill`,
`PCGExElementsShapes`, `PCGExElementsClustersRefine`,
`PCGExElementsClustersRelax`, `PCGExElementsClustersDecomp`,
`PCGExSpatialDomains`, `PCGExProperties`, `PCGExCollections`, `PCGExGraphs`,
`PCGExCore`, `PCGExFoundations`, `PCGExtendedToolkit`.

Rationale: these are engine-plumbing for a general-purpose point-graph framework.
Universe Creator already has its own graph, evaluator, caching, and port model.
Duplicating theirs would mean two competing graph systems in one project.

**A "PCGEx" menu group must not appear in our add-node search window.** The
reference editor exposes one by scanning folder names; that is a leak of another
asset's structure into the UI, and we do not repeat it.

---

## M2-2 — Texture / Shader Graph window (24 nodes)

A second graph window, `Window > Universe Creator > Texture Graph`, authoring the
terrain material. Same `GraphView` foundation as the terrain window after the
corrections pass, so the interaction model is identical: searchable node tree,
draggable wires, fields in the node body.

Data flowing between texture nodes is `RGBA` / scalar / UV / normal, not
heightfield tiles, so it needs its own port types and its own product cache.

### Texturing (T1-T7)
- [ ] T1 Albedo Layer — Albedo Texture, Tint Color, Tiling(x,y), Offset, Rotation; triplanar on steep slopes
- [ ] T2 Normal Map Layer — Normal Texture, Strength, Tiling; reoriented normal mapping for blending
- [ ] T3 Height Blend — Blend Sharpness, Height Offset; crisp material transitions
- [ ] T4 Triplanar Mapping — Texture and Tiling per channel; `blend = normalize(|n|^sharpness)`
- [ ] T5 Color Gradient — multi-stop gradient editor, Mode (Height/Slope/Custom)
- [ ] T6 Texture Splat — 4 textures blended by splatmap RGBA
- [ ] T7 Detail Texture — Detail Scale, Blend Mode (Overlay/Multiply), Distance Fade

### Environment (T8-T10)
- [ ] T8 Snow Cover — Snow Line Altitude, Coverage, Slope Limit, Snow Albedo; **outputs RGBA + snow mask for physics traction**
- [ ] T9 Wetness — Darkening Factor, Specular Boost, Puddle Threshold; driven by the weather rain mask
- [ ] T10 Mud / Dirt — Mud Texture, Mud Color, Threshold, Blend

### PBR (T11-T14)
- [ ] T11 Occlusion Map — Radius, Intensity, Samples; raymarched from the heightmap
- [ ] T12 Roughness Map — Base Value, Texture, Variation
- [ ] T13 Metallic Map — Base Value, Rock Metallic, Sand Metallic
- [ ] T14 Emission — Color, Intensity

### Utilities (T15-T18)
- [ ] T15 UV Scaler — Scale(x,y), Offset(x,y), Rotation
- [ ] T16 Texture Sampler — Filter Mode, Wrap Mode, Mip Bias
- [ ] T17 Color Math — Add/Multiply/Overlay/Screen/Subtract/Lerp
- [ ] T18 Noise Texture — Type (Perlin/Voronoi/White), Scale, Detail

### Water (T19-T21)
- [ ] T19 Water Surface — Base Color, Deep Color, Depth Fade, Transparency, Refraction, Fresnel Power; outputs RGBA + Normal
- [ ] T20 Water Foam — Foam Texture, Intensity, Speed, Edge Width
- [ ] T21 Caustics — Caustic Texture, Speed, Scale, Intensity; two scrolling layers

### Vegetation and Output (T22-T24)
- [ ] T22 Vegetation Color — Spring/Summer/Autumn/Winter colors, Transition Speed
- [ ] T23 Terrain Blend Final — Max Layers, Quality Level
- [ ] T24 Shader Output — Shader Type (URP Lit / HDRP Lit / Custom); emits the finished material

### M2-2.1 — Shader generation backend
The graph must produce a real material. Decision to make before implementing:
generate HLSL into a `Shader` asset, or drive a fixed über-shader by keywords and
properties. **Recommendation: über-shader with shader keywords**, because it keeps
mobile variant count controllable and avoids shipping a shader compiler. Record
the decision here before starting.

---

## M2-3 — Remaining catalog nodes

The PDF numbers its entries to 72 and labels many `ADDED`. Module 1 shipped the
core set; the ADDED entries that were deferred are listed here. Each needs the
parameter set from Section 5 and real math, not a stub.

### Creators
- [ ] Tectonic Plate Seed — Voronoi centroids + drift vectors; compression raises, divergence lowers
- [ ] Ridged Multifractal (Musgrave) — Roughness
- [ ] Hybrid Multifractal (Musgrave) — Bands; variable roughness per elevation band
- [ ] Domain Warp — Intensity; emits a WarpField
- [ ] Continental Shape Mask — Falloff; low-frequency noise + radial falloff
- [ ] Latitude Gradient — Range; world Z mapped to -90..90
- [ ] Prevailing Wind — Belts; vector field from a global wind-belt model
- [ ] River Source Seed — Density; Poisson-disc weighted by elevation and precipitation
- [ ] Random Seed — single integer driving all downstream stochastic nodes

### Modifiers
- [ ] Droplet Erosion (Musgrave/Olsen) — Raindrops
- [ ] Glacial Carve — Snowline Curve; parabolic U-valley cross-section along flow accumulation
- [ ] Slope Remap Curve — remap slope angle distribution
- [ ] Domain Warp Apply — consumes Height + WarpField
- [ ] Voronoi Rock Strata — Cells; layered rock-type mask
- [ ] Fault Line Displacement — Displacement, Gradient
- [ ] Coastal Shelf Shaper — shallow-gradient falloff below sea level

### Selectors
- [ ] River Network Trace — Spline; traces channels from points along the FlowField
- [ ] River Carve — Width/Depth; cuts the channel profile into the heightfield
- [ ] Lake Fill — Spill Elevation; floods local minima
- [ ] Ocean Level — Sea Level scalar to ocean mask
- [ ] Watershed Basin Segment — Basins; segments into drainage basins
- [ ] Groundwater / Wetland Mask — low slope + high flow + near water
- [ ] Underwater Terrain Extend — Depth; seabed and canyon shaping

### Scatter
- [ ] Biome Density Table — species list + base density per biome
- [ ] Slope/Altitude Exclusion — zeroes density outside walkable bands
- [ ] Poisson-Disc Scatter — Jitter, min spacing
- [ ] Cluster/Grove Bias — biases toward clumped groves
- [ ] Exclusion Mask — zeroes density inside RoadMaster masks
- [ ] Species Instance Resolver — assigns a prefab variant per point
- [ ] Wind Sway Parameter Bake — per-instance wind parameters
- [ ] Ground-Cover Colour Tint — samples nearby soil to tint grass

### Biome / Climate
- [ ] Precipitation Field — ray-marches wind across the heightfield; windward slopes get rain
- [ ] Rain Shadow Solver — Height + Wind + Precip
- [ ] Koeppen Biome Classifier — Temp + Precip lookup
- [ ] Whittaker Biome Classifier — 2-D Whittaker-diagram lookup to biome ID
- [ ] Seasonal Variation Driver — 0-1 season scalar over time
- [ ] Snowline Altitude Solver — per-latitude snowline height

---

## M2-4 — Vegetation and biome scattering execution

Module 1 has the scatter node shapes; Module 2 makes them place real instances.

- [ ] Biome map from temperature + moisture, written as a biome ID field
- [ ] Splatmap generation from biome + slope + altitude (pipeline stage 6)
- [ ] Tree placement via Unity `TreeInstance` with slope and altitude limits
- [ ] Grass and detail via `DetailPrototype`, density per square metre
- [ ] Rock placement with embed depth and slope alignment
- [ ] GPU instancing path for dense cover, with the mobile budget from Appendix B
- [ ] Per-chunk scatter so instances stream with the virtual chunk map

---

## M2-5 — Water bodies

- [ ] Ocean plane at the configured sea level, world-wide
- [ ] Rivers from River Source Seed → Network Trace → Carve, with flow direction
- [ ] Lakes from Lake Fill, with a spill-elevation solve
- [ ] Water surface material driven by the Texture Graph (T19-T21)
- [ ] Buoyancy and swim volumes for the physics layer

---

## M2-6 — Documentation

- [ ] Update the system documentation with the Texture Graph window
- [ ] Node reference page per new node: ports, parameters, math, screenshot
- [ ] PCGEx triage table (M2-1.1) filed as a design note explaining what was
      deliberately not ported and why

---

## Non-goals for Module 2

Weather simulation, the enhanced lighting system, Nanite-style tessellation,
the RoadMaster linker beyond the existing stub, and asynchronous world streaming
belong to Modules 3+. They are named here only so they are not pulled forward.
