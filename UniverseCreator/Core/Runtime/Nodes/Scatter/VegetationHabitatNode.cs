// UC-T06-04: compose the existing species, biome and placement laws into a mask; never maintain a second ecology model.
using System;
using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>UC-T06-11: optional water-domain restriction composed with the existing species suitability law.</summary>
    public enum VegetationWaterHabitat
    {
        /// <summary>Retain the species envelope's habitat without an additional water-domain restriction.</summary>
        Species = 0,
        /// <summary>Roots must lie below an observed/inferred water surface within the authored depth band.</summary>
        Submerged,
        /// <summary>Root depth must fit the band and remain below the species' minimum mature stem height.</summary>
        Emergent,
        /// <summary>Dry root contact within the authored water-proximity distance; proximity can be a documented bounds estimate.</summary>
        WetBank,
        /// <summary>Dry root contact retaining the existing shore exclusion and species laws.</summary>
        DryLand
    }

    /// <summary>Selects the existing prototype roster used to resolve a habitat's named species.</summary>
    public enum VegetationHabitatFamily
    {
        /// <summary>Native terrain tree prototypes.</summary>
        Trees,
        /// <summary>Instanced shrub prototypes.</summary>
        Shrubs,
        /// <summary>Terrain grass/detail prototypes.</summary>
        Details,
        /// <summary>Generic and aquatic object prototypes; depth rules distinguish their habitats.</summary>
        Objects
    }

    /// <summary>Produces a suitability mask from a named prototype's existing ecological envelope and accepted world channels.</summary>
    /// <remarks>Worker only. Soil depth, rockiness and fertility are topographic estimates; salinity is inferred from water kind/spray. Measured substrate, pH, saline-lake and estuary mixing fields are unsupported. This mask adds no controls for those missing sources.</remarks>
    [Serializable]
    public sealed class VegetationHabitatNode : Generator
    {
        /// <summary>Prototype roster searched for speciesName; must be a defined family.</summary>
        public VegetationHabitatFamily family;
        /// <summary>Assigned EcoEnvelope display name, matched without case or surrounding whitespace; blank/missing names reject execution.</summary>
        [Tooltip("Species name from this world's prototype library. Habitat controls remain in that species' envelope.")]
        public string speciesName = "";
        /// <summary>Channels required in addition to Height. Missing required data rejects execution; optional data uses the sampler's documented estimates.</summary>
        [Tooltip("Require measured/routed channels before sampling. Missing required channels produce an error. Soil is estimated from topography; salinity from water kind and spray. No measured substrate, pH or estuary mixing source exists.")]
        public EnvironmentChannel requiredChannels = EnvironmentChannel.Height;
        /// <summary>UC-T06-11: optional water-domain restriction; Species preserves existing graphs.</summary>
        [Tooltip("Optional submerged/emergent/wet-bank/dry-land restriction. Species envelopes still govern suitability. Accepted meshes supply real root depth; outside coverage, bank proximity is a bounds estimate.")]
        public VegetationWaterHabitat waterHabitat;
        /// <summary>Positive ordered root-depth band in metres for Submerged/Emergent modes.</summary>
        [Tooltip("Root depth in world metres for submerged/emergent habitat. Emergent also requires depth below the species' minimum mature height.")]
        public Vector2 waterDepthRangeMetres = new Vector2(0.01f, 2f);
        /// <summary>Nonnegative maximum dry-bank proximity in metres; active only for WetBank.</summary>
        [Min(0f), Tooltip("Dry-bank proximity in metres. Outside accepted triangle coverage this is a conservative body-bounds estimate, not a surveyed shoreline.")]
        public float wetBankDistanceMetres = 5f;
        /// <summary>Require accepted mesh current in metres per second; missing current is an error, never inferred from flow accumulation.</summary>
        public bool requireAcceptedCurrent;
        /// <summary>Finite nonnegative ordered current-speed band in metres per second; active only when current is required.</summary>
        [Tooltip("Accepted steady mesh current in m/s. Flow accumulation/scour is dimensionless and cannot supply this channel.")]
        public Vector2 currentSpeedRangeMetresPerSecond = new Vector2(0f, 2f);
        private static readonly Port[] Inputs = {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Allowed Area", PortType.Matrix, PortDirection.Input)
        };
        public override string NodeName => "Vegetation Habitat";
        public override NodeCategory Category => NodeCategory.Scatter;
        public override string Tooltip => "Suitability for a named species using accepted ground, climate, biome, water and estimated soil. Allowed Area is 0 excluded, 1 allowed. Wire the mask to a scatter consumer; species selection still applies its own law.";
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => SharedPorts.MaskOut;

        /// <summary>Samples the existing suitability/placement pipeline on the accepted ground lattice.</summary>
        /// <param name="outputIndex">Mask outlet index, zero.</param>
        /// <param name="tile">Worker tile with accepted Height and its own ecology context.</param>
        /// <param name="stop">Optional cancellation, checked every row; cancelled work returns null.</param>
        /// <returns>Finite [0,1] suitability times biome affinity and optional allowed-area mask; invalid individual environments are excluded.</returns>
        /// <exception cref="ArgumentNullException">Tile is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Outlet or prototype family is undefined.</exception>
        /// <exception cref="InvalidOperationException">Height, ecology, the named species or required channels are missing, or the species habitat is invalid.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (tile == null) throw new ArgumentNullException(nameof(tile));
            if (outputIndex != 0) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            if (stop != null && stop.StopRequested) return null;
            Matrix height = GetInputMatrix(tile, 0, stop), allowed = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;
            EcoScatterContext context = tile.ecology;
            if (height == null || context == null)
                throw new InvalidOperationException("Vegetation Habitat requires accepted Height and this world's ecology context.");
            SpeciesTable table;
            switch (family)
            {
                case VegetationHabitatFamily.Trees: table = context.trees; break;
                case VegetationHabitatFamily.Shrubs: table = context.shrubs; break;
                case VegetationHabitatFamily.Details: table = context.details; break;
                case VegetationHabitatFamily.Objects: table = context.objects; break;
                default: throw new ArgumentOutOfRangeException(nameof(family));
            }
            int index = table != null ? table.FindSlotByName(speciesName) : -1;
            if (index < 0) throw new InvalidOperationException("Vegetation Habitat species is not assigned in this world's selected prototype roster: " + speciesName);
            EcoEnvelope envelope = table.envelopes[index];
            if (!SpeciesSuitability.HasValidHabitatParameters(envelope))
                throw new InvalidOperationException("Vegetation Habitat species has invalid habitat parameters: " + speciesName);
            ValidateWaterSettings(envelope); // UC-T06-11: authoring failures precede worker mask writes.
            context.EnsureHydrology(this, tile, stop);
            if (stop != null && stop.StopRequested) return null;
            context.EnsureBound(height);
            const EnvironmentChannel supported = EnvironmentChannel.Height | EnvironmentChannel.Moisture | EnvironmentChannel.Biome |
                EnvironmentChannel.Water | EnvironmentChannel.Derivatives;
            EnvironmentChannel missing = context.sampler.MissingOutputs & (requiredChannels | EnvironmentChannel.Height);
            if ((requiredChannels & ~supported) != 0 || missing != EnvironmentChannel.None)
                throw new InvalidOperationException("Vegetation Habitat required channels are unavailable: " + missing + ". " + context.sampler.DescribeMissing());
            if (requireAcceptedCurrent && tile.worldGraphGround?.Water?.HasSurfaces != true)
                throw new InvalidOperationException("Vegetation Habitat current requires accepted water-mesh observations in m/s; inferred scour is unavailable for this input.");
            Matrix result = height.Blank();
            for (int z = 0; z < result.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < result.rect.size.x; x++)
                {
                    Vector3 point = result.PixelToWorld(x + result.rect.offset.x, z + result.rect.offset.z);
                    EnvironmentSample site = context.sampler.Sample(point.x, point.z);
                    float score = 0;
                    if (site.IsValidForHabitat() && PlacementClearance.TerrainAdmits(in context.settings, in site) &&
                        PlacementClearance.CutRegionsAdmit(context.cutRegions, context.cutRegionCount, point.x, point.z, site.worldPos.y,
                            context.settings.avoidCutRegions, context.settings.clearanceM) &&
                        PlacementClearance.WaterAdmits(envelope, in site, context.settings.avoidWater, context.settings.clearanceM,
                            envelope.submergedDepthRangeM.y > 0f && waterHabitat != VegetationWaterHabitat.DryLand) && // UC-T06-11: explicit aquatic species mask does not inherit the land-only guard.
                        WaterHabitatAdmits(waterHabitat, in site, waterDepthRangeMetres, wetBankDistanceMetres, envelope.matureHeightM.x) &&
                        (!requireAcceptedCurrent || (context.sampler.TryCurrentMetresPerSecond(point.x, point.z, out float speed) &&
                            speed >= currentSpeedRangeMetresPerSecond.x && speed <= currentSpeedRangeMetresPerSecond.y)))
                        score = SpeciesSuitability.EvaluateValidSite(envelope, in site) * BiomeAffinity.Of(envelope, in site);
                    if (allowed != null)
                    {
                        Vector2 pixel = allowed.WorldToPixel(point.x, point.z);
                        score *= Mathf.Clamp01(allowed.GetInterpolated(pixel.x, pixel.y));
                    }
                    result.arr[z * result.rect.size.x + x] = !float.IsNaN(score) && !float.IsInfinity(score) ? Mathf.Clamp01(score) : 0;
                }
            }
            return result;
        }

        private void ValidateWaterSettings(EcoEnvelope envelope)
        {
            if (waterHabitat < VegetationWaterHabitat.Species || waterHabitat > VegetationWaterHabitat.DryLand ||
                ((waterHabitat == VegetationWaterHabitat.Submerged || waterHabitat == VegetationWaterHabitat.Emergent) &&
                    (!TerrainSystem.ScatterApplier.Finite(waterDepthRangeMetres.x) || !TerrainSystem.ScatterApplier.Finite(waterDepthRangeMetres.y) ||
                        waterDepthRangeMetres.x <= 0f || waterDepthRangeMetres.y < waterDepthRangeMetres.x)) ||
                (waterHabitat == VegetationWaterHabitat.WetBank && (!TerrainSystem.ScatterApplier.Finite(wetBankDistanceMetres) || wetBankDistanceMetres < 0f)) ||
                (waterHabitat == VegetationWaterHabitat.Emergent && (!TerrainSystem.ScatterApplier.Finite(envelope.matureHeightM.x) || envelope.matureHeightM.x <= 0f)) ||
                (requireAcceptedCurrent && (!TerrainSystem.ScatterApplier.Finite(currentSpeedRangeMetresPerSecond.x) ||
                    !TerrainSystem.ScatterApplier.Finite(currentSpeedRangeMetresPerSecond.y) || currentSpeedRangeMetresPerSecond.x < 0f ||
                    currentSpeedRangeMetresPerSecond.y < currentSpeedRangeMetresPerSecond.x)))
                throw new InvalidOperationException("Vegetation Habitat water mode needs finite ordered metre-depth/current bands and valid active bank/stem limits.");
        }

        internal static bool WaterHabitatAdmits(VegetationWaterHabitat mode, in EnvironmentSample site, Vector2 depth, float bankDistance, float stemHeight)
        {
            switch (mode)
            {
                case VegetationWaterHabitat.Species: return true;
                case VegetationWaterHabitat.Submerged: return site.hasWaterInRange && site.submergedDepthM >= depth.x && site.submergedDepthM <= depth.y;
                case VegetationWaterHabitat.Emergent: return site.hasWaterInRange && site.submergedDepthM >= depth.x && site.submergedDepthM <= depth.y && site.submergedDepthM < stemHeight;
                case VegetationWaterHabitat.WetBank: return site.hasWaterInRange && site.submergedDepthM == 0f && site.distanceToWaterM <= bankDistance;
                case VegetationWaterHabitat.DryLand: return site.submergedDepthM == 0f;
                default: return false;
            }
        }
    }
}
