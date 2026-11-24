#  Universe Creator — Plugin Architecture (replace the Linker with a plugin system)

> **Status:** design spec, written 2026-10-11. **Code has not started.** This document is written
> FIRST, per the author's instruction; the linker teardown + plugin build happen only after it.
> **Grounding (hard):** Unreal plugin model fetched from
> https://dev.epicgames.com/documentation/unreal-engine/plugins-in-unreal-engine ; Unity plugin
> model from the official manual (managed vs native, Plugins folder, reflection). Nothing guessed —
> every UC mechanism below maps to a named Unreal or Unity concept and to a real file in our code.
> **Rule:** modernised for **Unity** (that is the engine we ship on); Unreal is a source of ideas,
> re-derived, not copied. Opt-in / zero-regression where it touches existing worlds.

---

## 0. WHERE THE UC SYSTEM OVERHAUL IS RIGHT NOW (resume map)

This plugin overhaul is inserted into the middle of `UC_SYSTEM_OVERHAUL_PLAN.md`. The plugin build
(PLG-1…7) is **DONE + GREEN**, so **resume that plan at Topic 5 → RIV-2 node wiring** (RIV-1b's CPU
droplet solver is done + green; its GPU compute path is still unbuilt). Exact state (last self-test
GREEN: 15304 checks / 0 failures / 0 CS / 0 warnings, 2026-10-13):

| Topic | Status |
|---|---|
| 1 Sub-engine (SE-3, SE-1/ENG-2, ENG-1a, ENG-4, ENG-3) | ✅ DONE + GREEN |
| 2 Geometry (GEO-1 Delaunay, GEO-2 Prim MST) | ✅ DONE + GREEN |
| 3 Vegetation (VEG-1,2,3,4,5,6,8,9,10,11,13 + GEO-3) | ✅ DONE + GREEN (VEG-12 = already satisfied) |
| 4 Biomes (BIO-1…BIO-10) | ✅ DONE + GREEN |
| **5 Rivers** | 🟣 **RESUME HERE** — RIV-1 ✅, RIV-2 core ✅, RIV-1b 🟡 (CPU droplet solver green via `RIV-ERO2`; GPU path unbuilt); **next unbuilt: RIV-2 node, RIV-3, RIV-4** |
| 6 Controls / 7 Perf / 8 Water / 9 Streaming / 10 Region+sim / PHASE E / PHASE M | ⬜ not started |

---

## 1. What a "plugin" means for Universe Creator

Universe Creator is a **PCG system on a sub-engine** (the generation core: `GraphEvaluator`,
`ChunkManager`, the math layer; the PCG node graph runs on it). Today a second product
(RoadCityMaster) is bolted on with a **Linker** — a hard-coded bridge that UC must know about. We
replace that with a **plugin system**: the sub-engine is the HOST, and any number of external
systems (RoadCityMaster, a buyer's own PCG, a future module) plug in by **implementing contracts the
host defines and registering at load** — the host never names any of them.

<!-- PLACEHOLDER-PLUGIN -->
The two defining constraints (both from Unreal, both right for us):
- **One-way dependency (Unreal's leveled rule).** Unreal: a component "can only depend on other
  Plugins or Modules at the same level or higher" — the engine can build with no project, so an
  engine module may never depend on a project module. Ours: **the sub-engine references no plugin;
  a plugin references only the host contract.** This is exactly why UC and RoadCityMaster stay two
  separate repos with no shared git history — the coupling points one way, plugin → host.
- **Discover-and-register, don't hard-wire (Unreal's auto-discovery + Unity's reflection).** Unreal
  scans for `.uplugin` descriptors at startup and loads them. Unity ships code as managed assemblies
  that "behave almost exactly like native C# scripts." We already do this for nodes — `NodeCatalog`
  reflects over one assembly for `Generator` subclasses. The plugin system **widens that to every
  loaded assembly** and adds non-node extension points.

**A UC plugin is NOT** a GameObject child in the world hierarchy (the Linker was a MonoBehaviour
child — wrong, it made the world "know" about the module), and it is **NOT a procedural node**. It
is an **assembly + a descriptor + interface implementations that register with the host** — part of
the system, not part of a scene graph or a graph.

---

## 2. What we learn from Unreal (fetched, not guessed)

| Unreal concept | What it does | UC adaptation |
|---|---|---|
| `.uplugin` **descriptor** (JSON: FriendlyName, Version, Description, Category, CreatedBy, Modules, EnabledByDefault) | Names/announces a plugin; read by the engine + the Plugin Browser | A **`UCPluginDescriptor`** (an `[assembly: UCPlugin(...)]` attribute, or a `UCPluginManifest` ScriptableObject for data-only plugins) carrying the same fields; read by our connected-plugins window |
| **Modules** with a **Type** (Runtime / Editor / Developer…) controlling WHEN each loads | A plugin can mix runtime + editor code | Unity gives us this for free via **asmdef** (Runtime vs Editor assemblies). A UC plugin is one or two asmdefs; Editor-only extensions live in an Editor asmdef |
| **Auto-discovery** of descriptors at startup; "never scans beneath an already-found plugin" | No manual registration list | **Reflection scan of `AppDomain.CurrentDomain.GetAssemblies()`** once, cached, guarded by `ReflectionTypeLoadException` (the exact pattern `NodeCatalog.Build()` already uses) |
| **Leveled dependency** (depend only same-level-or-higher) | Engine never depends on project | Host contract assembly is the "higher" level; plugins the "lower" — one-way, enforced by asmdef references |
| **Plugin Browser** (Edit → Plugins): name/icon/version/author/enabled, category tree, search, enable toggle, restart note | See + toggle what is installed | Our **"Connected Plugins" window** (§5): lists discovered plugins, which interfaces each implements, whether it is currently *registered/connected* this session, and an enable toggle |
| **"Add or override engine features without modifying engine code"** | Plugins extend, don't fork | Plugins add nodes + bake behaviour without editing the sub-engine |
| **"A plugin does not require code at all"** (content-only plugins) | Data/asset packs | A **data-only UC plugin** = a `UCPluginManifest` + ScriptableObjects (palettes, presets, prototype sets) — no `Generator` code needed |

## 3. What we use from Unity (the engine we ship on)

Unreal gives us the *shape* of the idea; Unity gives us the *mechanism*. Every Unreal concept
above lands on a Unity primitive we already have, so nothing here is invented:

| Unity primitive | What it is (official manual) | How a UC plugin uses it |
|---|---|---|
| **Managed plugin** (.NET assembly / `.dll`) | Managed `.dll`s "behave almost exactly like regular C# scripts"; Unity loads them like any script. Source may be hidden. | A compiled external PCG (e.g. RoadCityMaster) ships as a managed assembly. Its `[UCPlugin]`-marked types are discovered by reflection exactly as if they were in-project scripts. |
| **asmdef (Assembly Definition)** | Splits code into separately-compiled assemblies with explicit references. An *Editor platform* asmdef compiles only in the editor. | A UC plugin is 1–2 asmdefs: a Runtime asmdef that references `UniverseCreator.PluginContract`, and an optional Editor asmdef for its inspector/window extensions. This IS Unreal's "module Type" mechanism, done the Unity way. |
| **Reflection over loaded assemblies** | `AppDomain.CurrentDomain.GetAssemblies()` returns every managed assembly in the domain. | The host's `UCPluginRegistry` scans all of them once for `[UCPlugin]` descriptors and contract implementations — the generalisation of today's single-assembly `NodeCatalog`. |
| **`Plugins/` folder** (special folder) | Unity treats `Plugins/` specially for native/managed libs. | Not required for in-project source plugins, but a buyer dropping a pre-compiled PCG `.dll` into `Plugins/` is auto-picked-up with zero wiring — the drop-in story. |
| **ScriptableObject** | A serializable data asset, no GameObject needed. | A **data-only plugin** descriptor (`UCPluginManifest`) and its payload (palettes, presets) are ScriptableObjects — Unreal's "content-only plugin", modernised. |
| **`[DllImport]` native plugin** | C/C++ called through P/Invoke. | Out of scope for v1 (no UC extension point needs native code), but the contract does not forbid a plugin assembly from P/Invoking its own native lib — that is the plugin's business, not the host's. |

**Why NOT a Unity Package (UPM) for the plugin seam.** A UPM package is a *distribution* unit
(how code arrives on disk); it is orthogonal to *discovery* (how the host finds the code at
runtime). A plugin may ship as a package, as loose scripts, or as a dropped `.dll` — the registry
discovers all three identically, because discovery keys off the `[UCPlugin]` attribute and the
contract interfaces, never off a folder layout. This is the Unity-correct separation.

---

## 4. The UC plugin architecture (host + contract + extension points + registry)

Four pieces, in dependency order (each references only the ones above it — the leveled rule):

```
  ┌─────────────────────────────────────────────────────────────┐
  │  UniverseCreator.PluginContract   (NEW asmdef, the "host API")│  ← highest level
  │   • [UCPlugin] attribute        • UCPluginManifest (SO)       │     references NOTHING
  │   • IUCNodeProvider             • IUCWorldBakeHook            │     of UC's internals
  │   • IUCTerrainShapeProvider      • IUCWorldDataConsumer        │
  │   • UCPluginContext (read-only handles the host hands a plugin)│
  └─────────────────────────────────────────────────────────────┘
             ▲                                   ▲
             │ references                         │ references
  ┌──────────┴───────────────┐        ┌──────────┴────────────────────┐
  │  UniverseCreator (core)  │        │  <any external PCG assembly>   │  ← lowest level
  │   • UCPluginRegistry      │        │   e.g. RoadCityMaster.Plugin   │     references the
  │     (reflection discovery)│        │   [assembly:UCPlugin(...)]      │     CONTRACT only,
  │   • calls hooks at bake    │        │   : IUCTerrainShapeProvider     │     never UC core
  └───────────────────────────┘        └────────────────────────────────┘
```

The **host (sub-engine + core) references the contract, never a plugin.** A plugin references the
contract, never the core. This is Unreal's "depend only same-or-higher level" made literal by
asmdef references, and it is *why* UC and RoadCityMaster can stay two separate repos.

### 4.1 The descriptor — `[UCPlugin]` and `UCPluginManifest`

Mirrors `.uplugin`'s fields. Two forms, same data:

```csharp
// Code plugin: one assembly-level attribute. Discovered by reflection.
[assembly: UCPlugin(
    FriendlyName = "Road & City Master",
    Version      = "4.0.0",
    Author       = "…",
    Category     = "World/Infrastructure",
    Description  = "Roads, cities, infrastructure that shape UC terrain.",
    EnabledByDefault = true)]

// Data-only plugin: a ScriptableObject carrying the identical fields + a payload list.
//   Assets > Create > Universe Creator > Plugin Manifest
public sealed class UCPluginManifest : ScriptableObject { /* same fields + Object[] payload */ }
```

### 4.2 Extension points (the contracts a plugin implements)

A plugin implements **only the interfaces it needs**; all are optional. These are the four seams
an external PCG actually needs to "work as one" with the sub-engine:

| Interface | When the host calls it | What it lets a plugin do | Replaces (from the Linker) |
|---|---|---|---|
| **`IUCNodeProvider`** | Once, at registry build | Contribute extra `Generator` node types into `NodeCatalog` (even from another assembly) | the hard-coded "catalog also knows about linker nodes" |
| **`IUCTerrainShapeProvider`** | Main thread, per bake prime, before chunks dispatch | Hand the host footprints/shapes to stamp into terrain (a road flattens a strip; a plaza levels an area) | `TerrainShapeInbox` / `TerrainShapeRequest` / `TerrainShapeApplier` |
| **`IUCWorldBakeHook`** | Main thread, at bake begin + bake end | Observe/participate in a bake (prime caches, publish results back out) | the Linker's bake-time callbacks |
| **`IUCWorldDataConsumer`** | After a bake completes | Read finished world data (heights, water profiles, biome field) to drive the plugin's own output | the Linker reading UC's results directly |

```csharp
public interface IUCTerrainShapeProvider {
    // Pull, not push: the HOST asks each registered provider for shapes. The host never
    // names the provider; it just iterates whatever registered. One-way, exactly like PCG.
    void CollectTerrainShapes(UCPluginContext ctx, List<UCTerrainShape> into);
}
```

`UCPluginContext` is a **read-only handle bundle** the host hands in (world bounds, sea level,
pixel size, a stop token) — the plugin never reaches into UC internals for them, so the surface
stays small and stable.

### 4.3 The registry — discovery by reflection (generalise `NodeCatalog`)

```csharp
public static class UCPluginRegistry {
    // Built once, cached. Same guard pattern NodeCatalog.Build() already uses.
    static void Build() {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
            var attr = asm.GetCustomAttribute<UCPluginAttribute>();   // descriptor?
            if (attr == null) continue;                               // not a plugin, skip whole asm
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            // register the descriptor + every contract impl it declares, honouring the
            // per-plugin enabled toggle (EditorPrefs), default = EnabledByDefault.
        }
    }
}
```

This is the single mechanism that replaces the Linker: the core stops *naming* RoadCityMaster and
instead *iterates whatever registered*. Discovery is Unreal's auto-discovery; the toggle is
Unreal's per-plugin Enabled checkbox; the scan is Unity reflection.

### 4.4 What a plugin is NOT (the user's hard rules)

- **NOT a GameObject in the world hierarchy.** The Linker was a `MonoBehaviour` child, which made
  the world scene "know" about the module. A plugin is an assembly + descriptor; it owns no scene
  object. If a plugin needs scene authoring (e.g. RoadCityMaster's own road objects), those live
  in the *plugin's* own hierarchy, invisible to UC.
- **NOT a procedural node.** A plugin may *contribute* nodes (via `IUCNodeProvider`), but the
  plugin itself is not a node and does not live in a graph.
- **Opt-in, zero-regression.** No plugin present ⇒ the registry is empty ⇒ every call site is a
  no-op loop over zero providers ⇒ existing worlds bake byte-identically.

---

## 5. The "Connected Plugins" window (our Plugin Browser)

`Window > Universe Creator > Connected Plugins` — the Unity-side mirror of Unreal's Plugin Browser,
answering the user's "a window to show if the plugins are connected":

- **One row per discovered plugin:** FriendlyName, Version, Author, Category (from the descriptor).
- **Interfaces column:** which contracts it implements (Node / Shape / BakeHook / DataConsumer),
  so you can see *how* it plugs in at a glance.
- **Connected status:** a live dot — green = registered & enabled this session; grey = discovered
  but disabled; the window reads `UCPluginRegistry`, so "connected" means *actually registered*,
  not merely present on disk.
- **Enable toggle** per plugin (persisted in EditorPrefs); mirrors Unreal's Enabled checkbox.
- **Empty state:** "No plugins connected — UC is running as the standalone sub-engine," proving the
  zero-plugin path is a first-class state, not an error.

Editor-only, in the core's Editor asmdef; it reads the contract + registry, writes nothing a bake
depends on.

---

## 6. How an external PCG "works as one" with the sub-engine

The user's phrase: *"how to make the other connect to our sub-engine to work as one — that is the
work of a plugin."* The lifecycle, with RoadCityMaster as the worked example:

1. **Ship.** RoadCityMaster compiles an assembly `RoadCityMaster.Plugin` that references only
   `UniverseCreator.PluginContract`, carries `[assembly: UCPlugin(...)]`, and implements
   `IUCTerrainShapeProvider` (roads flatten terrain) + `IUCWorldDataConsumer` (reads finished
   heights to drape its roads).
2. **Discover.** On first catalog/registry build, UC reflects over loaded assemblies, finds the
   descriptor, registers the two contract impls. The Connected Plugins window shows it green.
3. **Bake (one-way, host-driven).** At bake prime (main thread) the host asks every
   `IUCTerrainShapeProvider` for shapes — RoadCityMaster returns its road footprints; the host
   stamps them exactly where today it stamps lake/biome footprints. The host never calls
   RoadCityMaster by name.
4. **Consume.** After the bake, the host offers finished world data to every
   `IUCWorldDataConsumer`; RoadCityMaster reads the heights it needs. Still one-way: the plugin
   pulls from a handed-in context, the host does not depend on the plugin.
5. **Absent.** Remove the assembly and every step above iterates zero providers. UC is the
   standalone sub-engine again. No stubs, no dangling references, no scene objects left behind.

This is identical in spirit to how UC's own PCG nodes already compose — the plugin is simply a PCG
that lives in another assembly and announces itself through the contract.

---

## 7. Linker-removal plan (every file, delete vs. rewrite)

Grepped. Nothing stub or stale is left behind — each file is either deleted or has its linker
coupling replaced by a contract call.

| File | Action |
|---|---|
| `Core/Runtime/Integration/RoadCityMasterLinker.cs` | **DELETE** — the hard-coded bridge; replaced wholesale by the contract + registry. |
| `Core/Editor/Inspectors/RoadCityMasterLinkerInspector.cs` | **DELETE** — its UI role is taken by the Connected Plugins window. |
| `Core/Runtime/Integration/TerrainShapeInbox.cs` | **DELETE** — replaced by `IUCTerrainShapeProvider` (pull, not an inbox). |
| `Core/Runtime/Integration/TerrainShapeRequest.cs` | **REPLACE** → `UCTerrainShape` in the contract asmdef (same data, host-owned type). |
| `Core/Runtime/Terrain/TerrainShapeApplier.cs` | **KEEP the stamping math; REWIRE its source** from the inbox to `UCPluginRegistry`'s providers. |
| `Core/Editor/UniverseCreatorMenus.cs` | **EDIT** — remove the linker create/menu entries; add the Connected Plugins window entry. |
| `Core/Editor/Utilities/UCBrandImages.cs` | **EDIT** — drop any linker-specific brand asset lookups (asset-path-free rule). |
| `Core/Runtime/Terrain/ChunkManager.cs` | **EDIT** — replace the `RoadCityMasterLinker` bake call with "ask registry's `IUCTerrainShapeProvider`/`IUCWorldBakeHook`." |
| `Core/Runtime/Terrain/TerrainBuilder.cs` | **EDIT** — same: linker reference → contract call. |
| `Core/Runtime/Terrain/DamReservoirReconciler.cs` | **EDIT** — drop the linker reference; it reads shapes through the host now. |
| `Core/Runtime/…/CutRegionRegistry.cs` | **EDIT** — decouple from the linker type. |
| `Core/Runtime/Nodes/Outputs/HolesOutputNode.cs` | **EDIT** — remove linker-specific hole routing; use the contract if needed. |
| `Core/Editor/Diagnostics/UCSelfTest.PathSeam.cs` | **EDIT** — swap linker-type tests for registry/contract tests. |
| `UniverseCreator_RoadCityMaster_Linker.md` | **RETIRE** (move to `_retired/`) — superseded by this doc. |
| `UniverseCreator_Manual.html` / `UniverseCreator_Documentation.md` | **EDIT** — replace the Linker section with the plugin section (via the manual pipeline, not by hand). |

**New files created by the overhaul:**
- `Core/PluginContract/` (new asmdef `UniverseCreator.PluginContract`): `UCPluginAttribute.cs`,
  `UCPluginManifest.cs`, `UCPluginContext.cs`, `IUCNodeProvider.cs`,
  `IUCTerrainShapeProvider.cs`, `IUCWorldBakeHook.cs`, `IUCWorldDataConsumer.cs`, **plus the
  relocated `TerrainShapeRequest.cs`** (moved in from `Runtime/Integration`, namespace unchanged,
  so it is the one shape type both the host and a plugin speak — no duplicate `UCTerrainShape`).
- `Core/Runtime/Plugins/UCPluginRegistry.cs` (reflection discovery) + `UCPluginBakeBridge.cs`
  (fires the hooks / collects shapes at the bake seams); core asmdef.
- `Core/Editor/Windows/UCConnectedPluginsWindow.cs` (the browser; Editor asmdef).

---

## 8. Build order + gates (one self-test per numbered step)

1. ✅ **PLG-1 Contract asmdef.** Created `UniverseCreator.PluginContract` (references nothing) with
   the `[UCPlugin]` attribute, `UCPluginManifest`, `UCPluginContext`, the four interfaces, and the
   relocated `TerrainShapeRequest`. **GREEN** — `PLG-CONTRACT1`, 0 CS, 15248 checks / 0 fail.
2. ✅ **PLG-2 Registry.** `UCPluginRegistry` reflection scan (guarded), per-plugin enabled state,
   enabled-only views. **GREEN** — `PLG-REG1`, 0 CS, 15257 checks / 0 fail (fixture registers;
   real-domain scan empty + no throw with no plugin present).
3. ✅ **PLG-3 NodeCatalog widening.** `NodeCatalog.BuildCatalog` folds in enabled plugins'
   node types with dedupe. **GREEN** — `PLG-NODE1`, 0 CS, 15261 checks / 0 fail (plugin node
   appears once; none ⇒ catalog byte-identical).
4. ✅ **PLG-4 Bake wiring.** `ChunkManager` builds a `UCPluginContext`, fires `IUCWorldBakeHook`
   begin at bake prime + end/`IUCWorldDataConsumer` at settle-complete, and folds enabled
   `IUCTerrainShapeProvider` shapes into the existing `ShapeRequests`/`TerrainShapeApplier` path
   through `UCPluginBakeBridge`. **GREEN** — `PLG-BAKE1`, 0 CS, 15265 checks / 0 fail (zero
   providers ⇒ byte-identical; fixture shapes accumulate; a throwing provider is isolated).
5. ✅ **PLG-5 Linker teardown.** DELETED — and only this: `RoadCityMasterLinker.cs` +
   `RoadCityMasterLinkerInspector.cs` (+ `.meta`), the product-named MonoBehaviour bridge and its
   inspector. It computed nothing; it forwarded all six provider calls to `ChunkManager`.
   RESTORED INTO THE CONTRACT (an over-wide first pass deleted these, broke RCM's 23 consumers with
   87 CS errors, and was reverted — see §10): `IPCGDataProvider`, `IPathToolLink`, `PathLinkState`,
   `PathLinkStatus`, `PathProviderSocket` now live in `Core/PluginContract/UCPathProviderContracts.cs`
   under the UNCHANGED `UniverseCreator.Integration` namespace, plus `PathProviderSocketInspector.cs`
   — so RCM compiles untouched. REWIRED: `ChunkManager` now implements `IPCGDataProvider` itself
   (6 wrapper members), so the capability survives with no bridge object and no product name in the
   chain; the linker `SystemChild` + one-time wiring removed from `UniverseCreatorMenus.cs`; stale
   hierarchy/menu text corrected; linker-naming comments re-pointed (`CutRegionRegistry`,
   `ChunkManager` ×2, `HolesOutputNode`, `TreeScatterNode`, `TerrainShapeRequest` header).
   KEPT: the `TerrainShapeRequest`/`Inbox`/`Applier` pipeline (independent consumers — dam
   reconciler, chunk manager, terrain builder). Gate `PLG-TEARDOWN1` pins all three facts at once:
   the bridge type no longer resolves, the contracts DO resolve at exactly 6 members, and
   `ChunkManager` implements the surface. **GREEN** — 0 CS, 15304 checks / 0 fail.
6. ✅ **PLG-6 Connected Plugins window.** `UCConnectedPluginsWindow`
   (`Window > Universe Creator > Connected Plugins`): one row per discovered plugin with identity,
   a **green/grey connected dot** (green = registered AND enabled this session, i.e. exactly the
   set the bake iterates), the **contracts it actually registered** (derived from implementations,
   not claims), a per-plugin Enabled toggle persisted in EditorPrefs, Rescan, search, and a
   first-class empty state ("running as the standalone sub-engine — nothing is wrong"). Gate
   `PLG-WIN1`. **GREEN** — 0 CS, 15304 checks / 0 fail.
7. ✅ **PLG-7 RoadCityMaster re-adoption.** ONE file added to RCM's own tree:
   `Assets/RoadCityMaster/Integration/RoadCityMasterUCPlugin.cs` — a plain class with
   `[UCPlugin(...)]` implementing `IUCTerrainShapeProvider` + `IUCWorldDataConsumer`, publishing
   corridors as data and reading the finished surface through the context sampler (the replacement
   for the old linker's `SampleHeight`). **Zero UC edits were needed to adopt it** beyond adding
   `"UniverseCreator.PluginContract"` to RCM's two existing `.asmdef` reference lists — no RCM
   source was edited. Gate `PLG-UNIV1` drives both descriptor forms with its OWN fixtures and names
   no other product. **GREEN** — 0 CS, 15304 checks / 0 fail. Still open, by design: RCM's road
   bake does not yet call `PublishCorridors`, so the adapter is inert until it does.

Each numbered PLG step ends with ONE batchmode self-test (`UCSelfTest.RunBatch`, RAM ≥ 4.2 GB + no
open editor), must be green / 0 CS / 0 failures. Then **resume `UC_SYSTEM_OVERHAUL_PLAN.md` at
Topic 5 → RIV-1b.**

---

## 9. Is the plugin universal? (yes — and what a PCG must add)

**The host is universal: it works with ANY PCG, with zero changes to Universe Creator.** What each
PCG adds is one small adapter — exactly Unreal's split, where the engine is universal and every
plugin carries its own descriptor plus module.

**The whole recipe for any PCG:**

```csharp
[UCPlugin(FriendlyName = "My PCG", Version = "1.0.0")]       // 1. announce yourself
public sealed class MyPcgUCPlugin : IUCTerrainShapeProvider  // 2. implement what you need
{
    public void CollectTerrainShapes(UCPluginContext ctx, List<TerrainShapeRequest> into) { }
}                                                            // 3. that's it - UC finds it
```

No UC edit, no registration list, no scene object, no node. Implement only the contracts you need;
all four are optional.

**Two descriptor forms, and why both exist (PLG-2b).** This is what makes "any PCG" true rather
than "any well-packaged PCG":

| The PCG ships as… | Descriptor form | Result |
|---|---|---|
| Its own asmdef, or a pre-compiled `.dll` in `Plugins/` | `[assembly: UCPlugin(...)]` | The assembly is one plugin holding every contract in it. Cheapest scan (one attribute read, no `GetTypes`). |
| **Loose `.cs` files with no asmdef** — common on the Asset Store, where scripts land in Unity's shared predefined assembly | `[UCPlugin(...)]` **on a class** | That class is its own plugin, keyed by type name. Two unrelated tools sharing that one assembly stay two separately-toggleable plugins. |

An assembly-level descriptor alone would have been tidier, but it silently excludes every tool
that has no asmdef, because marking Unity's shared predefined assembly would claim the whole
project as one plugin. The class form is what makes the seam genuinely universal.

**The host is never its own plugin.** `UCPluginRegistry.IsHostAssembly` excludes UC's own three
assemblies from discovery. That is right in principle, and load-bearing in practice: the editor
assembly holds self-test fixtures, and an attributed fixture adopted as a live plugin would be
handed real bake work — a fixture returning a terrain cut would put a hole in the author's world.

**What RoadCityMaster had to add:** one source file (`RoadCityMasterUCPlugin.cs`, §8 PLG-7) plus
`"UniverseCreator.PluginContract"` in its two existing asmdef reference lists. No change to any of
its ~23 existing sources. *(Correction: an earlier draft of this section claimed RCM has no asmdef
and therefore had to use the class descriptor form. That was wrong — RCM has `RoadCityMaster.asmdef`
and `RoadCityMaster.Editor.asmdef`, so either form would work; the class form is kept because it
scopes the descriptor to the adapter rather than to the whole product. The class form's
justification stands on its own for loose-script tools.)*

**Zero-regression, stated honestly.** Once a real plugin is connected, "no plugins exist" stops
being the truth, so that is no longer what is pinned. What is pinned instead: a connected, enabled
plugin that has **published nothing contributes nothing**, and a **disabled** plugin drops out of
the enabled set entirely while its code stays loaded. A world only changes once a plugin actually
publishes work.

**The self-test touches no other product.** `PLG-UNIV1` proves universality with its own fixtures
in UC's assembly and names no external system, so the suite neither depends on nor reaches into a
separate repository.

---

## 10. Teardown post-mortem — what the over-wide delete taught (keep this)

The first teardown attempt deleted four files and broke the build with **87 CS errors**, every one
of them in the *other* product. Recorded because the cause is a trap that will recur:

- **`RoadCityMaster/` is listed in this repository's `.gitignore`.** The search tooling is ripgrep,
  which honours ignore files, so a repo-wide `grep IPCGDataProvider` returned **zero** hits in
  those 23 files and the audit concluded "linker-only". Ripgrep *does* search an ignored directory
  when it is named explicitly — so an audit that must cover the other product has to point at
  `Assets/RoadCityMaster` directly. The same blindness hid its two `.asmdef` files from a `Glob`.
- **"It was in the linker's file" is not "it is linker-specific."** `RoadCityMasterLinker.cs` held
  both a product-named bridge *and* the product-neutral `IPCGDataProvider` surface. Only the bridge
  was in scope. The interface, `IPathToolLink`, and the socket were restored verbatim into the
  contract assembly under the **same namespace**, so every existing consumer compiles untouched.
- **A deleted capability needs a new home, not just a deletion.** The linker was the only
  implementation of the provider surface. `ChunkManager` now publishes it — it already owned all six
  operations and is part of UC's own hierarchy, so the capability survives with no bridge component
  and no product name in the chain.
- **The gate was corrected, not loosened.** `PLG-TEARDOWN1` now pins *both* halves: the bridge is
  gone **and** the contracts it used to carry still resolve, with the surface still at six members.

---

> **Spec complete; build complete and verified.** PLG-1→7 are all GREEN — last batchmode run
> 0 error CS, 15304 checks / 0 failures (2026-10-13), ticks in §8. One thing is deliberately still
> open: RCM's road bake does not yet call `PublishCorridors`, so its adapter is discovered and
> listed but inert until it does. Nothing here is guessed: every row maps to a fetched Unreal
> concept, a Unity manual primitive, and a real file in the tree.


