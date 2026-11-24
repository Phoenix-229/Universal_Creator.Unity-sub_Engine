// -----------------------------------------------------------------------------
//  Universe Creator - Geometry, density and streaming
//
//  File:    AngularError.cs
//  Purpose: The honest replacement for an authored LOD curve. Terrain and prop
//           level-of-detail should not be picked off a hand-tuned ramp - a ramp
//           silently changes meaning with screen resolution and field of view, so
//           the same authored numbers give a different image on a phone and a
//           monitor. Detail is instead bounded by two real limits that a buyer can
//           check:
//
//             L-49  Visual acuity. The human eye resolves about one arcminute of
//                   arc. A feature that subtends less than that is beneath the
//                   resolving power of the eye and cannot be seen as detail, no
//                   matter how many triangles carry it. In small-angle terms the
//                   geometric error allowed at distance d is d * tan(1') ~= d/3438
//                   metres.
//
//             L-50  Nyquist. Detail finer than twice the source field's texel
//                   spacing is not present in the source; asking a renderer for it
//                   shows aliasing, not detail. (The clamp that consumes this lives
//                   alongside the acuity math because both answer the same question:
//                   how fine is it worth drawing here.)
//
//           This file is the pure, deterministic core: given a distance, a camera
//           vertical field of view and a render-target pixel height, it converts
//           freely between metres of world-space error and pixels of screen-space
//           error, and states the acuity distance for a feature of a given size.
//           No Unity objects, no per-frame state - the density controller composes
//           these with hysteresis and the terrain's own texel spacing.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Visual-acuity (L-49) and screen-projection maths for the geometry density
    /// systems. Pure and deterministic: the same inputs resolve to the same numbers
    /// on any machine, so LOD selection does not drift per device or per run.
    /// </summary>
    public static class AngularError
    {
        /// <summary>One arcminute in radians: (pi/180) / 60. The eye's resolving power (L-49).</summary>
        public const float RadiansPerArcminute = 2.908882086657216e-4f;

        /// <summary>
        /// 1 / tan(1 arcminute) ~= 3437.75. Multiplying a feature's size by this gives
        /// the distance at which it shrinks to one arcminute of arc - the range past
        /// which it is no longer resolvable. In the small-angle regime this equals the
        /// reciprocal of <see cref="RadiansPerArcminute"/>.
        /// </summary>
        public const float ArcminuteReciprocal = 3437.746770784939f;

        /// <summary>
        /// The default acuity budget in pixels of tolerated silhouette error. One pixel
        /// is the honest desktop default - "how many pixels of edge error is acceptable"
        /// - and unlike an authored curve it is resolution- and FOV-independent by
        /// construction. Per-tier caps raise it on smaller screens.
        /// </summary>
        public const float DefaultAcuityPixels = 1.0f;

        /// <summary>
        /// The world-space size (metres) that projects to exactly one vertical pixel at
        /// <paramref name="distanceM"/>, for a camera with vertical field of view
        /// <paramref name="verticalFovDeg"/> rendering to <paramref name="screenHeightPixels"/>
        /// pixels of height. The screen spans 2*d*tan(fov/2) metres at that distance, shared
        /// over the pixel rows. Returns 0 for a degenerate camera or a point on the lens.
        /// </summary>
        public static float MetresPerPixel(float distanceM, float verticalFovDeg, int screenHeightPixels)
        {
            if (screenHeightPixels <= 0 || distanceM <= 0f) return 0f;
            float fov = Mathf.Clamp(verticalFovDeg, 1e-3f, 179f);
            float halfSpanM = distanceM * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            return 2f * halfSpanM / screenHeightPixels;
        }

        /// <summary>
        /// The geometric error in metres that is allowed at <paramref name="distanceM"/>
        /// before it exceeds <paramref name="acuityPixels"/> pixels of screen-space error.
        /// This is the budget that replaces an authored curve: because it is stated in
        /// pixels and only converted to metres through the live camera and screen, the
        /// on-screen error it permits is the same on every display. Negative acuity is
        /// clamped to zero (a zero budget forbids all error).
        /// </summary>
        public static float AllowedErrorMetres(float distanceM, float verticalFovDeg,
                                               int screenHeightPixels, float acuityPixels)
        {
            return Mathf.Max(0f, acuityPixels) * MetresPerPixel(distanceM, verticalFovDeg, screenHeightPixels);
        }

        /// <summary>
        /// The inverse of <see cref="AllowedErrorMetres"/>: the screen-space error in
        /// pixels that a world-space error of <paramref name="worldErrorM"/> produces at
        /// <paramref name="distanceM"/>. This is the quantity held constant across
        /// resolutions - the same metres of silhouette error read as the same pixels of
        /// edge error only when the projection is computed from the live screen. Returns 0
        /// for a degenerate projection.
        /// </summary>
        public static float ScreenErrorPixels(float worldErrorM, float distanceM,
                                              float verticalFovDeg, int screenHeightPixels)
        {
            float mpp = MetresPerPixel(distanceM, verticalFovDeg, screenHeightPixels);
            if (mpp <= 0f) return 0f;
            return Mathf.Max(0f, worldErrorM) / mpp;
        }

        /// <summary>
        /// The angular size in radians that a feature of <paramref name="featureSizeM"/>
        /// metres subtends at <paramref name="distanceM"/>. Below
        /// <see cref="RadiansPerArcminute"/> the feature is finer than the eye can resolve.
        /// Uses the small-angle relation (arctangent), so it stays exact for the tiny
        /// angles that matter here. Returns pi at zero distance (a feature at the lens
        /// fills the view).
        /// </summary>
        public static float AngularSizeRadians(float featureSizeM, float distanceM)
        {
            if (distanceM <= 0f) return Mathf.PI;
            return Mathf.Atan2(Mathf.Max(0f, featureSizeM), distanceM);
        }

        /// <summary>
        /// The distance in metres beyond which a feature of <paramref name="featureSizeM"/>
        /// metres subtends less than one arcminute and so stops being resolvable - the
        /// screen-independent statement of the same law, useful for placing prop and
        /// detail draw distances (L-49). Equal to featureSizeM * <see cref="ArcminuteReciprocal"/>.
        /// </summary>
        public static float AcuityDistanceMetres(float featureSizeM)
        {
            return Mathf.Max(0f, featureSizeM) * ArcminuteReciprocal;
        }

        /// <summary>
        /// A reference render target for stating a draw distance as one authorable number.
        /// The terrain's tree, detail and basemap distances are world-space metres that
        /// Unity applies against the live camera itself, so they cannot be re-projected per
        /// frame the way <see cref="MetresPerPixel"/> is; they are resolved once, here,
        /// against a fixed 1080-row / 60-degree reference view. Change the reference and
        /// every derived distance moves together, which is the point - one honest anchor
        /// instead of four hand-tuned magic numbers.
        /// </summary>
        public const int ReferenceScreenHeightPixels = 1080;

        /// <summary>Vertical field of view of the reference view used to derive draw distances.</summary>
        public const float ReferenceVerticalFovDeg = 60f;

        /// <summary>
        /// The distance in metres at which a feature of <paramref name="featureSizeM"/>
        /// metres projects down to exactly <paramref name="acuityPixels"/> vertical pixels -
        /// the inverse of <see cref="MetresPerPixel"/>, solved for distance. Past this range
        /// the feature is smaller than the tolerated pixel budget, so drawing it at full
        /// fidelity buys nothing the eye can resolve; it is the honest cutoff for a tree,
        /// grass or basemap draw distance. Raising the shared <paramref name="acuityPixels"/>
        /// budget (coarser tolerance) pulls the cutoff in for every feature at once. Fails
        /// closed: a degenerate feature or view returns 0.
        /// </summary>
        public static float FeatureCullDistanceMetres(float featureSizeM, float acuityPixels,
                                                      float verticalFovDeg, int screenHeightPixels)
        {
            if (screenHeightPixels <= 0 || featureSizeM <= 0f) return 0f;
            float acuity = Mathf.Max(1e-3f, acuityPixels);
            float fov = Mathf.Clamp(verticalFovDeg, 1e-3f, 179f);
            // screenHeight / (2*tan(fov/2)) is the focal length in pixels; a feature of size
            // s then covers s * focalPixels / d pixels at distance d. Setting that equal to
            // the budget and solving for d gives the line below.
            float focalPixels = screenHeightPixels / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            return Mathf.Max(0f, featureSizeM * focalPixels / acuity);
        }

        /// <summary>
        /// <see cref="FeatureCullDistanceMetres"/> evaluated against the fixed reference view
        /// (<see cref="ReferenceScreenHeightPixels"/>, <see cref="ReferenceVerticalFovDeg"/>).
        /// This is the form the terrain settings use: a draw distance authored once, off the
        /// same acuity budget that drives heightmap density, so one dial moves the whole
        /// level-of-detail envelope coherently.
        /// </summary>
        public static float FeatureCullDistanceReference(float featureSizeM, float acuityPixels)
        {
            return FeatureCullDistanceMetres(featureSizeM, acuityPixels,
                                             ReferenceVerticalFovDeg, ReferenceScreenHeightPixels);
        }

        // ---------------------------------------------------------------------
        //  L-50  Nyquist limit of the source heightfield
        //
        //  A pixel error finer than the heightmap's own texel spacing buys nothing:
        //  Unity cannot draw detail the height field does not contain, and asking for
        //  it only rebuilds the patch LOD tree more often. The clamp below turns "how
        //  fine is the source" into a floor on the pixel error, so the acuity budget is
        //  never allowed to demand sub-texel geometry.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The world-space spacing in metres between adjacent heightmap samples across a
        /// terrain of <paramref name="terrainWidthMetres"/> whose height field is
        /// <paramref name="heightmapResolution"/> samples on a side. Divides by the SPAN
        /// (resolution - 1), never the sample count: dividing by the count is an
        /// off-by-one that surfaces as a seam at cell edges - the worst place to find it.
        /// Returns 0 for a degenerate field.
        /// </summary>
        public static float TexelMetres(float terrainWidthMetres, int heightmapResolution)
        {
            if (heightmapResolution <= 1 || terrainWidthMetres <= 0f) return 0f;
            return terrainWidthMetres / (heightmapResolution - 1);
        }

        /// <summary>
        /// The Nyquist floor on pixel error (L-50): the pixels that half a source texel
        /// projects to at <paramref name="metresPerPixel"/>. Half a texel is the finest
        /// feature the height field can honestly carry, so a pixel error below this is
        /// asking for detail that is not present - aliasing, not resolution. Returns 0
        /// for a degenerate projection, which leaves the acuity budget unclamped.
        /// </summary>
        public static float NyquistFloorPixels(float texelMetres, float metresPerPixel)
        {
            if (metresPerPixel <= 0f || texelMetres <= 0f) return 0f;
            return (texelMetres / metresPerPixel) * 0.5f;
        }

        /// <summary>
        /// The heightmap pixel error to apply to a terrain patch: the acuity budget in
        /// pixels (<paramref name="acuityPixels"/>), never driven below the Nyquist floor
        /// of the source field (L-50) nor below a hard <paramref name="pixelErrorFloor"/>.
        /// Because a lower pixel error asks Unity for MORE geometry, these are floors, not
        /// ceilings: they cap how much detail may be requested, so raising acuity past the
        /// Nyquist point produces no further geometry and no further LOD-tree rebuilds.
        /// All inputs fail closed - a degenerate camera or field yields the plain acuity
        /// budget clamped to the floor, never a negative or NaN error.
        /// </summary>
        public static float PixelErrorForTerrain(float acuityPixels, float texelMetres,
                                                 float distanceM, float verticalFovDeg,
                                                 int screenHeightPixels, float pixelErrorFloor)
        {
            float mpp = MetresPerPixel(distanceM, verticalFovDeg, screenHeightPixels);
            float nyquist = NyquistFloorPixels(texelMetres, mpp);
            float error = Mathf.Max(0f, acuityPixels);
            error = Mathf.Max(error, nyquist);
            error = Mathf.Max(error, Mathf.Max(0f, pixelErrorFloor));
            return error;
        }
    }
}
