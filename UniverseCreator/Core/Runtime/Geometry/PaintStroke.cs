// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PaintStroke.cs
//  Purpose: The load-bearing decision of non-destructive painting (§ 9.8 G-48): a stroke
//           is stored in WORLD METRES, never as a rasterised texel footprint. A
//           `PaintStroke` is a world-space centre, a radius in metres, a strength, a
//           falloff exponent and a target layer - a description of intent, not a picture.
//           Everything the section needs follows from that one choice:
//             - changing heightmapResolution / alphamapResolution / cell size re-composites
//               the SAME strokes at the new grid and the paint still lines up, because a
//               stroke is evaluated as a continuous function of world position; a texel
//               store would be silently destroyed by any of those changes;
//             - undo/redo is a stroke-LIST operation, so a hundred steps of history cost
//               kilobytes of structs, not a texture per step;
//             - a world can be re-seeded and the paint survives, because a stroke's anchor
//               is a place, not a pixel.
//
//  THE FALLOFF IS A FUNCTION OF WORLD DISTANCE
//  -------------------------------------------
//  A stroke's weight at a point is strength · (1 − d/r)^exponent for d < r and 0 beyond,
//  where d and r are both in metres. The painted edge sits at exactly the world radius
//  regardless of how finely the grid samples it - which is precisely why doubling the
//  alphamap resolution cannot move an edge.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  No Texture, no Terrain. The self-test pins the resolution-independent edge, the
//  monotone falloff, and a hundred-step undo/redo that allocates no raster. Compositing
//  the evaluated weights into the alphamap is the scene layer above.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>A paint stroke stored in world metres: a place, a size and an intent - never a rasterised
    /// footprint. Re-composited at any resolution and survives a re-seed because its anchor is a world position.</summary>
    public readonly struct PaintStroke
    {
        public readonly float WorldX;
        public readonly float WorldZ;
        public readonly float RadiusM;
        public readonly float Strength;
        public readonly float FalloffExponent;
        public readonly int TargetLayer;

        public PaintStroke(float worldX, float worldZ, float radiusM, float strength, float falloffExponent, int targetLayer)
        {
            WorldX = worldX; WorldZ = worldZ; RadiusM = radiusM;
            Strength = strength; FalloffExponent = falloffExponent; TargetLayer = targetLayer;
        }
    }

    /// <summary>Pure world-space evaluation of a stroke's weight, and a stroke-list undo/redo history that costs no
    /// raster. Device-free: the whole point is that a stroke is a continuous function of world position.</summary>
    public static class PaintStrokeMath
    {
        /// <summary>The normalised brush falloff at a world distance: (1 − d/r)^exponent for d &lt; r, else 0. One at
        /// the centre, zero at exactly the radius (in metres), monotone between. Returns 0 on a non-positive radius.</summary>
        public static float NormalizedFalloff(float distanceM, float radiusM, float falloffExponent)
        {
            if (radiusM <= 0f) return 0f;
            float t = distanceM / radiusM;
            if (t >= 1f) return 0f;
            if (t < 0f) t = 0f;
            return Mathf.Pow(1f - t, falloffExponent);
        }

        /// <summary>The stroke's weight at a world position: strength · falloff(distance). A function of world
        /// metres alone, so the same stroke evaluates identically at any grid resolution.</summary>
        public static float Weight(in PaintStroke s, float worldX, float worldZ)
        {
            float dx = worldX - s.WorldX, dz = worldZ - s.WorldZ;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            return s.Strength * NormalizedFalloff(d, s.RadiusM, s.FalloffExponent);
        }
    }

    /// <summary>
    /// Undo/redo as a pure stroke-list operation: a list plus an active-count pointer. Adding truncates the redo
    /// tail; undo/redo move the pointer. No texture is ever allocated, so a hundred steps of history is kilobytes.
    /// </summary>
    public sealed class PaintStrokeHistory
    {
        private readonly List<PaintStroke> _strokes = new List<PaintStroke>();
        private int _active;   // number of strokes currently applied (0.._strokes.Count)

        public int ActiveCount => _active;
        public bool CanUndo => _active > 0;
        public bool CanRedo => _active < _strokes.Count;
        public PaintStroke this[int i] => _strokes[i];

        /// <summary>Record a stroke: drop any undone (redo) tail, then append and apply it.</summary>
        public void Add(in PaintStroke s)
        {
            if (_active < _strokes.Count) _strokes.RemoveRange(_active, _strokes.Count - _active);
            _strokes.Add(s);
            _active = _strokes.Count;
        }

        /// <summary>Walk back one stroke by moving the pointer - no raster touched. Returns false at the start.</summary>
        public bool Undo() { if (_active == 0) return false; _active--; return true; }

        /// <summary>Re-apply one undone stroke by moving the pointer. Returns false when nothing is redoable.</summary>
        public bool Redo() { if (_active >= _strokes.Count) return false; _active++; return true; }
    }
}
