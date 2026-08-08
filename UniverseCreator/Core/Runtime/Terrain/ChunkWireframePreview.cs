// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    ChunkWireframePreview.cs
//  Purpose: Low-poly wireframe stand-in shown while a chunk generates.
//
//  WHY THIS EXISTS
//  ---------------
//  Generation runs on a worker thread and takes long enough to notice. Without a
//  stand-in the user sees either empty space or the previous heightfield, and has
//  no way to tell whether the system is working or has hung.
//
//  WHY LINE TOPOLOGY AND NOT GIZMOS
//  --------------------------------
//  Gizmos only draw in the editor's Scene view. Streaming happens in play mode
//  too, so the preview has to be real geometry: a MeshTopology.Lines mesh is one
//  draw call, needs no collider, and costs a few hundred verts per chunk.
//
//  WHY IT SAMPLES THE OLD HEIGHTS
//  ------------------------------
//  On a regeneration the previous heightfield is still the best available guess at
//  the new shape, so downsampling it gives a preview that actually resembles the
//  terrain. Only a first-ever generation falls back to a flat grid.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Builds and drives the wireframe grid mesh used as a placeholder while a chunk
    /// is being generated. Main-thread only.
    /// </summary>
    public static class ChunkWireframePreview
    {
        // Grid divisions per axis. 16 reads as terrain rather than as a flat quad,
        // while staying far below any vertex limit worth thinking about.
        public const int Divisions = 16;

        /// <summary>
        /// Fills a mesh with a line grid spanning a chunk footprint. Pass the previous
        /// heightfield to shape it, or null for a flat grid at ground level.
        /// heights is normalised 0..1 and indexed [z, x], matching Unity.
        /// </summary>
        public static void Build(Mesh mesh, float[,] heights, float chunkSize, float maxHeight)
        {
            if (mesh == null) return;

            int div = Divisions;
            int side = div + 1;
            int vertCount = side * side;

            Vector3[] verts = new Vector3[vertCount];
            float step = chunkSize / div;

            int hz = heights != null ? heights.GetLength(0) : 0;
            int hx = heights != null ? heights.GetLength(1) : 0;

            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < side; x++)
                {
                    float y = 0f;

                    if (hz > 1 && hx > 1)
                    {
                        // Nearest-sample downsample. Interpolating would be smoother but
                        // this is a placeholder that lives for a few frames.
                        int sx = Mathf.Clamp(Mathf.RoundToInt(x / (float)div * (hx - 1)), 0, hx - 1);
                        int sz = Mathf.Clamp(Mathf.RoundToInt(z / (float)div * (hz - 1)), 0, hz - 1);
                        y = heights[sz, sx] * maxHeight;
                    }

                    verts[z * side + x] = new Vector3(x * step, y, z * step);
                }
            }

            // Two families of polylines: one along X per row, one along Z per column.
            int segments = side * div * 2;
            int[] indices = new int[segments * 2];
            int w = 0;

            for (int z = 0; z < side; z++)
            {
                for (int x = 0; x < div; x++)
                {
                    indices[w++] = z * side + x;
                    indices[w++] = z * side + x + 1;
                }
            }

            for (int x = 0; x < side; x++)
            {
                for (int z = 0; z < div; z++)
                {
                    indices[w++] = z * side + x;
                    indices[w++] = (z + 1) * side + x;
                }
            }

            mesh.Clear();
            mesh.vertices = verts;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);

            // Bounds by hand: RecalculateBounds on a line mesh is fine, but the height
            // range is already known and this avoids a pass over the vertices.
            mesh.bounds = new Bounds(new Vector3(chunkSize * 0.5f, maxHeight * 0.5f, chunkSize * 0.5f),
                                     new Vector3(chunkSize, maxHeight, chunkSize));
        }

        /// <summary>
        /// Best-effort unlit material for the preview lines. Render pipelines disagree
        /// about which shaders exist, so this walks a fallback chain rather than
        /// assuming one; returns null if nothing usable is present.
        /// </summary>
        public static Material CreateFallbackMaterial(Color colour)
        {
            string[] candidates =
            {
                "Unlit/Color",                       // built-in render pipeline
                "Universal Render Pipeline/Unlit",   // URP
                "HDRP/Unlit",                        // HDRP
                "Sprites/Default",                   // always present, ignores lighting
                "Hidden/Internal-Colored"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                Shader s = Shader.Find(candidates[i]);
                if (s == null) continue;

                Material m = new Material(s);
                m.hideFlags = HideFlags.DontSave;

                // Property name varies by pipeline; set whichever the shader actually has.
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
                if (m.HasProperty("_Color")) m.SetColor("_Color", colour);

                return m;
            }

            return null;
        }
    }
}
