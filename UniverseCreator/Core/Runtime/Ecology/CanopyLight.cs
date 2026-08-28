// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    CanopyLight.cs
//  Purpose: The light budget of one chunk. Stems deposit leaf area into a coarse
//           grid as they are placed; everything placed afterwards is evaluated
//           against the light that is actually left (L-17, Beer-Lambert).
//
//  THIS IS THE FILE THAT MAKES A FOREST LAYERED. Without it every species is
//  scored against full sun, so the species that likes full sun wins everywhere
//  and the result is one plant repeated - a mat, not a wood. Shade tolerance is
//  already an authored field and already read by SpeciesSuitability; until now the
//  light it was compared against was the constant 1.0. This supplies the real one.
//
//  ORDER, NOT ITERATION. Section 1.2 forbids iterative succession - no growing the
//  forest for n simulated years. So light flows exactly one way, top down, in a
//  single pass: Emergent stems shade the Canopy, those two shade the Understory,
//  those three shade the GroundCover. Each layer is placed once, against the light
//  the layers above it have already taken. Nothing is revisited.
//
//  LEAF AREA IS CONSERVED, NOT SPRINKLED. A crown holds laiContribution square
//  metres of leaf per square metre of its own footprint. That total is deposited
//  over the cells the crown covers and no more, so a wide crown shades a wide area
//  thinly and a narrow crown shades a small area deeply. Adding the LAI figure to
//  every covered cell instead - the obvious shortcut - would make a big tree cast
//  the same depth of shade over ten times the ground, and a mature forest would go
//  black.
//
//  4 m CELLS. One mature crown across. Fine enough that a clearing reads as a
//  clearing and a canopy edge as an edge; coarse enough that a 1 km chunk is a
//  250-square grid instead of a million cells. Grass is queried per detail cell -
//  a metre or less - so it reads this grid interpolated rather than nearest, or
//  the shade under a wood would come out in 4 m squares.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Per-chunk canopy leaf area, one plane per structural layer, and the
    /// transmittance queries the species model reads from it.
    /// </summary>
    public sealed class CanopyLightGrid
    {
        // One plane per CanopyLayer value. Indexed by (int)layer, so adding a layer
        // to the enum needs no change here beyond the enum's own length.
        private const int LayerCount = 4;

        private float[][] lai;

        private int width;
        private int height;

        private float originX;
        private float originZ;
        private float cellM = EcoConstants.CanopyGridCellMetres;

        public bool IsBound { get { return lai != null; } }

        public int Width { get { return width; } }
        public int Height { get { return height; } }

        /// <summary>
        /// Sizes the grid to a chunk's world footprint. Cheap enough to call per
        /// chunk; the caller is expected to hold one grid per chunk and no more.
        /// </summary>
        public void Bind(float worldX, float worldZ, float worldSizeX, float worldSizeZ)
        {
            cellM = Mathf.Max(0.5f, EcoConstants.CanopyGridCellMetres);

            originX = worldX;
            originZ = worldZ;

            // One cell of margin on each side. A crown centred just inside the chunk
            // edge spills outward, and clamping that deposit back into the last cell
            // would double the shade along every chunk boundary - a visible grid of
            // dark seams across the world.
            width = Mathf.Max(1, Mathf.CeilToInt(worldSizeX / cellM) + 2);
            height = Mathf.Max(1, Mathf.CeilToInt(worldSizeZ / cellM) + 2);

            originX -= cellM;
            originZ -= cellM;

            lai = new float[LayerCount][];

            for (int i = 0; i < LayerCount; i++)
                lai[i] = new float[width * height];
        }

        public void Clear()
        {
            if (lai == null) return;

            for (int i = 0; i < LayerCount; i++)
                System.Array.Clear(lai[i], 0, lai[i].Length);
        }

        /// <summary>
        /// Deposits one crown's leaf area. Total leaf area is laiContribution times
        /// the crown's own footprint; it is spread over the cells the crown covers so
        /// the total is conserved whatever the crown's size relative to a cell.
        /// </summary>
        public void Deposit(float worldX, float worldZ, float crownRadiusM,
                            float laiContribution, CanopyLayer layer)
        {
            if (lai == null) return;
            if (laiContribution <= 0f) return;

            int plane = (int)layer;
            if (plane < 0 || plane >= LayerCount) return;

            float r = Mathf.Max(0f, crownRadiusM);
            float cellArea = cellM * cellM;

            float totalLeafArea = laiContribution * Allometry.CrownAreaM2(r);
            if (totalLeafArea <= 0f) return;

            float fx = (worldX - originX) / cellM;
            float fz = (worldZ - originZ) / cellM;

            int span = Mathf.CeilToInt(r / cellM);

            int cx = Mathf.FloorToInt(fx);
            int cz = Mathf.FloorToInt(fz);

            // Cells whose centre lies inside the crown. Counted first so the total can
            // be divided among them; two passes over at most a few dozen cells is
            // cheaper than the alternative of computing exact circle-square overlap.
            int covered = 0;

            for (int z = cz - span; z <= cz + span; z++)
            {
                if (z < 0 || z >= height) continue;

                for (int x = cx - span; x <= cx + span; x++)
                {
                    if (x < 0 || x >= width) continue;

                    float dx = (x + 0.5f - fx) * cellM;
                    float dz = (z + 0.5f - fz) * cellM;

                    if (dx * dx + dz * dz <= r * r) covered++;
                }
            }

            // A crown narrower than a cell covers no cell centre. It still holds leaves,
            // so all of them go into the cell it stands in - which is also the honest
            // answer at this resolution.
            if (covered <= 0)
            {
                int x0 = Mathf.Clamp(cx, 0, width - 1);
                int z0 = Mathf.Clamp(cz, 0, height - 1);

                lai[plane][z0 * width + x0] += totalLeafArea / cellArea;
                return;
            }

            float perCell = totalLeafArea / covered / cellArea;

            for (int z = cz - span; z <= cz + span; z++)
            {
                if (z < 0 || z >= height) continue;

                int row = z * width;

                for (int x = cx - span; x <= cx + span; x++)
                {
                    if (x < 0 || x >= width) continue;

                    float dx = (x + 0.5f - fx) * cellM;
                    float dz = (z + 0.5f - fz) * cellM;

                    if (dx * dx + dz * dz > r * r) continue;

                    lai[plane][row + x] += perCell;
                }
            }
        }

        /// <summary>
        /// Cumulative leaf area strictly above <paramref name="layer"/>, bilinearly
        /// interpolated. Interpolated and not nearest because grass samples this at
        /// sub-metre spacing and a nearest read would tile the shade into 4 m squares.
        /// </summary>
        public float LaiAbove(float worldX, float worldZ, CanopyLayer layer)
        {
            if (lai == null) return 0f;

            int planes = (int)layer;
            if (planes <= 0) return 0f;
            if (planes > LayerCount) planes = LayerCount;

            float fx = (worldX - originX) / cellM - 0.5f;
            float fz = (worldZ - originZ) / cellM - 0.5f;

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);

            float tx = fx - x0;
            float tz = fz - z0;

            int x1 = x0 + 1;
            int z1 = z0 + 1;

            x0 = Mathf.Clamp(x0, 0, width - 1);
            x1 = Mathf.Clamp(x1, 0, width - 1);
            z0 = Mathf.Clamp(z0, 0, height - 1);
            z1 = Mathf.Clamp(z1, 0, height - 1);

            float total = 0f;

            for (int p = 0; p < planes; p++)
            {
                float[] a = lai[p];

                float v00 = a[z0 * width + x0];
                float v10 = a[z0 * width + x1];
                float v01 = a[z1 * width + x0];
                float v11 = a[z1 * width + x1];

                float bottom = Mathf.Lerp(v00, v10, tx);
                float top = Mathf.Lerp(v01, v11, tx);

                total += Mathf.Lerp(bottom, top, tz);
            }

            return total;
        }

        /// <summary>
        /// Fraction of open-sky light reaching the top of <paramref name="layer"/>.
        /// Emergent is above everything and always gets 1.
        /// </summary>
        public float LightReaching(float worldX, float worldZ, CanopyLayer layer)
        {
            float above = LaiAbove(worldX, worldZ, layer);
            if (above <= 0f) return 1f;

            return EcoMath.BeerLambertTransmittance(
                Mathf.Min(above, EcoConstants.LeafAreaIndexMax));
        }

        /// <summary>Total leaf area index at a position, all layers. For the readout.</summary>
        public float TotalLai(float worldX, float worldZ)
        {
            return LaiAbove(worldX, worldZ, (CanopyLayer)LayerCount);
        }
    }

    public static class CanopyLight
    {
        /// <summary>
        /// True when this species is one of the layers that shades what is below it.
        /// GroundCover is excluded not because moss holds no leaves but because
        /// nothing is placed beneath it, so its deposit could only shade itself.
        /// </summary>
        public static bool Shades(CanopyLayer layer)
        {
            return layer == CanopyLayer.Emergent
                || layer == CanopyLayer.Canopy
                || layer == CanopyLayer.Understory;
        }

        /// <summary>
        /// The light figure a species of this layer competes in. Reads the three
        /// planes the sampler has already resolved rather than the grid, so scoring a
        /// species costs no grid lookups at all.
        /// </summary>
        public static float AvailableTo(CanopyLayer layer, in EnvironmentSample site)
        {
            if (layer == CanopyLayer.Emergent) return 1f;
            if (layer == CanopyLayer.Canopy) return site.subcanopyLightFraction;
            if (layer == CanopyLayer.Understory) return site.canopyLightFraction;

            return site.floorLightFraction;
        }
    }
}
