// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    StrataModifierNodes.cs
//  Purpose: Domain warp application, Voronoi rock strata, fault displacement,
//           and the coastal shelf shaper.
//
//  These four take a heightfield that is already the right SHAPE and give it the
//  structure of rock: bedding planes, offset blocks, and a submarine gradient
//  that does not simply continue the land's slope underwater.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    /// <summary>
    /// Displaces a heightfield by a warp field from Domain Warp Field. Splitting
    /// generation from application lets one field drive several branches, so a
    /// height, a mask and a splat can all be warped identically.
    /// </summary>
    [System.Serializable]
    public class DomainWarpApplyNode : Generator
    {
        public override string NodeName { get { return "Domain Warp Apply"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Displaces a heightfield by an external warp field."; } }

        private static readonly Port[] Ins =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Warp X", PortType.Matrix, PortDirection.Input),
            new Port("Warp Z", PortType.Matrix, PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.HeightOut; }

        /// <summary>Displacement at full warp, in world metres.</summary>
        public float intensity = 60f;

        /// <summary>Bicubic instead of bilinear resampling. Smoother, slower.</summary>
        public bool highQuality;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();

            Matrix warpX = GetInputMatrix(tile, 1, stop);
            Matrix warpZ = GetInputMatrix(tile, 2, stop);

            // No field means no displacement. Passing the input through unchanged
            // beats returning flat: a half-wired graph should still show terrain.
            if (warpX == null && warpZ == null) return new Matrix(src);

            if (stop != null && stop.StopRequested) return null;

            Matrix dst = tile.CreateMatrix();

            int resX = dst.rect.size.x;
            int resZ = dst.rect.size.z;
            Vector2 ps = src.PixelSize;
            if (ps.x <= 1e-9f || ps.y <= 1e-9f) return new Matrix(src);

            // Warp is authored in metres but sampling is in pixels.
            float pxPerMetreX = 1f / ps.x;
            float pxPerMetreZ = 1f / ps.y;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;

                    float ox = warpX != null ? (warpX.arr[i] - 0.5f) * 2f : 0f;
                    float oz = warpZ != null ? (warpZ.arr[i] - 0.5f) * 2f : 0f;

                    float sx = x + ox * intensity * pxPerMetreX;
                    float sz = z + oz * intensity * pxPerMetreZ;

                    dst.arr[i] = highQuality
                        ? Mathf.Clamp01(src.GetInterpolatedCubic(sx, sz))
                        : Mathf.Clamp01(src.GetInterpolated(sx, sz));
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Layered rock strata. Bedding planes are cut at a tilted angle through the
    /// heightfield and the surface is quantised to them, so erosion exposes bands
    /// the way it does in sedimentary country. Port 1 emits the rock-type id for
    /// the texture graph to colour.
    /// </summary>
    [System.Serializable]
    public class VoronoiRockStrataNode : Generator
    {
        public override string NodeName { get { return "Voronoi Rock Strata"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Tilted sedimentary bedding planes plus a rock-type mask."; } }

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("Rock Type", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.HeightIn; }
        public override Port[] GetOutputs() { return Outs; }

        public int seed = 4747;

        /// <summary>Number of distinct rock types in the sequence.</summary>
        public int cells = 6;

        /// <summary>Bed thickness as a fraction of world height.</summary>
        public float bedThickness = 0.05f;

        /// <summary>Dip of the beds in degrees. 0 lies flat.</summary>
        public float dipAngle = 8f;

        /// <summary>Compass direction the beds dip toward, in degrees.</summary>
        public float dipDirection = 45f;

        /// <summary>How hard the surface snaps to a bedding plane, 0..1.</summary>
        public float stepStrength = 0.35f;

        /// <summary>Lateral size of a rock-type domain, in world metres.</summary>
        public float domainSize = 3000f;

        /// <summary>Per-type resistance variation. Hard beds stand out as benches.</summary>
        public float hardnessVariation = 0.6f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix src = GetInputMatrix(tile, 0, stop);
            if (src == null) return tile.CreateMatrix();
            if (stop != null && stop.StopRequested) return null;

            Matrix dst = outputIndex == 0 ? new Matrix(src) : src.Blank();

            Noise noise = new Noise(seed);
            Noise typeNoise = noise.WithSubSeed(88);

            int resX = src.rect.size.x;
            int resZ = src.rect.size.z;
            float mppX = src.PixelSize.x;
            float mppZ = src.PixelSize.y;
            float originX = src.worldPos.x;
            float originZ = src.worldPos.z;

            float safeThickness = Mathf.Max(1e-4f, bedThickness);
            int safeCells = Mathf.Clamp(cells, 1, 32);
            float safeDomain = Mathf.Max(1f, domainSize);

            // The dip converts horizontal distance into an elevation offset, which
            // is what tilts the planes without rotating the heightfield.
            float dipRad = dipAngle * Mathf.Deg2Rad;
            float dirRad = dipDirection * Mathf.Deg2Rad;
            float dipTan = Mathf.Tan(Mathf.Clamp(dipRad, -1.4f, 1.4f));
            float dipX = Mathf.Cos(dirRad) * dipTan;
            float dipZ = Mathf.Sin(dirRad) * dipTan;

            float worldHeight = Mathf.Max(1e-4f, src.worldHeight);

            // Per-bed hardness, cached so every pixel in a bed agrees.
            float[] hardness = new float[safeCells];
            for (int i = 0; i < safeCells; i++)
                hardness[i] = 1f + (typeNoise.Random(i) - 0.5f) * 2f * Mathf.Clamp01(hardnessVariation);

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;
                    int i = row + x;

                    // Elevation measured relative to the tilted datum, in metres.
                    float datum = (wx - originX) * dipX + (wz - originZ) * dipZ;
                    float elevation = src.arr[i] * worldHeight - datum;

                    float bedCoord = elevation / (safeThickness * worldHeight);
                    int bedIndex = Mathf.FloorToInt(bedCoord);
                    float withinBed = bedCoord - bedIndex;

                    // Lateral domain: rock type varies across the map as well as
                    // through the sequence, so one bed is not the same rock everywhere.
                    float domain = typeNoise.Voronoi(wx / safeDomain, wz / safeDomain, 0f, VoronoiBlend.Flat);
                    int typeIndex = ((bedIndex + Mathf.RoundToInt(domain * safeCells)) % safeCells + safeCells) % safeCells;

                    if (outputIndex == 1)
                    {
                        dst.arr[i] = safeCells > 1 ? typeIndex / (float)(safeCells - 1) : 0f;
                        continue;
                    }

                    float hard = hardness[typeIndex];

                    // A hard bed resists: its top surface is pulled toward the plane
                    // above, forming a bench. A soft bed recedes.
                    float snapped = (bedIndex + (hard > 1f ? 1f : 0f)) * safeThickness * worldHeight + datum;
                    float target = snapped / worldHeight;

                    float weight = stepStrength * Mathf.Clamp01(Mathf.Abs(hard - 1f) + 0.25f);
                    weight *= 1f - InterpolationMath.SmoothStep(Mathf.Abs(withinBed - 0.5f) * 2f);

                    dst.arr[i] = Mathf.Clamp01(Mathf.Lerp(src.arr[i], target, Mathf.Clamp01(weight)));
                }
            }

            return dst;
        }
    }

    /// <summary>
    /// Fault displacement. A fault trace is traced through the tile and everything
    /// on one side is lifted, with the throw tapering to zero at the trace tips -
    /// a fault that ends abruptly reads as a terrain seam, which is exactly what
    /// this node exists to avoid.
    /// </summary>
    [System.Serializable]
    public class FaultLineDisplacementNode : ModifierNode
    {
        public override string NodeName { get { return "Fault Line Displacement"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Offsets blocks across fault traces with a tapered throw."; } }

        public int seed = 2727;

        /// <summary>Vertical throw at the fault centre, 0..1 of world height.</summary>
        public float displacement = 0.08f;

        /// <summary>Width of the damage zone the throw is smeared over, in metres.</summary>
        public float gradient = 120f;

        /// <summary>Average spacing between faults, in world metres.</summary>
        public float faultSpacing = 2500f;

        /// <summary>How much the trace meanders, 0..1.</summary>
        public float traceWarp = 0.4f;

        /// <summary>Adds a horizontal component, as in a strike-slip fault.</summary>
        public float strikeSlip;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            Noise noise = new Noise(seed);
            Noise warpNoise = noise.WithSubSeed(31);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safeSpacing = Mathf.Max(1f, faultSpacing);
            float safeGradient = Mathf.Max(1f, gradient);

            float[] src = (float[])m.arr.Clone();

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && (z & 31) == 0 && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;
                    int i = row + x;

                    float sx = wx / safeSpacing;
                    float sz = wz / safeSpacing;

                    if (traceWarp > 1e-5f)
                    {
                        sx += (warpNoise.Perlin(sx * 1.7f, sz * 1.7f) - 0.5f) * traceWarp;
                        sz += (warpNoise.Perlin(sx * 1.7f + 19.3f, sz * 1.7f - 7.1f) - 0.5f) * traceWarp;
                    }

                    // Cellular distance is 0 on the trace and rises into each block.
                    float distToTrace = noise.Voronoi(sx, sz, 0f, VoronoiBlend.Cellular) * safeSpacing;

                    // Which side: the block id decides the sign, so the same block is
                    // consistently up-thrown across the whole tile.
                    float blockId = noise.Voronoi(sx, sz, 0f, VoronoiBlend.Flat);
                    float side = blockId < 0.5f ? -1f : 1f;

                    // Taper: full throw well inside a block, zero at the trace.
                    float taper = InterpolationMath.SmoothStep(Mathf.Clamp01(distToTrace / safeGradient));

                    float throwAmount = side * displacement * taper;

                    float value = src[i] + throwAmount;

                    if (Mathf.Abs(strikeSlip) > 1e-4f)
                    {
                        // Lateral slip: resample from an offset position along the
                        // trace, which shears the surface pattern across the fault.
                        float shift = side * strikeSlip / Mathf.Max(1e-4f, mppX);
                        value = m.GetInterpolated(x + shift, z) + throwAmount;
                    }

                    m.arr[i] = Mathf.Clamp01(value);
                }
            }
        }
    }

    /// <summary>
    /// Continental shelf. Below sea level the gradient flattens to a shelf, breaks
    /// at the shelf edge, then falls away to the abyssal plain. Extending the land
    /// slope underwater instead gives a bathtub-shaped ocean floor.
    /// </summary>
    [System.Serializable]
    public class CoastalShelfShaperNode : ModifierNode
    {
        public override string NodeName { get { return "Coastal Shelf Shaper"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Flattens the near-shore sea bed into a shelf, then breaks to deep water."; } }

        /// <summary>Sea level, 0..1 of world height. The world's one waterline - see
        /// Coastal Erosion; a shelf cut at a different height than the sea stands at is
        /// a shelf in mid-air or a shelf drowned too deep to read.</summary>
        public float seaLevel = 0.35f;

        /// <summary>Depth at the shelf break, as a fraction below sea level.</summary>
        public float shelfDepth = 0.06f;

        /// <summary>How much of the submarine range the shelf occupies, 0..1.</summary>
        public float shelfExtent = 0.45f;

        /// <summary>Steepness past the shelf break. Higher gives a sharper slope.</summary>
        public float slopeSharpness = 3f;

        /// <summary>Depth of the abyssal floor, 0..1. Everything bottoms out here.</summary>
        public float abyssalDepth = 0.12f;

        /// <summary>Preserves relief on the sea bed rather than planing it smooth.</summary>
        public float reliefRetention = 0.35f;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            float sea = Mathf.Clamp01(seaLevel);
            if (sea <= 1e-5f) return;

            float shelfFloor = Mathf.Clamp01(sea - Mathf.Max(0f, shelfDepth));
            float abyss = Mathf.Clamp01(Mathf.Min(shelfFloor, abyssalDepth));
            float extent = Mathf.Clamp01(shelfExtent);
            float sharp = Mathf.Max(0.1f, slopeSharpness);
            float retain = Mathf.Clamp01(reliefRetention);

            float[] arr = m.arr;

            for (int i = 0; i < arr.Length; i++)
            {
                if (stop != null && (i & 8191) == 0 && stop.StopRequested) return;

                float h = arr[i];
                if (h >= sea) continue;

                // 0 at the shoreline, 1 at the deepest input value.
                float submarine = 1f - Mathf.Clamp01(h / sea);

                float target;
                if (submarine <= extent)
                {
                    // Shelf: nearly flat, gently dipping to the break.
                    float t = extent > 1e-5f ? submarine / extent : 0f;
                    target = Mathf.Lerp(sea, shelfFloor, InterpolationMath.SmoothStep(t) * 0.35f);
                }
                else
                {
                    // Continental slope, then the abyssal plain.
                    float t = Mathf.Clamp01((submarine - extent) / Mathf.Max(1e-5f, 1f - extent));
                    target = Mathf.Lerp(shelfFloor, abyss, Mathf.Pow(t, 1f / sharp));
                }

                // Retaining some of the original relief keeps canyons and seamounts
                // rather than replacing the sea bed with a smooth ramp.
                arr[i] = Mathf.Clamp01(Mathf.Lerp(target, h, retain * (1f - submarine)));
            }
        }
    }
}
