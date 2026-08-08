// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    TerrainBuilder.cs
//  Purpose: Static helpers that apply TileData.outputs to Unity TerrainData.
//           ChunkManager owns the Terrain GameObjects; this class knows only how
//           to translate OutputBag -> SetHeights/SetAlphamaps/SetHoles.
// -----------------------------------------------------------------------------

using UnityEngine;
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

        /// <summary>
        /// Applies the OutputBag from a finished background generation to the given
        /// TerrainData. Writes heightmap, splatmap, holes, and detail layers.
        /// Returns true if the collider should be rebuilt.
        /// </summary>
        public static bool ApplyOutputs(TerrainData data, OutputBag outputs, UniverseCreatorObject settings)
        {
            return ApplyOutputs(data, outputs, settings, null, default(Bounds), 0f);
        }

        /// <summary>
        /// As ApplyOutputs, but folds manual sculpt edits back in on top of the
        /// generated heights. Pass the world's override layer and the chunk's world
        /// bounds; the deltas are added before the height write, so an edit survives
        /// every subsequent graph re-run instead of being overwritten by it.
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
                                        float metresPerSample)
        {
            if (data == null || outputs == null) return false;

            bool needsColliderRebuild = false;

            // Heightmap
            if (outputs.heightmap != null)
            {
                // Manual edits are re-applied here rather than after the write,
                // because reading the heights back out of TerrainData to patch them
                // would cost a full round-trip through native memory per chunk.
                if (overrides != null && !overrides.IsEmpty && metresPerSample > 0f)
                    overrides.ApplyTo(outputs.heightmap, chunkWorldBounds, metresPerSample);

                // FINAL SAFETY NET: Clamp all height values to Unity's expected [0.0, 1.0] range.
                // This definitively prevents extreme vertex spikes caused by overflowing noise or blending logic.
                int resY = outputs.heightmap.GetLength(0);
                int resX = outputs.heightmap.GetLength(1);
                for (int y = 0; y < resY; y++)
                {
                    for (int x = 0; x < resX; x++)
                    {
                        outputs.heightmap[y, x] = Mathf.Clamp01(outputs.heightmap[y, x]);
                    }
                }

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

            // Detail layers (grass, small rocks)
            if (outputs.detailLayers != null && outputs.detailLayers.Count > 0)
            {
                foreach (var kvp in outputs.detailLayers)
                {
                    int layer = kvp.Key;
                    int[,] densities = kvp.Value;
                    if (layer >= 0 && layer < data.detailPrototypes.Length && densities != null)
                    {
                        data.SetDetailLayer(0, 0, layer, densities);
                    }
                }
            }

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
            string[] candidates =
            {
                "Nature/Terrain/Standard",              // built-in render pipeline
                "Universal Render Pipeline/Terrain/Lit",
                "HDRP/TerrainLit",
                "Nature/Terrain/Diffuse",               // legacy, no instancing path
                "Standard"                              // last resort, not a terrain shader
            };
            bool[] supportsInstancing = { true, true, true, false, false };

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
                Texture2D tex = new Texture2D(2, 2);
                tex.name = "UC_DefaultGroundTex";
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.filterMode = FilterMode.Point; // Keep edges sharp for the checker pattern

                Color c1 = new Color(0.8f, 0.8f, 0.8f, 1f); // light grey
                Color c2 = Color.white;
                
                tex.SetPixels(new Color[] { c1, c2, c2, c1 });
                tex.Apply();

                fallbackLayer = new TerrainLayer();
                fallbackLayer.name = "Default_Terrain_Layer";
                fallbackLayer.diffuseTexture = tex;
                // A 2x2 texture mapped over a 2x2 tile makes each checker square exactly 1x1 world units
                fallbackLayer.tileSize = new Vector2(2f, 2f);
                fallbackLayer.hideFlags = HideFlags.HideAndDontSave;
            }

            if (data.terrainLayers == null || data.terrainLayers.Length == 0)
            {
                data.terrainLayers = new TerrainLayer[] { fallbackLayer };
            }
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
            if (settings != null && settings.terrainMaterial != null)
                terrain.materialTemplate = settings.terrainMaterial;
            else if (terrain.materialTemplate == null)
            {
                Shader urpTerrainShader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
                if (urpTerrainShader != null)
                {
                    Material defaultTerrainMat = new Material(urpTerrainShader);
                    defaultTerrainMat.name = "Default_URP_TerrainMat";
                    terrain.materialTemplate = defaultTerrainMat;
                }
                else
                {
                    terrain.materialTemplate = FallbackMaterial();
                }
            }

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
            terrain.drawInstanced = wantInstanced && materialHandlesIt;

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
                h = h * 31 + settings.pixelError.GetHashCode();
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

            terrain.heightmapPixelError = settings.pixelError;
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
