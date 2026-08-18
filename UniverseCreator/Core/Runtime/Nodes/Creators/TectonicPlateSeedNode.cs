// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    TectonicPlateSeedNode.cs
//  Purpose: Plate model with drift. Boundaries are classified by relative plate
//           motion, so convergent margins build mountains and divergent margins
//           open rift valleys and mid-ocean ridges.
//
//  The Module 1 Tectonic Plates node raises EVERY margin, which reads as a net of
//  ridges around each cell. Real margins do three different things depending on
//  which way the two plates are moving, and that difference is what puts a trench
//  on one side of a continent and a rift lake in the middle of another.
//
//  Both nodes ship: the older one is cheaper and is the right pick when a user
//  just wants continental structure.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class TectonicPlateSeedNode : Generator
    {
        public override string NodeName { get { return "Tectonic Plate Seed"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Plate model with drift vectors. Convergence uplifts, divergence rifts."; } }

        private static readonly Port[] Outs =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true),
            new Port("Boundary", PortType.Matrix, PortDirection.Output, true),
            new Port("Plate Id", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return SharedPorts.None; }
        public override Port[] GetOutputs() { return Outs; }

        public int seed = 8080;

        /// <summary>Average plate width in world metres.</summary>
        public float plateSize = 5000f;

        /// <summary>Fraction of plates that are oceanic, 0..1.</summary>
        public float oceanFraction = 0.5f;

        /// <summary>Elevation gap between continental and oceanic crust.</summary>
        public float plateRelief = 0.35f;

        /// <summary>Uplift at a fully convergent margin.</summary>
        public float convergentUplift = 0.5f;

        /// <summary>Depth of the rift at a fully divergent margin.</summary>
        public float divergentRift = 0.22f;

        /// <summary>Margin width as a fraction of plate size.</summary>
        public float marginWidth = 0.14f;

        /// <summary>Depth of the trench on the ocean side of a convergent margin.</summary>
        public float trenchDepth = 0.18f;

        /// <summary>Irregularity of the plate borders. 0 gives straight edges.</summary>
        public float borderWarp = 0.35f;

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix m = tile.CreateMatrix();

            Noise noise = new Noise(seed);
            Noise warpNoise = noise.WithSubSeed(4111);
            Noise plateNoise = noise.WithSubSeed(4222);
            Noise driftNoise = noise.WithSubSeed(4333);

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            float safePlate = Mathf.Max(1f, plateSize);
            float invPlate = 1f / safePlate;
            float safeMargin = Mathf.Max(0.001f, marginWidth);

            // One plate is ~1 cell wide, so a step of a third of a plate is far
            // enough to land in the neighbour across a margin but not two cells out.
            float probe = safePlate * 0.34f;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return null;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    float sx, sz;
                    Warp(warpNoise, wx, wz, invPlate, out sx, out sz);

                    float cellular = plateNoise.Voronoi(sx, sz, 0f, VoronoiBlend.Cellular);
                    float margin = 1f - Mathf.Clamp01(cellular / safeMargin);
                    float plateId = plateNoise.Voronoi(sx, sz, 0f, VoronoiBlend.Flat);

                    if (outputIndex == 1) { m.arr[rowStart + x] = InterpolationMath.SmoothStep(margin); continue; }
                    if (outputIndex == 2) { m.arr[rowStart + x] = plateId; continue; }

                    bool isOceanic = plateId < Mathf.Clamp01(oceanFraction);
                    float baseElevation = isOceanic
                        ? 0.5f - plateRelief * 0.5f
                        : 0.5f + plateRelief * 0.5f;
                    baseElevation += (plateId - 0.5f) * plateRelief * 0.25f;

                    float h = baseElevation;

                    if (margin > 0.001f)
                    {
                        // Convergence is the divergence of the drift field measured
                        // across the margin: sample the plate on each side, project
                        // each one's drift onto the line joining them. Plates moving
                        // toward each other give a negative sum, so its negation is
                        // positive under compression.
                        float gx, gz;
                        MarginNormal(plateNoise, warpNoise, wx, wz, invPlate, probe, safeMargin, out gx, out gz);

                        float driftAX, driftAZ, driftBX, driftBZ;
                        Drift(driftNoise, plateNoise, warpNoise, wx - gx * probe, wz - gz * probe, invPlate, out driftAX, out driftAZ);
                        Drift(driftNoise, plateNoise, warpNoise, wx + gx * probe, wz + gz * probe, invPlate, out driftBX, out driftBZ);

                        float approach = (driftAX - driftBX) * gx + (driftAZ - driftBZ) * gz;

                        float shaped = InterpolationMath.SmoothStep(margin);

                        if (approach > 0f)
                        {
                            h += shaped * approach * convergentUplift;

                            // Oceanic crust subducts, so the low side of a convergent
                            // pair gets a trench rather than sharing the uplift.
                            if (isOceanic) h -= shaped * approach * trenchDepth;
                        }
                        else
                        {
                            h += shaped * approach * divergentRift;
                        }
                    }

                    m.arr[rowStart + x] = Mathf.Clamp01(h);
                }
            }

            return m;
        }

        private void Warp(Noise warpNoise, float wx, float wz, float invPlate, out float sx, out float sz)
        {
            sx = wx * invPlate;
            sz = wz * invPlate;

            if (borderWarp <= 1e-5f) return;

            sx += (warpNoise.Perlin(wx * invPlate * 2f, wz * invPlate * 2f) - 0.5f) * borderWarp;
            sz += (warpNoise.Perlin(wx * invPlate * 2f + 31.7f, wz * invPlate * 2f - 17.3f) - 0.5f) * borderWarp;
        }

        // Direction of steepest increase in border proximity: the margin's normal.
        private void MarginNormal(Noise plateNoise, Noise warpNoise, float wx, float wz,
                                  float invPlate, float probe, float safeMargin,
                                  out float gx, out float gz)
        {
            float step = probe * 0.5f;

            float left = BorderProximity(plateNoise, warpNoise, wx - step, wz, invPlate, safeMargin);
            float right = BorderProximity(plateNoise, warpNoise, wx + step, wz, invPlate, safeMargin);
            float down = BorderProximity(plateNoise, warpNoise, wx, wz - step, invPlate, safeMargin);
            float up = BorderProximity(plateNoise, warpNoise, wx, wz + step, invPlate, safeMargin);

            gx = right - left;
            gz = up - down;

            float len = Mathf.Sqrt(gx * gx + gz * gz);
            if (len < 1e-6f) { gx = 1f; gz = 0f; return; }

            gx /= len;
            gz /= len;
        }

        private float BorderProximity(Noise plateNoise, Noise warpNoise, float wx, float wz,
                                      float invPlate, float safeMargin)
        {
            float sx, sz;
            Warp(warpNoise, wx, wz, invPlate, out sx, out sz);
            float cellular = plateNoise.Voronoi(sx, sz, 0f, VoronoiBlend.Cellular);
            return 1f - Mathf.Clamp01(cellular / safeMargin);
        }

        // Each plate drifts as a rigid body, so the vector is constant per cell:
        // it is keyed off the cell's flat Voronoi id, not off the sample position.
        private void Drift(Noise driftNoise, Noise plateNoise, Noise warpNoise,
                           float wx, float wz, float invPlate, out float dx, out float dz)
        {
            float sx, sz;
            Warp(warpNoise, wx, wz, invPlate, out sx, out sz);

            float plateId = plateNoise.Voronoi(sx, sz, 0f, VoronoiBlend.Flat);
            int key = Mathf.RoundToInt(plateId * 65536f);

            float angle = driftNoise.Random(key) * Mathf.PI * 2f;
            float speed = 0.35f + driftNoise.Random(key + 7919) * 0.65f;

            dx = Mathf.Cos(angle) * speed;
            dz = Mathf.Sin(angle) * speed;
        }
    }
}
