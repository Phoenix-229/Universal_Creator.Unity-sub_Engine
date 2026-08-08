# Universe Creator — System Documentation

**A procedural content generation engine subsystem for Unity.**

This document covers the whole Universe Creator system. It is written to grow: Module 1 (Terrain Core) is what exists today, and later modules append their own sections rather than replacing this one.

- **Unity version:** 6000.5.2f1 (Unity 6.5)
- **Root namespace:** `UniverseCreator` (runtime), `UniverseCreator.Editor` (editor)
- **Install path:** `Assets/UniverseCreator/`

---

## Table of contents

1. [What Universe Creator is](#1-what-universe-creator-is)
2. [Quick start](#2-quick-start)
3. [Architecture](#3-architecture)
4. [The terrain model](#4-the-terrain-model)
5. [Virtual chunking (25+1)](#5-virtual-chunking-251)
6. [The node graph](#6-the-node-graph)
7. [Node reference](#7-node-reference)
8. [The inspector](#8-the-inspector)
9. [Input field behaviour](#9-input-field-behaviour)
10. [Async generation](#10-async-generation)
11. [World-size-relative maths](#11-world-size-relative-maths)
12. [The continent edge rule](#12-the-continent-edge-rule)
13. [Tunnels and terrain holes](#13-tunnels-and-terrain-holes)
14. [Manual sculpting that survives regeneration](#14-manual-sculpting-that-survives-regeneration)
15. [Graph editor controls](#15-graph-editor-controls)
16. [Optimization hooks](#16-optimization-hooks)
17. [Integration contract](#17-integration-contract)
18. [Troubleshooting](#18-troubleshooting)
19. [Extending the system](#19-extending-the-system)
20. [Module status](#20-module-status)

---

## 1. What Universe Creator is

Universe Creator is an **engine subsystem**, not a terrain painter and not a single Unity asset. It owns:

- its own **node-graph engine** with pull-based lazy evaluation,
- an **async execution model** that keeps the editor responsive,
- **editor windows** (node graph, texture graph, world settings),
- a **formal bridge contract** (`IPCGDataProvider`) that other systems — notably RoadMaster — read terrain through.

The design goal is that terrain, roads, water, vegetation and weather behave as **one world**, not as separate systems that happen to overlap. Buildings sit on the ground; roads follow the surface; tunnels cut the terrain exactly.

**What it is not:** it does not replace Unity's terrain. It *drives* Unity's native `Terrain` component. You keep every sculpt and paint brush Unity ships with.

---

## 2. Quick start

### Create a world

`GameObject > 3D Object > Universe Creator > Create New World`

This does four things:

1. Builds the hierarchy (below).
2. Creates a graph asset in `Assets/UniverseCreator/Graphs/`.
3. Opens the Node Graph window on it.
4. Starts generating terrain in the background.

### The hierarchy it builds

```
Universe Creator World        (UniverseCreatorObject)
├── Terrain                   (parent for the chunk GameObjects)
├── WaterSystem               (Module 3)
├── VegetationSystem          (Module 4)
├── WeatherSystem             (Module 5)
├── LightingSystem            (Module 5)
├── ChunkManager              (ChunkManager)
└── RoadMasterLinker          (RoadMasterLinker — Module 7 bridge)
```

The empty children are deliberate. Later modules attach their components to these existing objects rather than restructuring the hierarchy, so a world built today stays valid.

### Other entry points

| Action | Where |
|---|---|
| Create a graph asset | `Assets > Create > Universe Creator > Graph (Empty / Simple Terrain)` |
| Open the node editor | `Window > Universe Creator > Node Graph Editor` |
| Open a graph | **Double-click the `.asset`** in the Project window |
| Project-wide defaults | `Edit > Project Settings > Universe Creator` |
| Version / module status / docs | `Window > Universe Creator > About` |
| Share a graph between projects | Node editor toolbar → **Export / Import** (`.ucgraph`) |

---

## 3. Architecture

### Assembly split

| Assembly | Location | Rule |
|---|---|---|
| Runtime | `Core/Runtime/` | **Must never reference `UnityEditor`.** Ships in player builds. |
| Editor | `Core/Editor/` | Editor-only. Free to use `UnityEditor`. |

Breaking the runtime rule is the single most common way to make a project fail to build. If you add a `using UnityEditor;` to anything under `Core/Runtime/`, the build breaks even though the editor compiles fine.

### Threading model

This is load-bearing. Get it wrong and you get crashes that only reproduce sometimes.

| Thread | May do | Must never do |
|---|---|---|
| **Main** | Anything. Snapshots settings, primes nodes, applies results to `TerrainData`. | — |
| **Worker** | Pure maths on plain arrays (`Matrix`, `float[]`). | Touch **any** `UnityEngine.Object`. |

The trap: `UnityEngine.Object`'s `==` operator and its `.name` property both marshal into native code. So this is **not** thread-safe on a worker:

```csharp
if (graph == null) { ... }      // native call — crashes off-thread
Debug.Log(graph.name);          // native call — crashes off-thread
```

Reading plain managed fields *is* safe:

```csharp
graph.nodes          // plain List<Generator> — fine
graph.connections    // plain List<Connection> — fine
```

Where a worker genuinely must null-check a Unity object, use `object.ReferenceEquals(x, null)`. Real null checks belong on the main thread, before dispatch.

### The generation pipeline

```
Main thread                          Worker thread                Main thread
───────────                          ─────────────                ───────────
RebuildChunks()
  ├─ null-check graph
  ├─ node.PrepareMainThread()   ──▶  (Unity objects copied
  │    for every node                 into plain arrays)
  ├─ snapshot settings
  └─ Task.Run  ─────────────────▶    GraphEvaluator.Evaluate()
                                       ├─ pull output nodes
                                       ├─ recurse upstream
                                       └─ fill TileData.outputs
                                              │
                                              ▼
                                     PumpCompleted()  ◀────────── each frame
                                       ├─ TerrainBuilder.ApplyOutputs
                                       │    SetHeights / SetAlphamaps / SetHoles
                                       └─ re-stamp tunnel carves
```

`PrepareMainThread()` is the escape hatch: a node that needs a `Texture2D` or an `AnimationCurve` copies it into a plain array there, while it is still legal to touch Unity API.

---

## 4. The terrain model

### Why Unity native Terrain

The system drives Unity's built-in `Terrain` component rather than custom plane meshes. The reasoning:

| Requirement | Why native wins |
|---|---|
| Manual editing | Unity's sculpt and paint brushes work immediately. Custom meshes mean rebuilding all of it. |
| Tunnel holes | `TerrainData.SetHoles` exists and is exact. |
| LOD | `heightmapResolution` per chunk is a one-line LOD lever. |
| Colliders | `TerrainCollider` is heavily optimised and battle-tested. |

You can sculpt a generated world by hand at any time. The caveat is that a **regeneration overwrites hand edits**, because the graph is the source of truth for heights. Persisting manual edits across regenerations is `TerrainOverrideLayer`'s job — see [Module status](#17-module-status).

### One world, virtually sliced

The world is a **finite continent** of `worldSizeKm` on a side, divided into `chunkCountPerSide²` cells (25 when the count is 5).

- **In the editor:** every cell is present and generated. You see the whole terrain as one surface while you build.
- **In play:** the cells stay put — a finite world has no edge to fall off — but **LOD follows the player**. The centre square is high-resolution with colliders; the outer ring is low-poly; one buffer ring beyond is precomputed.

This is why it is called *virtual* chunking: the slicing is a performance strategy, not a change to what the world is.

### Recycling, not destroying

**Chunks are never destroyed.** Each cell owns a pooled `GameObject` + `Terrain` + `TerrainData` for the world's whole lifetime. When a cell's LOD changes, its heights are regenerated **into the same `TerrainData`**.

This matters because the alternative — instantiate on approach, destroy on retreat — allocates and frees terrain meshes continuously as the player moves. That produces GC spikes, frame hitches, and on memory-constrained platforms, crashes. Reusing the objects means steady-state memory and no allocation churn.

Shrinking the world parks surplus chunks inactive rather than destroying them, so growing it again reuses them.

---

## 5. Virtual chunking (25+1)

### The grid

```
+---+---+---+---+---+
| L | L | L | L | L |    L = Low  — outer ring, half resolution
+---+---+---+---+---+
| L | H | H | H | L |    H = High — centre square, full resolution,
+---+---+---+---+---+          colliders on
| L | H | P | H | L |
+---+---+---+---+---+    P = the cell the player is standing on
| L | H | H | H | L |
+---+---+---+---+---+    +1 buffer ring (not drawn) sits outside
| L | L | L | L | L |       the grid at quarter resolution
+---+---+---+---+---+
```

### LOD policy

Implemented in `DualLODSystem`, which is pure maths with no Unity objects, so it is callable from anywhere.

| Level | Where | Resolution | Collider |
|---|---|---|---|
| `High` | within the high-LOD ring | full (`resolution`) | **yes** |
| `Low` | rest of the visible grid | half the span | no |
| `Buffer` | one ring beyond the grid | quarter the span | no |

Ring distance is Chebyshev (chessboard) distance — `max(|dx|, |dz|)` — which is what makes the rings square rather than diamond-shaped.

Resolution division always divides the **span** (`resolution - 1`), never the sample count, so the 2^n+1 shape Unity requires is preserved. A 257 base gives 257 / 129 / 65.

### Anti-fall-through

Colliders are guaranteed on `High` chunks — the ones the player can actually stand on. `Low` and `Buffer` skip colliders because the player is never on them. Since the centre ring is always `High` and always collidered, there is no state in which the player is standing over a collider-free cell.

### Streaming

`ChunkManager.HandleStreaming` runs each frame in play. It recomputes which cell the focus transform (the main camera by default, or an explicit `focus`) sits over. If that cell has not changed, it returns immediately. If it has, every cell is reclassified and **only the ones whose LOD actually changed** are regenerated.

Work is dispatched nearest-first, capped by `maxConcurrentChunks`, so the ground under the player resolves before distant scenery.

### Speed-adaptive preloading

A player on foot and a player in a fast vehicle need different things from the streamer. At 100 km/h you cross a chunk in a couple of seconds, so the default ring resolves terrain roughly as fast as you arrive at it — which is how you get pop-in, or worse, a fall-through.

So the streamer reads the focus object's velocity each frame and widens its look-ahead as you speed up:

| Speed | High-LOD ring | Vegetation density |
|---|---|---|
| Under 20 km/h — on foot | Unchanged | Full |
| 20 km/h and up — vehicle | +1 ring | 50% |
| 80 km/h and up — fast vehicle | +2 rings | 25% |

The density cut pays for the wider ring. Preloading more chunks costs more work per chunk arrival, and at speed you cannot resolve individual grass blades anyway, so trading detail you cannot see for terrain that exists when you get there is free in practice. Vegetation multipliers are published for Modules 2–4 to consume; Module 1 stores them and applies the ring boost itself.

Velocity comes from a `CharacterController` or a `Rigidbody` if either is present, and falls back to differencing the transform position. Note `Rigidbody.velocity` was renamed `linearVelocity` in Unity 6 — `CharacterController.velocity` still exists.

### Wireframe preview during generation

When `showWireframePreview` is on, a dispatched chunk immediately shows a low-poly line-topology stand-in built from its previous heights, so you see terrain shape rather than a hole while the real surface generates. It is torn down in `ApplyChunk` once the real heightmap lands.

Line topology rather than a lit mesh, because it needs no material setup and reads clearly against an empty scene, and it works identically in edit and play mode.

Cancellation tears the stand-in down too. `CancelAll` walks the whole pool, requests the stop, and hides every stand-in — otherwise a cancelled run would leave the wireframe visible over a hidden real surface indefinitely.

---

## 6. The node graph

### The model

Evaluation is **pull-based and lazy**. Nothing runs until an output node is asked for its product. Pulling an input recurses upstream; every product is memoised in `TileData` keyed by `(nodeId, portIndex)`.

Two consequences worth knowing:

- A node feeding three consumers computes **once**, not three times.
- A disconnected island of nodes costs **nothing** — it is never pulled.

### Graphs are assets

`Graph` is a `ScriptableObject` saved as a `.asset`. That is what gives you the object-picker circle, double-click-to-open, and the ability to drive several worlds from one graph.

> **Spec deviation:** the implementation prompt asks for the extension to display as `.ucgraph`. Unity will not import a custom extension as a `ScriptableObject` without a `ScriptedImporter`, which is out of Module 1 scope. Topology and parameters match the spec exactly; only the extension differs.

### Disabled nodes are transparent

Disabling a node does not punch a hole in the graph. The evaluator passes the first type-compatible input straight through, so bypassing a modifier behaves like a wire. This makes A/B comparison quick.

### Cycles

`Graph.WouldCreateCycle` blocks cycles at connection time. `GraphEvaluator` additionally tracks a visiting set and bails with `null` rather than blowing the stack, which covers hand-edited asset files.

---

## 7. Node reference

Nodes live in `Core/Runtime/Nodes/`, grouped by category. Every node exposes public fields; those fields are what the inspector and the node body draw as input controls.

### Creators — produce a heightfield from nothing

`PerlinNoise`, `SimplexNoise`, `ValueNoise`, `BillowNoise`, `RidgedNoise`, `Voronoi`, `Constant`, `SimpleForm`, `SpotBrush`, `TectonicPlates`, `ImportHeightmap`, `RealWorldImport`

The five fBm-based noise nodes share `NoiseCreatorBase`:

| Field | Meaning |
|---|---|
| `seed` | Deterministic permutation seed. |
| `size` | **World metres per noise period.** Larger = broader features. |
| `octaves` | fBm iterations (1–16). More = finer detail, more cost. |
| `detail` | Per-octave amplitude falloff. ~0.5 is natural; higher is rougher. |
| `turbulence` | Folds the noise; positive inverts. Produces ridged, veiny forms. |
| `intensity` | Output multiplier. |
| `offset` | Output addend. |
| `worldOffset` | Shifts the sampling window without moving the world. |

`size` being in **world metres** is what makes chunks seamless: neighbouring chunks evaluate the same world coordinate along a shared edge, so they agree exactly.

### Modifiers — transform a heightfield

`HydraulicErosion`, `ThermalErosion`, `WindErosion`, `CoastalErosion`, `Curve`, `Levels`, `Contrast`, `Blur`, `Sharpen`, `Distort`, `Terrace`, `Ledge`, `Direction`, `Parallax`, `Clamp`, `Invert`, `Power`, `SedimentMap`, **`WorldEdgeFalloff`**

Erosion is where realism comes from. The hydraulic solver implements the standard sort-by-height / cast-torrents / erode / transfer-settle formulation of stream-power erosion; the thermal solver follows Musgrave's talus-angle relaxation; the wind solver uses the standard saltation pickup/deposition model.

### Selectors — produce masks

`HeightSelector`, `SlopeSelector`, `AspectSelector`, `CurvatureSelector`, `CavitySelector`, `FlowMapSelector`, `NoiseMaskSelector`, `TextureImportMask`

Masks drive *where* things happen — snow above an altitude, rock on steep slopes, grass in flat hollows.

### Combiners — merge heightfields

`Blend`, `MaskMix`, `Maximum`, `Minimum`, `Normalize`, `WeightedAverage`

### Outputs — write to Unity

`HeightOutput`, `TextureOutput`, `GrassOutput`, `HolesOutput`, `NormalMapOutput`, `CustomMeshOutput`

**A graph with no output node produces nothing.** This is why the "Empty" template ships with a `HeightOutput` already placed — wire anything into it and terrain appears.

### Utilities, Scatter, Biome

`PortalIn` / `PortalOut` route signals without long wires. Scatter and Biome nodes exist with correct signatures; their full behaviour belongs to Modules 2 and 6.

---

## 8. The inspector

Select the world root to get the custom inspector. It has two states.

### No graph assigned — collapsed

Only the graph slot shows, plus:

- an **object field** whose **circle on the right opens the graph picker**,
- **Open Node Graph** (disabled until a graph exists),
- **Create Empty**,
- template buttons: **Empty Graph**, **Simple Terrain**.

Everything else is hidden. There is nothing meaningful to configure without a graph.

### Graph assigned — revealed

Assigning a graph — by picker, drag, or template button — immediately reveals every section, opens the Node Graph window on it, and starts generating.

| Section | Controls |
|---|---|
| **Graph Info** | Node count, connection count, validation warnings. |
| **World** | Seed, world size (km), max height (m), resolution, world origin offset. |
| **Chunk** | Chunks per side, high-LOD ring size, buffer ring width, recentre threshold. |
| **Terrain Rendering** | Material, pixel error, basemap distance, cast shadows, draw instanced, generate collider, physics material, reflection probe usage. |
| **Terrain Layers** | Layer count and one slot per splatmap channel. |
| **Trees, Grass & Detail** | Draw toggle, detail distance and density, grass wind (strength / amount / speed / tint), tree distance, billboard start, fade length, max full-mesh trees. |
| **Performance** | GPU instancing, draw distance, LOD bias, max concurrent chunks, preview quality. |
| **Generation** | Auto-regenerate, wireframe preview. |
| **Stats** | Derived read-outs (below). |

### Native Terrain settings are driven, not defaulted

A chunk's `Terrain` component is **created from script**. Nobody ever opens Unity's terrain inspector to configure it, so any native property this system does not explicitly drive stays at whatever Unity's constructor happened to produce — invisible to you, and different from what the inspector implies.

So Universe Creator drives the full native set, and `TerrainBuilder.ConfigureTerrain` re-applies every one of them to all 25 chunks after each generation. Change a value once and the whole world follows.

Two of these need care:

- **Terrain Layers** map to splatmap channels **positionally**. Channel 0 of every Texture Output node paints element 0 of this list. Reordering the list repaints the world without touching the graph. Empty slots are skipped rather than written, because a part-filled array is the normal state while you are wiring one up.
- Layers are only re-assigned **when the set actually differs**. Writing identical layers reallocates the splatmaps and would wipe whatever the graph just painted.

Grass wind (`wavingGrass*`) lives on `TerrainData`, not on the `Terrain` component — a detail worth knowing if you drive it yourself from another system.

### Derived read-outs

These are computed from your settings and shown back to you, so the consequences of a change are visible before you commit to it:

| Read-out | Formula |
|---|---|
| World size | `worldSizeKm × 1000` |
| Chunk size | `worldSizeKm × 1000 / chunkCountPerSide` |
| Metres per pixel | `chunkSize / (effectiveResolution - 1)` |
| High-LOD chunks | `highLODRingSize²` |
| Low-LOD chunks | `chunkCountPerSide² − highLODRingSize²` |
| Buffer chunks | `(n + 2b)² − n²` |
| Estimated memory | `samples × 4 bytes` |

**Metres per pixel is the number to watch.** It is the real resolution of your world. A 4 km world at 257 across 5 chunks gives 800 m / 256 ≈ 3.1 m per sample — reasonable. Push the world to 32 km without raising resolution and you are at 25 m per sample, which no amount of erosion will make look detailed.

### Validation

Settings are clamped by `ValidateSettings()`, which runs on every commit:

- `resolution` snaps to the nearest 2^n+1 in [33, 4097] — Unity rejects anything else.
- `chunkCountPerSide` and `highLODRingSize` are forced **odd**, because an even grid has no single centre cell for the player to occupy.
- `highLODRingSize` cannot exceed `chunkCountPerSide`.

---

## 9. Input field behaviour

**Nothing commits per keystroke.** This is a hard rule, and it is not cosmetic: every commit can start a full terrain regeneration, so a per-keystroke field would queue one generation per character typed.

| Control | Commits on |
|---|---|
| Text / number box | **Enter**, or **focus-out** (clicking away) |
| Slider | **Drag release** (mouse button up) |
| Toggle, dropdown | Immediately — a single discrete choice |

While a text box is focused, your typing goes into a pending buffer; the underlying value is untouched. **Escape** discards the buffer and restores the committed value.

While a slider is being dragged, the pending number shows greyed beside it. The value commits when you release.

Direct field writes bypass Unity's `OnValidate`, so the inspector calls `ValidateSettings()` explicitly after each commit. `Undo.RecordObject` is called before every mutation, so Ctrl+Z works normally.

---

## 10. Async generation

Generation runs on `Task.Run`. The editor stays fully responsive; there are **no modal dialogs and no blocking progress bars**. Progress appears in Unity's background-tasks area (bottom-right) via `Progress.Report`.

`EditorUtility.DisplayProgressBar` is forbidden in this system — it is modal and halts the editor.

### Edit mode vs play mode

Edit mode has no `Update()` loop, so a finished background chunk would sit unapplied forever. `EditModeChunkDriver` hooks `EditorApplication.update` — the one main-thread heartbeat edit mode provides — and pumps completed chunks onto their `TerrainData`. Play mode drives itself from `ChunkManager.Update`.

### Cancellation

Every dispatch carries a `StopToken`. Starting a new generation calls `RequestStop()` on the previous one; the worker sees the flag at its next per-row check and unwinds. A cancelled run's partial data is **never** applied and never cached — half-finished data must not poison the product cache.

An older worker finishing late cannot report the newest run as idle: the completion path checks it still owns the current token by reference.

---

## 11. World-size-relative maths

Changing world size **reveals more terrain**. It does not stretch what is there.

This works because noise is sampled in **world-metre space**, not in normalised tile space:

```csharp
float wx = originX + x * pixelSize.x;    // world metres
float v = noise.Fractal(wx, wz, size, ...);
```

`size` is a period in metres. A mountain range with `size = 2000` is 2 km across whether the world is 4 km or 40 km. Growing the world from 4 km to 8 km therefore gives you twice as much land with the same-sized mountains — which is what "more world" should mean.

Everything downstream recomputes from the same source values:

- chunk size = `worldSizeKm × 1000 / chunkCountPerSide`
- metres per pixel = `chunkSize / (effectiveResolution - 1)`
- erosion step size derives from metres per pixel, so erosion stays physically consistent at any world size.

**Practical consequence:** when you enlarge the world, raise `resolution` or `chunkCountPerSide` alongside it, or metres-per-pixel grows and the terrain gets visibly coarser. The Stats section shows this number precisely so you are never guessing.

---

## 12. The continent edge rule

Real continents end at sea level. A generated world that simply stops at its boundary ends in a wall of hills sliced flat — instantly readable as fake.

`WorldEdgeFalloffNode` fixes this. Everything within `falloffMeters` (default **500 m**) of the world edge is blended toward `edgeHeight` (default **0**, sea level) along a smoothstep curve, so hills near the border **ease down gently** rather than snapping flat at the band edge.

| Field | Meaning |
|---|---|
| `falloffMeters` | Width of the band, measured inward from the world edge. Default 500. |
| `edgeHeight` | Height the very edge settles to. 0 is sea level. |
| `curvePower` | Shapes the descent. 1 is plain smoothstep; higher keeps land high longer then drops more steeply near the shore. |
| `falloffX` / `falloffZ` | Enable per axis, for worlds meant to tile in one direction. |

Two details that make it correct:

- Distance is measured against the **full world bounds**, not the chunk. A chunk in the middle of the continent is untouched; only true border chunks fall away. `TileData.fullWorldPos` / `fullWorldSize` carry those bounds, defaulting to the chunk's own bounds so a single whole-world tile still behaves.
- The band stays 500 m wide **at any world size**, because it is measured in metres. Corners fall off on both axes at once, which rounds them naturally.

Place it **last, after erosion**, so the coastline it carves cannot be re-roughened. The Simple Terrain template already wires it this way:

```
Perlin Noise → Hydraulic Erosion → World Edge Falloff → Height Output
```

---

## 13. Tunnels and terrain holes

### What Unity actually does

**A terrain hole removes surface, and nothing else.** Unity builds no wall around the cut and, critically, *also removes the collider there*. A bare hole therefore fails twice:

| Failure | What you see |
|---|---|
| No wall geometry | You look straight through the open edge into the terrain's underside. A bored hill looks sliced in half rather than bored. |
| No collider | Anything standing on the footprint falls out of the world. |

Universe Creator fixes both by **generating the missing geometry itself**. A carve is not just a hole — it is a hole plus a sealing shell.

### The three things a carve does

`ChunkManager.CarveTunnel(bounds, settings)` performs all three, and all three are required for an opening to be *usable* rather than merely visible:

1. **Removes the surface** inside the box from the holes map.
2. **Generates a wall skirt** around the cut edge, hanging down to the tunnel floor. This is what keeps the hill looking bored instead of sliced open.
3. **Lays a floor with a collider** across the footprint, so the road and everything on it has ground to rest on.

Faces named in `openFaces` are skipped — walling the tunnel mouth would brick it up.

### The no-gap guarantee: overlap, not precision

The holes map is **per-quad**, so the real cut boundary is quantised to that grid and can land up to one quad outside the box you requested. Building the skirt exactly on the requested bounds would leave a hairline crack along most of the perimeter.

The fix is **deliberate overlap**:

- The skirt is pushed outward by `oversize` (default 0.5 m).
- Its **top edge follows the sampled terrain height**, so the wall top always sits *under* the surviving surface.

Overlapping geometry cannot crack. Exactly-matched geometry can, because floating point says so. This is why `oversize` exists and why raising it is the correct response to a visible seam.

### `TunnelShellSettings`

| Field | Meaning |
|---|---|
| `openFaces` | Which sides stay open. `AlongX` / `AlongZ` open both ends of a bore and wall the sides. Defaults from the box's longer axis. |
| `floorY` | Height of the generated floor. Defaults to `bounds.min.y`. |
| `buildFloor` | Off for an underwater or open-bottom cut. |
| `oversize` | Metres pushed outward to tuck the skirt under the terrain. Raise if a seam shows. |
| `segments` | Subdivisions per wall. Higher follows a rugged surface more closely. |
| `skirtBelowFloor` | Extra drop below the floor, so the wall base is never visible from inside. |
| `doubleSided` | Emits back faces, so the shell reads correctly from inside and outside. |
| `uvScale` | Texture tiling on the generated geometry. |

Shells are parented under a `TunnelShells` child, and their meshes carry `HideFlags.DontSave` so they never leak into the scene as orphaned assets.

### Automatic carving

You do **not** paint holes by hand. When a tunnel prefab is placed on a road, RoadMaster calls:

```csharp
int handle = linker.RequestTunnelOpening(worldBounds);
```

and the terrain cuts *and seals* itself. `ReleaseTunnelOpening(handle)` fills it back in and destroys the shell.

Handles are **ids, not list indices**. Releasing one tunnel would otherwise shift every later index and silently repoint another caller's handle at someone else's tunnel.

### Why carves survive regeneration

Carve volumes are stored in `ChunkManager` **outside the graph**, as world-space bounds. A graph re-run rebuilds heights and holes from scratch, so without this a tunnel would vanish the moment you nudged a noise slider.

`ApplyChunk` therefore does two things after applying graph outputs:

- **Re-stamps every carve**, so the holes come back.
- **Marks overlapping shells dirty and rebuilds them**, because a shell's wall tops follow the surface and a new heightfield leaves them meeting nothing.

Shell meshes are **recycled in place** rather than reallocated, consistent with the chunk-recycling contract. `MeshCollider.sharedMesh` is re-assigned after the vertices change — Unity does not notice the edit otherwise, and the collider would keep the old shape.

### Implementation details that matter

- Unity stores **`true` = surface, `false` = hole** — the inverse of how a "hole mask" reads. `HolesOutputNode` inverts on write.
- The holes map is **per-quad**, so it is one smaller than the heightmap on each axis: a 257 heightmap has a 256×256 holes map. Writing a 257×257 array throws.
- Carve sampling is at **quad centres** (`(i + 0.5) / res`), which is what makes the cut land where you asked rather than half a quad off.
- Shell meshes switch `indexFormat` to `UInt32` above 65 000 vertices.
- Shells use `tunnelShellMaterial`, falling back to the terrain material.

---

## 14. Manual sculpting that survives regeneration

You can sculpt terrain by hand with Unity's own terrain tools and keep those edits through a regeneration. This is what `TerrainOverrideLayer` on the settings asset does.

### Deltas, not absolutes

The layer stores the **difference** your edit made, not the resulting height.

That distinction is the whole design. If it stored absolute heights, every edited sample would be frozen at that value forever — raise the world's height scale, change a noise seed, or widen the world, and your edited patch would stay put while everything around it moved, punching a flat scar through the new terrain. Storing a delta means the edit rides on top of whatever the graph produces next: raise a hill by 3 m and it stays 3 m higher than its surroundings, whatever those surroundings become.

### Keyed in world space

Deltas are keyed by **world-space sample coordinate**, not by chunk index.

Chunks are recycled and the world re-centres around the player, so a given chunk object represents different ground over time. A per-chunk index would drag your edits to the wrong place the first time the world shifted. World-space keys are stable under both recycling and re-centring.

Storage is sparse — only edited samples exist — so a few brush strokes cost a few hundred entries, not a full heightmap.

### How it applies

Overrides are folded into the heightmap **before** `SetHeights`, inside `TerrainBuilder.ApplyOutputs`. Patching afterwards would mean reading the whole heightmap back out of `TerrainData` per chunk — a full round trip through native memory for no benefit.

```csharp
TerrainBuilder.ApplyOutputs(data, outputs, settings,
                            settings.OverrideLayer,   // may be null
                            chunkWorldBounds,
                            settings.GetMetersPerPixel());
```

The 3-argument overload still exists and skips overrides entirely.

### Capturing an edit

`CaptureDifference` compares the current terrain against the baseline the generator produced and records what changed. It **accumulates** into any existing delta rather than replacing it, because the baseline it diffs against already has earlier deltas folded in — assigning would silently discard every prior stroke.

| Member | Does |
|---|---|
| `RecordDelta` | Adds one sample's change |
| `CaptureDifference` | Diffs a whole region and accumulates |
| `ApplyTo` | Folds deltas into a heightmap |
| `AffectsRegion` | Cheap test before doing work |
| `ClearRegion` / `ClearAll` | Discards edits |
| `EditCount` / `IsEmpty` | Inspection |

Deltas serialise with the settings asset, so they survive editor restarts.

---

## 15. Graph editor controls

| Action | Input |
|---|---|
| Pan | Middle-drag, or Alt+drag |
| Zoom | Scroll wheel |
| Add node | Right-click canvas |
| Select | Click |
| Add to selection | Ctrl+click |
| Box select | Drag on empty canvas |
| Move selection | Drag any selected node — the whole set moves |
| Delete | Delete key |
| Duplicate | Ctrl+D |
| Save | Toolbar **Save** |

### Comment groups

A comment group is a tinted backdrop you drop behind a cluster of nodes to label it. It has no ports and is never evaluated — `Generate()` returns null. It lives in the node list anyway, which is what makes it move, serialise, undo, and copy along with the graph for free.

Groups draw **before** nodes so they sit behind them, and they are hit-tested in a second pass for the same reason: a group added after the nodes it frames would otherwise steal their clicks.

### Sharing graphs between projects — `.ucgraph`

Two formats, on purpose:

| Format | Use | Why |
|---|---|---|
| `.asset` | Graphs inside this project | Unity cannot import a custom extension as a `ScriptableObject` without a `ScriptedImporter` |
| `.ucgraph` | Moving a graph to another project | Plain JSON: readable, diffable in git, reviewable in a pull request |

An `.asset` carries binary type GUIDs, so dropping one into a different project gives broken script references. `.ucgraph` resolves node types by **name** at import, which is what makes it portable.

Toolbar **Export** / **Import** in the graph window. On import, a node whose type no longer exists is skipped with a warning, and any connection touching a skipped node is dropped — a dangling connection would crash evaluation on the first pull. Import replaces the open graph rather than merging, because merging would need a conflict policy for colliding node ids.

Duplicating a selection mints fresh ids and rewires connections **internal to the selection**. Connections crossing the selection boundary are deliberately not copied: a duplicated node cannot share its source's single input slot without silently stealing or duplicating upstream data, and guessing which you meant is worse than leaving the inlet open.

---

## 16. Optimization hooks

Three components Module 1 provides and later modules consume. All three are opt-in: add them to a GameObject when you want them.

| Component | Does | Consumed by |
|---|---|---|
| `AdaptiveQuality` | Averages frame time over a rolling window and publishes a tier (Ultra / High / Medium / Low) | Modules 2–4, to scale density and effect complexity |
| `GPUInstancingManager` | Groups instances by mesh+material+layer and submits via `Graphics.DrawMeshInstanced` | Vegetation and prop scatter |
| `OcclusionManager` | Frustum and distance culling for chunk and batch bounds | Chunk streaming |

Notes that matter if you extend them:

- **Instancing caps at 1023 per call.** Larger sets are split automatically. A single reused submit buffer avoids allocating an array every frame.
- **Instancing submits in `LateUpdate`**, because scatter systems populate during `Update` — submitting earlier would draw an incomplete batch.
- **`OcclusionManager` tests distance first**, then the six frustum planes. Distance is one `sqrMagnitude` and rejects most of a large world before the expensive test runs. Frustum planes are recomputed at most once per frame.
- **True occlusion is Unity's job.** This class does frustum and distance only. Something hidden *behind* a hill is left to Unity's built-in occlusion culling, which already has the depth buffer this class would otherwise have to rebuild.

---

## 17. Integration contract

Other systems read terrain through `IPCGDataProvider`, implemented by `RoadMasterLinker`. They never reach into Universe Creator's internals.

```csharp
public interface IPCGDataProvider
{
    float   SampleHeight(float worldX, float worldZ);
    Vector3 SampleNormal(float worldX, float worldZ);
    List<Bounds> GetActiveChunkBounds();
    void    NotifyCutRegion(Bounds worldBounds);
    int     RequestTunnelOpening(Bounds worldBounds);
    void    ReleaseTunnelOpening(int handle);
}
```

`SampleHeight` and `SampleNormal` are what stop things floating or sinking. Anything that must sit on the ground — a road spline control point, a building footing, a scattered rock — queries the surface at its world position rather than assuming a height. Because the query resolves against the live chunk grid, it returns the same answer the player will physically stand on.

These are **implemented for real** against the Module 1 chunk grid. Module 7 consumes them; nothing here waits on Module 7 to exist.

---

## 18. Troubleshooting

### No terrain appears

Work through these in order:

1. **Is a graph assigned?** The inspector stays collapsed without one.
2. **Does the graph have a `HeightOutput` node?** Nothing renders without one. This is the most common cause.
3. **Is anything connected to it?** An output node with an empty inlet produces a flat world.
4. **Check the Console.** Generation failures are logged with the chunk coordinate.
5. **Press Regenerate** in the inspector.

### Terrain is flat

Either nothing reaches `HeightOutput`, or `intensity` on the noise node is 0, or a `Clamp` upstream has collapsed the range. Attach a `Preview` node to inspect the signal partway through the graph.

### Terrain looks coarse or blocky

Check **metres per pixel** in the Stats section. If it is large, either raise `resolution` or raise `chunkCountPerSide`. Enlarging the world without doing one of these is the usual cause.

### Terrain is detailed but too smooth

Raise `octaves`, or raise `detail` toward 0.6–0.7. Check that a `Blur` or a high `relax` on the erosion node is not flattening it.

### Changes do not apply

Input fields commit on **Enter or focus-out**, sliders on **release**. If you typed a value and immediately looked at the scene without leaving the field, nothing has committed yet. Also confirm **Auto Regenerate** is on, or press Regenerate manually.

### Editor stutters while generating

Lower `maxConcurrentChunks` — it should stay below your core count. Set **Preview Quality** to Draft while iterating and Full for the final pass.

### Chunk seams

Seams mean neighbouring chunks disagree at a shared edge. Any node sampling in tile space rather than world space causes this. All shipped nodes sample in world space; a custom node must do the same — derive coordinates from `m.worldPos` and `m.PixelSize`, never from the raw loop index alone.

### Player falls through the terrain

Confirm **Generate Collider** is on. Colliders exist only on `High` LOD chunks by design, and the centre ring is always `High`, so a fall-through means either colliders are off entirely or the focus transform is not tracking the player. Set `ChunkManager.focus` explicitly rather than relying on `Camera.main`.

### Player falls through where a tunnel was carved

A carve removes the terrain collider over its footprint — that is unavoidable, it is what a hole *is*. The generated floor is what replaces it, so a fall-through here means the floor is missing or in the wrong place:

- `buildFloor` is off in the carve's `TunnelShellSettings`.
- `floorY` sits below where the road actually runs, so the road hangs over nothing.
- The shell was created but its `MeshCollider` lost its mesh. `BuildShell` re-assigns `sharedMesh` after editing vertices precisely because Unity ignores the edit otherwise; a custom carve path that skips that step will render correctly and collide with nothing.

### You can see through the cut edge into the hill

The wall skirt is missing or too short.

- The face you are looking at is listed in `openFaces`, so it was deliberately left open. `AlongX` opens both X ends; if the bore actually runs along Z, this is backwards.
- The skirt does not reach the surface. Raise `oversize` — the seal works by overlapping under the terrain, not by matching it exactly.
- The heightfield changed but the shell was not rebuilt. `ApplyChunk` marks overlapping shells dirty automatically; a direct `SetHeights` that bypasses it will not.

### Terrain renders untextured no matter what the graph paints

No terrain layers are assigned. Splatmap channels index the **Terrain Layers** list positionally, so with an empty list there is nothing for channel 0 to paint. Add one layer per channel your Texture Output nodes write.

### Tunnel fills back in after regenerating

It should not — carves are stored outside the graph. If it does, the carve was applied to `TerrainData` directly rather than through `RequestTunnelOpening`.

### A crash only in play mode, or only sometimes

Almost always a thread-safety violation: a worker touched a `UnityEngine.Object`. Check any custom node's `Generate()` for Unity API calls. Move them to `PrepareMainThread()`.

### Build fails but the editor compiles

Something under `Core/Runtime/` references `UnityEditor`. Editor-only code belongs in `Core/Editor/`.

---

## 19. Extending the system

### Writing a node

1. Derive from the right base: `CreatorNode`, `ModifierNode`, `SelectorNode`, `CombinerNode`, or `OutputNode`.
2. Override `NodeName`, `Category`, `Tooltip`.
3. Implement the category's abstract method (`Create`, `Modify`, `WriteOutput`, …).
4. Add public fields for parameters — these become the input controls automatically.

Rules a node must follow:

- **No Unity API in `Generate()`.** Copy anything you need in `PrepareMainThread()`.
- **Check the stop token** at least once per row: `if (stop != null && stop.StopRequested) return;`
- **Sample in world space**, from `m.worldPos` and `m.PixelSize`, or your node will produce seams.

Minimal example:

```csharp
public class MyNode : ModifierNode
{
    public override string NodeName { get { return "My Node"; } }
    public override NodeCategory Category { get { return NodeCategory.Modifier; } }

    public float strength = 1f;

    protected override void Modify(Matrix m, TileData tile, StopToken stop)
    {
        for (int z = 0; z < m.rect.size.z; z++)
        {
            if (stop != null && stop.StopRequested) return;

            int rowStart = z * m.rect.size.x;
            for (int x = 0; x < m.rect.size.x; x++)
                m.arr[rowStart + x] *= strength;
        }
    }
}
```

### Adding a system in a later module

Attach to the existing hierarchy child (`WaterSystem`, `VegetationSystem`, …) rather than restructuring. Read terrain through `IPCGDataProvider`, never by reaching into `ChunkManager` directly — that contract is what keeps the modules independent.

---

## 20. Module status

### Module 1 — Terrain Core

**Complete:**

- Folder structure, assembly definitions, world-space math library (noise, erosion, matrix ops)
- Node system: `Generator` / `Graph` / `GraphEvaluator` / `Port` / `Connection` / `StopToken` / `TileData`
- All node categories implemented with real algorithms
- Unity hierarchy + menu integration + Project Settings page
- Node Graph editor window with pan, zoom, node drawing, Bezier connections
- Custom inspector with focus-out input, collapsed/revealed states, derived stats
- Virtual chunking with recycling pool, dual LOD, nearest-first dispatch, anti-fall-through
- Async generation with cancellation; edit-mode pumping
- World-edge falloff (the 500 m continent rule)
- Automatic tunnel carving that survives regeneration, **generating its own wall skirt and collidered floor** so nothing falls through and no hill reads as sliced open
- Full native Terrain coverage — rendering, collider physics material, terrain layers, trees, grass and wind — all driven from the world settings rather than left at constructor defaults
- Node Graph window: right-click add-node menu built by reflection, node delete / duplicate / bypass, connect and reroute, frame all, and a reflective parameter panel that gives every node full UI with no per-node drawer code
- `IPCGDataProvider` implemented against the live chunk grid
- Graph templates: Empty, Simple Terrain
- `TerrainOverrideLayer` — manual sculpt edits persist across regenerations as world-keyed deltas
- Low-poly wireframe stand-in during generation, torn down on completion and on cancel
- Node Graph window: box select, multi-node drag, comment groups drawn behind nodes
- Speed-adaptive preloading — 20 / 80 km/h thresholds widen the ring and cut density
- `.ucgraph` JSON import/export for moving graphs between projects, with node-id remapping on duplicate
- `ConnectionDrawer` / `PortDrawer` / `GroupDrawer` — extracted so the window has one definition of each, not inline copies
- `AdaptiveQuality`, `GPUInstancingManager`, `OcclusionManager` — working implementations, opt-in components
- About window

**Beyond the original design document**

Two things in this module were not in the technical design document and were added deliberately:

1. **Generated tunnel shells.** The design assumed Unity's terrain holes were sufficient. They are not — a hole removes surface and collider but generates no wall geometry, so you see straight through the cut into an empty hill and fall through the gap. Module 1 therefore generates the wall skirt and collidered floor itself. Section 13.
2. **Manual sculpt persistence as deltas.** The document called for manual editing to remain possible but did not say what happens to those edits on regeneration. Storing deltas keyed in world space is what makes an edit survive both regeneration and chunk recycling. Section 14.

**Not verified by me**

These compile clean and the logic matches spec, but they need your eyes in the editor — I cannot see the Scene view:

- Terrain appearance: whether generated terrain looks right, erosion reads naturally, edge falloff is convincing
- The 25+1 grid at runtime, and the LOD split between the 9 inner and 16 outer chunks
- Streaming as you move, including the speed thresholds in an actual vehicle
- Node graph interaction feel: drag, box select, connection snapping, group framing
- Tunnel shells meeting a real tunnel prefab with no visible gap
- Sculpt-then-regenerate round trip

### Later modules

| Module | Scope |
|---|---|
| 2 | Texture graph, splatmaps, scatter instantiation |
| 3 | Water |
| 4 | Vegetation |
| 5 | Weather and lighting |
| 6 | Biomes |
| 7 | RoadMaster integration |

---

*Universe Creator — Unity 6000.5.2f1*
