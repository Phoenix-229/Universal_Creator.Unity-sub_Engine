// UC-T05-01: one asset format retains explicit domain identity and legacy compatibility.
namespace UniverseCreator.Nodes
{
    /// <summary>Identifies a graph's authoring and consumer domain; saved numeric values must remain stable.</summary>
    public enum UCGraphDomain
    {
        /// <summary>Preserves the original mixed-domain workflow until an explicit migration succeeds.</summary>
        LegacyMixed = 0,
        /// <summary>Main Node Graph terrain, ground masks, hydrology and water-body generation.</summary>
        TerrainWater = 1,
        /// <summary>Vegetation, placement and ecology using accepted world inputs.</summary>
        Vegetation = 2,
        /// <summary>Lighting, baseline climate and dynamic weather belonging to one world.</summary>
        LightingClimateWeather = 3,
        /// <summary>Texture processing, surface materials and supported shader outputs.</summary>
        TextureMaterialShader = 4
    }
}
