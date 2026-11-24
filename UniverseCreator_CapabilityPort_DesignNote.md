# Capability Port — Triage and Design Note

What was ported from the studied reference toolkit, what was deliberately not, and why.

This is the document M2-1.1 asks for. It is the record of a **capability port, not a file
port**: the reference tree was read for architecture and UI approach, and only the
capabilities the terrain pipeline actually needs were re-implemented, in our own idiom,
under our own names, against our own `Generator` base.

No code, identifier, comment text or menu structure was copied. The reference path is not
recorded anywhere in this project — this module ships, and a tree that exists only on one
machine is not part of the deliverable.

---

## 1. The count, reconciled

M2-1.2 asked whether the figure was 32 or 41. Both are correct; they count different things.

The analysis covers **41 translation documents**. Removing the ones that are not runtime
PCGEx folders:

| Removed | Count | Why it is not a runtime folder |
|---|---|---|
| `*Editor` companions — Collections, Core, Clusters, ElementsMeta, Foundations, Properties, PCGExtendedToolkit | 7 | Editor-side UI for a runtime folder already counted |
| `CollectionsUncooked` | 1 | A build variant of `Collections`, not a separate capability |
| `ThirdParty` | 1 | Vendored third-party code, not a PCGEx module at all |

`41 − 7 − 1 − 1 = 32` runtime folders. The 32 counted on disk and the 41 documents are the
same set seen at different granularity. **Reconciled; no folder is unaccounted for.**

---

## 2. The architectural gap, and what it forced

Every translation document in the reference analysis targets the same architecture:

> *"aggressive shift … to unmanaged, `[BurstCompile]`-enabled Unity C# Job systems … strict
> enforcement of `Unity.Mathematics` … `NativeArray<float>`, `NativeSlice<byte>` … Unity's
> DOTS Job System with Burst Compilation."*

Universe Creator is the opposite by deliberate design: **managed C# throughout** — flat
`float[]`, stateless math functions, `System.Threading.Tasks`. No DOTS, no Burst, no
`NativeArray`, no `Unity.Mathematics`.

That is not a stylistic preference. A terrain node here must be a pure function of absolute
world position and seed, evaluated on a background thread, re-entered constantly as the
25+1 chunk grid follows the player. Flat managed arrays with no allocation churn hit that
contract, run on every platform the toolkit targets including mid-range mobile, and require
no additional package dependency in a customer's project.

**The consequence for the port:** a file-level translation was never available. Every
capability adopted below was re-derived from the *idea*, not transcribed from the source.
Anything whose value lay in its DOTS execution model rather than in its algorithm had no
value to transfer at all.

---

## 3. Tier 1 — ported

Six capabilities the terrain pipeline genuinely needed. All shipped.

| Capability | Why the pipeline needs it | Where it lives now | Consumed by |
|---|---|---|---|
| 3-D noise basis | Caves, overhangs and volumetric warping need noise in three dimensions, not a heightfield | `Core/Runtime/Math/Noise3D.cs` | `Holes Output`, volumetric warping |
| Polyline resample / smooth / offset | River courses and shorelines are polylines that must be resampled, smoothed and offset | `Core/Runtime/Math/Splines/Polyline.cs`, `SplineOperations.cs` | `UCWaterBody` river courses, `River Network Trace`, `Snap to Ground`, `Profile Smoothing` |
| Point sampling of fields | Scatter must sample the heightfield and masks at arbitrary non-grid positions | Sampling helpers on the matrix types | Every scatter node |
| Spatial hash / nearest neighbour | Min-spacing scatter is O(n²) without an acceleration structure | `Core/Runtime/Math/Spatial/SpatialHash.cs` | `Min Spacing` on all scatter |
| Poisson-disc distribution | Blue-noise placement with a guaranteed minimum radius | `Core/Runtime/Math/Spatial/PoissonDisc.cs` | `Poisson Disc Scatter` and all instance placement |
| Predicate filtering of point sets | Scatter exclusion by arbitrary combined predicates | `Core/Runtime/Math/Filters/ScatterFilter.cs` | `Exclusion Mask`, `Slope Altitude Exclusion`, `Composite Filter` |

**Blending** was on the Tier 1 list as "already partly covered; fill gaps." The gaps were
filled inside the existing nodes rather than as a new subsystem: the `Blend` node's mode set
(Add, Subtract, Multiply, Screen, Overlay, Difference, Lerp, Min, Max), the smooth-min and
smooth-max on `Minimum`/`Maximum`, and `Colour Math` in the texture graph. A separate
blending operator framework would have been a second way to do what `Blend` already does.

---

## 4. Tier 2 — ported, because the consumers now exist

M2-1.3 said to port the cluster diagram builders *when* the Tectonic Plate and Watershed
nodes were implemented, and not before. Those nodes shipped, so the geometry did:

| Capability | Consumer that justified it |
|---|---|
| Voronoi cell decomposition | `Tectonic Plates`, `Tectonic Plate Seed`, `Voronoi`, `Voronoi Rock Strata` |
| Basin segmentation from a connected-region solve | `Watershed Basin Segment`, `Lake Fill` |

Both are implemented directly against our matrix types on flat managed arrays. Neither
imports a general cluster framework — a Voronoi decomposition over a heightfield grid does
not need a graph library, and adding one would have brought a second graph system into a
project that already has its own.

Delaunay triangulation, convex hull and dual-graph construction were **not** needed by any
node that shipped, and were not ported. If a future node needs them, that is when they get
written.

---

## 5. Tier 3 — deliberately not ported

Twenty-three folders. The reason is the same for all of them, and it is worth stating once
plainly:

> These are engine plumbing for a general-purpose point-graph framework. Universe Creator
> already has its own graph, evaluator, port model, evaluation cache and serialisation.
> Porting theirs would mean **two competing graph systems in one project** — two node
> registries, two caches, two ways to express the same operation, and a permanent question
> about which one a given feature belongs in.

| Not ported | What it is | Why not |
|---|---|---|
| `PCGExCore`, `PCGExFoundations`, `PCGExtendedToolkit` | The framework's own core, base types and module bootstrap | We have `Graph`, `Node`, `NodeCatalog`, `PortType`, the evaluation cache and the per-pass evaluator already |
| `PCGExGraphs`, `PCGExProperties`, `PCGExCollections` | Graph data model, property bags, asset collections | Duplicates our graph model and Unity's own asset handling |
| `PCGExElementsClustersRefine`, `…Relax`, `…Decomp` | Cluster post-processing | No consumer; refining a cluster graph we do not build |
| `PCGExElementsPathfindingNavmesh`, `PCGExHeuristics` | Navmesh pathfinding and A* heuristics | Terrain generation does not path-find. River courses follow the flow field, which is a gradient descent, not a search |
| `PCGExElementsBridges` | Connecting point clusters with bridge geometry | No terrain consumer |
| `PCGExElementsClipper2` | 2-D polygon boolean clipping (vendored Clipper2) | Water outlines are convex-ish polygons snapped to ground; they do not need general boolean clipping, and vendoring a third-party library into a product we ship is a licensing and support liability for one unused feature |
| `PCGExMatching`, `PCGExPickers` | Point matching and selection strategies | Our scatter selects by weighted prototype index; a strategy framework is more machinery than the problem has |
| `PCGExElementsActions`, `PCGExElementsMeta` | Deferred action lists and metadata attribute plumbing | Our nodes carry typed fields; there is no attribute-bag layer to plumb |
| `PCGExElementsTensors` | Tensor field vector guidance | `Prevailing Wind` and `Direction` cover the two vector fields terrain actually uses |
| `PCGExElementsTopology` | Topology extraction / mesh from clusters | We produce native `TerrainData`, not generated topology. This is the NO CUSTOM MESH TERRAINS rule |
| `PCGExElementsProbing` | Ray/shape probing of point sets | Our sampling helpers cover what scatter needs |
| `PCGExElementsFloodFill` | Generic flood fill over point graphs | `Lake Fill` implements the one flood fill terrain needs, over a grid, with a spill-elevation solve |
| `PCGExElementsShapes` | Shape generators for point layouts | `Simple Form` covers the analytic shapes terrain uses |
| `PCGExSpatialDomains` | Spatial domain abstraction | We have one spatial domain: absolute world metres. An abstraction over one case is a cost with no benefit |

### The UI rule

**No "PCGEx" menu group appears anywhere in this system.** The reference editor exposes one
by scanning folder names; that leaks another asset's internal structure into the user's UI.
Everything ported is filed under our own categories — Creators, Modifiers, Selectors,
Combiners, Scatter, Utility, Biome, Texture, Surface, Water — chosen by what the node *does*
for a terrain author, not by where the idea came from. The `NodeCatalog` is reflection-based
and groups by `NodeCategory`, so there is no folder-name path into the search window at all.

---

## 6. What the study produced beyond the port list

Reading the reference tree changed some decisions that were not on the port list. Recording
them because they are the actual return on the exercise:

**Ghost margins as a first-class concept.** The strongest idea in any tile-based generation
architecture is that a tile must be computed with valid neighbourhood data and trimmed
afterwards, rather than patched at the seam. Every spatial node here — blur, sharpen,
erosion, distance field, thermal talus, occlusion — reads past the tile edge and is trimmed
before write. This is why chunk edges are seamless, and it is cheaper and more correct than
any stitching pass.

**Filtering as composition, not as a node chain.** The reference's predicate filters showed
that combining four range tests through four selectors and three combiners is the wrong
shape. `Composite Filter` does height, slope, aspect and mask in one node with All / Any /
Average — one node instead of seven, and one evaluation instead of seven.

**Separating a field from its application.** `Domain Warp Field` emits warp offsets;
`Domain Warp Apply` consumes them. One warp can then be applied identically to several
heightfields, which a self-contained warp node cannot express.

**Uber-shader over generated HLSL** (the M2-2.1 decision, recorded here as required). The
alternative — generating HLSL per graph and compiling at edit time — means shipping a shader
compiler, a variant cache, and a class of failure where a customer's graph produces code
that will not compile on their platform. `UC_TerrainLit` gates every feature, but only
`_UC_SLOPEBLEND_ON` and `_UC_SNOW_ON` are real keywords; the rest branch on a uniform float.
Fourteen keyword toggles would be 16 384 variants multiplied by URP's own lighting matrix.
A uniform branch costs one scalar compare with no wave divergence, because every pixel in a
wave takes the same branch. Slope and snow earn keywords because each is six texture
fetches. **Decision: uber-shader with two keywords. Implemented.**

**Realism comes from physical constants, not from more nodes.** The clearest lesson was
negative: capability count is not quality. What makes terrain read as real is that the
numbers mean something — the angle of repose is 35° because dry soil cannot hold steeper;
snow slides off above 50–60°; Manning's *n* is 0.035 for a natural stream with stones;
whitewater appears when the Froude number crosses 0.8; deep water is nearly black because
absorption is exponential. Those constants are in the defaults throughout, and they are what
a user gets before touching anything.

---

## 7. Portability and IP constraints observed

- **Nothing in this project reads an absolute path.** Every path is project-relative under
  `Assets`. The system is built to be sold and to run on machines that are not the author's.
- **No Python, and no external tooling, inside the Unity project.** The extraction scripts
  used to produce this analysis live outside the project tree and are not shipped.
- **No third-party code is vendored.** Every ported capability is an independent
  implementation.
- **URP only.** One render pipeline, verified by `Check Shaders`.
- **Managed C# only** — no DOTS, no Burst, no `NativeArray`, no `Unity.Mathematics`.
- **Native Unity `Terrain`, `TerrainData` and `TerrainCollider`** are the deliverable. No
  custom mesh terrain.

---

*Filed as the M2-1.1 output. See `UniverseCreator_Documentation.md` for the developer
manual, and `UniverseCreator_Module2_TaskList.md` for item-level status.*
