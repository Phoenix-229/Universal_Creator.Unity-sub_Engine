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
    public static class UCTextureGraphApplier
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
        /// <returns>True if a material was written.</returns>
        public static bool Apply(OutputBag outputs, UnityEngine.Terrain terrain)
        {
            if (outputs == null || outputs.surface == null) return false;

            SurfaceProduct surface = outputs.surface;
            string folder = EnsureFolder(ShaderOutputNode.SanitiseFolder(outputs.surfaceOutputFolder));
            if (folder == null) return false;

            Material material = ResolveMaterial(
                folder,
                string.IsNullOrEmpty(outputs.surfaceMaterialName) ? "UC_TerrainMaterial" : outputs.surfaceMaterialName,
                (ShaderTarget)outputs.surfaceShaderTarget,
                terrain,
                surface);

            if (material == null) return false;

            // Order matters. Baked maps and gradients write texture properties, so
            // they must land before the property bag is flushed - otherwise a
            // texture the graph baked this pass would be overwritten by the
            // reference the bag still holds from the last one.
            ApplyBakedMaps(surface, material, folder);
            ApplyGradients(surface, material, folder);
            ApplyProperties(surface, material);

            // Keywords last, and through the shader GUI's own sync, so a material
            // driven by a graph and a material driven by hand end up in exactly
            // the same state. Two code paths that set keywords differently is how
            // a feature works in the inspector and not from the graph.
            UCTerrainShaderGUI.SyncAllKeywords(material);

            if (terrain != null && terrain.terrainData != null)
            {
                if (outputs.surfaceWriteLayers && surface.splat != null)
                    ApplyTerrainLayers(surface, terrain.terrainData, folder);

                if (surface.vegetationTintSet)
                    ApplyVegetationTint(surface, terrain.terrainData);

                terrain.materialTemplate = material;
            }

            EditorUtility.SetDirty(material);
            ReportNotices(surface);
            return true;
        }

        // ---------------------------------------------------------------------
        //  Material
        // ---------------------------------------------------------------------

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

            foreach (var kv in surface.floats)
            {
                if (!material.HasProperty(kv.Key)) { missing++; if (firstMissing == null) firstMissing = kv.Key; continue; }
                material.SetFloat(kv.Key, kv.Value);
            }

            foreach (var kv in surface.colors)
            {
                if (!material.HasProperty(kv.Key)) { missing++; if (firstMissing == null) firstMissing = kv.Key; continue; }
                material.SetColor(kv.Key, kv.Value);
            }

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
        private static void ApplyTerrainLayers(SurfaceProduct surface, TerrainData data, string folder)
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
            TerrainLayer[] result = new TerrainLayer[wanted];

            for (int i = 0; i < wanted; i++)
            {
                LayerProduct src = splat.layers[i];

                // A gap in the middle of the set. The control map's channels are
                // positional, so this slot cannot simply be skipped - a blank
                // layer keeps channel i meaning channel i.
                if (src == null)
                {
                    result[i] = (existing != null && i < existing.Length && existing[i] != null)
                        ? existing[i]
                        : ResolveLayerAsset(folder, i, "Layer " + i);
                    continue;
                }

                TerrainLayer layer = (existing != null && i < existing.Length && existing[i] != null &&
                                      AssetDatabase.Contains(existing[i]))
                    ? existing[i]
                    : ResolveLayerAsset(folder, i, src.name);

                WriteLayer(layer, src, surface);
                EditorUtility.SetDirty(layer);
                result[i] = layer;
            }

            data.terrainLayers = result;

            if (existing != null && existing.Length > wanted)
            {
                surface.Notice("Layer count dropped from " + existing.Length + " to " + wanted +
                               ". Painting on the removed channel(s) is no longer visible.");
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
            layer.name = string.IsNullOrEmpty(displayName) ? ("Layer " + index) : displayName;
            AssetDatabase.CreateAsset(layer, path);
            return layer;
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
        private static void WriteLayer(TerrainLayer layer, LayerProduct src, SurfaceProduct surface)
        {
            if (!string.IsNullOrEmpty(src.name) && layer.name != src.name) layer.name = src.name;

            Texture2D albedo = src.albedo != null ? src.albedo.texture : null;
            if (layer.diffuseTexture != albedo)
            {
                layer.diffuseTexture = albedo;
                if (src.albedo != null) src.albedo.Apply();
            }
            if (albedo == null)
                surface.Notice("Layer '" + src.name + "' has no albedo texture; it will paint as flat colour.");

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

            if (layer.diffuseRemapMax != (Vector4)src.tint)
            {
                // Tint rides on the diffuse remap rather than on a separate
                // property, because that is the only per-layer colour multiplier
                // the terrain engine itself understands - and it therefore keeps
                // working in the basemap and in the stock paint preview.
                layer.diffuseRemapMin = Vector4.zero;
                layer.diffuseRemapMax = src.tint;
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
        private static void ApplyBakedMaps(SurfaceProduct surface, Material material, string folder)
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

                string path = folder + "/UC_Baked" + kv.Key.Replace("_", "") + ".asset";
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                bool created = false;
                if (tex == null || tex.width != w || tex.height != h)
                {
                    // R8 and not RGBA32: these are single-channel masks, and a
                    // quarter of the memory for exactly the same information.
                    // Linear, because an occlusion factor is a multiplier and
                    // running it through sRGB would darken it twice.
                    tex = new Texture2D(w, h, TextureFormat.R8, false, true);
                    tex.name = kv.Key;
                    created = true;
                }

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
                    Texture2D stale = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (stale != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(tex, path);
                }
                else EditorUtility.SetDirty(tex);

                material.SetTexture(kv.Key, tex);
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
        private static void ApplyGradients(SurfaceProduct surface, Material material, string folder)
        {
            if (surface.bakedGradients.Count == 0) return;

            foreach (var kv in surface.bakedGradients)
            {
                if (kv.Value == null) continue;
                if (!material.HasProperty(kv.Key)) continue;

                string path = folder + "/UC_Lut" + kv.Key.Replace("_", "") + ".asset";
                Texture2D lut = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                bool created = false;
                if (lut == null || lut.width != LutWidth || lut.height != 1)
                {
                    lut = new Texture2D(LutWidth, 1, TextureFormat.RGBA32, false, false);
                    lut.name = kv.Key;
                    created = true;
                }

                Color[] row = new Color[LutWidth];
                for (int x = 0; x < LutWidth; x++)
                    row[x] = kv.Value.Evaluate(x / (float)(LutWidth - 1));

                lut.SetPixels(row);
                lut.wrapMode = TextureWrapMode.Clamp;
                lut.filterMode = FilterMode.Bilinear;
                lut.Apply(false, false);

                if (created)
                {
                    Texture2D stale = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (stale != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(lut, path);
                }
                else EditorUtility.SetDirty(lut);

                material.SetTexture(kv.Key, lut);
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
        /// Warning and not Error: every notice describes a request that was
        /// honoured differently, not a failure.
        /// </summary>
        private static void ReportNotices(SurfaceProduct surface)
        {
            if (surface.notices.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("Universe Creator texture graph:");
            for (int i = 0; i < surface.notices.Count; i++)
                sb.Append("\n  - ").Append(surface.notices[i]);

            Debug.LogWarning(sb.ToString());
        }
    }
}
