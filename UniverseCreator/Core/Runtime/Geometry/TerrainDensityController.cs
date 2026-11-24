// -----------------------------------------------------------------------------
//  Universe Creator - Geometry, density and streaming
//
//  File:    TerrainDensityController.cs
//  Purpose: Owns the DECISION of when to write Terrain.heightmapPixelError, and
//           nothing else. The maths of what the value should be belongs to
//           AngularError; the terrain object belongs to the chunk manager. This
//           class sits between them and answers one question cheaply: is this new
//           pixel error different enough from the last one written to this cell to
//           be worth the write?
//
//           Assigning heightmapPixelError is not free - it rebuilds the terrain
//           patch LOD tree. Doing that every frame, for a value that moved a
//           hundredth of a pixel because the camera drifted, is a measurable
//           per-frame cost that buys no visible change. A hysteresis band absorbs
//           that churn: the property is written only when the value crosses the
//           band, so a still camera settles to zero writes per frame.
//
//           The last written value is kept PER CELL, never globally. A single
//           global "last value" would let one cell's write suppress another's -
//           the near cell and the far cell legitimately hold different errors, and
//           a global gate would starve whichever one lost the race each frame. The
//           write counters are exposed so a debug window can prove the still-camera
//           guarantee rather than assert it.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Per-cell hysteresis gate for the terrain heightmap pixel-error write. Pure
    /// state and arithmetic - no Unity terrain references - so it is exercised in full
    /// by the self-test without a scene. The chunk manager owns one instance and asks
    /// it, per resident cell per frame, whether the freshly derived error is worth
    /// assigning.
    /// </summary>
    public sealed class TerrainDensityController
    {
        /// <summary>The default hysteresis band in pixels. Below this the write is skipped.</summary>
        public const float DefaultHysteresisPixels = 0.75f;

        private readonly Dictionary<long, float> _lastWritten = new Dictionary<long, float>();
        private int _writesThisFrame;
        private int _writesTotal;

        /// <summary>Writes performed since the last <see cref="BeginFrame"/> - the debug-window counter.</summary>
        public int WritesThisFrame => _writesThisFrame;

        /// <summary>Writes performed over the controller's lifetime.</summary>
        public int WritesTotal => _writesTotal;

        /// <summary>Cells currently tracked (one per resident cell that has ever been written).</summary>
        public int TrackedCellCount => _lastWritten.Count;

        /// <summary>Zeroes the per-frame counter. Call once at the top of the frame's density pass.</summary>
        public void BeginFrame()
        {
            _writesThisFrame = 0;
        }

        /// <summary>
        /// Packs a signed cell coordinate pair into one dictionary key. Uses the low 32
        /// bits of each so negative coordinates (cells left of / below the origin) do not
        /// collide with positive ones.
        /// </summary>
        public static long CellKey(int cellX, int cellZ)
        {
            return ((long)cellX << 32) | (uint)cellZ;
        }

        /// <summary>
        /// Derives the pixel error for a cell (via <see cref="AngularError.PixelErrorForTerrain"/>)
        /// and reports whether it should be written. Returns true and updates the remembered
        /// value only when the new error differs from the last written value for this cell by
        /// more than <paramref name="hysteresisPixels"/>; otherwise returns false and writes
        /// nothing, so the caller skips the assignment and its LOD-tree rebuild. The first
        /// resolve for a cell always writes, establishing the baseline.
        /// </summary>
        public bool TryResolve(int cellX, int cellZ, float acuityPixels, float texelMetres,
                               float distanceM, float verticalFovDeg, int screenHeightPixels,
                               float pixelErrorFloor, float hysteresisPixels, out float pixelError)
        {
            pixelError = AngularError.PixelErrorForTerrain(acuityPixels, texelMetres,
                distanceM, verticalFovDeg, screenHeightPixels, pixelErrorFloor);

            long key = CellKey(cellX, cellZ);
            float band = Mathf.Max(0f, hysteresisPixels);
            if (_lastWritten.TryGetValue(key, out float previous)
                && Mathf.Abs(pixelError - previous) <= band)
            {
                return false;   // inside the band - the write is not worth its rebuild
            }

            _lastWritten[key] = pixelError;
            _writesThisFrame++;
            _writesTotal++;
            return true;
        }

        /// <summary>Drops a cell's remembered value when it leaves residency, so its next
        /// entry re-establishes a baseline rather than comparing against a stale error.</summary>
        public void Forget(int cellX, int cellZ)
        {
            _lastWritten.Remove(CellKey(cellX, cellZ));
        }

        /// <summary>Clears all remembered values and both counters (a full grid rebuild).</summary>
        public void Reset()
        {
            _lastWritten.Clear();
            _writesThisFrame = 0;
            _writesTotal = 0;
        }
    }
}
