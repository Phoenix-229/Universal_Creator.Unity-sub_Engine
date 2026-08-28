// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LithologyOutput.cs
//  Purpose: The graph node that EXPOSES the ground's geology as fields other nodes can
//           read - it does not sculpt the terrain and it writes no render state. For
//           every pixel it asks the outcrop resolver which bed is exposed at that world
//           point, and the surficial-cover resolver what loose material (if any) sits on
//           top, then publishes four numbers a downstream mask or scatter node can select
//           against: the exposed unit's index, the cover thickness in metres, the grain
//           size of the material actually at the surface, and the bedrock's erodibility.
//
//  IT ONLY REPORTS - IT NEVER CHANGES THE GROUND
//  ---------------------------------------------
//  Every value here is a pure function of the profile and the incoming height field:
//  the same resolvers the erosion modifier and the cover system already trust, read a
//  second time as data instead of as a displacement. So a graph that drops this node in
//  gains four readable channels and nothing moves; a graph without it builds exactly as
//  before. That is the whole contract: adding it changes only what it claims to change.
//
//  THE SURFACE ANSWER, NOT THE BEDROCK ANSWER, FOR THE THINGS THAT SIT ON TOP
//  -------------------------------------------------------------------------
//  Cover depth and grain size describe what a seed, a boot or a raindrop meets first -
//  the colluvium/alluvium/soil the cover resolver places - because that is the material
//  the surface presents. Unit index and erodibility describe the bedrock beneath, blended
//  across a contact exactly as the erosion node blends it, so the two nodes never disagree
//  about which rock a pixel is standing on.
//
//  NEUTRAL WHEN UNWIRED
//  --------------------
//  No profile assigned, an empty profile, or no height feeding the input all resolve to a
//  field of zeros placed on the tile - there is no geology to report, so it reports none,
//  and a consumer reads that as "no data here" rather than a false reading.
//
//  DEVICE-FREE, WORKER-SAFE
//  ------------------------
//  The per-pixel core is a static function over the Matrix and the profile (only Mathf and
//  the pure resolvers, legal off the main thread), so the evaluator may run it on a worker
//  and the self-test may pin it on a hand-built tile without a scene.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Geology;

namespace UniverseCreator.Nodes.Outputs
{
    /// <summary>
    /// Data node (§ 9.11 G-70) that publishes the exposed geology as four selectable Matrix channels -
    /// unit index, cover depth (m), surface grain size (φ) and bedrock erodibility (klsu) - by reading the
    /// outcrop and surficial-cover resolvers per pixel. It sculpts nothing and writes no render state; an
    /// unwired instance emits zeros so a graph that adds it changes only the channels it claims to add.
    /// </summary>
    [System.Serializable]
    public class LithologyOutput : Generator
    {
        public override string NodeName { get { return "Lithology Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip
        {
            get { return "Expose the exposed unit index, cover depth, surface grain size and bedrock erodibility as fields."; }
        }

        /// <summary>The stratigraphic column read at each pixel. Null = no geology; the node emits zeros.</summary>
        public LithologyProfile profile;

        /// <summary>Seed handed to the outcrop resolver so its structural jitter matches the erosion node's.</summary>
        public int seed = 12345;

        /// <summary>Metres over which two beds blend at a contact, mirroring the erosion node's contact blend.</summary>
        public float contactBlendM = 2f;

        // Output channel indices - the order the port array declares, referenced by the switch below.
        private const int ChannelUnitIndex = 0;
        private const int ChannelCoverDepth = 1;
        private const int ChannelGrainSize = 2;
        private const int ChannelErodibility = 3;

        // Cached: GetOutputs is polled every editor repaint, so it must never allocate. allowMultiple so
        // one field can fan out to several masks/scatter nodes at once.
        private static readonly Port[] LithoOutputs =
        {
            new Port("Unit Index", PortType.Matrix, PortDirection.Output, true),
            new Port("Cover Depth", PortType.Matrix, PortDirection.Output, true),
            new Port("Grain Size", PortType.Matrix, PortDirection.Output, true),
            new Port("Erodibility", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return LithoOutputs; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (stop != null && stop.StopRequested) return null;

            // No height feeding in, or no geology to read: a neutral, correctly-placed field of zeros.
            if (height == null || profile == null || profile.UnitCount == 0)
                return tile.CreateMatrix();

            return ComputeChannel(height, profile, outputIndex, seed, contactBlendM, stop);
        }

        /// <summary>
        /// The pure per-pixel core, static and scene-free so a self-test can drive it on a hand-built tile.
        /// Reads the world point under each pixel, resolves the exposed bed and its surficial cover, and fills
        /// the requested channel. The result is a fresh Matrix placed exactly like <paramref name="height"/>.
        /// </summary>
        public static Matrix ComputeChannel(Matrix height, LithologyProfile profile,
                                            int channel, int seed, float contactBlendM, StopToken stop = null)
        {
            Matrix result = new Matrix(height.rect)
            {
                worldPos = height.worldPos,
                worldSize = height.worldSize,
                worldHeight = height.worldHeight
            };
            if (profile == null || profile.UnitCount == 0) return result;

            int w = height.rect.size.x;
            int h = height.rect.size.z;
            int ox = height.rect.offset.x;
            int oz = height.rect.offset.z;
            int ch = channel < ChannelUnitIndex ? ChannelUnitIndex
                   : (channel > ChannelErodibility ? ChannelErodibility : channel);

            for (int z = 0; z < h; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return result;
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    Vector3 wp = height.PixelToWorld(ox + x, oz + z);
                    LithoSample s = OutcropResolver.Resolve(profile, wp.x, wp.z, wp.y, seed, contactBlendM);

                    float value;
                    if (ch == ChannelUnitIndex)
                    {
                        value = s.unitIndex; // the exposed bed, or -1 above the top of the column
                    }
                    else if (ch == ChannelErodibility)
                    {
                        value = SampleErodibility(profile, s);
                    }
                    else
                    {
                        // Cover depth and grain size describe the material at the surface, which needs slope.
                        float slopeDeg = MatrixSurfaceForm.Sample(height, x, z).slopeDeg;
                        CoverSample cov = SurficialCoverResolver.Resolve(profile, s, slopeDeg, 0f, 0f);
                        value = ch == ChannelCoverDepth ? cov.depthM : cov.grainSizePhi;
                    }

                    result.arr[i] = value;
                }
            }

            return result;
        }

        /// <summary>Bedrock erodibility at the sample, blended toward the unit above across a contact - the
        /// SAME reading the erosion modifier uses, so the reported field and the sculpted relief agree.</summary>
        private static float SampleErodibility(LithologyProfile profile, LithoSample s)
        {
            if (s.unit == null) return 0f; // no bed exposed here (above the column top): nothing to report
            float k = s.unit.erodibilityKlsu;
            if (s.aboveIndex >= 0 && s.blendWeightToAbove > 0f)
            {
                LithoUnit above = profile.UnitAt(s.aboveIndex);
                if (above != null)
                    k = Mathf.Lerp(k, above.erodibilityKlsu, Mathf.Clamp01(s.blendWeightToAbove));
            }
            return k;
        }
    }
}
