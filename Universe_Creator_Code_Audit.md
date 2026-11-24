# Universe Creator — Code Audit & Root Cause Analysis

**Status**: Audit Complete (Based on User Screenshots & Source Code Analysis)  
**Focus**: Missing water objects, gray terrain, and node graph configuration.

I have reviewed your new node graph screenshot in detail and audited the C# source code. You are completely correct: **you wired the graph exactly right, but there is a fatal bug in the system's C# code that prevents water from spawning.** 

Here is the exact breakdown of why the system is failing, what is wrong in the code, and why your terrain is gray.

---

## 1. The Code Bug: Why No Water Objects Spawn

In your screenshot, your `Hydrology Spawner` is perfectly wired. It receives `Height` from `Thermal Erosion`, and its masks flow correctly through `Lake/River Fill` to `Water Bank Seat` and eventually to an `OutputNode`. 

Because it is wired correctly, the system *does* reach it, and it *does* call `HydrologyGenerator.GenerateFromNode()` on the main thread after the terrain settles. 

**The failure happens inside `HydrologyGenerator.cs` on line 1200:**
```csharp
// 2. Perform a world-scale D8 drainage solve to find where water runs.
Matrix mask = node.GetInputMatrix(node.tile, 0, null);
```

### The Fatal Flaw:
The generator claims to do a "world-scale" drainage solve. However, `node.tile` does **not** contain the world. Because the terrain is built in chunks (as seen in your console: `chunk at x 800 z 800`), `node.tile` is continually overwritten during generation. By the time `GenerateFromNode` runs, `node.tile` only holds the data for the **very last chunk that finished baking** (a single 800x800m square, usually a corner of the map).

**What this means for your world:**
1. The script pulls the heightmap for only that single corner chunk.
2. It tries to find a river catchment area of `250,000` square meters inside that single chunk.
3. Unless that specific corner chunk contains a massive mountain peak, it will find 0 rivers. 
4. It spawns 0 river objects.

This is why `River Carve` successfully digs trenches across your entire scene (because `River Carve` correctly runs per-chunk during the bake), but the `HydrologyGenerator` fails to spawn the actual water objects to fill them (because it only looks at the last chunk).

### Why Lakes and Oceans Also Failed:
- **Oceans**: In your `Hydrology Spawner` node, the `Generate Ocean` checkbox is **unchecked** (the box is empty in your screenshot). So it correctly spawned 0 oceans.
- **Lakes**: The lake spawner scans the real terrain for enclosed basins (bowls). However, river trenches do not count as basins (because they drain outward). Since your terrain is an island that slopes to the sea, it has no deep enclosed bowls, so it found 0 valid lakes.

Because Rivers = 0, Lakes = 0, and Ocean = 0, the script silently finishes and creates no water objects.

---

## 2. Why the Terrain is Gray (No Textures)

In your screenshot, you placed all the **Texture Graph nodes** (`Shader Output`, `Terrain Blend Final`, `Texture Sampler`, etc.) on the far right side of your **Height Node Graph**.

**This does not work.** 
The Universe Creator architecture relies on two strictly separated graphs:
1. **The Node Graph** (where you are now): Only handles geometry, heights, and carving. It terminates at `Height Output` or `Biome Splat Output`.
2. **The Texture Graph**: Must be opened in a separate window (`Window > Universe Creator > Texture Graph`). It handles the PBR materials and terminates at `Shader Output`.

Because your `Shader Output` node is sitting inside the Height Graph, the system's material compiler never sees it. The terrain generator simply ignores those nodes, resulting in a flat, untextured gray terrain.

---

## 3. Why Rivers "Climb Hills"

When a river *does* manage to spawn (e.g., if you lower `Channel Head Area` enough that a small river forms in the final chunk), it will trace its path based on the un-carved, original mountains. 

The system relies on `RiverCourseSolver` to snap the water down into the carved valleys. However, if the `Drainage Resolution` on your `Hydrology Spawner` (currently 256) does not exactly match the `Drainage Resolution` on your `River Network Trace` node (also 256), the two nodes will disagree on where the valley actually is, causing the river object to miss the trench and sit on the hillside.

---

## 4. How to Fix Your Project

As requested, I have not modified your source code. To get your world working, you must take the following manual actions:

### Action 1: Fix the Texture Graph
1. Open the Texture Graph window (`Window > Universe Creator > Texture Graph`).
2. Re-create your Texture nodes there (from `Texture Sampler` up to `Shader Output`).
3. Delete the Texture nodes from your Height graph.
4. Click the **"Apply to Terrain"** button in the Texture Graph toolbar. Your terrain will instantly receive its materials.

### Action 2: Workaround for the River Bug
Because `HydrologyGenerator.cs` has a bug where it only scans one chunk, you cannot generate world-spanning procedural river objects on a multi-chunk world without a code fix. 
**Your options are:**
1. Check **Generate Ocean** on your `Hydrology Spawner`. This will at least spawn the sea level water correctly, filling the lower sections of your trenches.
2. Wait for a patch to the codebase that updates `HydrologyGenerator.cs` to stitch a world-wide height matrix before solving drainage.
3. Or, manually place `UCWaterBody` objects (set to Spline mode) into your trenches using Unity's standard tools.

### Reference Table: Hydrology Spawner Values (V4)

| Field | Your Value | What it Should Be |
|---|---|---|
| `Target River Count` | 3 | 3 (Will fail until codebase is patched) |
| `Target Lake Count` | 0 | 0 (0 = use percentage) |
| `Lake Chunk %` | 0.24 | 0.24 |
| `Lake Outlet Chunk %` | 0.08 | 0.08 |
| `Min Tributaries` | 1 | 1 |
| `Enable Distributaries`| Checked | Checked |
| `Generate Ocean` | **Unchecked** | **CHECKED** (This is why you have no sea) |
| `Drainage Resolution` | 256 | 256 (Must equal River Network Trace) |
| `Sea Level` | 0.35 | 0.35 |
| `Channel Head Area` | 250,000 | Try 50,000 to force smaller rivers to form |
| `Min Lake Depth` | 0.002 | 0.002 |
