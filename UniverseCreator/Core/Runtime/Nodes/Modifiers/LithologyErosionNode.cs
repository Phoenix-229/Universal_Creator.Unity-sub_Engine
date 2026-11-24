// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LithologyErosionNode.cs
//  Purpose: Differential erosion driven by the § 9.4 lithology field: a hard bed
//           stands out as a bench or a scarp, a soft bed recedes into a slope,
//           because rock does not erode at one rate. It reads the exposed unit at
//           every point (OutcropResolver) and scales the LOCAL RELIEF of the incoming
//           height by that unit's resistance, so the landform keeps its large shape
//           while its texture becomes rock-aware instead of uniform.
//
//  THE CIRCULARITY, NAMED AND BROKEN IN ONE PASS (L-01 / C-01)
//  ----------------------------------------------------------
//  There is a genuine loop here: the outcrop needs the surface elevation, and this
//  node wants to change the surface elevation from the outcrop. Iterating that to
//  convergence is a landscape-evolution model - out of scope by C-01, and it would
//  also destroy per-tile determinism, because a tile's answer would then depend on
//  how many of its neighbours had already been solved. So this node does NOT iterate.
//  It reads the PROVISIONAL height handed down the node stack once, resolves the
//  outcrop against that height, and applies its adjustment a single time. The
//  structural model it resolves through (GeologicStructure) is a pure function of
//  world position and seed and never reads terrain height, so it is always available
//  without a chicken-and-egg. The result is a plausible differential-erosion
//  SIGNATURE, not a simulated history: rebuilding the same tile twice is bit-identical.
//
//  OPT-IN, BECAUSE IT EDITS MODULE 1'S HEIGHT (C-08)
//  -------------------------------------------------
//  Strength defaults to zero, so a freshly placed node is a pass-through until an
//  author dials it up; at zero, or when the node is disabled, the heightmap is
//  returned untouched, bit-for-bit. When it does act, it preserves the tile mean
//  elevation - an un-normalised multiplier would raise or lower the whole world and
//  drag every § 7 water level with it - so only the relief around the mean changes.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Geology;

namespace UniverseCreator.Nodes.Modifiers
{
    /// <summary>
    /// Scales local relief by the exposed unit's erodibility, mean-preserved, in one pass.
    /// Off by default (strength 0). Reads the lithology profile's structure and units, resolves
    /// the outcrop per pixel, and stands hard beds out while soft beds recede.
    /// </summary>
    [System.Serializable]
    public class LithologyErosionNode : ModifierNode, IPreparedGraphInputHash, ILithologySnapshotInput
    {
        public override string NodeName { get { return "Lithology Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip
        {
            get { return "Differential erosion from the lithology column: hard beds stand as " +
                         "benches, soft beds recede. One pass, mean-preserving, off by default."; }
        }

        [Tooltip("The stratigraphic column that decides what rock is exposed where. Without one " +
                 "assigned, the node is a pass-through.")]
        public LithologyProfile profile;
        [System.NonSerialized] private ulong preparedProfileHash;
        [System.NonSerialized] private LithologyProfile capturedProfile;
        [System.NonSerialized] private bool hasCapturedProfile;
        LithologyProfile ILithologySnapshotInput.SourceProfile { get { return profile; } }
        LithologyProfile ILithologySnapshotInput.CapturedProfile { get { return hasCapturedProfile ? capturedProfile : profile; } }
        void ILithologySnapshotInput.BindCapturedProfile(LithologyProfile value) { capturedProfile = value; hasCapturedProfile = true; }

        /// <summary>UC-T01-07: fingerprints every copied column/structure value on the main thread before a bake.</summary>
        public override void PrepareMainThread() { base.PrepareMainThread(); preparedProfileHash = PreparedLithologyInputs.Fingerprint(((ILithologySnapshotInput)this).CapturedProfile); }
        /// <summary>Returns the prepared column fingerprint; absence has a distinct nonzero hash.</summary>
        /// <remarks>Call after PrepareMainThread. No Unity API is accessed here.</remarks>
        public ulong GetPreparedInputHash() { return preparedProfileHash; }

        [Tooltip("World seed feeding the structural model's fault set, so the pattern matches the " +
                 "rest of the world's geology.")]
        public int seed = 12345;

        [Range(0f, 1f)]
        [Tooltip("How strongly resistance reshapes the relief. 0 is OFF - a freshly placed node " +
                 "does nothing until an author opts in, because this edits Module 1's height.")]
        public float strength = 0f;

        [Range(1, 16)]
        [Tooltip("Radius in pixels of the neighbourhood that defines 'local relief'. The bench/slope " +
                 "contrast is built by scaling each point's departure from this local mean.")]
        public int reliefRadiusPx = 8;

        [Tooltip("Thickness in metres of the soft contact band between beds, so a bed boundary " +
                 "crosses as a gradient of resistance rather than a hard step.")]
        public float contactBlendM = 2f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            LithologyProfile inputProfile = ((ILithologySnapshotInput)this).CapturedProfile;
            // Opt-in gate: off, unassigned, or an empty column - hand the height back untouched.
            if (strength <= 0f || ReferenceEquals(inputProfile, null) || inputProfile.UnitCount == 0) return; // UC-T01-06: worker-safe identity.

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            if (resX < 3 || resZ < 3) return;

            float[] arr = m.arr;
            int count = arr.Length;

            // Pass A: resolve the exposed unit at every pixel and read its resistance, blended
            // across the contact toward the unit above. Also accumulate the tile-mean resistance,
            // so what follows is a RELATIVE (differential) contrast, not an absolute multiplier.
            float[] klsu = new float[count];
            float meanKlsu = 0f;
            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;
                for (int x = 0; x < resX; x++)
                {
                    int i = z * resX + x;
                    Vector3 w = m.PixelToWorld(x, z);
                    LithoSample s = OutcropResolver.Resolve(inputProfile, w.x, w.z, w.y, seed, contactBlendM);
                    float k = SampleErodibility(inputProfile, s);
                    klsu[i] = k;
                    meanKlsu += k;
                }
            }
            meanKlsu /= count;
            if (meanKlsu < 1e-6f) return;

            // Pass B: the single differential-relief adjustment, mean-preserved. Pure and testable.
            float[] outArr = new float[count];
            ApplyDifferentialErosion(arr, outArr, klsu, resX, resZ,
                                     Mathf.Clamp(reliefRadiusPx, 1, GhostMargin), strength, meanKlsu);
            if (stop != null && stop.StopRequested) return;

            System.Array.Copy(outArr, arr, count);
            m.Clamp01();
        }

        /// <summary>
        /// Erodibility at a resolved outcrop, blended across the soft contact. Well inside a bed
        /// this is just that bed's resistance; near its top it ramps toward the unit above by the
        /// sample's contact weight, so a bed boundary crosses as a gradient of resistance rather
        /// than a one-texel jump in relief. Falls back to a neutral 1 for an empty sample.
        /// </summary>
        private static float SampleErodibility(LithologyProfile profile, LithoSample s)
        {
            if (s.unit == null) return 1f;
            float k = s.unit.erodibilityKlsu;
            if (s.aboveIndex >= 0 && s.blendWeightToAbove > 0f)
            {
                LithoUnit above = profile.UnitAt(s.aboveIndex);
                if (above != null)
                    k = Mathf.Lerp(k, above.erodibilityKlsu, Mathf.Clamp01(s.blendWeightToAbove));
            }
            return k > 1e-6f ? k : 1e-6f;
        }

        /// <summary>
        /// The pure, deterministic core: scale each pixel's departure from its LOCAL mean by the
        /// pixel's resistance relative to the tile mean, then correct the global mean back to its
        /// original value. Kept static and Unity-free so it is directly testable without a terrain:
        /// same inputs always yield the same output (single pass, fixed order, no RNG), a hard bed
        /// (k &gt; mean) amplifies relief into a bench, a soft bed (k &lt; mean) damps it into a slope,
        /// and strength 0 reproduces the input exactly.
        /// </summary>
        public static void ApplyDifferentialErosion(float[] src, float[] dst, float[] klsu,
            int resX, int resZ, int radius, float strength, float meanKlsu)
        {
            int count = src.Length;
            if (meanKlsu < 1e-6f) { System.Array.Copy(src, dst, count); return; }

            // Summed-area table over src (double, fixed order -> deterministic) for O(1) box means.
            // Indices are +1 shifted: sat[(z+1)*W + (x+1)] = sum of src over [0..x] x [0..z].
            int W = resX + 1;
            double[] sat = new double[W * (resZ + 1)];
            for (int z = 0; z < resZ; z++)
            {
                double rowSum = 0.0;
                int satRow = (z + 1) * W;
                int prevRow = z * W;
                int srcRow = z * resX;
                for (int x = 0; x < resX; x++)
                {
                    rowSum += src[srcRow + x];
                    sat[satRow + (x + 1)] = sat[prevRow + (x + 1)] + rowSum;
                }
            }

            // Differential relief: keep the local mean, scale the departure from it by resistance
            // relative to the tile mean. Hard bed (k > mean) -> factor > 1 -> relief stands out;
            // soft bed (k < mean) -> factor < 1 -> relief recedes. Clamped so an extreme contrast
            // cannot invert or explode the relief.
            double origSum = 0.0, newSum = 0.0;
            for (int z = 0; z < resZ; z++)
            {
                int row = z * resX;
                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float local = BoxMean(sat, W, resX, resZ, x, z, radius);
                    float relief = src[i] - local;
                    float factor = Mathf.Clamp(Mathf.Lerp(1f, klsu[i] / meanKlsu, strength), 0.25f, 4f);
                    float v = local + relief * factor;
                    dst[i] = v;
                    origSum += src[i];
                    newSum += v;
                }
            }

            // Mean preservation: the redistribution can nudge the tile mean; correct it exactly so
            // enabling the node never raises or lowers the whole world (and every § 7 water level).
            float delta = (float)((origSum - newSum) / count);
            for (int i = 0; i < count; i++) dst[i] += delta;
        }

        /// <summary>Mean of src over the clamped box [cx-r,cx+r] x [cz-r,cz+r] via the summed-area table.</summary>
        private static float BoxMean(double[] sat, int W, int resX, int resZ, int cx, int cz, int r)
        {
            int x0 = cx - r; if (x0 < 0) x0 = 0;
            int x1 = cx + r; if (x1 > resX - 1) x1 = resX - 1;
            int z0 = cz - r; if (z0 < 0) z0 = 0;
            int z1 = cz + r; if (z1 > resZ - 1) z1 = resZ - 1;

            double a = sat[z0 * W + x0];
            double b = sat[z0 * W + (x1 + 1)];
            double c = sat[(z1 + 1) * W + x0];
            double d = sat[(z1 + 1) * W + (x1 + 1)];
            double sum = d - b - c + a;
            int area = (x1 - x0 + 1) * (z1 - z0 + 1);
            return (float)(sum / area);
        }
    }
}
