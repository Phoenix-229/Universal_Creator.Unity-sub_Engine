// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    WaterBodyApplier.cs
//  Purpose: Turns the WaterBodyData a chunk produced off-thread into real scene
//           objects - mesh, renderer, water volume - and takes them away again
//           when the chunk unloads.
//
//  Mirrors ScatterApplier exactly: Apply(owner, bodies, parent) creates, Release
//  (owner) destroys, and the owner key is the chunk. That symmetry is not
//  decoration - it is what makes a recentre safe. The chunk system calls Release
//  then Apply on the same key, and anything that does not follow that contract
//  leaks a GameObject per chunk per recentre, which on an endless world is a leak
//  with no ceiling.
//
//  WHY THE MESH IS BUILT HERE AND NOT IN THE NODE
//  ----------------------------------------------
//  UnityEngine.Mesh cannot be constructed on a worker thread. The node produces
//  arrays; this turns them into a Mesh on the main thread, in the same batch as
//  every other output write, so a chunk appears all at once rather than as terrain
//  followed a frame later by its water.
//
//  MATERIAL SHARING
//  ----------------
//  Bodies that describe the same water share one material instance, keyed by the
//  WaterProduct reference. A world with an ocean, four lakes and a river gets
//  three materials, not six - and, more usefully, the four lakes stay batchable.
//  A body with no product at all uses the shared default, which is the common case
//  and allocates nothing.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public static class WaterBodyApplier
    {
        // Keyed by owner, exactly as ScatterApplier is. The value is the list of
        // objects that owner is responsible for.
        private static readonly Dictionary<int, List<GameObject>> spawned =
            new Dictionary<int, List<GameObject>>();

        // Materials keyed by the product that described them. A ConditionalWeakTable
        // would be tidier, but the products are replaced wholesale on every graph
        // evaluation and this is cleared on the same schedule, so a plain dictionary
        // with an explicit clear is both simpler and easier to reason about.
        private static readonly Dictionary<WaterProduct, Material> materials =
            new Dictionary<WaterProduct, Material>();

        private static Material sharedDefault;

        // Global registry of shared water body parents across chunks.
        // Key: "name|roundedSurfaceLevel" so all chunk slices of the same lake
        // share one parent instead of each spawning a separate Lake object.
        private static readonly Dictionary<string, GameObject> bodyParents =
            new Dictionary<string, GameObject>();

        /// <summary>
        /// Call this at the start of each full world rebuild so stale parents
        /// from a previous generate do not accumulate.
        /// </summary>
        public static void ClearBodyParents()
        {
            foreach (var kv in bodyParents)
            {
                if (kv.Value != null)
                {
                    if (Application.isPlaying) Object.Destroy(kv.Value);
                    else Object.DestroyImmediate(kv.Value);
                }
            }
            bodyParents.Clear();
        }

        private static Transform GetOrCreateBodyParent(WaterBodyData body, Transform parent, int layer)
        {
            string key = body.kind.ToString();

            GameObject existing;
            if (bodyParents.TryGetValue(key, out existing) && existing != null)
                return existing.transform;

            GameObject newParent = new GameObject(body.name);
            newParent.layer = body.layerOverride >= 0 ? body.layerOverride : layer;
            newParent.hideFlags = HideFlags.DontSave;
            if (parent != null) newParent.transform.SetParent(parent, false);
            newParent.transform.position = new Vector3(body.worldBounds.center.x,
                                                       body.surfaceLevel,
                                                       body.worldBounds.center.z);

            // Attach the water volume once on the shared parent (not on every chunk mesh)
            if (body.generateVolume)
            {
                UCWaterVolume volume = newParent.AddComponent<UCWaterVolume>();
                volume.kind = (UCWaterVolume.BodyKind)(int)body.kind;
                volume.surfaceLevel = body.surfaceLevel;
                volume.maxDepth = Mathf.Max(0.1f, body.maxDepth);
                volume.worldBounds = body.worldBounds;

                Bounds b = body.worldBounds;
                b.Encapsulate(new Vector3(b.center.x, b.min.y - volume.maxDepth, b.center.z));
                volume.worldBounds = b;
            }

            bodyParents[key] = newParent;
            return newParent.transform;
        }

        /// <summary>
        /// Set by the editor applier so runtime spawning uses the same material the
        /// texture graph generated. Left null in a build with no texture graph, in
        /// which case a default material is created from the shader.
        /// </summary>
        public static Material DefaultMaterial
        {
            get { return sharedDefault; }
            set { sharedDefault = value; }
        }

        // ---------------------------------------------------------------------
        //  Apply
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds scene objects for every body and files them under owner. Any
        /// objects the owner already had are destroyed first, so calling this twice
        /// replaces rather than accumulates.
        /// </summary>
        public static void Apply(int owner, List<WaterBodyData> bodies, Transform parent,
                                 int layer = 0)
        {
            Release(owner);

            if (bodies == null || bodies.Count == 0) return;

            List<GameObject> list = new List<GameObject>(bodies.Count);

            for (int i = 0; i < bodies.Count; i++)
            {
                GameObject go = Build(bodies[i], parent, layer);
                if (go != null) list.Add(go);

                // The horizon is world-scale, not chunk-scale, so it is handled
                // outside the per-owner list - see EnsureHorizon.
                if (bodies[i] != null && bodies[i].horizonExtentMetres > 0.01f)
                    EnsureHorizon(bodies[i], parent, layer);
            }

            if (list.Count > 0) spawned[owner] = list;
        }

        private static GameObject Build(WaterBodyData body, Transform parent, int layer)
        {
            if (body == null || body.vertices == null || body.vertices.Length < 3) return null;
            if (body.triangles == null || body.triangles.Length < 3) return null;

            // Get or create the one shared parent for this water body across all chunks.
            // All 25 chunk slices of the same lake go under ONE "Lake" parent in WaterSystem.
            Transform sharedParent = GetOrCreateBodyParent(body, parent, layer);

            // Each chunk fragment spawns its own mesh child, all parented to the shared body root.
            GameObject go = new GameObject("UC Water Mesh");
            go.layer = body.layerOverride >= 0 ? body.layerOverride : layer;
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(sharedParent, false);
            go.transform.position = body.origin;

            Mesh mesh = new Mesh();
            mesh.name = "UC Water Mesh";

            // 16-bit while it fits. A 32-bit index buffer costs twice the bandwidth
            // and is not supported for instancing on some mobile GPUs, and water
            // meshes are built coarse specifically so they fit.
            mesh.indexFormat = body.vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices = body.vertices;
            if (body.normals != null) mesh.normals = body.normals;
            if (body.tangents != null) mesh.tangents = body.tangents;
            if (body.uv != null) mesh.uv = body.uv;
            if (body.colors != null) mesh.colors = body.colors;
            mesh.triangles = body.triangles;

            // Bounds by hand rather than RecalculateBounds, because the shader
            // displaces vertices upward by the wave height and a mesh culled on its
            // undisplaced bounds pops out of view at the screen edge on a swell.
            Bounds local = new Bounds(Vector3.zero, Vector3.zero);
            for (int i = 0; i < body.vertices.Length; i++)
            {
                if (i == 0) local = new Bounds(body.vertices[i], Vector3.zero);
                else local.Encapsulate(body.vertices[i]);
            }
            local.Expand(new Vector3(0f, 4f, 0f));
            mesh.bounds = local;

            mesh.hideFlags = HideFlags.DontSave;

            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ResolveMaterial(body);

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;

            // Return the mesh child so it gets tracked by the chunk owner for cleanup.
            return go;
        }

        private static void AttachVolume(GameObject go, WaterBodyData body)
        {
            if (!body.generateVolume) return;

            UCWaterVolume volume = go.AddComponent<UCWaterVolume>();

            volume.kind = (UCWaterVolume.BodyKind)(int)body.kind;
            volume.surfaceLevel = body.surfaceLevel;
            volume.maxDepth = Mathf.Max(0.1f, body.maxDepth);
            volume.worldBounds = body.worldBounds;

            // Extended downward, the same way FitToRenderer does, because the
            // renderer's bounds describe a surface and a query a metre below it must
            // still land inside the volume.
            Bounds b = body.worldBounds;
            b.Encapsulate(new Vector3(b.center.x, b.min.y - volume.maxDepth, b.center.z));
            volume.worldBounds = b;

            volume.flow = body.meanFlow;

            WaterProduct p = body.product;
            if (p != null)
            {
                // One description drives both the look and the physics. Reading the
                // wave from anywhere else is how a boat ends up floating a hand's
                // width above the visible surface.
                volume.surface = WaterSurfaceParams.Default(body.surfaceLevel);
                volume.surface.waveHeight = p.waveHeightMetres;
                volume.surface.waveLength = p.waveLengthMetres;
                volume.surface.waveSpeed = p.waveSpeed;
                volume.surface.flow = body.meanFlow != Vector2.zero
                    ? body.meanFlow
                    : p.flowMetresPerSecond;
            }
            else
            {
                volume.surface = WaterSurfaceParams.Default(body.surfaceLevel);
                volume.surface.flow = body.meanFlow;
            }

            // Salt water is about 2.5% denser than fresh, and that is enough to
            // change what floats: it is why a boat sits measurably higher at sea
            // than in a river, and why swimming in the sea is easier.
            volume.density = body.kind == WaterBodyKind.Ocean ? 1025f : 998f;

            // Graph-built water dives the same as hand-placed water. There is no
            // UCWaterBody here to copy fields from - the graph node describes the body
            // - so the look comes from the kind, using the same numbers SetKindOnce
            // seeds a component with. Both paths agreeing is the point: a lake is a
            // lake whether the user dropped it or a node produced it.
            volume.underwaterView = true;
            UCWaterVolume.UnderwaterDefaultsFor(volume.kind,
                                                out volume.underwaterColor,
                                                out volume.visibilityMetres,
                                                out volume.underwaterBrightness);
        }

        private static Material ResolveMaterial(WaterBodyData body)
        {
            Material baseMaterial = EnsureDefault();
            if (baseMaterial == null) return null;

            if (body.product != null)
            {
                Material existing;
                if (materials.TryGetValue(body.product, out existing) && existing != null)
                    return existing;
            }

            Material m = new Material(baseMaterial);
            m.name = "UC Water - " + body.name;
            m.hideFlags = HideFlags.DontSave;

            if (body.product != null)
            {
                ApplyProductRuntime(m, body.product);
                materials[body.product] = m;
            }
            else
            {
                // Fallback default colors based on the water body kind
                switch (body.kind)
                {
                    case WaterBodyKind.Ocean:
                        m.SetColor(UCWaterProps.ShallowColor, new Color(0.1f, 0.4f, 0.5f, 0.8f));
                        m.SetColor(UCWaterProps.DeepColor, new Color(0.02f, 0.1f, 0.3f, 0.95f));
                        break;
                    case WaterBodyKind.River:
                        m.SetColor(UCWaterProps.ShallowColor, new Color(0.3f, 0.4f, 0.3f, 0.8f));
                        m.SetColor(UCWaterProps.DeepColor, new Color(0.1f, 0.2f, 0.1f, 0.95f));
                        break;
                    case WaterBodyKind.Pool:
                        m.SetColor(UCWaterProps.ShallowColor, new Color(0.2f, 0.5f, 0.4f, 0.7f));
                        m.SetColor(UCWaterProps.DeepColor, new Color(0.1f, 0.3f, 0.4f, 0.9f));
                        break;
                    case WaterBodyKind.Lake:
                    default:
                        m.SetColor(UCWaterProps.ShallowColor, new Color(0.15f, 0.45f, 0.55f, 0.8f));
                        m.SetColor(UCWaterProps.DeepColor, new Color(0.05f, 0.15f, 0.35f, 0.95f));
                        break;
                }
            }

            // Mesh flow is normalised 0..1 in COLOR.b, so the shader needs to know
            // what 1 means. Setting it from the body is what makes the surface
            // scroll at the metres per second Manning's equation computed rather
            // than at whatever the material was authored with.
            m.SetFloat(UCWaterProps.FlowStrength, body.maxFlowSpeed);
            m.SetFloat(UCWaterProps.UseVertexFlow, body.kind == WaterBodyKind.Ocean ? 0f : 1f);

            return m;
        }

        private static Material EnsureDefault()
        {
            if (sharedDefault != null) return sharedDefault;

            Shader shader = Shader.Find(UCWaterProps.ShaderName);
            if (shader == null)
            {
                Debug.LogWarning("[Universe Creator] Shader '" + UCWaterProps.ShaderName +
                                 "' was not found. Water bodies will render untextured. " +
                                 "Check that the shader is included in the build.");
                return null;
            }

            sharedDefault = new Material(shader);
            sharedDefault.name = "UC Water (default)";
            sharedDefault.hideFlags = HideFlags.DontSave;
            return sharedDefault;
        }

        /// <summary>
        /// Runtime half of UCWaterApplier.ApplyProduct. Duplicated deliberately and
        /// kept small: that one lives in an editor assembly and cannot be called
        /// from a build, and this one must not touch TextureRef.Apply, which does
        /// asset work. The two are validated against each other by UCSelfTest.
        /// </summary>
        public static void ApplyProductRuntime(Material m, WaterProduct w)
        {
            if (m == null || w == null) return;

            Set(m, UCWaterProps.ShallowColor, w.shallowColor);
            Set(m, UCWaterProps.DeepColor, w.deepColor);
            Set(m, UCWaterProps.DepthFadeMetres, w.depthFadeMetres);
            Set(m, UCWaterProps.Transparency, w.transparency);
            Set(m, UCWaterProps.Smoothness, w.smoothness);
            Set(m, UCWaterProps.FresnelPower, w.fresnelPower);
            Set(m, UCWaterProps.Refraction, w.refraction);
            Set(m, UCWaterProps.NormalScale, w.normalScale);
            Set(m, UCWaterProps.WaveTileMetres, w.waveTileMetres);

            m.SetVector(UCWaterProps.FlowSpeed,
                new Vector4(w.flowMetresPerSecond.x, w.flowMetresPerSecond.y, 0f, 0f));

            Set(m, UCWaterProps.FoamEnabled, w.foamEnabled ? 1f : 0f);
            Set(m, UCWaterProps.FoamColor, w.foamColor);
            Set(m, UCWaterProps.FoamIntensity, w.foamIntensity);
            Set(m, UCWaterProps.FoamEdgeWidth, w.foamEdgeWidthMetres);
            Set(m, UCWaterProps.FoamScrollSpeed, w.foamScrollSpeed);
            Set(m, UCWaterProps.FoamTileMetres, w.foamTileMetres);

            Set(m, UCWaterProps.CausticsEnabled, w.causticsEnabled ? 1f : 0f);
            Set(m, UCWaterProps.CausticsColor, w.causticsColor);
            Set(m, UCWaterProps.CausticsIntensity, w.causticsIntensity);
            Set(m, UCWaterProps.CausticsTileMetres, w.causticsTileMetres);
            Set(m, UCWaterProps.CausticsSpeed, w.causticsSpeed);
            Set(m, UCWaterProps.CausticsDepthFade, w.causticsDepthFadeMetres);

            Set(m, UCWaterProps.WaveHeight, w.waveHeightMetres);
            Set(m, UCWaterProps.WaveLength, w.waveLengthMetres);
            Set(m, UCWaterProps.WaveSpeed, w.waveSpeed);
            Set(m, UCWaterProps.EdgeSoftenMetres, w.edgeSoftenMetres);

            Set(m, UCWaterProps.RippleStrength, w.rippleStrength);
            Set(m, UCWaterProps.RippleFoam, w.rippleFoam);

            // Textures, but not TextureRef.Apply(). Assigning a texture to a material
            // is a plain reference write; Apply() writes filterMode and wrapMode onto
            // the texture asset itself, which is import-time work this side must never
            // do. Without these three lines a hand-placed body's wave normal map,
            // foam and caustics were silently dropped at runtime - the editor applier
            // set them, so it only went wrong in a build.
            SetTex(m, UCWaterProps.WaveNormalMap, w.normalMap);
            SetTex(m, UCWaterProps.FoamTexture, w.foamTexture);
            SetTex(m, UCWaterProps.CausticsTexture, w.causticsTexture);
        }

        private static void SetTex(Material m, string name, TextureRef reference)
        {
            if (!m.HasProperty(name)) return;
            if (reference == null || reference.texture == null) return;
            m.SetTexture(name, reference.texture);
        }

        // ---------------------------------------------------------------------
        //  Horizon
        // ---------------------------------------------------------------------

        // One shared far-field plane for the whole world, not one per chunk. Every
        // ocean chunk asks for a horizon, and honouring each would stack a hundred
        // overlapping 4km planes at the same elevation - which is both a hundred
        // draw calls and z-fighting across the entire sea.
        private static GameObject horizon;
        private static float horizonLevel = float.NaN;
        private static float horizonExtent;

        private static void EnsureHorizon(WaterBodyData body, Transform parent, int layer)
        {
            // Rebuilt only when the sea itself changed. A chunk load must not cost a
            // 4km mesh rebuild, and on an endless world chunks load constantly.
            if (horizon != null &&
                Mathf.Approximately(horizonLevel, body.surfaceLevel) &&
                Mathf.Approximately(horizonExtent, body.horizonExtentMetres))
                return;

            if (horizon != null) DestroyHorizon();

            horizonLevel = body.surfaceLevel;
            horizonExtent = body.horizonExtentMetres;

            horizon = new GameObject("UC Water - Horizon");
            horizon.layer = body.layerOverride >= 0 ? body.layerOverride : layer;
            horizon.hideFlags = HideFlags.DontSave;

            // Parented to the world root, not to the chunk. The chunk that happened
            // to trigger this will be unloaded long before the horizon should be.
            Transform root = parent != null ? parent.parent : null;
            if (root != null) horizon.transform.SetParent(root, false);

            horizon.transform.position = new Vector3(body.worldBounds.center.x,
                                                     body.surfaceLevel,
                                                     body.worldBounds.center.z);

            Mesh mesh = BuildHorizonMesh(body.horizonExtentMetres);
            mesh.hideFlags = HideFlags.DontSave;

            MeshFilter f = horizon.AddComponent<MeshFilter>();
            f.sharedMesh = mesh;

            MeshRenderer r = horizon.AddComponent<MeshRenderer>();
            r.sharedMaterial = ResolveMaterial(body);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic = false;

            // No UCWaterVolume. The horizon is scenery past the edge of the world -
            // there is no terrain under it, so nothing can float or swim there, and
            // a volume this size would win every Sample() query in the scene.
        }

        /// <summary>
        /// A flat ring at the sea surface, extending outward from the world edge.
        ///
        /// A ring and not a disc: the middle is where the real per-chunk sea meshes
        /// are, and a disc under them would z-fight along the whole surface. It is
        /// eight sectors rather than a smooth circle because the geometry is flat and
        /// perfectly horizontal - subdivision adds vertices that are all coplanar and
        /// changes nothing on screen.
        /// </summary>
        private static Mesh BuildHorizonMesh(float extent)
        {
            const int sectors = 24;

            // Inner radius reaches just inside the generated world so there is no
            // gap, and the per-chunk water draws over the overlap.
            float inner = horizonInnerRadius;
            float outer = inner + Mathf.Max(1f, extent);

            Vector3[] verts = new Vector3[sectors * 2];
            Vector3[] normals = new Vector3[verts.Length];
            Vector4[] tangents = new Vector4[verts.Length];
            Vector2[] uv = new Vector2[verts.Length];
            Color[] colors = new Color[verts.Length];
            int[] tris = new int[sectors * 6];

            for (int i = 0; i < sectors; i++)
            {
                float a = (i / (float)sectors) * Mathf.PI * 2f;
                float cos = Mathf.Cos(a);
                float sin = Mathf.Sin(a);

                int vi = i * 2;
                verts[vi] = new Vector3(cos * inner, 0f, sin * inner);
                verts[vi + 1] = new Vector3(cos * outer, 0f, sin * outer);

                normals[vi] = Vector3.up;
                normals[vi + 1] = Vector3.up;

                tangents[vi] = new Vector4(1f, 0f, 0f, -1f);
                tangents[vi + 1] = new Vector4(1f, 0f, 0f, -1f);

                // No whitewater, and a depth deep enough that the shader's depth
                // ramp reads full ocean colour rather than shoreline shallows.
                uv[vi] = new Vector2(0f, 100f);
                uv[vi + 1] = new Vector2(0f, 100f);

                // Direction 0.5 is "no flow", weight 1 is fully opaque water. The
                // horizon never fades - it is the furthest thing on screen and an
                // alpha ramp there would show the skybox through the sea.
                colors[vi] = new Color(0.5f, 0.5f, 0f, 1f);
                colors[vi + 1] = new Color(0.5f, 0.5f, 0f, 1f);

                int next = (i + 1) % sectors;
                int t = i * 6;

                tris[t + 0] = vi;
                tris[t + 1] = next * 2;
                tris[t + 2] = vi + 1;

                tris[t + 3] = vi + 1;
                tris[t + 4] = next * 2;
                tris[t + 5] = next * 2 + 1;
            }

            Mesh mesh = new Mesh();
            mesh.name = "UC Water Horizon";
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.triangles = tris;

            // Never culled. The horizon is always behind whatever the camera is
            // looking at horizontally, and a bounds test that removes it leaves a
            // visible hard edge where the sea stops.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(outer * 2f, 20f, outer * 2f));

            return mesh;
        }

        /// <summary>
        /// Inner radius of the horizon ring, in metres. Set by the chunk manager to
        /// half the world diagonal so the ring starts just outside the generated
        /// terrain rather than at an arbitrary distance.
        /// </summary>
        public static float horizonInnerRadius = 1000f;

        private static void DestroyHorizon()
        {
            if (horizon == null) return;

            MeshFilter f = horizon.GetComponent<MeshFilter>();
            if (f != null && f.sharedMesh != null) Destroy(f.sharedMesh);

            Destroy(horizon);
            horizon = null;
            horizonLevel = float.NaN;
            horizonExtent = 0f;
        }

        // ---------------------------------------------------------------------
        //  Release
        // ---------------------------------------------------------------------

        /// <summary>Destroys everything this owner spawned. Safe to call when the
        /// owner has nothing.</summary>
        public static void Release(int owner)
        {
            List<GameObject> list;
            if (!spawned.TryGetValue(owner, out list)) return;

            spawned.Remove(owner);
            DestroyAll(list);
        }

        /// <summary>Destroys every water object this applier has spawned, for a full
        /// regenerate or a scene teardown.</summary>
        public static void ReleaseAll()
        {
            foreach (var kv in spawned) DestroyAll(kv.Value);
            spawned.Clear();
            
            ClearBodyParents();

            // Before the materials, because the horizon's renderer holds one of them.
            DestroyHorizon();

            foreach (var kv in materials) Destroy(kv.Value);
            materials.Clear();
        }

        private static void DestroyAll(List<GameObject> list)
        {
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                GameObject go = list[i];
                if (go == null) continue;

                // The mesh is generated, not an asset, so destroying the object
                // leaves it in memory. Meshes are the biggest thing here, so this
                // is the leak that would actually be felt.
                MeshFilter f = go.GetComponent<MeshFilter>();
                if (f != null && f.sharedMesh != null) Destroy(f.sharedMesh);

                Destroy(go);
            }

            list.Clear();
        }

        private static void Destroy(Object o)
        {
            if (o == null) return;

            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        // ---------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------

        private static void Set(Material m, string name, float v)
        {
            if (m.HasProperty(name)) m.SetFloat(name, v);
        }

        private static void Set(Material m, string name, Color v)
        {
            if (m.HasProperty(name)) m.SetColor(name, v);
        }
    }
}
