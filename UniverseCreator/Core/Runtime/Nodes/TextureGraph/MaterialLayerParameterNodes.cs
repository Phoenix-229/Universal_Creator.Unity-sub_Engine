// UC-T07-06: reusable PBR layer parameters edit existing descriptors; maps remain with Albedo/Normal Map Layer and the common packer.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Edits scalar PBR channels on a copied layer; colour/normal/mask maps and UV metadata remain borrowed.</summary>
    [Serializable]
    public sealed class MaterialLayerParametersNode : TextureNode
    {
        private static readonly Port[] Inputs =
        {
            new Port("Layer", PortType.Layer, PortDirection.Input), new Port("Roughness", PortType.Scalar, PortDirection.Input),
            new Port("Metallic", PortType.Scalar, PortDirection.Input), new Port("Occlusion", PortType.Scalar, PortDirection.Input),
            new Port("Emission", PortType.Color, PortDirection.Input), new Port("Opacity", PortType.Scalar, PortDirection.Input)
        };
        /// <summary>Finite roughness 0…1, converted once to smoothness = 1 - roughness.</summary>
        [Range(0, 1), Tooltip("Roughness 0…1; converted to the shader's smoothness channel.")]
        public float roughness = 0.65f;
        /// <summary>Finite metallic 0…1; natural soil, sand, snow and rock are normally zero.</summary>
        [Range(0, 1), Tooltip("Metallic 0…1; natural terrain normally remains dielectric (zero).")]
        public float metallic;
        /// <summary>Finite occlusion multiplier 0…1; one retains the supplied AO mask.</summary>
        [Range(0, 1), Tooltip("Occlusion multiplier 0…1; one preserves supplied AO.")]
        public float occlusion = 1;
        /// <summary>Nonnegative linear HDR emission RGB, without baked-in illumination.</summary>
        [ColorUsage(true, true), Tooltip("Linear HDR emitted RGB; actual lighting/exposure must be calibrated separately.")]
        public Color emission = Color.black;
        /// <summary>Finite opacity 0…1; applied only when Surface Clipping is enabled.</summary>
        [Range(0, 1), Tooltip("Material opacity 0…1; enable Surface Clipping to cut terrain coverage.")]
        public float opacity = 1;
        /// <summary>Explicitly uses albedo alpha for opacity, keeping roughness in mask alpha or its scalar default.</summary>
        [Tooltip("Use albedo alpha as opacity. Smoothness then comes from mask alpha or its scalar default.")]
        public bool albedoAlphaIsOpacity;
        public override string NodeName => "Material Layer Parameters";
        public override string Tooltip => "Reusable roughness/metallic/AO/emission/opacity channels; parameter edits do not bake textures.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => TexturePorts.LayerOut;

        /// <summary>Copies the upstream layer and resolves scalar/colour parameters, retaining all maps and mapping.</summary>
        /// <param name="outputIndex">Sole layer output, zero.</param>
        /// <param name="tile">Bound graph input/lifetime owner.</param>
        /// <param name="stop">Optional cooperative cancellation.</param>
        /// <returns>Independent layer metadata, or null when cancelled.</returns>
        /// <exception cref="ArgumentException">A material parameter is nonfinite or outside its declared range.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            var layer = GetInputLayer(tile, 0, stop)?.Copy() ?? new LayerProduct();
            layer.smoothness = 1 - Unit(GetInputScalarOr(tile, 1, stop, roughness));
            layer.metallic = Unit(GetInputScalarOr(tile, 2, stop, metallic));
            layer.occlusion = Unit(GetInputScalarOr(tile, 3, stop, occlusion));
            layer.emission = TryGetInputColor(tile, 4, stop, out Color colour) ? colour : emission;
            layer.opacity = Unit(GetInputScalarOr(tile, 5, stop, opacity)); layer.albedoAlphaIsOpacity = albedoAlphaIsOpacity;
            layer.ValidateTextureChannels(); return stop != null && stop.StopRequested ? null : layer;
        }
        private static float Unit(float value)
        { TextureImageSampling.RequireFinite(value); if (value < 0 || value > 1) throw new ArgumentException("Material parameter must be 0…1."); return value; }
    }

    /// <summary>Enables supported TerrainLit cutoff clipping; this does not create transparent terrain or change terrain heights.</summary>
    [Serializable]
    public sealed class SurfaceClippingNode : SurfaceFeatureNode
    {
        /// <summary>Finite coverage cutoff 0…1; fragments with opacity below this value are discarded in every depth/shadow pass.</summary>
        [Range(0, 1), Tooltip("Opacity cutoff 0…1; applied consistently to forward, shadow and depth passes.")]
        public float cutoff = 0.5f;
        public override string NodeName => "Surface Clipping";
        public override string Tooltip => "Cut off low layer opacity in TerrainLit without changing terrain geometry.";
        protected override void Apply(SurfaceProduct surface, TileData tile, StopToken stop)
        {
            TextureImageSampling.RequireFinite(cutoff);
            if (cutoff < 0 || cutoff > 1) throw new ArgumentException("Surface cutoff must be 0…1.");
            surface.SetFloat(UCShaderProps.EnableSurfaceClip, 1); surface.SetFloat(UCShaderProps.SurfaceCutoff, cutoff);
        }
    }
}
