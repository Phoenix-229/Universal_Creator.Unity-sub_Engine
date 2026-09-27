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
                // The graph paints channel L meaning "terrain layer L". Unity's
                // SetAlphamaps throws "Float array size wrong (layers should be N)"
                // the instant the map's depth differs from alphamapLayers, and that
                // throw lands inside the chunk pump - it aborts ApplyChunk and the
                // whole generation stalls. That is exactly what a Biome Splat Output
                // (8 channels) wired onto a world with one authored layer produced.
                // A matching resolution throw follows if the map's width/height differ
                // from alphamapResolution (nothing assigns it, so it sits at the default
                // while the graph paints at the heightmap's resolution). Reconcile both
                // here, the last gate before the write, the way Unreal's Landscape
                // auto-provisions a layer info per weight channel rather than refusing
                // the paint. A splatmap whose depth and resolution already match (worlds
                // where those line up) passes through untouched.
                float[,,] splat = ReconcileSplatResolution(data, outputs.splatmap);
                splat = ReconcileSplatLayers(data, splat, ChunkLabel(chunkWorldBounds),
                                             outputs.splatLayerTextures);
                data.SetAlphamaps(0, 0, splat);
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

            TerrainLayer[] current = data.terrainLayers;

            // No array at all: seed it with the single stand-in.
            if (current == null || current.Length == 0)
            {
                EnsureFallbackLayer();
                data.terrainLayers = new TerrainLayer[] { fallbackLayer };
                return;
            }

            // Refill every dead slot. The stand-in layers are HideAndDontSave, so a
            // domain reload or a project reopen destroys them and leaves the array at
            // its OLD length holding dead references - which renders as Unity's
            // untextured gray, the exact brown->gray reversion the user reported.
            //
            // Detecting the dead slot (the null-check below reads a destroyed
            // UnityEngine.Object as null) is NOT enough on its own: the previous code
            // only re-seeded a zero-length array, and a reopened world's array is never
            // zero-length - it is full-length-all-dead (1 slot for a stand-in world, 8
            // for a Biome-Splat world). The dead slot has to actually be refilled. A
            // real authored layer is an asset, survives the reload, and is left as it is.
            bool anyDead = false;
            for (int i = 0; i < current.Length; i++)
                if (current[i] == null) { anyDead = true; break; }

            if (!anyDead) return;

            EnsureFallbackLayer();
            for (int i = 0; i < current.Length; i++)
                if (current[i] == null) current[i] = fallbackLayer;

            // Reassign so the property setter picks up the mutated slots.
            data.terrainLayers = current;
        }

        /// <summary>
        /// Builds the shared neutral stand-in layer once. Split out of
        /// EnsureVisibleLayer so the splat reconciler can hang autofill layers off the
        /// same texture. HideAndDontSave, so a domain reload destroys it and every
        /// caller must null-check before use.
        /// </summary>
        private static void EnsureFallbackLayer()
        {
            if (fallbackLayer != null) return;

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

        // Stand-in layers minted to fill splat channels the world has not authored.
        // Cached so a 25-chunk grid does not create a fresh set per chunk, and rebuilt
        // lazily because HideAndDontSave entries die on a domain reload.
        private static readonly List<TerrainLayer> autofillLayers = new List<TerrainLayer>();

        /// <summary>
        /// A distinct, valid TerrainLayer for splatmap channel <paramref name="index"/>
        /// that the world has not authored. The channel is real - it simply draws the
        /// neutral stand-in until the user assigns a texture to that slot, the same way
        /// an unpainted Unreal landscape layer reads. Distinct instances rather than one
        /// shared reference, so the terrain inspector does not flag "layer used more
        /// than once".
        /// </summary>
        private static TerrainLayer AutofillLayer(int index)
        {
            EnsureFallbackLayer();
            while (autofillLayers.Count <= index) autofillLayers.Add(null);
            if (autofillLayers[index] == null)
            {
                TerrainLayer layer = new TerrainLayer();
                layer.name = "UC_Autofill_Layer_" + index;
                layer.diffuseTexture = fallbackLayer.diffuseTexture;
                layer.tileSize = fallbackLayer.tileSize;
                layer.hideFlags = HideFlags.HideAndDontSave;
                autofillLayers[index] = layer;
            }
            return autofillLayers[index];
        }

        // Real layers minted from an output node's per-channel SplatLayerTexture,
        // cached by channel with a cheap signature so a 25-chunk grid reuses them and
        // they rebuild when the user changes a texture. HideAndDontSave, so they die on
        // a domain reload and the null check below rebuilds them.
        private struct DescribedLayerCache { public SplatLayerTexture desc; public TerrainLayer layer; }
        private static readonly Dictionary<int, DescribedLayerCache> describedLayers =
            new Dictionary<int, DescribedLayerCache>();

        // Flat single-colour textures keyed by quantised colour, so a snow-white stand-in
        // is built once and shared by every channel and chunk that needs that colour.
        private static readonly Dictionary<int, Texture2D> flatTintTextures =
            new Dictionary<int, Texture2D>();

        /// <summary>
        /// The layer for channel <paramref name="index"/> when the world has not authored
        /// one: dressed from a matching descriptor when the node supplied textures/tint,
        /// otherwise the neutral damp-earth autofill.
        /// </summary>
        private static TerrainLayer DescribedOrAutofillLayer(int index, IList<SplatLayerTexture> described)
        {
            if (described != null)
            {
                for (int i = 0; i < described.Count; i++)
                    if (described[i].layer == index && described[i].HasContent)
                        return DescribedLayer(index, described[i]);
            }
            return AutofillLayer(index);
        }

        private static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>
        /// Builds (or reuses) the TerrainLayer for one described channel. An empty albedo
        /// paints a flat texture of the descriptor's tint - snow-white for snow - so the
        /// channel never shows Unity's grey checker or the brown neutral stand-in. The
        /// user's own textures are referenced directly (no CPU read, so import settings
        /// never matter for the visible fix); AO is packed into a mask and is the only
        /// part that can degrade, and it degrades to albedo+normal, never to a break.
        /// </summary>
        private static TerrainLayer DescribedLayer(int index, SplatLayerTexture d)
        {
            DescribedLayerCache cached;
            if (describedLayers.TryGetValue(index, out cached) &&
                cached.layer != null && SameDescriptor(cached.desc, d))
                return cached.layer;

            TerrainLayer layer = new TerrainLayer();
            layer.name = "UC_Described_Layer_" + index;
            layer.hideFlags = HideFlags.HideAndDontSave;

            layer.diffuseTexture = d.albedo != null ? d.albedo : FlatTintTexture(d.tint);

            if (d.normal != null)
            {
                layer.normalMapTexture = d.normal;
                layer.normalScale = d.normalScale > 0f ? d.normalScale : 1f;
            }

            Texture2D mask = d.ao != null ? TryPackMask(d.ao, d.smoothness) : null;
            if (mask != null)
            {
                layer.maskMapTexture = mask;
            }
            else
            {
                layer.smoothness = Mathf.Clamp01(d.smoothness);
                layer.metallic = 0f;
            }

            float tileX = d.tileSizeMetres.x > 0.01f ? d.tileSizeMetres.x : 8f;
            float tileZ = d.tileSizeMetres.y > 0.01f ? d.tileSizeMetres.y : tileX;
            layer.tileSize = new Vector2(tileX, tileZ);

            describedLayers[index] = new DescribedLayerCache { desc = d, layer = layer };
            return layer;
        }

        private static Texture2D FlatTintTexture(Color tint)
        {
            // A black/undefined tint would read as "no colour"; give it a mid grey so the
            // layer is still visibly flat rather than invisible.
            if (tint.r <= 0f && tint.g <= 0f && tint.b <= 0f) tint = new Color(0.5f, 0.5f, 0.5f, 1f);
            tint.a = 1f;

            int key = QuantiseColour(tint);
            Texture2D tex;
            if (flatTintTextures.TryGetValue(key, out tex) && tex != null) return tex;

            const int size = 8;
            tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Repeat;
            Color[] px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = tint;
            tex.SetPixels(px);
            tex.Apply();

            flatTintTextures[key] = tex;
            return tex;
        }

        private static int QuantiseColour(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        // Cache validity by reference-equal textures + approx scalars. Deliberately not
        // GetInstanceID (deprecated in Unity 6) nor a hash - a direct compare cannot
        // collide two different descriptors onto one stale stand-in.
        private static bool SameDescriptor(SplatLayerTexture a, SplatLayerTexture b)
        {
            return a.albedo == b.albedo &&
                   a.normal == b.normal &&
                   a.ao == b.ao &&
                   ApproxColour(a.tint, b.tint) &&
                   Mathf.Approximately(a.tileSizeMetres.x, b.tileSizeMetres.x) &&
                   Mathf.Approximately(a.tileSizeMetres.y, b.tileSizeMetres.y) &&
                   Mathf.Approximately(a.smoothness, b.smoothness) &&
                   Mathf.Approximately(a.normalScale, b.normalScale);
        }

        private static bool ApproxColour(Color a, Color b)
        {
            return Mathf.Approximately(a.r, b.r) && Mathf.Approximately(a.g, b.g) &&
                   Mathf.Approximately(a.b, b.b) && Mathf.Approximately(a.a, b.a);
        }

        // Packs an AO map into a URP terrain mask: R metallic 0, G occlusion = AO
        // luminance, B height 0, A smoothness. Wrapped whole - if the AO texture cannot
        // be read on this platform the mask is skipped and the caller keeps albedo+normal.
        private static Texture2D TryPackMask(Texture2D ao, float smoothness)
        {
            try
            {
                Texture2D readable = MakeReadable(ao);
                if (readable == null) return null;

                int w = readable.width, h = readable.height;
                Color[] src = readable.GetPixels();
                Color[] dst = new Color[src.Length];
                float sm = Mathf.Clamp01(smoothness);
                for (int i = 0; i < src.Length; i++)
                {
                    float occ = src[i].r * 0.2126f + src[i].g * 0.7152f + src[i].b * 0.0722f;
                    dst[i] = new Color(0f, occ, 0f, sm);
                }

                Texture2D mask = new Texture2D(w, h, TextureFormat.RGBA32, true, true);
                mask.hideFlags = HideFlags.HideAndDontSave;
                mask.wrapMode = TextureWrapMode.Repeat;
                mask.SetPixels(dst);
                mask.Apply();

                if (readable != ao) SafeDestroy(readable);
                return mask;
            }
            catch
            {
                return null;
            }
        }

        // A CPU-readable copy of a texture without needing Read/Write enabled in its
        // import settings: blit through a temporary RenderTexture and read it back.
        // Returns the source unchanged when it is already readable.
        private static Texture2D MakeReadable(Texture2D src)
        {
            if (src == null) return null;
            if (src.isReadable) return src;

            RenderTexture rt = RenderTexture.GetTemporary(
                src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                Texture2D copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Highest channel deficit already reported this session, so the autofill
        // notice is logged once rather than once per chunk (25+ times a generation).
        private static int loggedAutofillDepth = -1;

        /// <summary>
        /// Makes a produced splatmap and the terrain's layer set agree in depth before
        /// SetAlphamaps sees them, so the write can never throw "Float array size wrong"
        /// and stall the chunk pump. Pure given the map depth and the current layer
        /// count, so a self-test can pin it without a live Terrain.
        ///
        ///   layers &lt; map depth : grow the layer set with autofill stand-ins (Unreal
        ///                          provisions a layer info per channel; it never refuses
        ///                          the weightmap). Real authored layers are kept.
        ///   layers &gt; map depth : pad the map with empty trailing channels, which does
        ///                          not disturb the per-pixel normalisation.
        ///   equal              : returned unchanged.
        /// </summary>
        public static float[,,] ReconcileSplatLayers(TerrainData data, float[,,] splat, string chunkLabel)
        {
            return ReconcileSplatLayers(data, splat, chunkLabel, null);
        }

        /// <summary>
        /// As above, but a channel the world has not authored is dressed from a matching
        /// <see cref="SplatLayerTexture"/> descriptor when one is supplied (biome-splat
        /// snow / road / grass / forest), so that channel shows the user's texture (or a
        /// flat tint when the slot is empty) instead of the neutral grey stand-in. A
        /// channel with no descriptor still falls back to the neutral autofill.
        /// </summary>
        public static float[,,] ReconcileSplatLayers(TerrainData data, float[,,] splat,
                                                      string chunkLabel, IList<SplatLayerTexture> described)
        {
            if (data == null || splat == null) return splat;

            int want = splat.GetLength(2);
            int have = data.terrainLayers != null ? data.terrainLayers.Length : 0;
            if (want == have) return splat;

            if (have < want)
            {
                // Grow the layer set. Keep every real layer the world already has and
                // fill the remainder with the described texture where one exists, else
                // a neutral stand-in, so channel L still maps to layer L.
                TerrainLayer[] grown = new TerrainLayer[want];
                TerrainLayer[] current = data.terrainLayers;
                for (int i = 0; i < want; i++)
                {
                    if (current != null && i < current.Length && current[i] != null)
                    {
                        grown[i] = current[i];
                        continue;
                    }
                    grown[i] = DescribedOrAutofillLayer(i, described);
                }
                data.terrainLayers = grown;

                if (want > loggedAutofillDepth)
                {
                    loggedAutofillDepth = want;
                    // Info, not a warning: this is a HEALED case - the missing slots are
                    // dressed (from the node's per-channel descriptors when present, else a
                    // neutral stand-in) and generation continues correctly. It once was the
                    // SetAlphamaps throw that stalled the build, so it is worth saying once,
                    // but it is guidance ("assign textures / lower Layer Count"), not a fault.
                    Debug.Log(
                        "Universe Creator: a splat output addresses " + want +
                        " layer(s) but the world had " + have +
                        " authored - the missing slot(s) are dressed with the node's per-channel " +
                        "texture/tint where set, or a neutral stand-in otherwise, so generation " +
                        "continues (this replaces the SetAlphamaps stall once seen at " +
                        (chunkLabel ?? "a chunk") + "). Assign textures to those layers on the " +
                        "Universe Creator object, or lower the node's Layer Count, to control them.");
                }
                return splat;
            }

            // have > want: pad the map's depth up to the layer count. The extra
            // channels stay at zero, so each pixel's weights still sum as before.
            int h = splat.GetLength(0);
            int w = splat.GetLength(1);
            float[,,] padded = new float[h, w, have];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    for (int L = 0; L < want; L++)
                        padded[z, x, L] = splat[z, x, L];
            return padded;
        }

        /// <summary>
        /// Makes a produced splatmap's width/height agree with the terrain's
        /// alphamapResolution before SetAlphamaps sees them - the sibling of
        /// <see cref="ReconcileSplatLayers"/>. SetAlphamaps validates depth (layers)
        /// in managed code first, then the map's w/h against alphamapResolution in the
        /// native call; the second throw ("Array size does not match the dimensions of
        /// the TerrainData") is just as uncaught and lands on the same main-thread pump,
        /// so fixing only the depth would swap one stall for another. The graph paints
        /// the splat at the heightmap's sample resolution while nothing ever assigns
        /// alphamapResolution (it sits at the TerrainData default), so the two are
        /// essentially never equal until reconciled here.
        ///
        /// Prefer making the control texture match the map, so no fidelity is lost and
        /// the splat stays per-sample like the heightmap the same graph wrote. If Unity
        /// keeps a different resolution than requested (or the map is not square), fall
        /// back to a nearest-neighbour resample onto whatever alphamapResolution actually
        /// became - the weights are already normalised per pixel, so a nearest copy keeps
        /// each pixel summing to 1. Returns the map to hand to SetAlphamaps.
        /// </summary>
        public static float[,,] ReconcileSplatResolution(TerrainData data, float[,,] splat)
        {
            if (data == null || splat == null) return splat;

            int mapH = splat.GetLength(0);
            int mapW = splat.GetLength(1);
            int layers = splat.GetLength(2);

            // Only a square map can be described by a single alphamapResolution, and
            // every terrain chunk is square (the heightmap is resolution x resolution).
            if (mapW == mapH && data.alphamapResolution != mapW)
                data.alphamapResolution = mapW;

            int aRes = data.alphamapResolution;
            if (aRes == mapW && aRes == mapH) return splat;

            // Unity kept a resolution of its own, or the map was non-square: resample to
            // exactly what the terrain now expects so the write can never be rejected.
            float[,,] resized = new float[aRes, aRes, layers];
            for (int z = 0; z < aRes; z++)
            {
                int srcZ = mapH == aRes ? z : Mathf.Min(mapH - 1, (int)((long)z * mapH / aRes));
                for (int x = 0; x < aRes; x++)
                {
                    int srcX = mapW == aRes ? x : Mathf.Min(mapW - 1, (int)((long)x * mapW / aRes));
                    for (int L = 0; L < layers; L++)
                        resized[z, x, L] = splat[srcZ, srcX, L];
                }
            }
            return resized;
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

            // A material draws nothing without a layer to draw into. On a domain reload
            // the stand-in layer + texture (HideAndDontSave) are destroyed exactly like
            // the material above, but the surviving-heights restore path re-asserted only
            // the material - never the layer - so a reloaded stand-in world kept a valid
            // material yet drew through a dead layer set and reverted to Unity's default
            // gray. Reassert the layer here too: EnsureVisibleLayer is idempotent (it
            // returns the instant a live authored layer exists) and rebuilds the neutral
            // brown ground only when every slot is null/destroyed, so authored worlds are
            // untouched and only the stand-in case self-heals. (§37) See [[EnsureVisibleLayer]].
            if (terrain.terrainData != null)
                EnsureVisibleLayer(terrain.terrainData);

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
