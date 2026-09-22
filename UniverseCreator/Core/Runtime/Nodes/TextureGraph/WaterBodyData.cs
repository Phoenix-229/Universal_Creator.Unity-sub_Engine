// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    WaterBodyData.cs
//  Purpose: One water surface as geometry - the vertex streams, the bounds, and the
//           few physical numbers the volume and the material need. A plain data
//           carrier with no behaviour, so it can be filled on a worker thread and
//           handed to the main thread to become a Mesh.
//
//  WHO FILLS IT
//  ------------
//  UCWaterBody, for every water body in the world: hand-placed from the menu, or
//  spawned by HydrologyGenerator from the drainage solve. Both go through the same
//  builder, so a lake is the same object whether a user dropped it or the hydrology
//  produced it - which is the whole point of that unification.
//
//  WHY VERTICES ARE RELATIVE TO THE BODY'S TRANSFORM
//  -------------------------------------------------
//  A world ten kilometres across puts absolute vertex coordinates far enough from
//  zero that a float mantissa starts losing centimetres, which shows up as a water
//  surface that shimmers when the camera moves and nothing else does. The vertices
//  are therefore local to the GameObject that renders them, and its transform is
//  the origin - the same one Unity already applies, so there is no second copy of
//  that position to keep in step.
//
//  REMOVED: the graph-side mesh builder that used to live here (WaterMeshBuilder)
//  and its WaterBuildSettings, along with the two Output nodes that drove them.
//  They built one throwaway mesh per chunk per body; the applier had stopped
//  consuming those meshes when discrete, editable UCWaterBody objects became the
//  single water source, so the nodes could be wired and tuned and would never put
//  water in the world. Manning's equation and the Froude whitewater term were not
//  lost with them - UCWaterBody's own river builder carries both.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// What kind of body this is. Mirrors UCWaterVolume.BodyKind, and is a separate
    /// enum on purpose: this one is graph data that gets serialized into a saved
    /// asset, and the other is a scene component's field. Tying them together would
    /// mean a rename in the scene layer silently retyping every saved graph.
    /// </summary>
    public enum WaterBodyKind
    {
        Ocean = 0,
        Lake = 1,
        River = 2,
        Pool = 3,
        Swamp = 4
    }

    /// <summary>
    /// One water surface, ready for the main thread to turn into a Mesh.
    /// </summary>
    public class WaterBodyData
    {
        public string name = "Water";
        public WaterBodyKind kind = WaterBodyKind.Lake;

        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector4[] tangents;

        /// <summary>x whitewater 0..1 from the Froude number, y water depth in
        /// metres. UC_Water reads x; y is written for anything else that wants a
        /// geometric depth without a depth texture.</summary>
        public Vector2[] uv;

        /// <summary>rg flow direction packed around 0.5, b flow speed normalised by
        /// <see cref="maxFlowSpeed"/>, a water weight for the shoreline fade.</summary>
        public Color[] colors;

        public int[] triangles;

        /// <summary>Surface extent in world space. The volume extends this downward
        /// by the depth itself, bounded by ShorelineConformer.MaxSkirtDepthMetres.</summary>
        public Bounds worldBounds;

        /// <summary>Mean surface elevation in world metres - what the volume and the
        /// physics treat as the water level.</summary>
        public float surfaceLevel;

        /// <summary>Deepest point, in metres, after the conformer's depth cap.</summary>
        public float maxDepth;

        /// <summary>Mean current in metres per second, for the volume. A single
        /// vector for a whole body is a simplification the mesh does not make - the
        /// vertex stream carries the real per-point flow - but a swimmer needs one
        /// number and a reach of river has one useful answer.</summary>
        public Vector2 meanFlow;

        /// <summary>What colour.b is normalised by, so the material's Mesh Flow
        /// Scale can be set to it and the shader's scroll speed comes out in real
        /// metres per second.</summary>
        public float maxFlowSpeed = 1f;

        /// <summary>Material description, or null to use the shared material.</summary>
        public WaterProduct product;

        /// <summary>Whether the body attaches a UCWaterVolume. False is visuals
        /// only - a decorative pond nothing ever swims in does not need to be
        /// queried by every buoyant object in the scene.</summary>
        public bool generateVolume = true;

        public int VertexCount { get { return vertices != null ? vertices.Length : 0; } }
        public int TriangleCount { get { return triangles != null ? triangles.Length / 3 : 0; } }
    }
}
