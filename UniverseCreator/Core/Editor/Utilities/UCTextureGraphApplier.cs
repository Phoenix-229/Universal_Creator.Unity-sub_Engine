// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    UCTextureGraphApplier.cs
//  Purpose: Turns the SurfaceProduct a texture graph produced into a real
//           Material, a real TerrainLayer array, and real baked textures.
//
//  WHY THIS IS A SEPARATE CLASS AND NOT THE OUTPUT NODE
//  ----------------------------------------------------
//  Shader Output describes; this applies. The split buys three things:
//
//    1. A cancelled evaluation cannot leave half a material written. The node
//       builds a description in memory and this class commits it in one pass, so
//       either the material changed or it did not.
//    2. A preview, a thumbnail or a self-test can evaluate the whole graph
//       without touching a project asset - they just never call Apply.
//    3. This file is the only place in the texture pipeline that needs
//       UnityEditor. The nodes themselves stay in the runtime assembly, so a
//       graph asset still deserialises in a player build.
//
//  ASSET LIFETIME - THE THING THAT LEAKS IF YOU GET IT WRONG
//  ---------------------------------------------------------
//  A naive applier creates a new Material and a new set of TerrainLayers on every
//  evaluation. Slide one slider for ten seconds and the project has three hundred
//  orphaned sub-assets, every one of them serialised into the graph file, and the
//  user's first symptom is a .asset that has grown to forty megabytes.
//
//  So the rule here is REUSE, ALWAYS:
//    - the material is found by path and its properties overwritten in place
//    - TerrainLayers are found by path and overwritten in place
//    - baked textures are re-uploaded into the existing Texture2D when the
//      resolution matches, and only reallocated when it does not
//    - a layer that a graph no longer produces is deleted, not left behind
//
//  Nothing is created that an existing asset could have carried, and nothing is
//  left behind that the graph stopped asking for. That is what keeps a project
//  that has been iterated on for a month the same size as one made this morning.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Editor.Drawing;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Commits a finished SurfaceProduct to a Material, a TerrainLayer array and
    /// any baked maps. Main thread and editor only.
    /// </summary>
    public static partial class UCTextureGraphApplier
    {
        /// <summary>Suffix for generated TerrainLayer assets, so they are findable.</summary>
        private const string LayerSuffix = "_Layer";

        /// <summary>Width of a baked gradient LUT. See the note in GradientProduct.</summary>
        private const int LutWidth = 256;

        // ---------------------------------------------------------------------
        //  Entry point
        // ---------------------------------------------------------------------

        /// <summary>
        /// Applies the description in <paramref name="outputs"/> to the terrain.
        ///
        /// Safe to call with a bag that has no surface - a height-only graph
        /// produces exactly that, and this returns false without touching
        /// anything rather than making an empty material.
        /// </summary>
        /// <param name="outputs">Evaluated material description; null, empty or UC-T02-01 obsolete/foreign assigned identities are rejected.</param>
        /// <param name="terrain">Target terrain; may be null for an unowned material-only bake.</param>
        /// <returns>True if a material was written; false on rejection or unavailable material/folder.</returns>
        /// <exception cref="System.InvalidOperationException">UC-T02-06: admitted texture bytes exceed the budget or a generated asset conflicts with authored data.</exception>
        public static bool Apply(OutputBag outputs, UnityEngine.Terrain terrain)
        {
            var world = terrain != null ? terrain.GetComponentInParent<UniverseCreator.TerrainSystem.UniverseCreatorObject>(true) : null; // UC-T07-09: inactive accepted terrain keeps its world and memory authority.
            return Apply(outputs, terrain, world != null ? world.ProductMemory : null);
        }

        // UC-T02-06: the window also supplies a budget when applying to a standalone Unity terrain.
        internal static bool Apply(OutputBag outputs, UnityEngine.Terrain terrain, ProductMemoryBudget memory)
            => Apply(outputs, terrain, memory, out _); // UC-T05-06: preserve callers while exposing the persisted descriptor to accepted-product publication.

        internal static bool Apply(OutputBag outputs, UnityEngine.Terrain terrain, ProductMemoryBudget memory, out SurfaceProduct appliedSurface)
        {
            appliedSurface = null;
            // UC-T02-01: reject stale or foreign material publication before any native resource allocation or asset write.
            if (outputs != null && outputs.RunId.IsAssigned)
            {
                var world = terrain != null ? terrain.GetComponentInParent<UniverseCreator.TerrainSystem.UniverseCreatorObject>(true) : null; // UC-T07-09: inactive terrain still requires the current owning run.
                if (world == null || !world.GenerationScheduler.IsCurrent(outputs.RunId)) return false;
            }
            if (outputs == null || outputs.surface == null) return false;

            outputs.surface.ValidateTextureChannels(); // UC-T07-02: reject explicit semantic errors before creating assets or mutating a renderer.
            if (outputs.surface.ShaderOutput != null && (int)outputs.surface.ShaderOutput.Target != outputs.surfaceShaderTarget)
                throw new System.ArgumentException("UC-T07-02: material output target differs from its captured shader contract.");

            SurfaceProduct surface = outputs.surface.Copy(); // UC-T02-06/T01-12: persistent bindings belong to this consumer, not the cached graph descriptor.
            // UC-T07-11: validate coordinates and admit continuous control resampling before assets or native terrain state change.
            bool writeCoverage = SurfaceCoverageSampling.HasCoverage(surface.splat);
            if (writeCoverage && (!outputs.surfaceWriteLayers || terrain == null || terrain.terrainData == null))
                throw new System.ArgumentException("Authored surface membership requires Write Terrain Layers and a bound terrain.");
            using ProductMemoryBudget.Lease coverageMemory = writeCoverage ? memory?.Reserve(CoverageBytes(surface, terrain.terrainData), 0) : null;
            float[,,] coverage = writeCoverage ? BuildCoverage(surface, terrain.terrainData, terrain.transform.position) : null;
            string folder = EnsureFolder(ShaderOutputNode.SanitiseFolder(outputs.surfaceOutputFolder));
            if (folder == null) return false;

            Material material = ResolveMaterial(
                folder,
                string.IsNullOrEmpty(outputs.surfaceMaterialName) ? "UC_TerrainMaterial" : outputs.surfaceMaterialName,
                (ShaderTarget)outputs.surfaceShaderTarget,
                terrain,
                surface);

            if (material == null) return false;
            ShaderOutputDescriptor.ValidateProperties(surface, material.shader, outputs.surfaceShaderTarget == (int)ShaderTarget.ExistingMaterial);
            PersistGeneratedTextures(surface, material, folder, memory); // UC-T02-06: renderer references survive release of evaluation-owned native textures.

            // Order matters. Baked maps and gradients write texture properties, so
            // they must land before the property bag is flushed - otherwise a
            // texture the graph baked this pass would be overwritten by the
            // reference the bag still holds from the last one.
            ApplyBakedMaps(surface, material, folder, memory);
            ApplyGradients(surface, material, folder, memory);
            ApplyProperties(surface, material);

            // Keywords last, and through the shader GUI's own sync, so a material
            // driven by a graph and a material driven by hand end up in exactly
            // the same state. Two code paths that set keywords differently is how
            // a feature works in the inspector and not from the graph.
            UCTerrainShaderGUI.SyncAllKeywords(material);

            if (terrain != null && terrain.terrainData != null)
            {
                if (outputs.surfaceWriteLayers && surface.splat != null)
                    ApplyTerrainLayers(surface, terrain.terrainData, folder, memory);
                if (coverage != null) terrain.terrainData.SetAlphamaps(0, 0, coverage); // UC-T07-11: continuous independently normalized weights reach Unity's final control texture.

                if (surface.vegetationTintSet)
                    ApplyVegetationTint(surface, terrain.terrainData);

                terrain.materialTemplate = material;
                var binding = terrain.GetComponentInParent<UniverseCreator.TerrainSystem.UCWorldGraphBindings>(true);
                binding?.RegisterTextureQuality(terrain); // UC-T07-11: explicit Apply admits the runtime consumer; later measured tiers only change its scalar property block.
            }

            EditorUtility.SetDirty(material);
            ReportNotices(surface);
            appliedSurface = surface; // UC-T05-06: only persisted asset references can outlive the evaluation tile.
            return true;
        }

        // ---------------------------------------------------------------------
        //  Material
        // ---------------------------------------------------------------------

        private static int CoverageLayerCount(SurfaceProduct surface, TerrainData data)
        {
            int wanted = 0;
            for (int i = 0; i < 4; i++) if (surface.splat.layers[i] != null && i < surface.maxLayers) wanted = i + 1;
            if (wanted == 0) throw new System.ArgumentException("Surface membership requires at least one authored layer.");
            return Mathf.Max(wanted, data.alphamapLayers);
        }

        private static long CoverageBytes(SurfaceProduct surface, TerrainData data)
        {
            long bytes = 128L + (long)data.alphamapWidth * data.alphamapHeight * (CoverageLayerCount(surface, data) + data.alphamapLayers) * sizeof(float);
            if (bytes > 64L * 1024 * 1024) throw new System.ArgumentException("Surface control resampling exceeds its 64 MiB work envelope; lower alphamap resolution or publish smaller terrain cells.");
            return bytes;
        }

        // Reads the old reserved channels before TerrainLayer assignment. Their paint retains its weight;
        // the authored set partitions only the remaining coverage, in terrain-local sample order [z,x,layer].
        internal static float[,,] BuildCoverage(SurfaceProduct surface, TerrainData data, Vector3 position)
        {
            CoverageBytes(surface, data);
            SplatProduct splat = surface.splat; int count = CoverageLayerCount(surface, data), wanted = 0;
            for (int i = 0; i < 4; i++)
            {
                if (splat.layers[i] != null && i < surface.maxLayers) wanted = i + 1;
                if (splat.coverage[i] != null)
                {
                    if (splat.layers[i] == null || i >= surface.maxLayers) throw new System.ArgumentException("Membership selects an absent or capped material layer.");
                    SurfaceCoverageSampling.Validate(splat.coverage[i], position, data.size);
                }
            }
            int width = data.alphamapWidth, height = data.alphamapHeight;
            float[,,] previous = data.alphamapLayers > wanted ? data.GetAlphamaps(0, 0, width, height) : null;
            var result = new float[height, width, count];
            for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
            {
                float wx = position.x + x * data.size.x / Mathf.Max(1, width - 1);
                float wz = position.z + z * data.size.z / Mathf.Max(1, height - 1);
                Vector4 weights = SurfaceCoverageSampling.Weights(splat, wx, wz);
                double reserved = 0;
                for (int i = wanted; i < count; i++)
                {
                    float value = previous != null ? previous[z, x, i] : 0;
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) throw new System.ArgumentException("Reserved terrain coverage contains invalid membership.");
                    result[z, x, i] = value; reserved += value;
                }
                if (reserved > 1) for (int i = wanted; i < count; i++) result[z, x, i] = (float)(result[z, x, i] / reserved);
                float remaining = (float)Mathf.Max(0, 1 - (float)reserved);
                for (int i = 0; i < wanted; i++) result[z, x, i] = weights[i] * remaining;
            }
            return result;
        }

        /// <summary>
        /// Finds or creates the target material.
        ///
        /// ExistingMaterial mode returns whatever is already on the terrain and
        /// creates nothing. The properties the graph writes are then filtered by
        /// HasProperty, so pointing a UC graph at a stock URP TerrainLit material
        /// sets the handful it does share and reports the rest, instead of
        /// throwing on the first name that shader has never heard of.
        /// </summary>
        private static Material ResolveMaterial(string folder, string name, ShaderTarget target,
                                                UnityEngine.Terrain terrain, SurfaceProduct surface)
        {
            if (target == ShaderTarget.ExistingMaterial)
            {
                if (terrain == null || terrain.materialTemplate == null)
                {
                    surface.Notice("Shader Output is set to Existing Material, but the terrain has none. " +
                                   "Switch to UniverseCreator TerrainLit, or assign a material first.");
                    return null;
                }
                return terrain.materialTemplate;
            }

            Shader shader = Shader.Find(UCShaderProps.ShaderName);
            if (shader == null)
            {
                surface.Notice("Shader '" + UCShaderProps.ShaderName + "' was not found. " +
                               "The terrain material cannot be built.");
                return null;
            }

            string path = folder + "/" + name + ".mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (existing != null)
            {
                // Reuse. Swapping the shader on an existing material keeps every
                // property the two shaders share, which is what a user expects
                // when they change the target and not a reset to defaults.
                if (existing.shader != shader) existing.shader = shader;
                if (!existing.enableInstancing) existing.enableInstancing = true;
                return existing;
            }

            Material created = new Material(shader);
            created.name = name;

            // new Material() leaves instancing OFF, and TerrainBuilder reads this
            // flag to decide terrain.drawInstanced - a material without it turns
            // instanced terrain rendering off for the whole world, silently. Our
            // shader compiles the instancing variants, so the answer is always yes
            // here; the flag exists to stop a shader that does NOT compile them
            // from producing undefined unity_ObjectToWorldArray and invisible
            // ground. See TerrainBuilder.ConfigureTerrain.
            created.enableInstancing = true;
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        /// <summary>
        /// Flushes the property bag onto the material.
        ///
        /// Every write is guarded by HasProperty. UCSelfTest already proves that
        /// every name in UCShaderProps exists on UC_TerrainLit, so for the normal
        /// path this guard never fires - it exists for ExistingMaterial mode,
        /// where the target shader is genuinely unknown. Setting a property a
        /// shader does not have is silently ignored by Unity, which is the wrong
        /// behaviour here: the user asked for snow and deserves to be told the
        /// material they pointed at has no snow.
        /// </summary>
        private static void ApplyProperties(SurfaceProduct surface, Material material)
        {
            int missing = 0;
            string firstMissing = null;

            ShaderOutputDescriptor.ApplyParameters(surface, material); // UC-T07-07: runtime/editor parameter updates share actual property/type validation.

            foreach (var kv in surface.textures)
            {
                if (kv.Value == null || kv.Value.texture == null) continue;
                if (!material.HasProperty(kv.Key)) { missing++; if (firstMissing == null) firstMissing = kv.Key; continue; }

                kv.Value.Apply();
                material.SetTexture(kv.Key, kv.Value.texture);
            }

            if (missing > 0)
            {
                surface.Notice("The target material has no " + firstMissing +
                               (missing > 1 ? " (and " + (missing - 1) + " other propert" +
                                              (missing == 2 ? "y" : "ies") + ")" : "") +
                               ". Those settings were skipped.");
            }
        }

        // ---------------------------------------------------------------------
        //  Terrain layers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Writes the splat set onto the terrain's TerrainLayer array.
        ///
        /// WHY THIS WRITES REAL TerrainLayer ASSETS
        /// ----------------------------------------
        /// Because the native Terrain inspector is part of what ships. A user must
        /// be able to open Paint Terrain, see the four layers the graph produced,
        /// and paint a fifth by hand. That only works if the layers are genuine
        /// TerrainLayer assets in the array - a material that samples four
        /// textures directly would render identically and leave the paint tools
        /// showing nothing.
        ///
        /// Existing layer assets are overwritten in place rather than replaced, so
        /// the control map keeps meaning what it meant. Replacing layer 2's asset
        /// with a new one would leave every texel that was painted 100% layer 2
        /// still painted layer 2 - which is correct - but replacing the ARRAY with
        /// a shorter one silently discards the alpha channel's painting.
        /// </summary>
        private static void ApplyTerrainLayers(SurfaceProduct surface, TerrainData data, string folder, ProductMemoryBudget memory)
        {
            SplatProduct splat = surface.splat;

            int wanted = 0;
            for (int i = 0; i < 4; i++)
                if (splat.layers[i] != null && i < surface.maxLayers) wanted = i + 1;

            if (wanted == 0)
            {
                surface.Notice("The splat set has no layers; the terrain's existing layers were left alone.");
                return;
            }

            TerrainLayer[] existing = data.terrainLayers;

            // The texture graph owns channels 0..wanted-1 (its hard ceiling is 4).
            // Anything the terrain already carries BEYOND that set is reserved by the
            // height/biome splat path - BiomeSplatOutputNode keeps snow at layer 4,
            // road at 5, grass at 6, forest floor at 7 - and is NOT ours to discard.
            // Shrinking the array to `wanted` would strip those reserved layers and,
            // as this file already argues above, silently wipe their painted control-
            // map channels. So the result is at least as long as what was there: we
            // overwrite only our own channels and carry the reserved tail through
            // untouched. That is the snow-layer-preserve guard.
            int total = (existing != null && existing.Length > wanted) ? existing.Length : wanted;
            TerrainLayer[] result = new TerrainLayer[total];

            for (int i = 0; i < wanted; i++)
            {
                LayerProduct src = splat.layers[i];

                // A gap in the middle of the set. The control map's channels are
                // positional, so this slot cannot simply be skipped - a blank
                // layer keeps channel i meaning channel i.
                if (src == null)
                {
                    TerrainLayer gap = (existing != null && i < existing.Length && existing[i] != null)
                        ? existing[i]
                        : ResolveLayerAsset(folder, i, "Layer " + i);
                    // A freshly minted gap layer has no diffuse, which Unity draws as its
                    // grey CHECKERBOARD. Dress it with the damp-earth stand-in so a
                    // positional gap reads as ground, not as a failed material.
                    if (gap.diffuseTexture == null)
                    {
                        gap.diffuseTexture = FlatStandInAlbedo(DampEarth, gap, folder, memory);
                        EditorUtility.SetDirty(gap);
                    }
                    SyncLayerNameToFile(gap);
                    result[i] = gap;
                    continue;
                }

                TerrainLayer layer = (existing != null && i < existing.Length && existing[i] != null &&
                                      AssetDatabase.Contains(existing[i]))
                    ? existing[i]
                    : ResolveLayerAsset(folder, i, src.name);

                WriteLayer(layer, src, surface, folder, memory);
                SyncLayerNameToFile(layer);
                EditorUtility.SetDirty(layer);
                result[i] = layer;
            }

            // Reserved layers past the texture graph's set (snow at 4, road, grass, ...)
            // are copied straight across so applying a texture graph never blows away
            // what the biome/height path put there.
            for (int i = wanted; i < total; i++)
                result[i] = existing[i];

            data.terrainLayers = result;

            if (existing != null && existing.Length > wanted)
            {
                surface.Notice("The texture graph set " + wanted + " layer(s); the " +
                               (existing.Length - wanted) + " reserved layer(s) beyond it (e.g. the " +
                               "snow layer the biome splat path keeps at index 4) were preserved, not dropped.");
            }
        }

        /// <summary>
        /// Loads the generated TerrainLayer for a channel, or creates it.
        ///
        /// Keyed by channel index and not by layer name: a user renaming a layer
        /// in the graph must not orphan the asset the control map is painted
        /// against. The name is written INTO the asset instead, so it shows
        /// correctly in the paint tools without changing identity.
        /// </summary>
        private static TerrainLayer ResolveLayerAsset(string folder, int index, string displayName)
        {
            string path = folder + "/UC" + LayerSuffix + index + ".terrainlayer";

            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer != null) return layer;

            layer = new TerrainLayer();
            // Name after the file stem, NOT the display name: Unity logs "Main Object Name
            // X does not match filename Y" whenever a saved asset's object name differs
            // from its file, and this file is keyed by channel index for a stable identity
            // (the control map is painted against it). SyncLayerNameToFile keeps the two
            // equal on every reuse; displayName is kept only for call-site readability.
            layer.name = "UC" + LayerSuffix + index;
            AssetDatabase.CreateAsset(layer, path);
            return layer;
        }

        // Unity logs "Main Object Name X does not match filename Y" for any saved asset
        // whose object name differs from its file. The layer file is keyed by channel
        // index for a stable identity (see ResolveLayerAsset), so the fix is to make the
        // OBJECT name track the FILE stem rather than the graph's renamable display name -
        // silencing the warning without renaming the asset the control map is painted on.
        private static void SyncLayerNameToFile(TerrainLayer layer)
        {
            if (layer == null) return;
            string path = AssetDatabase.GetAssetPath(layer);
            if (string.IsNullOrEmpty(path)) return; // in-memory autofill/stand-in: no file, no warning
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(stem) && layer.name != stem)
            {
                layer.name = stem;
                EditorUtility.SetDirty(layer);
            }
        }

        // Damp earth for a layer the graph left un-textured AND un-tinted. White is the
        // one colour that reads as a failed material against water and snow (the exact
        // shoreline "whitish" bug), so an undressed channel paints soil, matching the
        // biome path's TerrainBuilder.AutofillLayer. See TintIsAuthored below.
        private static readonly Color DampEarth = new Color(0.40f, 0.33f, 0.25f, 1f);

        // UC-T02-06: each persistent layer owns one reusable stand-in asset; tint edits cannot grow a static native cache.

        /// <summary>True when a tint is a real choice, not the AlbedoLayerNode default
        /// (Color.white) or an undefined black - either of which means "channel not
        /// dressed yet" and must read as ground, never as glaring white.</summary>
        private static bool TintIsAuthored(Color c)
        {
            bool nearWhite = c.r > 0.96f && c.g > 0.96f && c.b > 0.96f;
            bool nearBlack = c.r < 0.02f && c.g < 0.02f && c.b < 0.02f;
            return !nearWhite && !nearBlack;
        }

        private static Texture2D FlatStandInAlbedo(Color colour, TerrainLayer layer, string folder, ProductMemoryBudget memory)
        {
            colour.a = 1f;
            const int size = 4;
            using ProductMemoryBudget.Lease upload = memory?.Reserve(64 + size * size * 16L, 1024 + size * size * 12L);
            string stem = "UC_StandIn_" + Hash128.Compute(AssetDatabase.GetAssetPath(layer));
            string path = folder + "/" + stem + ".asset";
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            Texture2D tex = existing as Texture2D;
            if (existing != null && (tex == null || System.Array.IndexOf(AssetDatabase.GetLabels(existing), GeneratedTextureLabel) < 0))
                throw new System.InvalidOperationException("Stand-in output conflicts with an authored asset: " + path);
            bool created = tex == null;
            try
            {
                if (created) tex = new Texture2D(size, size); // sRGB albedo, RGBA32, mip chain
                tex.name = stem;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Repeat;

                Color[] px = new Color[size * size];
                for (int i = 0; i < px.Length; i++)
                {
                    // Two coprime strides over the 16 cells so value never lines up into a
                    // row or a checker; the walk rides on top of the baked colour.
                    float v = 0.92f + 0.05f * (i % 3) + 0.03f * (i % 5);
                    px[i] = new Color(colour.r * v, colour.g * v, colour.b * v, 1f);
                }
                tex.SetPixels(px);
                tex.Apply();

                if (created)
                {
                    AssetDatabase.CreateAsset(tex, path);
                    AssetDatabase.SetLabels(tex, new[] { GeneratedTextureLabel });
                }
                else EditorUtility.SetDirty(tex);
                return tex;
            }
            finally
            {
                if (created && tex != null && !AssetDatabase.Contains(tex)) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Copies one LayerProduct onto a TerrainLayer.
        ///
        /// Every write is conditional on the value actually differing. A
        /// TerrainLayer is a serialised asset: assigning a field that already
        /// holds that value still marks it dirty, and a dirty terrainlayer forces
        /// the terrain to rebuild its basemap. Doing that on every slider tick is
        /// the difference between a graph that previews smoothly and one that
        /// stutters once a second.
        /// </summary>
        private static void WriteLayer(TerrainLayer layer, LayerProduct src, SurfaceProduct surface, string folder, ProductMemoryBudget memory)
        {
            // The object name is synced to the asset's file stem by SyncLayerNameToFile
            // after this call (Unity warns when they differ), so it is not set from
            // src.name here.

            Texture2D srcAlbedo = src.albedo != null ? src.albedo.texture : null;
            bool bakedStandIn = srcAlbedo == null;
            Texture2D diffuse;
            if (bakedStandIn)
            {
                // No albedo. Bake a flat stand-in of the authored tint - but a DEFAULT
                // white tint (AlbedoLayerNode.tint = Color.white) or an undefined black
                // is not a colour choice, it is "this channel is not dressed yet", and
                // white is the one colour that reads as a failed material against water
                // and snow (the reported "whitish" band). So an unauthored channel paints
                // damp earth, matching the biome path's autofill; a real tint paints that
                // colour. The colour is baked in here, so the diffuse remap below stays at
                // identity or it would be squared.
                Color flat = TintIsAuthored(src.tint) ? (Color)src.tint : DampEarth;
                diffuse = FlatStandInAlbedo(flat, layer, folder, memory);
                surface.Notice("Layer '" + src.name + "' has no albedo texture; painting as flat " +
                               (TintIsAuthored(src.tint) ? "colour." : "damp-earth ground (no tint set)."));
            }
            else
            {
                diffuse = srcAlbedo;
            }
            if (layer.diffuseTexture != diffuse)
            {
                layer.diffuseTexture = diffuse;
                if (src.albedo != null) src.albedo.Apply();
            }

            Texture2D normal = src.normal != null ? src.normal.texture : null;
            if (layer.normalMapTexture != normal)
            {
                layer.normalMapTexture = normal;
                if (src.normal != null) src.normal.Apply();
            }

            Texture2D mask = src.mask != null ? src.mask.texture : null;
            if (layer.maskMapTexture != mask)
            {
                layer.maskMapTexture = mask;
                if (src.mask != null) src.mask.Apply();
            }

            Vector2 tile = src.uv != null ? src.uv.SafeTileSize : new Vector2(8f, 8f);
            if (layer.tileSize != tile) layer.tileSize = tile;

            Vector2 offset = src.uv != null ? src.uv.offset : Vector2.zero;
            if (layer.tileOffset != offset) layer.tileOffset = offset;

            Vector4 wantRemapMax = bakedStandIn ? Vector4.one : (Vector4)src.tint;
            if (layer.diffuseRemapMax != wantRemapMax)
            {
                // With a real albedo the tint rides on the diffuse remap - the only
                // per-layer colour multiplier the terrain engine itself understands, so
                // it keeps working in the basemap and the stock paint preview. With a
                // baked stand-in the colour is already in the texture, so the remap is
                // left at identity to avoid squaring it.
                layer.diffuseRemapMin = Vector4.zero;
                layer.diffuseRemapMax = wantRemapMax;
            }

            float ns = Mathf.Clamp(src.normalStrength, 0f, 4f);
            if (!Mathf.Approximately(layer.normalScale, ns)) layer.normalScale = ns;

            if (!Mathf.Approximately(layer.metallic, src.metallic))
                layer.metallic = Mathf.Clamp01(src.metallic);

            if (!Mathf.Approximately(layer.smoothness, src.smoothness))
                layer.smoothness = Mathf.Clamp01(src.smoothness);

            // Mask map channels are R metallic, G occlusion, B height, A
            // smoothness. Height offset shifts the B remap window, which is what
            // height blending compares - so lifting a layer makes it read through
            // its neighbours where they overlap.
            if (mask != null)
            {
                Vector4 min = layer.maskMapRemapMin;
                Vector4 max = layer.maskMapRemapMax;
                float lift = Mathf.Clamp(src.heightOffset, -1f, 1f);

                if (!Mathf.Approximately(min.z, lift) || !Mathf.Approximately(max.z, 1f + lift))
                {
                    min.z = lift;
                    max.z = 1f + lift;
                    layer.maskMapRemapMin = min;
                    layer.maskMapRemapMax = max;
                }
            }
            else if (!Mathf.Approximately(src.heightOffset, 0f))
            {
                surface.Notice("Layer '" + src.name + "' has a height offset but no mask map. " +
                               "Height blending reads the mask's B channel, so the offset has nothing to shift.");
            }
        }

        // ---------------------------------------------------------------------
        //  Baked maps and gradient LUTs
        // ---------------------------------------------------------------------

        /// <summary>
        /// Uploads CPU-baked Matrix maps as single-channel textures.
        ///
        /// The reuse rule matters most here. An occlusion bake at 1025x1025 is a
        /// four-megabyte texture; creating a fresh one per evaluation and letting
        /// the old ones fall out of scope leaves the editor holding every one of
        /// them until the next domain reload, because a Texture2D's memory lives
        /// on the native side where the GC cannot see the pressure. Re-uploading
        /// into the existing object costs one SetPixels and allocates nothing.
        /// </summary>
        private static void ApplyBakedMaps(SurfaceProduct surface, Material material, string folder, ProductMemoryBudget memory)
        {
            if (surface.bakedMaps.Count == 0) return;

            foreach (var kv in surface.bakedMaps)
            {
                if (kv.Value == null) continue;
                if (!material.HasProperty(kv.Key)) continue;

                Matrix map = kv.Value;
                int w = map.rect.size.x;
                int h = map.rect.size.z;
                if (w <= 0 || h <= 0) continue;

                // UC-T02-06: reserve upload/native demand before allocation; persisted assets then own their residency.
                using ProductMemoryBudget.Lease upload = memory?.Reserve(64 + (long)w * h * 4, 1024 + (long)w * h * 2);

                string path = folder + "/UC_Baked" + kv.Key.Replace("_", "") + ".asset";
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                bool created = tex == null;
                try
                {
                    if (created)
                    {
                        // R8 and not RGBA32: these are single-channel masks, and a
                        // quarter of the memory for exactly the same information.
                        // Linear, because an occlusion factor is a multiplier and
                        // running it through sRGB would darken it twice.
                        tex = new Texture2D(w, h, TextureFormat.R8, false, true);
                        tex.name = kv.Key;
                    }
                    else if ((tex.width != w || tex.height != h) && !tex.Reinitialize(w, h, TextureFormat.R8, false))
                        throw new System.InvalidOperationException("Unable to resize the baked map: " + path);

                    Color32[] pixels = new Color32[w * h];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(map.arr[i] * 255f), 0, 255);
                        pixels[i] = new Color32(v, v, v, 255);
                    }

                    tex.SetPixels32(pixels);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Bilinear;
                    tex.Apply(false, false);

                    if (created)
                    {
                        AssetDatabase.CreateAsset(tex, path);
                    }
                    else EditorUtility.SetDirty(tex);

                    material.SetTexture(kv.Key, tex);
                }
                finally
                {
                    // UC-T02-06: a failed asset write cannot orphan a new native texture; existing assets keep their identity.
                    if (created && tex != null && !AssetDatabase.Contains(tex)) UnityEngine.Object.DestroyImmediate(tex);
                }
            }
        }

        /// <summary>
        /// Bakes colour ramps to 256x1 LUTs.
        ///
        /// A LUT rather than shader-side interpolation because a gradient may have
        /// any number of stops, and a shader that supported N stops would need a
        /// loop with a dynamic bound in the fragment path. One texture fetch is
        /// cheaper than any number of them, and the ramp is authored at edit time
        /// so there is nothing to gain from evaluating it per pixel.
        ///
        /// Clamped, always. A repeat-wrapped ramp bleeds its last colour into its
        /// first at the seam, which on an altitude gradient means the snow line
        /// gets a thin band of beach at the top of the mountain.
        /// </summary>
        private static void ApplyGradients(SurfaceProduct surface, Material material, string folder, ProductMemoryBudget memory)
        {
            if (surface.bakedGradients.Count == 0) return;

            foreach (var kv in surface.bakedGradients)
            {
                if (kv.Value == null) continue;
                if (!material.HasProperty(kv.Key)) continue;

                // UC-T02-06: LUT staging contributes to the same evaluation peak as packed/noise textures.
                using ProductMemoryBudget.Lease upload = memory?.Reserve(64 + LutWidth * 16L, 1024 + LutWidth * 8L);
                string path = folder + "/UC_Lut" + kv.Key.Replace("_", "") + ".asset";
                Texture2D lut = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                bool created = lut == null;
                try
                {
                    if (created)
                    {
                        lut = new Texture2D(LutWidth, 1, TextureFormat.RGBA32, false, false);
                        lut.name = kv.Key;
                    }
                    else if ((lut.width != LutWidth || lut.height != 1) && !lut.Reinitialize(LutWidth, 1, TextureFormat.RGBA32, false))
                        throw new System.InvalidOperationException("Unable to resize the gradient LUT: " + path);

                    Color[] row = new Color[LutWidth];
                    for (int x = 0; x < LutWidth; x++)
                        row[x] = kv.Value.Evaluate(x / (float)(LutWidth - 1));

                    lut.SetPixels(row);
                    lut.wrapMode = TextureWrapMode.Clamp;
                    lut.filterMode = FilterMode.Bilinear;
                    lut.Apply(false, false);

                    if (created)
                    {
                        AssetDatabase.CreateAsset(lut, path);
                    }
                    else EditorUtility.SetDirty(lut);

                    material.SetTexture(kv.Key, lut);
                }
                finally
                {
                    if (created && lut != null && !AssetDatabase.Contains(lut)) UnityEngine.Object.DestroyImmediate(lut);
                }
            }
        }

        // ---------------------------------------------------------------------
        //  Vegetation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Pushes the season tint onto every detail prototype.
        ///
        /// DetailPrototype is a struct returned BY VALUE from
        /// TerrainData.detailPrototypes. Mutating the array element in place and
        /// not assigning the array back is a no-op that looks exactly like working
        /// code, and is the single most common way this write silently fails.
        /// </summary>
        private static void ApplyVegetationTint(SurfaceProduct surface, TerrainData data)
        {
            DetailPrototype[] protos = data.detailPrototypes;
            if (protos == null || protos.Length == 0) return;

            bool changed = false;
            for (int i = 0; i < protos.Length; i++)
            {
                if (protos[i].healthyColor != surface.vegetationTint)
                {
                    protos[i].healthyColor = surface.vegetationTint;
                    changed = true;
                }
                if (protos[i].dryColor != surface.vegetationDryTint)
                {
                    protos[i].dryColor = surface.vegetationDryTint;
                    changed = true;
                }
            }

            // The assignment back is the whole point. See the note above.
            if (changed) data.detailPrototypes = protos;
        }

        // ---------------------------------------------------------------------
        //  Support
        // ---------------------------------------------------------------------

        /// <summary>
        /// Creates the output folder chain if it does not exist.
        ///
        /// AssetDatabase.CreateFolder takes a parent and one new segment, so a
        /// nested path has to be walked. Returns null if the path is not under
        /// Assets, which SanitiseFolder has already guaranteed - belt and braces,
        /// because a null here is a clean skip and a bad path is an exception in
        /// the middle of a write.
        /// </summary>
        private static string EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets")) return null;
            if (AssetDatabase.IsValidFolder(folder)) return folder;

            string[] parts = folder.Split('/');
            string path = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = path + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(path, parts[i]);
                path = next;
            }

            return AssetDatabase.IsValidFolder(path) ? path : null;
        }

        /// <summary>
        /// Logs the graph's notices as one grouped message.
        ///
        /// One message and not one per notice, because a graph with a quality
        /// budget applied can easily produce six, and six separate console
        /// warnings for one evaluation trains the user to ignore the console.
        /// Info (Debug.Log) and not a warning: every notice describes a request
        /// that was honoured differently, not a failure - nothing here needs the
        /// user to act, so it must not wear the yellow warning icon that reads as
        /// "something broke".
        /// </summary>
        private static void ReportNotices(SurfaceProduct surface)
        {
            if (surface.notices.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("Universe Creator texture graph:");
            for (int i = 0; i < surface.notices.Count; i++)
                sb.Append("\n  - ").Append(surface.notices[i]);

            Debug.Log(sb.ToString());
        }
    }
}
