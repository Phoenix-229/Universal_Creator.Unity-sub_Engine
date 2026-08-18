// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    ScatterApplier.cs
//  Purpose: The main-thread half of scatter. Nodes emit ScatterInstance records
//           off-thread; this turns them into the three things Unity actually
//           draws - TreeInstance entries, detail density grids, and either real
//           GameObjects or GPU-instanced draws.
//
//  Channel routing, and why each type goes where it does:
//
//    trees                  -> TerrainData.treeInstances   (billboards at distance)
//    grass, detail_N        -> TerrainData detail layers   (wind, per-cell counts)
//    rocks/debris/objects/  -> GameObjects or instanced draws
//    underwater/custom
//
//  Trees and details are terrain-owned, so recycling a chunk's TerrainData drops
//  them for free. Objects are scene-owned and have to be released by hand, which
//  is what the returned spawn list is for.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    public static class ScatterApplier
    {
        // Channels that are detail layers rather than discrete instances.
        private const string DetailChannelPrefix = "detail_";

        /// <summary>
        /// Pushes the library's prototype definitions onto the TerrainData. Both
        /// arrays are compared before writing: assigning treePrototypes or
        /// detailPrototypes drops every placed instance on that terrain, so doing
        /// it unconditionally would wipe the scatter on every regeneration pass.
        /// </summary>
        public static void ApplyPrototypes(TerrainData data, ScatterPrototypeLibrary lib)
        {
            if (data == null || lib == null) return;

            ApplyTreePrototypes(data, lib);
            ApplyDetailPrototypes(data, lib);
            ApplyDetailWindClass(lib);
        }

        // Property ids on UniverseCreator/WindDetail.
        private static readonly int IdVegClass = Shader.PropertyToID("_VegetationClass");
        private static readonly int IdWindResp = Shader.PropertyToID("_WindResponse");
        private static readonly int IdSqueeze  = Shader.PropertyToID("_TouchResponse");

        /// <summary>
        /// Tells each detail prototype's material which vegetation class it is.
        /// Terrain details render through the prototype prefab's own material and
        /// there is no per-prototype property block, so the class has to live on
        /// the material - which is fine, because a material is already per-plant.
        ///
        /// Only materials using our detail shader are touched. Anything else is
        /// left exactly as authored.
        /// </summary>
        private static void ApplyDetailWindClass(ScatterPrototypeLibrary lib)
        {
            for (int i = 0; i < lib.details.Count; i++)
            {
                DetailPrototypeDef def = lib.details[i];
                if (def == null || def.prefabMesh == null) continue;

                Renderer[] renderers = def.prefabMesh.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) continue;

                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer rend = renderers[r];
                    if (rend == null) continue;

                    Material m = rend.sharedMaterial;
                    if (m == null || m.shader == null) continue;
                    if (m.shader.name != WindDetailShaderName) continue;

                    float cls = (float)def.vegetationClass;

                    // Only write on a real change. Assigning a material property in
                    // the Editor dirties the asset, and doing that every regeneration
                    // pass would mark the project modified on every chunk load.
                    if (!Mathf.Approximately(m.GetFloat(IdVegClass), cls))
                        m.SetFloat(IdVegClass, cls);

                    float resp = Mathf.Max(0f, def.windResponse);
                    if (!Mathf.Approximately(m.GetFloat(IdWindResp), resp))
                        m.SetFloat(IdWindResp, resp);

                    float sq = Mathf.Max(0f, def.squeeze);
                    if (!Mathf.Approximately(m.GetFloat(IdSqueeze), sq))
                        m.SetFloat(IdSqueeze, sq);
                }
            }
        }

        /// <summary>Shader that understands the vegetation class properties.</summary>
        public const string WindDetailShaderName = "UniverseCreator/WindDetail";

        private static void ApplyTreePrototypes(TerrainData data, ScatterPrototypeLibrary lib)
        {
            List<TreePrototype> wanted = new List<TreePrototype>(lib.trees.Count);

            for (int i = 0; i < lib.trees.Count; i++)
            {
                TreePrototypeDef def = lib.trees[i];
                if (def == null || def.prefab == null) continue;

                TreePrototype p = new TreePrototype();
                p.prefab = def.prefab;
                p.bendFactor = Mathf.Max(0f, def.bendFactor);
                wanted.Add(p);
            }

            TreePrototype[] current = data.treePrototypes;
            if (SameTreeSet(current, wanted)) return;

            data.treePrototypes = wanted.ToArray();
        }

        private static bool SameTreeSet(TreePrototype[] current, List<TreePrototype> wanted)
        {
            if (current == null) return wanted.Count == 0;
            if (current.Length != wanted.Count) return false;

            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] == null) return false;
                if (!ReferenceEquals(current[i].prefab, wanted[i].prefab)) return false;
                if (!Mathf.Approximately(current[i].bendFactor, wanted[i].bendFactor)) return false;
            }
            return true;
        }

        private static void ApplyDetailPrototypes(TerrainData data, ScatterPrototypeLibrary lib)
        {
            List<DetailPrototype> wanted = new List<DetailPrototype>(lib.details.Count);

            for (int i = 0; i < lib.details.Count; i++)
            {
                DetailPrototypeDef def = lib.details[i];
                if (def == null) continue;
                if (def.prefabMesh == null && def.billboardTexture == null) continue;

                DetailPrototype p = new DetailPrototype();

                if (def.prefabMesh != null)
                {
                    // A mesh prototype is the only path that can be GPU instanced,
                    // and VertexLit is the only render mode Unity accepts with one.
                    p.usePrototypeMesh = true;
                    p.prototype = def.prefabMesh;
                    p.renderMode = DetailRenderMode.VertexLit;
                    p.useInstancing = def.useInstancing;
                }
                else
                {
                    // Billboards always face the camera and never instance.
                    p.usePrototypeMesh = false;
                    p.prototypeTexture = def.billboardTexture;
                    p.renderMode = DetailRenderMode.GrassBillboard;
                    p.useInstancing = false;
                }

                p.minWidth = Mathf.Max(0.01f, Mathf.Min(def.width.x, def.width.y));
                p.maxWidth = Mathf.Max(p.minWidth, Mathf.Max(def.width.x, def.width.y));
                p.minHeight = Mathf.Max(0.01f, Mathf.Min(def.height.x, def.height.y));
                p.maxHeight = Mathf.Max(p.minHeight, Mathf.Max(def.height.x, def.height.y));

                p.noiseSpread = Mathf.Max(0.001f, def.noiseSpread);
                p.healthyColor = def.healthyColour;
                p.dryColor = def.dryColour;
                p.alignToGround = def.alignToGround ? 1f : 0f;

                // Padding keeps grass from poking through a carved tunnel mouth.
                p.holeEdgePadding = 0f;

                wanted.Add(p);
            }

            DetailPrototype[] current = data.detailPrototypes;
            if (SameDetailSet(current, wanted)) return;

            data.detailPrototypes = wanted.ToArray();
        }

        private static bool SameDetailSet(DetailPrototype[] current, List<DetailPrototype> wanted)
        {
            if (current == null) return wanted.Count == 0;
            if (current.Length != wanted.Count) return false;

            for (int i = 0; i < current.Length; i++)
            {
                DetailPrototype a = current[i];
                DetailPrototype b = wanted[i];
                if (a == null) return false;

                if (a.usePrototypeMesh != b.usePrototypeMesh) return false;
                if (!ReferenceEquals(a.prototype, b.prototype)) return false;
                if (!ReferenceEquals(a.prototypeTexture, b.prototypeTexture)) return false;
                if (a.renderMode != b.renderMode) return false;
                if (a.useInstancing != b.useInstancing) return false;
                if (!Mathf.Approximately(a.minWidth, b.minWidth)) return false;
                if (!Mathf.Approximately(a.maxWidth, b.maxWidth)) return false;
                if (!Mathf.Approximately(a.minHeight, b.minHeight)) return false;
                if (!Mathf.Approximately(a.maxHeight, b.maxHeight)) return false;
                if (a.healthyColor != b.healthyColor) return false;
                if (a.dryColor != b.dryColor) return false;
            }
            return true;
        }

        /// <summary>
        /// Commits every scatter channel for one chunk. Returns the GameObjects it
        /// spawned so the caller can release them when the chunk is recycled;
        /// trees and details need no such bookkeeping because the TerrainData owns
        /// them and is overwritten on the next pass.
        /// </summary>
        public static void Apply(Terrain terrain, TerrainData data, OutputBag outputs,
                                 ScatterPrototypeLibrary lib, Transform spawnParent,
                                 GPUInstancingManager instancing, int instancingOwner,
                                 List<GameObject> spawnedOut)
        {
            if (terrain == null || data == null || outputs == null || lib == null) return;

            // Withdraw this chunk's previous instanced draws before adding new ones,
            // or a recentred grid draws every prop twice.
            if (instancing != null) instancing.ClearOwner(instancingOwner);

            ApplyPrototypes(data, lib);
            ApplyDetailGrids(data, outputs, lib);

            if (outputs.scatter == null || outputs.scatter.Count == 0) return;

            List<ScatterInstance> trees;
            if (outputs.scatter.TryGetValue("trees", out trees))
                ApplyTrees(terrain, data, trees, lib);

            // Shrubs are their own channel because they are their own thing: a
            // trigger collider you can walk through, and a wind model that holds
            // its base. Routing them through the object path would give them a
            // solid collider and grass-like motion, both wrong.
            List<ScatterInstance> shrubs;
            if (outputs.scatter.TryGetValue("shrubs", out shrubs))
                ApplyShrubs(shrubs, lib, spawnParent, instancing, instancingOwner, spawnedOut);

            int objectBudget = Mathf.Max(0, lib.maxObjectsPerChunk);

            foreach (KeyValuePair<string, List<ScatterInstance>> kvp in outputs.scatter)
            {
                string channel = kvp.Key;
                if (channel == "trees" || channel == "grass" || channel == "shrubs") continue;
                if (channel.StartsWith(DetailChannelPrefix)) continue;

                List<ScatterInstance> list = kvp.Value;
                if (list == null || list.Count == 0) continue;

                objectBudget -= ApplyObjects(list, lib, spawnParent, instancing, instancingOwner,
                                             spawnedOut, objectBudget);
                if (objectBudget <= 0) break;
            }
        }

        /// <summary>
        /// Writes the detail density grids the graph produced. Grids arrive at the
        /// heightfield's resolution because that is the space the nodes work in, and
        /// SetDetailLayer demands exactly detailResolution squared - so they are
        /// resampled here rather than at the node, where the terrain's resolution
        /// is not knowable.
        /// </summary>
        private static void ApplyDetailGrids(TerrainData data, OutputBag outputs,
                                             ScatterPrototypeLibrary lib)
        {
            if (outputs.detailLayers == null || outputs.detailLayers.Count == 0) return;

            DetailPrototype[] protos = data.detailPrototypes;
            if (protos == null || protos.Length == 0) return;

            // Instance counts, not coverage percentages. SetDetailLayer means two
            // different things depending on this mode, and the nodes emit counts.
            if (data.detailScatterMode != DetailScatterMode.InstanceCountMode)
                data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);

            int detailRes = data.detailResolution;
            if (detailRes < 1) return;

            float scale = Mathf.Max(0f, lib.globalDetailDensityScale);

            foreach (KeyValuePair<int, int[,]> kvp in outputs.detailLayers)
            {
                int layer = kvp.Key;
                int[,] src = kvp.Value;
                if (src == null) continue;
                if (layer < 0 || layer >= protos.Length) continue;

                int srcZ = src.GetLength(0);
                int srcX = src.GetLength(1);
                if (srcZ < 1 || srcX < 1) continue;

                int[,] dst;

                if (srcZ == detailRes && srcX == detailRes && Mathf.Approximately(scale, 1f))
                {
                    dst = src;
                }
                else
                {
                    dst = new int[detailRes, detailRes];

                    // Nearest-neighbour on purpose. These are counts, and averaging
                    // them across a downsample turns a sharp meadow edge into a haze
                    // of single blades over ground that should be bare.
                    float stepZ = (float)srcZ / detailRes;
                    float stepX = (float)srcX / detailRes;

                    for (int z = 0; z < detailRes; z++)
                    {
                        int sz = Mathf.Min(srcZ - 1, (int)(z * stepZ));
                        for (int x = 0; x < detailRes; x++)
                        {
                            int sx = Mathf.Min(srcX - 1, (int)(x * stepX));
                            int v = src[sz, sx];
                            if (v <= 0) continue;
                            dst[z, x] = Mathf.Max(0, Mathf.RoundToInt(v * scale));
                        }
                    }
                }

                data.SetDetailLayer(0, 0, layer, dst);
            }
        }

        /// <summary>
        /// Converts tree instances to TerrainData.treeInstances. Positions arrive in
        /// absolute world space and Unity wants them normalised to the terrain's own
        /// box, so anything outside this chunk is dropped rather than clamped: a
        /// clamped tree lands on the boundary in a line, and the neighbouring chunk
        /// owns that ground anyway.
        /// </summary>
        private static void ApplyTrees(Terrain terrain, TerrainData data,
                                       List<ScatterInstance> instances,
                                       ScatterPrototypeLibrary lib)
        {
            TreePrototype[] protos = data.treePrototypes;
            if (protos == null || protos.Length == 0)
            {
                // No prototypes means nothing to index. Clearing rather than leaving
                // the previous chunk's trees behind on a recycled TerrainData.
                if (data.treeInstanceCount > 0) data.SetTreeInstances(new TreeInstance[0], true);
                return;
            }

            Vector3 tPos = terrain.GetPosition();
            Vector3 tSize = data.size;
            if (tSize.x <= 1e-4f || tSize.z <= 1e-4f || tSize.y <= 1e-4f) return;

            // The library's per-prototype altitude and slope limits are applied here
            // rather than in the node, because a node emits into one channel while
            // the prototype it resolves to can differ per instance.
            Noise variation = new Noise(20749);

            List<TreeInstance> outList = new List<TreeInstance>(instances.Count);

            for (int i = 0; i < instances.Count; i++)
            {
                ScatterInstance inst = instances[i];

                int protoIndex = inst.prototypeIndex;
                if (protoIndex < 0 || protoIndex >= protos.Length) continue;

                Vector3 local = inst.position - tPos;

                float nx = local.x / tSize.x;
                float nz = local.z / tSize.z;
                if (nx < 0f || nx > 1f || nz < 0f || nz > 1f) continue;

                float ny = Mathf.Clamp01(local.y / tSize.y);

                TreePrototypeDef def = protoIndex < lib.trees.Count ? lib.trees[protoIndex] : null;
                if (def != null)
                {
                    if (ny < def.minAltitude || ny > def.maxAltitude) continue;

                    // Slope from the terrain itself, not from the graph's heightfield:
                    // this is the surface the tree will actually stand on, after
                    // sculpt overrides and stitching.
                    float steepness = data.GetSteepness(nx, nz);
                    if (steepness > def.maxSlope) continue;
                }

                float rw, rh;
                if (def != null)
                {
                    int hx = Mathf.RoundToInt(inst.position.x * 100f);
                    int hz = Mathf.RoundToInt(inst.position.z * 100f);
                    float r = variation.Random(hx, hz);
                    float r2 = variation.Random(hz + 15486071, hx + 32452843);

                    rw = Mathf.Lerp(Mathf.Min(def.widthScale.x, def.widthScale.y),
                                    Mathf.Max(def.widthScale.x, def.widthScale.y), r);
                    rh = Mathf.Lerp(Mathf.Min(def.heightScale.x, def.heightScale.y),
                                    Mathf.Max(def.heightScale.x, def.heightScale.y), r2);

                    // The node's own scale still counts, so a scatter node set to
                    // half scale halves the whole stand.
                    rw *= inst.scale.x;
                    rh *= inst.scale.y;
                }
                else
                {
                    rw = inst.scale.x;
                    rh = inst.scale.y;
                }

                TreeInstance t = new TreeInstance();
                t.position = new Vector3(nx, ny, nz);
                t.prototypeIndex = protoIndex;
                t.widthScale = Mathf.Max(0.01f, rw);
                t.heightScale = Mathf.Max(0.01f, rh);
                t.rotation = inst.rotation.eulerAngles.y * Mathf.Deg2Rad;
                t.color = Color.white;
                t.lightmapColor = Color.white;

                outList.Add(t);
            }

            // snapToHeightmap false: the Y already came from the graph's heightfield
            // plus any sculpt override, and re-snapping would re-sample a heightmap
            // that may not be synced yet on this pass.
            data.SetTreeInstances(outList.ToArray(), false);
        }

        /// <summary>
        /// Spawns the shrub channel. Two spawn shapes, chosen per instance:
        ///
        ///   near  A real GameObject with UCShrubInstance on it, carrying a trigger
        ///         capsule. The player walks into it, passes through, and the shrub
        ///         reports the entry so gameplay can slow them or rustle.
        ///   far   A GPU-instanced draw. Keeps the wind, loses the collider. The
        ///         right trade for fill you will never reach - a collider on a bush
        ///         600 metres away is pure cost.
        ///
        /// The collider budget, not the distance, decides which: whichever shrubs
        /// come first in the chunk get the colliders. Scatter nodes emit roughly in
        /// placement order, so that lands sensibly without a distance sort per frame.
        /// </summary>
        private static void ApplyShrubs(List<ScatterInstance> instances,
                                        ScatterPrototypeLibrary lib, Transform parent,
                                        GPUInstancingManager instancing, int instancingOwner,
                                        List<GameObject> spawnedOut)
        {
            if (instances == null || instances.Count == 0) return;
            if (lib.shrubs.Count == 0) return;

            int budget = Mathf.Max(0, lib.maxShrubsPerChunk);
            int colliderBudget = Mathf.Max(0, lib.shrubColliderBudget);
            int spawned = 0;

            for (int i = 0; i < instances.Count && spawned < budget; i++)
            {
                ScatterInstance inst = instances[i];

                int protoIndex = inst.prototypeIndex;
                if (protoIndex < 0 || protoIndex >= lib.shrubs.Count) continue;

                ShrubPrototypeDef def = lib.shrubs[protoIndex];
                if (def == null || def.prefab == null) continue;

                // Zero scale is how a scatter node marks an instance it thinned
                // out - Shrub Scatter uses it to cut gaps between thickets. Spawning
                // one would put an invisible trigger collider in the world.
                if (inst.scale.x <= 0.0001f) continue;

                Vector3 pos = inst.position;
                if (def.embedDepth > 0.0001f)
                    pos.y -= def.embedDepth * inst.scale.y;

                bool wantsCollider = def.spawnCollider && spawned < colliderBudget;
                bool instanced = !wantsCollider && def.gpuInstanceWhenDense;

                if (instanced)
                {
                    if (instancing == null) continue;
                    if (!AddInstancedDraw(instancing, instancingOwner, def.prefab,
                                          pos, inst.rotation, inst.scale, parent))
                        continue;
                }
                else
                {
                    GameObject go = Object.Instantiate(def.prefab, pos, inst.rotation, parent);
                    go.transform.localScale = Vector3.Scale(def.prefab.transform.localScale,
                                                            inst.scale);

                    // Never static. A static shrub is batched into the scene's
                    // combined mesh, and a combined mesh has one shared object
                    // origin - so every bush in the batch would take its wind pivot
                    // from that origin and the whole patch would bend as one slab.
                    go.isStatic = false;

                    UCShrubInstance shrub = go.GetComponent<UCShrubInstance>();
                    if (shrub == null) shrub = go.AddComponent<UCShrubInstance>();

                    // Average of the instance scale: a shrub scaled unevenly still
                    // needs one number for its trigger and its wind pivot height.
                    float scale = (inst.scale.x + inst.scale.y + inst.scale.z) / 3f;
                    shrub.Configure(def, scale);

                    if (spawnedOut != null) spawnedOut.Add(go);
                }

                spawned++;
            }
        }

        private static int ApplyObjects(List<ScatterInstance> instances,
                                        ScatterPrototypeLibrary lib, Transform parent,
                                        GPUInstancingManager instancing, int instancingOwner,
                                        List<GameObject> spawnedOut, int budget)
        {
            if (lib.objects.Count == 0 || budget <= 0) return 0;

            bool forceInstanced = instances.Count > Mathf.Max(1, lib.gpuInstancingThreshold);
            int used = 0;

            for (int i = 0; i < instances.Count && used < budget; i++)
            {
                ScatterInstance inst = instances[i];

                int protoIndex = inst.prototypeIndex;
                if (protoIndex < 0 || protoIndex >= lib.objects.Count) continue;

                ObjectPrototypeDef def = lib.objects[protoIndex];
                if (def == null || def.prefab == null) continue;

                // Embed sinks the object so a boulder sits in the ground rather than
                // balancing on it. Measured against the object's own scale, so a big
                // rock buries proportionally deeper.
                Vector3 pos = inst.position;
                if (def.embedDepth > 0.0001f)
                    pos.y -= def.embedDepth * inst.scale.y;

                Quaternion rot = inst.rotation;

                if (forceInstanced || def.gpuInstanced)
                {
                    if (instancing == null) continue;
                    if (!AddInstancedDraw(instancing, instancingOwner, def.prefab,
                                          pos, rot, inst.scale, parent))
                        continue;
                }
                else
                {
                    GameObject go = Object.Instantiate(def.prefab, pos, rot, parent);
                    go.transform.localScale = Vector3.Scale(def.prefab.transform.localScale,
                                                            inst.scale);
                    if (def.markStatic) go.isStatic = true;
                    if (spawnedOut != null) spawnedOut.Add(go);
                }

                used++;
            }

            return used;
        }

        /// <summary>
        /// Queues one instanced draw per renderer on the prefab. Walking the prefab's
        /// renderers rather than assuming a single MeshRenderer matters because a rock
        /// prefab commonly carries a separate moss or snow mesh.
        /// </summary>
        private static bool AddInstancedDraw(GPUInstancingManager instancing, int owner,
                                             GameObject prefab,
                                             Vector3 pos, Quaternion rot, Vector3 scale,
                                             Transform parent)
        {
            MeshRenderer[] renderers = prefab.GetComponentsInChildren<MeshRenderer>();
            if (renderers == null || renderers.Length == 0) return false;

            int layer = parent != null ? parent.gameObject.layer : prefab.layer;
            bool any = false;

            for (int r = 0; r < renderers.Length; r++)
            {
                MeshRenderer mr = renderers[r];
                if (mr == null) continue;

                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                if (mr.sharedMaterial == null) continue;

                // The child's own offset inside the prefab has to survive, or a
                // two-part prefab collapses onto one origin.
                Matrix4x4 childLocal = Matrix4x4.TRS(mr.transform.localPosition,
                                                     mr.transform.localRotation,
                                                     mr.transform.localScale);
                Matrix4x4 world = Matrix4x4.TRS(pos, rot, scale) * childLocal;

                instancing.AddInstance(owner, mf.sharedMesh, mr.sharedMaterial, world, layer,
                                       mr.shadowCastingMode, mr.receiveShadows);
                any = true;
            }

            return any;
        }

        /// <summary>
        /// Destroys the GameObjects a chunk spawned. Trees and details are not listed
        /// here because the TerrainData owns those and overwriting it releases them.
        /// </summary>
        public static void Release(List<GameObject> spawned)
        {
            if (spawned == null || spawned.Count == 0) return;

            for (int i = spawned.Count - 1; i >= 0; i--)
            {
                GameObject go = spawned[i];
                if (go == null) continue;

                if (Application.isPlaying) Object.Destroy(go);
                else Object.DestroyImmediate(go);
            }

            spawned.Clear();
        }

        /// <summary>
        /// Clears terrain-owned scatter for a chunk being recycled onto new ground,
        /// so a cell does not briefly draw the previous cell's forest.
        /// </summary>
        public static void ClearTerrainScatter(TerrainData data)
        {
            if (data == null) return;

            if (data.treeInstanceCount > 0)
                data.SetTreeInstances(new TreeInstance[0], false);

            DetailPrototype[] protos = data.detailPrototypes;
            if (protos == null || protos.Length == 0) return;

            int res = data.detailResolution;
            if (res < 1) return;

            int[,] empty = new int[res, res];
            for (int i = 0; i < protos.Length; i++)
                data.SetDetailLayer(0, 0, i, empty);
        }
    }
}
