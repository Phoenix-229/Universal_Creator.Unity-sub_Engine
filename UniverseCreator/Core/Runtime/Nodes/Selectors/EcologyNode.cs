// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    EcologyNode.cs
//  Purpose: V-46. The ONE node that makes the derived environment visible in the
//           graph. Without it every field the ecology model computes is invisible
//           and untunable except by rebuilding.
//
//  WHY THIS IS ONE NODE AND NOT SIX
//  --------------------------------
//  Every field below comes from a SINGLE EnvironmentSampler.Sample call. Splitting
//  them across six nodes would mean six samples of the same site to answer six
//  questions one sample already answers, and the six would drift apart the moment
//  one of them was given its own settings. The model stays in Ecology/ (C-15); this
//  file only reads it and lays the answer out on a lattice.
//
//  WHY IT IS A SELECTOR AND NOT A NEW CATEGORY
//  -------------------------------------------
//  It emits masks to select against, which is what every node in this folder does,
//  so it is filed with its functional peers. No NodeCategory value was appended:
//  the numeric value is what a saved graph stores, and adding one would mean
//  touching the colour map and every category filter for a node that already has an
//  honest home.
//
//  It does NOT extend SelectorNode, and that is not an oversight. SelectorNode is
//  defined as one height in and ONE mask out, and it implements Generate itself to
//  guarantee that shape. Seven outputs cannot honour that contract, so this node
//  derives from Generator directly - the same choice HydrologyNodes made, and for
//  the same reason.
//
//  WHAT IS DELIBERATELY NOT COPIED FROM KoeppenBiomeClassifierNode
//  --------------------------------------------------------------
//  That node softens its class mask by NUMERIC DISTANCE between class ids, which is
//  right for Koeppen because its classes are ordered along temperature. LandformZone
//  is NOT ordered: Bog is 10 and MontaneForest is 11 and nothing physical makes them
//  adjacent, so a distance-softened mask would bleed bog into montane forest and
//  call it a transition. The zone mask here is therefore an exact test. To soften it
//  spatially, feed it through the shipped BlurNode - which is the only blur in the
//  package and must stay so.
//
//  THE ARIDITY PORT READS HIGH WHEN THE SITE IS WET, and that is not a bug. The
//  field is the published aridity index, precipitation over PET, so 0 is desert and
//  values above 1 are humid. The port tooltip says so, because the field name alone
//  reads backwards to anyone who has not met the index.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.Nodes.Selectors
{
    /// <summary>
    /// Previews the derived ecology environment (V-04/V-08) as greyscale masks.
    /// </summary>
    [System.Serializable]
    public class EcologyNode : Generator
    {
        public override string NodeName { get { return "Ecology"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }

        public override string Tooltip
        {
            get
            {
                return "Exposes the derived ecology environment as masks: soil moisture, "
                     + "topographic wetness, aridity, wind exposure, light and landform zone.";
            }
        }

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        private static readonly Port[] Outs =
        {
            new Port("Soil Moisture",  PortType.Matrix, PortDirection.Output, true),
            new Port("Wetness Index",  PortType.Matrix, PortDirection.Output, true),
            new Port("Aridity",        PortType.Matrix, PortDirection.Output, true),
            new Port("Wind Exposure",  PortType.Matrix, PortDirection.Output, true),
            new Port("Light",          PortType.Matrix, PortDirection.Output, true),
            new Port("Zone Id",        PortType.Matrix, PortDirection.Output, true),
            new Port("Zone Mask",      PortType.Matrix, PortDirection.Output, true)
        };

        // Port indices, named so the switch below cannot drift from the array above.
        private const int OutSoilMoisture = 0;
        private const int OutWetnessIndex = 1;
        private const int OutAridity = 2;
        private const int OutWindExposure = 3;
        private const int OutLight = 4;
        private const int OutZoneId = 5;
        private const int OutZoneMask = 6;

        // ---- Which light ---------------------------------------------------------

        /// <summary>
        /// Which layer of the canopy stack the Light port reports. Shade is a stack
        /// rather than a scalar (L-17): an emergent oak is not shaded by its own
        /// canopy, an understory hazel is, and moss is shaded by both. Three ports
        /// would imply three independent fields; one port plus this choice says
        /// plainly that they are three readings of one column of air.
        /// </summary>
        public LightLayer lightLayer = LightLayer.Floor;

        public enum LightLayer
        {
            /// <summary>Light a Canopy-layer species competes in. Reads subcanopyLightFraction.</summary>
            Canopy = 0,

            /// <summary>Light an Understory species receives. Reads canopyLightFraction.</summary>
            Understory = 1,

            /// <summary>Light reaching the ground, for grass, moss and seedlings. Reads floorLightFraction.</summary>
            Floor = 2
        }

        // ---- Display scaling ----------------------------------------------------

        /// <summary>
        /// Topographic wetness at the white end of the Wetness Index port. TWI is
        /// ln(a/tan beta) and has no upper bound - a large flat catchment outlet can
        /// exceed 25 - so a ceiling is needed to show it at all. It scales the
        /// PREVIEW only and never reaches the model; the deposit and species code read
        /// the raw index. The default spans hillslope through channel: the shipped
        /// deposit model treats 5 as a bench and 9 as a channel, so both land in the
        /// visible middle rather than saturating.
        /// </summary>
        public float wetnessIndexCeiling = 20f;

        /// <summary>
        /// Aridity index at the white end of the Aridity port. The index is P/PET, so
        /// 0.05 is hyper-arid, 0.2 arid, 0.5 semi-arid, 0.65 dry sub-humid and above
        /// that humid (the UNEP bands). A ceiling of 2 keeps every one of those bands
        /// inside the lower half of the ramp where they can be told apart, and still
        /// shows a genuinely wet site as bright rather than clipped.
        /// </summary>
        public float aridityCeiling = 2f;

        /// <summary>Which zone the Zone Mask port isolates.</summary>
        public LandformZone isolateZone = LandformZone.MontaneForest;

        // ---- Cost ---------------------------------------------------------------

        /// <summary>
        /// Lattice spacing, in pixels, between environment samples. Continuous fields
        /// are bilinearly filled between them; the zone id is nearest-neighbour,
        /// because a value halfway between Talus and CoastalDune is not a zone.
        ///
        /// Above 1 for a physical reason rather than to cut corners: these fields vary
        /// over tens of metres - climate over a lapse-rate gradient, TWI over flow
        /// accumulation, light over a canopy grid cell - so sampling every pixel of a
        /// metre lattice resolves detail the model does not contain. Same principle as
        /// the V-40 coarse tick: sample at the rate the physics changes, interpolate
        /// between. Set to 1 when auditing a small tile and the cost does not matter.
        /// </summary>
        public int sampleStride = 4;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            if (height == null) return null;
            if (stop != null && stop.StopRequested) return null;

            EcoScatterContext ctx = tile != null ? tile.ecology : null;

            // No ecology context means this world has no vegetation manager, so there
            // is no derived environment to report. Null rather than a filled matrix,
            // and the direction matters: a null mask reads downstream as "no mask" and
            // places everything, whereas a matrix of zeros reads as "nothing may grow
            // anywhere" and would silently empty the world. Failing toward no masking
            // is recoverable; failing toward an empty world looks like a broken build.
            if (ctx == null) return null;

            ctx.EnsureBound(height);
            if (ctx.sampler == null) return null;

            Matrix dst = tile.CreateMatrix();

            int sizeX = dst.rect.size.x;
            int sizeZ = dst.rect.size.z;
            if (sizeX <= 0 || sizeZ <= 0) return dst;

            int stride = Mathf.Clamp(sampleStride, 1, 16);
            bool categorical = outputIndex == OutZoneId || outputIndex == OutZoneMask;

            // One extra column and row so the far edge has an anchor to interpolate
            // toward instead of being clamped from the last interior sample.
            int coarseX = (sizeX + stride - 1) / stride + 1;
            int coarseZ = (sizeZ + stride - 1) / stride + 1;

            float[] coarse = new float[coarseX * coarseZ];

            if (!SampleCoarse(coarse, coarseX, coarseZ, stride, sizeX, sizeZ,
                              dst, ctx, outputIndex, stop))
                return null;

            if (stride == 1)
            {
                // The coarse lattice already covers every pixel; copying beats
                // interpolating between samples that are one pixel apart.
                if (!CopyDirect(coarse, coarseX, dst, sizeX, sizeZ, stop)) return null;
            }
            else if (categorical)
            {
                if (!UpsampleNearest(coarse, coarseX, stride, dst, sizeX, sizeZ, stop)) return null;
            }
            else
            {
                if (!UpsampleBilinear(coarse, coarseX, coarseZ, stride, dst, sizeX, sizeZ, stop)) return null;
            }

            return dst;
        }

        // -------------------------------------------------------------------------
        //  Sampling
        // -------------------------------------------------------------------------

        /// <summary>
        /// Fills the coarse lattice by sampling the environment. Returns false when
        /// cancelled, so the caller can drop the partial result rather than publish it.
        /// </summary>
        private bool SampleCoarse(float[] coarse, int coarseX, int coarseZ, int stride,
                                  int sizeX, int sizeZ, Matrix dst, EcoScatterContext ctx,
                                  int outputIndex, StopToken stop)
        {
            int offX = dst.rect.offset.x;
            int offZ = dst.rect.offset.z;

            int isolate = (int)isolateZone;

            for (int cz = 0; cz < coarseZ; cz++)
            {
                if (stop != null && stop.StopRequested) return false;

                // Clamped rather than extrapolated: the last anchor sits on the final
                // pixel, so the edge is interpolated from a real site instead of one
                // outside the tile that the sampler would have to invent.
                int z = offZ + Mathf.Min(cz * stride, sizeZ - 1);
                int row = cz * coarseX;

                for (int cx = 0; cx < coarseX; cx++)
                {
                    int x = offX + Mathf.Min(cx * stride, sizeX - 1);

                    // The shipped lattice-to-world conversion, not a second copy of it.
                    Vector3 world = dst.PixelToWorld(x, z);

                    EnvironmentSample site = ctx.sampler.Sample(world.x, world.z);

                    coarse[row + cx] = FieldValue(outputIndex, in site, isolate);
                }
            }

            return true;
        }

        /// <summary>
        /// The one place a port index becomes a number. Everything is returned already
        /// mapped to 0-1 so the upsamplers need no knowledge of what they carry.
        /// </summary>
        private float FieldValue(int outputIndex, in EnvironmentSample site, int isolate)
        {
            switch (outputIndex)
            {
                case OutSoilMoisture:
                    // Already a 0-1 plant-available fraction.
                    return Mathf.Clamp01(site.soilMoisture);

                case OutWetnessIndex:
                    return Mathf.Clamp01(site.twi / Mathf.Max(0.01f, wetnessIndexCeiling));

                case OutAridity:
                    return Mathf.Clamp01(site.aridity / Mathf.Max(0.01f, aridityCeiling));

                case OutWindExposure:
                    return Mathf.Clamp01(site.windExposure);

                case OutLight:
                    return Mathf.Clamp01(LightFraction(in site));

                case OutZoneId:
                    // Normalised so the ramp spans the enum. Seventeen grey steps is a
                    // poor thing to eyeball, which is exactly why Zone Mask exists.
                    return (int)LandformAssemblage.Classify(in site) *
                           (1f / (LandformAssemblage.ZoneCount - 1));

                case OutZoneMask:
                    // Exact test. See the header on why no distance softening.
                    return (int)LandformAssemblage.Classify(in site) == isolate ? 1f : 0f;
            }

            return 0f;
        }

        private float LightFraction(in EnvironmentSample site)
        {
            switch (lightLayer)
            {
                case LightLayer.Canopy: return site.subcanopyLightFraction;
                case LightLayer.Understory: return site.canopyLightFraction;
                default: return site.floorLightFraction;
            }
        }

        // -------------------------------------------------------------------------
        //  Upsampling
        // -------------------------------------------------------------------------

        private static bool CopyDirect(float[] coarse, int coarseX, Matrix dst,
                                       int sizeX, int sizeZ, StopToken stop)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return false;

                int src = z * coarseX;
                int row = z * sizeX;

                for (int x = 0; x < sizeX; x++) dst.arr[row + x] = coarse[src + x];
            }

            return true;
        }

        /// <summary>Nearest anchor, for the categorical outputs.</summary>
        private static bool UpsampleNearest(float[] coarse, int coarseX, int stride,
                                            Matrix dst, int sizeX, int sizeZ, StopToken stop)
        {
            float inv = 1f / stride;

            for (int z = 0; z < sizeZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return false;

                int cz = Mathf.RoundToInt(z * inv);
                int src = cz * coarseX;
                int row = z * sizeX;

                for (int x = 0; x < sizeX; x++)
                    dst.arr[row + x] = coarse[src + Mathf.RoundToInt(x * inv)];
            }

            return true;
        }

        /// <summary>Bilinear between anchors, for the continuous outputs.</summary>
        private static bool UpsampleBilinear(float[] coarse, int coarseX, int coarseZ,
                                             int stride, Matrix dst,
                                             int sizeX, int sizeZ, StopToken stop)
        {
            float inv = 1f / stride;
            int maxCX = coarseX - 1;
            int maxCZ = coarseZ - 1;

            for (int z = 0; z < sizeZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return false;

                float fz = z * inv;
                int z0 = Mathf.Min((int)fz, maxCZ);
                int z1 = Mathf.Min(z0 + 1, maxCZ);
                float tz = fz - z0;

                int rowA = z0 * coarseX;
                int rowB = z1 * coarseX;
                int row = z * sizeX;

                for (int x = 0; x < sizeX; x++)
                {
                    float fx = x * inv;
                    int x0 = Mathf.Min((int)fx, maxCX);
                    int x1 = Mathf.Min(x0 + 1, maxCX);
                    float tx = fx - x0;

                    float top = Mathf.Lerp(coarse[rowA + x0], coarse[rowA + x1], tx);
                    float bottom = Mathf.Lerp(coarse[rowB + x0], coarse[rowB + x1], tx);

                    dst.arr[row + x] = Mathf.Lerp(top, bottom, tz);
                }
            }

            return true;
        }
    }
}
