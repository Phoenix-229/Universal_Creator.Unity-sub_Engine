// UC-T05-03: catalog and runtime admission use the same declared node-domain contract.
using System;

namespace UniverseCreator.Nodes
{
    /// <summary>Supported authoring domains for a node; independent of its worker/main-thread execution requirement.</summary>
    [Flags]
    public enum UCGraphDomainMask
    {
        /// <summary>No supported explicit domain; a legacy graph may retain the node for recovery.</summary>
        None = 0,
        /// <summary>Terrain and water generation.</summary>
        TerrainWater = 1,
        /// <summary>Placement, vegetation and ecology.</summary>
        Vegetation = 2,
        /// <summary>Lighting, baseline climate and live weather.</summary>
        LightingClimateWeather = 4,
        /// <summary>Texture, material and supported shader descriptions.</summary>
        TextureMaterialShader = 8,
        /// <summary>Shared field or editor utility, available in every explicit domain.</summary>
        All = 15
    }

    /// <summary>Shared domain admission for palettes, programmatic assignment and graph capture.</summary>
    public static class GraphDomainRules
    {
        /// <summary>Returns whether a node's declared supported domains include the selected graph domain.</summary>
        /// <param name="mask">Node's declared domain flags; undefined bits never grant admission.</param>
        /// <param name="domain">Saved graph domain; LegacyMixed preserves its prior behavior.</param>
        /// <returns>False for undeclared domain values or unsupported explicit domains.</returns>
        public static bool Supports(UCGraphDomainMask mask, UCGraphDomain domain)
        {
            switch (domain)
            {
                case UCGraphDomain.LegacyMixed: return true;
                case UCGraphDomain.TerrainWater: return (mask & UCGraphDomainMask.TerrainWater) != 0;
                case UCGraphDomain.Vegetation: return (mask & UCGraphDomainMask.Vegetation) != 0;
                case UCGraphDomain.LightingClimateWeather: return (mask & UCGraphDomainMask.LightingClimateWeather) != 0;
                case UCGraphDomain.TextureMaterialShader: return (mask & UCGraphDomainMask.TextureMaterialShader) != 0;
                default: return false;
            }
        }

        // Existing category/base-class contracts identify shared managed field arithmetic. Plugins may override GraphDomains.
        internal static UCGraphDomainMask DefaultFor(Generator node)
        {
            if (node is WorldGraphInputNode) return UCGraphDomainMask.All & ~UCGraphDomainMask.TerrainWater;
            if (node is Outputs.LightingOutputNode || node is Outputs.WeatherOutputNode)
                return UCGraphDomainMask.LightingClimateWeather;
            // UC-T08-15.11: terrain excavation/import and native ground terminals are not shared field tools.
            // LegacyMixed still retains old assets; explicit companion graphs report incompatible saved nodes.
            if (node is Creators.ContinentalShapeMaskNode || node is Creators.TectonicPlatesNode ||
                node is Creators.ImportHeightmapNode || node is Creators.RealWorldImportNode ||
                node is Modifiers.CoastalErosionNode || node is Modifiers.CoastalShelfShaperNode ||
                node is Modifiers.DropletErosionNode || node is Modifiers.FaultLineDisplacementNode ||
                node is Modifiers.GlacialCarveNode || node is Modifiers.HydraulicErosionNode ||
                node is Modifiers.LedgeNode || node is Modifiers.LithologyErosionNode ||
                node is Modifiers.SedimentMapNode || node is Modifiers.TerraceNode ||
                node is Modifiers.ThermalErosionNode || node is Modifiers.WindErosionNode ||
                node is Modifiers.WorldEdgeFalloffNode || node is Selectors.UnderwaterTerrainExtendNode ||
                node is Outputs.LithologyOutput || node is Outputs.MicroReliefOutput)
                return UCGraphDomainMask.TerrainWater;
            if (node.Category == NodeCategory.Scatter) return UCGraphDomainMask.Vegetation;
            if (node.Category == NodeCategory.Texture || node.Category == NodeCategory.Surface ||
                node.GetType().Namespace == "UniverseCreator.Nodes.Texturing") return UCGraphDomainMask.TextureMaterialShader;
            if (node.Category == NodeCategory.Biome) return UCGraphDomainMask.TerrainWater | UCGraphDomainMask.Vegetation | UCGraphDomainMask.LightingClimateWeather;
            if (node is CreatorNode || node is ModifierNode || node.Category == NodeCategory.Combiner ||
                node.Category == NodeCategory.Utility || node is Selectors.SlopeToDensityNode) return UCGraphDomainMask.All;
            return UCGraphDomainMask.TerrainWater;
        }
    }
}
