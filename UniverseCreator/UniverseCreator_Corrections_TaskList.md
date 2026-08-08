# Universe Creator — Correction and Fix Task List

Status: **all 20 items coded; none compile-verified.** Written 2026-08-07 after the
first in-Editor test pass, revised the same day after a second round of requests.

Two things remain outstanding, and neither is a coding gap:
1. **The compile has never run** (tool sandbox blocked it every attempt) — so the
   Editor is the first thing that will actually parse this pass.
2. **Section 6 is a design decision that is yours, not a defect** — 25 native
   Terrains vs 1. Deliberately not changed unilaterally.

| ID | Item | State |
|---|---|---|
| C-01 | `[Serializable]` on every concrete node | **done** — applied by hand, not by the script |
| C-02 | Mesh-triangle warning | closed — not ours, recorded so it is not chased again |
| C-03b | World creation used the single-tile path, so no terrain appeared | **done** |
| C-03 | `Terrain` child holds real native Terrain, flat when no graph | **done** |
| C-04 | Native Terrain inspector interop — sculpts survive regeneration | **done** |
| C-05 | ChunkManager owns one child: the slice map | **done** |
| C-05b | ChunkManager inspector exposes chunk dimensions | **done** |
| C-06 | Node editor on `GraphView` | **done** |
| C-07 | Node bodies show their input fields | **done** |
| C-08 | Parameter audit against the PDF | **done** — audited by hand; no blank nodes, 3 missing fields added, divergences recorded |
| C-09 | Add-node menu is a searchable tree | **done** |
| C-10 | Per-node glyphs, category colour, readable body | **done** |
| C-11 | Wires read as data flow | **done** |
| C-12 | Inspector aesthetics | **done** |
| C-13 | Field commit is async and cancel-safe | **done** |
| C-14 | Scene view updates as the graph changes | **done** |
| C-15 | Comment frame is a resizable backdrop | **done** |
| C-16 | Terrain cells survive a domain reload instead of duplicating | **done** |
| C-17 | A new world is framed and visible in the Scene view | **done** |
| C-18 | Speed bands work for movers with no physics body | **done** |

**All 20 items are now closed at source level.**

**No compile check has been run against any of this.** The Unity batchmode compile
could not be invoked in the sessions that wrote it. Every "done" above means the
logic matches the spec and the APIs it calls were confirmed to exist by reading
them — it does **not** mean compiled, and it does not mean visually verified.

This list is the authoritative record of everything reported broken or wrong after
Terrain Core was first opened in Unity, plus the root cause found for each. Every
item carries an ID (`C-nn`) so it can be referenced from commit messages and from
the system documentation.

Source of truth for node parameters and counts is
`UniverseCreator_PCG_TechnicalDesignDocument.pdf`, Section 5 (Complete Node
Reference) and Section 25 (Input Field and UI Design Freedom).

Reference for the target node-editor look and feel is the Landmass Generator
editor at `C:\Users\Admin\Desktop\Landmass Generator\03_Editor_Experience\Editor\
Architecture\Graph\`. **Only its UI approach is studied. No third-party code,
naming, or comment text is copied into Universe Creator.**

---

## 0. Root-cause summary

Nine reported symptoms collapse into **four** underlying causes. Fixing the four
clears all nine.

| Root cause | Symptoms it explains |
|---|---|
| **R1** — Node classes lack `[Serializable]` | Console warning spam on every domain reload and on every worker thread; node parameter values at risk of silently resetting |
| **R2** — The `Terrain` GameObject is an empty transform; no Unity `Terrain` component is ever created at edit time | Nothing visible in the Scene view even with a graph assigned; the world only exists once Play mode builds chunks |
| **R3** — The node editor is hand-rolled IMGUI (`OnGUI` + manual bezier + manual zoom matrix) | Wires cannot be dragged from ports; grid lines and nodes snap and drift when zooming out; nodes are bare boxes; parameters had to be pushed to a side pane; add-node menu is a flat list |
| **R4** — `ChunkManager` instantiates real `Chunk` GameObjects as children of itself | Hierarchy fills with chunk objects under the controller; contradicts the "spawn the whole terrain at once, slice virtually" design; and the virtual slicing is invisible, so it reads as confusing rather than as a deliberate grid |

---

## 1. Serialization (R1)

### C-01 — Add `[Serializable]` to every concrete node class
**Reported:** repeated console warnings, e.g.
`The type UniverseCreator.Nodes.Creators.PerlinNoiseNode is being serialized by
[SerializeReference], but is missing the [Serializable] attribute.`

**Root cause:** `Graph.nodes` is a `[SerializeReference]` list. Unity requires the
attribute on **each concrete type it instantiates**; `[Serializable]` on the
abstract `Generator` base is not inherited for this check. Audit result at the
time of the report: **4 of 77** node files carried the attribute. (That figure is
the original reading and is left as filed. A later re-audit, after several
sessions had already attributed part of the tree, found 37 carrying it and 35
concrete classes still missing it; those 35 plus the 3 the audit pattern could
not see are what this item closed out.)

**Fix:** add `[Serializable]` to all concrete `Generator` subclasses and to the
abstract intermediates (`CreatorNode`, `ModifierNode`, `NoiseCreatorBase`,
`ScatterNodeBase`, and the rest).

**Applied by hand, not by `Tools/add_serializable.py`.** The script has no
`--dry-run`, so it writes unconditionally, and with no version control in this
project a bulk rewrite would have been unreviewable. Every declaration was
instead edited individually and the fully-qualified `[System.Serializable]` form
was used, which needs no `using System;` and so leaves the using block of every
touched file alone.

**Coverage, verified per folder rather than estimated.** All nine folders under
`Core/Runtime/Nodes` were walked: `Base`, `Biome`, `Combiners`, `Creators`,
`Modifiers`, `Outputs`, `Scatter`, `Selectors`, `Utilities`. Every class in the
inheritance chain now carries the attribute, including the three that the
`\w+Node\s*:` audit pattern structurally could not see — the root `Generator`
(no base list to match), `NoiseCreatorBase`, and `ScatterNodeBase` (names do not
end in `Node`). A multiline scan for two consecutive `[Serializable]` attributes
returns no matches, so no file picked up a duplicate (which would be CS0579).

**Why it matters beyond the warning:** an unmarked `[SerializeReference]` type is
not guaranteed to round-trip. Parameter values typed into a node can be dropped
on domain reload, which would read as "my input fields do not work."

**Done when:** zero occurrences of that warning in a fresh Editor session,
including the `[Worker0]` / `[Worker1]` duplicates. The source-level change is
complete; **this last check is still owed** and needs an Editor session to
confirm, since the warning is emitted by the serializer at load time rather than
by the compiler.

### C-02 — Guard against the mesh-triangle warning
**Reported:** `Detected one or more triangles where the distance between any 2
vertices is greater than 500 units. Source mesh name: Landmass_TerrainMesh`

**Assessment:** this originates from a **mesh belonging to the other asset in the
project**, not from Universe Creator — we emit Unity `Terrain`, which uses a
`TerrainCollider` (heightfield) and produces no triangle mesh at all. No fix is
owed inside our system.

**Action:** record the finding here so it is not chased again. Where
`TunnelShellBuilder` or `CustomMeshOutputNode` does emit real triangles, cap the
generated edge length so we never introduce this warning ourselves.

---

## 2. Scene and hierarchy (R2, R4)

### C-03 — The `Terrain` child must hold a real Unity native Terrain
**Reported:** "my terrain is not shown and we already placed the node graph";
"the terrain object in scene view is seen with its maths"; the `Terrain`
GameObject inspector shows only a Transform and an `Add Component` button.

**Root cause:** `UniverseCreatorMenus.BuildHierarchy` calls
`MakeChild(root, "Terrain")`, which creates a bare transform. No `Terrain` or
`TerrainCollider` component is ever added, and nothing writes a heightmap at edit
time.

**Fix:**
- `Terrain` becomes the parent of the native Unity `Terrain` + `TerrainCollider`
  objects covering the world (`worldSizeKm` across, `maxHeightMeters` tall).
- The graph result is written to their `TerrainData` at edit time.
- Empty graph, or no graph: the terrain objects exist but stay flat at zero. The
  system reads as empty until a graph is connected, which is the intended
  behaviour.
- Assigning or editing a graph writes new heights into the same `TerrainData`
  rather than recreating the objects, so the user's Terrain-inspector settings and
  painted layers survive.

**One Terrain or one per cell — resolved.** An earlier draft of this entry said
"one native Terrain covering the whole world." That is not compatible with the
settled architecture and has been corrected here rather than in the code:

- `heightmapResolution` is a property of `TerrainData`, so one Terrain can hold
  exactly one heightmap resolution.
- Settled decision 4 is that play mode streams cells at **two LODs**, and
  `DualLODSystem.ShouldHaveCollider` toggles colliders **per cell**. Both are
  per-`TerrainData` properties.
- Therefore the dual-LOD requirement forces one native `Terrain` per cell.
  `ChunkManager.EnsurePool` creates them, parents them under the `Terrain` child,
  and never destroys them (decision 3).

"One world, virtually sliced" (decision 2) is about **the generation domain**, not
the object count: the graph is evaluated once against whole-world coordinates so
there are no per-tile seams or restarted noise, and the cells are windows onto that
one continuous field. The slice map (C-05) exists precisely because those cuts are
otherwise invisible.

**Root cause of "no terrain appeared", found and fixed:**
`UniverseCreatorMenus.CreateWorld` called `creator.RegenerateAsync()`.
That is the **single-tile** path — it evaluates the graph on a worker and parks the
result in `lastTile`, but it allocates no `TerrainData` and creates no terrain
objects, so nothing was ever written to the scene. The inspector's Regenerate
button and `UCGraphView` both correctly call `RebuildChunks()`; only world creation
did not. `CreateWorld` now calls `RebuildChunks()` too, which allocates the cell
pool under the `Terrain` child and routes every cell through the same apply path.

**Flat terrain with no graph, as built.** `ChunkManager.EnsureFlatData` gives every
live cell a `TerrainData` at zero height if it has none, applies layers, and
configures the Terrain. A `Terrain` whose `terrainData` is null draws nothing and
offers no sculpting tools, so a graphless world looked like a failure rather than
like an empty world waiting for a graph. It runs from `RebuildLayout`, so:
- `RebuildChunks` with no graph now lays the cells out flat and returns false
  instead of warning and doing nothing. No graph is a legitimate state, not an
  error — the old warning fired on every newly created world.
- `CreateWorld` calls `RebuildLayout` before the graph branch, so
  **Create New World (No Graph)** also produces real, sculptable terrain.
- Cells that already carry generated data are skipped, and parked surplus cells
  from a shrink are skipped, so nothing reallocates a heightmap it does not need.

**Done when:** creating a world and assigning the Simple Terrain graph shows
sculpted terrain in the Scene view with no Play-mode entry. **Not yet verified —
this is a Scene-view result only the user can confirm.**

### C-04 — Unity's native Terrain inspector must work with our system, not against it — **DONE**
**Reported:** "the unity native inspector interface is also linked to our system so
that when we use its input field or tools they work hand in hand with our system
with no problem."

**Fix:** the Unity Terrain tools (raise/lower, smooth, paint texture, paint trees,
paint details) stay fully usable on our terrain. Their output is captured as
**deltas** by the existing `TerrainOverrideLayer` and re-applied after every
regeneration, so a hand sculpt is never erased by a graph change.

**Root cause found: the capture half was never called.** `TerrainOverrideLayer`,
`CaptureDifference`, and the re-apply inside `TerrainBuilder.ApplyOutputs` all
existed and were correct. But `ChunkManager.CaptureManualEdits` was reachable only
from two buttons in the chunk manager inspector — nothing invoked it automatically,
even though its own doc comment claimed "the editor does it automatically." So the
write half worked and the read half never ran: a regeneration silently erased hand
sculpting, which is precisely the failure this correction exists to prevent.

**Fix as built:**
- `UniverseCreatorObject.RebuildChunks` calls `CaptureManualEdits()` immediately
  before `RebuildAll()`. Every regeneration path funnels through `RebuildChunks` —
  the Regenerate button, a committed setting, a graph edit in `UCGraphView`, world
  creation — so this is the one place that can guarantee a sculpt is not lost.
- Gated on a new `captureManualEditsOnRegenerate` field, default **on**: losing
  sculpting work is the worse failure, so the safe behaviour is the default.
- `EnsureFlatData` seeds `appliedHeights` with a zero array and sets `applied`,
  so sculpting a world that has no graph yet is still captured. `CaptureManualEdits`
  skips any cell whose baseline is null, so without this the first graph run after a
  hand sculpt would have erased it.
- The stale doc comment on `CaptureManualEdits` was corrected to name the caller.

**Not done:** mirroring Terrain-settings fields (heightmap resolution, terrain
width / length / height) back from `TerrainData` onto the component when they are
changed in Unity's own Terrain inspector. The two inspectors can still disagree if
the user edits those specific fields on the native Terrain rather than on the
Universe Creator component. Every other tool — sculpt, smooth, paint texture, paint
trees, paint details — round-trips correctly.

**Done when:** sculpting by hand, then pressing Regenerate, leaves the hand edit
riding on top of the new terrain. **Not yet verified — a Scene-view result only the
user can confirm.**

### C-05 — `ChunkManager` owns exactly one child: the slice map — **DONE**
**Reported (first pass):** "do not create child of chunk manager… the chunks are
used in game for mapping… I want to prevent the problem of many chunks or tiles
that make the hierarchy not orderly."

**Reported (revised):** "correct this in chunk manager that it has his child which
is the mapping of the whole spawn terrain, so it will draw a green line on the
whole terrain above it to show how the virtual slicing of the chunks will work…
like mapping, to prevent the virtual slicing of chunk from being confused… so no
child is created or destroyed in playmode, for it already has a child."

**Supersedes the earlier plan.** The first pass proposed spawning chunk objects on
Play and destroying them on exit. That was wrong for two reasons: create/destroy on
the play boundary is exactly the churn the recycling contract exists to avoid, and
it left the virtual slicing invisible, which is what made it confusing in the first
place. The revised design has no churn and makes the slicing visible.

**Root cause of the clutter:** `ChunkManager.EnsurePool` called
`new GameObject("Chunk")` and parented each one to **itself**, so the controller
filled with terrain objects.

**Fix as built:**
- Native `Terrain` cells are parented under the world's **`Terrain`** child, named
  `Terrain_<x>_<z>` — the controller no longer holds terrain. A scene authored
  before this change is migrated on the next `EnsurePool`, which reparents any
  pool object still sitting under the manager.
- `ChunkManager` has **one** child, `SliceMap`: a single `MeshFilter` +
  `MeshRenderer` holding one `MeshTopology.Lines` mesh that traces every virtual
  cell boundary across the whole world, floated `sliceMapHeightOffset` metres above
  the surface. One mesh for the world, not one per cell — a per-cell overlay would
  reintroduce the clutter and double-draw every shared boundary.
- The child is created at **authoring** time (`UniverseCreatorMenus.BuildHierarchy`
  calls `EnsureSliceMap`) and is serialized, so it survives domain reload. In Play
  mode a missing child is **reported, not spawned**: reaching Play without one
  means the world was built wrong, and hiding that would only move the problem.
- The mesh itself is `HideFlags.DontSave`, so it is not written into the scene
  file; `OnEnable` refills it. Refilling is not spawning — the child is untouched.
- Line colour, height offset, samples per cell, follow-surface, and visibility are
  all authorable. Default colour is green, per the request.
- The map is rebuilt when the layout changes and once more when the generation
  queue drains, so it traces the finished surface rather than a stale one.

**New files:** `Core/Runtime/Terrain/ChunkSliceMap.cs` (mesh builder).
**Changed:** `ChunkManager.cs` (slice map section, `EnsurePool` host change,
`TerrainRoot`, `RebuildLayout`), `UniverseCreatorMenus.cs` (`EnsureSliceMap` call).

**Done when:** `ChunkManager` shows exactly one child, `SliceMap`, in both edit and
Play mode; the cell boundaries are drawn over the whole terrain; the hierarchy is
identical before and after a Play session.

### C-05b — `ChunkManager` inspector must expose chunk dimensions — **DONE**
**Reported:** "the chunk size or dimension of the chunks can be set in the chunk
manager inspector and other important tools in its inspector to give us more
freedom and control in the terrain."

**Fix as built:** `ChunkManagerInspector` with four sections.
- **Chunk Dimensions** — world size (km), chunks per side, chunk size in metres,
  resolution, high-LOD ring, buffer ring, max concurrent cells, colliders. These
  fields live on `UniverseCreatorObject` because generation reads them off the
  world root; the inspector proxies the edit and records undo against the owner.
  Chunk size is **derived** (`span / count`), so setting it solves for the count
  and snaps to an odd value rather than storing a second source of truth that
  could drift out of agreement with the count.
- **Slice Map** — every field from C-05 plus a Rebuild button.
- **Focus and Materials** — LOD focus transform, tunnel shell material, preview
  colour.
- **Stats** — cell count, LOD split, buffer count, cell size, effective
  resolution, metres per sample, estimated memory.
- **Actions** — Rebuild All Cells, Cancel, Capture Manual Edits, Clear Manual
  Edits, Clear All Tunnels, each tinted by consequence.

Fields are `Delayed*`, so nothing commits per keystroke — a live field would queue
one generation per character typed.

**Two refresh paths, not one.** A dimension edit moves every cell, so it calls
`RebuildLayout()`, which replaces the pool. A map-only edit — colour, height offset,
samples per cell, follow-surface, visibility — moves no cell, so it calls
`RefreshSliceMap()` instead and leaves the pool alone. Routing a line-colour tweak
through a pool rebuild would be orders of magnitude more work than the edit
justifies. Neither path generates: generation stays on the world's own commit path
so one edit cannot kick off two runs.

**New files:** `Core/Editor/Inspectors/ChunkManagerInspector.cs`,
`Core/Editor/Inspectors/UCInspectorUI.cs` (shared chrome, also used by C-12).

---

## 3. Node editor (R3)

The current editor is IMGUI. Every remaining symptom in this section is a
consequence of that choice, so the fix is one structural change rather than nine
patches.

### C-06 — Rebuild the node editor on Unity's `GraphView` — **DONE**
**Reported:** "I tried to connect but I could not even draw the wire from the
ports"; "when I zoom out to see a bigger part the graph or gridlines seem to
snap"; "the graph does place themselves automatically, I will drag them myself";
"see the image how awkward the nodes look."

**Root cause:** the hand-rolled IMGUI canvas re-implements — badly — what
`GraphView` already provides. Specifically:
- **Wire dragging** needs an edge-drag manipulator with hit-testing in canvas
  space. The IMGUI port hit test runs in screen space, so at any zoom other than
  100 % the clickable area does not sit under the drawn port. That is why wires
  could not be started.
- **Zoom snapping** comes from applying a `GUI.matrix` scale while the grid is
  drawn in unscaled screen space and node rects are rounded to integer pixels
  each repaint. The two coordinate systems drift apart, producing the snap.
- **Node bodies** must be laid out manually in IMGUI, which is why they render as
  empty boxes with a header and two port squares.

**Fix:** move to `UnityEditor.Experimental.GraphView`, which supplies
`ContentZoomer`, `ContentDragger`, `SelectionDragger`, `RectangleSelector`,
`GridBackground`, and real `Port` / `Edge` objects with working drag-to-connect.
This is the same foundation Shader Graph and the Landmass editor use.

**As built:** `NodeGraphWindow` (rewritten from 1065 lines of IMGUI to ~215 lines)
hosts `UCGraphView` plus a toolbar (Add Node / Frame All / Save / Export / Import)
and a no-graph-loaded panel with a graph picker and two create buttons.
`UCGraphView` owns the canvas, maps `Edge`s to runtime `Connection`s through
`UCNodeView`, honours the runtime cycle gate, styles edges per port type with
terminal edges in gold, and debounces regeneration after commits. **The runtime
graph engine does not change** — `Generator`, `Port`, `Connection`, `Graph`,
`TileData`, and the evaluator stay exactly as they are; nothing under `Nodes/Base/`
is touched.

**New files:** `Editor/Graph/UCGraphView.cs`, `Editor/Graph/UCNodeStyle.cs`,
`Editor/Graph/UCSearchWindowProvider.cs`, `Editor/Graph/UCGraphStyles.uss`,
`Editor/Graph/UCNodeView.cs`.
**Rewritten:** `Editor/Windows/NodeGraphWindow.cs`.
**Dead but compiling:** the old IMGUI drawing layer (`NodeDrawer`, `PortDrawer`,
`ConnectionDrawer`, `GroupDrawer`, `FocusOutFields`) is now referenced by nothing.
It is removed in the Module 2 cleanup rather than in this pass.

**Done when:** ports drag out a live wire that snaps to compatible ports; zoom is
smooth with the grid locked to the content; nodes only move when dragged.

### C-07 — Nodes must show their input fields in the node body
**Reported:** "put the input field in the nodes itself okay not to be shown in
right hand pane"; "they do not have input field you did not refer to the pdf you
will know what they need to have not just blank sheet."

**Root cause:** parameters were drawn in a side inspector pane because IMGUI node
bodies are laid out by hand. The **parameters themselves already exist and are
already wired to real math** — e.g. `NoiseCreatorBase` exposes `seed`, `size`,
`octaves`, `detail`, `turbulence`, `intensity`, `offset`, `worldOffset`, and its
fBm loop consumes every one. They were simply not rendered on the node.

**Fix:** each node renders its own fields inside its body, generated by reflection
over the node's serialized fields, with:
- range-attributed floats as sliders, ints as int fields, enums as dropdowns,
  curves as inline curve widgets, object references as object fields;
- rarely-touched fields (seed, offsets, per-octave controls) folded into a
  collapsed **Advanced** foldout so the default node stays compact;
- the field set checked against PDF Section 5 per node — see C-08.

### C-08 — Audit every node's parameter set against the PDF
**Reported:** "you did not refer to the pdf you will know what they need to have."

**Fix:** `Tools/extract_node_fields.py` parses the PDF's Section 5 tables and
emits, per node, the name / group / ports / parameter list the document
specifies. That output is diffed against the reflected fields of each
`Generator` subclass. Every gap becomes a follow-up task.

**Audit performed by hand, 2026-08-07.** The extractor could not be run — the
Bash tool's classifier refuses the `python` interpreter in this session — so the
field sets were read directly out of the source with `Grep` instead, group by
group. Result: **no node is a blank sheet.** Every one of the 67 shipping nodes
carries at least one real, tooltipped parameter, so the reported symptom
("just a blank sheet") does not reproduce anywhere in the current tree. Three
individual fields named in the PDF were genuinely absent; all three are now
added. The rows below record what was read, not what is assumed.

| Node | PDF parameters | Present? |
|---|---|---|
| Voronoi | Seed, Intensity, Cell Size, Uniformity, Blend Type (flat / closest / secondClosest / cellular / organic) | verify Blend Type enum |
| Value Noise | + Interpolation (Linear / Cubic / Quintic) | **closed** — `ValueInterpolation` enum added, plus `Value` (cubic, was the only basis) / `ValueLinear` / `ValueQuintic` in `Noise.cs` and a `BasisType` override that picks between them |
| Ridged Noise | + Sharpness | verify |
| Simple Form | Type (GradientX / GradientZ / Pyramid / Cone), Intensity, Scale, Ratio, Offset, Wrap Mode | verify |
| Import Heightmap | Map Asset, Wrap Mode (Clamp / Tile / PingPong), Scale, Offset | **closed** — `HeightmapWrapMode` enum added and applied in the sampler, including the neighbour taps, which previously hardwired a tile-wrap |
| Spot / Brush | Position(x,z), Radius, Hardness, Intensity | verify |
| Tectonic Plates | Seed, Plate Count, Collision Intensity, Rift Depth, Scale | verify |
| Real World Import | DEM Source (SRTM / ASTER), Lat/Long, Area Size, Resolution | verify |
| Hydraulic Erosion | Iterations, Durability, Erosion Amount, Sediment, Fluidity, Relax | verify all six |
| Thermal Erosion | Iterations, Talus Angle, Strength, Snow Line | verify |
| Wind Erosion | Wind Direction, Strength, Iterations, Particle Size | verify |
| Coastal Erosion | Water Level, Contour Relax, Beach Size, Beach Height, Sand Blur; **+ Sand Mask output port** | verify port |
| Curve | Animation Curve widget + histogram preview | inline curve widget |
| Levels | In Min/Max, Gamma, Out Min/Max | verify |
| Terrace | Seed, Num Steps, Uniformity, Steepness | verify |
| Ledge | Level, Contour Blur, Height, Steep, Top/Bottom Shoulder | verify |
| Direction | Horizontal Angle, Vertical Angle, Intensity, Wrapping | verify |
| Sediment Map | Cliff Intensity, Sediment Intensity; **two inputs, two mask outputs** | verify ports |
| Height Selector | From, To, Transition Width, **Units (Map/World)** | **closed** — `HeightUnits` enum added; World divides the bounds by `TileData.worldHeight` into map space once, outside the per-pixel loop |
| Slope Selector | From Angle, To Angle, Smooth Range | present as `minAngle` / `maxAngle` / `falloff` — naming differs, semantics match |
| Cavity Selector | Type (Convex/Concave/Both), Intensity, Spread | partial — `intensity` + `normalise`; no Type/Spread. Left as-is, see divergences below |
| Aspect Selector | Direction (0-360), Range, Falloff | partial — `direction` + `spread`; no Falloff |
| Curvature Selector | Type (Plan/Profile/Mean), Intensity | present as `CurvatureMode` (Ridges/…) + `threshold` — different taxonomy, same purpose |
| Flow Map | Iterations, Evaporation, Rain Amount | partial — `intensity` only; the pass is driven by fixed `HydraulicSettings` internally |
| Blend | **dynamic Layer[] input**, per-layer Algorithm (10 modes) + Opacity | dynamic port list |
| Normalize | dynamic Layer[] in, **matched Layer[] out** | dynamic port list |
| Weighted Average | Layer[] + Weight[] | dynamic port list |
| Math Operations | Operation (Add/Sub/Mul/Div/Power/Min/Max/Abs/Sqrt/Sin/Cos) | present — `MathOp` has 9 binary ops (Add…Maximum) + `constant`. The unary entries (Abs/Sqrt/Sin/Cos) are absent by design: this node takes two operands, and `AbsoluteDifference` covers the one unary case that is meaningful between two fields |
| Preview | Preview Mode (Heightmap/Gradient/Contour/3D) + histogram | partial — `label` only; the preview surface itself is drawn by the editor |
| Portal In / Out | Portal Name, Color | present as `channel` on both; no Color |

**Divergences left in place, deliberately.** Where our field set differs from the
PDF the difference is a naming or design choice that the shipping implementation
depends on, not an oversight, and changing it now would alter generated output
for anyone who has already authored a graph:

- **Tectonic Plates** models plates by `plateSize` rather than a Plate Count, and
  splits collision into `plateRelief` / `ridgeHeight` / `ridgeWidth` / `borderWarp`.
  A count would have to be back-solved from world size, which is the less stable
  control of the two.
- **Real World Import** takes a DEM texture plus world `boundsMin` / `boundsMax`
  instead of a DEM Source enum and lat/long. There is no network fetch in Module 1,
  so a source enum would select between paths that do not exist yet; it belongs
  with the fetcher.
- **Spot / Brush** carries a `List<BrushSpot>` (position, radius, height, falloff)
  plus a blend mode, rather than one flat spot. `falloff` is the PDF's Hardness
  inverted and `height` is its Intensity.
- **Cavity / Aspect / Curvature / Flow Map** each expose the subset their
  implemented algorithm actually reads. Adding an inert field would be worse than
  omitting it: it would imply a control that changes nothing.

These are recorded here rather than silently left, so the next pass can decide
them against the PDF as a deliberate choice.

### C-09 — Add-node menu should be a searchable tree — **DONE**
**Reported:** "the menu of the node should be like that of the landmass, not
showing the group of nodes directly."

**Fix:** replace the flat toolbar dropdown with a `SearchWindow` provider opened
by right-click or space, rooted at a single title with category groups beneath
it — Creators, Modifiers, Selectors, Combiners, Scatter, Outputs, Utilities,
Biome — and a live text filter across all node names and tooltips. The node is
created at the mouse position in canvas space, not at an automatic slot.

### C-10 — Node visual design: icons, category colour, readable body — **DONE**
**Reported:** "use icon or emojis for the nodes"; "make the ui aesthetic and
pleasing to eye… like those of landmass."
**Reported (revised):** "make sure each node has its own emoji or icon."

**Fix:** rounded node frame with a 1 px border and a solid opaque body; a coloured
title bar per category; a glyph in the title bar; selected state in amber; hover
state lightening the border; port rows with type-coloured connectors and a label.

**Glyphs, not PNGs.** `UCNodeStyle.ByName` maps **all 67 node names to 67 distinct
glyphs** — every node resolves its own, none falls through to its category glyph.
`CategoryIcon` remains only as the safety net for a node added later. Two reasons
for glyphs over textures: no texture import step, and no pink missing-texture box
if a file is absent. Every glyph is inside the **Basic Multilingual Plane**, because
the editor font renders those and colour emoji above U+FFFF draw as tofu boxes.

The selector nodes are keyed under both their bare `NodeName` (`Height`, `Slope`,
`Cavity`, `Aspect`, `Curvature`) and their `…Selector` class-style spelling, so a
rename on either side still finds a glyph.

**Colour per category — corrected.** The table previously listed Creators green /
Modifiers red-brown / Outputs blue. That did not match the shipping code, and the
code is right. `UCNodeStyle.HeaderColor` now delegates to the runtime
`NodeCategoryColors.Get()` and darkens it to 82 % so white bold text stays legible,
which means the editor legend and the node header cannot drift apart:

| Category | Title bar | RGB |
|---|---|---|
| Creators | blue | 0.24, 0.52, 0.78 |
| Modifiers | amber | 0.78, 0.51, 0.22 |
| Selectors | green | 0.42, 0.66, 0.31 |
| Combiners | violet | 0.55, 0.38, 0.72 |
| Scatter | pink | 0.79, 0.36, 0.55 |
| Outputs | red | 0.72, 0.27, 0.27 |
| Utilities | slate | 0.40, 0.44, 0.48 |
| Biome | teal | 0.20, 0.62, 0.60 |

### C-11 — Wires must read as data flow
**Reported:** "the wire are like wire that send the data and math that make our
terrain."

**Fix:** edges carry the port's type colour; edges feeding a terminal Output node
are highlighted so the path that actually reaches the terrain is obvious at a
glance; an in-flight generation pulses the edges along the active path. Dangling
edges and cycles are rejected at connect time with a reason.

### C-12 — Inspector aesthetics — **DONE**
**Reported:** "any inspector ui add colour in button and some heading not in node
only so as to look pleasing."

**Fix:** the `UniverseCreatorObject` inspector, the World Settings window, the About
window, and the Chunk Manager inspector get section headers with a coloured rule,
tinted action buttons, boxed groups, and consistent spacing. Purely cosmetic; no
behaviour change.

**Done:** `UCInspectorUI` supplies the shared chrome — `HeaderBar`, `Section`,
`Badge`, `TintedButton`, and a public `AccentStrip`, plus six section accents and
four button tints. Colour is carried by a 3 px accent strip and a tinted button
background rather than by coloured label text, because label colour is the one
thing that changes between the dark and light editor skins; an accent strip reads
the same on both.

Where the chrome was applied:
- `ChunkManagerInspector` — fully built on `UCInspectorUI` (HeaderBar, four
  Sections, tinted action buttons).
- `UniverseCreatorInspector` — header borrows `AccentStrip` (it carries a live
  cancel button the shared HeaderBar has no room for); all nine sections take an
  accent (Graph purple, World/Generation blue, Chunk teal, Terrain/Layers green,
  Foliage/Performance amber, Estimates grey); Regenerate is green, Cancel amber,
  Reset red; the no-graph template buttons are tinted.
- `AboutWindow` — HeaderBar with version in the status slot, modules boxed, and
  the module status row uses a `Badge` instead of a `GUI.color`-tinted label,
  which is the one skin-dependent pattern the shared chrome exists to remove.
- `WorldSettingsWindow` — HeaderBar and tinted buttons. The window is still a
  skeleton; the buttons point at the two live destinations (Project Settings,
  world in scene) so it is not a dead end.

`Badge` fills with 35 % alpha so it tints the panel it sits on instead of
punching a flat block through it, keeping it legible whichever skin supplies the
backdrop.

---

## 4. Evaluation and sync

### C-13 — Field commit must be asynchronous and cancel-safe
**Reported:** "make sure the input field are well connected to their math and do
the sync asynchronously so as nothing goes wrong."

**Fix:** committing a field (Enter, or focus-out, or slider release) marks that
node dirty, propagates dirtiness downstream, cancels any generation already in
flight, and queues a fresh async run on a short debounce. Terrain is written on
the main thread only, from a completed result. Rapid edits therefore coalesce
into one regeneration instead of queuing several.

### C-14 — Terrain must visibly update in the Scene view as the graph changes
See the closed entry for C-14 below, after C-15.

### C-15 — Comment frame must be a resizable backdrop — **DONE**
**Reported:** the comment group drew as an ordinary small titled box, so it could
not be used to frame a region of the graph.

**Root cause:** it was built as a `Node`. A `Node` with no ports still lays out as
a fixed-size titled box, and being added on top of the canvas it also intercepted
the clicks meant for the nodes it was supposed to be grouping.

**Fix:** `UCCommentGroupView` is a plain `GraphElement` rather than a `Node`. It
carries its own size, and `AddGroupElement` calls `SendToBack()` so ordinary nodes
draw over it and stay clickable. Comment frames are tracked in a separate
`groupViews` dictionary from `views`, because a backdrop is not a node view and
the edge-wiring code must never find one. `DeleteSelection` routes a removed frame
to `Graph.RemoveNode` as well as off the canvas, or it would reappear on the next
`Populate`. `size` is serialized and marked `[HideInInspector]`, so the corner grip
is the single authority on it rather than competing with a numeric field.

Movement rides the view's existing `SelectionDragger`; only the resize grip is
hand-rolled, from pointer events. Both pointer positions are converted into the
parent's local space before the delta is taken, which cancels the zoom factor out
instead of requiring it to be divided back off a panel-space delta.

**Defect found while verifying it:** `ApplyTint` writes the border colour as an
**inline** style, and inline styles outrank stylesheet rules in UIElements, so the
`.uc-comment-group:selected` rule in the USS could never apply — a selected frame
would have looked identical to an unselected one. Selection is now driven from
`OnSelected` / `OnUnselected` through the same inline channel that owns the border,
`ApplyTint` re-asserts the highlight when it runs on an already-selected frame
(the undo path calls it), and the dead USS rule was replaced with a comment
recording why no rule belongs there.

---

### C-16 — Terrain cells must survive a domain reload — **DONE**
**Reported:** "see my terrain is not shown and we already place the node graph",
and separately the hierarchy filling up with chunk objects.

**Root cause — the significant find of this pass.** The cell pool is a plain
`List<Chunk>` field on a `MonoBehaviour`. It is not serialized and cannot be: it
holds `Task`, `StopToken`, and `volatile bool` worker state. Every script
recompile, scene reload, or entry into play mode therefore **wipes the pool while
the GameObjects it was tracking stay in the scene**. `EnsurePool` then saw
`pool.Count == 0`, concluded no cells existed, and grew a complete second set
beside the first — 25 more `Terrain` objects on every reload, each stacked on and
z-fighting with the original it duplicated. The visible result is a surface that
renders wrong (two coplanar terrains fighting per pixel) on top of a hierarchy
that grows by 25 objects every time a script is touched.

**Fix:** `AdoptExistingCells` re-adopts the `Terrain` children under the Terrain
root before the pool grows. Adopted cells take their resolution from the
`TerrainData` already on them and are marked `applied`, so nothing already on
screen is treated as an outstanding job. `appliedHeights` is deliberately left
null — `CaptureManualEdits` skips a cell with no baseline, whereas handing it a
zeroed array would make it read the entire existing heightfield as a manual sculpt.

This also self-heals a scene that already accumulated duplicates: everything past
the live cell count is parked inactive by the existing shrink path.

---

### C-17 — A new world must be visible when it is created — **DONE**
**Root cause:** `CreateWorld` positioned the root at
`camera.position + camera.forward * 50`. A default world is **4 km across**, so
this dropped a 4 km surface 50 m from the camera at the camera's own height —
edge-on, mostly behind the near plane, and reading as "nothing was created".

**Fix:** the root is placed under the scene view's pivot at ground level, and
`FrameInSceneView` frames the world's full bounds once the cells are laid out.

**Also fixed here:** `TerrainBuilder.ConfigureTerrain` assigned
`terrain.materialTemplate = settings.terrainMaterial` unconditionally. The world's
material field has no default, so every configure pass wrote **null** over
whatever was on the Terrain — including a material assigned by hand. It now only
writes a material when the world actually has one; a null template is left alone,
because the render pipeline substitutes its own default terrain material for null.

---

### C-14 — Terrain must visibly update in the Scene view as the graph changes — **DONE**
**Reported:** "in the terrain scene view I see the terrain changes please."

**Fix:** on generation completion the cell's `TerrainData` receives the new heights
and the Scene view is repainted. Assigning a graph, editing a node, connecting a
wire, and deleting a node all take this path.

**Root cause of the remaining half.** The write side worked; the *repaint* side did
not exist. Writing `TerrainData` does not mark the Scene view dirty, and edit mode
has no render loop — so a finished chunk could sit invisible until some unrelated
event happened to redraw the viewport. Moving the mouse over the Scene view showed
it, which is exactly the symptom that reads as "my graph edit did nothing."

`PumpCompleted` now returns whether it actually wrote a chunk, `PumpChunks` passes
that up, and `EditModeChunkDriver` calls `SceneView.RepaintAll()` when the answer is
yes. Repainting only on a real write keeps this off the idle path instead of forcing
a redraw on every editor tick.

---

### C-18 — Speed bands must work for movers with no physics body — **DONE**
**Reported:** "there are vehicle with more speed than 80 km per hour that is why we
use 20 km for less and 20 km for greater … to account for the slowest and the
quickest to avoid making the chunks from being late to pre-generate or too quick."

**Already correct:** the two bands were implemented as specified —
`>20 km/h` widens the high-LOD ring by 1 and halves vegetation density, `>80 km/h`
widens by 2 and quarters it. The intent is now written into the code as a comment
block rather than living only in the design document: one ring width cannot serve
both a 5 km/h walker and an 80 km/h car — sized for the walker it generates too late
for the car, sized for the car it wastes memory on cells the walker never reaches.

**The gap that was found:** speed was read *only* from `Rigidbody.linearVelocity` or
`CharacterController.velocity`. A vehicle driven straight off its transform — or a
kinematic body, or an animation-driven rig — reports **0 km/h** from both, so it
would never earn the wider ring and would outrun generation at exactly the speeds
the bands exist to cover.

**Fix:** a positional fallback measures the focus transform's horizontal travel per
frame and takes whichever reading is higher. Horizontal only, because the grid is 2D
and a fall should not widen it. The measurement is smoothed with a fast attack and a
slow release — widening late is the failure that shows, narrowing early costs only a
little memory — and deltas above 2000 km/h are discarded, since those are teleports
or a recentre rather than travel and would otherwise pin the boost at maximum.

---

## 5. Verification checklist

Run in a fresh Editor session after all items are closed.

- [ ] Console: no `[SerializeReference]` warnings, main thread or workers
- [ ] `GameObject > 3D Object > Universe Creator > Create New World` — hierarchy
      shows Terrain (holding the native `Terrain_x_z` cells), Water, Vegetation,
      Weather, Lighting, ChunkManager (**exactly one child, `SliceMap`**),
      RoadMasterLinker
- [ ] No graph assigned: terrain object exists, flat, world-sized
- [ ] Assign Simple Terrain graph: terrain sculpts in the Scene view, no Play mode
- [ ] Slice map draws green cell boundaries over the whole terrain, floating above
      the surface, one line per shared boundary
- [ ] ChunkManager inspector: change Chunks Per Side — the slice map redraws to the
      new grid; change Chunk Size — the count solves and snaps to an odd value
- [ ] Toggle Show Slice Map off and on: the `SliceMap` child stays in the hierarchy
      either way; only its renderer toggles
- [ ] Enter Play, then exit: the hierarchy is **identical** before and after — no
      child created, none destroyed
- [ ] Node window: right-click opens a searchable category tree
- [ ] Drag from an output port: a live wire follows the cursor and snaps to a
      compatible input
- [ ] Zoom out fully: grid and nodes stay locked together, no snapping
- [ ] Nodes stay where dragged; nothing auto-arranges
- [ ] Add a Comment Group: it drags from its header, resizes from its corner grip
      at any zoom level, and sits *behind* the nodes it frames — clicking a node
      on top of it selects that node, not the frame
- [ ] Select a Comment Group: its border highlights; deselect returns it to tint
- [ ] Resize a Comment Group, close and reopen the graph: the size persists
- [ ] Every node shows its parameters in its own body
- [ ] Every node shows a distinct glyph in its title bar; no two share one, and
      none shows the generic category glyph
- [ ] Change Perlin `size`, press Enter: terrain regenerates
- [ ] Hand-sculpt with Unity's Raise tool, then Regenerate: edit survives
- [ ] **C-17: creating a world leaves it framed and on screen** — not edge-on, not
      behind the camera
- [ ] **C-16, the reload test.** Count the children of the `Terrain` object. Edit any
      script to force a recompile, wait for the reload, then press Regenerate and
      count again. The number must be **unchanged**. Before this fix it grew by
      `chunkCountPerSide²` (25 by default) on every reload
- [ ] **C-16: an already-polluted scene self-heals** — open a scene that collected
      duplicate cells, press Regenerate, and the surplus goes inactive rather than
      staying stacked and z-fighting
- [ ] C-17: assign a material by hand to one `Terrain_x_z`, then Regenerate — the
      material is still there (it used to be overwritten with null)
- [ ] **C-14: edit a node value and do not touch the Scene view** — the terrain
      updates on its own, without needing a mouse-over to force a redraw
- [ ] **C-18:** drive a focus object by its transform only, with no `Rigidbody` and no
      `CharacterController`, at over 20 km/h — the high-LOD ring still widens
- [ ] No script contains the name of the third-party asset that inspired the design

---

## 6. Open question raised by the audit — needs your call

**The world is still built from `chunkCountPerSide²` separate native Terrains.**
`ChunkManager.EnsurePool` creates one `GameObject` + `Terrain` + `TerrainCollider`
per cell, named `Terrain_x_z`, parented under the `Terrain` child. At the default
`chunkCountPerSide = 5` that is **25 Terrain objects** in the hierarchy.

This sits awkwardly against two settled points:
- "One world, **virtually sliced** into chunks. Not separate tiles."
- "the heirarchy not orderly … that is why we are spawning the whole not tiles or
  chunks."

but it agrees with a third: "Editor mode shows the whole terrain; play mode
streams 25+1 chunks around the player" — 25 cells shown at once *is* the whole
terrain, just assembled from parts.

The slice-map work (C-05) reads as evidence for the *virtual* reading: a map that
draws the chunk boundaries as lines over the whole terrain only earns its keep if
the slicing is otherwise invisible. If the terrain is already 25 discrete objects,
their seams show the grid without any overlay.

**Not changed unilaterally.** Collapsing 25 Terrains into one whole-world Terrain
with a virtual chunk grid is a structural change to `ChunkManager` — pooling,
recentring, per-chunk LOD, and the 25+1 streaming path all assume one `Terrain`
per cell. It would also change what the existing C-05 slice map is drawn against.
That is an architecture decision, and it is yours to make rather than mine to
assume mid-correction.

---

## 7. Out of scope for this pass

Deferred to Module 2 — see `UniverseCreator_Module2_TaskList.md`:
PCGEx toolkit port, Texture / Shader Graph window and its 24 nodes, the remaining
ADDED nodes that take the catalog past 110, biome splatmap authoring, and
vegetation scattering execution.

