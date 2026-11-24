// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    WaterBankSeat.cs
//  Purpose: Seats a water surface into the ground it belongs to. Given the terrain,
//           a water level and the mask of where that water is, it reshapes the bank
//           so the waterline is a real intersection of two surfaces instead of a
//           polygon edge hanging in the air.
//
//  THE PROBLEM THIS SOLVES
//  -----------------------
//  A water body is rendered as a surface at one elevation. The ground is a separate
//  surface. Nothing makes them meet. Wherever the bank is steeper than the mask's
//  own fade, the water's polygon boundary ends at some height that is not the
//  ground's height there, and the result is one of the two failures a user actually
//  sees: a visible seam where the plane's edge floats above the slope, or a
//  rectangular notch where the plane is buried and only its corner pokes out. On a
//  bank crossed at an angle - a river cutting a contour diagonally, a lake in a
//  steep bowl - it happens along the whole shore, not just at a corner.
//
//  Softening the mask does not fix it. That fades the water out in the air; the
//  geometry is still wrong. The only fix is to make the GROUND pass through the
//  water's elevation everywhere along the shore.
//
//  HOW
//  ---
//  Three zones, measured outward from the water in real metres:
//
//    - Under the water: the bed is pushed down far enough to guarantee a minimum
//      depth, so no part of the bed can poke back through its own surface. Only
//      ever downward - raising a bed would grow islands inside a finished lake.
//
//    - The bench: a narrow shelf where the ground is set TO the water's elevation.
//      This is the band the waterline actually lands in, and it is why the meeting
//      is exact rather than approximate. Real shores have one - a beach, a wave-cut
//      platform, a river's gravel bar - so it reads as terrain, not as a fix.
//
//    - The skirt: from the bench's outer edge the ground eases back to whatever it
//      naturally was, on a smoothstep so there is no ridge where the skirt ends.
//
//  Beyond the skirt nothing is touched. The whole displacement is scaled by one
//  strength so it can be dialled back without changing the shape of the blend.
//
//  WHY THE LEVEL HAS TO TRAVEL WITH THE DISTANCE
//  ---------------------------------------------
//  A dry pixel needs the elevation of the water it is the bank OF, and the level
//  field is only meaningful inside the water - outside it reads as the ground. So
//  the sweep that measures distance carries the level of the seed it came from.
//  One pass produces both, and they cannot disagree about which body a bank
//  belongs to.
//
//  Managed C# over float[]. No Unity API beyond Mathf; safe on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// Reshapes the ground around a water body so the water meets it exactly. See
    /// the file header for why a soft mask cannot do this instead.
    /// </summary>
    public static class WaterBankSeat
    {
        // Chamfer kernel. The diagonal has to be sqrt(2) or the distance measured is
        // Chebyshev and the bench comes out square around a round lake - the very
        // artefact this file exists to remove.
        private const float Straight = 1f;
        private const float Diagonal = 1.41421356f;

        /// <summary>
        /// A mask value at or below this is dry. Matches the threshold the fill
        /// nodes use to decide a pixel is water, so the two cannot disagree about
        /// where the shore is.
        /// </summary>
        public const float WetThreshold = 0.001f;

        /// <summary>
        /// Ghost pixels a seat needs either side of a chunk: the bench and skirt
        /// together, rounded up. Water just outside the chunk still owns a bank
        /// inside it, so without this the shore stops at the seam.
        /// </summary>
        public static int MarginPixels(float benchMetres, float skirtMetres,
                                       float metresPerPixel, int cap)
        {
            float mpp = Mathf.Max(1e-4f, metresPerPixel);
            float reach = Mathf.Max(0f, benchMetres) + Mathf.Max(0f, skirtMetres);
            return Mathf.Clamp(Mathf.CeilToInt(reach / mpp), 0, cap);
        }

        /// <summary>
        /// Returns the ground reshaped to seat the given water. <paramref name="ground"/>
        /// is not modified.
        /// </summary>
        /// <param name="level">Water surface elevation, 0-1. Only read where wet.</param>
        /// <param name="mask">Where the water is, 0-1.</param>
        /// <param name="benchMetres">Width of the shelf held at the water's elevation.</param>
        /// <param name="skirtMetres">Distance over which the bench eases back to the terrain.</param>
        /// <param name="minDepth">Clearance kept between bed and surface, 0-1 of world height.</param>
        /// <param name="strength">0 leaves the terrain alone, 1 applies the full seat.</param>
        public static float[] Apply(float[] ground, float[] level, float[] mask,
                                    int resX, int resZ, float metresPerPixel,
                                    float benchMetres, float skirtMetres,
                                    float minDepth, float strength,
                                    System.Func<bool> cancel)
        {
            // Back-compatible overload: the old signature with the new bank limits at their
            // defaults (world height 600 m, bank slope 0.5, a 1.5 m cap on any raise).
            return Apply(ground, level, mask, resX, resZ, metresPerPixel, benchMetres, skirtMetres,
                         minDepth, strength, 600f, DefaultBankSlope, DefaultMaxRaiseMetres, cancel);
        }

        /// <summary>Steepest bank the seat will leave, rise over run (0.5 = ~27 degrees).</summary>
        public const float DefaultBankSlope = 0.5f;

        /// <summary>Most a dry bank may be RAISED to meet the water, in metres. Beyond this the
        /// water is left to the bed carve instead of building a levee around it.</summary>
        public const float DefaultMaxRaiseMetres = 1.5f;

        /// <summary>
        /// FIX 2026-10-19 (the sunken lakes inside sharp-walled pits).
        /// The old seat set the dry ground TO the water's elevation across the bench and then
        /// eased back to the natural ground over a fixed skirt (30 m). On a hillside 40 m above
        /// the lake that is a 40 m wall in 30 m - a cliff - and on the low side it RAISED ground up
        /// to the water, building the flat levee-like apron seen around the lakes.
        /// Now, on the dry side: the bench still lands the waterline exactly (ground within the bench
        /// is brought to the level, but never raised by more than <paramref name="maxRaiseMetres"/>);
        /// beyond the bench the ground is only ever LOWERED, and only to a slope-limited ramp
        /// (level + distance x bankSlope), blended in with a smooth minimum and faded to nothing at
        /// the outer edge of the reach. A hillside therefore becomes a valley side, not a wall.
        /// </summary>
        public static float[] Apply(float[] ground, float[] level, float[] mask,
                                    int resX, int resZ, float metresPerPixel,
                                    float benchMetres, float skirtMetres,
                                    float minDepth, float strength,
                                    float worldHeightMetres, float bankSlope, float maxRaiseMetres,
                                    System.Func<bool> cancel)
        {
            int count = resX * resZ;
            float[] result = new float[count];

            if (ground == null || ground.Length < count) return result;
            System.Array.Copy(ground, result, count);

            if (level == null || mask == null) return result;
            if (level.Length < count || mask.Length < count) return result;

            float amount = Mathf.Clamp01(strength);
            if (amount <= 1e-4f) return result;

            float mpp = Mathf.Max(1e-4f, metresPerPixel);
            float benchPx = Mathf.Max(0f, benchMetres) / mpp;
            float skirtPx = Mathf.Max(0f, skirtMetres) / mpp;
            float floor = Mathf.Max(0f, minDepth);

            // 1. The bed. Done before the sweep so the seeds carry the surface of a
            //    body whose bed is already clear of it.
            for (int i = 0; i < count; i++)
            {
                if (mask[i] <= WetThreshold) continue;

                float required = level[i] - floor;
                if (result[i] > required)
                    result[i] = Mathf.Lerp(result[i], required, amount);
            }

            // Nothing to blend outward: no bench, no skirt. The bed clearance above
            // is still worth having, so this is a valid configuration, not an error.
            if (benchPx + skirtPx < 1e-3f) return result;

            float[] dist;
            float[] nearLevel;
            Sweep(mask, level, resX, resZ, out dist, out nearLevel, cancel);

            if (cancel != null && cancel()) return result;

            float reachPx = benchPx + skirtPx;

            // 2. The bench and the skirt, on the dry side only. Wet pixels were
            //    settled above; re-touching them here would undo their clearance.
            float worldH = worldHeightMetres > 1e-3f ? worldHeightMetres : 600f;
            float slope = Mathf.Max(0.05f, bankSlope);
            float raiseN = Mathf.Max(0f, maxRaiseMetres) / worldH;
            float blendN = 1.5f / worldH;                       // smooth-min radius: ~1.5 m

            for (int i = 0; i < count; i++)
            {
                if (mask[i] > WetThreshold) continue;
                if (dist[i] > reachPx) continue;

                float g = result[i];
                float lv = nearLevel[i];
                float dm = dist[i] * mpp;                       // metres from the water
                float benchM = benchPx * mpp;

                float target;
                if (dist[i] <= benchPx || skirtPx < 1e-4f)
                {
                    // On the bench the waterline must land exactly: bring ground to the level.
                    // Lowering is unlimited; raising is capped so no levee is built.
                    target = g > lv ? lv : Mathf.Min(lv, g + raiseN);
                }
                else
                {
                    if (g <= lv) continue;                      // below the water level here: leave it

                    // Slope-limited ramp from the bench edge outward, smoothly joined to the ground.
                    float ramp = lv + (dm - benchM) * slope / worldH;
                    target = SmoothMin(g, ramp, blendN);

                    // Fade the whole effect out over the outer 40% of the reach so the ground
                    // returns to its own height with no step.
                    float t = (dist[i] - benchPx) / Mathf.Max(1e-4f, reachPx - benchPx);
                    float fade = 1f - InterpolationMath.SmoothStep(InterpolationMath.Clamp01((t - 0.6f) / 0.4f));
                    target = Mathf.Lerp(g, target, fade);
                }

                result[i] = Mathf.Clamp01(Mathf.Lerp(g, target, amount));
            }

            return result;
        }

        /// <summary>
        /// Two-pass chamfer sweep outward from the wet cells, carrying each cell's
        /// distance in pixels and the water elevation of the nearest wet cell.
        /// </summary>
        /// <remarks>
        /// Both passes propagate the payload alongside the distance, so a cell always
        /// reports the level of whichever body won its distance. Taking the level
        /// from a separate lookup instead would let a bank be shaped by one lake and
        /// levelled by another across a narrow isthmus.
        /// </remarks>
        private static void Sweep(float[] mask, float[] level, int resX, int resZ,
                                  out float[] dist, out float[] nearLevel,
                                  System.Func<bool> cancel)
        {
            int count = resX * resZ;
            dist = new float[count];
            nearLevel = new float[count];

            // Any distance the sweep can produce is below this, so an unreached cell
            // is left far outside every reach test rather than at a false zero.
            float far = (resX + resZ) * Diagonal;

            for (int i = 0; i < count; i++)
            {
                bool wet = mask[i] > WetThreshold;
                dist[i] = wet ? 0f : far;
                nearLevel[i] = wet ? level[i] : 0f;
            }

            // Forward: every neighbour read here was settled earlier this pass.
            for (int z = 0; z < resZ; z++)
            {
                if (cancel != null && (z & 31) == 0 && cancel()) return;

                int row = z * resX;
                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    if (dist[i] == 0f) continue;

                    if (x > 0) Relax(dist, nearLevel, i, i - 1, Straight);
                    if (z > 0) Relax(dist, nearLevel, i, i - resX, Straight);
                    if (x > 0 && z > 0) Relax(dist, nearLevel, i, i - resX - 1, Diagonal);
                    if (x < resX - 1 && z > 0) Relax(dist, nearLevel, i, i - resX + 1, Diagonal);
                }
            }

            // Backward: picks up the water lying down-and-right of a pixel, which the
            // forward pass structurally cannot see.
            for (int z = resZ - 1; z >= 0; z--)
            {
                if (cancel != null && (z & 31) == 0 && cancel()) return;

                int row = z * resX;
                for (int x = resX - 1; x >= 0; x--)
                {
                    int i = row + x;
                    if (dist[i] == 0f) continue;

                    if (x < resX - 1) Relax(dist, nearLevel, i, i + 1, Straight);
                    if (z < resZ - 1) Relax(dist, nearLevel, i, i + resX, Straight);
                    if (x < resX - 1 && z < resZ - 1) Relax(dist, nearLevel, i, i + resX + 1, Diagonal);
                    if (x > 0 && z < resZ - 1) Relax(dist, nearLevel, i, i + resX - 1, Diagonal);
                }
            }
        }

        /// <summary>Polynomial smooth minimum: like Min, but rounds the crease within radius k.</summary>
        private static float SmoothMin(float a, float b, float k)
        {
            if (k <= 1e-9f) return a < b ? a : b;
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        /// <summary>Takes the cheaper route to water through a neighbour, payload and all.</summary>
        private static void Relax(float[] dist, float[] nearLevel, int i, int n, float step)
        {
            float through = dist[n] + step;
            if (through >= dist[i]) return;

            dist[i] = through;
            nearLevel[i] = nearLevel[n];
        }
    }
}
