// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    AngleOfReposeClamp.cs
//  Purpose: Height painting cannot exceed the angle of repose of the material (§ 9.8 G-52,
//           L-34). Sand does not stand at 60°: loose granular material fails at its angle
//           of repose (~34° for dry sand), so a paint stroke that tries to raise a vertical
//           wall of it must shed the excess downslope in one pass - which is also exactly
//           how a real talus cone forms. The post-stroke slope between adjacent samples is
//           clamped to tan(repose)·run, and the material above that line is removed. On
//           BEDROCK the limit is joint geometry, not repose, so a bare rock face can stand
//           as a cliff; the repose clamp therefore only applies where the cover depth
//           exceeds a grain diameter or two - below that the surface is rock and the stroke
//           is left alone.
//
//  THE CLAMP IS A SLOPE LIMIT, NOT A HEIGHT LIMIT
//  ----------------------------------------------
//  Repose bounds the height DIFFERENCE over a horizontal run, not the absolute height, so a
//  tall dune is fine as long as its flanks sit at the angle. The clamp lowers the higher
//  sample to neighbour + tan(repose)·run; an already-gentle slope is returned untouched, so
//  a single pass is idempotent on material already at rest.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Trigonometry on heights and runs; no TerrainData, no heightmap. The self-test pins the
//  34° sand cone, the standing granite cliff (repose does not apply on thin cover), the
//  shed-excess behaviour, idempotence, and the guards. Sweeping the clamp across the
//  heightmap neighbourhood is the scene layer above.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Pure angle-of-repose slope clamp for height painting: the maximum rise over a run at a material's repose
    /// angle, the clamp that sheds excess into a talus slope, and the cover-depth test that exempts bedrock
    /// (L-34). Device-free trigonometry.
    /// </summary>
    public static class AngleOfReposeClamp
    {
        /// <summary>The tangent of the repose angle: the maximum height rise per unit horizontal run. Clamped to a
        /// non-negative angle below vertical.</summary>
        public static float TangentOfRepose(float angleOfReposeDeg)
        {
            float a = angleOfReposeDeg < 0f ? 0f : (angleOfReposeDeg > 89.9f ? 89.9f : angleOfReposeDeg);
            return Mathf.Tan(a * Mathf.Deg2Rad);
        }

        /// <summary>The maximum height difference two samples <paramref name="runM"/> apart may have at this repose
        /// angle: tan(repose)·run. Zero on a non-positive run.</summary>
        public static float MaxRise(float angleOfReposeDeg, float runM)
        {
            if (runM <= 0f) return 0f;
            return TangentOfRepose(angleOfReposeDeg) * runM;
        }

        /// <summary>
        /// Clamp the higher sample so the slope to its lower neighbour does not exceed repose: the excess above
        /// neighbour + tan(repose)·run is shed. An already-gentle slope is returned unchanged (idempotent).
        /// </summary>
        public static float ClampHeight(float higherH, float lowerNeighborH, float runM, float angleOfReposeDeg)
        {
            float ceiling = lowerNeighborH + MaxRise(angleOfReposeDeg, runM);
            return higherH > ceiling ? ceiling : higherH;
        }

        /// <summary>The slope between two samples in degrees: atan(|Δh| / run). Zero on a non-positive run.</summary>
        public static float SlopeDeg(float higherH, float lowerH, float runM)
        {
            if (runM <= 0f) return 0f;
            return Mathf.Atan(Mathf.Abs(higherH - lowerH) / runM) * Mathf.Rad2Deg;
        }

        /// <summary>Whether the repose clamp applies here: only where loose cover is deeper than a grain or two.
        /// On thinner cover the surface is bedrock, whose limit is joint geometry, so the stroke stands as a cliff.</summary>
        public static bool ReposeApplies(float coverDepthM, float grainDiameterM, float grainCount = 2f)
        {
            return coverDepthM > grainCount * grainDiameterM;
        }
    }
}
