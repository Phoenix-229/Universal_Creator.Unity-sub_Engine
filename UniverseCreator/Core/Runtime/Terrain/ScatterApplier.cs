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

        // Fixed, because the dither must be the same on every rebuild and on every
        // machine. It is deliberately not derived from a graph seed: this decides a
        // fraction of one blade inside one cell, and tying it to the world seed would
        // make two worlds that differ only in seed disagree about grass totals for a
        // reason no author could see.
        private const int DetailDitherSeed = 24036583;

        /// <summary>
        /// Pushes the library's prototype definitions onto the TerrainData. Both
        /// arrays are compared before writing: assigning treePrototypes or
        /// detailPrototypes drops every placed instance on that terrain, so doing
        /// it unconditionally would wipe the scatter on every regeneration pass.
        /// </summary>
        public static void ApplyPrototypes(TerrainData data, ScatterPrototypeLibrary lib,
                                           UCVegetationSystemManager veg = null,
                                           float chunkAltitudeM = 0f)
        {
            if (data == null || lib == null) return;

            ApplyTreePrototypes(data, lib);
            ApplyDetailPrototypes(data, lib, veg, chunkAltitudeM);
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

        /// <summary>Keyword that switches on the shader's own season handling.</summary>
        public const string SeasonTintKeyword = "_SEASONTINT_ON";

        /// <summary>
        /// Whether this prototype resolves the season in its own shader, in which
        /// case the CPU must not also write the seasonal prototype colours.
        ///
        /// Both halves have to be true. A material can only resolve the season if
        /// the world is actually publishing the season globals, and those sit behind
        /// their own switch - with them off the shader reads the zeroes that mean
        /// "high summer, dry, no snow" and would hold the meadow in permanent June.
        /// So if nobody is publishing, ownership stays on the CPU.
        ///
        /// On the material side the keyword is what decides it, not the
        /// _SeasonTintEnable float, because the keyword is what the shader's #ifdef
        /// actually reads. A material with the float raised but the keyword off
        /// renders no season either.
        ///
        /// One renderer is enough. A prototype mixing our detail shader with another
        /// gets the shader path, since the alternative double-applies the season to
        /// the part that already handles it - too dark beats inconsistent.
        /// </summary>
        private static bool DetailShaderOwnsSeason(DetailPrototypeDef def, UCVegetationSystemManager veg)
        {
            if (veg == null || !veg.publishSeasonGlobals) return false;

            // Billboard-only prototypes have no prefab and therefore no material of
            // ours, so they keep the CPU tint.
            if (def == null || def.prefabMesh == null) return false;

            Renderer[] renderers = def.prefabMesh.GetComponentsInChildren<Renderer>(true);
            if (renderers == null) return false;

            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer rend = renderers[r];
                if (rend == null) continue;

                Material m = rend.sharedMaterial;
                if (m == null || m.shader == null) continue;
                if (m.shader.name != WindDetailShaderName) continue;

                if (m.IsKeywordEnabled(SeasonTintKeyword)) return true;
            }

            return false;
        }

        /// <summary>
        /// Library slot -> terrain prototype index, or -1 for a slot that has none.
        ///
        /// These two numbers are not the same one, and the difference is not cosmetic.
        /// TerrainData holds only prototypes it can actually draw, so a library slot
        /// with a null prefab contributes nothing and every slot after it shifts down
        /// by one. Scatter nodes emit the LIBRARY slot, because that is the index an
        /// author authored against. Feeding a library slot straight into
        /// TreeInstance.prototypeIndex therefore plants the wrong species from the
        /// first empty slot onward - and V-51 exists precisely because an empty slot
        /// is a normal authoring state, not an impossible one.
        ///
        /// One helper, so the skip rule lives in one place: ApplyTreePrototypes builds
        /// the array this maps into. If the two ever disagreed, the world would quietly
        /// draw a pine where a birch was authored and nothing would report it.
        /// </summary>
        private static int[] BuildTreeSlotMap(ScatterPrototypeLibrary lib)
        {
            int[] map = new int[lib.trees.Count];
            int next = 0;

            for (int i = 0; i < lib.trees.Count; i++)
            {
                TreePrototypeDef def = lib.trees[i];
                map[i] = (def != null && def.prefab != null) ? next++ : -1;
            }

            return map;
        }

        private static void ApplyTreePrototypes(TerrainData data, ScatterPrototypeLibrary lib)
        {
            int[] map = BuildTreeSlotMap(lib);

            List<TreePrototype> wanted = new List<TreePrototype>(lib.trees.Count);

            for (int i = 0; i < lib.trees.Count; i++)
            {
                if (map[i] < 0) continue;

                TreePrototypeDef def = lib.trees[i];

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

        private static void ApplyDetailPrototypes(TerrainData data, ScatterPrototypeLibrary lib,
                                                  UCVegetationSystemManager veg,
                                                  float chunkAltitudeM)
        {
            List<DetailPrototype> wanted = new List<DetailPrototype>(lib.details.Count);

            // V-36/37/38. Resolved once for the chunk, not once per prototype: the
            // season and the weather are world facts, and only the species envelope
            // differs from one detail to the next.
            bool seasonal = veg != null && veg.enableSeasonalPhenology;

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

                if (seasonal && def.eco != null)
                {
                    Ecology.PhenologyState state = veg.PhenologyFor(def.eco, chunkAltitudeM);

                    // V-37. Grass dies back to its winter height and returns to the
                    // authored height in summer. Applied to both ends of the range so
                    // the within-species variation survives the scaling.
                    float hs = Ecology.Phenology.SeasonalHeightScale(
                        def.eco.seasonalHeightScale, state.canopyFraction);

                    p.minHeight = Mathf.Max(0.01f, p.minHeight * hs);
                    p.maxHeight = Mathf.Max(p.minHeight, p.maxHeight * hs);

                    // ONE OWNER FOR THE SEASON COLOUR, decided here in code rather
                    // than by asking an author to remember to zero two properties.
                    //
                    // Unity's detail renderer delivers healthyColor/dryColor to the
                    // shader as vertex colour, and the shader MULTIPLIES it into
                    // albedo. A multiply cannot repigment: a green grass texture
                    // carries almost no red for an autumn tint to scale, so this
                    // path can only ever darken the sward toward dirty green. That
                    // is the best a CPU tint can do through that renderer, and it is
                    // what still runs for any prototype whose material is not ours.
                    //
                    // A material on UniverseCreator/WindDetail with _SEASONTINT_ON
                    // does the same job properly - per pixel, hue rotated, and cured
                    // tip-first along the blade from the same globals. Where that is
                    // available the colour is left to it and the authored colours
                    // pass through untouched, because writing both would apply the
                    // season twice and land the meadow black in an autumn storm.
                    //
                    // Height is not in that argument. Only the CPU can resize a
                    // prototype, so V-37 above applies either way.
                    if (!DetailShaderOwnsSeason(def, veg))
                    {
                        // V-36/38. The healthy colour carries the season, rain and
                        // snow. The dry colour is the same chain over the authored
                        // dry tint, so Unity's own healthy/dry noise blend still has
                        // two ends to work between instead of collapsing to one flat
                        // colour.
                        float wet = veg.ResolvedWetness;
                        float snow = veg.SnowCoverFractionAt(chunkAltitudeM);
                        float phase = veg.InterpolatedSeasonPhase;

                        p.healthyColor = Ecology.Phenology.AppearanceColour(
                            def.eco, phase, state, wet, snow);

                        Color dry = Ecology.Phenology.WetDarken(def.dryColour, wet, def.eco.wetDarkening);
                        p.dryColor = Ecology.Phenology.SnowBlend(
                            dry, Ecology.Phenology.CanopySnowFraction(
                                snow, def.eco.snowCanopyAccumulation, state.canopyFraction));
                    }
                }

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
        ///
        /// The vegetation manager arrives as a parameter rather than being looked up
        /// here (V-54). ForWorld walks GetComponentInParent then GetComponentInChildren,
        /// and this runs once per chunk per apply - on a streaming grid that is the same
        /// hierarchy search repeated every time a cell lands, for an answer that cannot
        /// change between cells. ChunkManager caches it exactly as it caches the wind
        /// manager, and null stays a legitimate value: a world with no vegetation
        /// manager scatters with pre-Module-3 behaviour rather than failing.
        /// </summary>
        public static void Apply(Terrain terrain, TerrainData data, OutputBag outputs,
                                 ScatterPrototypeLibrary lib, Transform spawnParent,
                                 GPUInstancingManager instancing, int instancingOwner,
                                 UCVegetationSystemManager veg,
                                 List<GameObject> spawnedOut)
        {
            if (terrain == null || data == null || outputs == null || lib == null) return;

            // Withdraw this chunk's previous instanced draws before adding new ones,
            // or a recentred grid draws every prop twice.
            if (instancing != null) instancing.ClearOwner(instancingOwner);

            // The device's shadow-caster range, resolved once per chunk rather than
            // once per instance. A species can ask for less than this but never more,
            // so the budget travels down and EcoEnvelope clamps against it. (V-32)
            float shadowBudgetM = -1f;
            if (veg != null && veg.vegetationEnabled) shadowBudgetM = veg.shadowCasterDistanceMetres;

            // V-58. Opens this chunk's registry entry, discarding whatever it held
            // before. Here rather than inside each channel because a chunk is the unit
            // that streams: the entry has to exist before the first channel reports,
            // and it has to be replaced wholesale when a recentred grid re-applies the
            // same owner id. instancingOwner is reused as the registry key on purpose -
            // it is already this chunk's identity for the instancing manager one line
            // above, and a second identity scheme would be one more thing to keep in
            // step across a stream-out.
            if (veg != null) veg.BeginChunkRegistration(instancingOwner);

            // V-52. The zone tally is already complete: species assignment filled it
            // during graph eval, long before this main-thread apply. Reported here,
            // right after the entry is opened, rather than at the end - the empty-scatter
            // return below would otherwise skip it, and it belongs to what this chunk
            // contains, not to any one commit path. It is the ADMITTED planting (before
            // the render budgets thin the trees, shrubs and objects further down), which
            // is why it is read from the bag and not summed from the placed instances.
            if (veg != null) veg.RegisterZones(instancingOwner, outputs.zoneHistogram);

            // The chunk's own ground altitude, sampled once at its centre. Phenology
            // is altitude-dependent (lapse rate), and a chunk is small enough that
            // one figure per chunk is the right grain - a figure per instance would
            // be exactly the per-instance ecology V-40 forbids. (V-35)
            float chunkAltitudeM = 0f;
            if (veg != null && veg.enableSeasonalPhenology)
            {
                Vector3 tp = terrain.GetPosition();
                Vector3 centre = tp + new Vector3(data.size.x * 0.5f, 0f, data.size.z * 0.5f);
                chunkAltitudeM = tp.y + terrain.SampleHeight(centre);
            }

            ApplyPrototypes(data, lib, veg, chunkAltitudeM);
            ApplyDetailGrids(data, outputs, lib, veg, instancingOwner);

            // Returns with the chunk's registry entry open and empty, which is the
            // honest state: this chunk applied and placed nothing.
            if (outputs.scatter == null || outputs.scatter.Count == 0) return;

            List<ScatterInstance> trees;
            if (outputs.scatter.TryGetValue("trees", out trees))
                ApplyTrees(terrain, data, trees, lib, veg, chunkAltitudeM, instancingOwner);

            // Shrubs are their own channel because they are their own thing: a
            // trigger collider you can walk through, and a wind model that holds
            // its base. Routing them through the object path would give them a
            // solid collider and grass-like motion, both wrong.
            List<ScatterInstance> shrubs;
            if (outputs.scatter.TryGetValue("shrubs", out shrubs))
                ApplyShrubs(shrubs, lib, spawnParent, instancing, instancingOwner, spawnedOut,
                            shadowBudgetM, veg);

            int objectBudget = Mathf.Max(0, lib.maxObjectsPerChunk);

            foreach (KeyValuePair<string, List<ScatterInstance>> kvp in outputs.scatter)
            {
                string channel = kvp.Key;
                if (channel == "trees" || channel == "grass" || channel == "shrubs") continue;
                if (channel.StartsWith(DetailChannelPrefix)) continue;

                List<ScatterInstance> list = kvp.Value;
                if (list == null || list.Count == 0) continue;

                objectBudget -= ApplyObjects(list, lib, spawnParent, instancing, instancingOwner,
                                             spawnedOut, objectBudget, shadowBudgetM, veg);
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
                                             ScatterPrototypeLibrary lib,
                                             UCVegetationSystemManager veg,
                                             int instancingOwner)
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

            // V-58. Details are registered against the LIBRARY slot, because that is
            // the index the inspector's detail list is drawn from and therefore the
            // one an author is looking at. The grid itself is keyed by the terrain
            // layer index, which is what Grass Detail and Grass Output author directly,
            // so the two are reconciled here rather than either side changing meaning.
            int[] slotMap = veg != null ? BuildDetailSlotMap(lib) : null;
            int[] placed = veg != null ? new int[lib.details.Count] : null;

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

                    // A count is instances in ONE cell, so changing the cell size
                    // changes what the number means. Four destination cells covering
                    // one source cell must share its blades, not each inherit the full
                    // figure - without this a node's blades per square metre silently
                    // multiplies by the resolution ratio, which is the whole reason
                    // the shipped grassPerSquareMetre field never matched the scene.
                    float areaRatio = stepX * stepZ;

                    // The remainder is dithered rather than rounded. At a quarter of a
                    // blade per cell, rounding gives zero everywhere and a sparse
                    // meadow disappears; a hash of the cell decides instead, so the
                    // total is preserved and the same cells decide the same way on
                    // every rebuild.
                    Noise dither = new Noise(DetailDitherSeed);

                    for (int z = 0; z < detailRes; z++)
                    {
                        int sz = Mathf.Min(srcZ - 1, (int)(z * stepZ));
                        for (int x = 0; x < detailRes; x++)
                        {
                            int sx = Mathf.Min(srcX - 1, (int)(x * stepX));
                            int v = src[sz, sx];
                            if (v <= 0) continue;

                            float exact = v * scale * areaRatio;
                            int whole = (int)exact;

                            if (exact - whole > dither.Random(x, z)) whole++;

                            if (whole > 0) dst[z, x] = whole;
                        }
                    }
                }

                data.SetDetailLayer(0, 0, layer, dst);

                if (placed == null) continue;

                // Summed from the grid that was actually written, not from the source
                // the node emitted: the resample path rescales counts by cell area and
                // dithers the remainder, so the two figures differ by design whenever
                // the node's resolution is not the terrain's.
                int slot = SlotForLayer(slotMap, layer);
                if (slot >= 0) placed[slot] += SumGrid(dst);
            }

            // Detail instances carry no colliders, no GameObjects and no instanced draws
            // of ours - the terrain's own detail renderer submits them - so only the
            // counts are reported.
            FlushRegistration(veg, instancingOwner, UCScatterKind.Detail, placed, 0, 0, 0);
        }

        /// <summary>
        /// Library slot -> terrain detail layer index, or -1 for a slot that has none.
        /// Same compaction as trees: a detail slot with neither a mesh nor a billboard
        /// contributes no prototype, and every slot after it shifts down.
        ///
        /// Unlike trees, nothing in the scatter path needs this to be correct - Grass
        /// Detail and Grass Output author the terrain layer index directly, so the grid
        /// side never consults the library. It exists only so a count can be reported
        /// under the species an author is actually looking at. That asymmetry is worth
        /// a validation row rather than a silent remap (V-51): an author who deletes a
        /// detail prefab shifts which layer their grass node feeds, and only a warning
        /// can tell them so.
        /// </summary>
        private static int[] BuildDetailSlotMap(ScatterPrototypeLibrary lib)
        {
            int[] map = new int[lib.details.Count];
            int next = 0;

            for (int i = 0; i < lib.details.Count; i++)
            {
                DetailPrototypeDef def = lib.details[i];

                bool drawable = def != null
                             && (def.prefabMesh != null || def.billboardTexture != null);

                map[i] = drawable ? next++ : -1;
            }

            return map;
        }

        /// <summary>
        /// Reverses <see cref="BuildDetailSlotMap"/> for one layer. A linear scan
        /// because a library holds a handful of details and this runs once per layer
        /// per chunk, where an inverse array would be one more thing to keep in step.
        /// </summary>
        private static int SlotForLayer(int[] slotMap, int layer)
        {
            if (slotMap == null) return -1;

            for (int i = 0; i < slotMap.Length; i++)
                if (slotMap[i] == layer) return i;

            return -1;
        }

        /// <summary>
        /// Total instances a detail grid asks the terrain to draw.
        ///
        /// One pass over an array that was just written and is still in cache, once per
        /// layer per chunk build. That is affordable next to SetDetailLayer above it,
        /// which marshals the same array into native memory - and it is the only honest
        /// way to get the figure, because a count grid has no running total.
        /// </summary>
        private static int SumGrid(int[,] grid)
        {
            if (grid == null) return 0;

            int rows = grid.GetLength(0);
            int cols = grid.GetLength(1);
            int total = 0;

            for (int z = 0; z < rows; z++)
                for (int x = 0; x < cols; x++)
                    total += grid[z, x];

            return total;
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
                                       ScatterPrototypeLibrary lib,
                                       UCVegetationSystemManager veg,
                                       float chunkAltitudeM,
                                       int instancingOwner)
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

            // Everything below indexes by LIBRARY slot and translates to the terrain
            // prototype index only at the point of writing the instance. See
            // BuildTreeSlotMap for why conflating the two plants the wrong species.
            int[] slotMap = BuildTreeSlotMap(lib);

            // V-36. One seasonal colour per species per chunk, solved before the
            // instance loop. Trees of one species standing on one chunk are in the
            // same season at the same altitude, so solving inside the loop would be
            // the same answer computed a thousand times.
            Color[] seasonColour = null;
            if (veg != null && veg.enableSeasonalPhenology)
            {
                seasonColour = new Color[lib.trees.Count];
                for (int p = 0; p < lib.trees.Count; p++)
                {
                    Ecology.EcoEnvelope eco = lib.trees[p] != null ? lib.trees[p].eco : null;

                    seasonColour[p] = eco != null
                        ? veg.SeasonalAppearance(eco, chunkAltitudeM)
                        : Color.white;
                }
            }

            // V-58. Trees register instances and no overheads: a terrain tree is not a
            // GameObject we spawned and not a collider we attached - the TerrainCollider
            // generates those itself - so reporting either would be a guess.
            int[] placed = veg != null ? new int[lib.trees.Count] : null;

            List<TreeInstance> outList = new List<TreeInstance>(instances.Count);

            for (int i = 0; i < instances.Count; i++)
            {
                ScatterInstance inst = instances[i];

                int slot = inst.prototypeIndex;
                if (slot < 0 || slot >= slotMap.Length) continue;

                int protoIndex = slotMap[slot];
                if (protoIndex < 0 || protoIndex >= protos.Length) continue;

                Vector3 local = inst.position - tPos;

                float nx = local.x / tSize.x;
                float nz = local.z / tSize.z;
                if (nx < 0f || nx > 1f || nz < 0f || nz > 1f) continue;

                float ny = Mathf.Clamp01(local.y / tSize.y);

                TreePrototypeDef def = lib.trees[slot];
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

                // Per-instance tint is how the native tree renderer carries season.
                // lightmapColor stays white: it scales baked lighting, and darkening
                // it would put the season into the world's light rather than the leaf.
                t.color = seasonColour != null ? seasonColour[slot] : Color.white;
                t.lightmapColor = Color.white;

                outList.Add(t);

                if (placed != null) placed[slot]++;
            }

            // snapToHeightmap false: the Y already came from the graph's heightfield
            // plus any sculpt override, and re-snapping would re-sample a heightmap
            // that may not be synced yet on this pass.
            data.SetTreeInstances(outList.ToArray(), false);

            FlushRegistration(veg, instancingOwner, UCScatterKind.Tree, placed, 0, 0, 0);
        }

        /// <summary>
        /// Instance count per prototype for this chunk. Per prototype rather than per
        /// chunk because "dense" has to mean dense in one species: twenty prototypes of
        /// twenty instances each must not all batch on the strength of a crowd none of
        /// them is actually in.
        /// </summary>
        private static int[] CountByPrototype(List<ScatterInstance> instances, int protoCount)
        {
            int[] counts = new int[Mathf.Max(1, protoCount)];
            if (instances == null) return counts;

            for (int i = 0; i < instances.Count; i++)
            {
                int p = instances[i].prototypeIndex;
                if (p >= 0 && p < protoCount) counts[p]++;
            }

            return counts;
        }

        /// <summary>
        /// Hands one channel's COMMITTED tally to the vegetation registry (V-58).
        ///
        /// Why a per-prototype array is filled during the commit loop and flushed here
        /// rather than the loop calling RegisterInstances once per instance: the
        /// registry folds a repeated prototype by scanning that chunk's species list,
        /// so a per-instance call would be O(species) each time. A chunk carrying five
        /// thousand shrubs across twenty prototypes would spend a hundred thousand
        /// comparisons in bookkeeping alone, every stream-in, which on a phone is a
        /// stutter for a number nobody is looking at. One call per prototype makes it
        /// twenty. Overheads go the same way, accumulated as plain locals and reported
        /// once.
        ///
        /// Never confuse this with <see cref="CountByPrototype"/>. That one counts what
        /// the graph REQUESTED and exists to decide batching; this one carries what the
        /// applier actually COMMITTED, after out-of-range indices, null prefabs, thinned
        /// instances, exhausted budgets and failed instancing calls have all been walked
        /// past. V-52's bar is that the numbers match a profiler capture, and only the
        /// second of those two ever could.
        /// </summary>
        private static void FlushRegistration(UCVegetationSystemManager veg, int owner,
                                              UCScatterKind kind, int[] placed,
                                              int colliders, int gameObjects, int draws)
        {
            if (veg == null) return;

            if (placed != null)
            {
                for (int i = 0; i < placed.Length; i++)
                {
                    if (placed[i] <= 0) continue;
                    veg.RegisterInstances(owner, kind, i, placed[i]);
                }
            }

            veg.RegisterChunkOverheads(owner, colliders, gameObjects, draws);
        }

        /// <summary>
        /// Removes the colliders a prefab ships with, for the case where the policy says
        /// None but the instance still had to spawn as a GameObject. An author who set
        /// instancing to Never asked for a transform, not for collision the policy denies.
        /// </summary>
        private static void StripColliders(GameObject go)
        {
            Collider[] cols = go.GetComponentsInChildren<Collider>();
            if (cols == null) return;

            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null) continue;

                if (Application.isPlaying) Object.Destroy(cols[i]);
                else Object.DestroyImmediate(cols[i]);
            }
        }

        /// <summary>
        /// The order in which instances get first claim on a budget: fittest first,
        /// and emission order between equals.
        ///
        /// A budget has to drop something. Dropping the last-emitted means the
        /// retained set changes whenever the emission order does, so a rebuild
        /// silently moves colliders and triggers around a chunk the author did not
        /// touch. Ordering by suitability instead makes the drop mean something -
        /// the worst-sited instances go - and makes it reproducible.
        ///
        /// Returns null when there is nothing worth reordering, and the caller then
        /// walks the list directly rather than through an index array it would have
        /// had to allocate. (V-34)
        /// </summary>
        private static int[] SuitabilityOrder(List<ScatterInstance> instances)
        {
            if (instances == null || instances.Count < 2) return null;

            int n = instances.Count;

            // Unscored throughout means a graph that placed these geometrically -
            // river cobble, a wall of deadfall - and there is no fitness to sort by.
            // Emission order is then the honest order, and sorting would only
            // shuffle equals for nothing.
            bool anyScored = false;
            for (int i = 0; i < n; i++)
            {
                if (instances[i].suitability > 0f) { anyScored = true; break; }
            }
            if (!anyScored) return null;

            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;

            // The index is part of the key rather than left to the sort to preserve:
            // Array.Sort is unstable, and "some equally valid set" is not the same
            // promise as "the same set every rebuild". Comparing the index last makes
            // the ordering total, so the result cannot depend on the sort's internals.
            List<ScatterInstance> src = instances;
            System.Array.Sort(order, delegate (int a, int b)
            {
                float sa = src[a].suitability;
                float sb = src[b].suitability;
                if (sa > sb) return -1;
                if (sa < sb) return 1;
                return a - b;
            });

            return order;
        }

        /// <summary>
        /// Silences the shadow casters of a species that is not to cast at all, on a
        /// spawn that stayed a GameObject.
        ///
        /// Only the never-casts case is handled here. A distance-driven flip would
        /// mean testing every spawned instance against the camera every frame, which
        /// is the per-frame per-instance work § 6 is not allowed to do; the instanced
        /// path gets the distance test instead, once per batch, where it is cheap.
        /// A negative budget means nothing resolved one, and the prefab's own setting
        /// is then left alone rather than overridden from a value nobody authored.
        /// (V-32)
        /// </summary>
        private static void ApplyShadowPolicy(GameObject go, Ecology.EcoEnvelope eco,
                                              float shadowBudgetM)
        {
            if (go == null || eco == null || shadowBudgetM < 0f) return;
            if (eco.ResolveShadowDistanceM(shadowBudgetM) > 0f) return;

            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            if (rs == null) return;

            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                rs[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
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
        /// The collider budget, not the distance, decides which - and the budget is
        /// spent in suitability order, so the shrubs that keep their trigger are the
        /// ones best suited to where they stand rather than the ones the graph
        /// happened to emit first. That is what makes the retained set survive a
        /// rebuild unchanged. (V-34)
        /// </summary>
        private static void ApplyShrubs(List<ScatterInstance> instances,
                                        ScatterPrototypeLibrary lib, Transform parent,
                                        GPUInstancingManager instancing, int instancingOwner,
                                        List<GameObject> spawnedOut, float shadowBudgetM,
                                        UCVegetationSystemManager veg)
        {
            if (instances == null || instances.Count == 0) return;
            if (lib.shrubs.Count == 0) return;

            int budget = Mathf.Max(0, lib.maxShrubsPerChunk);
            int colliderBudget = Mathf.Max(0, lib.shrubColliderBudget);
            int[] counts = CountByPrototype(instances, lib.shrubs.Count);

            // Both budgets above are spent in this order, so the shrubs that survive
            // a tight chunk and the ones that keep their trigger are the best-sited
            // rather than the first-emitted. (V-34)
            int[] order = SuitabilityOrder(instances);
            int spawned = 0;

            // V-58. What this chunk COMMITTED, tallied as it commits. counts[] above
            // cannot serve: it counts what the graph asked for, and the loop below
            // walks past out-of-range indices, null prefabs, thinned instances and
            // failed instancing calls. Registering the request rather than the
            // commitment would put a number in the diagnostics panel that no amount
            // of profiling could reproduce.
            int[] placed = veg != null ? new int[lib.shrubs.Count] : null;
            int placedGameObjects = 0;
            int placedColliders = 0;
            int placedDraws = 0;

            for (int k = 0; k < instances.Count && spawned < budget; k++)
            {
                ScatterInstance inst = instances[order != null ? order[k] : k];

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

                // A collider can only exist on a GameObject, so both answers are resolved
                // together on the envelope - deciding them apart is how a species silently
                // loses its collision. The budget is what makes the trade tolerable: near
                // shrubs keep their trigger, the fill past it batches and keeps only the
                // shader-side wind.
                Ecology.ColliderPolicy collider;
                bool instanced;
                def.eco.ResolveRouting(counts[protoIndex], lib.gpuInstancingThreshold,
                                       spawned < colliderBudget, out collider, out instanced);

                if (instanced)
                {
                    if (instancing == null) continue;
                    if (!AddInstancedDraw(instancing, instancingOwner, def.prefab,
                                          pos, inst.rotation, inst.scale, parent,
                                          def.eco.ResolveShadowDistanceM(shadowBudgetM)))
                        continue;

                    placedDraws++;
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
                    shrub.Configure(def, scale, collider != Ecology.ColliderPolicy.None);

                    ApplyShadowPolicy(go, def.eco, shadowBudgetM);

                    if (spawnedOut != null) spawnedOut.Add(go);

                    placedGameObjects++;

                    // Counted from the policy that was actually resolved, not from the
                    // budget test above it: ResolveRouting can refuse a collider a
                    // species never wanted even while the budget is still open.
                    if (collider != Ecology.ColliderPolicy.None) placedColliders++;
                }

                if (placed != null) placed[protoIndex]++;

                spawned++;
            }

            FlushRegistration(veg, instancingOwner, UCScatterKind.Shrub, placed,
                              placedColliders, placedGameObjects, placedDraws);
        }

        private static int ApplyObjects(List<ScatterInstance> instances,
                                        ScatterPrototypeLibrary lib, Transform parent,
                                        GPUInstancingManager instancing, int instancingOwner,
                                        List<GameObject> spawnedOut, int budget,
                                        float shadowBudgetM,
                                        UCVegetationSystemManager veg)
        {
            if (lib.objects.Count == 0 || budget <= 0) return 0;

            int[] counts = CountByPrototype(instances, lib.objects.Count);

            // Fittest first, so a chunk that runs out of object budget drops the
            // worst-sited deposits rather than the last-emitted ones. (V-34)
            int[] order = SuitabilityOrder(instances);
            int used = 0;

            // V-58, as in ApplyShrubs: committed, not requested.
            int[] placed = veg != null ? new int[lib.objects.Count] : null;
            int placedGameObjects = 0;
            int placedColliders = 0;
            int placedDraws = 0;

            for (int k = 0; k < instances.Count && used < budget; k++)
            {
                ScatterInstance inst = instances[order != null ? order[k] : k];

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

                // Objects have no collider budget of their own - a boulder's collider is
                // not optional the way a shrub's trigger is - so the budget reads as open
                // here and the hierarchy threshold is what strips colliders. The count
                // budget above is the one V-34 orders by.
                Ecology.ColliderPolicy collider;
                bool instanced;
                def.eco.ResolveRouting(counts[protoIndex], lib.gpuInstancingThreshold,
                                       true, out collider, out instanced);

                if (instanced)
                {
                    if (instancing == null) continue;
                    if (!AddInstancedDraw(instancing, instancingOwner, def.prefab,
                                          pos, rot, inst.scale, parent,
                                          def.eco.ResolveShadowDistanceM(shadowBudgetM)))
                        continue;

                    placedDraws++;
                }
                else
                {
                    GameObject go = Object.Instantiate(def.prefab, pos, rot, parent);
                    go.transform.localScale = Vector3.Scale(def.prefab.transform.localScale,
                                                            inst.scale);
                    if (def.markStatic) go.isStatic = true;

                    // The prefab ships whatever colliders the artist put on it. A policy of
                    // None means this species is not to be collided with, so a spawn that
                    // had to stay a GameObject still has to shed them.
                    if (collider == Ecology.ColliderPolicy.None) StripColliders(go);

                    ApplyShadowPolicy(go, def.eco, shadowBudgetM);

                    if (spawnedOut != null) spawnedOut.Add(go);

                    placedGameObjects++;

                    // Counted after StripColliders has run, so the figure is what the
                    // object is left holding rather than what its prefab shipped with.
                    if (collider != Ecology.ColliderPolicy.None) placedColliders++;
                }

                if (placed != null) placed[protoIndex]++;

                used++;
            }

            FlushRegistration(veg, instancingOwner, UCScatterKind.Object, placed,
                              placedColliders, placedGameObjects, placedDraws);

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
                                             Transform parent,
                                             float shadowDistanceM = -1f)
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
                                       mr.shadowCastingMode, mr.receiveShadows, shadowDistanceM);
                any = true;
            }

            return any;
        }

        /// <summary>
        /// Destroys the GameObjects a chunk spawned and drops its registry entry.
        /// Trees and details are not listed here because the TerrainData owns those and
        /// overwriting it releases them - but they ARE registered, which is why the
        /// registry release below is unconditional. (V-58)
        /// </summary>
        public static void Release(List<GameObject> spawned,
                                   UCVegetationSystemManager veg, int owner)
        {
            // Before the early return, not after. A chunk that placed only trees and
            // grass has an empty spawned list and a full registry entry, and returning
            // early would leave that entry behind for a chunk that no longer exists -
            // which is precisely the leak V-58's bar forbids.
            if (veg != null) veg.ReleaseChunk(owner);

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
