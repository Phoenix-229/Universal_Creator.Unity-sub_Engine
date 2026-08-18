// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterProps.cs
//  Purpose: The property names on UniverseCreator/Water, for the same reason
//           UCShaderProps exists for the terrain shader - a misspelled uniform is
//           a control that silently does nothing, and that is the worst failure a
//           tool can have.
//
//  Separate from UCShaderProps rather than a section inside it, because UCSelfTest
//  reflects over every constant in that class and asks UC_TerrainLit whether it
//  has that property. A water name in there would fail a test that was working
//  perfectly. Water gets its own class, its own shader name, and its own check.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Shader property names for UniverseCreator/Water, in the order the fragment
    /// shader applies them.
    /// </summary>
    public static class UCWaterProps
    {
        /// <summary>The shader every water material targets.</summary>
        public const string ShaderName = "UniverseCreator/Water";

        // -- colour ----------------------------------------------------------
        public const string ShallowColor = "_ShallowColor";
        public const string DeepColor = "_DeepColor";
        public const string DepthFadeMetres = "_DepthFadeMetres";
        public const string Transparency = "_Transparency";
        public const string Smoothness = "_Smoothness";
        public const string FresnelPower = "_FresnelPower";
        public const string SpecularTint = "_SpecularTint";

        // -- waves -----------------------------------------------------------
        public const string WaveNormalMap = "_WaveNormalMap";
        public const string WaveTileMetres = "_WaveTileMetres";
        public const string NormalScale = "_NormalScale";
        public const string FlowSpeed = "_FlowSpeed";
        public const string SecondaryScale = "_SecondaryScale";
        public const string Refraction = "_Refraction";
        public const string UseVertexFlow = "_UseVertexFlow";
        public const string FlowStrength = "_FlowStrength";

        // -- foam ------------------------------------------------------------
        public const string FoamEnabled = "_FoamEnabled";
        public const string FoamTexture = "_FoamTexture";
        public const string FoamColor = "_FoamColor";
        public const string FoamIntensity = "_FoamIntensity";
        public const string FoamEdgeWidth = "_FoamEdgeWidth";
        public const string FoamScrollSpeed = "_FoamScrollSpeed";
        public const string FoamTileMetres = "_FoamTileMetres";
        public const string FlowFoam = "_FlowFoam";

        // -- caustics --------------------------------------------------------
        public const string CausticsEnabled = "_CausticsEnabled";
        public const string CausticsTexture = "_CausticsTexture";
        public const string CausticsColor = "_CausticsColor";
        public const string CausticsIntensity = "_CausticsIntensity";
        public const string CausticsTileMetres = "_CausticsTileMetres";
        public const string CausticsSpeed = "_CausticsSpeed";
        public const string CausticsDepthFade = "_CausticsDepthFade";

        // -- surface motion --------------------------------------------------
        public const string WaveHeight = "_WaveHeight";
        public const string WaveLength = "_WaveLength";
        public const string WaveSpeed = "_WaveSpeed";

        // -- shore -----------------------------------------------------------
        public const string EdgeSoftenMetres = "_EdgeSoftenMetres";

        // -- interactive ripples ---------------------------------------------
        public const string RippleStrength = "_RippleStrength";
        public const string RippleFoam = "_RippleFoam";
    }
}
