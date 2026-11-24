// UC-T06-03: regional authoring uses the same world lattice and existing scatter mask consumers, without a second world/placement engine.
using System;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>Produces a bounded rectangular world-metre density mask for regional rules in one Vegetation Graph.</summary>
    /// <remarks>Wire to existing scatter Mask inputs or field combiners; accepted ground still comes from World Graph Input. No placement or native resource is owned by this mask.</remarks>
    [Serializable]
    public sealed class VegetationRegionMaskNode : Generator
    {
        /// <summary>Minimum absolute world X/Z, metres; both components must be finite.</summary>
        [Tooltip("Minimum absolute world X/Z in metres; independent of terrain chunk order.")]
        public Vector2 minimumWorldXZ;
        /// <summary>Rectangle width/depth, metres; both components must be finite and positive.</summary>
        [Tooltip("Regional width/depth in world metres; both must be positive.")]
        public Vector2 sizeMetres = new Vector2(100, 100);
        /// <summary>Inward smooth density transition, metres; finite and nonnegative, limited to half the smaller dimension.</summary>
        [Min(0), Tooltip("Inward smooth density fade in metres, limited to half the smaller region dimension. Zero gives a half-open hard boundary.")]
        public float fadeMetres = 5;
        public override string NodeName => "Vegetation Region Mask";
        public override NodeCategory Category => NodeCategory.Scatter;
        public override string Tooltip => "Regional density rules in absolute world metres; one mask agrees across adjoining streamed cells.";
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => SharedPorts.MaskOut;

        /// <summary>Samples the region's half-open bounds into the tile's world lattice with an optional inward fade.</summary>
        /// <param name="outputIndex">Mask outlet index; must be zero.</param>
        /// <param name="tile">Existing world tile providing matrix dimensions and world-coordinate mapping.</param>
        /// <param name="stop">Optional cancellation checked each row.</param>
        /// <returns>Coverage matrix in [0,1], or null when cancelled; no candidate generation or ground mutation.</returns>
        /// <exception cref="ArgumentException">Region bounds/fade are nonfinite or dimensions are nonpositive.</exception>
        /// <exception cref="ArgumentNullException">The tile is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The outlet is not zero.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (tile == null) throw new ArgumentNullException(nameof(tile));
            if (outputIndex != 0) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            Vector2 maximum = minimumWorldXZ + sizeMetres;
            if (!Finite(minimumWorldXZ.x) || !Finite(minimumWorldXZ.y) || !Finite(maximum.x) || !Finite(maximum.y) ||
                !Finite(sizeMetres.x) || !Finite(sizeMetres.y) || sizeMetres.x <= 0 || sizeMetres.y <= 0 ||
                maximum.x <= minimumWorldXZ.x || maximum.y <= minimumWorldXZ.y || !Finite(fadeMetres) || fadeMetres < 0)
                throw new ArgumentException("Vegetation region requires finite world bounds, positive metre dimensions and a nonnegative finite fade.");
            float fade = Mathf.Min(fadeMetres, 0.5f * Mathf.Min(sizeMetres.x, sizeMetres.y));
            var matrix = tile.CreateMatrix();
            for (int z = 0; z < matrix.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < matrix.rect.size.x; x++)
                {
                    Vector3 point = matrix.PixelToWorld(x + matrix.rect.offset.x, z + matrix.rect.offset.z);
                    float value = 0;
                    if (point.x >= minimumWorldXZ.x && point.z >= minimumWorldXZ.y && point.x < maximum.x && point.z < maximum.y)
                    {
                        float distance = Mathf.Min(Mathf.Min(point.x - minimumWorldXZ.x, maximum.x - point.x),
                            Mathf.Min(point.z - minimumWorldXZ.y, maximum.y - point.z)); // Two-argument overloads avoid a params-array allocation per sample.
                        float t = fade > 0 ? Mathf.Clamp01(distance / fade) : 1;
                        value = t * t * (3 - 2 * t);
                    }
                    matrix.arr[z * matrix.rect.size.x + x] = value;
                }
            }
            return matrix;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
