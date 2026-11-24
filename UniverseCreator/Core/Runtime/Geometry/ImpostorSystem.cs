// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ImpostorSystem.cs
//  Purpose: The DERIVED impostor switch, replacing the retired "screen-fraction 0.012"
//           magic number with a threshold that reads off atlas resolution and screen
//           resolution and so never needs re-tuning per device. The rule is a texel-vs-
//           pixel Nyquist match: a baked billboard is indistinguishable from geometry
//           exactly while its atlas tile carries at least as many texels as the screen
//           gives the object pixels. Switch to the impostor once the object's projected
//           pixel diameter has fallen to that texel count (loosened by impostorSharpness):
//
//               projectedPixelDiameter <= atlasTileResolution / impostorSharpness
//
//  THE SWITCH DISTANCE IS INVERSELY PROPORTIONAL TO ATLAS RESOLUTION
//  -----------------------------------------------------------------
//  Solving the criterion for the range at which the impostor becomes acceptable gives
//
//               dSwitch = boundsRadius * screenHeight * sharpness
//                         / (atlasTileResolution * tan(fov/2))
//
//  so a COARSER atlas (fewer texels) is only acceptable FARTHER away, and a finer atlas
//  can stand in from nearer. This is the honest reading of the same texel-vs-pixel law
//  the density core (AngularError) already uses: quality follows resolution, and the one
//  threshold tracks both the atlas and the display instead of a hand-liked fraction.
//  (Note: this makes dSwitch scale as 1 / atlasTileResolution - halving the tile doubles
//  the switch range, because a coarser billboard must retreat farther to hide its texels.
//  A finer atlas buys a NEARER switch, freeing the near geometry sooner.)
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Every method is a static function of numbers - no camera, no atlas object, no
//  allocation - so a self-test pins the done-when (the switch distance moves by exactly
//  the atlas-resolution ratio, and by nothing else) without entering play mode. The
//  runtime component that reads a real Camera, a real atlas and drives the LOD->impostor
//  hand-off is the thin scene layer this feeds; the arithmetic lives here.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure derivation of the LOD-to-impostor switch from a texel-vs-pixel Nyquist match: an impostor is
    /// acceptable once its atlas tile carries at least as many texels as the screen gives the object pixels.
    /// Exposes the boxed criterion and the switch DISTANCE that criterion implies, both device-free.
    /// </summary>
    public static class ImpostorSystem
    {
        /// <summary>Default atlas-sharpness multiplier: switch exactly at the texel=pixel Nyquist match.</summary>
        public const float DefaultImpostorSharpness = 1f;

        /// <summary>
        /// The boxed criterion: true when the object's projected pixel diameter has fallen to or below the
        /// atlas tile's texel count (loosened by <paramref name="impostorSharpness"/>), i.e. the impostor is now
        /// over-sampled and reads as geometry. Fails closed to false (keep geometry) on degenerate inputs.
        /// </summary>
        public static bool ShouldUseImpostor(float projectedPixelDiameterPx, int atlasTileResolution, float impostorSharpness = DefaultImpostorSharpness)
        {
            if (atlasTileResolution <= 0 || impostorSharpness <= 0f || projectedPixelDiameterPx <= 0f)
                return false;
            return projectedPixelDiameterPx <= atlasTileResolution / impostorSharpness;
        }

        /// <summary>
        /// The range at and beyond which the impostor is acceptable, obtained by solving the criterion for
        /// distance. Reuses the same projection as <see cref="MeshLODController.ProjectedPixelRadiusPx"/> (the
        /// projected DIAMETER is twice the radius), so the impostor hand-off and the LOD selection ride one
        /// model. Inversely proportional to <paramref name="atlasTileResolution"/>: a coarser atlas retreats
        /// farther. Fails closed to 0 on a degenerate camera, atlas or sharpness.
        /// </summary>
        public static float ImpostorSwitchDistanceM(
            float boundsRadiusM, int atlasTileResolution, int screenHeightPx, float fovDeg,
            float impostorSharpness = DefaultImpostorSharpness)
        {
            if (boundsRadiusM <= 0f || atlasTileResolution <= 0 || screenHeightPx <= 0
                || fovDeg <= 0f || fovDeg >= 180f || impostorSharpness <= 0f)
                return 0f;
            float halfTan = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            if (halfTan <= 0f) return 0f;
            // dSwitch = boundsRadius * screenHeight * sharpness / (atlasTileResolution * tan(fov/2)).
            // (= diameter-projection C / threshold T, with C = boundsRadius*screenHeight/tan(fov/2),
            //  T = atlasTileResolution / sharpness.)
            return boundsRadiusM * screenHeightPx * impostorSharpness / (atlasTileResolution * halfTan);
        }

        // ----- Octahedral view parameterisation: hemi for grounded, full for what tumbles -----
        //
        // An impostor atlas stores one baked view per cell of a square grid; the mapping from a view
        // DIRECTION to that grid is octahedral, because it is seamless and area-even. A grounded prop -
        // a tree - is never seen from below, so its views should spread over the UPPER HEMISPHERE only:
        // the hemi-octahedron packs 2π sr into the same square the full octahedron gives 4π sr, so for
        // an equal atlas footprint every axis carries twice the angular resolution. A loose rock a
        // physics pass can leave at any orientation needs the whole sphere, so it pays the full
        // octahedron. Both are exact round-trip encodings with no seam at the fold - the property the
        // self-test pins - because the fold lines map continuously across the diamond edges.
        //
        // Up axis is Unity's +Y. Encodings return UV in [0,1]²; decodings return a unit direction.

        private static float SignNonZero(float v) { return v >= 0f ? 1f : -1f; }

        /// <summary>
        /// Full-octahedron encode: a unit direction over the WHOLE sphere to atlas UV in [0,1]². The lower
        /// hemisphere folds out across the diamond edges, so the mapping is continuous (no seam) at y = 0.
        /// For loose props that a physics pass can leave at any orientation.
        /// </summary>
        public static Vector2 EncodeOctahedronFull(Vector3 dir)
        {
            float l1 = Mathf.Abs(dir.x) + Mathf.Abs(dir.y) + Mathf.Abs(dir.z);
            if (l1 <= 1e-8f) return new Vector2(0.5f, 0.5f);
            float px = dir.x / l1, py = dir.y / l1, pz = dir.z / l1;
            float u = px, v = pz;
            if (py < 0f)
            {
                u = (1f - Mathf.Abs(pz)) * SignNonZero(px);
                v = (1f - Mathf.Abs(px)) * SignNonZero(pz);
            }
            return new Vector2(u * 0.5f + 0.5f, v * 0.5f + 0.5f);
        }

        /// <summary>Inverse of <see cref="EncodeOctahedronFull"/>: atlas UV back to a unit direction over the sphere.</summary>
        public static Vector3 DecodeOctahedronFull(Vector2 uv)
        {
            float fx = uv.x * 2f - 1f, fz = uv.y * 2f - 1f;
            float x = fx, z = fz;
            float y = 1f - Mathf.Abs(fx) - Mathf.Abs(fz);
            if (y < 0f)
            {
                x = (1f - Mathf.Abs(fz)) * SignNonZero(fx);
                z = (1f - Mathf.Abs(fx)) * SignNonZero(fz);
            }
            return new Vector3(x, y, z).normalized;
        }

        /// <summary>
        /// Hemi-octahedron encode: a unit direction over the UPPER hemisphere (y ≥ 0) to atlas UV in [0,1]²,
        /// using the (x+z, x−z) diamond-to-square rotation so the whole square is used. Same footprint as the
        /// full octahedron, twice the angular density per axis. For grounded props never seen from below.
        /// </summary>
        public static Vector2 EncodeHemiOctahedron(Vector3 dir)
        {
            if (dir.y < 0f) dir = new Vector3(dir.x, 0f, dir.z); // clamp the horizon rather than fold under
            float l1 = Mathf.Abs(dir.x) + Mathf.Abs(dir.y) + Mathf.Abs(dir.z);
            if (l1 <= 1e-8f) return new Vector2(0.5f, 0.5f);
            float px = dir.x / l1, pz = dir.z / l1;
            float u = px + pz, v = px - pz;
            return new Vector2(u * 0.5f + 0.5f, v * 0.5f + 0.5f);
        }

        /// <summary>Inverse of <see cref="EncodeHemiOctahedron"/>: atlas UV back to a unit direction on the upper hemisphere.</summary>
        public static Vector3 DecodeHemiOctahedron(Vector2 uv)
        {
            float fu = uv.x * 2f - 1f, fv = uv.y * 2f - 1f;
            float x = (fu + fv) * 0.5f;
            float z = (fu - fv) * 0.5f;
            float y = 1f - Mathf.Abs(x) - Mathf.Abs(z);
            if (y < 0f) y = 0f;
            return new Vector3(x, y, z).normalized;
        }

        /// <summary>Solid angle a parameterisation covers: 2π sr for the hemi-octahedron, 4π sr for the full one.</summary>
        public static float CoveredSolidAngleSr(bool hemi)
        {
            return hemi ? 2f * Mathf.PI : 4f * Mathf.PI;
        }

        /// <summary>
        /// Angular density (views per steradian) the hemi-octahedron buys over the full one for an EQUAL atlas
        /// footprint: the same view count spread over half the solid angle, so exactly 2. This is the done-when
        /// ratio between a grounded tree's atlas and a tumbling boulder's.
        /// </summary>
        public static float HemiAngularDensityAdvantage()
        {
            return CoveredSolidAngleSr(false) / CoveredSolidAngleSr(true);
        }

        // ----- The depth channel: without it an impostor is a flat card that floats and clips wrongly -----
        //
        // A billboard is a plane through the object's centre; every fragment sits at the SAME camera depth,
        // so a trunk base pokes in front of a rim it should sink behind, and the card intersects the ground
        // along a straight line. Baking a depth channel alongside albedo fixes this: each texel stores where
        // its surface sits along the view ray relative to the billboard plane, normalised over the object's
        // half-depth. The shader decodes that to a metric offset, displaces the fragment's view depth by it,
        // and the ordinary z-test then occludes the impostor by terrain EXACTLY as it occludes the geometry -
        // because both write the same view depth at that fragment. Encoding centres the plane at 0.5 so the
        // front half is [0,0.5) and the back half (0.5,1]; the maths here is the CPU reference the shader mirrors.

        /// <summary>Encodes a metric view-depth offset (metres from the billboard plane, + into the screen) to a
        /// baked [0,1] depth value, centring the plane at 0.5 over ±<paramref name="halfDepthM"/>. Clamped to [0,1].</summary>
        public static float EncodeImpostorDepth01(float offsetM, float halfDepthM)
        {
            if (halfDepthM <= 1e-6f) return 0.5f;
            return Mathf.Clamp01(offsetM / (2f * halfDepthM) + 0.5f);
        }

        /// <summary>Inverse of <see cref="EncodeImpostorDepth01"/>: a baked depth value back to a metric offset from
        /// the billboard plane. 0.5 → 0 (on the plane), 0 → −halfDepth (nearest), 1 → +halfDepth (farthest).</summary>
        public static float DecodeImpostorDepthOffsetM(float depth01, float halfDepthM)
        {
            return (depth01 - 0.5f) * 2f * halfDepthM;
        }

        /// <summary>The fragment's true camera view depth: the billboard plane's view depth plus the decoded
        /// per-texel offset. This is the value the shader writes so the depth buffer sees the real surface.</summary>
        public static float FragmentViewDepthM(float billboardPlaneViewDepthM, float depthOffsetM)
        {
            return billboardPlaneViewDepthM + depthOffsetM;
        }

        /// <summary>The ordinary depth test: nearer terrain occludes the impostor fragment. True when the terrain
        /// surface is closer to the camera than the fragment's reconstructed view depth (the rim wins).</summary>
        public static bool IsOccludedByTerrain(float fragmentViewDepthM, float terrainViewDepthM)
        {
            return terrainViewDepthM < fragmentViewDepthM;
        }
    }

    /// <summary>
    /// Season-aware impostors with NO extra atlas memory (§ 5 group I). One atlas bakes IDENTITY, not
    /// appearance: alongside albedo and depth each texel carries a leaf/wood classification and a stable
    /// per-texel hash. At runtime the shader repigments leaf texels toward § 7's current seasonal colour and
    /// clips the leaf texels whose hash exceeds the leaf-off threshold, so one summer atlas yields four seasons
    /// and a canopy that thins continuously - never a green card in January, never a fourfold memory bill.
    /// These are the pure CPU functions the impostor shader mirrors per texel.
    /// </summary>
    public static class ImpostorSeason
    {
        // PLACEHOLDER_SEASON

        /// <summary>
        /// Whether a leaf texel is clipped during leaf-off. A leaf texel drops out once its stable hash exceeds
        /// 1 − leafOffFraction, so raising the fraction only ADDS clipped texels (a texel once gone stays gone) -
        /// which is what makes the thinning flicker-free. Wood texels never clip. The fraction is clamped [0,1].
        /// </summary>
        public static bool IsLeafOffClipped(bool isLeaf, float texelHash01, float leafOffFraction)
        {
            if (!isLeaf) return false;
            float f = Mathf.Clamp01(leafOffFraction);
            return texelHash01 > 1f - f;
        }

        /// <summary>The fraction of a canopy's leaf texels retained at a given leaf-off fraction: 1 − clamp01(fraction).
        /// Continuous and monotone, so advancing the calendar thins the canopy smoothly rather than in a step.</summary>
        public static float CanopyKeepFraction(float leafOffFraction)
        {
            return 1f - Mathf.Clamp01(leafOffFraction);
        }

        /// <summary>
        /// Repigments a leaf texel toward the season's colour WITHOUT flattening its texture: it keeps the base
        /// texel's value (its light/shade detail) and moves only hue and saturation toward the seasonal colour by
        /// <paramref name="tintStrength"/> - an HSV repigment, not an RGB multiply, so autumn recolours the canopy
        /// instead of darkening it. Wood texels pass through unchanged. Continuous in both the strength and the
        /// seasonal colour, so a calendar sweep produces no switch frame.
        /// </summary>
        public static Color RepigmentLeaf(Color baseAlbedo, Color seasonColor, float tintStrength, bool isLeaf)
        {
            if (!isLeaf) return baseAlbedo;
            float t = Mathf.Clamp01(tintStrength);
            Color.RGBToHSV(baseAlbedo, out float bh, out float bs, out float bv);
            Color.RGBToHSV(seasonColor, out float sh, out float ss, out _);
            float h = LerpHue(bh, sh, t);
            float s = Mathf.Lerp(bs, ss, t);
            Color outc = Color.HSVToRGB(h, s, bv); // base VALUE preserved: recolour, do not re-shade
            outc.a = baseAlbedo.a;
            return outc;
        }

        /// <summary>Shortest-path interpolation on the hue circle (hue wraps at 1), so red→magenta never detours
        /// through the whole spectrum. Result wrapped to [0,1).</summary>
        private static float LerpHue(float a, float b, float t)
        {
            float d = b - a;
            if (d > 0.5f) d -= 1f;
            else if (d < -0.5f) d += 1f;
            float h = a + d * t;
            h -= Mathf.Floor(h);
            return h;
        }
    }
}
