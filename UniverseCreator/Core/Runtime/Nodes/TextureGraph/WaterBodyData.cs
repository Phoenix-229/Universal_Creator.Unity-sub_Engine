// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    WaterBodyData.cs
//  Purpose: The description of a water surface as geometry, and the off-thread
//           mesh generator that produces it from the hydrology maths.
//
//  WHY THE MESH AND NOT A FLAT PLANE PER CHUNK
//  -------------------------------------------
//  A chunk can contain two lakes at different elevations, a river crossing a
//  corner, and a stretch of sea. A single plane per chunk cannot hold three
//  surface heights, and a UCWaterVolume carries one surfaceLevel - so a chunk-wide
//  plane would put a mountain tarn and the sea at the same altitude and drown the
//  valley between them.
//
//  So the wet cells are labelled into connected bodies and each body becomes its
//  own mesh with its own volume. That is more code than one plane, and it is the
//  difference between water that follows the terrain and water that is a blue
//  sheet at one height.
//
//  WHERE THE FLOW COMES FROM
//  -------------------------
//  Manning's equation for open-channel flow:
//
//      v = (1/n) * R^(2/3) * sqrt(S)
//
//  n is the channel roughness (0.035 for a natural stream), R the hydraulic radius
//  (the water depth, for a channel much wider than it is deep) and S the slope of
//  the water surface. It needs nothing this builder does not already have, and it
//  is why the answer comes out in real metres per second rather than as a tuned
//  number: a deep slow reach and a shallow steep rapid fall out of the same
//  formula instead of having to be authored separately.
//
//  A lake's surface is level, so S is zero, so v is zero. The same line of code
//  that makes a river run makes a lake sit still, which is the test that the
//  formula is doing the work rather than decorating a hand-tuned result.
//
//  Froude number Fr = v / sqrt(g*d) then says whether the flow is subcritical
//  (smooth) or supercritical (broken, white). Above about Fr 1 a channel forms a
//  hydraulic jump - standing white water - and that is written into the mesh so
//  the shader whitens the rapids and not the slow reaches.
//
//  MOBILE BUDGET
//  -------------
//  The mesh is built on a lattice coarser than the heightmap, because a water
//  surface is nearly flat and its detail comes from the shader's normal maps and
//  the vertex wave, not from its triangles. At the default 64 the whole chunk's
//  water is at most 65x65 vertices, which keeps the index buffer 16-bit - and a
//  16-bit index buffer is the difference between one draw call and two on a lot of
//  mobile hardware.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.TerrainSystem;

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
        Pool = 3
    }

    /// <summary>
    /// One water surface, ready for the main thread to turn into a Mesh.
    ///
    /// Vertices are relative to <see cref="origin"/> rather than absolute. A world
    /// ten kilometres across puts absolute vertex coordinates far enough from zero
    /// that a float mantissa starts losing centimetres, which shows up as a water
    /// surface that shimmers when the camera moves and nothing else does.
    /// </summary>
    public class WaterBodyData
    {
        public string name = "Water";
        public WaterBodyKind kind = WaterBodyKind.Lake;

        /// <summary>Shape mode for generation (Grid Mesh or Polygon outline).</summary>
        public UCWaterBody.ShapeMode shape = UCWaterBody.ShapeMode.Rectangle;

        /// <summary>The extracted boundary points if shape is Polygon.</summary>
        public Polyline outline;

        /// <summary>World position the vertices are relative to.</summary>
        public Vector3 origin;

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
        /// by the depth itself.</summary>
        public Bounds worldBounds;

        /// <summary>Mean surface elevation in world metres - what the volume and the
        /// physics treat as the water level.</summary>
        public float surfaceLevel;

        /// <summary>Deepest point, in metres.</summary>
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

        /// <summary>Whether the spawner attaches a UCWaterVolume. False is visuals
        /// only - a decorative pond nothing ever swims in does not need to be
        /// queried by every buoyant object in the scene.</summary>
        public bool generateVolume = true;

        /// <summary>How far past the generated world a flat far-field plane is
        /// carried, in metres. 0 for none. Only the ocean sets this: it is the one
        /// body that is visible beyond the last chunk, and without it the sea ends
        /// in mid-air at the edge of the loaded world.</summary>
        public float horizonExtentMetres;

        /// <summary>Layer for the spawned object, or -1 to take the world's water
        /// layer. -1 rather than 0 because 0 is Default, which is a layer a user may
        /// genuinely want - a sentinel that doubles as a real value is how an
        /// override ends up impossible to switch off.</summary>
        public int layerOverride = -1;

        public int VertexCount { get { return vertices != null ? vertices.Length : 0; } }
        public int TriangleCount { get { return triangles != null ? triangles.Length / 3 : 0; } }
    }

    /// <summary>
    /// Settings the water output node hands to the builder.
    /// </summary>
    public struct WaterBuildSettings
    {
        public WaterBodyKind kind;
        public string name;

        /// <summary>Vertices along the longest edge of the chunk's water lattice.</summary>
        public int meshResolution;

        /// <summary>Water thinner than this is not water. Kills the one-pixel
        /// puddles a noise-driven level field produces along every contour.</summary>
        public float minDepthMetres;

        /// <summary>Bodies covering less than this many square metres are dropped.</summary>
        public float minAreaSquareMetres;

        /// <summary>Depth over which the vertex weight ramps from 0 to 1, so the
        /// mesh boundary is never visible as a line.</summary>
        public float edgeFeatherMetres;

        /// <summary>Manning roughness. 0.011 smooth concrete, 0.035 natural stream,
        /// 0.07 a boulder field.</summary>
        public float manningRoughness;

        /// <summary>Hydraulic radius is capped here. Manning is a channel formula;
        /// feeding it a 200m lake depth returns a velocity no lake has.</summary>
        public float hydraulicRadiusCap;

        /// <summary>Speed that maps to a full flow channel, in m/s.</summary>
        public float maxFlowSpeed;

        /// <summary>Raises or lowers the whole surface, in metres.</summary>
        public float surfaceOffsetMetres;

        public static WaterBuildSettings Default(WaterBodyKind kind)
        {
            WaterBuildSettings s = new WaterBuildSettings();
            s.kind = kind;
            s.name = kind.ToString();
            s.meshResolution = 64;
            s.minDepthMetres = 0.05f;
            s.minAreaSquareMetres = 4f;
            s.edgeFeatherMetres = 0.6f;
            s.manningRoughness = 0.035f;
            s.hydraulicRadiusCap = 3f;
            s.maxFlowSpeed = 4f;
            s.surfaceOffsetMetres = 0f;
            return s;
        }
    }

    /// <summary>
    /// Turns a bed heightfield and a water-level field into water body meshes.
    /// Pure managed arrays and stateless maths, so it runs on the graph's worker
    /// thread alongside everything else.
    /// </summary>
    public static class WaterMeshBuilder
    {
        private const float Gravity = 9.81f;

        // Scratch, reused across bodies within one call. Not static: two chunks
        // generate concurrently on different workers.
        private class Scratch
        {
            public int gx, gz;
            public float texelX, texelZ;

            public float[] bed;        // metres
            public float[] surf;       // metres
            public float[] depth;      // metres, negative where dry
            public bool[] wet;
            public int[] label;        // -1 unlabelled
            public int[] stack;

            public Vector2[] flowDir;
            public float[] flowSpeed;  // m/s
            public float[] whitewater; // 0..1

            public int[] vertexIndex;  // per grid point, -1 when not in this body
        }

        /// <summary>
        /// Builds every connected water body found in the tile and appends them to
        /// results. bed and level are normalised 0..1 in the tile's height space;
        /// level may be null, in which case constantLevel (also normalised) is used
        /// for the whole tile - which is how a flat sea or a fixed pond works.
        /// </summary>
        public static void Build(Matrix bed, Matrix level, float constantLevel,
                                 Matrix mask, WaterBuildSettings settings,
                                 List<WaterBodyData> results, StopToken stop)
        {
            if (bed == null || results == null) return;

            int resX = bed.rect.size.x;
            int resZ = bed.rect.size.z;
            if (resX < 2 || resZ < 2) return;

            int target = Mathf.Clamp(settings.meshResolution, 8, 240);

            // Stride, not resampling. Taking every Nth lattice point keeps the water
            // vertices exactly on heightmap samples, so the shoreline the mesh
            // computes is the shoreline the terrain has - a resampled bed would put
            // the waterline a fraction of a texel off and leave a visible rim of dry
            // ground poking through.
            int stride = Mathf.Max(1, Mathf.CeilToInt((Mathf.Max(resX, resZ) - 1) / (float)target));

            Scratch s = new Scratch();
            s.gx = (resX - 1) / stride + 1;
            s.gz = (resZ - 1) / stride + 1;

            int n = s.gx * s.gz;
            if (n < 4) return;

            Vector2 pixel = bed.PixelSize;
            s.texelX = pixel.x * stride;
            s.texelZ = pixel.y * stride;

            s.bed = new float[n];
            s.surf = new float[n];
            s.depth = new float[n];
            s.wet = new bool[n];
            s.label = new int[n];
            s.stack = new int[n];
            s.flowDir = new Vector2[n];
            s.flowSpeed = new float[n];
            s.whitewater = new float[n];
            s.vertexIndex = new int[n];

            SampleFields(bed, level, constantLevel, mask, settings, s, stride, stop);
            if (Cancelled(stop)) return;

            ExtendSurfaceToBanks(s);
            ComputeFlow(s, settings);

            if (Cancelled(stop)) return;

            int bodyCount = LabelBodies(s);
            if (bodyCount == 0) return;

            float cellArea = s.texelX * s.texelZ;
            float minCells = settings.minAreaSquareMetres / Mathf.Max(0.0001f, cellArea);

            for (int id = 0; id < bodyCount; id++)
            {
                if (Cancelled(stop)) return;

                WaterBodyData body = BuildBody(bed, s, settings, id, minCells, stride);
                if (body != null) results.Add(body);
            }
        }

        // -----------------------------------------------------------------
        //  Fields
        // -----------------------------------------------------------------

        private static void SampleFields(Matrix bed, Matrix level, float constantLevel,
                                         Matrix mask, WaterBuildSettings settings,
                                         Scratch s, int stride, StopToken stop)
        {
            float baseY = bed.worldPos.y;
            float height = bed.worldHeight;

            int ox = bed.rect.offset.x;
            int oz = bed.rect.offset.z;
            int maxX = ox + bed.rect.size.x - 1;
            int maxZ = oz + bed.rect.size.z - 1;

            for (int gz = 0; gz < s.gz; gz++)
            {
                if (Cancelled(stop)) return;

                int lz = Mathf.Min(oz + gz * stride, maxZ);
                int row = gz * s.gx;

                for (int gx = 0; gx < s.gx; gx++)
                {
                    int lx = Mathf.Min(ox + gx * stride, maxX);
                    int i = row + gx;

                    float bedNorm = bed.GetSafe(lx, lz);
                    float levelNorm = level != null ? level.GetSafe(lx, lz) : constantLevel;

                    float bedM = baseY + bedNorm * height;
                    float surfM = baseY + levelNorm * height + settings.surfaceOffsetMetres;

                    s.bed[i] = bedM;
                    s.surf[i] = surfM;
                    s.depth[i] = surfM - bedM;

                    // The mask is a veto, not a source. A level field alone would put
                    // water in every hollow the heightfield has; the mask is how the
                    // graph says which of those hollows the user actually meant.
                    bool allowed = mask == null || mask.GetSafe(lx, lz) > 0.01f;

                    s.wet[i] = allowed && s.depth[i] >= settings.minDepthMetres;
                    s.label[i] = -1;
                }
            }
        }

        /// <summary>
        /// Gives dry grid points next to wet ones a surface height.
        ///
        /// The mesh deliberately overshoots the waterline by one cell so the alpha
        /// fade has somewhere to happen. Those overshoot vertices are dry, and a
        /// solved level field holds nothing meaningful outside the water it solved
        /// for - a lake fill leaves dry cells at their bed height, which would drag
        /// the mesh edge down the bank as a visible skirt. Copying the neighbouring
        /// wet level keeps the surface planar right through the boundary.
        /// </summary>
        private static void ExtendSurfaceToBanks(Scratch s)
        {
            float[] extended = new float[s.surf.Length];
            System.Array.Copy(s.surf, extended, s.surf.Length);

            for (int gz = 0; gz < s.gz; gz++)
            {
                int row = gz * s.gx;

                for (int gx = 0; gx < s.gx; gx++)
                {
                    int i = row + gx;
                    if (s.wet[i]) continue;

                    float sum = 0f;
                    int count = 0;

                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nz = gz + dz;
                        if (nz < 0 || nz >= s.gz) continue;

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = gx + dx;
                            if (nx < 0 || nx >= s.gx) continue;

                            int j = nz * s.gx + nx;
                            if (!s.wet[j]) continue;

                            sum += s.surf[j];
                            count++;
                        }
                    }

                    if (count > 0) extended[i] = sum / count;
                }
            }

            s.surf = extended;

            for (int i = 0; i < s.surf.Length; i++)
                s.depth[i] = s.surf[i] - s.bed[i];
        }

        /// <summary>
        /// Manning velocity and the Froude number, per grid point.
        /// </summary>
        private static void ComputeFlow(Scratch s, WaterBuildSettings settings)
        {
            float invN = 1f / Mathf.Max(0.005f, settings.manningRoughness);
            float cap = Mathf.Max(0.05f, settings.hydraulicRadiusCap);

            for (int gz = 0; gz < s.gz; gz++)
            {
                int row = gz * s.gx;

                for (int gx = 0; gx < s.gx; gx++)
                {
                    int i = row + gx;

                    float depth = s.depth[i];
                    if (depth <= 0f)
                    {
                        s.flowDir[i] = Vector2.zero;
                        s.flowSpeed[i] = 0f;
                        s.whitewater[i] = 0f;
                        continue;
                    }

                    // Central difference of the water surface, in metres per metre.
                    // The surface slope and not the bed slope: water in a pool behind
                    // a steep step has a level surface and does not flow, however
                    // steep the ground under it is.
                    float sx = (Sample(s.surf, s, gx + 1, gz) - Sample(s.surf, s, gx - 1, gz))
                               / (2f * s.texelX);
                    float sz = (Sample(s.surf, s, gx, gz + 1) - Sample(s.surf, s, gx, gz - 1))
                               / (2f * s.texelZ);

                    float slope = Mathf.Sqrt(sx * sx + sz * sz);

                    // Downhill. The gradient points uphill, so the flow is its
                    // negative.
                    if (slope > 1e-6f) s.flowDir[i] = new Vector2(-sx / slope, -sz / slope);
                    else s.flowDir[i] = Vector2.zero;

                    float radius = Mathf.Min(depth, cap);
                    float v = invN * Mathf.Pow(radius, 0.6666667f) * Mathf.Sqrt(slope);

                    s.flowSpeed[i] = v;

                    // Froude. Above 1 the flow cannot carry a wave upstream, so it
                    // breaks into a standing hydraulic jump - which is what white
                    // water is. The ramp starts a little below 1 because a real
                    // channel starts breaking on its roughness before the average
                    // crosses the threshold.
                    float froude = v / Mathf.Sqrt(Gravity * Mathf.Max(0.02f, depth));
                    s.whitewater[i] = Mathf.Clamp01((froude - 0.8f) / 0.6f);
                }
            }
        }

        private static float Sample(float[] field, Scratch s, int gx, int gz)
        {
            if (gx < 0) gx = 0; else if (gx >= s.gx) gx = s.gx - 1;
            if (gz < 0) gz = 0; else if (gz >= s.gz) gz = s.gz - 1;
            return field[gz * s.gx + gx];
        }

        // -----------------------------------------------------------------
        //  Connected bodies
        // -----------------------------------------------------------------

        /// <summary>
        /// Four-neighbour flood fill with an explicit stack. Explicit and not
        /// recursive because a lake filling a whole chunk is a quarter of a million
        /// cells deep and that is a stack overflow, on a worker thread, with no
        /// message that says which node did it.
        /// </summary>
        private static int LabelBodies(Scratch s)
        {
            int next = 0;

            for (int start = 0; start < s.wet.Length; start++)
            {
                if (!s.wet[start] || s.label[start] >= 0) continue;

                int id = next++;
                int top = 0;
                s.stack[top++] = start;
                s.label[start] = id;

                while (top > 0)
                {
                    int i = s.stack[--top];
                    int gx = i % s.gx;
                    int gz = i / s.gx;

                    Push(s, gx - 1, gz, id, ref top);
                    Push(s, gx + 1, gz, id, ref top);
                    Push(s, gx, gz - 1, id, ref top);
                    Push(s, gx, gz + 1, id, ref top);
                }
            }

            return next;
        }

        private static void Push(Scratch s, int gx, int gz, int id, ref int top)
        {
            if (gx < 0 || gx >= s.gx || gz < 0 || gz >= s.gz) return;

            int i = gz * s.gx + gx;
            if (!s.wet[i] || s.label[i] >= 0) return;

            s.label[i] = id;
            s.stack[top++] = i;
        }

        // -----------------------------------------------------------------
        //  One body's mesh
        // -----------------------------------------------------------------

        private static WaterBodyData BuildBody(Matrix bed, Scratch s, WaterBuildSettings settings,
                                               int id, float minCells, int stride)
        {
            // Pass 1: which quads belong to this body, and how many cells it has.
            // A quad is included when any of its four corners is wet with this
            // label, which is what makes the mesh reach one cell past the waterline.
            int wetCells = 0;
            for (int i = 0; i < s.label.Length; i++)
                if (s.label[i] == id) wetCells++;

            if (wetCells < minCells) return null;

            for (int i = 0; i < s.vertexIndex.Length; i++) s.vertexIndex[i] = -1;

            int quadCount = 0;

            for (int gz = 0; gz < s.gz - 1; gz++)
            {
                for (int gx = 0; gx < s.gx - 1; gx++)
                {
                    int i0 = gz * s.gx + gx;
                    int i1 = i0 + 1;
                    int i2 = i0 + s.gx;
                    int i3 = i2 + 1;

                    if (s.label[i0] != id && s.label[i1] != id &&
                        s.label[i2] != id && s.label[i3] != id) continue;

                    s.vertexIndex[i0] = 0;
                    s.vertexIndex[i1] = 0;
                    s.vertexIndex[i2] = 0;
                    s.vertexIndex[i3] = 0;
                    quadCount++;
                }
            }

            if (quadCount == 0) return null;

            // Pass 2: number the used vertices.
            int vertexCount = 0;
            for (int i = 0; i < s.vertexIndex.Length; i++)
                if (s.vertexIndex[i] == 0) s.vertexIndex[i] = vertexCount++;

            if (vertexCount < 4) return null;

            WaterBodyData body = new WaterBodyData();
            body.kind = settings.kind;
            body.name = settings.name;
            body.maxFlowSpeed = Mathf.Max(0.01f, settings.maxFlowSpeed);

            body.vertices = new Vector3[vertexCount];
            body.normals = new Vector3[vertexCount];
            body.tangents = new Vector4[vertexCount];
            body.uv = new Vector2[vertexCount];
            body.colors = new Color[vertexCount];
            body.triangles = new int[quadCount * 6];

            // Origin at the body's own centre in XZ and at its surface in Y, so the
            // local coordinates a mesh renderer sees stay small no matter where in
            // the world the body is.
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float surfSum = 0f;
            float deepest = 0f;
            Vector2 flowSum = Vector2.zero;
            int flowSamples = 0;

            float wx0 = bed.worldPos.x;
            float wz0 = bed.worldPos.z;

            for (int gz = 0; gz < s.gz; gz++)
            {
                for (int gx = 0; gx < s.gx; gx++)
                {
                    int i = gz * s.gx + gx;
                    if (s.vertexIndex[i] < 0) continue;

                    float x = wx0 + gx * s.texelX;
                    float z = wz0 + gz * s.texelZ;
                    float y = s.surf[i];

                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;

                    if (s.label[i] == id)
                    {
                        surfSum += y;
                        if (s.depth[i] > deepest) deepest = s.depth[i];
                        flowSum += s.flowDir[i] * s.flowSpeed[i];
                        flowSamples++;
                    }
                }
            }

            body.surfaceLevel = flowSamples > 0 ? surfSum / flowSamples : minY;
            body.maxDepth = Mathf.Max(0.1f, deepest);
            body.meanFlow = flowSamples > 0 ? flowSum / flowSamples : Vector2.zero;

            body.origin = new Vector3((minX + maxX) * 0.5f, body.surfaceLevel, (minZ + maxZ) * 0.5f);

            body.worldBounds = new Bounds(
                new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f),
                new Vector3(Mathf.Max(0.01f, maxX - minX),
                            Mathf.Max(0.01f, maxY - minY),
                            Mathf.Max(0.01f, maxZ - minZ)));

            float feather = Mathf.Max(0.01f, settings.edgeFeatherMetres);
            float invMaxSpeed = 1f / body.maxFlowSpeed;

            // Pass 3: write the vertices.
            for (int gz = 0; gz < s.gz; gz++)
            {
                for (int gx = 0; gx < s.gx; gx++)
                {
                    int i = gz * s.gx + gx;
                    int v = s.vertexIndex[i];
                    if (v < 0) continue;

                    float x = wx0 + gx * s.texelX;
                    float z = wz0 + gz * s.texelZ;

                    body.vertices[v] = new Vector3(x - body.origin.x,
                                                   s.surf[i] - body.origin.y,
                                                   z - body.origin.z);

                    // Normal from the surface gradient. A lake comes out straight up;
                    // a river reach comes out tilted downstream, which is what makes
                    // the sun's reflection sit correctly on a sloping run instead of
                    // as if the water were level.
                    float sx = (Sample(s.surf, s, gx + 1, gz) - Sample(s.surf, s, gx - 1, gz))
                               / (2f * s.texelX);
                    float sz = (Sample(s.surf, s, gx, gz + 1) - Sample(s.surf, s, gx, gz - 1))
                               / (2f * s.texelZ);

                    Vector3 normal = new Vector3(-sx, 1f, -sz).normalized;
                    body.normals[v] = normal;

                    // Tangent along world +X, made orthogonal to the normal. World
                    // aligned and not flow aligned: a flow-aligned tangent would
                    // rotate the wave normal map through every meander, and the
                    // rotation is visible as the texture spinning under itself where
                    // the channel turns.
                    Vector3 t = Vector3.right - normal * Vector3.Dot(normal, Vector3.right);
                    if (t.sqrMagnitude < 1e-6f) t = Vector3.forward;
                    t.Normalize();

                    // w = -1 is Unity's handedness for an imported mesh, and the
                    // shader's bitangent reconstruction assumes it.
                    body.tangents[v] = new Vector4(t.x, t.y, t.z, -1f);

                    float depth = s.depth[i];

                    body.uv[v] = new Vector2(s.whitewater[i], Mathf.Max(0f, depth));

                    Vector2 dir = s.flowDir[i];
                    float speed = Mathf.Clamp01(s.flowSpeed[i] * invMaxSpeed);

                    // Weight 0 at the waterline rising to 1 over the feather depth.
                    // The mesh edge therefore fades out rather than ending, and the
                    // polygon boundary is never visible as a line on the water.
                    float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(depth / feather));

                    body.colors[v] = new Color(dir.x * 0.5f + 0.5f,
                                               dir.y * 0.5f + 0.5f,
                                               speed,
                                               weight);
                }
            }

            // Pass 4: triangles. Same winding as CustomMeshOutputNode, which is the
            // known-good upward-facing order for an XZ lattice in this codebase.
            int idx = 0;
            for (int gz = 0; gz < s.gz - 1; gz++)
            {
                for (int gx = 0; gx < s.gx - 1; gx++)
                {
                    int i0 = gz * s.gx + gx;
                    int i1 = i0 + 1;
                    int i2 = i0 + s.gx;
                    int i3 = i2 + 1;

                    if (s.label[i0] != id && s.label[i1] != id &&
                        s.label[i2] != id && s.label[i3] != id) continue;

                    int v0 = s.vertexIndex[i0];
                    int v1 = s.vertexIndex[i1];
                    int v2 = s.vertexIndex[i2];
                    int v3 = s.vertexIndex[i3];

                    body.triangles[idx++] = v0;
                    body.triangles[idx++] = v2;
                    body.triangles[idx++] = v1;

                    body.triangles[idx++] = v1;
                    body.triangles[idx++] = v2;
                    body.triangles[idx++] = v3;
                }
            }

            // Pass 5: Boundary Smoothing
            // Smooths the outer boundary vertices (where label != id) to round off the 
            // jagged 90-degree stair-stepping into a curved, organic shape.
            if (body.vertices.Length > 0)
            {
                Vector3[] smoothed = new Vector3[body.vertices.Length];
                System.Array.Copy(body.vertices, smoothed, body.vertices.Length);

                for (int iter = 0; iter < 4; iter++)
                {
                    for (int gz = 1; gz < s.gz - 1; gz++)
                    {
                        for (int gx = 1; gx < s.gx - 1; gx++)
                        {
                            int i = gz * s.gx + gx;
                            int v = s.vertexIndex[i];
                            
                            // Only smooth the overshoot boundary vertices that form the jagged edge
                            if (v < 0 || s.label[i] == id) continue;

                            int v_up = s.vertexIndex[i + s.gx];
                            int v_dn = s.vertexIndex[i - s.gx];
                            int v_lt = s.vertexIndex[i - 1];
                            int v_rt = s.vertexIndex[i + 1];

                            Vector3 sum = Vector3.zero;
                            int count = 0;
                            
                            // Pull towards adjacent boundary vertices to curve the edge
                            if (v_up >= 0 && s.label[i + s.gx] != id) { sum += smoothed[v_up]; count++; }
                            if (v_dn >= 0 && s.label[i - s.gx] != id) { sum += smoothed[v_dn]; count++; }
                            if (v_lt >= 0 && s.label[i - 1] != id) { sum += smoothed[v_lt]; count++; }
                            if (v_rt >= 0 && s.label[i + 1] != id) { sum += smoothed[v_rt]; count++; }

                            if (count > 0)
                            {
                                body.vertices[v] = Vector3.Lerp(body.vertices[v], sum / count, 0.65f);
                            }
                        }
                    }
                    System.Array.Copy(body.vertices, smoothed, body.vertices.Length);
                }
            }

            return body;
        }

        private static bool Cancelled(StopToken stop)
        {
            return stop != null && stop.StopRequested;
        }
    }
}
