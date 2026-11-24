using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// A 3x3 patch of ground heights in world metres, plus the world distance
    /// between neighbouring samples.
    ///
    /// Both readers of the topography maths hand over a patch in this shape: the
    /// per-point sampler, which fills it with nine bilinear reads out of a
    /// heightmap, and the per-chunk derivative pass, which fills it by walking a
    /// grid it already holds. Sharing the shape is what keeps one slope
    /// convention in the project instead of two that disagree at the third
    /// decimal place.
    ///
    /// North is +z and east is +x, matching Unity's terrain axes.
    /// </summary>
    public struct HeightStencil
    {
        public float nw, n, ne;
        public float w, c, e;
        public float sw, s, se;

        /// <summary>m. Ground distance between two side-by-side samples.</summary>
        public float stepM;
    }

    /// <summary>
    /// The shape of the ground at a point: how steep it is, which way it faces,
    /// whether it bulges or dishes, and what those three imply about the depth
    /// of loose material sitting on it.
    ///
    /// Every function here is a pure function of its arguments, so it is safe to
    /// call from a worker thread and it returns the same answer on every machine.
    /// </summary>
    public static class TopographyMath
    {
        // Horn's 3x3 gradient divides by eight because the stencil's weights
        // (1,2,1 across, differenced) sum to eight on each axis.
        private const float HornWeightSum = 8f;

        // The curvature terms average two second derivatives, hence the halving.
        private const float CurvatureAxisCount = 2f;

        /// <summary>
        /// Steepness and facing direction, by Horn's 3x3 method - the same one
        /// GIS software uses for hillshade, chosen because it weights the four
        /// side neighbours twice as heavily as the corners and so is far less
        /// twitchy on noisy ground than a plain two-point difference.
        ///
        /// slopeDeg is 0 on the flat and approaches 90 on a cliff. aspectDeg is
        /// a compass bearing for the direction water would run off: 0 north, 90
        /// east, 180 south. Ground too flat to face anywhere reports
        /// <see cref="EcoConstants.AspectFlatSentinelDegrees"/> rather than a
        /// bearing invented out of rounding error, which is the GIS convention
        /// and matters because a flat plain must not read as a warm slope.
        /// </summary>
        public static void SlopeAspect(in HeightStencil h, out float slopeDeg, out float aspectDeg)
        {
            GradientOf(in h, out float dzdx, out float dzdz);

            float gradient = Mathf.Sqrt(dzdx * dzdx + dzdz * dzdz);
            slopeDeg = Mathf.Atan(gradient) * Mathf.Rad2Deg;

            if (gradient < EcoConstants.AspectFlatSlopeTangent)
            {
                aspectDeg = EcoConstants.AspectFlatSentinelDegrees;
                return;
            }

            // The gradient points uphill, so downhill is its negation. A compass
            // bearing measures clockwise from north, which is atan2 of the east
            // component over the north one - the arguments are in that order on
            // purpose, and swapping them would mirror every aspect in the world.
            float bearing = Mathf.Atan2(-dzdx, -dzdz) * Mathf.Rad2Deg;
            if (bearing < 0f) bearing += EcoConstants.DegreesPerCircle;
            aspectDeg = bearing;
        }

        /// <summary>
        /// Convexity, in 1/m, as the negated mean of the two second derivatives.
        ///
        /// Positive is a ridge, nose or shoulder - ground that sheds in every
        /// direction. Negative is a hollow, bench or valley floor, which
        /// collects. Zero is a plane, whether level or tilted.
        ///
        /// The sign is the reason this is negated: the Laplacian of a bowl is
        /// positive, and a bowl is the concave case, so reporting it unnegated
        /// would put forests on ridgetops and scree in the hollows.
        /// </summary>
        public static float Convexity(in HeightStencil h)
        {
            float step2 = h.stepM * h.stepM;
            if (step2 < Mathf.Epsilon) return 0f;

            float zxx = (h.e - 2f * h.c + h.w) / step2;
            float zzz = (h.n - 2f * h.c + h.s) / step2;

            return -(zxx + zzz) / CurvatureAxisCount;
        }

        /// <summary>
        /// Thickness of loose material over bedrock, in metres.
        ///
        /// Regolith is made by weathering at a roughly steady rate and removed
        /// by transport that speeds up with slope, so thickness falls away
        /// exponentially as the ground steepens - one
        /// <see cref="EcoConstants.SoilDepthSlopeDecayTangent"/> of gradient
        /// costs a factor of e. Curvature then moves it around: a convex nose
        /// sheds in every direction and runs thin, a hollow receives what the
        /// slopes above it shed and runs deep.
        ///
        /// maxDepthM is the world's flat-ground depth, authored per world rather
        /// than fixed here, because a young volcanic island and an old
        /// floodplain do not carry the same soil.
        /// </summary>
        public static float SoilDepthM(float slopeDeg, float convexityPerM, float maxDepthM)
        {
            float tanSlope = Mathf.Tan(Mathf.Clamp(slopeDeg, 0f, 89.9f) * Mathf.Deg2Rad);
            float slopeTerm = Mathf.Exp(-tanSlope / EcoConstants.SoilDepthSlopeDecayTangent);

            float curvNorm = Mathf.Clamp(convexityPerM / EcoConstants.SoilDepthCurvatureSpanPerMetre, -1f, 1f);
            float curvatureTerm = 1f - curvNorm * EcoConstants.SoilDepthConvexityLoss;

            float depth = maxDepthM * slopeTerm * curvatureTerm;
            return Mathf.Clamp(depth, EcoConstants.SoilDepthMinMetres, maxDepthM);
        }

        /// <summary>
        /// Fraction of the surface that is bare stone, 0 to 1.
        ///
        /// Two independent ways for rock to reach the surface, combined by
        /// whichever is worse rather than by a blend, because either one alone
        /// is enough to leave nothing to root in: the ground is steeper than
        /// loose material can rest on, or the regolith is simply too thin to
        /// bury the bedrock.
        ///
        /// reposeDeg is passed in rather than read from
        /// <see cref="EcoConstants.AngleOfReposeDegrees"/> so a world of rounded
        /// gravel and a world of angular scree can differ, which they do.
        /// </summary>
        public static float Rockiness(float slopeDeg, float soilDepthM, float reposeDeg)
        {
            float onsetDeg = reposeDeg - EcoConstants.RockinessOnsetBelowReposeDegrees;
            float band = Mathf.Max(EcoConstants.RockinessOnsetBelowReposeDegrees, Mathf.Epsilon);
            float fromSlope = Mathf.Clamp01((slopeDeg - onsetDeg) / band);

            float fromThinSoil = Mathf.Clamp01(1f - soilDepthM / EcoConstants.RockinessSoilCoverMetres);

            return Mathf.Max(fromSlope, fromThinSoil);
        }

        /// <summary>
        /// Warming a slope gets for facing the sun, in degrees Celsius, added to
        /// the site temperature before any thermal curve is evaluated.
        ///
        /// Equator-facing ground meets the sun nearer to square and so runs
        /// warmer, and which bearing that is depends on the hemisphere - south
        /// in the north, north in the south. warmAspectDeg carries that decision
        /// in, already signed off the world's latitude, so this function never
        /// has to assume a hemisphere.
        ///
        /// Ground with no aspect at all gets no bias: a level plain is neither
        /// sunny nor shaded, and the sentinel is not a bearing to take a cosine
        /// of.
        /// </summary>
        public static float AspectWarmingC(float aspectDeg, float warmAspectDeg, float amplitudeC)
        {
            if (aspectDeg < 0f) return 0f;

            float offsetRad = (aspectDeg - warmAspectDeg) * Mathf.Deg2Rad;
            return amplitudeC * Mathf.Cos(offsetRad);
        }

        /// <summary>
        /// The bearing that faces the equator, and so runs warm, for a latitude.
        ///
        /// The boundary case sits at the equator itself, where the sun passes
        /// overhead and neither side has an advantage worth modelling; it is
        /// grouped with the northern hemisphere so the function stays total.
        /// </summary>
        public static float WarmAspectDegFor(float latitudeDeg)
        {
            return latitudeDeg >= 0f
                ? EcoConstants.WarmAspectNorthDeg
                : EcoConstants.WarmAspectSouthDeg;
        }

        private static void GradientOf(in HeightStencil h, out float dzdx, out float dzdz)
        {
            float denom = HornWeightSum * h.stepM;
            if (denom < Mathf.Epsilon)
            {
                dzdx = 0f;
                dzdz = 0f;
                return;
            }

            dzdx = ((h.ne + 2f * h.e + h.se) - (h.nw + 2f * h.w + h.sw)) / denom;
            dzdz = ((h.nw + 2f * h.n + h.ne) - (h.sw + 2f * h.s + h.se)) / denom;
        }
    }
}
