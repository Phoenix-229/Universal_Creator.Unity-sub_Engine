// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.Geometry.cs
//  Purpose: Asserts that the geometry density core is the honest, screen-driven
//           budget it claims to be, not an authored ramp in disguise. The one
//           property that separates the two is that the applied error must MOVE
//           when the render target or the lens moves, while the on-screen error it
//           permits stays put. These checks pin exactly that behaviour on the pure
//           maths (AngularError), so a regression that quietly re-introduces a
//           resolution-blind ramp fails the build rather than the buyer's eye.
//
//  Partial of UCSelfTest: shares the checks counter and the Check helper declared
//  in UCSelfTest.cs, so its assertions fold into the single tally.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>
        /// The visual-acuity budget (L-49) and the screen projection that feeds it.
        ///
        /// The claim under test is the one that makes an angular-error budget honest
        /// where an authored curve is not: the metres of geometric error it tolerates
        /// track the live camera and render target, so doubling the pixel height halves
        /// the tolerated world error at a fixed distance, and widening the field of view
        /// loosens it. The reciprocal reading - screen-space error in pixels - is what is
        /// actually held constant, and the arcminute constants are checked against the
        /// closed-form small-angle values so the "d/3438" law cannot silently drift.
        /// </summary>
        private static void TestAngularError()
        {
            const float fov = 60f;
            const float dist = 100f;

            // The arcminute constants are each other's reciprocal in the small-angle
            // regime; if either is fat-fingered the whole acuity law shifts.
            Check(Mathf.Abs(AngularError.RadiansPerArcminute - (Mathf.PI / 180f) / 60f) < 1e-9f,
                "AngularError.RadiansPerArcminute must equal one arcminute in radians.");
            Check(Mathf.Abs(AngularError.ArcminuteReciprocal * AngularError.RadiansPerArcminute - 1f) < 1e-4f,
                "AngularError.ArcminuteReciprocal must be 1 / RadiansPerArcminute in the small-angle regime.");

            // Metres-per-pixel is the projection every other quantity rides on. At 100 m
            // through a 60-deg lens the screen spans 2*100*tan(30deg) ~= 115.47 m; over
            // 1080 rows that is ~0.10692 m per pixel.
            float expectedMpp = 2f * dist * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / 1080f;
            float mpp = AngularError.MetresPerPixel(dist, fov, 1080);
            Check(Mathf.Abs(mpp - expectedMpp) < 1e-5f,
                "MetresPerPixel must equal 2*d*tan(fov/2)/height.");

            // THE honest-budget property: taller render target => finer tolerated world
            // error at the same distance and lens. A ramp cannot do this; it is blind to
            // the render target. Half the pixels, twice the tolerated error.
            float errAt1080 = AngularError.AllowedErrorMetres(dist, fov, 1080, 1f);
            float errAt2160 = AngularError.AllowedErrorMetres(dist, fov, 2160, 1f);
            float errAt540 = AngularError.AllowedErrorMetres(dist, fov, 540, 1f);
            Check(errAt2160 < errAt1080 && errAt540 > errAt1080,
                "Allowed error must shrink as screen height grows (resolution-driven, not authored).");
            Check(Mathf.Abs(errAt2160 * 2f - errAt1080) < 1e-4f,
                "Doubling screen height must halve the tolerated world error.");

            // Widening the lens spreads the same rows over more world, so more metres of
            // error fit under one pixel: the budget loosens with field of view.
            float errWide = AngularError.AllowedErrorMetres(dist, 90f, 1080, 1f);
            Check(errWide > errAt1080,
                "A wider field of view must loosen the tolerated world error.");

            // The reciprocal is the quantity actually held constant across displays: a
            // world error equal to the one-pixel budget reads back as exactly one pixel.
            float oneBudget = AngularError.AllowedErrorMetres(dist, fov, 1080, 1f);
            float backToPixels = AngularError.ScreenErrorPixels(oneBudget, dist, fov, 1080);
            Check(Mathf.Abs(backToPixels - 1f) < 1e-4f,
                "ScreenErrorPixels must invert AllowedErrorMetres (round-trips to the acuity pixels).");

            // Acuity distance is the screen-independent statement of the same law: a 1 m
            // feature drops below one arcminute at ~3437.75 m, and it must scale linearly.
            float d1 = AngularError.AcuityDistanceMetres(1f);
            float d2 = AngularError.AcuityDistanceMetres(2f);
            Check(Mathf.Abs(d1 - AngularError.ArcminuteReciprocal) < 1e-2f,
                "AcuityDistanceMetres(1 m) must equal the arcminute reciprocal (~3437.75 m).");
            Check(Mathf.Abs(d2 - 2f * d1) < 1e-2f,
                "Acuity distance must scale linearly with feature size.");

            // A feature exactly at its acuity distance subtends one arcminute; nearer, more.
            float ang = AngularError.AngularSizeRadians(1f, d1);
            Check(Mathf.Abs(ang - AngularError.RadiansPerArcminute) < 1e-5f,
                "A feature at its acuity distance must subtend one arcminute.");
            Check(AngularError.AngularSizeRadians(1f, d1 * 0.5f) > AngularError.RadiansPerArcminute,
                "A feature nearer than its acuity distance must subtend more than one arcminute.");

            // L-50 Nyquist floor. Texel spacing is the SPAN divided by (resolution - 1),
            // never the sample count - the off-by-one that surfaces as an edge seam. A
            // 1000 m terrain at 513 samples spans 512 cells, so a texel is 1000/512 m.
            float texel = AngularError.TexelMetres(1000f, 513);
            Check(Mathf.Abs(texel - 1000f / 512f) < 1e-5f,
                "TexelMetres must divide by (resolution - 1), the span, not the sample count.");
            Check(texel != 1000f / 513f,
                "TexelMetres divided by the sample count would be the off-by-one that seams at edges.");

            // Below the Nyquist floor the pixel error is pinned to the floor, so pushing
            // the acuity budget finer than the source field can carry buys no further
            // geometry and no further LOD-tree rebuild - the whole point of the clamp.
            float floorPinnedA = AngularError.PixelErrorForTerrain(0.1f, texel, dist, fov, 1080, 0f);
            float floorPinnedB = AngularError.PixelErrorForTerrain(0.5f, texel, dist, fov, 1080, 0f);
            float nyquistPx = AngularError.NyquistFloorPixels(texel, AngularError.MetresPerPixel(dist, fov, 1080));
            Check(floorPinnedA > 0.5f && Mathf.Abs(floorPinnedA - nyquistPx) < 1e-4f,
                "A sub-Nyquist acuity budget must clamp up to the Nyquist floor.");
            Check(Mathf.Abs(floorPinnedA - floorPinnedB) < 1e-4f,
                "Two acuity values both below the Nyquist floor must produce the same pixel error (no further geometry change).");

            // Above the Nyquist floor the acuity budget is honoured verbatim - the floor
            // is a lower bound on error (an upper bound on detail), never a ceiling.
            float aboveFloor = AngularError.PixelErrorForTerrain(nyquistPx + 20f, texel, dist, fov, 1080, 0f);
            Check(Mathf.Abs(aboveFloor - (nyquistPx + 20f)) < 1e-3f,
                "An acuity budget above the Nyquist floor must pass through unchanged.");

            // The hard pixel-error floor wins when it is the largest of the three.
            float hardFloor = AngularError.PixelErrorForTerrain(0.1f, 0f, dist, fov, 1080, 50f);
            Check(Mathf.Abs(hardFloor - 50f) < 1e-4f,
                "PixelErrorForTerrain must never fall below the hard pixelErrorFloor.");

            // Degenerate cameras must fail closed to zero, never to NaN or a negative
            // budget that would invert the LOD selection downstream.
            Check(AngularError.MetresPerPixel(dist, fov, 0) == 0f,
                "Zero screen height must yield zero metres-per-pixel, not NaN.");
            Check(AngularError.AllowedErrorMetres(-1f, fov, 1080, 1f) == 0f,
                "A point behind the lens must yield a zero budget.");
            Check(AngularError.AllowedErrorMetres(dist, fov, 1080, -5f) == 0f,
                "A negative acuity budget must clamp to zero (forbid all error), not go negative.");
        }

        /// <summary>
        /// Screen-space-error LOD selection over a prefab's authored LODGroup (L-51).
        ///
        /// The claim under test is G-30's done-when, the one property a distance list cannot
        /// have: the same prefab at the same ON-SCREEN size selects the same LOD at 5 m and at
        /// 50 m under different fields of view. So a near-wide and a far-narrow camera arranged
        /// to put the prop at one relative height must land on one LOD. The rest pins the
        /// mechanics that make that true: the relative height is resolution-independent (a pixel
        /// radius is not) yet a genuine pixel radius is still available for the impostor test; the
        /// authored thresholds are read as descending ratios and never rewritten; below the
        /// coarsest level the selector hands off to impostor/cull; lodBias is the only device dial.
        /// </summary>
        private static void TestMeshLodSelection()
        {
            // A boulder's authored chain: LOD0 down to 0.5 of viewport, LOD1 to 0.25, LOD2 to 0.1,
            // impostor/cull below. Descending, exactly as Unity stores screenRelativeTransitionHeight.
            float[] thresholds = { 0.5f, 0.25f, 0.1f };
            const float radius = 1.5f;

            // THE done-when. Near through a wide lens, far through a narrow one, arranged so the
            // on-screen size is identical: d1*tan(fov1/2) == d2*tan(fov2/2). Same size => same LOD,
            // and the relative heights must match to the float, because the LOD choice rides them.
            const float d1 = 5f, fov1 = 90f;
            const float d2 = 50f;
            float fov2 = 2f * Mathf.Atan(d1 * Mathf.Tan(fov1 * 0.5f * Mathf.Deg2Rad) / d2) * Mathf.Rad2Deg;
            float hNear = MeshLODController.RelativeHeight(radius, d1, fov1);
            float hFar = MeshLODController.RelativeHeight(radius, d2, fov2);
            Check(Mathf.Abs(hNear - hFar) < 1e-4f,
                "The same on-screen size must give the same relative height at 5 m/wide and 50 m/narrow.");
            Check(MeshLODController.SelectLod(radius, d1, fov1, thresholds)
                    == MeshLODController.SelectLod(radius, d2, fov2, thresholds),
                "G-30 done-when: the same prefab at the same on-screen size selects the same LOD at 5 m and at 50 m under different fields of view.");

            // The relative height is resolution-INDEPENDENT: doubling the render target must not
            // change it (a distance list has no such property, but neither would a pixel radius).
            float pxA = MeshLODController.ProjectedPixelRadiusPx(radius, d1, 1080, fov1);
            float pxB = MeshLODController.ProjectedPixelRadiusPx(radius, d1, 2160, fov1);
            Check(Mathf.Abs(pxB - 2f * pxA) < 1e-3f,
                "The projected pixel radius must scale with the render target (it is the honest pixel count).");
            Check(Mathf.Abs(MeshLODController.RelativeHeight(radius, d1, fov1)
                            - MeshLODController.RelativeHeight(radius, d1, fov1)) < 1e-6f
                  && pxA != pxB,
                "The LOD choice must ride the resolution-independent relative height, not the pixel radius.");

            // Boundary selections walk the whole authored chain, in order. A height above the
            // finest threshold is LOD0; between two thresholds is the coarser of the pair; below
            // the last is the count (impostor/cull), never clamped back into the chain.
            Check(MeshLODController.SelectLod(0.9f, thresholds) == 0,
                "A prop larger than LOD0's threshold must select LOD0.");
            Check(MeshLODController.SelectLod(0.3f, thresholds) == 1,
                "Between LOD0 and LOD1 thresholds must select LOD1.");
            Check(MeshLODController.SelectLod(0.15f, thresholds) == 2,
                "Between LOD1 and LOD2 thresholds must select LOD2.");
            Check(MeshLODController.SelectLod(0.05f, thresholds) == 3,
                "Below the coarsest authored level must return the count (impostor/cull territory), not the last LOD.");
            Check(MeshLODController.SelectLod(0.5f, thresholds) == 0
                  && MeshLODController.SelectLod(0.25f, thresholds) == 1,
                "A height exactly on a threshold must belong to the finer level (>= is inclusive at the top).");

            // Farther is never a finer LOD: monotone non-decreasing index as the prop recedes.
            int prev = -1;
            for (float dist = 2f; dist <= 200f; dist += 6f)
            {
                int lod = MeshLODController.SelectLod(radius, dist, fov1, thresholds);
                Check(lod >= prev, "LOD index must never decrease as the prop moves farther away.");
                prev = lod;
            }

            // lodBias is the one device dial: a bias > 1 reads the prop as larger and holds a finer
            // LOD to a greater distance; the authored thresholds are untouched. At a distance the
            // default just tips to LOD1, doubling the bias must recover LOD0.
            float tipDist = radius / (0.5f * Mathf.Tan(fov1 * 0.5f * Mathf.Deg2Rad)) + 0.5f; // just past the LOD0/1 edge (threshold 0.5)
            Check(MeshLODController.SelectLod(radius, tipDist, fov1, thresholds, 1f) >= 1,
                "At the tip distance the default bias must have dropped below LOD0.");
            Check(MeshLODController.SelectLod(radius, tipDist, fov1, thresholds, 2f) == 0,
                "Doubling lodBias must read the prop as larger and recover the finer LOD (device dial, authored ratios intact).");

            // Degenerate inputs fail closed, never NaN or a negative index. An empty or null chain
            // is a single-mesh prefab: always LOD0.
            Check(MeshLODController.RelativeHeight(radius, -1f, fov1) == 0f,
                "A point behind the lens must yield a zero relative height, not NaN.");
            Check(MeshLODController.ProjectedPixelRadiusPx(radius, d1, 0, fov1) == 0f,
                "A zero-height render target must yield a zero pixel radius, not NaN.");
            Check(MeshLODController.SelectLod(0.9f, System.Array.Empty<float>()) == 0
                  && MeshLODController.SelectLod(0.9f, null) == 0,
                "A prefab with no authored LOD chain must always select its single mesh (LOD0).");
        }

        /// <summary>
        /// The DERIVED impostor switch distance (L-49, L-50), replacing the magic 0.012.
        ///
        /// The claim under test is G-31's intent: the switch is a texel-vs-pixel Nyquist match,
        /// so one dial — atlas tile resolution — moves the switch distance and nothing else does.
        /// At the returned distance the projected pixel DIAMETER must equal the atlas texel count
        /// (the criterion round-trips), and the distance must scale by exactly the atlas-resolution
        /// ratio and the screen-height ratio, proving it reads both instead of a hand-liked fraction.
        ///
        /// NOTE ON DIRECTION: the task's done-when reads "halving the atlas tile resolution halves
        /// the switch distance," but solving the boxed criterion gives dSwitch = boundsRadius ·
        /// screenHeight · sharpness / (atlasTileResolution · tan(fov/2)) — INVERSELY proportional to
        /// atlas resolution. A coarser billboard must retreat FARTHER to hide its texels, so halving
        /// the tile DOUBLES the switch distance. That is the honest physics of the same law, and it
        /// is what these checks pin; the done-when sentence has the proportionality backwards.
        /// </summary>
        private static void TestImpostorSwitchDistance()
        {
            const float radius = 1.5f;
            const int screenH = 1080;
            const float fov = 60f;

            // The criterion round-trips: at the switch distance the projected pixel diameter equals
            // the atlas texel count (sharpness 1). This is what "indistinguishable" means numerically.
            float d256 = ImpostorSystem.ImpostorSwitchDistanceM(radius, 256, screenH, fov);
            float diamAtSwitch = 2f * MeshLODController.ProjectedPixelRadiusPx(radius, d256, screenH, fov);
            Check(Mathf.Abs(diamAtSwitch - 256f) < 1e-2f,
                "At the switch distance the projected pixel diameter must equal the atlas tile resolution (texel=pixel Nyquist match).");

            // The one-dial property, with the true (inverse) direction: halving the atlas resolution
            // DOUBLES the switch distance; doubling it halves. Nothing else was edited.
            float d128 = ImpostorSystem.ImpostorSwitchDistanceM(radius, 128, screenH, fov);
            float d512 = ImpostorSystem.ImpostorSwitchDistanceM(radius, 512, screenH, fov);
            Check(Mathf.Abs(d128 - 2f * d256) < 1e-2f,
                "Halving the atlas tile resolution must DOUBLE the switch distance (a coarser billboard hides its texels only farther out).");
            Check(Mathf.Abs(d512 * 2f - d256) < 1e-2f,
                "Doubling the atlas tile resolution must halve the switch distance (a finer atlas is acceptable nearer).");

            // It reads the SCREEN resolution too: twice the pixels means the object resolves as a
            // billboard sooner, so the switch retreats — linearly with screen height.
            float d256_2160 = ImpostorSystem.ImpostorSwitchDistanceM(radius, 256, 2160, fov);
            Check(Mathf.Abs(d256_2160 - 2f * d256) < 1e-2f,
                "Doubling screen height must double the switch distance (the threshold tracks the display, not a fixed fraction).");

            // The boxed boolean agrees with the distance: just past the switch the impostor is used,
            // just inside it geometry is kept. (Farther => smaller diameter => impostor.)
            float diamJustFar = 2f * MeshLODController.ProjectedPixelRadiusPx(radius, d256 * 1.01f, screenH, fov);
            float diamJustNear = 2f * MeshLODController.ProjectedPixelRadiusPx(radius, d256 * 0.99f, screenH, fov);
            Check(ImpostorSystem.ShouldUseImpostor(diamJustFar, 256)
                  && !ImpostorSystem.ShouldUseImpostor(diamJustNear, 256),
                "ShouldUseImpostor must flip exactly across the derived switch distance (impostor beyond, geometry within).");

            // Sharpness > 1 demands a TIGHTER texel-to-pixel match, so the billboard is accepted only
            // once the object is smaller on screen (its texels out-resolve the pixels): the switch
            // retreats FARTHER, keeping geometry longer. The default 1.0 is the exact Nyquist match.
            float dSharp = ImpostorSystem.ImpostorSwitchDistanceM(radius, 256, screenH, fov, 2f);
            Check(Mathf.Abs(dSharp - 2f * d256) < 1e-2f,
                "Raising impostorSharpness must push the switch farther by that factor (a tighter texel match keeps geometry longer).");

            // Degenerate inputs fail closed: keep geometry, zero distance, never NaN.
            Check(ImpostorSystem.ImpostorSwitchDistanceM(radius, 0, screenH, fov) == 0f,
                "A zero-resolution atlas must yield a zero switch distance, not a divide-by-zero.");
            Check(!ImpostorSystem.ShouldUseImpostor(10f, 0)
                  && !ImpostorSystem.ShouldUseImpostor(0f, 256),
                "A degenerate atlas or a zero on-screen size must fail closed to keeping geometry.");
        }

        /// <summary>
        /// Weber contrast extends the silhouette budget where the eye catches it (L-51).
        ///
        /// The claim under test is G-32's done-when: a ridgeline of conifers against sky holds
        /// real geometry further out than the same conifers against a forested hillside. Contrast,
        /// not size, is what makes a silhouette noticeable, so a dark conifer on bright sky earns a
        /// larger effective size and holds a finer LOD at a distance where the low-contrast copy has
        /// already dropped a level. The checks pin the Weber fraction itself, the monotone gain it
        /// earns, the reciprocal error framing the task states, and the ordering that IS "further out"
        /// expressed at a fixed distance (a strictly finer LOD near-side of the same switch).
        /// </summary>
        private static void TestWeberContrast()
        {
            // A dark conifer (low albedo → low luminance) against a bright sky versus a mid-luminance
            // forested hillside. Same tree, two backgrounds; the sky gives far the larger contrast.
            const float conifer = 0.06f;
            const float sky = 1.0f;
            const float hillside = 0.12f;

            float cSky = MeshLODController.WeberContrast(conifer, sky);
            float cHill = MeshLODController.WeberContrast(conifer, hillside);
            Check(cSky > cHill,
                "A dark conifer against bright sky must have higher Weber contrast than against a forested hillside.");
            Check(Mathf.Abs(cSky - 0.94f) < 1e-4f && Mathf.Abs(cHill - 0.5f) < 1e-4f,
                "WeberContrast must equal |L_obj − L_bg| / L_bg.");

            // The gain is monotone in contrast and never below 1 (contrast only ever HOLDS geometry,
            // it never drops it below the size-only budget).
            const float sens = 0.5f;
            float gSky = MeshLODController.ContrastSizeGain(cSky, sens);
            float gHill = MeshLODController.ContrastSizeGain(cHill, sens);
            Check(gSky > gHill && gHill > 1f,
                "Higher contrast must earn a larger size gain, and any positive contrast holds more than size alone.");
            Check(MeshLODController.ContrastSizeGain(0f, sens) == 1f,
                "Zero contrast must leave selection untouched (gain exactly 1).");

            // THE done-when, expressed at a fixed distance: contrast enters as lodBias, so the
            // high-contrast conifer selects a strictly FINER LOD than the low-contrast one at the same
            // range — which is the same statement as "holds real geometry further out."
            float[] thresholds = { 0.5f, 0.25f, 0.1f };
            const float radius = 1.5f, fov = 60f, dist = 14.4f;
            int lodSky = MeshLODController.SelectLod(radius, dist, fov, thresholds, gSky);
            int lodHill = MeshLODController.SelectLod(radius, dist, fov, thresholds, gHill);
            Check(lodSky < lodHill,
                "G-32 done-when: at equal distance the against-sky conifer must hold a finer LOD than the against-hillside conifer (geometry held further out).");

            // The reciprocal framing never drifts from the gain: error scale is exactly 1/gain, and it
            // moves the inverse way — high contrast TIGHTENS the tolerated error.
            Check(Mathf.Abs(MeshLODController.ContrastErrorScale(cSky, sens) * gSky - 1f) < 1e-5f,
                "ContrastErrorScale must be exactly 1 / ContrastSizeGain (the two framings cannot drift).");
            Check(MeshLODController.ContrastErrorScale(cSky, sens) < MeshLODController.ContrastErrorScale(cHill, sens),
                "Higher contrast must scale the tolerated error DOWN further (inverse of the size gain).");

            // Guards: a non-positive background is zero contrast, not a divide-by-zero or a negative
            // fraction that would invert the gain.
            Check(MeshLODController.WeberContrast(conifer, 0f) == 0f
                  && MeshLODController.ContrastSizeGain(-1f, sens) == 1f,
                "A degenerate background or negative contrast must fail closed to no gain, never invert the budget.");
        }

        /// <summary>
        /// Octahedral impostor view parameterisation: hemi for grounded, full for what tumbles.
        ///
        /// The claim under test is G-33's done-when: a tree impostor and a boulder impostor of equal
        /// atlas footprint show the tree at TWICE the angular density, and neither shows a seam at the
        /// octahedral fold. The density factor is the ratio of the covered solid angles (4π/2π = 2);
        /// "no seam" is the round-trip fidelity of both encodings plus continuity of the full mapping
        /// straight across the y = 0 fold, where a naive octahedron tears.
        /// </summary>
        private static void TestOctahedralViews()
        {
            // Twice the angular density for equal footprint: the whole done-when number.
            Check(Mathf.Abs(ImpostorSystem.HemiAngularDensityAdvantage() - 2f) < 1e-5f,
                "A grounded (hemi) atlas must carry twice the angular density of a full-sphere atlas of equal footprint.");
            Check(Mathf.Abs(ImpostorSystem.CoveredSolidAngleSr(true) - 2f * Mathf.PI) < 1e-4f
                  && Mathf.Abs(ImpostorSystem.CoveredSolidAngleSr(false) - 4f * Mathf.PI) < 1e-4f,
                "Hemi must cover 2π sr and full 4π sr.");

            // Round-trip fidelity over the UPPER hemisphere for the hemi encoding: every baked view
            // direction is recovered from its atlas cell (no seam means the fold lines round-trip too).
            float worstHemi = 1f;
            for (int a = 0; a < 16; a++)
            for (int e = 0; e <= 8; e++)
            {
                float az = a / 16f * 2f * Mathf.PI;
                float el = e / 8f * (Mathf.PI * 0.5f); // 0 = horizon, PI/2 = straight up
                var dir = new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az)).normalized;
                var uv = ImpostorSystem.EncodeHemiOctahedron(dir);
                Check(uv.x >= -1e-4f && uv.x <= 1f + 1e-4f && uv.y >= -1e-4f && uv.y <= 1f + 1e-4f,
                    "Hemi encode must stay within the [0,1] atlas square.");
                var back = ImpostorSystem.DecodeHemiOctahedron(uv);
                worstHemi = Mathf.Min(worstHemi, Vector3.Dot(dir, back));
            }
            Check(worstHemi > 0.999f,
                "The hemi-octahedron must round-trip every upper-hemisphere view direction (no fold seam).");

            // Round-trip fidelity over the WHOLE sphere for the full encoding, lower hemisphere included.
            float worstFull = 1f;
            for (int a = 0; a < 16; a++)
            for (int e = -8; e <= 8; e++)
            {
                float az = a / 16f * 2f * Mathf.PI;
                float el = e / 8f * (Mathf.PI * 0.5f);
                var dir = new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az)).normalized;
                var back = ImpostorSystem.DecodeOctahedronFull(ImpostorSystem.EncodeOctahedronFull(dir));
                worstFull = Mathf.Min(worstFull, Vector3.Dot(dir, back));
            }
            Check(worstFull > 0.999f,
                "The full octahedron must round-trip every sphere direction (upper and lower hemispheres).");

            // The seam that a naive octahedron tears: the fold at y = 0. Two directions straddling the
            // equator must encode to nearly the same UV — a continuous mapping, not a jump across the fold.
            var above = new Vector3(0.6f, 0.02f, 0.8f).normalized;
            var below = new Vector3(0.6f, -0.02f, 0.8f).normalized;
            Vector2 uvA = ImpostorSystem.EncodeOctahedronFull(above);
            Vector2 uvB = ImpostorSystem.EncodeOctahedronFull(below);
            Check((uvA - uvB).magnitude < 0.05f,
                "The full octahedron must be continuous across the y=0 fold (no seam where a naive mapping tears).");

            // The two anchor views land where geometry says they must: straight up at the atlas centre,
            // a horizon direction at an edge — so the baked grid is oriented, not arbitrary.
            Vector2 up = ImpostorSystem.EncodeHemiOctahedron(Vector3.up);
            Check((up - new Vector2(0.5f, 0.5f)).magnitude < 1e-4f,
                "Straight-up must map to the centre of the hemi atlas.");
            Vector2 horizon = ImpostorSystem.EncodeHemiOctahedron(new Vector3(1f, 0f, 0f));
            Check(Mathf.Abs(horizon.x - 1f) < 1e-4f || Mathf.Abs(horizon.y - 1f) < 1e-4f
                  || Mathf.Abs(horizon.x) < 1e-4f || Mathf.Abs(horizon.y) < 1e-4f,
                "A horizon direction must map to an edge of the hemi atlas (the hemisphere fills the square).");
        }

        /// <summary>
        /// The impostor depth channel: an impostor in a dip is occluded by the dip's rim exactly as
        /// the geometry version is.
        ///
        /// The claim under test is G-34's done-when. A flat billboard writes one camera depth for
        /// every fragment, so a trunk base pokes in front of a rim it should sink behind. Baking a
        /// depth channel and displacing the fragment's view depth by it makes the ordinary z-test
        /// occlude the impostor identically to the mesh. These checks pin the encode/decode round-trip,
        /// its monotonicity, and the decisive comparison: WITH depth the impostor's occlusion matches
        /// the geometry's; WITHOUT it (a flat card) it does not — which is the bug the channel removes.
        /// </summary>
        private static void TestImpostorDepth()
        {
            const float halfDepth = 3f;      // the tree's half-extent along the bake view
            const float planeDepth = 40f;    // camera→billboard-plane distance

            // Encode/decode round-trips across the full ±halfDepth range, plane centred at 0.5.
            Check(Mathf.Abs(ImpostorSystem.DecodeImpostorDepthOffsetM(0.5f, halfDepth)) < 1e-5f,
                "A baked depth of 0.5 must decode to zero offset (the billboard plane).");
            float worst = 0f;
            for (float off = -halfDepth; off <= halfDepth; off += halfDepth / 8f)
            {
                float d01 = ImpostorSystem.EncodeImpostorDepth01(off, halfDepth);
                Check(d01 >= -1e-4f && d01 <= 1f + 1e-4f, "Encoded impostor depth must stay in [0,1].");
                worst = Mathf.Max(worst, Mathf.Abs(ImpostorSystem.DecodeImpostorDepthOffsetM(d01, halfDepth) - off));
            }
            Check(worst < 1e-4f, "The depth channel must round-trip a metric offset within its ±halfDepth range.");

            // Monotone: a larger baked depth is a farther fragment (the front half sinks toward camera,
            // the back half away), so depth ordering is preserved into the z-buffer.
            float near = ImpostorSystem.FragmentViewDepthM(planeDepth, ImpostorSystem.DecodeImpostorDepthOffsetM(0.2f, halfDepth));
            float far = ImpostorSystem.FragmentViewDepthM(planeDepth, ImpostorSystem.DecodeImpostorDepthOffsetM(0.8f, halfDepth));
            Check(near < planeDepth && far > planeDepth && far > near,
                "A larger baked depth must place the fragment farther from the camera (front half nearer, back half farther).");

            // THE done-when. A trunk-base fragment sits 2 m behind the plane (42 m); the dip's rim is
            // terrain at 41 m, in front of it. The geometry version is occluded by the rim.
            float baseOffset = 2f;
            float geomFragDepth = ImpostorSystem.FragmentViewDepthM(planeDepth, baseOffset); // 42 m, the mesh
            const float rimDepth = 41f;
            bool geomOccluded = ImpostorSystem.IsOccludedByTerrain(geomFragDepth, rimDepth);
            Check(geomOccluded, "Sanity: the geometry trunk base (42 m) must be occluded by the nearer rim (41 m).");

            // Impostor WITH depth reconstructs 42 m from the baked channel → occluded, matching geometry.
            float baked = ImpostorSystem.EncodeImpostorDepth01(baseOffset, halfDepth);
            float impFragDepth = ImpostorSystem.FragmentViewDepthM(planeDepth, ImpostorSystem.DecodeImpostorDepthOffsetM(baked, halfDepth));
            bool impWithDepthOccluded = ImpostorSystem.IsOccludedByTerrain(impFragDepth, rimDepth);
            Check(impWithDepthOccluded == geomOccluded,
                "G-34 done-when: the depth-channel impostor must be occluded by the dip's rim exactly as the geometry version is.");

            // Impostor WITHOUT depth is a flat card at the plane (40 m) → the rim at 41 m is now BEHIND
            // it, so it wrongly pokes through. This is the bug the channel removes; pin it so a regression
            // that drops the depth write fails here.
            bool flatCardOccluded = ImpostorSystem.IsOccludedByTerrain(planeDepth, rimDepth);
            Check(flatCardOccluded != geomOccluded,
                "A flat card (no depth channel) must occlude WRONGLY versus geometry — proving the depth write is what fixes it.");

            // A degenerate zero-depth object cannot divide by zero; it collapses to the plane.
            Check(ImpostorSystem.EncodeImpostorDepth01(5f, 0f) == 0.5f,
                "A zero half-depth must encode to the plane (0.5), never divide by zero.");
        }

        /// <summary>
        /// Season-aware impostors with no extra atlas memory (§ 5 group I).
        ///
        /// The claim under test is G-35's done-when: advancing the calendar through a year changes
        /// impostor COLOUR and canopy DENSITY continuously, with no switch frame. Colour rides an HSV
        /// repigment of leaf texels toward the seasonal colour (keeping the base value, so it recolours
        /// rather than darkens); density rides a stable per-texel hash clipped against the leaf-off
        /// fraction, so the canopy thins monotonically and a texel once dropped stays dropped — which is
        /// exactly what "no visible switch frame" means at the texel level. Wood is immune to both.
        /// </summary>
        private static void TestImpostorSeason()
        {
            var baseLeaf = new Color(0f, 0.6f, 0f);          // a dark summer green (value 0.6)
            var autumn = new Color(0.85f, 0.35f, 0.05f);     // a seasonal orange

            // At zero tint the leaf is untouched; wood is untouched at any tint.
            var t0 = ImpostorSeason.RepigmentLeaf(baseLeaf, autumn, 0f, true);
            Check((new Vector3(t0.r - baseLeaf.r, t0.g - baseLeaf.g, t0.b - baseLeaf.b)).magnitude < 1e-3f,
                "Zero tint strength must leave the leaf albedo unchanged.");
            var wood = ImpostorSeason.RepigmentLeaf(baseLeaf, autumn, 1f, false);
            Check(wood == baseLeaf,
                "Wood texels must never be repigmented, at any tint strength.");

            // Repigment is an HSV recolour, not a multiply: the base VALUE survives while hue/sat move to
            // the season. So the output value stays 0.6 even at full tint (it recolours, does not darken).
            var t1 = ImpostorSeason.RepigmentLeaf(baseLeaf, autumn, 1f, true);
            Color.RGBToHSV(t1, out _, out _, out float vFull);
            Check(Mathf.Abs(vFull - 0.6f) < 1e-2f,
                "Full-season repigment must preserve the base texel value (recolour, not re-shade).");

            // Colour changes CONTINUOUSLY and in the right direction: green→orange adds red, monotonically
            // across the calendar sweep, with no jump — the "no switch frame" property for colour.
            float prevR = -1f;
            for (float t = 0f; t <= 1.0001f; t += 0.1f)
            {
                var c = ImpostorSeason.RepigmentLeaf(baseLeaf, autumn, Mathf.Min(t, 1f), true);
                Check(c.r >= prevR - 1e-4f, "Repigment red channel must rise monotonically toward the season (continuous colour shift).");
                prevR = c.r;
            }
            Check(ImpostorSeason.RepigmentLeaf(baseLeaf, autumn, 0f, true).r < 0.05f
                  && ImpostorSeason.RepigmentLeaf(baseLeaf, autumn, 1f, true).r > 0.5f,
                "The repigment must span from the summer green to the seasonal orange across the year.");

            // Density: a stable hash set, clipped against the leaf-off fraction. Keep fraction is continuous
            // and monotone; none clipped at fraction 0, all leaf clipped at fraction 1.
            float[] hashes = { 0.05f, 0.25f, 0.5f, 0.75f, 0.95f };
            Check(Mathf.Abs(ImpostorSeason.CanopyKeepFraction(0f) - 1f) < 1e-5f
                  && Mathf.Abs(ImpostorSeason.CanopyKeepFraction(1f)) < 1e-5f,
                "Canopy keep fraction must be 1 in full leaf and 0 in full leaf-off, continuously between.");
            int clippedAt0 = 0, clippedAt1 = 0;
            foreach (float h in hashes)
            {
                if (ImpostorSeason.IsLeafOffClipped(true, h, 0f)) clippedAt0++;
                if (ImpostorSeason.IsLeafOffClipped(true, h, 1f)) clippedAt1++;
            }
            Check(clippedAt0 == 0 && clippedAt1 == hashes.Length,
                "No leaf texel clipped at fraction 0; every leaf texel clipped at fraction 1.");

            // No switch frame at the texel level: for each hash the clip flag is MONOTONE in the leaf-off
            // fraction (false→true, never back), so the canopy only ever thins — it never flickers a texel on.
            foreach (float h in hashes)
            {
                bool prev = false;
                for (float f = 0f; f <= 1.0001f; f += 0.1f)
                {
                    bool clipped = ImpostorSeason.IsLeafOffClipped(true, h, Mathf.Min(f, 1f));
                    Check(!(prev && !clipped), "A leaf texel once clipped must stay clipped as leaf-off deepens (no flicker).");
                    prev = clipped;
                }
            }

            // Wood is immune to clipping regardless of hash or fraction.
            Check(!ImpostorSeason.IsLeafOffClipped(false, 0.99f, 1f),
                "Wood must never clip during leaf-off (only the canopy thins).");
        }

        /// <summary>
        /// The measured impostor-atlas budget and the residency discipline it forces (§ 9.6 G-36).
        ///
        /// Two claims. First, the arithmetic: a species' atlas costs edge² × bpp/8 × maps, and the pool divided
        /// by that cost is the resident capacity — the numbers must reproduce the tier table (Desktop ≈ 8 MB a
        /// species, so a 48 MB pool holds SIX; Ultra holds twelve). Second, the behaviour behind the done-when:
        /// a world with a forty-species library whose per-locality WORKING SET fits the capacity shows a steady
        /// resident set with evictions falling to zero, while a working set that exceeds capacity thrashes and
        /// the eviction rate stays high — the signal the debug window must surface.
        /// </summary>
        private static void TestImpostorAtlasBudget()
        {
            // The byte cost reproduces the tier table exactly.
            int desktopEdge = ImpostorAtlasBudget.AtlasEdgePx(256, 8); // 8×8 hemi views of 256² tiles
            Check(desktopEdge == 2048, "Desktop atlas edge must be 256 tile × 8 views = 2048 px.");
            long desktopBytes = ImpostorAtlasBudget.BytesPerSpecies(2048, 8f, 2); // BC7 8 bpp, albedo+depth & normal
            Check(desktopBytes == 8L * 1024 * 1024, "Desktop species must cost 8 MiB (2048² @ 8 bpp × 2 maps).");
            long mobileLowBytes = ImpostorAtlasBudget.BytesPerSpecies(320, 3.56f, 2); // ASTC 6×6
            Check(System.Math.Abs(mobileLowBytes - 91136L) < 256L, "Mobile-Low species must cost ≈ 89 KB.");

            // The pool divided by the per-species cost is the resident capacity — six and twelve, not fifty.
            Check(ImpostorAtlasBudget.ResidentCapacity(48L * 1024 * 1024, desktopBytes) == 6,
                "A 48 MB Desktop pool must hold exactly six resident species.");
            Check(ImpostorAtlasBudget.ResidentCapacity(96L * 1024 * 1024, desktopBytes) == 12,
                "A 96 MB Ultra pool must hold exactly twelve resident species.");
            Check(ImpostorAtlasBudget.ResidentCapacity(0L, desktopBytes) == 0
                  && ImpostorAtlasBudget.ResidentCapacity(48L * 1024 * 1024, 0L) == 0,
                "Resident capacity must fail closed to 0 on a degenerate pool or species cost.");

            // Steady: a forty-species library, but the loaded locality shows only six distinct species. Touching
            // that working set repeatedly never exceeds capacity, so after admission NO eviction ever occurs.
            var steady = new LruResidencySet(6);
            for (int cycle = 0; cycle < 5; cycle++)
                for (int s = 0; s < 6; s++)
                    steady.Touch(s);
            Check(steady.ResidentCount == 6, "A working set that fits must fill the resident slots and hold them.");
            Check(steady.EvictionCount == 0, "A working set within capacity must never evict — a steady resident set.");
            Check(ImpostorAtlasBudget.EvictionsPerMinute(steady.EvictionCount, 30f) == 0f,
                "A steady resident set must report a zero eviction rate.");

            // Thrash: the whole forty-species library visible at once against six slots. A cyclic scan is LRU's
            // worst case — every touch past the initial fill is a miss and evicts — so the rate stays high.
            var thrash = new LruResidencySet(6);
            for (int cycle = 0; cycle < 10; cycle++)
                for (int s = 0; s < 40; s++)
                    thrash.Touch(s);
            Check(thrash.ResidentCount == 6, "Capacity is honoured even under thrash — never more than six resident.");
            Check(thrash.EvictionCount > 300, "A working set far over capacity must evict on nearly every touch (thrash).");
            float thrashRate = ImpostorAtlasBudget.EvictionsPerMinute(thrash.EvictionCount, 30f);
            Check(thrashRate > ImpostorAtlasBudget.EvictionsPerMinute(steady.EvictionCount, 30f),
                "The thrashing world's eviction rate must exceed the steady world's — the readout that distinguishes them.");

            // A hit on a resident species is free (promotes, does not evict); a zero-capacity set never resides.
            var lru = new LruResidencySet(3);
            lru.Touch(1); lru.Touch(2); lru.Touch(3);
            Check(!lru.Touch(1) && lru.EvictionCount == 0, "Re-touching a resident species must not evict.");
            Check(lru.Touch(4) && !lru.IsResident(2),
                "Admitting a fourth species must evict the least-recently-used (species 2, untouched longest).");
            var none = new LruResidencySet(0);
            Check(!none.Touch(7) && none.ResidentCount == 0, "A zero-capacity set must never hold a resident.");
        }

        /// <summary>
        /// The dithered cross-fade that hides a switch without alpha blending (§ 9.6 G-37).
        ///
        /// The done-when is "no pop at any switch, both meshes opaque". The no-pop part reduces to three pure
        /// invariants the shader mirrors: the incoming and outgoing keep-sets are exact COMPLEMENTS at every fade
        /// fraction (silhouette covered once — no hole, no double-draw); the incoming set is MONOTONE in t (a
        /// fragment once switched stays switched — no flicker); and coverage is LINEAR (kept fraction = t for
        /// evenly spread blue-noise thresholds, so area trades smoothly and hits full at the band ends). The band
        /// is a fraction of the switch distance, so it scales rather than being a fixed metre count.
        /// </summary>
        private static void TestDitherCrossFade()
        {
            // Complementarity + endpoints across a grid of thresholds and fade fractions.
            float[] ts = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            foreach (float t in ts)
            {
                for (int i = 0; i < 100; i++)
                {
                    float th = i / 100f; // evenly spread thresholds in [0,1)
                    bool inc = DitherCrossFade.KeepIncoming(th, t);
                    bool outg = DitherCrossFade.KeepOutgoing(th, t);
                    Check(inc != outg, "Incoming and outgoing keep-sets must be exact complements (cover the silhouette once).");
                }
            }
            for (int i = 0; i < 100; i++)
            {
                float th = i / 100f;
                Check(!DitherCrossFade.KeepIncoming(th, 0f), "At t=0 the incoming mesh keeps nothing (full outgoing).");
                Check(DitherCrossFade.KeepIncoming(th, 1f), "At t=1 the incoming mesh keeps every fragment (full incoming).");
            }

            // Monotone in t: for a fixed fragment, once it flips to incoming it never flips back (no flicker).
            for (int i = 0; i < 20; i++)
            {
                float th = i / 20f;
                bool prev = false;
                for (float t = 0f; t <= 1.0001f; t += 0.05f)
                {
                    bool inc = DitherCrossFade.KeepIncoming(th, Mathf.Min(t, 1f));
                    Check(!(prev && !inc), "A fragment once handed to the incoming mesh must stay incoming as t rises (no flicker).");
                    prev = inc;
                }
            }

            // Coverage law: with evenly spread thresholds the incoming fraction equals t (linear area trade).
            const int N = 1000;
            foreach (float t in new[] { 0.1f, 0.37f, 0.62f, 0.9f })
            {
                int kept = 0;
                for (int i = 0; i < N; i++)
                    if (DitherCrossFade.KeepIncoming(i / (float)N, t)) kept++;
                Check(Mathf.Abs(kept / (float)N - DitherCrossFade.IncomingCoverageFraction(t)) < 0.01f,
                    "Incoming coverage must equal t for evenly spread thresholds (linear, pop-free area trade).");
            }

            // The band is derived from the switch distance, not a fixed metre count: doubling the switch
            // distance doubles the band width, and equal fractional recession gives equal t at any scale.
            float end50 = DitherCrossFade.TransitionBandEndM(50f, 0.2f);
            float end100 = DitherCrossFade.TransitionBandEndM(100f, 0.2f);
            Check(Mathf.Abs((end50 - 50f) - 10f) < 1e-3f && Mathf.Abs((end100 - 100f) - 20f) < 1e-3f,
                "The transition band must be a fraction of the switch distance (wider at a farther switch).");
            Check(Mathf.Abs(DitherCrossFade.FadeFraction(50f, 50f, 0.2f)) < 1e-4f
                  && Mathf.Abs(DitherCrossFade.FadeFraction(end50, 50f, 0.2f) - 1f) < 1e-4f,
                "Fade fraction must be 0 at the switch point and 1 at the band's far end.");
            Check(Mathf.Abs(DitherCrossFade.FadeFraction(55f, 50f, 0.2f)
                            - DitherCrossFade.FadeFraction(110f, 100f, 0.2f)) < 1e-4f,
                "Equal fractional recession must give equal fade fraction at any switch scale (a constant on-screen dissolve).");

            // Guards: a non-positive band start yields no fade rather than a divide-by-zero.
            Check(DitherCrossFade.FadeFraction(10f, 0f, 0.2f) == 0f, "A degenerate band start must fail closed to no fade.");
        }

        /// <summary>
        /// The separate, shorter shadow budget (§ 9.6 G-38).
        ///
        /// The done-when is that an impostor casts either no shadow or its depth-derived shadow — never a
        /// full-mesh one. That reduces to the mode classifier's order (FullMesh only before the impostor switch;
        /// DepthDerived or None after) being monotone in distance, plus two budget facts: the effective
        /// shadow-cast distance is clamped below the geometry cull (shadows stop before geometry), and the shadow
        /// LOD rides a reduced bias so it is never finer than the visible mesh's at the same distance.
        /// </summary>
        private static void TestShadowLodBudget()
        {
            // The shadow budget is clamped below geometry — shadows never outlast the geometry that casts them.
            Check(Mathf.Abs(ShadowLodBudget.EffectiveShadowCastDistanceM(200f, 150f) - 150f) < 1e-4f,
                "An authored shadow distance beyond the geometry cull must clamp to the cull.");
            Check(Mathf.Abs(ShadowLodBudget.EffectiveShadowCastDistanceM(80f, 150f) - 80f) < 1e-4f,
                "An authored shadow distance within the geometry cull is honoured verbatim.");
            Check(ShadowLodBudget.EffectiveShadowCastDistanceM(0f, 150f) == 0f,
                "A disabled (0) authored shadow distance stays disabled.");

            const float switchM = 40f, shadowCastM = 100f;
            // The done-when: once the prop is an impostor, its shadow is never FullMesh — DepthDerived, then None.
            for (float d = switchM; d < shadowCastM; d += 2f)
                Check(ShadowLodBudget.SelectShadowMode(d, switchM, shadowCastM) == ShadowCastMode.DepthDerived,
                    "Between the impostor switch and the shadow-cast distance the shadow must be depth-derived, never full-mesh.");
            Check(ShadowLodBudget.SelectShadowMode(20f, switchM, shadowCastM) == ShadowCastMode.FullMesh,
                "Before the impostor switch the geometry casts its true shadow.");
            Check(ShadowLodBudget.SelectShadowMode(100f, switchM, shadowCastM) == ShadowCastMode.None
                  && ShadowLodBudget.SelectShadowMode(150f, switchM, shadowCastM) == ShadowCastMode.None,
                "Past the shadow-cast distance the prop casts no shadow at all.");

            // Monotone in distance: the mode can only coarsen as the prop recedes (FullMesh→DepthDerived→None).
            int prevMode = int.MaxValue;
            for (float d = 1f; d < 130f; d += 1f)
            {
                int m = (int)ShadowLodBudget.SelectShadowMode(d, switchM, shadowCastM);
                Check(m <= prevMode, "Shadow mode must be monotone in distance — a receding prop only sheds shadow detail.");
                prevMode = m;
            }

            // A disabled shadow distance yields None at every distance.
            Check(ShadowLodBudget.SelectShadowMode(10f, switchM, 0f) == ShadowCastMode.None,
                "A species with shadows disabled must cast none at any distance.");

            // Shadow LOD rides a reduced bias, so at any distance it is no finer than the visible mesh's LOD.
            float[] thresholds = { 0.5f, 0.2f, 0.05f };
            Check(Mathf.Abs(ShadowLodBudget.ShadowLodBias(1f, 0.5f) - 0.5f) < 1e-5f, "Shadow bias is the main bias times the acuity scale.");
            for (float d = 3f; d < 60f; d += 3f)
            {
                int mainLod = MeshLODController.SelectLod(1f, d, 60f, thresholds, 1f);
                int shadowLod = ShadowLodBudget.SelectShadowLod(1f, d, 60f, thresholds, 1f, 0.5f);
                Check(shadowLod >= mainLod, "The shadow LOD must never be finer than the visible mesh's LOD at the same distance.");
            }
            Check(ShadowLodBudget.ShadowLodBias(0f, 0.5f) == 0f && ShadowLodBudget.ShadowLodBias(1f, 0f) == 0f,
                "Shadow bias must fail closed to 0 on degenerate inputs.");
        }

        /// <summary>
        /// The managed instanced-draw plan within C-01 (§ 9.6 G-39).
        ///
        /// The done-when has two halves: 20 000 rocks of six prototypes issue on the order of TENS of draw
        /// calls, and the render path allocates zero bytes. The draw-call half is pure arithmetic — batches
        /// grouped by (mesh, material), each sliced into ≤ 1023 — and is checked exactly here. The zero-alloc
        /// half is modelled by the slice buffer being cleared and refilled rather than reallocated; the true GC
        /// figure is a play-mode measurement deferred to the gate. Also pinned: slices sum back to the instance
        /// count (nothing dropped or double-drawn) and grouping collapses material switches to the distinct count.
        /// </summary>
        private static void TestInstancedBatching()
        {
            // Per-batch draw-call arithmetic: ceil(instances / 1023).
            Check(InstancedBatchPlanner.DrawCallsForInstances(0) == 0, "An empty batch needs no draw call.");
            Check(InstancedBatchPlanner.DrawCallsForInstances(1) == 1, "One instance is one draw call.");
            Check(InstancedBatchPlanner.DrawCallsForInstances(1023) == 1, "Exactly 1023 instances fit one draw call.");
            Check(InstancedBatchPlanner.DrawCallsForInstances(1024) == 2, "1024 instances need a second slice.");

            // The done-when figure: 20 000 rocks across six prototypes → tens of draw calls, six batches.
            var keys = new List<MeshMaterialKey>(20000);
            for (int i = 0; i < 20000; i++)
            {
                int proto = i % 6;
                keys.Add(new MeshMaterialKey(proto, proto));
            }
            int total = InstancedBatchPlanner.PlanDrawCalls(keys, out int batchCount);
            Check(batchCount == 6, "Six prototypes must group into exactly six (mesh, material) batches.");
            Check(total >= 20 && total <= 30, "20 000 rocks of six prototypes must issue on the order of tens of draw calls.");
            Check(total < 100, "The instanced plan must collapse 20 000 instances to far fewer than 20 000 draws.");

            // Slice sizes sum back to the instance count (no instance dropped or double-drawn), and the buffer is
            // reused across calls rather than reallocated — the shape of the zero-allocation render path.
            var sliceBuffer = new List<int>(4);
            foreach (int n in new[] { 3334, 1023, 500, 2047 })
            {
                int slices = InstancedBatchPlanner.FillSliceSizes(n, sliceBuffer);
                int sum = 0;
                for (int i = 0; i < sliceBuffer.Count; i++)
                {
                    Check(sliceBuffer[i] <= InstancedBatchPlanner.MaxInstancesPerDraw, "No slice may exceed the 1023-matrix draw limit.");
                    sum += sliceBuffer[i];
                }
                Check(sum == n, "Slice sizes must sum back to the instance count — nothing dropped or double-drawn.");
                Check(slices == InstancedBatchPlanner.DrawCallsForInstances(n), "Slice count must match the draw-call arithmetic.");
            }

            // Grouping by material collapses material switches to the distinct-material count; a random order does not.
            int grouped = InstancedBatchPlanner.MaterialSwitchCount(new[] { 0, 0, 0, 1, 1, 2, 2, 2 });
            int scattered = InstancedBatchPlanner.MaterialSwitchCount(new[] { 0, 1, 0, 1, 2, 0 });
            Check(grouped == 3, "Sorted-by-material draw order incurs one switch per distinct material.");
            Check(scattered > grouped, "An unsorted order incurs more material switches — which grouping avoids.");

            // Guards.
            Check(InstancedBatchPlanner.TotalDrawCalls(null) == 0 && InstancedBatchPlanner.MaterialSwitchCount(null) == 0,
                "Null inputs must fail closed to 0.");
        }

        /// <summary>
        /// Stem count is ecology, not a render setting (§ 9.6 G-40, L-26 Reineke).
        ///
        /// The done-when: lowering the tier changes LOD and impostor distances but leaves stem counts identical,
        /// and only § 9.10's explicit degradation can change counts. This is pinned structurally — the count law
        /// takes diameter, k and area only, no tier — and behaviourally: the render knobs (lodBias, atlas
        /// resolution) are varied and the count does not move, while the switch distances do. Reineke's slope
        /// (N ∝ D^−1.605) is also checked, and that degradation at keep = 1 is the identity (no silent deletion).
        /// </summary>
        private static void TestSelfThinningDensity()
        {
            const float k = 175000f;

            // Reineke: bigger trees self-thin to fewer stems, on the stable −1.605 slope.
            float n10 = SelfThinningDensity.MaxStemsPerHectare(10f, k);
            float n20 = SelfThinningDensity.MaxStemsPerHectare(20f, k);
            float n40 = SelfThinningDensity.MaxStemsPerHectare(40f, k);
            Check(n10 > n20 && n20 > n40, "Self-thinning must reduce stems per hectare as tree diameter grows.");
            float ratio = Mathf.Pow(2f, SelfThinningDensity.ReinekeExponent); // ≈ 0.3287 per diameter doubling
            Check(Mathf.Abs(n20 / n10 - ratio) < 0.005f && Mathf.Abs(n40 / n20 - ratio) < 0.005f,
                "Doubling diameter must scale stems by 2^−1.605 (Reineke's stand-density line).");

            // Stem count scales with area, floored.
            int c1 = SelfThinningDensity.StemCountForStand(1f, 25f, k);
            int c2 = SelfThinningDensity.StemCountForStand(2f, 25f, k);
            Check(c1 > 0 && Mathf.Abs(c2 - 2 * c1) <= 2, "Stem count must scale with stand area.");

            // THE done-when: vary every render knob and the count does not move, while the switch distances do.
            int countUltra = SelfThinningDensity.StemCountForStand(1f, 25f, k);
            int countLow = SelfThinningDensity.StemCountForStand(1f, 25f, k); // same ecology inputs, "different tier"
            Check(countUltra == countLow, "Stem count must be identical across tiers — LOD never changes the number of stems.");

            float[] thresholds = { 0.5f, 0.2f, 0.05f };
            int lodUltra = MeshLODController.SelectLod(1f, 30f, 60f, thresholds, 2.0f);  // Ultra bias
            int lodLow = MeshLODController.SelectLod(1f, 30f, 60f, thresholds, 0.5f);    // Low bias
            float dSwitchUltra = ImpostorSystem.ImpostorSwitchDistanceM(2f, 256, 1080, 60f); // fine atlas
            float dSwitchLow = ImpostorSystem.ImpostorSwitchDistanceM(2f, 64, 1080, 60f);     // coarse atlas
            Check(lodUltra <= lodLow, "A lower tier (smaller bias) must select an equal-or-coarser LOD — cost changes.");
            Check(Mathf.Abs(dSwitchUltra - dSwitchLow) > 1f, "The impostor switch distance must move with tier (atlas resolution).");
            // ...and through all of that, the count is untouched:
            Check(SelfThinningDensity.StemCountForStand(1f, 25f, k) == countUltra,
                "Stem count stays bit-identical while LOD and impostor distances change — the render path deletes no tree.");

            // Degradation is the ONE door that may change the count; at keep = 1 it is the identity.
            Check(SelfThinningDensity.DegradedStemCount(countUltra, 1f) == countUltra,
                "Degradation at keep-fraction 1 must remove no stem (no silent deletion by default).");
            Check(SelfThinningDensity.DegradedStemCount(countUltra, 0.5f) == Mathf.FloorToInt(countUltra * 0.5f),
                "Only § 9.10's explicit degradation keep-fraction reduces the count, and by exactly that fraction.");

            // Guards.
            Check(SelfThinningDensity.MaxStemsPerHectare(0f, k) == 0f
                  && SelfThinningDensity.MaxStemsPerHectare(25f, 0f) == 0f
                  && SelfThinningDensity.StemCountForStand(0f, 25f, k) == 0,
                "Density law must fail closed to 0 on degenerate diameter, k or area.");
        }

        /// <summary>
        /// The splat-cache layer budget that states why the cache exists (§ 9.7 G-41).
        ///
        /// The done-when is a manual paragraph: the layer count achievable per tier, and why. These are the
        /// numbers it states. The "why" is the contrast pinned here — without the cache, sampled textures grow
        /// as ceil(layers/4) alphamaps and layers cap at eight; with it, the sampled-texture count is a constant
        /// two regardless of layer count, so the achievable layer count is bounded only by the tier's composite
        /// budget and runs past eight on Desktop and Ultra. The twelve-layer world § 9.4+§ 9.5 imply needs it.
        /// </summary>
        private static void TestSplatCacheModel()
        {
            // Without the cache, sampled alphamaps grow with the layer count; the sampler caps at eight layers.
            Check(SplatCacheModel.AlphamapsForLayers(4) == 1 && SplatCacheModel.AlphamapsForLayers(5) == 2
                  && SplatCacheModel.AlphamapsForLayers(8) == 2 && SplatCacheModel.AlphamapsForLayers(12) == 3,
                "Un-cached alphamap count must be ceil(layers/4) — it grows with the layer count.");
            Check(!SplatCacheModel.ExceedsUncachedLimit(8) && SplatCacheModel.ExceedsUncachedLimit(9)
                  && SplatCacheModel.ExceedsUncachedLimit(12),
                "The un-cached ceiling is eight layers; the 12-layer world is past it.");

            // With the cache, the per-pixel sampled-texture count is a CONSTANT two, no matter the layer count —
            // that is the whole justification, so it must not track the un-cached alphamap growth.
            foreach (int layers in new[] { 4, 8, 12, 24 })
                Check(SplatCacheModel.SampledTexturesWithCache == 2
                      && SplatCacheModel.SampledTexturesWithCache < SplatCacheModel.AlphamapsForLayers(layers) + 10,
                    "The cached path samples a fixed two textures regardless of source-layer count.");
            Check(SplatCacheModel.AlphamapsForLayers(24) > SplatCacheModel.SampledTexturesWithCache,
                "At 24 layers the un-cached cost exceeds the cache's fixed sample count — the cache pays off.");

            // The per-tier achievable layer count the manual states: past eight on Desktop and Ultra.
            Check(SplatCacheModel.MaxLayersWithCache(SplatCacheTier.MobileLow) == 8
                  && SplatCacheModel.MaxLayersWithCache(SplatCacheTier.Desktop) == 16
                  && SplatCacheModel.MaxLayersWithCache(SplatCacheTier.Ultra) == 24,
                "The per-tier layer budget must break the eight-layer limit on Desktop and Ultra.");
            Check(SplatCacheModel.MaxLayersWithCache(SplatCacheTier.Desktop) >= 12,
                "The 12-layer world § 9.4 + § 9.5 imply must fit inside the Desktop budget.");

            // Guard.
            Check(SplatCacheModel.AlphamapsForLayers(0) == 0, "Zero layers cost no alphamap.");
        }

        /// <summary>
        /// Analytic, deterministic page residency for the splat cache (§ 9.7 G-42).
        ///
        /// The done-when: the page set for a given camera transform is identical across runs and machines. Cross-
        /// machine identity follows from the set being pure integer floor arithmetic with a fixed fill order (no
        /// hash set, no float reduction), so what is pinned here is run-to-run identity, the count law, sorted
        /// duplicate-free output, whole-page translation invariance, and that a coarser mip holds fewer pages.
        /// </summary>
        private static void TestSplatPageResidency()
        {
            var a = new List<PageKey>();
            var b = new List<PageKey>();

            // Determinism: two identical calls produce identical, order-for-order equal sets.
            int na = SplatPageResidency.FillResidentPages(12.5f, -7.3f, 32f, 16f, a);
            int nb = SplatPageResidency.FillResidentPages(12.5f, -7.3f, 32f, 16f, b);
            Check(na == nb && na > 0, "The same camera transform must yield the same page count.");
            bool identical = a.Count == b.Count;
            for (int i = 0; identical && i < a.Count; i++)
                if (!a[i].Equals(b[i])) identical = false;
            Check(identical, "The page set must be bit-identical across runs (deterministic order).");

            // Sorted and duplicate-free without a hash set.
            bool ordered = true;
            for (int i = 1; i < a.Count; i++)
                if (a[i].CompareTo(a[i - 1]) <= 0) { ordered = false; break; }
            Check(ordered, "The resident page list must be strictly sorted and duplicate-free.");

            // Same tile → same page coord; a whole-tile step → the next page coord.
            Check(SplatPageResidency.PageCoordForWorld(5f, 9f, 16f).Equals(new PageKey(0, 0))
                  && SplatPageResidency.PageCoordForWorld(17f, 3f, 16f).Equals(new PageKey(1, 0)),
                "World positions in one tile share a page coord; crossing a tile advances it.");

            // Count law: the list count matches the closed-form span² and the analytic count.
            Check(SplatPageResidency.ResidentPageCount(0f, 0f, 32f, 16f) == 25, "range 32 / page 16 must cover a 5×5 page square.");
            Check(na == SplatPageResidency.ResidentPageCount(12.5f, -7.3f, 32f, 16f),
                "FillResidentPages count must equal the closed-form ResidentPageCount.");

            // Whole-page translation invariance: shifting the camera by exactly one page size shifts every page
            // index by one and preserves the count — the property that makes a slow pan stream pages predictably.
            var shifted = new List<PageKey>();
            int ns = SplatPageResidency.FillResidentPages(12.5f + 16f, -7.3f, 32f, 16f, shifted);
            bool shiftOk = ns == na;
            for (int i = 0; shiftOk && i < a.Count; i++)
                if (shifted[i].X != a[i].X + 1 || shifted[i].Z != a[i].Z) shiftOk = false;
            Check(shiftOk, "A one-page camera translation must shift every page index by one with the count unchanged.");

            // A coarser mip has larger pages, so it holds fewer of them.
            float s0 = SplatPageResidency.PageWorldSizeM(16f, 0);
            float s1 = SplatPageResidency.PageWorldSizeM(16f, 1);
            float s2 = SplatPageResidency.PageWorldSizeM(16f, 2);
            Check(Mathf.Abs(s0 - 16f) < 1e-4f && Mathf.Abs(s1 - 32f) < 1e-4f && Mathf.Abs(s2 - 64f) < 1e-4f,
                "Page world size must double per mip level.");
            int c0 = SplatPageResidency.ResidentPageCount(0f, 0f, 128f, s0);
            int c1 = SplatPageResidency.ResidentPageCount(0f, 0f, 128f, s1);
            int c2 = SplatPageResidency.ResidentPageCount(0f, 0f, 128f, s2);
            Check(c0 > c1 && c1 > c2, "A coarser mip must hold fewer resident pages for the same cache range.");

            // Guards.
            Check(SplatPageResidency.FillResidentPages(0f, 0f, 32f, 0f, a) == 0
                  && SplatPageResidency.ResidentPageCount(0f, 0f, -1f, 16f) == 0,
                "Residency must fail closed to 0 on a degenerate page size or negative range.");
        }

        private static void TestSplatMipSelection()
        {
            const float texel = 0.1f;   // world metres per cached texel at mip 0
            const int mips = 8;

            // The physically correct direction: as the ground recedes (metres-per-pixel grows) the mip RISES,
            // so far ground coarsens and near ground stays fine. This pins the corrected ratio against the task's
            // inverted boxed formula, which would finen the far ground and blur the near.
            int near = SplatMipSelection.SelectMip(texel, 0.05f, mips);   // texel bigger than a pixel → finest
            int mid  = SplatMipSelection.SelectMip(texel, 0.4f, mips);    // log2(4) = 2
            int far  = SplatMipSelection.SelectMip(texel, 1.6f, mips);    // log2(16) = 4
            Check(near == 0 && mid == 2 && far == 4, "Mip must rise with distance: fine near, coarse far.");
            Check(near < mid && mid < far, "The selected mip must increase monotonically with metres-per-pixel.");

            // Nyquist bracket (floor): at the selected mip m, one texel spans ≤ one pixel and mip m+1 would exceed it.
            int m = SplatMipSelection.SelectMip(texel, 0.3f, mips);       // log2(3) = 1.58 → 1
            Check(texel * (1 << m) <= 0.3f && 0.3f < texel * (1 << (m + 1)),
                "One texel at the selected mip must map to about one pixel (Nyquist floor bracket).");

            // Long-axis selection is coarser than short-axis, and the footprint method uses the long axis so the
            // worst-sampled direction cannot shimmer; the short axis is carried by anisotropic filtering.
            int mipLong  = SplatMipSelection.SelectMip(texel, 0.4f, mips);
            int mipShort = SplatMipSelection.SelectMip(texel, 0.1f, mips);
            Check(mipLong >= mipShort, "The long footprint axis must select a mip no finer than the short axis.");
            Check(SplatMipSelection.SelectMipFromFootprint(texel, 0.4f, 0.1f, mips) == mipLong,
                "Footprint mip selection must take the long (larger metres-per-pixel) axis.");
            Check(Mathf.Abs(SplatMipSelection.AnisotropyRatio(0.4f, 0.1f) - 4f) < 1e-4f,
                "Anisotropy ratio must be long / short metres-per-pixel.");

            // Clamp: a grazing far view cannot demand a mip past the top, nor a near view a negative one.
            Check(SplatMipSelection.SelectMip(texel, 1e6f, mips) == mips - 1
                  && SplatMipSelection.SelectMip(texel, 1e-6f, mips) == 0,
                "The selected mip must clamp to [0, mipCount − 1].");

            // Guards fail closed.
            Check(SplatMipSelection.SelectMip(0f, 0.4f, mips) == 0
                  && SplatMipSelection.SelectMip(texel, 0.4f, 0) == 0
                  && Mathf.Abs(SplatMipSelection.AnisotropyRatio(0.05f, 0.2f) - 1f) < 1e-4f,
                "Degenerate texel size, zero mip count, and short≥long must fail closed.");
        }

        private static void TestSplatPageCacheLru()
        {
            var A = new PageKey(0, 0); var B = new PageKey(1, 0); var C = new PageKey(0, 1);
            var D = new PageKey(5, 5); var E = new PageKey(6, 6);

            // Pin a three-page near ring in a four-page atlas, then admit one far page: it fits, no eviction.
            var c = new SplatPageCache(4);
            c.Touch(A, true); c.Touch(B, true); c.Touch(C, true);
            bool evD = c.Touch(D, false);
            Check(!evD && c.ResidentCount == 4 && c.PinnedCount == 3, "A working set fitting capacity must not evict.");

            // A fast pan touches many distinct far pages; each admit evicts only an UNPINNED page — the near ring
            // A/B/C is never evicted, which is the done-when: a 360° spin shows no unresolved page under the camera.
            bool evE = c.Touch(E, false);
            Check(evE && !c.IsResident(D) && c.IsResident(E), "A full atlas must evict the LRU unpinned page (the far one).");
            c.Touch(new PageKey(7, 7), false); c.Touch(new PageKey(8, 8), false); c.Touch(new PageKey(9, 9), false);
            Check(c.IsResident(A) && c.IsResident(B) && c.IsResident(C) && c.PinnedCount == 3,
                "The pinned near ring must survive a pan storm untouched.");
            Check(c.ResidentCount == 4, "Residency must never exceed capacity.");

            // Steady state: a working set that fits capacity reaches zero evictions once warm and stays there.
            var s = new SplatPageCache(4);
            s.Touch(A, true); s.Touch(B, true); s.Touch(C, true); s.Touch(D, false);
            for (int i = 0; i < 5; i++) { s.Touch(A, true); s.Touch(B, true); s.Touch(C, true); s.Touch(D, false); }
            Check(s.EvictionCount == 0, "A working set within capacity must reach zero evictions (steady, no thrash).");

            // Promotion protects a freshly-used page: touching P moves it off the LRU tail so Q is evicted instead.
            var p = new SplatPageCache(3);
            var P = new PageKey(1, 1); var Q = new PageKey(2, 2); var R = new PageKey(3, 3);
            p.Touch(P, false); p.Touch(Q, false); p.Touch(R, false);
            p.Touch(P, false);                                   // P is now MRU
            p.Touch(new PageKey(4, 4), false);
            Check(p.IsResident(P) && !p.IsResident(Q), "A promoted page must outlive the older unpinned one.");

            // The ring follows the camera: a page touched unpinned loses its pin and becomes evictable.
            var f = new SplatPageCache(2);
            f.Touch(A, true); f.Touch(B, false);
            f.Touch(A, false);                                   // A leaves the near ring
            Check(f.PinnedCount == 0, "Touching a page unpinned must clear its pin (ring follows the camera).");
            f.Touch(C, false); f.Touch(D, false);
            Check(!f.IsResident(A), "An unpinned former-ring page must become evictable again.");

            // Guard: a zero-capacity atlas holds nothing, even a pinned page.
            var z = new SplatPageCache(0);
            Check(!z.Touch(A, true) && !z.IsResident(A) && z.ResidentCount == 0, "A zero-capacity atlas must stay empty.");
        }

        private static void TestComputedLayerComposite()
        {
            var w = new float[ComputedLayerComposite.SourceCount];

            // A partition of unity: whatever the site, the visible weights sum to one (a proper cover fraction).
            ComputedLayerComposite.Composite(0.3f, 0.4f, 1f, 1f, 1f, 1f, 1f, w);
            float sum = 0f; for (int i = 0; i < w.Length; i++) sum += w[i];
            Check(Mathf.Abs(sum - 1f) < 1e-4f, "Composited layer weights must sum to one (partition of unity).");

            // Top-down cover order: snow covers everything, then litter covers the mineral soil, then the substrate.
            ComputedLayerComposite.Composite(1f, 0.5f, 1f, 1f, 1f, 1f, 1f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.Snow
                  && Mathf.Abs(w[(int)GroundLayerSource.Snow] - 1f) < 1e-4f,
                "Above the snowline, snow must dominate and cover the layers below it.");
            ComputedLayerComposite.Composite(0f, 1f, 1f, 1f, 1f, 1f, 1f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.Litter,
                "Under full canopy with no snow, litter must dominate the mineral substrate.");
            ComputedLayerComposite.Composite(0.6f, 1f, 0f, 0f, 1f, 0f, 0f, w);
            Check(w[(int)GroundLayerSource.Snow] > w[(int)GroundLayerSource.Litter]
                  && w[(int)GroundLayerSource.Alluvium] < 1e-4f,
                "Snow must occlude litter, and both must occlude the mineral substrate.");

            // Each § 9.4 rule wins on its own site — the varied, site-appropriate ground of an unpainted world.
            ComputedLayerComposite.Composite(0f, 0f, 0.1f, 5f, 0.1f, 0.1f, 0.1f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.Colluvium, "A talus slope must read as colluvium.");
            ComputedLayerComposite.Composite(0f, 0f, 0.1f, 0.1f, 5f, 0.1f, 0.1f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.Alluvium, "A valley bottom must read as alluvium.");
            ComputedLayerComposite.Composite(0f, 0f, 0.1f, 0.1f, 0.1f, 5f, 0.1f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.AeolianSand, "A dune field must read as aeolian sand.");
            ComputedLayerComposite.Composite(0f, 0f, 0.1f, 0.1f, 0.1f, 0.1f, 5f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.ClastArmour, "An armoured surface must read as clast lag.");
            ComputedLayerComposite.Composite(0f, 0f, 5f, 0.1f, 0.1f, 0.1f, 0.1f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.ExposedBedrock, "Thin cover must read as exposed bedrock.");

            // Never empty: with no active process the ground still resolves to bedrock, not to nothing.
            ComputedLayerComposite.Composite(0f, 0f, 0f, 0f, 0f, 0f, 0f, w);
            sum = 0f; for (int i = 0; i < w.Length; i++) sum += w[i];
            Check(Mathf.Abs(sum - 1f) < 1e-4f && ComputedLayerComposite.DominantSource(w) == GroundLayerSource.ExposedBedrock,
                "An unpainted world with no active process must fall back to bedrock, never to empty ground.");

            // Guards: snow over-range clamps; a negative suitability clamps to zero (colluvium then wins).
            ComputedLayerComposite.Composite(2f, 0f, 1f, 0f, 0f, 0f, 0f, w);
            Check(Mathf.Abs(w[(int)GroundLayerSource.Snow] - 1f) < 1e-4f, "Snow fraction must clamp to [0,1].");
            ComputedLayerComposite.Composite(0f, 0f, -5f, 1f, 0f, 0f, 0f, w);
            Check(ComputedLayerComposite.DominantSource(w) == GroundLayerSource.Colluvium, "A negative suitability must clamp to zero.");
        }

        private static void TestTerrainLayerAssignmentGuard()
        {
            // Stand-ins for TerrainLayer identities; positional index = alphamap channel.
            var painted = new[] { "rock", "soil", "snow" };

            // A quality-tier switch that changes no layer must NOT assign — the destructive write is skipped and
            // the painted alphamap survives (the done-when).
            var sameAfterTier = new[] { "rock", "soil", "snow" };
            Check(!TerrainLayerAssignmentGuard.ShouldAssign(painted, sameAfterTier),
                "An equal layer array must not be reassigned (would reset every painted weight).");

            // A real change — different length, different member, or reorder — must assign.
            Check(TerrainLayerAssignmentGuard.LayersDiffer(painted, new[] { "rock", "soil", "snow", "sand" }),
                "A different layer count must be detected as a difference.");
            Check(TerrainLayerAssignmentGuard.LayersDiffer(painted, new[] { "rock", "gravel", "snow" }),
                "A changed layer member must be detected as a difference.");
            Check(TerrainLayerAssignmentGuard.LayersDiffer(painted, new[] { "soil", "rock", "snow" }),
                "A reordered array must count as different (index is the alphamap channel).");

            // Null / empty handling: both empty do not differ; content vs null does.
            Check(!TerrainLayerAssignmentGuard.LayersDiffer<string>(null, null)
                  && !TerrainLayerAssignmentGuard.LayersDiffer(new string[0], null),
                "Two empty layer arrays must not differ.");
            Check(TerrainLayerAssignmentGuard.LayersDiffer(null, painted),
                "Assigning content over an empty array must count as a difference.");

            // ShouldAssign is exactly LayersDiffer — the guarded write fires iff there is a real change.
            Check(TerrainLayerAssignmentGuard.ShouldAssign(painted, new[] { "rock", "soil" })
                  == TerrainLayerAssignmentGuard.LayersDiffer(painted, new[] { "rock", "soil" }),
                "ShouldAssign must agree with LayersDiffer.");
        }

        private static void TestBasemapConsistency()
        {
            bool Close(Color a, Color b) =>
                Mathf.Abs(a.r - b.r) < 1e-4f && Mathf.Abs(a.g - b.g) < 1e-4f && Mathf.Abs(a.b - b.b) < 1e-4f;

            // A locally uniform patch downsamples to itself: crossing basemapDistance over uniform ground shows no
            // colour shift at all (the hard case of the done-when).
            var uniform = new Color(0.3f, 0.4f, 0.5f);
            var block = new[] { uniform, uniform, uniform, uniform };
            Check(Close(BasemapConsistency.DownsampleAverage(block), uniform),
                "A uniform patch must downsample to its own colour (no boundary shift).");

            // The downsample is the unbiased mean — no systematic offset from the high-res composite.
            var twoTone = new[] { new Color(1f, 0f, 0f), new Color(0f, 0f, 1f) };
            Check(Close(BasemapConsistency.DownsampleAverage(twoTone), new Color(0.5f, 0f, 0.5f)),
                "The basemap texel must be the mean of the composite colours it covers.");

            // Both representations read the SAME composite albedo (weight·layer sum), so they cannot disagree.
            var albedos = new[] { new Color(0.5f, 0.5f, 0.5f), Color.white };
            var comp = BasemapConsistency.CompositeAlbedo(new[] { 0.5f, 0.5f }, albedos);
            Check(Close(comp, new Color(0.75f, 0.75f, 0.75f)), "Composite albedo must be the weight·layer-albedo sum.");

            // The basemap is the downsample of that shared composite — the near side (a composite texel) and the far
            // side (the basemap) agree because they are one source, not two rule sets tuned to look alike.
            var fromComposite = new[] { comp, comp };
            Check(Close(BasemapConsistency.DownsampleAverage(fromComposite), comp),
                "A basemap built from the composite must match the composite at the boundary.");
            // An independently-authored basemap colour would NOT match — which is why the basemap must be derived.
            Check(!Close(BasemapConsistency.DownsampleAverage(fromComposite), Color.green),
                "An unrelated basemap colour must differ from the composite (the shift this task prevents).");

            // Staleness: a changed composite version forces a regenerate; an unchanged one does not.
            Check(!BasemapConsistency.NeedsRegen(7, 7) && BasemapConsistency.NeedsRegen(8, 7),
                "The basemap must regenerate exactly when the composite version changes.");

            // Guards fail closed to black.
            Check(Close(BasemapConsistency.DownsampleAverage(new Color[0]), Color.black)
                  && Close(BasemapConsistency.CompositeAlbedo(null, albedos), Color.black),
                "An empty block or null weights must fail closed to black.");
        }

        private static void TestPaintStroke()
        {
            // A stroke at (100,50), radius 10 m. Its weight is a function of world metres, so these facts hold at
            // any grid resolution — doubling the alphamap resolution samples the same continuous brush.
            var s = new PaintStroke(100f, 50f, 10f, 1f, 2f, 3);
            Check(Mathf.Abs(PaintStrokeMath.Weight(s, 100f, 50f) - 1f) < 1e-4f, "A stroke's weight peaks at strength at its centre.");
            Check(PaintStrokeMath.Weight(s, 110f, 50f) < 1e-6f, "The painted edge is at exactly the world radius (weight 0 at r).");
            Check(PaintStrokeMath.Weight(s, 109.9f, 50f) > 0f, "Just inside the world radius the weight is still positive.");
            Check(Mathf.Abs(PaintStrokeMath.Weight(s, 105f, 50f) - 0.25f) < 1e-4f, "Falloff at half-radius must be (0.5)^exponent.");

            // Monotone decreasing across the radius — no ringing, no grid dependence.
            float prev = PaintStrokeMath.Weight(s, 100f, 50f);
            bool mono = true;
            for (float x = 101f; x <= 110f; x += 1f)
            {
                float wv = PaintStrokeMath.Weight(s, x, 50f);
                if (wv > prev + 1e-6f) { mono = false; break; }
                prev = wv;
            }
            Check(mono, "Stroke weight must fall monotonically from centre to edge.");

            // Undo/redo is a stroke-list pointer op: a hundred steps walk back with no raster allocated.
            var h = new PaintStrokeHistory();
            for (int i = 0; i < 100; i++) h.Add(new PaintStroke(i, 0f, 5f, 1f, 1f, i));
            Check(h.ActiveCount == 100, "A hundred recorded strokes must all be active.");
            int undone = 0;
            while (h.Undo()) undone++;
            Check(undone == 100 && h.ActiveCount == 0 && !h.CanUndo, "Undo must walk back all hundred strokes to empty.");
            Check(h.Redo() && h.ActiveCount == 1, "Redo must re-apply an undone stroke.");

            // Adding after an undo truncates the redo tail (a new branch of history).
            var b = new PaintStrokeHistory();
            b.Add(new PaintStroke(0, 0, 5, 1, 1, 10));
            b.Add(new PaintStroke(0, 0, 5, 1, 1, 20));
            b.Add(new PaintStroke(0, 0, 5, 1, 1, 30));
            b.Undo(); b.Undo();                                    // active = 1 (only layer-10 applied)
            b.Add(new PaintStroke(0, 0, 5, 1, 1, 40));             // truncates 20/30, appends 40
            Check(b.ActiveCount == 2 && !b.CanRedo && b[1].TargetLayer == 40,
                "Adding after undo must truncate the redo tail and branch history.");

            // Guard: a non-positive radius paints nothing.
            Check(PaintStrokeMath.NormalizedFalloff(0f, 0f, 2f) == 0f, "A zero-radius stroke must have zero weight.");
        }

        private static void TestPaintStore()
        {
            var t0 = new PageKey(0, 0); var t1 = new PageKey(1, 0); var t2 = new PageKey(2, 0);
            var store = new PaintStore();

            // Untouched world costs nothing: querying an untouched tile creates no entry.
            Check(!store.HasRaster(new PageKey(5, 5)) && store.StrokeCount(new PageKey(5, 5)) == 0 && store.TileCount == 0,
                "An untouched tile must cost nothing and not be created by a query.");

            // Only touched tiles exist, keyed by tile coordinate.
            store.AddStroke(t0, new PaintStroke(1f, 2f, 5f, 1f, 1f, 0));
            store.AddStroke(t0, new PaintStroke(3f, 4f, 6f, 0.5f, 2f, 1));
            store.AddStroke(t1, new PaintStroke(20f, 0f, 5f, 1f, 1f, 2));
            store.AddStroke(t2, new PaintStroke(40f, 0f, 5f, 1f, 1f, 3));
            Check(store.TileCount == 3 && store.StrokeCount(t0) == 2, "Only touched tiles exist, keyed by tile coordinate.");

            // Composite all three, then enforce a ceiling of two: the LRU-composited raster is evicted, strokes kept.
            int v0 = store.Composite(t0);
            store.Composite(t1);
            store.Composite(t2);
            Check(store.ResidentRasterCount == 3, "Compositing must make all three rasters resident.");
            int evicted = store.EnforceCeiling(2);
            Check(evicted == 1 && store.ResidentRasterCount == 2, "The ceiling must evict rasters down to the limit.");
            Check(!store.HasRaster(t0) && store.TileCount == 3 && store.StrokeCount(t0) == 2,
                "Eviction must drop the LRU raster but keep the tile's strokes (never evict data).");

            // Re-entering an evicted area re-composites identically from the kept strokes (lossless).
            int v0Again = store.Composite(t0);
            Check(v0 == v0Again, "Re-compositing an evicted tile must reproduce its exact raster (lossless eviction).");

            // Determinism across stores: same strokes in the same order → the same composite.
            var storeB = new PaintStore();
            storeB.AddStroke(t0, new PaintStroke(1f, 2f, 5f, 1f, 1f, 0));
            storeB.AddStroke(t0, new PaintStroke(3f, 4f, 6f, 0.5f, 2f, 1));
            Check(storeB.Composite(t0) == v0, "The composite must be deterministic given the stroke list (C-04).");

            // Guard: a ceiling at or above the resident count evicts nothing.
            Check(store.EnforceCeiling(10) == 0, "A ceiling above the resident count must evict nothing.");
        }

        private static void TestPaintComposite()
        {
            // Computed base: a varied four-layer world (the § 9.7 result under an unpainted chunk).
            var baseW = new[] { 0.25f, 0.25f, 0.25f, 0.25f };
            var into = new float[4];

            // No strokes → the composite equals the computed base: an unpainted world is fully varied, not empty.
            PaintComposite.Composite(baseW, null, into);
            bool same = true;
            for (int i = 0; i < 4; i++) if (Mathf.Abs(into[i] - baseW[i]) > 1e-6f) same = false;
            Check(same, "With no strokes the composite must equal the computed base (varied unpainted world).");

            // A single stroke preserves the partition of unity — no drift, no renormalise needed.
            PaintComposite.Composite(baseW, new List<PaintOp> { new PaintOp(2, 0.5f) }, into);
            float sum = 0f; for (int i = 0; i < 4; i++) sum += into[i];
            Check(Mathf.Abs(sum - 1f) < 1e-4f, "A paint stroke must preserve the partition of unity.");
            Check(into[2] > baseW[2] && into[0] < baseW[0], "Paint must lay the target layer over the computed base.");

            // Full-strength paint saturates the target and zeroes the rest.
            var full = new float[4];
            PaintComposite.Composite(baseW, new List<PaintOp> { new PaintOp(1, 1f) }, full);
            Check(Mathf.Abs(full[1] - 1f) < 1e-4f && full[0] < 1e-4f && full[2] < 1e-4f && full[3] < 1e-4f,
                "A full-strength stroke must saturate its target layer and clear the others.");

            // Order matters: A-then-B differs from B-then-A, which is why strokes apply in recorded order.
            var ab = new float[4]; var ba = new float[4];
            PaintComposite.Composite(baseW, new List<PaintOp> { new PaintOp(0, 0.5f), new PaintOp(1, 0.5f) }, ab);
            PaintComposite.Composite(baseW, new List<PaintOp> { new PaintOp(1, 0.5f), new PaintOp(0, 0.5f) }, ba);
            Check(Mathf.Abs(ab[0] - ba[0]) > 1e-4f, "Stroke order must be significant (applied in recorded order).");

            // Determinism: rebuilding a chunk twice with the same stroke list is bit-identical (the done-when, C-04).
            var ops = new List<PaintOp> { new PaintOp(3, 0.3f), new PaintOp(0, 0.7f), new PaintOp(2, 0.4f) };
            var r1 = new float[4]; var r2 = new float[4];
            PaintComposite.Composite(baseW, ops, r1);
            PaintComposite.Composite(baseW, ops, r2);
            bool identical = true;
            for (int i = 0; i < 4; i++) if (r1[i] != r2[i]) identical = false;   // exact, not approximate
            Check(identical, "Rebuilding a chunk twice with the same stroke list must be bit-identical.");

            // Guard: an out-of-range target is a no-op, not a crash.
            var g = new[] { 0.5f, 0.5f };
            PaintComposite.ApplyStroke(g, 9, 1f);
            Check(Mathf.Abs(g[0] - 0.5f) < 1e-6f, "An out-of-range paint target must be a no-op.");
        }

        private static void TestConservingHeightBrush()
        {
            const float coreR = 5f, outerR = 10f, strength = 2f, exp = 2f, cell = 1f, area = cell * cell;

            // Pass 1: measure the deposited volume and the annulus unit volume over a grid enclosing the brush.
            float added = 0f, annulusUnit = 0f;
            for (float x = -11f; x <= 11f; x += cell)
                for (float z = -11f; z <= 11f; z += cell)
                {
                    float d = Mathf.Sqrt(x * x + z * z);
                    added += ConservingHeightBrush.CoreDeposit(d, coreR, strength, exp) * area;
                    annulusUnit += ConservingHeightBrush.AnnulusUnit(d, coreR, outerR) * area;
                }
            float scale = ConservingHeightBrush.RemovalScale(added, annulusUnit);

            // Pass 2: the conserving net volume must be under 1 % of the moved material, and there must be both a
            // mound (positive delta) and a source depression (negative delta) — a push, not an inflation.
            float total = 0f, minDelta = float.MaxValue, maxDelta = float.MinValue, unconserved = 0f;
            for (float x = -11f; x <= 11f; x += cell)
                for (float z = -11f; z <= 11f; z += cell)
                {
                    float d = Mathf.Sqrt(x * x + z * z);
                    float nd = ConservingHeightBrush.NetDelta(d, coreR, outerR, strength, exp, scale);
                    total += nd * area;
                    if (nd < minDelta) minDelta = nd;
                    if (nd > maxDelta) maxDelta = nd;
                    unconserved += ConservingHeightBrush.NetDelta(d, coreR, outerR, strength, exp, 0f) * area;
                }
            Check(added > 0f && Mathf.Abs(total) / added < 0.01f, "A conserving raise must change total volume by under 1 %.");
            Check(maxDelta > 0f && minDelta < 0f, "A conserving raise must leave a mound and a visible source depression.");

            // The unconserving fantasy mode adds net volume — it does not conserve (and is labelled as such).
            Check(Mathf.Abs(unconserved - added) < 1e-3f && unconserved > 0f,
                "The unconserving mode (removal scale 0) must add net volume, not conserve it.");

            // Guards.
            Check(ConservingHeightBrush.AnnulusUnit(7f, 5f, 5f) == 0f && ConservingHeightBrush.RemovalScale(3f, 0f) == 0f,
                "A collapsed annulus and a zero annulus volume must remove nothing.");
        }

        private static void TestAngleOfReposeClamp()
        {
            const float sandRepose = 34f, run = 1f;

            // A vertical wall of sand painted as height 10 over a neighbour at 0 sheds to a 34° talus face.
            float clamped = AngleOfReposeClamp.ClampHeight(10f, 0f, run, sandRepose);
            Check(clamped < 10f, "The repose clamp must shed the excess of a vertical sand wall.");
            Check(Mathf.Abs(AngleOfReposeClamp.SlopeDeg(clamped, 0f, run) - sandRepose) < 0.5f,
                "Clamped sand must come to rest at its angle of repose (~34°).");

            // The same stroke on granite stands as a cliff: on cover thinner than a grain or two the repose clamp
            // does not apply, so the steep face is left alone (its limit is joint geometry, not repose).
            Check(!AngleOfReposeClamp.ReposeApplies(0.001f, 0.001f) && AngleOfReposeClamp.ReposeApplies(0.5f, 0.001f),
                "Repose must apply on deep loose cover but not on near-bare bedrock.");
            float cliffSlope = AngleOfReposeClamp.SlopeDeg(10f, 0f, run);   // unclamped, since repose doesn't apply
            Check(cliffSlope > 80f, "An unclamped granite face must stand as a steep cliff.");

            // Repose bounds slope, not absolute height: a tall dune is fine while its flanks sit at the angle.
            Check(Mathf.Abs(AngleOfReposeClamp.ClampHeight(0.6f, 0f, run, sandRepose) - 0.6f) < 1e-4f,
                "A slope already within repose must be left unchanged (idempotent one pass).");

            // Steeper repose permits a higher rise over the same run.
            Check(AngleOfReposeClamp.MaxRise(60f, 1f) > AngleOfReposeClamp.MaxRise(34f, 1f),
                "A steeper repose angle must permit a larger rise per run.");

            // Guards: zero run permits no rise; a zero-degree repose clamps to flat.
            Check(AngleOfReposeClamp.MaxRise(34f, 0f) == 0f
                  && Mathf.Abs(AngleOfReposeClamp.ClampHeight(5f, 2f, 1f, 0f) - 2f) < 1e-4f,
                "A zero run permits no rise and a zero repose clamps to the neighbour height.");
        }

        private static void TestTerrainHoleMask()
        {
            // TRAP 2 - per-quad, not per-vertex: a res-65 heightmap yields a 64² hole array, never 65².
            const int res = 65;
            Check(TerrainHoleMask.HoleArrayEdge(res) == res - 1,
                "The hole array edge must be heightmapResolution − 1 (per-quad, not per-vertex).");
            bool[,] mask = TerrainHoleMask.NewFullyPresent(res);
            Check(mask.GetLength(0) == res - 1 && mask.GetLength(1) == res - 1,
                "A fresh hole mask must be dimensioned (res − 1)², not res².");

            // A fresh layer with no strokes leaves the terrain FULLY PRESENT (no accidental all-hole delete).
            Check(TerrainHoleMask.HoleCount(mask) == 0
                  && TerrainHoleMask.PresentCount(mask) == (res - 1) * (res - 1),
                "A fresh hole layer must leave the whole terrain present (true = surface present).");

            // TRAP 1 - inverted sense: punching sets quads to FALSE, and a known quad count comes out.
            int single = TerrainHoleMask.PunchCircle(mask, 10, 10, 0);
            Check(single == 1 && mask[10, 10] == false,
                "Punching radius 0 must open exactly one quad, and a hole quad must read false.");

            // A hole of a known quad count: assert the count and that the sense stayed inverted (rest present).
            bool[,] mask2 = TerrainHoleMask.NewFullyPresent(res);
            int punched = TerrainHoleMask.PunchCircle(mask2, 32, 32, 3);
            Check(punched > 0 && TerrainHoleMask.HoleCount(mask2) == punched
                  && TerrainHoleMask.PresentCount(mask2) == (res - 1) * (res - 1) - punched,
                "Punched quad count must equal the hole count, with all other quads still present.");
            Check(mask2[32, 32] == false && mask2[0, 0] == true,
                "The punched centre must be a hole (false) while a far quad stays present (true).");

            // Guards: a degenerate resolution has no quads; nulls are inert.
            Check(TerrainHoleMask.HoleArrayEdge(1) == 0 && TerrainHoleMask.NewFullyPresent(1).Length == 0,
                "A resolution of one or less must yield a zero-length hole array (no quads).");
        }

        private static void TestChunkPaintKey()
        {
            // A chunk at world [0,10]×[0,10] and another far away at [100,110]×[100,110].
            const float aMinX = 0f, aMinZ = 0f, aMaxX = 10f, aMaxZ = 10f;
            const float bMinX = 100f, bMinZ = 100f, bMaxX = 110f, bMaxZ = 110f;

            var empty = new System.Collections.Generic.List<PaintStroke>();
            int aEmpty = ChunkPaintKey.ChunkStrokeKey(empty, aMinX, aMinZ, aMaxX, aMaxZ);
            int bEmpty = ChunkPaintKey.ChunkStrokeKey(empty, bMinX, bMinZ, bMaxX, bMaxZ);

            // A single stroke centred on chunk A, radius 3 m: overlaps A, misses B by ~90 m.
            var stroke = new PaintStroke(5f, 5f, 3f, 1f, 2f, 0);
            Check(ChunkPaintKey.StrokeOverlapsChunk(in stroke, aMinX, aMinZ, aMaxX, aMaxZ),
                "A stroke centred inside a chunk must overlap it.");
            Check(!ChunkPaintKey.StrokeOverlapsChunk(in stroke, bMinX, bMinZ, bMaxX, bMaxZ),
                "A stroke 90 m away must not overlap a distant chunk.");

            var painted = new System.Collections.Generic.List<PaintStroke> { stroke };
            int aPainted = ChunkPaintKey.ChunkStrokeKey(painted, aMinX, aMinZ, aMaxX, aMaxZ);
            int bPainted = ChunkPaintKey.ChunkStrokeKey(painted, bMinX, bMinZ, bMaxX, bMaxZ);

            // Done-when: the one stroke changes exactly the chunk it overlaps and no other.
            Check(aPainted != aEmpty, "A stroke must change the cache key of the chunk it overlaps.");
            Check(bPainted == bEmpty, "A stroke must NOT change the cache key of a chunk it does not overlap.");

            // A stroke touching a chunk edge counts: centre 12 m out, radius 3 m reaches x=9 inside [0,10].
            var edge = new PaintStroke(12f, 5f, 3f, 1f, 2f, 0);
            Check(ChunkPaintKey.StrokeOverlapsChunk(in edge, aMinX, aMinZ, aMaxX, aMaxZ),
                "A disc reaching into the chunk rectangle must count as an overlap (circle/AABB test).");

            // Determinism and order-significance: same list twice is identical; reordering two strokes is not.
            var s2 = new PaintStroke(6f, 6f, 3f, 0.5f, 1f, 1);
            var ab = new System.Collections.Generic.List<PaintStroke> { stroke, s2 };
            var ba = new System.Collections.Generic.List<PaintStroke> { s2, stroke };
            Check(ChunkPaintKey.ChunkStrokeKey(ab, aMinX, aMinZ, aMaxX, aMaxZ)
                  == ChunkPaintKey.ChunkStrokeKey(ab, aMinX, aMinZ, aMaxX, aMaxZ),
                "The chunk paint key must be deterministic across identical inputs.");
            Check(ChunkPaintKey.ChunkStrokeKey(ab, aMinX, aMinZ, aMaxX, aMaxZ)
                  != ChunkPaintKey.ChunkStrokeKey(ba, aMinX, aMinZ, aMaxX, aMaxZ),
                "Reordering strokes must change the key, because compositing is order-dependent.");
        }

        private static void TestBrushInputModel()
        {
            // Modifier routing: no modifier drives size, the modifier drives strength (one wheel, no dialog).
            Check(BrushInputModel.ResolveTarget(false) == BrushScrollTarget.Size
                  && BrushInputModel.ResolveTarget(true) == BrushScrollTarget.Strength,
                "The scroll modifier must switch the wheel from size to strength.");

            // Size scrolls multiplicatively and clamps to [min, max] metres.
            float up = BrushInputModel.AdjustSize(10f, 1f, 1f, 100f, 1.1f);
            Check(up > 10f && Mathf.Abs(up - 11f) < 1e-3f, "A positive scroll notch must grow the brush by the factor.");
            Check(BrushInputModel.AdjustSize(100f, 50f, 1f, 100f) == 100f
                  && BrushInputModel.AdjustSize(1f, -50f, 1f, 100f) == 1f,
                "Brush size must clamp to its metre range at both ends.");

            // Strength scrolls additively and clamps to [0, 1].
            Check(Mathf.Abs(BrushInputModel.AdjustStrength(0.5f, 1f, 0.05f) - 0.55f) < 1e-4f,
                "A scroll notch must move strength by one additive step.");
            Check(BrushInputModel.AdjustStrength(0.99f, 10f) == 1f && BrushInputModel.AdjustStrength(0.01f, -10f) == 0f,
                "Brush strength must clamp to [0, 1].");

            // A stroke group is one press-to-release: start on down-edge, end on up-edge, nothing mid-drag.
            Check(BrushInputModel.IsStrokeGroupStart(false, true), "A stroke group must start on the pointer-down edge.");
            Check(BrushInputModel.IsStrokeGroupEnd(true, false), "A stroke group must end on the pointer-up edge.");
            Check(!BrushInputModel.IsStrokeGroupStart(true, true) && !BrushInputModel.IsStrokeGroupStart(false, false),
                "A held or idle pointer must not open a new undo group each frame.");
        }

        private static void TestPaintLayerResolver()
        {
            var r = new PaintLayerResolver();
            // Stable layer keys (stand-ins for TerrainLayer asset ids). A stroke targets key 20.
            r.Rebind(new System.Collections.Generic.List<int> { 10, 20, 30 });
            Check(r.ResolveIndex(20) == 1, "A stable key must resolve to its current channel index.");

            // A tier change reorders the array: identity, not index, keeps the stroke on the right layer.
            r.Rebind(new System.Collections.Generic.List<int> { 30, 20, 10 });
            Check(r.ResolveIndex(20) == 1 && r.ResolveIndex(10) == 2,
                "After a reorder, a stroke's layer must be re-resolved by identity, not left at the old index.");

            // Remove layer 20: it is HELD (resolves to −1), not dropped, and warns exactly once.
            r.Rebind(new System.Collections.Generic.List<int> { 30, 10 });
            Check(r.ResolveIndex(20) == -1, "A removed layer must resolve to −1 (stroke held, not dropped).");
            Check(r.WarnOnceForMissing(20) && !r.WarnOnceForMissing(20),
                "A missing layer must warn exactly once, not every composite pass.");

            // Re-add 20 (at a new index): its paint is RESTORED — the done-when.
            r.Rebind(new System.Collections.Generic.List<int> { 30, 10, 20 });
            Check(r.ResolveIndex(20) == 2, "Re-adding a removed layer must restore that layer's paint.");
            // And the warn mark reset, so a later removal warns afresh.
            r.Rebind(new System.Collections.Generic.List<int> { 30, 10 });
            Check(r.WarnOnceForMissing(20), "Removing a re-added layer must be able to warn again.");
        }

        private static void TestVelocityPredictor()
        {
            const float window = 0.5f, dt = 0.05f, predict = 0.5f;

            // Steady forward motion: 10 m/s along +z for a couple of seconds. The EMA converges to the travel
            // speed and the predicted point runs ahead in the direction of travel.
            var fwd = new VelocityPredictor();
            Vector3 pos = Vector3.zero;
            fwd.Reset(pos);
            for (int i = 0; i < 40; i++) { pos += new Vector3(0f, 0f, 10f * dt); fwd.Observe(pos, dt, window); }
            Check(Mathf.Abs(fwd.SmoothedVelocity.z - 10f) < 0.5f, "Steady travel must drive the EMA to the travel speed.");
            Vector3 ahead = fwd.Predict(predict);
            Check(VelocityPredictor.ShouldPrefetch(ahead, pos, 1f) && ahead.z > pos.z,
                "Steady forward motion must predict ahead and trigger a prefetch.");

            // A mouse flick: the camera rotates but does not translate, so position is unchanged frame to frame.
            // Velocity comes from translation, so the EMA stays at zero and no prefetch fires.
            var flick = new VelocityPredictor();
            flick.Reset(new Vector3(50f, 0f, 50f));
            for (int i = 0; i < 40; i++) flick.Observe(new Vector3(50f, 0f, 50f), dt, window);
            Check(flick.SmoothedVelocity.sqrMagnitude < 1e-4f, "A rotation-only flick must produce zero velocity.");
            Check(!VelocityPredictor.ShouldPrefetch(flick.Predict(predict), new Vector3(50f, 0f, 50f), 1f),
                "A fast flick with no translation must trigger no prefetch (the done-when).");

            // The EMA weight is frame-rate independent: larger dt yields a larger weight, and a degenerate window snaps.
            Check(VelocityPredictor.ExponentialAlpha(0.1f, 0.5f) > VelocityPredictor.ExponentialAlpha(0.01f, 0.5f),
                "A longer frame must fold in more of the raw velocity (frame-rate-independent EMA).");
            Check(VelocityPredictor.ExponentialAlpha(0.05f, 0f) == 1f && VelocityPredictor.ExponentialAlpha(0f, 0.5f) == 0f,
                "A zero window must snap and a zero dt must hold.");
        }

        private static void TestChunkBuildPriority()
        {
            const float near = 100f;

            // The four bands, best to worst.
            Check(ChunkBuildPriority.Classify(true, true, 50f, near) == BuildBand.InFrustumNear
                  && ChunkBuildPriority.Classify(true, true, 500f, near) == BuildBand.InFrustumFar
                  && ChunkBuildPriority.Classify(false, true, 500f, near) == BuildBand.AlongPredictedPath
                  && ChunkBuildPriority.Classify(false, false, 50f, near) == BuildBand.Elsewhere,
                "Chunks must classify into frustum-near, frustum-far, predicted-path, then elsewhere.");

            // A near chunk in front beats a near chunk behind, regardless of distance.
            Check(ChunkBuildPriority.Compare(BuildBand.InFrustumNear, 90f, BuildBand.Elsewhere, 10f) < 0,
                "A far-in-front chunk must still outrank a very-near chunk behind the camera.");
            // Within a band, nearer builds first.
            Check(ChunkBuildPriority.Compare(BuildBand.InFrustumNear, 10f, BuildBand.InFrustumNear, 20f) < 0,
                "Within a band, the nearer chunk must build first.");

            // Turning 180° re-prioritises: chunk A was in front & near, chunk B was behind. After the turn A is
            // behind (elsewhere) and B is in front & near — re-sorting flips their order.
            var list = new System.Collections.Generic.List<ChunkBuildPriority.Candidate>
            {
                new ChunkBuildPriority.Candidate(BuildBand.InFrustumNear, 20f), // A, before turn
                new ChunkBuildPriority.Candidate(BuildBand.Elsewhere, 30f),     // B, before turn
            };
            list.Sort(ChunkBuildPriority.CandidateComparer.Instance);
            Check(list[0].Band == BuildBand.InFrustumNear, "Before the turn, the in-front chunk sorts first.");

            // Reclassify against the turned-around frustum and re-sort.
            var turned = new System.Collections.Generic.List<ChunkBuildPriority.Candidate>
            {
                new ChunkBuildPriority.Candidate(ChunkBuildPriority.Classify(false, false, 20f, near), 20f), // A now behind
                new ChunkBuildPriority.Candidate(ChunkBuildPriority.Classify(true, true, 30f, near), 30f),   // B now in front
            };
            turned.Sort(ChunkBuildPriority.CandidateComparer.Instance);
            Check(turned[0].Band == BuildBand.InFrustumNear && turned[0].DistanceM == 30f,
                "After a 180° turn, the newly-in-front chunk must be re-prioritised to the head of the queue.");
        }

        private static void TestLoadHysteresis()
        {
            const float loadR = 200f, cell = 32f;
            float hyst = LoadHysteresis.DefaultHysteresis(cell);   // 1.25 × 32 = 40
            Check(Mathf.Abs(hyst - 40f) < 1e-4f, "Default unload hysteresis must be 1.25 × cell size.");
            Check(Mathf.Abs(LoadHysteresis.UnloadRadius(loadR, hyst) - 240f) < 1e-4f,
                "The unload radius must be the load radius plus the hysteresis.");

            // A non-resident chunk inside the load radius loads; a resident chunk past the unload radius unloads.
            Check(LoadHysteresis.Decide(150f, loadR, hyst, false) == ResidencyAction.Load,
                "A non-resident chunk within the load radius must load.");
            Check(LoadHysteresis.Decide(300f, loadR, hyst, true) == ResidencyAction.Unload,
                "A resident chunk beyond the unload radius must unload.");

            // Park on the boundary and jitter within the dead band: a resident chunk stays, a non-resident stays.
            // Simulate a camera nudging across the load radius (200) by ±5 m, well inside the 40 m dead band.
            int cycles = 0;
            bool resident = true;   // it loaded when the camera first arrived
            float[] jitter = { 198f, 202f, 199f, 203f, 201f, 197f, 205f, 200f };
            foreach (float d in jitter)
            {
                var a = LoadHysteresis.Decide(d, loadR, hyst, resident);
                if (a == ResidencyAction.Load) { resident = true; cycles++; }
                else if (a == ResidencyAction.Unload) { resident = false; cycles++; }
            }
            Check(cycles == 0 && resident,
                "Jittering across the load boundary inside the dead band must cause zero load/unload cycles.");

            // Only leaving the dead band entirely acts.
            Check(LoadHysteresis.Decide(241f, loadR, hyst, true) == ResidencyAction.Unload
                  && LoadHysteresis.Decide(239f, loadR, hyst, true) == ResidencyAction.None,
                "A resident chunk must unload only once it clears the unload radius, not within the band.");
        }

        private static void TestTeleportDetector()
        {
            const float threshold = 500f;

            // A normal fast run (a few metres per frame) is not a teleport; a fast-travel warp is.
            Check(!TeleportDetector.IsTeleport(Vector3.zero, new Vector3(0f, 0f, 8f), threshold),
                "A normal per-frame step must not read as a teleport.");
            Check(TeleportDetector.IsTeleport(Vector3.zero, new Vector3(0f, 0f, 5000f), threshold),
                "A fast-travel warp beyond the threshold must read as a teleport.");

            // The detector advances its reference each frame, so it measures from where the camera now is.
            var d = new TeleportDetector();
            d.Reset(Vector3.zero);
            Check(!d.Observe(new Vector3(0f, 0f, 10f), threshold), "A small move must not be flagged a teleport.");
            Check(d.Observe(new Vector3(0f, 0f, 5010f), threshold), "The warp from the last position must be flagged.");
            Check(!d.Observe(new Vector3(0f, 0f, 5020f), threshold),
                "After the warp, a normal step from the NEW position must not re-flag a teleport.");

            // Refill is radial: the near ring resolves before any distant chunk.
            Check(TeleportDetector.CompareByRadius(30f, 300f) < 0,
                "Radial refill must order the near ring before distant chunks around the new position.");
        }

        private static void TestTierConcurrencyGate()
        {
            // The per-tier caps: 1 / 2 / 4 / 6, clamped outside 0..3.
            Check(TierConcurrencyGate.CapForTier(0) == 1 && TierConcurrencyGate.CapForTier(1) == 2
                  && TierConcurrencyGate.CapForTier(2) == 4 && TierConcurrencyGate.CapForTier(3) == 6,
                "The tier concurrency caps must be 1 / 2 / 4 / 6.");
            Check(TierConcurrencyGate.CapForTier(-5) == 1 && TierConcurrencyGate.CapForTier(99) == 6,
                "The tier cap must clamp outside the 0..3 range.");

            // Stress: request 50 builds at once on tier 2 (cap 4). Simulate admit-then-complete and assert the
            // in-flight count never exceeds the cap and the peak equals it.
            var gate = TierConcurrencyGate.ForTier(2);
            int pending = 50, completed = 0;
            bool everExceeded = false;
            int guard = 0;
            while ((pending > 0 || gate.InFlight > 0) && guard++ < 10000)
            {
                while (pending > 0 && gate.TryBegin()) pending--;   // admit up to the cap
                if (gate.InFlight > gate.Cap) everExceeded = true;
                if (gate.InFlight > 0) { gate.End(); completed++; }  // one build finishes this pass
            }
            Check(!everExceeded && gate.PeakInFlight == 4,
                "Fifty simultaneous requests must never exceed the tier's cap of 4.");
            Check(completed == 50 && gate.InFlight == 0, "Every requested build must eventually complete and drain.");

            // A full gate refuses further admissions until a slot frees.
            var g2 = new TierConcurrencyGate(2);
            Check(g2.TryBegin() && g2.TryBegin() && !g2.TryBegin(),
                "A full gate must refuse admission until a slot frees.");

            // Cancellation drains to zero and leaves the cap intact for an immediate refill.
            g2.CancelAll();
            Check(g2.InFlight == 0 && g2.Available == 2 && g2.TryBegin(),
                "Cancellation must drain in-flight to zero with no orphaned count, and the ring must refill.");

            // Debounce coalesces a burst: under 100 ms not ready, at/over 100 ms ready.
            Check(!TierConcurrencyGate.DebounceElapsed(0f, 50f) && TierConcurrencyGate.DebounceElapsed(0f, 100f),
                "The 100 ms debounce must coalesce a burst of requests.");
        }

        private static void TestGeometryBudget()
        {
            // The rolling median: odd window returns the middle, even the mean of the two middles.
            var m = new RollingMedian(5);
            m.Add(1f); m.Add(2f); m.Add(3f); m.Add(4f); m.Add(5f);
            Check(m.Median() == 3f, "The median of 1..5 must be 3.");
            var e = new RollingMedian(4);
            e.Add(1f); e.Add(2f); e.Add(3f); e.Add(4f);
            Check(Mathf.Abs(e.Median() - 2.5f) < 1e-4f, "The median of an even window is the mean of the two middles.");

            // Spike immunity: a full window at 16.6 ms plus one 200 ms spike leaves the median at 16.6 ms.
            var f = new RollingMedian(15);
            for (int i = 0; i < 15; i++) f.Add(16.6f);
            f.Add(200f);
            Check(Mathf.Abs(f.Median() - 16.6f) < 1e-3f, "A single spike frame must not move the rolling median.");

            // The budget: a steady over-ceiling frame time reports over budget; a steady healthy window does not,
            // even with one spike frame in it.
            var over = new GeometryBudget(15, 16.6f, 1_000_000f, 2000f, 1024f);
            for (int i = 0; i < 15; i++) over.Sample(30f, 500_000f, 1000f, 512f);
            Check(over.IsOverBudget(), "A sustained over-ceiling frame time must report over budget.");

            var healthy = new GeometryBudget(15, 33.3f, 1_000_000f, 2000f, 1024f);
            for (int i = 0; i < 14; i++) healthy.Sample(16.6f, 500_000f, 1000f, 512f);
            healthy.Sample(300f, 500_000f, 1000f, 512f);   // one spike frame
            Check(!healthy.IsOverBudget(), "A single spike frame in an otherwise-healthy window must not breach the budget.");
        }

        private static void TestDegradationLadder()
        {
            var ladder = new DegradationLadder();
            Check(ladder.AtTop && !ladder.AtBottom, "A fresh ladder must start undegraded at the top.");

            // Forcing a breach walks the list in order, one step each, and a stem count handed alongside is
            // provably untouched (the done-when).
            const int stemCount = 250_000;
            int stemCountAfter = stemCount;   // the ladder must never change this
            var expected = new DegradeStep[]
            {
                DegradeStep.AcuityPixels, DegradeStep.OverlayRingRow, DegradeStep.ImpostorSwitchDistance,
                DegradeStep.PropShadowDistance, DegradeStep.CacheRangeRing, DegradeStep.OverlayTessellation,
                DegradeStep.OverlayDisabled,
            };
            int logLines = 0;
            for (int i = 0; i < DegradationLadder.StepCount; i++)
            {
                bool stepped = ladder.TryStepDown(out DegradeStep applied);
                Check(stepped && applied == expected[i], "The ladder must descend in the exact published order.");
                Check(!DegradationLadder.AffectsStemCount(applied), "No degradation step may change the stem count.");
                logLines++;   // one log line per step
            }
            Check(logLines == 7 && stemCountAfter == stemCount,
                "Walking the ladder must log once per step and leave the stem count unchanged.");

            // At the bottom, stepping down does nothing (no extra log line).
            Check(ladder.AtBottom && !ladder.TryStepDown(out _),
                "At the bottom the ladder must not step further.");

            // Recovery reverses the order, one step at a time.
            Check(ladder.TryStepUp(out DegradeStep removed) && removed == DegradeStep.OverlayDisabled,
                "Recovery must reverse the order, undoing the last step first.");

            // The interval gates one step per degradeIntervalSeconds.
            Check(!DegradationLadder.IntervalElapsed(0f, 0.5f, 1f) && DegradationLadder.IntervalElapsed(0f, 1f, 1f),
                "Degradation must advance at most one step per interval.");
        }

        private static void TestRecoveryController()
        {
            const float ceiling = 16.6f;
            var rc = new RecoveryController(5f, 0.8f);
            Check(Mathf.Abs(rc.MarginThreshold(ceiling) - 0.8f * ceiling) < 1e-4f,
                "The recovery margin must be recoverThreshold × ceiling (0.8 × ceiling).");

            // Under the margin for four seconds is not yet enough; the fifth second permits recovery.
            float under = 0.7f * ceiling;
            bool permitted = false;
            for (int i = 0; i < 4; i++) permitted = rc.Observe(1f, under, ceiling);
            Check(!permitted, "Recovery must not begin before the full hold under the margin.");
            permitted = rc.Observe(1f, under, ceiling);
            Check(permitted, "Recovery must be permitted once the hold under the margin completes.");

            // An excursion above the margin mid-hold resets the hold — a flickering load cannot creep back up.
            var rc2 = new RecoveryController(5f, 0.8f);
            rc2.Observe(1f, under, ceiling); rc2.Observe(1f, under, ceiling); rc2.Observe(1f, under, ceiling);
            rc2.Observe(1f, 0.9f * ceiling, ceiling);   // back above the margin
            Check(rc2.HeldSeconds == 0f && !rc2.Observe(1f, under, ceiling),
                "An excursion above the margin must reset the recovery hold to zero.");

            // Dead band [margin, ceiling]: neither recovers (not under margin) nor degrades (not over ceiling).
            var rc3 = new RecoveryController(5f, 0.8f);
            for (int i = 0; i < 10; i++) rc3.Observe(1f, 0.9f * ceiling, ceiling);
            Check(rc3.HeldSeconds == 0f, "A metric in the dead band must never accumulate recovery hold.");

            // A transient breach that pushed the ladder down two steps recovers fully within fifteen seconds
            // with no step-down (no oscillation): 5 s hold + one step per 1 s interval = ~7 s.
            var ladder = new DegradationLadder();
            ladder.TryStepDown(out _); ladder.TryStepDown(out _);   // two-step transient breach
            var rc4 = new RecoveryController(5f, 0.8f);
            float t = 0f, lastStep = -100f; int stepDowns = 0;
            while (!ladder.AtTop && t < 15f)
            {
                t += 1f;
                bool ok = rc4.Observe(1f, under, ceiling);
                // Load is under the margin throughout, so the ladder is never pushed down during recovery.
                if (ok && DegradationLadder.IntervalElapsed(lastStep, t, 1f))
                {
                    if (ladder.TryStepUp(out _)) lastStep = t;
                }
            }
            Check(ladder.AtTop && t <= 15f && stepDowns == 0,
                "A transient breach must recover fully within fifteen seconds with no oscillation.");
        }

        private static void TestThermalGovernor()
        {
            // Drift maths.
            Check(Mathf.Abs(ThermalGovernor.DriftFraction(20f, 16f) - 0.25f) < 1e-4f,
                "Drift must be (current − baseline) / baseline.");
            Check(ThermalGovernor.DriftFraction(20f, 0f) == 0f, "A non-positive baseline must yield zero drift.");

            var g = new ThermalGovernor(0.15f);
            // Record a cool baseline at a fixed complexity early in the session.
            g.Observe(500_000f, 1000f, 16.6f);

            // Same complexity, frame time drifted up 20% > 15% threshold → throttling → step down.
            Check(g.ShouldStepDown(500_000f, 1000f, 20f),
                "A 20% frame-time drift at matched complexity must signal a thermal step-down.");

            // A higher frame time at a HEAVIER scene (different bucket, no baseline) is not throttling.
            Check(!g.ShouldStepDown(2_000_000f, 4000f, 40f),
                "More work at higher complexity must not be mistaken for thermal drift.");

            // Step once, then hold: after marking the step-down, persisting drift does not fire again.
            g.MarkSteppedDown();
            Check(!g.ShouldStepDown(500_000f, 1000f, 22f) && g.HasSteppedDown,
                "After a thermal step-down the governor must hold, not sawtooth.");

            // Simulated ten-minute sustained run (coarse ticks): cool baseline, drift climbs and holds; the
            // governor steps down exactly once.
            var g2 = new ThermalGovernor(0.15f);
            int stepDowns = 0;
            for (int minute = 0; minute < 10; minute++)
            {
                float median = minute < 2 ? 16.6f : 16.6f * (1f + 0.05f * minute);   // clocks drift up under load
                if (minute < 2) g2.Observe(500_000f, 1000f, median);                 // early cool readings
                if (g2.ShouldStepDown(500_000f, 1000f, median)) { g2.MarkSteppedDown(); stepDowns++; }
            }
            Check(stepDowns == 1, "A ten-minute sustained-load run must step down exactly once and hold.");
        }

        /// <summary>
        /// The advisory governor status line (§ 9.10 G-66).
        ///
        /// The done-when is verbatim: the status must read "Tier: Mobile High
        /// (auto-reduced from Desktop — thermal drift 18 %)", and an author who
        /// pins the tier must defeat the auto-reduction (the effective tier becomes
        /// the authored one). The checks pin the tier display names, the pin rule,
        /// and the exact string in both the reduced and the not-reduced case.
        /// </summary>
        private static void TestGovernorStatus()
        {
            // Tier display names.
            Check(GovernorStatus.DisplayName(SplatCacheTier.MobileLow) == "Mobile Low", "MobileLow must render as \"Mobile Low\".");
            Check(GovernorStatus.DisplayName(SplatCacheTier.MobileHigh) == "Mobile High", "MobileHigh must render as \"Mobile High\".");
            Check(GovernorStatus.DisplayName(SplatCacheTier.Desktop) == "Desktop", "Desktop must render as \"Desktop\".");
            Check(GovernorStatus.DisplayName(SplatCacheTier.Ultra) == "Ultra", "Ultra must render as \"Ultra\".");

            // A pin defeats the auto-reduction: pinned → effective is the AUTHORED tier, not the reduced one.
            Check(GovernorStatus.EffectiveTier(SplatCacheTier.Desktop, SplatCacheTier.MobileHigh, true) == SplatCacheTier.Desktop,
                "A pinned tier must defeat the auto-reduction (effective = authored).");
            Check(GovernorStatus.EffectiveTier(SplatCacheTier.Desktop, SplatCacheTier.MobileHigh, false) == SplatCacheTier.MobileHigh,
                "When not pinned, the effective tier is the auto-reduced one.");

            // The verbatim done-when status line, thermal-drift case.
            Check(GovernorStatus.Format(SplatCacheTier.MobileHigh, SplatCacheTier.Desktop, true, ReductionReason.ThermalDrift, 18f)
                == "Tier: Mobile High (auto-reduced from Desktop — thermal drift 18 %)",
                "The auto-reduced thermal status must read the verbatim done-when line.");

            // Over-budget case names the reason without a drift figure.
            Check(GovernorStatus.Format(SplatCacheTier.MobileLow, SplatCacheTier.Ultra, true, ReductionReason.Budget, 0f)
                == "Tier: Mobile Low (auto-reduced from Ultra — over budget)",
                "The over-budget status must name the budget reason.");

            // Not auto-reduced (or active == authored): the bare tier line, no attribution.
            Check(GovernorStatus.Format(SplatCacheTier.Desktop, SplatCacheTier.Desktop, false, ReductionReason.None, 0f)
                == "Tier: Desktop",
                "An un-reduced tier must render as a bare \"Tier: …\" line.");
        }

        /// <summary>
        /// Nothing in § 9.10 degradation may write to TerrainData (G-67, C-04, G-17).
        ///
        /// The done-when: degrade to the bottom of the G-63 ladder and assert every
        /// TerrainData channel hash is unchanged. Because a step could only alter the
        /// world by writing heights, splats, holes or trees, a fingerprint of those four
        /// channels taken before and after a FULL descent must be bit-identical. The
        /// check also pins that no step reports itself as a TerrainData writer, and that
        /// a step does move the view-side state (so the descent is real, not a no-op).
        /// </summary>
        private static void TestViewSideDegradation()
        {
            // A small fixed world. The degradation loop never receives these arrays, so it cannot write them.
            var heights = new float[] { 0.10f, 0.42f, 0.75f, 0.33f };
            var splats  = new int[]   { 0, 1, 2, 1 };
            var holes   = new bool[]  { true, true, false, true };
            var trees   = new int[]   { 5, 9, 2 };

            int before = ViewSideDegradation.TerrainDataFingerprint(heights, splats, holes, trees);

            // Descend the entire G-63 ladder, applying each step to VIEW state only.
            var ladder = new DegradationLadder();
            var view = ViewSideState.Default;
            var viewAtTop = view;
            int steps = 0;
            while (ladder.TryStepDown(out DegradeStep step))
            {
                Check(!ViewSideDegradation.WritesTerrainData(step),
                    "No degradation step may write TerrainData.");
                view = ViewSideDegradation.Apply(in view, step);
                steps++;
            }
            Check(steps == DegradationLadder.StepCount && ladder.AtBottom,
                "The descent must reach the bottom of the G-63 ladder.");

            int after = ViewSideDegradation.TerrainDataFingerprint(heights, splats, holes, trees);
            Check(after == before,
                "Degrading to the bottom must leave every TerrainData channel hash unchanged (G-67).");

            // The descent must actually have changed the view side (otherwise the invariant is vacuous).
            Check(!view.OverlayEnabled && view.AcuityPixels > viewAtTop.AcuityPixels,
                "A full descent must change the view-side state, not the world.");

            // Every enumerated step is view-side, including any not reached above.
            foreach (DegradeStep s in System.Enum.GetValues(typeof(DegradeStep)))
                Check(!ViewSideDegradation.WritesTerrainData(s), "Every degradation step must be view-side.");
        }

        /// <summary>
        /// The authored geometry settings block (§ 9.11 G-68).
        ///
        /// The done-when is inspector wording plus one structural rule: every derived
        /// distance is derived from the acuity value that produced it. These checks pin
        /// that rule — the Derived* accessors equal the angular-error law evaluated at the
        /// same acuity, and raising acuity (coarser tolerance) pulls every derived distance
        /// in together — and that the shipped defaults are sane. The "no field unlabelled"
        /// half is the custom inspector drawer, an editor concern deferred to the G-topic gate.
        /// </summary>
        private static void TestGeometrySettings()
        {
            var s = new GeometrySettings();

            // Defaults are sane and positive where a positive value is required.
            Check(s.acuityPixels >= 1f && s.frameBudgetMs > 0f && s.triangleCeiling > 0f && s.loadRadiusM > 0f,
                "GeometrySettings defaults must be sane and positive.");

            // Each derived distance is exactly the angular-error law at this acuity — derived, never authored twice.
            Check(Mathf.Abs(s.DerivedTreeDrawDistanceM
                    - AngularError.FeatureCullDistanceReference(s.treeFeatureSizeM, s.acuityPixels)) < 1e-3f,
                "The tree draw distance must be derived from acuity via the angular-error law.");
            Check(Mathf.Abs(s.DerivedDetailDrawDistanceM
                    - AngularError.FeatureCullDistanceReference(s.detailFeatureSizeM, s.acuityPixels)) < 1e-3f,
                "The detail draw distance must be derived from acuity.");
            Check(Mathf.Abs(s.DerivedBasemapDistanceM
                    - AngularError.FeatureCullDistanceReference(s.basemapFeatureSizeM, s.acuityPixels)) < 1e-3f,
                "The basemap distance must be derived from acuity.");

            // Larger features are visible farther than smaller ones at the same acuity.
            Check(s.DerivedTreeDrawDistanceM > s.DerivedDetailDrawDistanceM,
                "A tree must be resolvable farther than a grass clump at equal acuity.");

            // Raising acuity (coarser tolerance) pulls the derived distance IN, for every feature at once.
            float treeFine = s.DerivedTreeDrawDistanceM;
            s.acuityPixels = 4f;   // Mobile Low tolerance
            Check(s.DerivedTreeDrawDistanceM < treeFine,
                "Coarser acuity must pull the derived draw distance in.");

            // The streaming unload radius is the load radius plus its hysteresis dead band (G-59).
            Check(Mathf.Abs(s.DerivedUnloadRadiusM - (s.loadRadiusM + s.unloadHysteresisM)) < 1e-3f,
                "The unload radius must be load radius plus the hysteresis dead band.");
        }

        /// <summary>
        /// The per-cell hysteresis gate on the pixel-error write.
        ///
        /// The claim under test is the done-when for the write cost: a camera holding
        /// still writes the property zero times per frame. Assigning heightmapPixelError
        /// rebuilds the patch LOD tree, so a value that has not crossed the hysteresis
        /// band must not be re-written. The checks also pin two failure modes that a
        /// naive gate falls into - comparing against a global last-value instead of a
        /// per-cell one, and letting skipped sub-band frames quietly advance the baseline.
        /// </summary>
        private static void TestTerrainDensityController()
        {
            var c = new TerrainDensityController();
            const float fov = 60f;
            const float dist = 100f;
            const float hyst = 0.75f;

            // texel 0 and floor 0 make PixelErrorForTerrain pass the acuity budget through
            // unclamped, so the applied error equals acuityPixels and the band arithmetic
            // is exercised on a value we control directly.

            // Frame 1: first resolve of a cell always writes, establishing the baseline.
            c.BeginFrame();
            bool w1 = c.TryResolve(0, 0, 2.0f, 0f, dist, fov, 1080, 0f, hyst, out float e1);
            Check(w1 && Mathf.Abs(e1 - 2.0f) < 1e-4f && c.WritesThisFrame == 1,
                "First resolve of a cell must write the baseline error.");

            // Frame 2: camera still, identical inputs - the done-when: zero writes.
            c.BeginFrame();
            bool w2 = c.TryResolve(0, 0, 2.0f, 0f, dist, fov, 1080, 0f, hyst, out _);
            Check(!w2 && c.WritesThisFrame == 0,
                "A still camera must write the pixel error zero times per frame.");

            // Frame 3: a sub-band change (0.5 px < 0.75 px band) is still skipped, and the
            // skip must NOT advance the baseline - otherwise creep accumulates unnoticed.
            c.BeginFrame();
            bool w3 = c.TryResolve(0, 0, 2.5f, 0f, dist, fov, 1080, 0f, hyst, out _);
            Check(!w3 && c.WritesThisFrame == 0,
                "A change within the hysteresis band must be skipped.");

            // Frame 4: 3.0 vs the last WRITTEN 2.0 is 1.0 px > band, so it writes. Comparing
            // against 2.5 (the skipped frame) would read 0.5 and wrongly skip - this pins it.
            c.BeginFrame();
            bool w4 = c.TryResolve(0, 0, 3.0f, 0f, dist, fov, 1080, 0f, hyst, out _);
            Check(w4 && c.WritesThisFrame == 1,
                "A change past the band from the last WRITTEN value must write (skips do not move the baseline).");

            // Per-cell, not global: a fresh cell writes even while cell (0,0) is settled.
            c.BeginFrame();
            bool wNew = c.TryResolve(5, 5, 3.0f, 0f, dist, fov, 1080, 0f, hyst, out _);
            Check(wNew && c.WritesThisFrame == 1 && c.TrackedCellCount == 2,
                "A different cell keeps its own baseline (the gate is per-cell, not global).");

            // Cell keys must not collide across the sign boundary or the axis swap.
            Check(TerrainDensityController.CellKey(-1, 0) != TerrainDensityController.CellKey(0, -1)
                  && TerrainDensityController.CellKey(1, 0) != TerrainDensityController.CellKey(0, 1)
                  && TerrainDensityController.CellKey(0, 0) != TerrainDensityController.CellKey(-1, -1),
                "CellKey must be collision-free across negative coordinates and axis swaps.");

            // Forget drops the baseline so re-entry re-establishes it with a write.
            c.Forget(0, 0);
            c.BeginFrame();
            bool wReentry = c.TryResolve(0, 0, 3.0f, 0f, dist, fov, 1080, 0f, hyst, out _);
            Check(wReentry,
                "After Forget, a cell's next resolve must write a fresh baseline.");
        }

        /// <summary>
        /// The four native draw distances (basemap, detail, tree, tree-billboard) come off
        /// the one acuity budget, not four authored numbers.
        ///
        /// The claim under test is the done-when: moving the single authored acuity value
        /// moves all four derived distances together. Each is the range at which its own
        /// real-world feature drops below the budget, so they must (a) round-trip through
        /// the same metres-per-pixel projection the density core uses, (b) all scale by the
        /// same factor when the budget changes - halve the tolerance, halve every distance -
        /// and (c) fall in the physical order billboard &lt; detail &lt; tree &lt; basemap.
        /// A regression that re-introduces one hand-tuned distance breaks one of these.
        /// </summary>
        private static void TestDerivedDrawDistances()
        {
            // (a) The derived distance is genuinely the inverse of the projection: a feature
            // sitting at its cull distance reads back as exactly the budget in pixels.
            float d = AngularError.FeatureCullDistanceReference(1f, 2f);
            float px = AngularError.ScreenErrorPixels(1f, d, AngularError.ReferenceVerticalFovDeg,
                                                      AngularError.ReferenceScreenHeightPixels);
            Check(Mathf.Abs(px - 2f) < 1e-3f,
                "FeatureCullDistanceReference must invert MetresPerPixel (a feature at the cutoff reads as the budget).");

            // A degenerate feature or budget fails closed to zero, never NaN.
            Check(AngularError.FeatureCullDistanceReference(0f, 1f) == 0f,
                "A zero-size feature must yield a zero cull distance, not NaN.");

            // (b) + (c) on the real object: the one dial moves all four fields together.
            var go = new GameObject("UC_SelfTest_DrawDistances");
            try
            {
                var uc = go.AddComponent<UniverseCreatorObject>();

                uc.acuityPixels = 1f;
                uc.RecomputeDerivedDistances();
                float bm1 = uc.basemapDistance, dt1 = uc.detailObjectDistance;
                float tr1 = uc.treeDistance, bb1 = uc.treeBillboardDistance;

                Check(bb1 > 0f && dt1 > bb1 && tr1 > dt1 && bm1 > 0f,
                    "Derived distances must be positive and ordered billboard < detail < tree.");
                Check(bb1 < tr1,
                    "The billboard swap must happen nearer than the full tree cull.");

                uc.acuityPixels = 2f;
                uc.RecomputeDerivedDistances();

                // Coarser tolerance pulls every distance in, and by the SAME factor - the
                // signature of one shared model rather than four independent numbers.
                Check(uc.basemapDistance < bm1 && uc.detailObjectDistance < dt1
                      && uc.treeDistance < tr1 && uc.treeBillboardDistance < bb1,
                    "Raising the acuity budget must pull ALL four derived distances in together.");
                Check(Mathf.Abs(uc.basemapDistance * 2f - bm1) < 1e-2f
                      && Mathf.Abs(uc.detailObjectDistance * 2f - dt1) < 1e-2f
                      && Mathf.Abs(uc.treeDistance * 2f - tr1) < 1e-2f
                      && Mathf.Abs(uc.treeBillboardDistance * 2f - bb1) < 1e-2f,
                    "Doubling the acuity budget must halve every derived distance (one shared model).");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The draw-instanced pairing (L-49 neighbourhood; the Unity 6 instancing trap).
        ///
        /// The claim under test is the done-when that keeps the ground from vanishing:
        /// toggling drawInstanced can never switch instancing on for a material that does
        /// not declare the transform array. TerrainBuilder.ResolveDrawInstanced is the one
        /// place the two are paired, so the whole truth table is pinned here - the only
        /// true cell is (author wants it) AND (material supports it); every other
        /// combination, above all "wanted but unsupported", must fail closed to false.
        /// </summary>
        private static void TestDrawInstancedPairing()
        {
            Check(TerrainBuilder.ResolveDrawInstanced(true, true),
                "Instancing must be on only when the author wants it AND the material supports it.");
            Check(!TerrainBuilder.ResolveDrawInstanced(true, false),
                "Wanting instancing on a material that cannot handle it must fail closed to false (the invisible-terrain guard).");
            Check(!TerrainBuilder.ResolveDrawInstanced(false, true),
                "Not wanting instancing must stay off even where the material could support it.");
            Check(!TerrainBuilder.ResolveDrawInstanced(false, false),
                "Neither wanted nor supported must be off.");
        }

        /// <summary>
        /// The per-tier acuity caps (L-49) and the single dial that carries them.
        ///
        /// The claim under test is the done-when: the four device classes differ in
        /// exactly one authored number - the acuity budget - plus the § 9.12 budget
        /// table. So the AcuityTier table must read back the table's own values
        /// (Mobile Low 4, Mobile High 2, Desktop 1, flagship 1), the coarser classes
        /// must tolerate MORE error not less, and - the tie to G-05 - applying a class
        /// must move the whole draw-distance envelope through that one value: four
        /// times the tolerated pixels means a quarter of the draw distance.
        /// </summary>
        private static void TestAcuityTier()
        {
            // The table matches § 9.12 exactly. A drift here is a drift from the budget
            // the manual sells against.
            Check(AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.Low) == 4f,
                "Mobile Low (Low tier) must cap acuity at 4 px per § 9.12.");
            Check(AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.Medium) == 2f,
                "Mobile High (Medium tier) must cap acuity at 2 px per § 9.12.");
            Check(AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.High) == 1f,
                "Desktop (High tier) must cap acuity at 1 px per § 9.12.");
            Check(AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.Ultra) == 1f,
                "Flagship (Ultra tier) shares Desktop's 1 px acuity; its extra budget is triangles, not a finer eye.");

            // Coarser class => more tolerated error, monotonically, down to the shared
            // Desktop/flagship floor. Ordering, not just values, so a re-order fails.
            Check(AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.Low)
                    > AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.Medium)
                  && AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.Medium)
                    > AcuityTier.Resolve(Optimization.AdaptiveQuality.QualityTier.High),
                "Acuity must loosen monotonically as the class weakens (Low > Medium > High).");

            // Absent a tier sampler, geometry must not break: it defaults to Desktop.
            Check(AcuityTier.Resolve((Optimization.AdaptiveQuality)null) == 1f,
                "With no AdaptiveQuality present, the acuity budget must default to Desktop's 1 px.");

            // The tie to G-05: choosing a class moves ALL the distances, and by the
            // reciprocal of the acuity ratio. Mobile Low (4) is a quarter of Desktop (1).
            var go = new GameObject("UC_SelfTest_AcuityTier");
            try
            {
                var uc = go.AddComponent<UniverseCreatorObject>();

                uc.ApplyAcuityTier(Optimization.AdaptiveQuality.QualityTier.High);
                float treeDesktop = uc.treeDistance;

                uc.ApplyAcuityTier(Optimization.AdaptiveQuality.QualityTier.Low);
                float treeMobileLow = uc.treeDistance;

                Check(treeMobileLow < treeDesktop,
                    "A weaker class must pull the draw distances in (the tier moves the whole envelope).");
                Check(Mathf.Abs(treeMobileLow * 4f - treeDesktop) < 1e-1f,
                    "Mobile Low's four-pixel budget must be a quarter of Desktop's draw distance (one shared dial).");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The neighbour-weld invariant that keeps one grid from spamming the console
        /// (L-50 seam handling; the Unity 6 auto-connect trap).
        ///
        /// The claim under test is G-08's first done-when: every generated cell leaves
        /// Unity's own <c>allowAutoConnect</c> OFF. It defaults ON for a fresh Terrain
        /// and, left on, re-links cells by world position on its own schedule - undoing
        /// the SetNeighbors links StitchGrid just made AND re-emitting the "different
        /// heightmap resolution" warning per repaint, which is where the dozens-per-frame
        /// spam came from. EnsureRenderable is the one place that flag is pinned, so the
        /// test flips it back on and proves EnsureRenderable drives it off again.
        /// </summary>
        private static void TestAutoConnectDisabled()
        {
            var go = new GameObject("UC_SelfTest_AutoConnect");
            var data = new TerrainData { heightmapResolution = 129 };
            try
            {
                var terrain = go.AddComponent<Terrain>();
                terrain.terrainData = data;

                // A fresh component defaults allowAutoConnect ON; force the worst case so
                // the assertion proves EnsureRenderable actively turns it off, not that it
                // merely happened to start off.
                terrain.allowAutoConnect = true;

                TerrainBuilder.EnsureRenderable(terrain, null);
                Check(terrain.allowAutoConnect == false,
                    "EnsureRenderable must leave allowAutoConnect == false on every generated cell (auto-connect fights StitchGrid and re-spams the resolution-mismatch warning).");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(data);
            }
        }

        /// <summary>
        /// The origin clamp (float-precision guard).
        ///
        /// The claim under test is G-09's done-when: a world far from the origin is
        /// reported, not silently tolerated, and the threshold is the authored
        /// <c>maxWorldOriginDistance</c> field rather than a hardcoded number. The math is
        /// horizontal only - lifting a world on Y keeps float precision, so height must not
        /// count as origin distance - and the predicate must re-flag a nearer world once the
        /// clamp is tightened, proving it actually reads the field.
        /// </summary>
        private static void TestOriginClamp()
        {
            var go = new GameObject("UC_SelfTest_Origin");
            try
            {
                var uc = go.AddComponent<UniverseCreatorObject>();
                uc.maxWorldOriginDistance = 20000f;

                // At the origin: zero distance, never flagged. A freshly created world lands here.
                go.transform.position = Vector3.zero;
                Check(Mathf.Abs(uc.OriginDistanceMetres()) < 1e-3f && !uc.IsFarFromOrigin(),
                    "A world at the origin must read zero distance and never be flagged as far.");

                // Height alone must not trip it - only horizontal spread costs precision.
                go.transform.position = new Vector3(0f, 500000f, 0f);
                Check(Mathf.Abs(uc.OriginDistanceMetres()) < 1e-3f && !uc.IsFarFromOrigin(),
                    "Lifting a world vertically must not count as origin distance (Y keeps float precision).");

                // Just inside stays quiet; past the clamp trips, at the true XZ distance.
                go.transform.position = new Vector3(19000f, 0f, 0f);
                Check(!uc.IsFarFromOrigin(),
                    "A root within maxWorldOriginDistance must not be flagged.");
                go.transform.position = new Vector3(21000f, 0f, 0f);
                Check(uc.IsFarFromOrigin() && Mathf.Abs(uc.OriginDistanceMetres() - 21000f) < 1e-1f,
                    "A root past maxWorldOriginDistance must be flagged, at its true XZ distance.");

                // The threshold is the authored field: tightening it re-flags a nearer world
                // the default would have passed. This is what pins it to the field, not a number.
                go.transform.position = new Vector3(5000f, 0f, 0f);
                Check(!uc.IsFarFromOrigin(),
                    "5 km is within the default 20 km clamp.");
                uc.maxWorldOriginDistance = 1000f;
                Check(uc.IsFarFromOrigin(),
                    "The clamp reads maxWorldOriginDistance, so tightening it must re-flag a nearer world.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The geometry debug window's readout is honest and locale-stable. The window itself is a thin
        /// drawer; all of its numbers come from the pure GeometryDebugView turning one published snapshot
        /// into rows and bars, and from the GeometryDiagnostics mailbox the scene layer publishes through.
        ///
        /// The claims under test: a null snapshot yields a "no data" row per panel rather than throwing;
        /// each bar is its value over the governing ceiling clamped to [0,1]; a row alerts exactly when its
        /// metric has crossed its ceiling (frame time over budget) or is thrashing (eviction rate high); the
        /// soloed micro-relief process is flagged and the activity bars are normalised to the busiest process;
        /// byte sizes format to the tier table's units; and the mailbox publishes, holds and clears, and a
        /// null publish cannot blank a good reading. This pins the window's content with no scene or GPU.
        /// </summary>
        private static void TestGeometryDebugView()
        {
            // A null snapshot must be safe on every panel - one "no data" row, never an exception.
            Check(GeometryDebugView.DistanceModel(null).Length == 1
                  && GeometryDebugView.MicroRelief(null).Length == 1
                  && GeometryDebugView.SplatCache(null).Length == 1
                  && GeometryDebugView.ImpostorAtlas(null).Length == 1
                  && GeometryDebugView.Budget(null).Length == 1
                  && GeometryDebugView.TerrainWrites(null).Length == 1,
                "GeometryDebugView must return one row per panel for a null snapshot.");

            var s = new GeometryDiagnosticsSnapshot
            {
                tierName = "Desktop", targetFrameRate = 60, acuityPixels = 1f,
                referenceFovDeg = 60f, referenceScreenHeightPixels = 1080,
                treeDrawDistanceM = 250f, detailDrawDistanceM = 60f, basemapDistanceM = 400f,
                processesEvaluated = 13, soloProcessId = "B",
                processIds = new[] { "A", "B", "C" },
                processActivityRatesMmPerYr = new[] { 0f, 2f, 1f },
                splatCacheEnabled = true, residentPages = 8, pageCapacity = 10, splatEvictionsPerMinute = 5f,
                residentSpecies = 6, speciesCapacity = 6,
                atlasResidentBytes = 8L * 1024 * 1024, atlasCeilingBytes = 48L * 1024 * 1024,
                impostorTileViews = "256² / 8×8", rebakeQueueLength = 3, impostorEvictionsPerMinute = 10f,
                medianFrameMs = 20f, frameMsCeiling = 16.6f,
                medianTriangles = 500000f, triangleCeiling = 2500000f,
                medianDrawCalls = 400f, drawCallCeiling = 800f,
                medianMemoryMB = 30f, memoryCeilingMB = 48f,
                overBudget = true, governorStatusLine = "Tier: Desktop",
                terrainWritesThisFrame = 0, terrainWritesTotal = 12, trackedCells = 5,
            };

            var dist = GeometryDebugView.DistanceModel(s);
            Check(dist.Length == 5 && dist[0].value.Contains("1.00 px") && dist[2].value == "250 m",
                "DistanceModel must format the acuity dial and the derived draw distances.");

            // Micro-relief: one row per process, the soloed process flagged, bars normalised to the busiest.
            var micro = GeometryDebugView.MicroRelief(s);
            Check(micro.Length == 3
                  && micro[1].label.StartsWith("●")               // "B" is soloed
                  && !micro[0].label.StartsWith("●")
                  && Mathf.Approximately(micro[1].bar01, 1f)        // 2 mm/yr is the busiest
                  && Mathf.Approximately(micro[2].bar01, 0.5f),     // 1 / 2
                "MicroRelief must flag the solo process and normalise activity bars to the busiest.");

            // Splat cache within capacity: a bar, no alert; and its "off" degenerate case is one row.
            var splat = GeometryDebugView.SplatCache(s);
            Check(splat.Length == 2 && !splat[0].alert && !splat[1].alert
                  && Mathf.Approximately(splat[0].bar01, 0.8f),     // 8 / 10
                "SplatCache must show residency without alert when within capacity and below thrash.");
            var off = GeometryDebugView.SplatCache(new GeometryDiagnosticsSnapshot { splatCacheEnabled = false });
            Check(off.Length == 1, "SplatCache must collapse to one row when the tier runs with the cache off.");

            // Impostor atlas: 8 MB of a 48 MB ceiling reads as a sub-full bar and does not alert.
            var imp = GeometryDebugView.ImpostorAtlas(s);
            Check(imp.Length == 5 && !imp[0].alert && !imp[1].alert
                  && imp[1].value.Contains("8.0 MB") && imp[1].value.Contains("48.0 MB"),
                "ImpostorAtlas must format the atlas memory against its ceiling in MB.");

            // Budget: frame time 20 ms over a 16.6 ms ceiling must clamp its bar to 1 and alert; the
            // governor row carries the over-budget flag; the metrics that are under their ceilings do not alert.
            var budget = GeometryDebugView.Budget(s);
            Check(budget.Length == 5
                  && budget[0].alert && Mathf.Approximately(budget[0].bar01, 1f)
                  && !budget[1].alert && !budget[2].alert && !budget[3].alert
                  && budget[4].alert,                               // governor row = overBudget
                "Budget must alert and clamp the over-ceiling frame time and carry the governor's status.");

            // A thrashing eviction rate (≥ the thrash threshold) must alert its rate row.
            var hot = GeometryDebugView.SplatCache(new GeometryDiagnosticsSnapshot
            {
                splatCacheEnabled = true, residentPages = 4, pageCapacity = 10,
                splatEvictionsPerMinute = GeometryDebugView.ThrashEvictionsPerMinute,
            });
            Check(hot.Length == 2 && !hot[0].alert && hot[1].alert,
                "SplatCache must alert the eviction row at or above the thrash threshold.");

            var writes = GeometryDebugView.TerrainWrites(s);
            Check(writes.Length == 3 && writes[0].value == "0" && writes[1].value == "12",
                "TerrainWrites must report the per-frame and total heightmap write counters.");

            // Byte formatting is invariant-culture and unit-correct across the B / KB / MB boundaries.
            Check(GeometryDebugView.FormatBytes(512L) == "512 B"
                  && GeometryDebugView.FormatBytes(1024L) == "1.0 KB"
                  && GeometryDebugView.FormatBytes(8L * 1024 * 1024) == "8.0 MB",
                "FormatBytes must format bytes, kilobytes and megabytes with invariant units.");

            // The mailbox contract: clear leaves no data; a publish holds exactly that snapshot; a null
            // publish cannot blank a good reading; and a final clear leaves the channel clean for the next run.
            GeometryDiagnostics.Clear();
            Check(!GeometryDiagnostics.HasData, "GeometryDiagnostics.Clear must leave the channel empty.");
            GeometryDiagnostics.Publish(s);
            Check(GeometryDiagnostics.HasData && ReferenceEquals(GeometryDiagnostics.Latest, s),
                "GeometryDiagnostics.Publish must hold exactly the published snapshot.");
            GeometryDiagnostics.Publish(null);
            Check(ReferenceEquals(GeometryDiagnostics.Latest, s),
                "GeometryDiagnostics.Publish(null) must not blank a good reading.");
            GeometryDiagnostics.Clear();
        }

        /// <summary>
        /// The impostor bake PLAN is deterministic and it never lets the editor block. The atlas is a square
        /// grid of tiles; each cell owns a fixed pixel rect and is rendered from the direction the octahedral
        /// mapping decodes from that cell's centre. The claims under test: two builds of the same grid are
        /// identical cell for cell (the deterministic half of byte-identical re-bakes); the row-major order and
        /// the pixel rects tile the atlas with no gap or overlap; a hemi-octahedron plan puts every view on the
        /// upper hemisphere; the frame schedule covers every tile exactly once and never hands a frame more than
        /// its budget (so the bake stays responsive); progress runs 0→1; and the output path is project-relative
        /// and sanitised. No scene, camera or render target.
        /// </summary>
        private static void TestImpostorBakePlan()
        {
            const int n = 8, tile = 64;

            // Determinism: a second build of the same grid must match the first, cell for cell.
            var a = ImpostorBakePlan.BuildCells(n, tile, true);
            var b = ImpostorBakePlan.BuildCells(n, tile, true);
            Check(a.Length == n * n && b.Length == a.Length, "BuildCells must produce viewsPerAxis² cells.");
            bool identical = true;
            for (int i = 0; i < a.Length && identical; i++)
            {
                identical = a[i].tileIndex == b[i].tileIndex
                            && a[i].pixelRect.Equals(b[i].pixelRect)
                            && a[i].viewDirection == b[i].viewDirection;
            }
            Check(identical, "BuildCells must be deterministic - a re-bake reproduces the identical plan.");

            // Row-major order and a gap-free, non-overlapping tiling of the atlas.
            bool ordered = true, tiled = true, hemiOk = true;
            for (int i = 0; i < a.Length; i++)
            {
                ordered &= a[i].tileIndex == i && a[i].tileIndex == a[i].viewZ * n + a[i].viewX;
                tiled &= a[i].pixelRect.x == a[i].viewX * tile && a[i].pixelRect.y == a[i].viewZ * tile
                         && a[i].pixelRect.width == tile && a[i].pixelRect.height == tile;
                hemiOk &= a[i].viewDirection.y >= -1e-4f
                          && Mathf.Abs(a[i].viewDirection.magnitude - 1f) < 1e-3f;
            }
            Check(ordered, "BuildCells must enumerate cells in row-major order.");
            Check(tiled, "Cell pixel rects must tile the atlas with no gap or overlap.");
            Check(hemiOk, "A hemi-octahedron plan must put every view on the upper hemisphere as a unit vector.");

            // The frame schedule: every tile once, and never more than the per-frame budget on any frame.
            int total = ImpostorBakePlan.TileCount(n);
            const int perFrame = ImpostorBakePlan.DefaultTilesPerFrame;
            int frames = ImpostorBakePlan.FramesRequired(total, perFrame);
            Check(frames == (total + perFrame - 1) / perFrame, "FramesRequired must be ceil(total / perFrame).");
            int covered = 0, prevEnd = 0; bool contiguous = true, withinBudget = true;
            for (int f = 0; f < frames; f++)
            {
                ImpostorBakePlan.TilesOnFrame(f, total, perFrame, out int start, out int count);
                withinBudget &= count <= perFrame && count > 0;
                contiguous &= start == prevEnd;
                prevEnd = start + count;
                covered += count;
            }
            Check(withinBudget, "TilesOnFrame must never exceed the per-frame tile budget.");
            Check(contiguous && covered == total, "The frame schedule must cover every tile exactly once.");
            ImpostorBakePlan.TilesOnFrame(frames, total, perFrame, out _, out int past);
            Check(past == 0, "A frame past the end of the bake must schedule no tiles.");

            // Progress runs monotonically from 0 to 1.
            Check(Mathf.Approximately(ImpostorBakePlan.Progress01(0, total), 0f)
                  && Mathf.Approximately(ImpostorBakePlan.Progress01(total, total), 1f)
                  && ImpostorBakePlan.Progress01(total / 2, total) > 0f,
                "Progress01 must run from 0 at the start to 1 at completion.");

            // The output path is project-relative and sanitised - never absolute, never escaping the folder.
            string path = ImpostorBakePlan.AtlasAssetPath("Oak/Quercus robur", "albedo");
            Check(path.StartsWith("Assets/") && !path.Contains(":")
                  && path.EndsWith(".png") && !path.Substring("Assets/".Length).Contains(".."),
                "AtlasAssetPath must be project-relative, sanitised and .png-suffixed.");
        }
    }
}
