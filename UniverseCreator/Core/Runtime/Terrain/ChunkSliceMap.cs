// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    ChunkSliceMap.cs
//  Purpose: Builds the single line mesh that maps the virtual chunk slicing over
//           the whole spawned terrain.
//
//  WHY THIS EXISTS
//  ---------------
//  The world is one continuous surface cut into chunkCountPerSide^2 virtual cells.
//  Nothing in the scene shows where those cuts fall, so LOD rings and per-cell
//  regeneration are invisible and easy to misread. This draws the cut lines - one
//  mesh spanning the entire terrain, not a per-chunk decoration.
//
//  WHY ONE WHOLE-WORLD MESH
//  ------------------------
//  A per-chunk overlay would mean one child per cell, which is exactly the
//  hierarchy clutter the design forbids, and the seams between overlays would
//  double-draw every shared boundary. One mesh over the whole world is a single
//  draw call and a single permanent child that never churns in play mode.
//
//  WHY LINES AND NOT GIZMOS
//  ------------------------
//  Gizmos exist only in the editor Scene view. The slice layout matters while
//  streaming, which is play mode, so this has to be real geometry.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Builds and drives the whole-world chunk boundary line mesh. Main-thread only.
    /// </summary>
    public static class ChunkSliceMap
    {
        /// <summary>Samples taken along each cell edge. Higher follows the surface
        /// more closely at the cost of vertices.</summary>
        public const int DefaultSamplesPerCell = 8;

        /// <summary>
        /// Fills a mesh with the chunk boundary grid for a whole world.
        /// Vertices are local to the world's min corner, so the holder sits there.
        /// sampleHeight takes world x/z and returns world y; pass null for a flat map
        /// at the world base, which is what an ungenerated world should show.
        /// </summary>
        public static void Build(Mesh mesh, Vector3 worldMinCorner, float cellSize,
                                 int cellsPerSide, int samplesPerCell, float heightOffset,
                                 System.Func<float, float, float> sampleHeight)
        {
            if (mesh == null) return;

            cellsPerSide = Mathf.Max(1, cellsPerSide);
            samplesPerCell = Mathf.Clamp(samplesPerCell, 1, 64);

            int lines = cellsPerSide + 1;                     // boundaries per axis
            int steps = samplesPerCell * cellsPerSide;         // segments along a boundary
            int pointsPerLine = steps + 1;
            float span = cellSize * cellsPerSide;
            float step = span / steps;

            // Two families: boundaries at constant x running along z, and the reverse.
            int vertCount = lines * pointsPerLine * 2;
            Vector3[] verts = new Vector3[vertCount];
            int[] indices = new int[lines * steps * 2 * 2];

            int v = 0;
            int w = 0;

            for (int family = 0; family < 2; family++)
            {
                for (int i = 0; i < lines; i++)
                {
                    float fixedAxis = i * cellSize;
                    int lineStart = v;

                    for (int s = 0; s < pointsPerLine; s++)
                    {
                        float moving = s * step;

                        float lx = family == 0 ? fixedAxis : moving;
                        float lz = family == 0 ? moving : fixedAxis;

                        float y = heightOffset;
                        if (sampleHeight != null)
                        {
                            float wy = sampleHeight(worldMinCorner.x + lx, worldMinCorner.z + lz);
                            y = wy - worldMinCorner.y + heightOffset;
                        }

                        verts[v++] = new Vector3(lx, y, lz);
                    }

                    for (int s = 0; s < steps; s++)
                    {
                        indices[w++] = lineStart + s;
                        indices[w++] = lineStart + s + 1;
                    }
                }
            }

            mesh.Clear();
            mesh.indexFormat = vertCount > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Best-effort unlit material for the map lines. Which unlit shader exists
        /// depends on the render pipeline, so this walks a fallback chain instead of
        /// assuming one. Returns null when nothing usable is present.
        /// </summary>
        public static Material CreateMaterial(Color colour)
        {
            return ChunkWireframePreview.CreateFallbackMaterial(colour);
        }
    }
}
