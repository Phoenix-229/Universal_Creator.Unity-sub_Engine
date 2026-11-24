// UC-T07-02: distinguish a material description from its supported terminal shader contract.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Nodes
{
    /// <summary>Requested shader expression stage; this material workflow currently supports SurfaceFragment only.</summary>
    public enum ShaderExpressionStage { SurfaceFragment = 0, Vertex = 1, Compute = 2 }
    /// <summary>Immutable selection of the supported fixed terrain surface shader or an existing material's compatible properties.</summary>
    /// <remarks>This describes the surface fragment material binding, not arbitrary generated HLSL or a vertex/compute program.
    /// Property/type/keyword validation belongs to the actual shader consumer; no native resource is owned here.</remarks>
    public sealed class ShaderOutputDescriptor
    {
        /// <summary>Supported serialized target; existing target values are preserved.</summary>
        public ShaderTarget Target { get; }
        /// <summary>UC-T07-07: supported surface-fragment material stage; vertex/compute programs require a separate owner.</summary>
        public ShaderExpressionStage Stage { get; }
        /// <summary>Fixed shader name, or null when the consumer resolves an existing material.</summary>
        public string ShaderName => Target == ShaderTarget.UniverseCreatorTerrainLit ? UCShaderProps.ShaderName : null;
        /// <summary>Maximum control-map material layers supported by this terrain contract.</summary>
        public int LayerLimit => 4;
        /// <summary>UC-T07-07: existing TerrainLit feature switches; at most 256 feature combinations before URP lighting/platform variants.</summary>
        public const int FeatureKeywordLimit = 8;
        private static readonly HashSet<string> FeatureKeywords = new HashSet<string>(StringComparer.Ordinal)
        { "_UC_SLOPEBLEND_ON", "_UC_SNOW_ON", "_UC_GRADIENT_ON", "_UC_MUD_ON", "_UC_DETAIL_ON", "_UC_BEACH_ON", "_UC_AOMAP_ON", "_UC_EMISSION_ON" };

        /// <summary>Creates a supported terminal selection without allocating a material or shader.</summary>
        /// <param name="target">UniverseCreatorTerrainLit or ExistingMaterial.</param>
        /// <exception cref="ArgumentOutOfRangeException">The target enum value is unsupported.</exception>
        /// <param name="stage">SurfaceFragment; unsupported vertex/compute stages are rejected explicitly.</param>
        public ShaderOutputDescriptor(ShaderTarget target, ShaderExpressionStage stage = ShaderExpressionStage.SurfaceFragment)
        {
            if (target != ShaderTarget.UniverseCreatorTerrainLit && target != ShaderTarget.ExistingMaterial)
                throw new ArgumentOutOfRangeException(nameof(target), "UC-T07-02: unsupported shader output target.");
            if (stage != ShaderExpressionStage.SurfaceFragment)
                throw new ArgumentOutOfRangeException(nameof(stage), "UC-T07-07: this output supports surface fragment material parameters; vertex/compute expressions are unavailable.");
            Target = target; Stage = stage;
        }

        /// <summary>UC-T07-07: captures actual installed shader property types on the main thread, with a 512-property bound.</summary>
        /// <param name="shader">Required actual shader asset; borrowed and never mutated.</param>
        /// <returns>Property name/type schema independent of image pixels and terrain geometry.</returns>
        /// <exception cref="InvalidOperationException">Called off the main thread, shader is unavailable or schema is too large.</exception>
        public static IReadOnlyDictionary<string, ShaderPropertyType> PropertySchema(Shader shader)
        {
            GraphExecutionGuard.RequireMainThread("Shader property schema");
            if (shader == null) throw new InvalidOperationException("Shader is unavailable; assign an installed supported shader.");
            int count = shader.GetPropertyCount();
            if (count > 512) throw new InvalidOperationException("Shader exceeds the 512-property material authoring envelope.");
            var schema = new Dictionary<string, ShaderPropertyType>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++) schema.Add(shader.GetPropertyName(i), shader.GetPropertyType(i));
            return schema;
        }

        /// <summary>Validates finite material values and declared types before a consumer writes or persists native resources.</summary>
        /// <param name="surface">Required material description.</param>
        /// <param name="shader">Actual destination shader.</param>
        /// <param name="allowMissing">True for legacy/custom material compatibility; absent properties produce an actionable notice.</param>
        /// <exception cref="ArgumentException">A value/type is incompatible, or a required property is absent.</exception>
        public static void ValidateProperties(SurfaceProduct surface, Shader shader, bool allowMissing = false)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            ValidateFeatureKeywords(surface);
            var schema = PropertySchema(shader); int missing = 0; string first = null;
            foreach (var pair in surface.floats) { TextureImageSampling.RequireFinite(pair.Value); Check(pair.Key, ShaderPropertyType.Float); }
            foreach (var pair in surface.colors) { TextureImageSampling.RequireFinite(pair.Value); Check(pair.Key, ShaderPropertyType.Color); }
            foreach (var pair in surface.vectors)
            { TextureImageSampling.RequireFinite(new Color(pair.Value.x, pair.Value.y, pair.Value.z, pair.Value.w)); Check(pair.Key, ShaderPropertyType.Vector); }
            foreach (var pair in surface.textures) if (pair.Value != null) Check(pair.Key, ShaderPropertyType.Texture);
            foreach (var pair in surface.bakedMaps) if (pair.Value != null) Check(pair.Key, ShaderPropertyType.Texture);
            foreach (var pair in surface.bakedGradients) if (pair.Value != null) Check(pair.Key, ShaderPropertyType.Texture);
            if (missing > 0) surface.Notice("Shader '" + shader.name + "' has no property '" + first + "'" +
                (missing > 1 ? " and " + (missing - 1) + " other requested properties" : "") + "; use a compatible shader or change the requested parameter.");

            void Check(string name, ShaderPropertyType expected)
            {
                if (string.IsNullOrEmpty(name)) throw new ArgumentException("Material parameter needs a property name.");
                if (!schema.TryGetValue(name, out ShaderPropertyType actual))
                { if (!allowMissing) throw new ArgumentException("Shader has no requested property '" + name + "'."); missing++; if (first == null) first = name; return; }
                if (actual != expected && !(expected == ShaderPropertyType.Float && actual == ShaderPropertyType.Range))
                    throw new ArgumentException("Shader property '" + name + "' is " + actual + "; the graph supplied " + expected + ". Convert the parameter explicitly.");
            }
        }

        /// <summary>UC-T07-07: rejects undeclared terrain feature keywords instead of expanding the supported variant set.</summary>
        /// <param name="surface">Required surface with at most the eight existing TerrainLit feature keywords.</param>
        /// <exception cref="ArgumentException">A keyword is unsupported or the feature envelope is exceeded.</exception>
        public static void ValidateFeatureKeywords(SurfaceProduct surface)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (surface.keywords.Count > FeatureKeywordLimit) throw new ArgumentException("Terrain material exceeds its eight-feature keyword envelope.");
            foreach (string keyword in surface.keywords)
                if (!FeatureKeywords.Contains(keyword)) throw new ArgumentException("Unsupported terrain feature keyword '" + keyword + "'; use an existing shader feature owner.");
        }

        /// <summary>Applies validated scalar/colour/vector material parameters on the main thread without image baking or terrain rebuilding.</summary>
        /// <param name="surface">Required material description; missing custom properties append a notice.</param>
        /// <param name="material">Required destination renderer material, owned by its caller.</param>
        /// <exception cref="ArgumentException">Values or property types are incompatible.</exception>
        /// <remarks>Textures, keywords and persisted assets retain their existing explicit Apply owners.</remarks>
        public static void ApplyParameters(SurfaceProduct surface, Material material)
        {
            GraphExecutionGuard.RequireMainThread("Material parameter update");
            if (material == null) throw new ArgumentNullException(nameof(material));
            ValidateProperties(surface, material.shader, true);
            foreach (var pair in surface.floats) if (material.HasProperty(pair.Key)) material.SetFloat(pair.Key, pair.Value);
            foreach (var pair in surface.colors) if (material.HasProperty(pair.Key)) material.SetColor(pair.Key, pair.Value);
            foreach (var pair in surface.vectors) if (material.HasProperty(pair.Key)) material.SetVector(pair.Key, pair.Value);
        }
    }
}
