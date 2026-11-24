// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    IrradianceGrid.cs
//  Purpose: The grid of ambient-cube probes that carries the module's bounce light. It
//           is a flat one-dimensional array, indexed by hand - a managed 3-D array would
//           pay two extra bounds checks on every access along the hot path, and native
//           containers are out - and it is surface-following: each horizontal column's
//           vertical stack starts at the terrain height beneath it and rises a fixed
//           number of levels, so a mountainous world spends its probes in the thin shell
//           of air near the ground where light actually changes, not underground or in
//           empty sky. Horizontal and vertical spacings differ on purpose.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// A surface-following grid of <see cref="IrradianceProbe"/> in one flat array,
    /// indexed <c>y * (dimX * dimZ) + z * dimX + x</c>. Columns are laid on a horizontal
    /// lattice from a world origin at the horizontal spacing; each column's stack begins
    /// at the terrain height under it and rises at the (denser) vertical spacing.
    /// </summary>
    public sealed class IrradianceGrid
    {
        public readonly int dimX;
        public readonly int dimZ;
        public readonly int dimY;
        public readonly float spacingM;
        public readonly float verticalSpacingM;
        public readonly Vector2 originXZ;   // world XZ of column (x=0, z=0)

        public readonly IrradianceProbe[] probes;   // flat, length dimX * dimZ * dimY
        private readonly float[] columnBaseY;        // terrain height under each (x,z) column

        /// <summary>
        /// Lay out a grid covering a horizontal region from an origin, following the
        /// terrain height sampled per column. Levels and spacings default to the module
        /// constants. <paramref name="terrainHeightAt"/> returns world height at a world
        /// XZ, so the grid needs no reference to the terrain system itself.
        /// </summary>
        public IrradianceGrid(Vector2 originXZ, float regionMetresX, float regionMetresZ,
                              Func<float, float, float> terrainHeightAt)
            : this(originXZ, regionMetresX, regionMetresZ,
                   LightingConstants.IrradianceProbeSpacingM,
                   LightingConstants.IrradianceVerticalSpacingM,
                   LightingConstants.IrradianceVerticalLevels, terrainHeightAt)
        {
        }

        /// <summary>
        /// As above, but with the authored horizontal spacing, vertical spacing and level
        /// count supplied by the caller - the overload the lighting manager uses so the
        /// grid honours the LightingSettings.probeSpacing dial (scaled by the resolved tier)
        /// rather than only the module default. A coarser spacing lays fewer columns, which
        /// is how a lower tier spends fewer probes over the same world.
        /// </summary>
        public IrradianceGrid(Vector2 originXZ, float regionMetresX, float regionMetresZ,
                              float spacingMetres, float verticalSpacingMetres, int levels,
                              Func<float, float, float> terrainHeightAt)
        {
            this.originXZ = originXZ;
            spacingM = spacingMetres > 0f ? spacingMetres : LightingConstants.IrradianceProbeSpacingM;
            verticalSpacingM = verticalSpacingMetres > 0f ? verticalSpacingMetres : LightingConstants.IrradianceVerticalSpacingM;
            dimY = Mathf.Max(1, levels);
            dimX = ProbesAcross(regionMetresX, spacingM);
            dimZ = ProbesAcross(regionMetresZ, spacingM);

            columnBaseY = new float[dimX * dimZ];
            probes = new IrradianceProbe[dimX * dimZ * dimY];

            for (int z = 0; z < dimZ; z++)
            {
                for (int x = 0; x < dimX; x++)
                {
                    float wx = originXZ.x + x * spacingM;
                    float wz = originXZ.y + z * spacingM;
                    columnBaseY[z * dimX + x] = terrainHeightAt != null ? terrainHeightAt(wx, wz) : 0f;
                }
            }
        }

        /// <summary>Probes along one axis over a region: an inclusive lattice, so a
        /// region an exact multiple of the spacing gets a probe on both edges.</summary>
        public static int ProbesAcross(float regionMetres, float spacingMetres)
        {
            if (spacingMetres <= 0f) return 1;
            return Mathf.FloorToInt(Mathf.Max(0f, regionMetres) / spacingMetres) + 1;
        }

        /// <summary>The flat index of probe (x, y, z), by the module's fixed convention.</summary>
        public int Index(int x, int y, int z)
        {
            return y * (dimX * dimZ) + z * dimX + x;
        }

        /// <summary>The inverse of <see cref="Index"/>: recover (x, y, z) from a flat index,
        /// so a round-robin fill can step one probe at a time without tracking three counters.</summary>
        public void ToXYZ(int flat, out int x, out int y, out int z)
        {
            int layer = dimX * dimZ;
            y = flat / layer;
            int rem = flat - y * layer;
            z = rem / dimX;
            x = rem - z * dimX;
        }

        /// <summary>The world position of probe (x, y, z): the column's lattice point,
        /// lifted from the terrain height under it by the level and vertical spacing.</summary>
        public Vector3 WorldPosition(int x, int y, int z)
        {
            float wx = originXZ.x + x * spacingM;
            float wz = originXZ.y + z * spacingM;
            float wy = columnBaseY[z * dimX + x] + y * verticalSpacingM;
            return new Vector3(wx, wy, wz);
        }

        /// <summary>The terrain height the column under (x, z) was built from.</summary>
        public float ColumnBaseY(int x, int z)
        {
            return columnBaseY[z * dimX + x];
        }

        /// <summary>Updates one existing column during a budgeted refill without reallocating the grid; invalid coordinates/heights return false.</summary>
        /// <param name="x">Column X index in [0, dimX).</param>
        /// <param name="z">Column Z index in [0, dimZ).</param>
        /// <param name="heightMetres">Finite accepted world-space ground height in metres.</param>
        /// <returns>True only when the stored height changed; main-thread grid owner must reupload its base-height texture.</returns>
        internal bool RefreshColumnHeight(int x, int z, float heightMetres)
        {
            if (x < 0 || x >= dimX || z < 0 || z >= dimZ || float.IsNaN(heightMetres) || float.IsInfinity(heightMetres)) return false;
            int index = z * dimX + x;
            if (columnBaseY[index] == heightMetres) return false;
            columnBaseY[index] = heightMetres; return true; // UC-T08-12: same-run terrain publication drains through the existing probe budget.
        }

        public int ProbeCount { get { return probes.Length; } }
    }
}
