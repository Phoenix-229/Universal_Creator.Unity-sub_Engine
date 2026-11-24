// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefOutput.cs
//  Purpose: The graph node that EXPOSES the sub-metre micro-relief the ground would
//           carry as fields other nodes can read - the signed vertical offset (in
//           millimetres) the surface is pushed to at each pixel, and the index of the
//           process that owns that pixel. It sculpts nothing: the offset is the very
//           displacement the camera-following overlay draws on the GPU, surfaced here as
//           a number so § 6's scatter can bias saplings, grass and loose stones onto the
//           microsites the relief creates (G-28) - one source of truth, read as data.
//
//  IT WRAPS THE EXISTING FIELD, IT DOES NOT REINVENT IT
//  ----------------------------------------------------
//  The thirteen weathering/creep/wash processes, their rate-weighted competition and the
//  Nyquist split between displacement and texture all already live in MicroReliefField.
//  This node builds that field once and, per pixel, assembles the context it evaluates -
//  it adds no new physics. So the offset a scatter node reads is bit-for-bit the offset
//  the overlay shader applies, because both come from the same evaluator.
//
//  ONLY THE PROCESSES THE INPUTS CAN HONESTLY DRIVE WILL FIRE
//  ---------------------------------------------------------
//  The context is filled from what the height field and the profile actually know -
//  slope, aspect, curvature and normal from the ground shape; the exposed bed and its
//  surficial cover from the resolvers; the vertex spacing from the tile. Every climate,
//  hydrology and biology driver is left at zero because the graph has not supplied one,
//  and each process reads that honest zero as "not active". The result is that a bare
//  geology-and-form context fires only the rock-and-form processes (jointing, spheroidal
//  weathering, a coarse clast lag where one is possible) and withholds the rest, rather
//  than inventing a climate to make them all fire. That is the done-when in one sentence:
//  adding this node changes only what the inputs can truthfully justify.
//
//  NEUTRAL WHEN UNWIRED
//  --------------------
//  No profile, an empty profile, or no height feeding the input all resolve to zeros -
//  no relief to report - so a graph that adds the node but wires nothing is unchanged.
//
//  DEVICE-FREE, WORKER-SAFE
//  ------------------------
//  The per-pixel core is static over the Matrix, the profile and a MicroReliefField (all
//  pure C# and Mathf, legal off the main thread), so the evaluator may run it on a worker
//  and the self-test may pin it on a hand-built tile without a scene or a GPU.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.Geology;
using UniverseCreator.Overlay;

namespace UniverseCreator.Nodes.Outputs
{
    /// <summary>
    /// Data node (§ 9.11 G-70) that publishes the micro-relief field as two selectable Matrix channels -
    /// the signed displacement in millimetres and the dominant process index - by assembling a
    /// <see cref="MicroReliefContext"/> per pixel from the ground shape and geology and evaluating the shared
    /// <see cref="MicroReliefField"/>. It writes no render state; an unwired instance emits zeros.
    /// </summary>
    [System.Serializable]
    public class MicroReliefOutput : Generator, IPreparedGraphInputHash, ILithologySnapshotInput
    {
        public override string NodeName { get { return "Micro-Relief Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Utility; } }
        public override string Tooltip
        {
            get { return "Expose the micro-relief offset (mm) and dominant process index as fields for scatter bias."; }
        }

        /// <summary>The stratigraphic column read at each pixel. Null = no geology; the node emits zeros.</summary>
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

        /// <summary>Seed handed to the outcrop resolver and the relief field so the reading is deterministic.</summary>
        public int seed = 12345;

        /// <summary>Metres over which two beds blend at a contact, matching the other geology readers.</summary>
        public float contactBlendM = 2f;

        // Output channel indices - the order the port array declares.
        private const int ChannelOffsetMm = 0;
        private const int ChannelDominantProcess = 1;

        // Cached: GetOutputs is polled every editor repaint, so it must never allocate.
        private static readonly Port[] ReliefOutputs =
        {
            new Port("Offset (mm)", PortType.Matrix, PortDirection.Output, true),
            new Port("Dominant Process", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return ReliefOutputs; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (stop != null && stop.StopRequested) return null;

            LithologyProfile inputProfile = ((ILithologySnapshotInput)this).CapturedProfile;
            // UC-T01-06: copied profiles remain alive via the tile lease; use managed identity on workers.
            if (height == null || ReferenceEquals(inputProfile, null) || inputProfile.UnitCount == 0)
                return tile.CreateMatrix();

            return ComputeChannel(height, inputProfile, outputIndex, seed, contactBlendM, stop);
        }

        /// <summary>
        /// The pure per-pixel core, static and scene-free so a self-test can drive it on a hand-built tile.
        /// Builds the relief field once, then for each pixel assembles the context from the ground shape and
        /// the resolved geology (all geomorphic/climate drivers left at zero) and emits the requested channel.
        /// The result is a fresh Matrix placed exactly like <paramref name="height"/>.
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
            if (ReferenceEquals(profile, null) || profile.UnitCount == 0) return result; // UC-T01-06: worker-safe identity.

            MicroReliefField field = MicroReliefField.CreateDefault();

            int w = height.rect.size.x;
            int h = height.rect.size.z;
            int ox = height.rect.offset.x;
            int oz = height.rect.offset.z;
            bool wantOffset = channel != ChannelDominantProcess;

            Vector2 ps = height.PixelSize;
            float vertexSpacingM = 0.5f * (ps.x + ps.y); // the tile's own sampling grid IS the vertex spacing

            for (int z = 0; z < h; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return result;
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    Vector3 wp = height.PixelToWorld(ox + x, oz + z);
                    LithoSample s = OutcropResolver.Resolve(profile, wp.x, wp.z, wp.y, seed, contactBlendM);
                    SurfaceForm form = MatrixSurfaceForm.Sample(height, x, z);
                    CoverSample cov = SurficialCoverResolver.Resolve(profile, s, form.slopeDeg, 0f, 0f);

                    MicroReliefContext c = new MicroReliefContext();
                    c.env.worldPos = wp;
                    c.env.normal = form.normal;
                    c.env.slopeDeg = form.slopeDeg;
                    c.env.aspectDeg = form.aspectDeg;
                    c.env.curvature = form.curvature;
                    // Every other env field, and the geomorphic/climate tail (freeze-thaw, contributing area,
                    // shear stress, wave run-up), stay at their zero default: the graph supplied no such driver,
                    // and each process reads that zero as "not active" - the honest unknown, not a fabricated one.
                    c.litho = s;
                    c.cover = cov;
                    c.vertexSpacingM = vertexSpacingM;
                    c.seed = seed;

                    result.arr[i] = wantOffset
                        ? field.Evaluate(c).displacementMm
                        : field.DominantProcessIndex(c);
                }
            }

            return result;
        }
    }
}
