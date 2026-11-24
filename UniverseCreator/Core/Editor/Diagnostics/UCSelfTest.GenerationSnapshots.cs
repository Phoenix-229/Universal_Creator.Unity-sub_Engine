using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Biome;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Geology;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static TileData SnapshotTile(Graph graph, Dictionary<string, RetainedProduct> retained = null)
        {
            TileData tile = new TileData(default(Coord), new CoordRect(new Coord(0, 0), new Coord(9, 9)),
                Vector3.zero, new Vector3(10f, 100f, 10f), 100f);
            tile.retained = retained;
            tile.Bind(new GraphEvaluator(graph));
            return tile;
        }

        private static Matrix SnapshotPull(Graph graph, string id, int port = 0,
                                           Dictionary<string, RetainedProduct> retained = null)
        {
            TileData tile = SnapshotTile(graph, retained);
            return (Matrix)new GraphEvaluator(graph).PullNode(graph.GetNodeById(id), port, tile, null);
        }

        private static void TestGenerationSnapshots()
        {
            TestSnapshotAuthoringIsolation();
            TestSnapshotPushedInputIsolation();
            TestPreparedCourseInvalidation();
            TestSnapshotWorldAndEcologyIsolation();
            TestSnapshotManagerOwnership(); // UC-T01-06/07: nested managers/oceans remain with their nearest world.
            TestSnapshotLithologyOwnership(); // UC-T01-06/07: native profile copies, retained results and final-consumer release.
            TestSnapshotNativeWorkerPaths();
            TestSnapshotWorkerRetirement();
            TestCompiledGraphPlans(); // UC-T01-08: compare indexed execution and measure compilation/query costs.
        }

        /// <summary>UC-T01-06/07: runs disk scene save/unload/reload acceptance in an isolated batch process.</summary>
        /// <remarks>
        /// Invoke with -executeMethod UniverseCreator.Editor.Diagnostics.UCSelfTest.RunSnapshotSceneReloadBatch.
        /// Preserves a saved startup scene using additive loading; an untitled startup scene must be empty.
        /// Creates and removes only its temporary fixture assets and exits with zero on success, one on failure.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunSnapshotSceneReloadBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Scene reload acceptance requires an isolated batch process.");
            checks = 0; failures.Clear();
            try { TestSnapshotSceneReload(); TestChunkRestoreSceneReload(); }
            catch (Exception ex) { Check(false, "UC-T01-06 native scene reload exception: " + ex); }
            bool ok = failures.Count == 0;
            Debug.Log(Report(ok));
            Console.WriteLine(Report(ok));
            EditorApplication.Exit(ok ? 0 : 1);
        }

        // UC-T01-06/07: native scene saves require a normal Editor scene, not a preview or runtime scene.
        private static void TestSnapshotSceneReload()
        {
            Scene originalScene = SceneManager.GetActiveScene();
            bool single = string.IsNullOrEmpty(originalScene.path);
            if (!originalScene.IsValid() || SceneManager.sceneCount != 1 ||
                (single && originalScene.GetRootGameObjects().Length != 0))
                throw new InvalidOperationException("Start the isolated reload gate with one saved scene or an empty untitled scene.");
            Scene fixture = default(Scene);
            string folder = "Assets/UCSnapshotReload_" + Guid.NewGuid().ToString("N");
            Graph graph = null;
            GenerationSnapshot captured = null, reloaded = null;
            TileData consumer = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                // UC-T01-06: only a verified empty startup scene may be replaced; saved scenes stay loaded.
                fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                    single ? NewSceneMode.Single : NewSceneMode.Additive);
                // NewScene may unload unreferenced assets, so create the source after the scene switch.
                graph = ScriptableObject.CreateInstance<Graph>();
                graph.nodes.Add(new ConstantNode { id = "source", value = 0.37f });
                // UC-T01-06/07: a real saved asset must preserve the old captured values across scene reload.
                LithologyProfile profile = ScriptableObject.CreateInstance<LithologyProfile>();
                profile.PopulateExampleColumn();
                foreach (LithoUnit unit in profile.units) unit.erodibilityKlsu = 1f;
                AssetDatabase.CreateAsset(profile, folder + "/Lithology.asset");
                graph.nodes.Add(new LithologyOutput { id = "lithology", profile = profile });
                graph.nodes.Add(new HeightOutputNode { id = "height" });
                graph.connections.Add(new Connection("source", 0, "lithology", 0));
                AssetDatabase.CreateAsset(graph, folder + "/Graph.asset");
                GameObject root = new GameObject("UC snapshot reload world");
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, fixture);
                UniverseCreatorObject world = root.AddComponent<UniverseCreatorObject>();
                world.graph = graph;
                UCVegetationSystemManager veg = root.AddComponent<UCVegetationSystemManager>();
                veg.useEcology = true;
                veg.suitabilityThreshold = 0.23f;
                captured = GenerationSnapshot.Capture(graph, world);
                Graph copy = captured.Graph;
                LithologyProfile copiedProfile = ((ILithologySnapshotInput)copy.GetNodeById("lithology")).CapturedProfile;
                consumer = SnapshotTile(copy);
                consumer.RetainGenerationInputs(captured);
                captured.Release();

                // The disk scene records the edit; the already captured bake must retain the old value.
                veg.suitabilityThreshold = 0.61f;
                foreach (LithoUnit unit in profile.units) unit.erodibilityKlsu = 3f;
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssetIfDirty(profile);
                Check(EditorSceneManager.SaveScene(fixture, folder + "/World.unity"),
                    "UC-T01-06 native scene fixture saves its edited manager settings.");
                bool closed = single
                    ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single).IsValid()
                    : EditorSceneManager.CloseScene(fixture, true);
                Check(closed && world == null,
                    "UC-T01-06 closing the authoring scene succeeds while a captured tile remains live.");
                fixture = default(Scene);
                Check(world == null && !captured.IsReleased && copy != null &&
                      Mathf.Abs(captured.Ecology.settings.suitabilityThreshold - 0.23f) < 1e-6f,
                    "UC-T01-06 scene unload does not retire inputs held by the final tile.");
                Check(Mathf.Abs(SnapshotPull(copy, "source").arr[0] - 0.37f) < 1e-6f,
                    "UC-T01-06 captured inputs remain evaluable after the owning scene unloads.");
                Check(copiedProfile != null && SnapshotPull(copy, "lithology", 3).arr[0] == 1f,
                    "UC-T01-06 native scene unload retains the copied geology values despite saved asset edits.");
                fixture = EditorSceneManager.OpenScene(folder + "/World.unity",
                    single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                UniverseCreatorObject restored = fixture.GetRootGameObjects()[0].GetComponent<UniverseCreatorObject>();
                reloaded = GenerationSnapshot.Capture(restored.graph, restored);
                Check(Mathf.Abs(reloaded.Ecology.settings.suitabilityThreshold - 0.61f) < 1e-6f &&
                      Mathf.Abs(captured.Ecology.settings.suitabilityThreshold - 0.23f) < 1e-6f,
                    "UC-T01-07 scene reload captures saved edits without replacing the previous bake's settings.");
                consumer.ReleaseGenerationInputs();
                Check(SnapshotPull(reloaded.Graph, "lithology", 3).arr[0] == 3f,
                    "UC-T01-07 native scene reload captures the saved lithology edit on the replacement run.");
                Check(captured.IsReleased && copy == null,
                    "UC-T01-06 final tile release destroys the copied native graph after scene reload.");
                Check(copiedProfile == null,
                    "UC-T01-06 native scene reload releases copied geology with the final tile.");
            }
            finally
            {
                if (consumer != null) consumer.ReleaseGenerationInputs();
                if (captured != null) captured.Release();
                if (reloaded != null) reloaded.Release();
                if (fixture.IsValid() && fixture.isLoaded)
                {
                    if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    else EditorSceneManager.CloseScene(fixture, true);
                }
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (graph != null && !AssetDatabase.Contains(graph)) UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        // A deterministic main-thread pump verifies Post ownership without invoking private Unity APIs.
        private sealed class SnapshotRetirementContext : SynchronizationContext
        {
            private readonly Queue<Action> pending = new Queue<Action>();
            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (pending) pending.Enqueue(() => callback(state));
            }
            internal void Drain()
            {
                while (true)
                {
                    Action callback;
                    lock (pending)
                    {
                        if (pending.Count == 0) return;
                        callback = pending.Dequeue();
                    }
                    callback();
                }
            }
        }

        private static void TestSnapshotWorkerRetirement()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            GenerationSnapshot snapshot = null;
            GenerationSnapshot.Lease worker = null;
            SynchronizationContext original = SynchronizationContext.Current;
            SnapshotRetirementContext retirement = new SnapshotRetirementContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(retirement);
                snapshot = GenerationSnapshot.Capture(graph);
                Graph copy = snapshot.Graph;
                worker = snapshot.Acquire();
                snapshot.Release();
                Task release = Task.Run(() => worker.Dispose());
                Check(release.Wait(5000), "UC-T01-06 final worker releases its lease without blocking on Unity.");
                Check(snapshot.IsReleased && copy != null,
                    "UC-T01-06 final worker retirement queues native destruction on the captured context.");
                retirement.Drain();
                Check(copy == null, "UC-T01-06 the main-thread retirement pump destroys the copied native graph.");
            }
            finally
            {
                if (worker != null) worker.Dispose();
                if (snapshot != null) snapshot.Release();
                retirement.Drain();
                SynchronizationContext.SetSynchronizationContext(original);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestSnapshotAuthoringIsolation()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            BiomeSplatPalette palette = ScriptableObject.CreateInstance<BiomeSplatPalette>();
            GenerationSnapshot first = null, second = null;
            GenerationSnapshot.Lease lease = null;
            TileData consumer = null;
            Task<float[]> work = null;
            using (ManualResetEventSlim go = new ManualResetEventSlim(false))
            try
            {
                texture.SetPixels(new[] { Color.gray, Color.gray, Color.gray, Color.gray });
                texture.Apply(false, false);
                ConstantNode source = new ConstantNode { id = "source", value = 0.25f };
                CurveNode curve = new CurveNode { id = "curve" };
                ImportHeightmapNode raster = new ImportHeightmapNode { id = "raster", heightmap = texture };
                BiomeSplatOutputNode splat = new BiomeSplatOutputNode { id = "splat", palette = palette };
                palette.rules.Add(BiomeSplatRule.Default(BiomeType.Desert, 3));
                graph.nodes.AddRange(new Generator[] { source, curve, raster, splat });
                graph.connections.Add(new Connection(source.id, 0, curve.id, 0));
                first = GenerationSnapshot.Capture(graph);
                ulong firstRasterHash = first.Graph.GetNodeById(raster.id).contentHash;
                ulong firstSplatHash = first.Graph.GetNodeById(splat.id).contentHash;
                string preparedPalette = JsonUtility.ToJson(((IPreparedGraphInputs)first.Graph.GetNodeById(splat.id)).GetPreparedInputsForHash());
                Check(!ReferenceEquals(first.Graph, graph) && !ReferenceEquals(first.Graph.GetNodeById(source.id), source),
                    "UC-T01-06 graph and polymorphic nodes are detached with unchanged IDs.");
                Check(first.Graph.compiledPlan != null && graph.compiledPlan == null,
                    "UC-T01-08 capture attaches indexes only to the copied revision.");
                Check(source.contentHash == 0UL,
                    "UC-T01-06 capture does not prime or hash the live authoring node.");

                lease = first.Acquire();
                consumer = SnapshotTile(first.Graph);
                consumer.RetainGenerationInputs(first);
                work = Task.Run(() =>
                {
                    if (!go.Wait(5000)) throw new TimeoutException("Snapshot edit barrier timed out.");
                    return new[] { SnapshotPull(first.Graph, source.id).arr[0],
                        SnapshotPull(first.Graph, curve.id).arr[0], SnapshotPull(first.Graph, raster.id).arr[0] };
                });
                source.value = 0.9f;
                curve.curve.keys = AnimationCurve.Linear(0f, 0.8f, 1f, 0.8f).keys;
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply(false, false);
                BiomeSplatRule edited = palette.rules[0];
                edited.groundLayer = 7;
                palette.rules[0] = edited;
                graph.connections.Clear();
                second = GenerationSnapshot.Capture(graph);
                Check(!ReferenceEquals(first.Graph.compiledPlan, second.Graph.compiledPlan) &&
                      first.Graph.GetInputConnection(curve.id, 0).sourceNodeId == source.id &&
                      string.IsNullOrEmpty(second.Graph.GetInputConnection(curve.id, 0).sourceNodeId),
                    "UC-T01-08 a replacement capture rebuilds indexes while the previous worker retains its wires.");
                first.Release();
                go.Set();
                Check(work.Wait(5000), "UC-T01-06 captured worker finishes after live edits and owner release.");
                float[] values = work.GetAwaiter().GetResult();
                Check(Mathf.Abs(values[0] - 0.25f) < 1e-6f && Mathf.Abs(values[1] - 0.25f) < 0.005f && Mathf.Abs(values[2] - 0.5f) < 0.005f,
                    "UC-T01-06 worker retains captured scalar, curve, topology and raster samples.");
                Check(Mathf.Abs(SnapshotPull(second.Graph, source.id).arr[0] - 0.9f) < 1e-6f,
                    "UC-T01-06 replacement snapshot sees newly authored values.");
                Check(firstRasterHash != second.Graph.GetNodeById(raster.id).contentHash &&
                      firstSplatHash != second.Graph.GetNodeById(splat.id).contentHash,
                    "UC-T01-07 repaint and same-palette edits change their actual dependencies.");
                Check(preparedPalette == JsonUtility.ToJson(((IPreparedGraphInputs)first.Graph.GetNodeById(splat.id)).GetPreparedInputsForHash()),
                    "UC-T01-06 palette rules remain detached while the asset changes in place.");
                StopToken cancel = new StopToken();
                cancel.RequestStop();
                try { new GraphEvaluator(second.Graph).Evaluate(SnapshotTile(second.Graph), cancel); }
                catch (OperationCanceledException) { }
                second.Release();
                lease.Dispose();
                Check(!first.IsReleased && second.IsReleased,
                    "UC-T01-06 retiring a cancelled replacement does not retire another snapshot's final tile.");
                Check(Mathf.Abs(((Matrix)consumer.PullInput(first.Graph.GetNodeById(curve.id), 0, null)).arr[0] - 0.25f) < 1e-6f,
                    "UC-T01-06 a tile can still pull captured inputs after owner and worker release.");
                consumer.ReleaseGenerationInputs();
                Check(first.IsReleased, "UC-T01-06 final tile release retires copied graph data.");
                bool refused = false;
                try { first.Acquire(); }
                catch (ObjectDisposedException) { refused = true; }
                Check(refused, "UC-T01-06 a released snapshot cannot be resurrected.");
            }
            finally
            {
                go.Set();
                if (work != null && !work.IsCompleted) work.Wait(5000);
                if (consumer != null) consumer.ReleaseGenerationInputs();
                if (lease != null) lease.Dispose();
                if (first != null) first.Release();
                if (second != null) second.Release();
                UnityEngine.Object.DestroyImmediate(graph);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(palette);
            }
        }

        private static void TestSnapshotPushedInputIsolation()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            GenerationSnapshot snapshot = null;
            try
            {
                var lake = new HeightOutputNode.LakeFootprint
                {
                    ringXZ = new[] { Vector2.zero, Vector2.right, Vector2.one },
                    planBounds = new Bounds(Vector3.zero, Vector3.one)
                };
                var region = new BiomeRegionFootprint { centerXZ = Vector2.zero, halfExtentsXZ = Vector2.one, biome = 5 };
                HeightOutputNode height = new HeightOutputNode { id = "height", enforceContinentalRise = false, enforceSeaLevelFloor = false, enforceCoastalShelf = false };
                BiomeClassifierNode biome = new BiomeClassifierNode { id = "biome" };
                height.SetLakeFootprints(new List<HeightOutputNode.LakeFootprint> { lake });
                biome.SetRegionOverrides(new List<BiomeRegionFootprint> { region });
                graph.nodes.Add(height);
                graph.nodes.Add(biome);
                snapshot = GenerationSnapshot.Capture(graph);
                lake.ringXZ[1] = new Vector2(50f, 50f);
                height.lakeFootprints.Clear();
                biome.regionOverrides.Clear();
                HeightOutputNode copy = (HeightOutputNode)snapshot.Graph.GetNodeById(height.id);
                Check(copy.lakeFootprints.Count == 1 && copy.lakeFootprints[0].ringXZ[1] == Vector2.right,
                    "UC-T01-06 cloned graphs retain detached nonserialized lake lists and vertices.");
                Check(!copy.enforceContinentalRise && !copy.enforceSeaLevelFloor && !copy.enforceCoastalShelf,
                    "UC-T01-06 cloned graphs preserve nonserialized height guard settings.");
                Check(((BiomeClassifierNode)snapshot.Graph.GetNodeById(biome.id)).regionOverrides.Count == 1,
                    "UC-T01-06 removing a live region does not remove a captured region.");
            }
            finally { if (snapshot != null) snapshot.Release(); UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestPreparedCourseInvalidation()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ConstantNode source = new ConstantNode { id = "ground", value = 0.5f };
                RiverCourseInputNode node = new RiverCourseInputNode { id = "manual" };
                var course = new RiverCourseInputNode.ManualRiverCourse
                {
                    bodyId = 123,
                    pointsXZ = new[] { new Vector2(0f, 5f), new Vector2(5f, 5f), new Vector2(10f, 5f) },
                    halfWidthM = new[] { 1f, 1f, 1f }, bedY = new[] { 10f, 9f, 8f }, depthM = new[] { 2f, 2f, 2f }
                };
                var courses = new List<RiverCourseInputNode.ManualRiverCourse> { course };
                graph.nodes.Add(source); graph.nodes.Add(node);
                graph.connections.Add(new Connection(source.id, 0, node.id, 0));
                node.SetManualCourses(courses);
                GraphContentHasher.ComputeHashes(graph);
                ulong sourceHash = source.contentHash;
                var retained = new Dictionary<string, RetainedProduct>();
                Matrix unchanged = SnapshotPull(graph, node.id, 1, retained);
                node.SetManualCourses(courses);
                GraphContentHasher.ComputeHashes(graph);
                Check(ReferenceEquals(unchanged, SnapshotPull(graph, node.id, 1, retained)),
                    "UC-T01-07 unchanged complete course data reuses the retained river mask.");
                Action[] edits = {
                    () => course.pointsXZ[1].y += 0.01f,
                    () => course.pointsXZ[2].x += 0.02f,
                    () => course.halfWidthM[1] += 0.7f,
                    () => course.bedY[1] += 0.01f,
                    () => course.depthM[1] += 0.2f,
                    () => course.bodyId++,
                    () => courses.Clear()
                };
                string[] names = { "point 1", "point 2", "width", "bed", "depth", "source identity", "removal" };
                for (int e = 0; e < edits.Length; e++)
                {
                    ulong before = node.contentHash;
                    ulong preparedBefore = node.GetPreparedInputHash();
                    edits[e]();
                    Check(node.GetPreparedInputHash() == preparedBefore,
                        "UC-T01-06 supplied course " + names[e] + " edits cannot mutate captured arrays.");
                    node.SetManualCourses(courses);
                    GraphContentHasher.ComputeHashes(graph);
                    Check(node.contentHash != before && source.contentHash == sourceHash,
                        "UC-T01-07 course " + names[e] + " edit invalidates its branch without invalidating ground.");
                    for (int port = 0; port < 2; port++)
                    {
                        Matrix warm = SnapshotPull(graph, node.id, port, retained);
                        Matrix cold = SnapshotPull(graph, node.id, port);
                        bool equal = warm.arr.Length == cold.arr.Length;
                        for (int i = 0; i < warm.arr.Length && equal; i++) equal &= Mathf.Abs(warm.arr[i] - cold.arr[i]) < 1e-6f;
                        Check(equal, "UC-T01-07 course " + names[e] + " retained/cold port " + port + " agree.");
                    }
                }
                Check(!node.HasCourses && SnapshotPull(graph, node.id, 1).arr[0] == 0f,
                    "UC-T01-07 explicit course removal emits an empty river mask.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestSnapshotWorldAndEcologyIsolation()
        {
            GameObject rootA = new GameObject("UC snapshot world A");
            GameObject rootB = new GameObject("UC snapshot world B");
            rootA.SetActive(false); rootB.SetActive(false);
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            ScatterPrototypeLibrary library = new ScatterPrototypeLibrary();
            GenerationSnapshot first = null, second = null, edited = null, settingsEdited = null;
            const int publishedId = -20261114;
            try
            {
                UniverseCreatorObject worldA = rootA.AddComponent<UniverseCreatorObject>();
                UniverseCreatorObject worldB = rootB.AddComponent<UniverseCreatorObject>();
                worldA.enabled = false; worldB.enabled = false;
                worldA.graph = graph; worldB.graph = graph;
                worldA.scatterLibrary = library;
                UCVegetationSystemManager veg = rootA.AddComponent<UCVegetationSystemManager>();
                veg.enabled = false;
                veg.useEcology = true;
                rootB.transform.SetParent(rootA.transform, false);
                GameObject lakeA = new GameObject("A lake"); lakeA.transform.SetParent(rootA.transform, false);
                GameObject lakeB = new GameObject("B lake"); lakeB.transform.SetParent(rootB.transform, false);
                UCWaterBody bodyA = lakeA.AddComponent<UCWaterBody>(); bodyA.buildTrigger = UCWaterBody.BuildTrigger.Manual;
                UCWaterBody bodyB = lakeB.AddComponent<UCWaterBody>(); bodyB.buildTrigger = UCWaterBody.BuildTrigger.Manual;
                bodyA.sizeMetres = new Vector2(8f, 6f); bodyB.sizeMetres = new Vector2(4f, 2f);
                UCBiomeRegion regionA = lakeA.AddComponent<UCBiomeRegion>(); regionA.biome = BiomeType.Desert;
                UCBiomeRegion regionB = lakeB.AddComponent<UCBiomeRegion>(); regionB.biome = BiomeType.Ice;
                rootB.SetActive(true); rootA.SetActive(true);
                var envelope = new UniverseCreator.Ecology.EcoEnvelope { weight = 0.75f, perBiomeWeight = new[] { 0.2f, 0.4f } };
                library.trees.Add(new TreePrototypeDef { eco = envelope, weight = 0.8f });
                graph.nodes.Add(new ConstantNode { id = "source", value = 0.4f });
                graph.nodes.Add(new BiomeDensityTableNode { id = "density" });
                graph.nodes.Add(new RiverCourseInputNode { id = "river" });
                graph.nodes.Add(new HeightOutputNode { id = "height" });
                graph.nodes.Add(new BiomeClassifierNode { id = "biome" });
                ((RiverCourseInputNode)graph.nodes[2]).SetManualCourses(new List<RiverCourseInputNode.ManualRiverCourse>
                    { new RiverCourseInputNode.ManualRiverCourse { pointsXZ = new[] { Vector2.zero, Vector2.one } } });
                RiverCourseInputNode.PublishBodyCourse(publishedId, new RiverCourseInputNode.ManualRiverCourse
                    { pointsXZ = new[] { Vector2.zero, Vector2.one }, halfWidthM = new[] { 1f, 1f } });
                first = GenerationSnapshot.Capture(graph, worldA);
                second = GenerationSnapshot.Capture(graph, worldB);
                var lakesA = ((HeightOutputNode)first.Graph.GetNodeById("height")).lakeFootprints;
                var lakesB = ((HeightOutputNode)second.Graph.GetNodeById("height")).lakeFootprints;
                Check(lakesA != null && lakesB != null && lakesA.Count == 1 && lakesB.Count == 1 &&
                      Mathf.Abs(lakesA[0].planBounds.size.x - 8f) < 1e-5f && Mathf.Abs(lakesB[0].planBounds.size.x - 4f) < 1e-5f,
                    "UC-T01-06 overlapping/nested worlds capture only their own lake footprints.");
                var regionsA = ((BiomeClassifierNode)first.Graph.GetNodeById("biome")).regionOverrides;
                var regionsB = ((BiomeClassifierNode)second.Graph.GetNodeById("biome")).regionOverrides;
                Check(regionsA != null && regionsB != null && regionsA.Count == 1 && regionsB.Count == 1 &&
                      regionsA[0].biome == (int)BiomeType.Desert && regionsB[0].biome == (int)BiomeType.Ice,
                    "UC-T01-06 overlapping/nested worlds capture only their own biome regions.");
                ulong unchangedSource = first.Graph.GetNodeById("source").contentHash;
                ulong oldDensity = first.Graph.GetNodeById("density").contentHash;
                envelope.perBiomeWeight[1] = 0.9f;
                envelope.weight = 0.2f;
                worldA.maxHeightMeters += 30f;
                Check(Mathf.Abs(first.Ecology.trees.envelopes[0].perBiomeWeight[1] - 0.4f) < 1e-6f &&
                      Mathf.Abs(first.Ecology.trees.envelopes[0].weight - 0.75f) < 1e-6f,
                    "UC-T01-06 ecology entries and nested affinity arrays are detached from the live library.");
                Check(first.Settings.maxHeightMeters != worldA.maxHeightMeters,
                    "UC-T01-06 captured world height does not track later manager edits.");
                Check(second.Ecology == null && !((RiverCourseInputNode)first.Graph.GetNodeById("river")).HasCourses &&
                      !((RiverCourseInputNode)second.Graph.GetNodeById("river")).HasCourses,
                    "UC-T01-06 overlapping worlds do not consume another world's ecology or global river fallback.");
                worldA.maxHeightMeters = first.Settings.maxHeightMeters;
                edited = GenerationSnapshot.Capture(graph, worldA);
                Check(edited.Graph.GetNodeById("source").contentHash == unchangedSource &&
                      edited.Graph.GetNodeById("density").contentHash != oldDensity,
                    "UC-T01-07 profile edits invalidate ecology consumers while unrelated terrain stays reusable.");
                ulong beforeSettings = edited.Graph.GetNodeById("density").contentHash;
                float capturedThreshold = edited.Ecology.settings.suitabilityThreshold;
                veg.suitabilityThreshold += 0.1f;
                settingsEdited = GenerationSnapshot.Capture(graph, worldA);
                Check(settingsEdited.Graph.GetNodeById("source").contentHash == unchangedSource &&
                      settingsEdited.Graph.GetNodeById("density").contentHash != beforeSettings,
                    "UC-T01-07 manager setting edits invalidate ecology consumers without invalidating terrain.");
                Check(edited.Ecology.settings.suitabilityThreshold == capturedThreshold &&
                      Mathf.Abs(settingsEdited.Ecology.settings.suitabilityThreshold - capturedThreshold - 0.1f) < 1e-6f,
                    "UC-T01-06 copied manager settings remain fixed while replacement captures the edit.");
            }
            finally
            {
                if (first != null) first.Release(); if (second != null) second.Release(); if (edited != null) edited.Release();
                if (settingsEdited != null) settingsEdited.Release();
                RiverCourseInputNode.UnpublishBodyCourse(publishedId);
                UnityEngine.Object.DestroyImmediate(rootA); if (rootB != null) UnityEngine.Object.DestroyImmediate(rootB);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        [Serializable]
        private sealed class SnapshotBarrierNode : CreatorNode
        {
            public float value = 0.31f;
            internal static ManualResetEventSlim gate;
            internal static CountdownEvent started;
            public override string NodeName { get { return "UC snapshot barrier fixture"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                started.Signal();
                if (!gate.Wait(5000)) throw new TimeoutException("Native worker snapshot barrier timed out.");
                if (stop != null && stop.StopRequested) return;
                matrix.Fill(value);
            }
        }

        private static void TestSnapshotNativeWorkerPaths()
        {
            GameObject rootA = new GameObject("UC async snapshot A"); rootA.SetActive(false);
            GameObject rootB = new GameObject("UC async snapshot B"); rootB.SetActive(false);
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            using (ManualResetEventSlim gate = new ManualResetEventSlim(false))
            using (CountdownEvent started = new CountdownEvent(2))
            try
            {
                SnapshotBarrierNode.gate = gate; SnapshotBarrierNode.started = started;
                UniverseCreatorObject first = rootA.AddComponent<UniverseCreatorObject>();
                UniverseCreatorObject second = rootB.AddComponent<UniverseCreatorObject>();
                first.graph = graph; second.graph = graph;
                first.resolution = second.resolution = 33;
                SnapshotBarrierNode source = new SnapshotBarrierNode { id = "source", value = 0.31f };
                HeightOutputNode height = new HeightOutputNode { id = "height", enforceSeaLevelFloor = false,
                    enforceContinentalRise = false, enforceCoastalShelf = false };
                graph.nodes.Add(source); graph.nodes.Add(height);
                graph.connections.Add(new Connection(source.id, 0, height.id, 0));
                bool firstStarted = first.RegenerateAsync();
                bool secondStarted = second.RegenerateAsync();
                Check(firstStarted && secondStarted,
                    "UC-T01-06 actual world worker paths capture two runs from one graph.");
                if (!firstStarted || !secondStarted) return;
                Check(started.Wait(5000), "UC-T01-06 both actual workers reach the edit barrier.");
                source.value = 0.93f;
                graph.connections.Clear();
                first.CancelGeneration();
                gate.Set();
                Check(Task.WaitAll(new[] { first.CurrentTask, second.CurrentTask }, 5000),
                    "UC-T01-06 actual cancelled/surviving workers both exit.");
                Check(first.LastTile == null && second.LastTile != null,
                    "UC-T01-06 only the surviving world publishes its captured tile.");
                TileData surviving = second.LastTile;
                Check(surviving != null && surviving.outputs.heightmap != null &&
                      Mathf.Abs(surviving.outputs.heightmap[0, 0] - 0.31f) < 1e-6f,
                    "UC-T01-06 actual survivor preserves captured node values and wires after live edits.");
                if (surviving != null) surviving.ReleaseGenerationInputs();
            }
            finally
            {
                gate.Set();
                UniverseCreatorObject first = rootA.GetComponent<UniverseCreatorObject>();
                UniverseCreatorObject second = rootB.GetComponent<UniverseCreatorObject>();
                if (first != null) { first.CancelGeneration(); if (first.CurrentTask != null) first.CurrentTask.Wait(5000); }
                if (second != null) { second.CancelGeneration(); if (second.CurrentTask != null) second.CurrentTask.Wait(5000); }
                SnapshotBarrierNode.gate = null; SnapshotBarrierNode.started = null;
                UnityEngine.Object.DestroyImmediate(rootA); UnityEngine.Object.DestroyImmediate(rootB);
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }
    }
}
