// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    UCShaderProps.cs
//  Purpose: The single list of shader property names the texture graph writes.
//
//  WHY THIS FILE EXISTS
//  --------------------
//  SurfaceProduct is a string-keyed property bag. That buys a node the freedom to
//  add one slider without editing three files, and it costs the risk that a typo
//  produces a control which does nothing at all - the worst failure mode in a
//  tool, because it looks like it worked.
//
//  Two things close that gap, and both need this file:
//
//    1. Nodes reference UCShaderProps.SnowAltitude, never "_SnowAltitude". A
//       misspelling is then a compile error rather than a silent no-op.
//    2. UCSelfTest reflects over every const in here and asks the shader whether
//       it actually has that property. A name the shader dropped or renamed is a
//       red test in batch mode, before the asset ships.
//
//  So the rule is: if a node writes a key, the key is declared here. There is no
//  second place to look, and nothing is spelled twice.
//
//  Names are the shader's, not ours to choose - _Splat0 and friends are read by
//  Unity's terrain engine by string, and the UC block matches
//  UC_TerrainLitInput.hlsl line for line.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Shader property and keyword names for UniverseCreator/TerrainLit. Grouped
    /// in the order the fragment shader applies them, so this file reads as the
    /// pipeline it drives.
    /// </summary>
    public static class UCShaderProps
    {
        /// <summary>The shader every texture graph targets.</summary>
        public const string ShaderName = "UniverseCreator/TerrainLit";

        // -- blending --------------------------------------------------------
        public const string EnableHeightBlend = "_EnableHeightBlend";
        public const string HeightTransition = "_HeightTransition";

        // -- occlusion -------------------------------------------------------
        public const string OcclusionStrength = "_OcclusionStrength";
        public const string CavityStrength = "_CavityStrength";
        public const string EnableAOMap = "_EnableAOMap";
        public const string AOMap = "_AOMap";
        public const string AOMapStrength = "_AOMapStrength";

        // -- macro variation -------------------------------------------------
        public const string EnableMacroVariation = "_EnableMacroVariation";
        public const string MacroVariationScale = "_MacroVariationScale";
        public const string MacroVariationAmount = "_MacroVariationAmount";
        public const string MacroVariationColor = "_MacroVariationColor";

        // -- slope / cliff ---------------------------------------------------
        public const string EnableSlopeBlend = "_EnableSlopeBlend";
        public const string SlopeTex = "_SlopeTex";
        public const string SlopeNormal = "_SlopeNormal";
        public const string SlopeColor = "_SlopeColor";
        public const string SlopeAngleStart = "_SlopeAngleStart";
        public const string SlopeAngleEnd = "_SlopeAngleEnd";
        public const string SlopeSmoothness = "_SlopeSmoothness";
        public const string SlopeMetallic = "_SlopeMetallic";
        public const string SlopeNormalStrength = "_SlopeNormalStrength";
        public const string SlopeTriplanarScale = "_SlopeTriplanarScale";

        // -- snow ------------------------------------------------------------
        public const string EnableSnow = "_EnableSnow";
        public const string SnowTex = "_SnowTex";
        public const string SnowNormal = "_SnowNormal";
        public const string SnowColor = "_SnowColor";
        public const string SnowAltitude = "_SnowAltitude";
        public const string SnowFalloff = "_SnowFalloff";
        public const string SnowSlopeLimit = "_SnowSlopeLimit";
        public const string SnowSlopeFalloff = "_SnowSlopeFalloff";
        public const string SnowCoverage = "_SnowCoverage";
        public const string SnowSmoothness = "_SnowSmoothness";
        public const string SnowNormalStrength = "_SnowNormalStrength";
        public const string SnowTriplanarScale = "_SnowTriplanarScale";

        // -- wetness ---------------------------------------------------------
        public const string EnableWetness = "_EnableWetness";
        public const string WetnessAmount = "_WetnessAmount";
        public const string WetDarkening = "_WetDarkening";
        public const string WetSmoothness = "_WetSmoothness";
        public const string WetSpecBoost = "_WetSpecBoost";
        public const string PuddleThreshold = "_PuddleThreshold";
        public const string PuddleLevel = "_PuddleLevel";
        public const string PuddleFalloff = "_PuddleFalloff";

        // -- mud / dirt ------------------------------------------------------
        public const string EnableMud = "_EnableMud";
        public const string MudTex = "_MudTex";
        public const string MudColor = "_MudColor";
        public const string MudScale = "_MudScale";
        public const string MudThreshold = "_MudThreshold";
        public const string MudBlend = "_MudBlend";
        public const string MudSmoothness = "_MudSmoothness";
        public const string MudSlopeLimit = "_MudSlopeLimit";

        // -- detail overlay --------------------------------------------------
        public const string EnableDetail = "_EnableDetail";
        public const string DetailTex = "_DetailTex";
        public const string DetailScale = "_DetailScale";
        public const string DetailStrength = "_DetailStrength";
        public const string DetailBlendMode = "_DetailBlendMode";
        public const string DetailFadeStart = "_DetailFadeStart";
        public const string DetailFadeEnd = "_DetailFadeEnd";

        // -- gradient LUT ----------------------------------------------------
        public const string EnableGradient = "_EnableGradient";
        public const string GradientTex = "_GradientTex";
        public const string GradientMode = "_GradientMode";
        public const string GradientBlendMode = "_GradientBlendMode";
        public const string GradientMin = "_GradientMin";
        public const string GradientMax = "_GradientMax";
        public const string GradientStrength = "_GradientStrength";

        // -- emission --------------------------------------------------------
        public const string EnableEmission = "_EnableEmission";
        public const string EmissionMap = "_EmissionMap";
        public const string EmissionColor = "_EmissionColor";
        public const string EmissionIntensity = "_EmissionIntensity";

        // -- global PBR remap ------------------------------------------------
        public const string SmoothnessRemapMin = "_SmoothnessRemapMin";
        public const string SmoothnessRemapMax = "_SmoothnessRemapMax";
        public const string MetallicScale = "_MetallicScale";
        public const string RoughnessVariationScale = "_RoughnessVariationScale";
        public const string RoughnessVariationAmount = "_RoughnessVariationAmount";

        // -- distance fade ---------------------------------------------------
        public const string EnableDistanceFade = "_EnableDistanceFade";
        public const string DistanceFadeStart = "_DistanceFadeStart";
        public const string DistanceFadeEnd = "_DistanceFadeEnd";

        // -- colour grade ----------------------------------------------------
        public const string Saturation = "_Saturation";
        public const string Brightness = "_Brightness";
        public const string Contrast = "_Contrast";
        public const string TintColor = "_TintColor";

        // ---- keywords ------------------------------------------------------

        /// <summary>
        /// The only two features expensive enough to earn a real shader keyword -
        /// six extra texture fetches each. Everything else branches on a uniform
        /// float, which is why there is no keyword constant for it.
        /// </summary>
        public const string KeywordSlopeBlend = "_UC_SLOPEBLEND_ON";
        public const string KeywordSnow = "_UC_SNOW_ON";
    }
}
