// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    TerrainBuilder.cs
//  Purpose: Static helpers that apply TileData.outputs to Unity TerrainData.
//           ChunkManager owns the Terrain GameObjects; this class knows only how
//           to translate OutputBag -> SetHeights/SetAlphamaps/SetHoles.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Integration;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Static helpers that apply TileData.outputs to Unity TerrainData. Main-thread
    /// only — every method touches Unity API.
    /// </summary>
    public static class TerrainBuilder
    {
        /// <summary>
        /// Creates or reuses a TerrainData, matching the requested resolution and
        /// physical size. If an existing data asset is supplied and its resolution
        /// matches, it's reused in place (heights overwritten); otherwise a fresh
        /// one is allocated. This is the object-pool contract: ChunkManager hands us
        /// the TerrainData from a recycled chunk, we overwrite its heights, and
        /// nothing is ever destroyed.
        /// </summary>
        public static TerrainData GetOrCreateTerrainData(TerrainData existing, int resolution, Vector3 size)
        {
            bool canReuse = existing != null &&
                           existing.heightmapResolution == resolution &&
                           Mathf.Approximately(existing.size.x, size.x) &&
                           Mathf.Approximately(existing.size.z, size.z);

            if (canReuse)
            {
                // Reuse: size already matches, just need to update the height scale
                // in case maxHeightMeters changed.
                Vector3 currentSize = existing.size;
                currentSize.y = size.y;
                existing.size = currentSize;
                return existing;
            }

            // Allocate fresh. Unity requires heightmapResolution to be 2^n+1.
            TerrainData data = new TerrainData();
            data.heightmapResolution = resolution;
            if (size.x <= 0f) size.x = 1000f;
            if (size.y <= 0f) size.y = 600f;
            if (size.z <= 0f) size.z = 1000f;
            data.size = new Vector3(size.x, size.y, size.z);
            return data;
        }

        // Names the cell being written, in metres, for the height guard's warning.
        //
        // The guard fires from inside a static apply with no Chunk in scope, and a
        // warning that cannot say WHERE it acted is close to useless on a 25-cell
        // grid: the whole value of the message is being able to fly to that corner
        // and look at what the graph did there. The bounds are the one piece of
        // identity this method already has.
        private static string ChunkLabel(Bounds worldBounds)
        {
            return "the chunk at x " + Mathf.RoundToInt(worldBounds.min.x)
                   + " z " + Mathf.RoundToInt(worldBounds.min.z) + " m";
        }

        /// <summary>
        /// Applies the OutputBag from a finished background generation to the given
        /// TerrainData. Writes heightmap, splatmap, holes, and detail layers.
        /// Returns true if the collider should be rebuilt.
        /// </summary>
        public static bool ApplyOutputs(TerrainData data, OutputBag outputs, UniverseCreatorObject settings)
        {
            return ApplyOutputs(data, outputs, settings, null, default(Bounds), 0f, null);
        }

        /// <summary>
        /// As ApplyOutputs, but folds a path tool's terrain shapings and the author's
        /// manual sculpt edits back in on top of the generated heights, in that order.
        /// Pass the world's override layer, the chunk's world bounds, and the shaping
        /// requests (null if the world has no path tool); the shapings conform the
        /// ground to roads and key dams into their valley walls, then the sculpt deltas
        /// are added, both before the height write, so neither is overwritten by a
        /// subsequent graph re-run.
        ///
        /// The height write goes through SetHeightsDelayLOD, which stages the new
        /// heightfield without rebuilding the LOD tree or pushing the GPU texture. That
        /// is the cheap path, but it leaves the chunk drawing its OLD surface - flat, if
        /// the cell had never been baked - until something flushes it, which is the blank
        /// terrain this pairing exists to prevent.
        ///
        /// SyncHeightmap is that flush, and on Unity 6 it is the whole of it.
        /// Terrain.ApplyDelayedHeightmapModification used to be required alongside it and
        /// is now obsolete precisely because SyncHeightmap notifies every Terrain instance
        /// sharing the TerrainData - calling both is a deprecation warning for no added
        /// effect. See the TerrainData docs: "Use TerrainData.SyncHeightmap to notify all
        /// Terrain instances using the TerrainData."
        /// </summary>
        public static bool ApplyOutputs(TerrainData data, OutputBag outputs, UniverseCreatorObject settings,
                                        TerrainOverrideLayer overrides, Bounds chunkWorldBounds,
                                        float metresPerSample, IReadOnlyList<TerrainShapeRequest> shapeRequests)
        {
            if (data == null || outputs == null) return false;

            bool needsColliderRebuild = false;

            // Heightmap
            if (outputs.heightmap != null)
            {
                // WATER DIGS FIRST, SCULPTS SECOND.
                //
                // The order is the whole contract. Water carving is a rule re-derived
                // from the body's own fields on every apply, so a graph re-run cuts
                // the channel again instead of filling it in - that is what makes
                // Regenerate stop wiping rivers. Manual sculpting then lands on top,
                // so a bank the user shaped by hand is not undone by the river that
                // runs past it. Reverse these two and every hand edit inside a water
                // body would be erased on the next rebuild.
                if (settings != null && metresPerSample > 0f)
                    WaterCarveRegistry.ApplyAll(settings, outputs.heightmap,
                                                chunkWorldBounds, metresPerSample);

                // A PATH TOOL'S SHAPINGS, BETWEEN THE DIG AND THE SCULPT.
                //
                // The same seat in the order as the water carve, for the same reason:
                // a road conforms to ground the river has already cut, so an embankment
                // beside a river blends down to the carved bank instead of to ground
                // that no longer exists - and the hand sculpt below still lands on top
                // of the road. Re-derived from the request on every apply exactly as the
                // carve is, so a regenerate re-conforms the road rather than wiping it,
                // and a withdrawn request leaves nothing behind on the next rebuild.
                // Null on a world with no path tool, and then this costs nothing.
                if (settings != null && metresPerSample > 0f && shapeRequests != null)
                    TerrainShapeApplier.ApplyAll(shapeRequests, outputs.heightmap,
                                                 chunkWorldBounds, metresPerSample, settings.maxHeightMeters);

                // Manual edits are re-applied here rather than after the write,
                // because reading the heights back out of TerrainData to patch them
                // would cost a full round-trip through native memory per chunk.
                if (overrides != null && !overrides.IsEmpty && metresPerSample > 0f)
                    overrides.ApplyTo(outputs.heightmap, chunkWorldBounds, metresPerSample);

                // LAST GATE BEFORE THE SURFACE EXISTS.
                //
                // This used to be a Clamp01 loop commented as preventing spikes. It
                // could not: Unity's Clamp01 is two inequalities and an else, and NaN
                // fails both inequalities, so every NaN the graph produced fell
                // straight through into SetHeights - which is what draws a vertex at
                // an undefined position. See HeightGuard for the arithmetic that
                // produces one and for what replaces the clamp.
                //
                // Placed after the carve and the sculpt deliberately, so it is the
                // last thing to touch the samples and audits the water dig and the
                // override layer on the same terms as the node math. Nothing gets to
                // be trusted here because of where it came from.
                HeightGuard.Guard(outputs.heightmap, metresPerSample,
                                  settings != null ? settings.maxHeightMeters : 0f,
                                  ChunkLabel(chunkWorldBounds), settings);

                data.SetHeightsDelayLOD(0, 0, outputs.heightmap);

                // Flush: uploads the samples to the GPU and rebuilds the patch LODs and
                // the physics heightfield on every Terrain sharing this data.
                data.SyncHeightmap();

                needsColliderRebuild = settings != null && settings.generateCollider;
            }

            // Splatmap (texture layers)
            if (outputs.splatmap != null)
            {
                data.SetAlphamaps(0, 0, outputs.splatmap);
            }
            else if (data.terrainLayers != null && data.terrainLayers.Length > 0 && data.alphamapResolution > 0)
            {
                // Initialize default alphamap to layer 0 so unpainted terrain isn't grey
                int aRes = data.alphamapResolution;
                float[,,] defaultSplat = new float[aRes, aRes, data.terrainLayers.Length];
                for (int y = 0; y < aRes; y++)
                {
                    for (int x = 0; x < aRes; x++)
                    {
                        defaultSplat[y, x, 0] = 1f;
                    }
                }
                data.SetAlphamaps(0, 0, defaultSplat);
            }
            
            // Rebuild basemap so distant chunks don't fall back to a grey uninitialized texture
            data.SetBaseMapDirty();

            // Holes (tunnels, caves, underwater passages)
            if (outputs.holes != null)
            {
                data.SetHoles(0, 0, outputs.holes);
            }

            // Detail layers are NOT written here. SetDetailLayer requires a grid of
            // exactly detailResolution squared, and the graph produces them at the
            // heightfield's resolution - the two are independent settings and are
            // rarely equal, so writing them raw threw on the size mismatch. Module 2
            // moved that to ScatterApplier.ApplyDetailGrids, which resamples first
            // and also owns the prototype array those layers index into.

            return needsColliderRebuild || outputs.requestColliderRebuild;
        }

        // One shared material for every chunk that has no world material assigned.
        private static Material fallbackTerrainMaterial;

        /// <summary>
        /// A terrain material that exists in whichever render pipeline is installed.
        /// Shader names differ per pipeline and Shader.Find returns null for the ones
        /// that are absent, so this walks a chain rather than assuming.
        /// </summary>
        private static Material FallbackMaterial()
        {
            if (fallbackTerrainMaterial != null) return fallbackTerrainMaterial;

            // The flag says whether that shader compiles the terrain instancing variants.
            // enableInstancing is a plain material bool that stays true on a shader
            // lacking them, so it cannot be used to discover support after the fact -
            // it has to be decided here, alongside the shader choice.
            // Ordered by preference, not by popularity. Ours first because it is
            // the one with the UC control surface on it; the stock pipeline
            // shaders are here so a project that strips this module still draws
            // terrain instead of magenta.
            //
            // Nature/Terrain/Standard sits below the SRP entries deliberately.
            // It is a built-in-render-pipeline shader, so under URP or HDRP it
            // resolves - Shader.Find still returns it - and then renders magenta.
            // Finding a shader is not the same as that shader working in the
            // active pipeline, and this list is ordered on the latter.
            string[] candidates =
            {
                "UniverseCreator/TerrainLit",           // ours, URP
                "Universal Render Pipeline/Terrain/Lit",
                "HDRP/TerrainLit",
                "Nature/Terrain/Standard",              // built-in render pipeline only
                "Nature/Terrain/Diffuse",               // legacy, no instancing path
                "Standard"                              // last resort, not a terrain shader
            };
            bool[] supportsInstancing = { true, true, true, true, false, false };

            for (int i = 0; i < candidates.Length; i++)
            {
                Shader s = Shader.Find(candidates[i]);
                if (s == null) continue;

                fallbackTerrainMaterial = new Material(s);
                fallbackTerrainMaterial.name = "UC_DefaultTerrain";

                // new Material() leaves this off, which is what silently broke instanced
                // terrain rendering: the shader never declared unity_ObjectToWorldArray,
                // so every patch transform arrived undefined.
                fallbackTerrainMaterial.enableInstancing = supportsInstancing[i];

                // Not DontSave: the terrain keeps referencing this between reloads, and
                // a destroyed material would leave every chunk drawing magenta.
                fallbackTerrainMaterial.hideFlags = HideFlags.HideAndDontSave;
                return fallbackTerrainMaterial;
            }

            return null;
        }

        // A visible stand-in so an unpainted world reads as ground rather than as
        // nothing. Replaced the moment the user assigns real layers.
        private static TerrainLayer fallbackLayer;

        /// <summary>
        /// Guarantees the chunk has at least one TerrainLayer. A terrain with none
        /// has no splat channel to draw into, so it renders untextured - which on a
        /// freshly generated world is indistinguishable from the terrain not existing.
        /// Does nothing once the world has real layers.
        /// </summary>
        public static void EnsureVisibleLayer(TerrainData data)
        {
            if (data == null) return;

            // A live layer is enough to leave this alone. The null check matters as much
            // as the length check: the stand-in below is HideAndDontSave, so a domain
            // reload destroys it and leaves a slot holding a dead reference. Treating
            // that as "already has layers" is what would strand the surface untextured
            // after every recompile.
            TerrainLayer[] current = data.terrainLayers;
            if (current != null)
            {
                for (int i = 0; i < current.Length; i++)
                    if (current[i] != null) return;
            }

            if (fallbackLayer == null)
            {
                // Earth, not a test pattern. The old stand-in was a light-grey/white
                // checker, and white is the one colour that cannot be read as ground:
                // against a blue water surface it reads exactly as "this material
                // failed to assign", which is precisely the bug reported at the
                // shoreline when in fact the terrain was drawing correctly with the
                // stand-in it was given.
                //
                // 4x4 rather than 2x2 with bilinear filtering and a several-metre tile,
                // so it reads as soil breaking up over distance instead of as squares.
                const int size = 4;
                Texture2D tex = new Texture2D(size, size);
                tex.name = "UC_DefaultGroundTex";
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Repeat;

                // Damp earth, with the value walked by a small fixed pattern rather than
                // a random draw: the stand-in has to look identical on every machine and
                // after every reload, or a screenshot comparison is worthless.
                Color[] px = new Color[size * size];
                for (int i = 0; i < px.Length; i++)
                {
                    // Two coprime strides over the 16 cells, so light and dark never line
                    // up into rows or a checker.
                    float v = 0.88f + 0.06f * (i % 3) + 0.04f * (i % 5);
                    px[i] = new Color(0.42f * v, 0.35f * v, 0.26f * v, 1f);
                }

                tex.SetPixels(px);
                tex.Apply();

                fallbackLayer = new TerrainLayer();
                fallbackLayer.name = "Default_Terrain_Layer";
                fallbackLayer.diffuseTexture = tex;
                // Metres per texture repeat. Large enough that the 4x4 never resolves as
                // a grid underfoot, small enough to still break up a hillside.
                fallbackLayer.tileSize = new Vector2(6f, 6f);
                fallbackLayer.hideFlags = HideFlags.HideAndDontSave;
            }

            if (data.terrainLayers == null || data.terrainLayers.Length == 0)
            {
                data.terrainLayers = new TerrainLayer[] { fallbackLayer };
            }
        }

        /// <summary>
        /// The one rule pairing <c>Terrain.drawInstanced</c> to the material's
        /// instancing support, kept as a pure truth table so a self-test can pin it
        /// without a live Terrain. Instanced terrain feeds every patch transform
        /// through the shader's <c>unity_ObjectToWorldArray</c>; a material that does
        /// not declare that array leaves the matrices undefined and the whole surface
        /// draws at a garbage transform - it vanishes. So the flag may go true ONLY
        /// when the material actually supports instancing, whatever the author toggled.
        /// Visible ground beats the draw-call saving.
        /// </summary>
        public static bool ResolveDrawInstanced(bool wantInstanced, bool materialSupportsInstancing)
        {
            return wantInstanced && materialSupportsInstancing;
        }

        /// <summary>
        /// The invariants that decide whether the chunk draws at all. Safe to re-assert
        /// on every pass, and separate from ConfigureTerrain because these are not
        /// values a user tunes - they are the difference between ground and nothing.
        /// </summary>
        public static void EnsureRenderable(Terrain terrain, UniverseCreatorObject settings)
        {
            if (terrain == null) return;

            // A world material wins. Failing that the chunk gets an explicit fallback
            // instead of being left on null: AddComponent<Terrain> starts with no
            // material at all, and trusting the pipeline to substitute one is what left
            // the ground invisible while the slice map floating above it drew fine.
            //
            // FallbackMaterial() rather than a fresh Material here, for two reasons.
            // It walks the candidate chain - UniverseCreator/TerrainLit first - so the
            // chunk gets our control surface where it exists and still draws where it
            // does not. And it hands back one shared cached instance: constructing a
            // material per chunk meant twenty-five identical materials, none of them
            // owned by anything, none ever destroyed, and each one its own SRP batcher
            // break for a surface that should batch as one.
            if (settings != null && settings.terrainMaterial != null)
                terrain.materialTemplate = settings.terrainMaterial;
            else if (terrain.materialTemplate == null)
                terrain.materialTemplate = FallbackMaterial();

            // Both of these default on for a fresh component, but either one off draws
            // nothing whatsoever, and they are cheap to reassert.
            terrain.drawHeightmap = true;
            terrain.enabled = true;
            terrain.drawTreesAndFoliage = true;
            if (terrain.gameObject != null)
            {
                terrain.gameObject.SetActive(true);
                terrain.gameObject.layer = LayerMask.NameToLayer("Default");
            }

            // Instanced terrain feeds every patch transform through the shader's
            // unity_ObjectToWorldArray. A material with instancing switched off never
            // declares that array, so the matrices arrive undefined, each patch draws at
            // a garbage transform, and the surface disappears while non-instanced
            // overlays like the slice map keep rendering normally. The console names it
            // outright: "Unhandled property 'unity_ObjectToWorldArray'".
            //
            // A material the user owns is not ours to modify, so the non-instanced path
            // is the fallback instead. Visible ground beats the draw-call saving.
            bool wantInstanced = settings != null && settings.drawInstanced;
            bool materialHandlesIt = terrain.materialTemplate != null &&
                                     terrain.materialTemplate.enableInstancing;
            terrain.drawInstanced = ResolveDrawInstanced(wantInstanced, materialHandlesIt);

            // Unity's own auto-connect is switched off, deliberately.
            //
            // It defaults ON, and it re-links terrains by world position on its own
            // schedule - including immediately after we call SetNeighbors, overwriting
            // the links we just made. It is also a second, independent source of the
            // "…neighbor of the terrain has a different heightmap resolution. Stop
            // neighboring." warning: auto-connect finds every adjacent Terrain in the
            // scene, ours or not, and complains about each pair it cannot join. That is
            // how one grid produced warnings by the dozen per repaint.
            //
            // StitchAll owns neighbour links here. It knows which cells are live, which
            // have heightfields yet, and which belong to this world, none of which
            // auto-connect can tell.
            terrain.allowAutoConnect = false;
        }

        /// <summary>
        /// Hash of exactly the fields ConfigureTerrain mirrors onto each cell. Lives
        /// beside it so the two cannot drift apart: adding a pushed setting without
        /// adding it here would make that setting stop propagating.
        /// </summary>
        public static int RenderSettingsSignature(UniverseCreatorObject settings)
        {
            if (settings == null) return 0;

            unchecked
            {
                int h = 17;
                h = h * 31 + settings.basemapDistance.GetHashCode();
                h = h * 31 + (settings.castShadows ? 1 : 0);
                h = h * 31 + (settings.drawInstanced ? 1 : 0);
                h = h * 31 + (settings.drawTreesAndFoliage ? 1 : 0);
                h = h * 31 + settings.detailObjectDistance.GetHashCode();
                h = h * 31 + settings.detailObjectDensity.GetHashCode();
                h = h * 31 + settings.treeDistance.GetHashCode();
                h = h * 31 + settings.treeBillboardDistance.GetHashCode();
                h = h * 31 + settings.treeCrossFadeLength.GetHashCode();
                h = h * 31 + settings.treeMaximumFullLODCount;
                h = h * 31 + (int)settings.reflectionProbeUsage;
                h = h * 31 + settings.wavingGrassStrength.GetHashCode();
                h = h * 31 + settings.wavingGrassAmount.GetHashCode();
                h = h * 31 + settings.wavingGrassSpeed.GetHashCode();
                h = h * 31 + settings.wavingGrassTint.GetHashCode();
                h = h * 31 + (settings.terrainMaterial != null
                              ? settings.terrainMaterial.GetHashCode() : 0);

                // Never collide with the "nothing pushed yet" sentinel, or a fresh cell
                // would be treated as already configured and never get its material.
                return h == 0 ? 1 : h;
            }
        }

        /// <summary>
        /// Mirrors the world's Terrain Settings onto one cell. Every value written here
        /// is also editable in Unity's native Terrain inspector, so ChunkManager pushes
        /// this only when the world values actually change - see SyncTerrainSettings.
        /// Calling it unconditionally is what reverted hand edits on the next pass.
        /// </summary>
        public static void ConfigureTerrain(Terrain terrain, UniverseCreatorObject settings)
        {
            if (terrain == null || settings == null) return;

            EnsureRenderable(terrain, settings);

            // heightmapPixelError is deliberately NOT pushed here. It is owned per cell by
            // ChunkManager.ApplyDensity / Geometry.TerrainDensityController, which derives it
            // from the live view against the visual-acuity budget (L-49) and the source
            // field's Nyquist floor (L-50). Writing a single authored value here would fight
            // that controller: every settings change would slam every cell back to one static
            // error and force a redundant patch LOD-tree rebuild on the next density tick.
            terrain.basemapDistance = settings.basemapDistance;
            terrain.shadowCastingMode = settings.castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;

            // drawInstanced is deliberately NOT set here. EnsureRenderable above already
            // resolved it against whether the material actually compiles the instancing
            // variants. Assigning settings.drawInstanced raw at this point overwrote that
            // decision and switched instancing on over a material that never declares
            // unity_ObjectToWorldArray - every patch transform then arrives undefined and
            // the whole surface disappears while non-instanced overlays keep drawing.
            // That is the invisible-ground bug: the guard existed, this line undid it.

            // Foliage and trees. A script-created Terrain gets Unity's constructor
            // defaults for all of these, so anything not set here is invisible to the
            // user no matter what they type into the inspector.
            terrain.drawTreesAndFoliage = settings.drawTreesAndFoliage;
            terrain.detailObjectDistance = settings.detailObjectDistance;
            terrain.detailObjectDensity = settings.detailObjectDensity;
            terrain.treeDistance = settings.treeDistance;
            terrain.treeBillboardDistance = settings.treeBillboardDistance;
            terrain.treeCrossFadeLength = settings.treeCrossFadeLength;
            terrain.treeMaximumFullLODCount = settings.treeMaximumFullLODCount;

            terrain.reflectionProbeUsage = settings.reflectionProbeUsage;

            // Grass wind lives on TerrainData, not on the Terrain component.
            TerrainData data = terrain.terrainData;
            if (data != null)
            {
                data.wavingGrassStrength = settings.wavingGrassStrength;
                data.wavingGrassAmount = settings.wavingGrassAmount;
                data.wavingGrassSpeed = settings.wavingGrassSpeed;
                data.wavingGrassTint = settings.wavingGrassTint;
            }

            // Neighbour stitching happens in ChunkManager after all chunks are placed.
        }

        /// <summary>
        /// Assigns the world's terrain layers to a chunk. Splatmaps written by Texture
        /// Output nodes index these by channel, so a chunk with no layers assigned
        /// renders as untextured no matter what the graph produced.
        /// </summary>
        public static void ApplyLayers(TerrainData data, UniverseCreatorObject settings)
        {
            if (data == null || settings == null) return;
            if (settings.terrainLayers == null || settings.terrainLayers.Length == 0)
            {
                // No authored layers yet: give the chunk a visible stand-in so a new
                // world shows ground instead of reading as a failed generation.
                EnsureVisibleLayer(data);
                return;
            }

            // Skip nulls: an inspector array almost always has an empty trailing slot
            // while the user is filling it in, and the terrain rejects those.
            int count = 0;
            for (int i = 0; i < settings.terrainLayers.Length; i++)
                if (settings.terrainLayers[i] != null) count++;

            if (count == 0)
            {
                EnsureVisibleLayer(data);
                return;
            }

            TerrainLayer[] layers = new TerrainLayer[count];
            int w = 0;
            for (int i = 0; i < settings.terrainLayers.Length; i++)
                if (settings.terrainLayers[i] != null) layers[w++] = settings.terrainLayers[i];

            // Re-assigning identical layers reallocates the splatmaps and wipes what
            // the graph just painted, so only write when the set actually differs.
            TerrainLayer[] current = data.terrainLayers;
            if (current != null && current.Length == layers.Length)
            {
                bool same = true;
                for (int i = 0; i < layers.Length; i++)
                {
                    if (!ReferenceEquals(current[i], layers[i])) { same = false; break; }
                }
                if (same) return;
            }

            data.terrainLayers = layers;
        }

        /// <summary>
        /// Links neighbouring terrains so Unity stitches their edges seamlessly.
        /// ChunkManager calls this after repositioning the virtual grid.
        ///
        /// Every pair is resolution-checked first. Unity refuses to join two terrains
        /// whose heightmapResolution differs and logs "…neighbor of the terrain has a
        /// different heightmap resolution. Stop neighboring." for each rejected edge,
        /// once per pass - which is how a single mismatched cell turned into a console
        /// full of warnings. The pipeline now allocates one resolution for the whole
        /// grid, so a mismatch here means something outside it assigned a TerrainData by
        /// hand; the odd cell is left unlinked rather than allowed to spam, and the rest
        /// of the grid still stitches.
        /// </summary>
        public static void StitchNeighbours(Terrain[,] grid, int gridSize)
        {
            if (grid == null || gridSize < 1) return;

            for (int z = 0; z < gridSize; z++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    Terrain t = grid[x, z];
                    if (t == null || t.terrainData == null) continue;

                    int res = t.terrainData.heightmapResolution;

                    Terrain left = (x > 0) ? Matching(grid[x - 1, z], res) : null;
                    Terrain right = (x < gridSize - 1) ? Matching(grid[x + 1, z], res) : null;
                    Terrain bottom = (z > 0) ? Matching(grid[x, z - 1], res) : null;
                    Terrain top = (z < gridSize - 1) ? Matching(grid[x, z + 1], res) : null;

                    t.SetNeighbors(left, top, right, bottom);
                }
            }
        }

        // A neighbour is only usable if it exists, has a heightfield, and matches the
        // caller's resolution. Anything else is returned as null so SetNeighbors is never
        // handed a pair it will reject.
        private static Terrain Matching(Terrain candidate, int resolution)
        {
            if (candidate == null || candidate.terrainData == null) return null;
            if (candidate.terrainData.heightmapResolution != resolution) return null;
            return candidate;
        }
    }
}
