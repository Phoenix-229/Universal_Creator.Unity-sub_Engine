// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterApplier.cs
//  Purpose: Turns the WaterProduct the texture graph produced into a real water
//           material, and makes sure the pipeline can actually render it.
//
//  WHY IT IS SEPARATE FROM UCTextureGraphApplier
//  ---------------------------------------------
//  Different shader, different material, different asset, different failure mode.
//  Folding water into the terrain applier would mean every terrain apply carries a
//  water branch it usually skips, and - worse - a water problem would be reported
//  as a terrain problem.
//
//  The asset discipline is copied verbatim from UCTextureGraphApplier, because the
//  failure it prevents is the same one: a material created per evaluation turns a
//  ten-second slider drag into three hundred orphaned assets. Find by path,
//  overwrite in place, create only when nothing is there.
//
//  THE PIPELINE CHECK IS NOT OPTIONAL
//  ----------------------------------
//  UC_Water reads _CameraDepthTexture and _CameraOpaqueTexture. If either is off
//  on the URP asset the shader still compiles and still renders - as a flat sheet
//  of deep colour with no shoreline, no refraction and no caustics. That is the
//  worst kind of failure: it looks like the water was authored badly rather than
//  configured wrongly. So this turns both on, and says so in the console once.
// -----------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Commits a WaterProduct to a Material. Main thread and editor only.
    /// </summary>
    public static class UCWaterApplier
    {
        /// <summary>Name of the generated material asset.</summary>
        public const string MaterialName = "UC_WaterMaterial";

        // Reported once per domain reload rather than per apply. A warning that
        // fires on every slider drag is a warning the user learns to ignore.
        private static bool reportedPipeline;

        // ---------------------------------------------------------------------
        //  Entry point
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds or updates the water material from the graph's description.
        /// Returns null when the graph has no water chain, which is the normal case
        /// for a dry world and must not be treated as an error.
        /// </summary>
        public static Material Apply(OutputBag outputs)
        {
            if (outputs == null || outputs.surface == null) return null;

            WaterProduct water = outputs.surface.water;
            if (water == null) return null;

            string folder = EnsureFolder(ShaderOutputNode.SanitiseFolder(outputs.surfaceOutputFolder));
            if (folder == null) return null;

            Material material = ResolveMaterial(folder);
            if (material == null) return null;

            ApplyProduct(material, water);
            EnsurePipelineFeatures();

            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Copies every WaterProduct field onto the material.
        ///
        /// Public because UCWaterBuilder applies the same product to a runtime
        /// material instance when a body needs its own settings - a muddy river and
        /// a clear lake in one world share the shader and not the material. Two
        /// copies of this mapping is how a control ends up working on one water
        /// body and not the other.
        /// </summary>
        public static void ApplyProduct(Material m, WaterProduct w)
        {
            if (m == null || w == null) return;

            SetColor(m, UCWaterProps.ShallowColor, w.shallowColor);
            SetColor(m, UCWaterProps.DeepColor, w.deepColor);
            SetFloat(m, UCWaterProps.DepthFadeMetres, w.depthFadeMetres);
            SetFloat(m, UCWaterProps.Transparency, w.transparency);
            SetFloat(m, UCWaterProps.Smoothness, w.smoothness);
            SetFloat(m, UCWaterProps.FresnelPower, w.fresnelPower);
            SetFloat(m, UCWaterProps.Refraction, w.refraction);

            SetTexture(m, UCWaterProps.WaveNormalMap, w.normalMap);
            SetFloat(m, UCWaterProps.NormalScale, w.normalScale);
            SetFloat(m, UCWaterProps.WaveTileMetres, w.waveTileMetres);

            // Flow is a Vector2 in metres per second on the product and a float4 on
            // the shader. zw are unused and written as zero rather than left alone,
            // because a stale z from a previous graph would be read as garbage by
            // any future use of those channels.
            m.SetVector(UCWaterProps.FlowSpeed,
                new Vector4(w.flowMetresPerSecond.x, w.flowMetresPerSecond.y, 0f, 0f));

            SetFloat(m, UCWaterProps.FoamEnabled, w.foamEnabled ? 1f : 0f);
            SetTexture(m, UCWaterProps.FoamTexture, w.foamTexture);
            SetColor(m, UCWaterProps.FoamColor, w.foamColor);
            SetFloat(m, UCWaterProps.FoamIntensity, w.foamIntensity);
            SetFloat(m, UCWaterProps.FoamEdgeWidth, w.foamEdgeWidthMetres);
            SetFloat(m, UCWaterProps.FoamScrollSpeed, w.foamScrollSpeed);
            SetFloat(m, UCWaterProps.FoamTileMetres, w.foamTileMetres);

            SetFloat(m, UCWaterProps.CausticsEnabled, w.causticsEnabled ? 1f : 0f);
            SetTexture(m, UCWaterProps.CausticsTexture, w.causticsTexture);
            SetColor(m, UCWaterProps.CausticsColor, w.causticsColor);
            SetFloat(m, UCWaterProps.CausticsIntensity, w.causticsIntensity);
            SetFloat(m, UCWaterProps.CausticsTileMetres, w.causticsTileMetres);
            SetFloat(m, UCWaterProps.CausticsSpeed, w.causticsSpeed);
            SetFloat(m, UCWaterProps.CausticsDepthFade, w.causticsDepthFadeMetres);

            SetFloat(m, UCWaterProps.WaveHeight, w.waveHeightMetres);
            SetFloat(m, UCWaterProps.WaveLength, w.waveLengthMetres);
            SetFloat(m, UCWaterProps.WaveSpeed, w.waveSpeed);
            SetFloat(m, UCWaterProps.EdgeSoftenMetres, w.edgeSoftenMetres);

            SetFloat(m, UCWaterProps.RippleStrength, w.rippleStrength);
            SetFloat(m, UCWaterProps.RippleFoam, w.rippleFoam);

            // Fallback-shader tint; see UCWaterMaterials.ResolveWaterShader. Guarded,
            // so it is a no-op on UC_Water, which has no _BaseColor. Kept identical to
            // the runtime applier so the two stay in agreement (UCSelfTest checks it).
            SetColor(m, "_BaseColor", w.shallowColor);
        }

        /// <summary>
        /// The physics side of the same description. Rendering and buoyancy must
        /// read one source or a boat floats off the visible surface, so the
        /// conversion lives here next to the material write rather than in the
        /// component that consumes it.
        /// </summary>
        public static WaterSurfaceParams ToSurfaceParams(WaterProduct w, float level)
        {
            WaterSurfaceParams p = WaterSurfaceParams.Default(level);
            if (w == null) return p;

            p.waveHeight = w.waveHeightMetres;
            p.waveLength = w.waveLengthMetres;
            p.waveSpeed = w.waveSpeed;
            p.flow = w.flowMetresPerSecond;
            return p;
        }

        // ---------------------------------------------------------------------
        //  Material asset
        // ---------------------------------------------------------------------

        private static Material ResolveMaterial(string folder)
        {
            // Shared resolver (runtime): UC_Water, or a pipeline-correct URP fallback
            // if it was stripped/failed to compile, so the material is never the white
            // default (spec 4.4). The missing-shader warning is logged once there.
            Shader shader = UniverseCreator.TerrainSystem.UCWaterMaterials.ResolveWaterShader();
            if (shader == null) return null;

            string path = folder + "/" + MaterialName + ".mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (existing != null)
            {
                if (existing.shader != shader) existing.shader = shader;
                return existing;
            }

            Material created = new Material(shader);
            created.name = MaterialName;

            // Not instanced. Water bodies are a handful of large meshes, so
            // instancing buys nothing, and the ForwardLit pass reads screen-space
            // textures - which is the one case where batching them together would
            // be actively wrong.
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        // ---------------------------------------------------------------------
        //  Pipeline
        // ---------------------------------------------------------------------

        /// <summary>
        /// Turns on the two URP features UC_Water cannot work without.
        ///
        /// Written through SerializedObject rather than through the properties,
        /// because supportsCameraDepthTexture and supportsCameraOpaqueTexture are
        /// read-only on UniversalRenderPipelineAsset - the backing fields are what
        /// the inspector edits, and this is the only way to set them from code.
        /// </summary>
        public static void EnsurePipelineFeatures()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null)
            {
                if (!reportedPipeline)
                {
                    reportedPipeline = true;
                    Debug.LogWarning("[Universe Creator] No Universal Render Pipeline asset is " +
                                     "active. UC_Water needs URP; water will not render correctly.");
                }
                return;
            }

            var so = new SerializedObject(asset);
            SerializedProperty depth = so.FindProperty("m_RequireDepthTexture");
            SerializedProperty opaque = so.FindProperty("m_RequireOpaqueTexture");

            bool changed = false;

            if (depth != null && !depth.boolValue) { depth.boolValue = true; changed = true; }
            if (opaque != null && !opaque.boolValue) { opaque.boolValue = true; changed = true; }

            if (!changed) return;

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);

            Debug.Log("[Universe Creator] Enabled Depth Texture and Opaque Texture on the active " +
                      "URP asset. UC_Water reads both - without them water renders as a flat " +
                      "sheet with no shoreline or refraction.");
        }

        // ---------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------

        // Every write guarded by HasProperty. UCSelfTest proves every name in
        // UCWaterProps exists on UC_Water, so this never fires on the normal path -
        // it exists so that pointing this at a hand-made material with a different
        // shader sets what it shares and skips the rest, instead of throwing on the
        // first unknown name.
        private static void SetFloat(Material m, string name, float value)
        {
            if (m.HasProperty(name)) m.SetFloat(name, value);
        }

        private static void SetColor(Material m, string name, Color value)
        {
            if (m.HasProperty(name)) m.SetColor(name, value);
        }

        private static void SetTexture(Material m, string name, TextureRef reference)
        {
            if (!m.HasProperty(name)) return;
            if (reference == null || reference.texture == null) return;

            reference.Apply();
            m.SetTexture(name, reference.texture);
        }

        private static string EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets")) return null;
            if (AssetDatabase.IsValidFolder(folder)) return folder;

            string[] parts = folder.Split('/');
            string built = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = built + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }

            return AssetDatabase.IsValidFolder(built) ? built : null;
        }
    }
}
