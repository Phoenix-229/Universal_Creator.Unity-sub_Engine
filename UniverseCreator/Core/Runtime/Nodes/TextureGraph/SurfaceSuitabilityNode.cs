// UC-T07-11: physical surface membership uses the captured world; unavailable observations never become invented soil or weather.
using System;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Authored surface habitat constraint; bed categories refer to actual accepted wave-free water geometry.</summary>
    public enum SurfaceHabitat
    {
        /// <summary>Dry accepted ground, excluding water coverage.</summary>
        Land,
        /// <summary>Ground under an accepted river.</summary>
        RiverBed,
        /// <summary>Ground under an accepted lake.</summary>
        LakeBed,
        /// <summary>Ground under an accepted ocean.</summary>
        OceanFloor,
        /// <summary>An explicitly supplied signed shore-distance field; negative inside, positive inland, in metres.</summary>
        Shore
    }

    /// <summary>Produces continuous material membership from accepted elevation/slope/water and explicitly connected biome, substrate or weather observations.</summary>
    [Serializable]
    public sealed class SurfaceSuitabilityNode : TextureNode
    {
        private static readonly Port[] Inputs =
        {
            new Port("Biome Membership (0..1)", PortType.Matrix, PortDirection.Input),
            new Port("Substrate Membership (0..1)", PortType.Matrix, PortDirection.Input),
            new Port("Wetness (0..1)", PortType.Matrix, PortDirection.Input),
            new Port("Snow Coverage (0..1)", PortType.Matrix, PortDirection.Input),
            new Port("Signed Shore Distance (metres)", PortType.Matrix, PortDirection.Input)
        };
        private static readonly Port[] Outputs = { new Port("Material Membership (0..1)", PortType.Matrix, PortDirection.Output) };
        /// <summary>Worker-safe: samples immutable accepted fields and admitted matrices without native resource access.</summary>
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override string NodeName => "Surface Suitability";
        public override NodeCategory Category => NodeCategory.Surface;
        public override string Tooltip => "Continuous physical material membership from accepted ground and water. Required missing observations report their source.";
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => Outputs;

        /// <summary>Authored dry, bed or explicit shore constraint; categories are never inferred from colour or depth.</summary>
        [Tooltip("Actual accepted river/lake/ocean bed, dry ground, or an explicit signed shore-distance input. Soil composition is never inferred.")]
        public SurfaceHabitat habitat;
        /// <summary>Inclusive centre of the lower/upper altitude transitions, in world metres.</summary>
        [Tooltip("Suitable elevation range in absolute world metres; use independent profiles for plains, hills, mountains and snow peaks.")]
        public Vector2 elevationMetres = new Vector2(-10000, 10000);
        /// <summary>Positive half-width of each elevation transition, in metres.</summary>
        [Tooltip("Half-width in world metres over which each elevation boundary blends continuously.")]
        public float elevationBandMetres = 25;
        /// <summary>Suitable slope range in degrees, between zero and ninety; physical endpoints admit flat and vertical ground without an impossible outside-domain fade.</summary>
        [Tooltip("Accepted final-ground slope range in degrees. Rock and grass use separate authored ranges.")]
        public Vector2 slopeDegrees = new Vector2(0, 90);
        /// <summary>Positive half-width of each slope transition, in degrees.</summary>
        [Tooltip("Half-width of the continuous slope transition, in degrees.")]
        public float slopeBandDegrees = 5;
        /// <summary>Suitable bed-depth range in metres below accepted wave-free water.</summary>
        [Tooltip("Accepted water surface minus final bed height, in metres. River, lake and ocean profiles may use different ranges.")]
        public Vector2 bedDepthMetres = new Vector2(0, 10000);
        /// <summary>Positive half-width of each bed-depth transition, in metres.</summary>
        [Tooltip("Half-width of bed-depth transitions in metres.")]
        public float bedDepthBandMetres = 1;
        /// <summary>Positive width of the land/bed coverage transition inside the actual shore, in world metres, at most 256.</summary>
        [Tooltip("Blend land and each accepted bed continuously inside the physical bank/coast over this many metres. Missing shore metadata requires rebuilding the water body.")]
        public float waterTransitionMetres = 1;
        /// <summary>Signed shore-distance range in metres; requires a real connected spatial distance field.</summary>
        [Tooltip("Negative metres inside water, positive inland. This requires a supplied shore field; volume bounds and sea-level bands are not shore-distance observations.")]
        public Vector2 shoreDistanceMetres = new Vector2(-2, 3);
        /// <summary>Positive half-width of each shore transition, in world metres.</summary>
        [Tooltip("Half-width of each shore membership transition in metres; retain narrow banks with a suitably resolved source field.")]
        public float shoreBandMetres = 0.5f;
        /// <summary>Requires independent accepted biome membership rather than guessing a biome from the chunk.</summary>
        public bool requireBiome;
        /// <summary>Requires a connected substrate/geology membership observation; absence is an error.</summary>
        public bool requireSubstrate;
        /// <summary>Requires connected wetness coverage; absence is an error, not assumed rain.</summary>
        public bool requireWetness;
        /// <summary>Requires connected snow coverage; absence is an error, not assumed climate.</summary>
        public bool requireSnow;

        /// <summary>Builds an admitted continuous membership matrix on the tile's physical lattice.</summary>
        /// <param name="outputIndex">Sole membership output, zero.</param>
        /// <param name="tile">Bound companion tile with current accepted final ground and captured water.</param>
        /// <param name="stop">Optional cooperative cancellation token, checked each row.</param>
        /// <returns>Nonnegative coverage 0…1; null when cancelled. No terrain generation, material edits or native allocation occurs.</returns>
        /// <exception cref="ArgumentException">A range, band, membership or source coordinate is invalid.</exception>
        /// <exception cref="InvalidOperationException">Accepted ground/water or an explicitly required observation is unavailable.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (outputIndex != 0 || !Enum.IsDefined(typeof(SurfaceHabitat), habitat)) throw new ArgumentException("Unsupported surface habitat/output.");
            var ground = tile?.worldGraphGround;
            if (ground == null || !ground.Run.Equals(tile.RunId)) throw new InvalidOperationException("Pending: build accepted final ground for this bound Texture Graph.");
            var water = ground.Water ?? throw new InvalidOperationException("Pending: capture accepted water for this surface revision, including an explicitly dry world.");
            ValidateRange(elevationMetres, elevationBandMetres); ValidateRange(slopeDegrees, slopeBandDegrees);
            ValidateRange(bedDepthMetres, bedDepthBandMetres); ValidateRange(shoreDistanceMetres, shoreBandMetres);
            TextureImageSampling.RequireFinite(waterTransitionMetres); // UC-T07-11: physical water coverage must not retain a binary bank cutoff.
            if (waterTransitionMetres <= 0 || waterTransitionMetres > 256) throw new ArgumentException("Water transitions require a width in metres greater than zero and at most 256.");
            if (slopeDegrees.x < 0 || slopeDegrees.y > 90 || bedDepthMetres.x < 0) throw new ArgumentException("Slope requires 0…90 degrees and depth requires nonnegative metres.");
            Matrix[] fields = new Matrix[5];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = GetInputMap(tile, i, stop);
                bool required = i == 0 ? requireBiome : i == 1 ? requireSubstrate : i == 2 ? requireWetness : i == 3 ? requireSnow : habitat == SurfaceHabitat.Shore;
                if (required && fields[i] == null) throw new InvalidOperationException("Pending: connect " + Inputs[i].name + " from an accepted published field; this observation cannot be inferred.");
                if (fields[i] != null) SurfaceCoverageSampling.Validate(fields[i], tile.worldPos, tile.worldSize, i < 4);
            }
            Matrix result = tile.CreateMatrix();
            for (int z = 0; z < result.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < result.rect.size.x; x++)
                {
                    Vector3 p = result.PixelToWorld(x + result.rect.offset.x, z + result.rect.offset.z);
                    if (!ground.TrySample(p.x, p.z, out float height)) throw new InvalidOperationException("Pending: accepted final ground does not cover the requested surface position.");
                    float slope = Mathf.Acos(Mathf.Clamp(ground.Snapshot.GroundNormal(p.x, p.z).y, -1, 1)) * Mathf.Rad2Deg;
                    float weight = Band(height, elevationMetres, elevationBandMetres) * Band(slope, slopeDegrees, slopeBandDegrees, 0, 90);
                    bool wet = water.TryStillObservation(p.x, p.z, out float level, out _, out WaterBodyKind kind);
                    if (habitat == SurfaceHabitat.Land)
                        weight *= wet ? 1 - BedCoverage(water.SignedShoreDistance(p.x, p.z, waterTransitionMetres, kind)) : 1;
                    else if (habitat == SurfaceHabitat.Shore) weight *= Band(SurfaceCoverageSampling.Sample(fields[4], p.x, p.z), shoreDistanceMetres, shoreBandMetres);
                    else
                    {
                        WaterBodyKind wanted = habitat == SurfaceHabitat.RiverBed ? WaterBodyKind.River : habitat == SurfaceHabitat.LakeBed ? WaterBodyKind.Lake : WaterBodyKind.Ocean;
                        weight *= wet && kind == wanted ? BedCoverage(water.SignedShoreDistance(p.x, p.z, waterTransitionMetres, wanted)) *
                            Band(Mathf.Max(0, level - height), bedDepthMetres, bedDepthBandMetres) : 0;
                    }
                    for (int i = 0; i < 4; i++) if (fields[i] != null) weight *= SurfaceCoverageSampling.Sample(fields[i], p.x, p.z);
                    result.arr[z * result.rect.size.x + x] = Mathf.Clamp01(weight);
                }
            }
            return result;
        }

        private float BedCoverage(float signedDistance)
        {
            // UC-T07-11: zero at the physical shore, full inside the authored band; dry samples never need invented bed-depth observations.
            float t = Mathf.Clamp01(-signedDistance / waterTransitionMetres);
            return t * t * (3 - 2 * t);
        }

        private static void ValidateRange(Vector2 range, float band)
        {
            TextureImageSampling.RequireFinite(range.x); TextureImageSampling.RequireFinite(range.y); TextureImageSampling.RequireFinite(band);
            if (range.x > range.y || band <= 0) throw new ArgumentException("Surface ranges must be ordered with positive finite transition bands.");
        }

        private static float Band(float value, Vector2 range, float halfWidth, float physicalMinimum = float.NegativeInfinity, float physicalMaximum = float.PositiveInfinity)
        {
            // UC-T07-11: a default full physical range must admit its endpoints fully; blending towards negative slope would halve every flat bed/land sample.
            float lower = range.x <= physicalMinimum ? 1 : Mathf.Clamp01((float)(((double)value - range.x + halfWidth) / (2d * halfWidth)));
            float upper = range.y >= physicalMaximum ? 1 : Mathf.Clamp01((float)(((double)range.y + halfWidth - value) / (2d * halfWidth)));
            return lower * lower * (3 - 2 * lower) * upper * upper * (3 - 2 * upper);
        }
    }
}
