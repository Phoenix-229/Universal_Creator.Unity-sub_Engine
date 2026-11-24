// UC-T03-08: resolve scale/absolute criteria before selecting actual eligible basins.
using System;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Optional world-area scaling for lake count and area. Defaults preserve the existing
    /// chunk-coverage count and absolute area controls; no setting creates an otherwise ineligible basin.</summary>
    [Serializable]
    public sealed class LakeDistributionSettings
    {
        /// <summary>Scale automatic counts by world area relative to referenceWorldAreaM2; false uses chunk coverage.</summary>
        public bool scaleToWorld;
        /// <summary>Reference world area in square metres, finite and positive; default is the 2 km square scenario.</summary>
        public float referenceWorldAreaM2 = 4000000f;
        /// <summary>Automatic count on the reference world, 0..4096; positive authored counts override this.</summary>
        public int referenceLakeCount = 4;
        /// <summary>Keep authored minimum/maximum lake areas in square metres; false scales them with world area when scaleToWorld is true.</summary>
        public bool absoluteAreas = true;
        /// <summary>Largest eligible body area in square metres, finite and nonnegative; zero leaves the upper area unrestricted.</summary>
        public float maximumAreaSquareMetres;

        /// <summary>Resolves bounded count and area criteria without modifying authored settings.</summary>
        /// <param name="worldAreaM2">Finite positive captured world XZ area in square metres.</param>
        /// <param name="totalChunks">Nonnegative legacy whole-world chunk count.</param>
        /// <param name="coverage">Legacy chunk-coverage fraction, finite in 0..1.</param>
        /// <param name="authoredCount">Explicit count in 0..4096; zero selects automatic count.</param>
        /// <param name="minimumAreaM2">Finite nonnegative authored minimum body area in square metres.</param>
        /// <returns>Resolved criteria; automatic counts are capped at 4096 with CountLimited reported.</returns>
        /// <exception cref="ArgumentException">Invalid controls, geometry or inverted area limits.</exception>
        public ResolvedLakeDistribution Resolve(double worldAreaM2, int totalChunks, float coverage, int authoredCount, float minimumAreaM2)
        {
            if (double.IsNaN(worldAreaM2) || double.IsInfinity(worldAreaM2) || worldAreaM2 <= 0 ||
                !Finite(referenceWorldAreaM2) || referenceWorldAreaM2 <= 0 || referenceLakeCount < 0 || referenceLakeCount > 4096 ||
                totalChunks < 0 || !Finite(coverage) || coverage < 0 || coverage > 1 || authoredCount < 0 || authoredCount > 4096 ||
                !Finite(minimumAreaM2) || minimumAreaM2 < 0 || !Finite(maximumAreaSquareMetres) || maximumAreaSquareMetres < 0)
                throw new ArgumentException("Lake distribution requires finite valid counts, area and coverage.");
            double ratio = worldAreaM2 / referenceWorldAreaM2;
            double requested = authoredCount > 0 ? authoredCount : scaleToWorld ? referenceLakeCount * ratio : totalChunks * (double)coverage;
            requested = Math.Round(requested, MidpointRounding.ToEven);
            if (authoredCount == 0 && requested == 0 && (scaleToWorld ? referenceLakeCount > 0 : coverage > 0 && totalChunks > 0)) requested = 1;
            int target = (int)Math.Min(4096, requested);
            double areaScale = scaleToWorld && !absoluteAreas ? ratio : 1;
            double minimum = minimumAreaM2 * areaScale;
            double maximum = maximumAreaSquareMetres > 0 ? maximumAreaSquareMetres * areaScale : double.PositiveInfinity;
            if (double.IsNaN(minimum) || double.IsInfinity(minimum) || minimum > float.MaxValue ||
                double.IsNaN(maximum) || (maximumAreaSquareMetres > 0 && (double.IsInfinity(maximum) || maximum > float.MaxValue)) || maximum < minimum)
                throw new ArgumentException("Resolved lake area limits are inverted or outside the supported range.");
            return new ResolvedLakeDistribution(target, (float)minimum, (float)maximum, requested > 4096);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>UC-T03-08: resolved immutable lake criteria; areas are square metres and infinity means no upper limit.</summary>
    public readonly struct ResolvedLakeDistribution
    {
        /// <summary>Requested eligible-body count, bounded to 0..4096.</summary>
        public readonly int TargetCount;
        /// <summary>Smallest eligible connected body in square metres.</summary>
        public readonly float MinimumAreaM2;
        /// <summary>Largest eligible connected body in square metres, or positive infinity.</summary>
        public readonly float MaximumAreaM2;
        /// <summary>True when automatic density exceeded the bounded 4096-body target.</summary>
        public readonly bool CountLimited;
        internal ResolvedLakeDistribution(int target, float minimum, float maximum, bool limited)
        { TargetCount = target; MinimumAreaM2 = minimum; MaximumAreaM2 = maximum; CountLimited = limited; }
    }

    /// <summary>UC-T03-08: immutable selection evidence for eligible lakes; a shortfall never floods additional terrain.</summary>
    public sealed class LakeDistributionReport
    {
        /// <summary>Resolved requested count; -1 means the caller requested all eligible basins.</summary>
        public int RequestedCount { get; internal set; }
        /// <summary>Connected bodies satisfying the authored depth and resolved area limits.</summary>
        public int EligibleCount { get; internal set; }
        /// <summary>Eligible bodies retained after the optional count cap.</summary>
        public int SelectedCount { get; internal set; }
        /// <summary>Total selected lake area in square metres.</summary>
        public double SelectedAreaM2 { get; internal set; }
        /// <summary>Smallest selected body in square metres, or zero when none are selected.</summary>
        public float SmallestAreaM2 { get; internal set; }
        /// <summary>Largest selected body in square metres, or zero when none are selected.</summary>
        public float LargestAreaM2 { get; internal set; }
    }
}
