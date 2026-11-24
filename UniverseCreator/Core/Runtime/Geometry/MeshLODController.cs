// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MeshLODController.cs
//  Purpose: The screen-space-error rule that picks a prop's LOD from how big it
//           actually is on screen, NOT from a distance list. A boulder and a spruce
//           both drop to their coarse mesh at the on-screen size their silhouettes
//           warrant, so the switch is correct at any field of view and any render
//           target - the property a distance table cannot have.
//
//  WHY A RELATIVE HEIGHT AND NOT A PIXEL COUNT
//  -------------------------------------------
//  Unity's own LODGroup compares a resolution-INDEPENDENT relative height - the
//  object's size as a fraction of the viewport - against each level's authored
//  screenRelativeTransitionHeight. We select against that same quantity so the
//  artist's authored chain is honoured verbatim: their transition heights are the
//  RATIOS between levels, and we never replace them with a generated ramp. The pixel
//  radius (the task's projected-radius formula) is also exposed, because the impostor
//  Nyquist test downstream needs a genuine pixel count; but the LOD choice rides the
//  relative height so it is identical across displays at a fixed on-screen size.
//
//  WHERE THE DEVICE ENTERS
//  -----------------------
//  Only through lodBias, exactly as Unity's QualitySettings.lodBias does: a bias > 1
//  makes objects read as larger, holding a finer LOD to a greater distance; < 1 pulls
//  the chain in on a weak part. The authored thresholds are used AS-IS - the bias
//  scales the measured height, never the artist's numbers - so one dial moves the
//  whole envelope while the intent between levels is preserved.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Every method here is a static function of numbers: no camera reference, no
//  MonoBehaviour, no allocation. The per-frame component that reads a real Camera and
//  a real LODGroup and applies the selection is a separate, thinner layer; keeping the
//  arithmetic here lets a self-test pin the done-when - same on-screen size selects the
//  same LOD at 5 m and at 50 m under different lenses - without ever entering play mode.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure screen-space-error LOD selection over a prefab's authored LODGroup thresholds. Computes the
    /// resolution-independent on-screen relative height (and, separately, the projected pixel radius) from
    /// bounds, distance and field of view, then picks the level whose authored transition height that height
    /// clears. No camera, no scene object, no allocation - the arithmetic only.
    /// </summary>
    public static class MeshLODController
    {
        /// <summary>
        /// The task's projected-radius formula: the on-screen radius, in pixels, of a sphere of world radius
        /// <paramref name="boundsRadiusM"/> at <paramref name="distanceM"/> through a lens of vertical field of
        /// view <paramref name="fovDeg"/> on a target <paramref name="screenHeightPx"/> pixels tall. Exposed for
        /// the impostor Nyquist test, which needs a genuine pixel count; the LOD choice itself rides the
        /// resolution-independent <see cref="RelativeHeight"/>. Fails closed to 0 on a degenerate camera.
        /// </summary>
        public static float ProjectedPixelRadiusPx(float boundsRadiusM, float distanceM, int screenHeightPx, float fovDeg)
        {
            if (boundsRadiusM <= 0f || distanceM <= 0f || screenHeightPx <= 0 || fovDeg <= 0f || fovDeg >= 180f)
                return 0f;
            float halfTan = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            if (halfTan <= 0f) return 0f;
            return boundsRadiusM * (screenHeightPx * 0.5f) / (distanceM * halfTan);
        }

        /// <summary>
        /// The resolution-INDEPENDENT on-screen relative height Unity's LODGroup compares against: the object's
        /// world radius as a fraction of the viewport half-height, = boundsRadius / (distance * tan(fov/2)),
        /// scaled by <paramref name="lodBias"/> (the one place the device enters, exactly as
        /// QualitySettings.lodBias). Two cameras at different distances and fields of view that put the prop at
        /// the same on-screen size return the SAME value here - which is why the LOD choice is identical across
        /// them. Fails closed to 0 on a degenerate camera or non-positive bias.
        /// </summary>
        public static float RelativeHeight(float boundsRadiusM, float distanceM, float fovDeg, float lodBias = 1f)
        {
            if (boundsRadiusM <= 0f || distanceM <= 0f || fovDeg <= 0f || fovDeg >= 180f || lodBias <= 0f)
                return 0f;
            float halfTan = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            if (halfTan <= 0f) return 0f;
            return lodBias * boundsRadiusM / (distanceM * halfTan);
        }

        /// <summary>
        /// Picks the LOD index for <paramref name="relativeHeight"/> against a prefab's authored transition
        /// heights, ordered highest-detail first (descending, as Unity stores them). LOD i is active when the
        /// height clears LOD i's threshold but not LOD i-1's, so the rule is: the FIRST index whose threshold the
        /// height meets or exceeds. Returns <c>thresholds.Count</c> when the prop is smaller than every level -
        /// impostor-or-cull territory, which the impostor layer picks up. An empty or null chain returns 0 (the
        /// prefab has a single mesh, always shown). The authored numbers are used verbatim: this reads the chain,
        /// it never rewrites it.
        /// </summary>
        public static int SelectLod(float relativeHeight, IReadOnlyList<float> screenRelativeTransitionHeights)
        {
            if (screenRelativeTransitionHeights == null || screenRelativeTransitionHeights.Count == 0)
                return 0;
            int count = screenRelativeTransitionHeights.Count;
            for (int i = 0; i < count; i++)
            {
                if (relativeHeight >= screenRelativeTransitionHeights[i])
                    return i;
            }
            return count; // below the coarsest authored level: hand off to impostor / cull
        }

        /// <summary>
        /// Convenience overload that projects and selects in one call: computes the relative height from bounds,
        /// distance, field of view and LOD bias, then selects against the authored chain. The device-independent
        /// path the per-frame component uses.
        /// </summary>
        public static int SelectLod(
            float boundsRadiusM, float distanceM, float fovDeg,
            IReadOnlyList<float> screenRelativeTransitionHeights, float lodBias = 1f)
        {
            float h = RelativeHeight(boundsRadiusM, distanceM, fovDeg, lodBias);
            return SelectLod(h, screenRelativeTransitionHeights);
        }

        // ----- Weber contrast: extend the silhouette budget where the eye actually catches it -----
        //
        // Size is not what makes a silhouette noticeable - CONTRAST is. A dark conifer on bright sky is
        // caught far past where a mid-grey boulder on mid-grey scree is. The Weber fraction of the
        // object against its background scales the geometry budget: high contrast holds detail longer,
        // low contrast drops it sooner, and the frame-time saving comes from the low-contrast half of
        // the scene that nobody was looking at. The gain enters exactly like lodBias - it multiplies the
        // effective on-screen size - so a high-contrast prop reads as larger and its LOD/impostor switch
        // retreats, while the tolerated geometric error moves the inverse way (the task's "scale the
        // allowed error inversely"). Pure luminance in, factor out; the § 8 sky/ground luminance and the
        // species albedo that feed it are read by the scene layer, not here.

        /// <summary>
        /// The Weber contrast of an object luminance against its background: |L_obj − L_bg| / L_bg. Guarded
        /// so a non-positive background (an unlit or degenerate sample) yields 0 contrast rather than a
        /// divide-by-zero or a negative fraction.
        /// </summary>
        public static float WeberContrast(float objectLuminance, float backgroundLuminance)
        {
            if (backgroundLuminance <= 0f) return 0f;
            float c = Mathf.Abs(objectLuminance - backgroundLuminance) / backgroundLuminance;
            return c < 0f ? 0f : c;
        }

        /// <summary>
        /// The size gain a Weber contrast earns: 1 + weberSensitivity · contrast, clamped to at least 1. A
        /// gain of 1 (zero contrast, or zero sensitivity) leaves selection untouched; higher contrast makes
        /// the prop read as larger, so it holds a finer LOD and switches to an impostor farther out. Multiply
        /// this into <see cref="RelativeHeight"/>'s lodBias (or divide the allowed error by it) to apply it.
        /// </summary>
        public static float ContrastSizeGain(float contrast, float weberSensitivity)
        {
            if (contrast <= 0f || weberSensitivity <= 0f) return 1f;
            float g = 1f + weberSensitivity * contrast;
            return g < 1f ? 1f : g;
        }

        /// <summary>
        /// The reciprocal reading the task states directly: the factor to scale the tolerated geometric error
        /// by, INVERSELY with contrast - high contrast tightens the budget (holds geometry), low contrast
        /// loosens it (drops sooner). Exactly 1 / <see cref="ContrastSizeGain"/>, so the two framings never drift.
        /// </summary>
        public static float ContrastErrorScale(float contrast, float weberSensitivity)
        {
            return 1f / ContrastSizeGain(contrast, weberSensitivity);
        }
    }
}
