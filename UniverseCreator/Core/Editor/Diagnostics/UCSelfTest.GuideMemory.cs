// UC repair 29: exact guide inputs, actual climate preparation and bounded worker evaluation.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable] private sealed class GuideRecipes { public GuideWorld[] worlds; }
        [Serializable] private sealed class GuideWorld { public string version; public GuideGraph[] graphs; }
        [Serializable] private sealed class GuideGraph { public string domain; public GuideNode[] nodes; public GuideWire[] wires; }
        [Serializable] private sealed class GuideNode { public string id, name; public GuideSetting[] settings; }
        [Serializable] private sealed class GuideSetting { public string name, value; }
        [Serializable] private sealed class GuideWire { public string source, output, target, input; }

        /// <summary>Evaluates the exact Green Coast guide at 513 height samples / 512 river cells and a hard 256 MiB payload budget.</summary>
        /// <remarks>Isolated batch entry only. Awaits existing scheduler workers, so Unity's main thread remains free.
        /// Tests two cells of the 6 km, six-by-six world, not full native world rendering or device performance.
        /// Prints fingerprints, elapsed time and admitted peaks; exits nonzero on a missing output, budget refusal or exception.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static async void RunGuideMemoryBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Guide memory acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            GameObject root = null;
            var graphs = new List<Graph>();
            GenerationSnapshot snapshot = null;
            WorldDrainageCache drainage = null;
            var memory = new ProductMemoryBudget(256L * 1024 * 1024);
            try
            {
                // UC repair 29: prove surviving payloads and cancellation cleanup before the full-detail workload.
                TestCompletedDrainageViewMemory();
                TestCompletedHaloViewMemory();
                string path = Path.Combine(Application.dataPath, "UniverseCreator/Core/Editor/Diagnostics/Fixtures/guide_worlds.json");
                var recipe = JsonUtility.FromJson<GuideRecipes>(File.ReadAllText(path)).worlds.Single(w => w.version == "V5.5");
                foreach (var definition in recipe.graphs) graphs.Add(BuildGuideGraph(definition));
                Check(graphs.Count == 4, "UC guide: all four actual guide graphs construct with verified fields and named ports.");
                root = new GameObject("UC full-detail guide memory"); root.SetActive(false);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = graphs.Single(g => g.Domain == UCGraphDomain.TerrainWater);
                world.worldSizeKm = 6; world.chunkCountPerSide = 6; world.resolution = 513; world.maxHeightMeters = 900;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = graphs.Single(g => g.Domain == UCGraphDomain.LightingClimateWeather);
                await bindings.PrepareTerrainClimateAsync();
                Check(bindings.CaptureClimateBaseline() != null, "UC guide: connected early climate is accepted before terrain input capture.");
                snapshot = GenerationSnapshot.Capture(world.graph, world, true);
                var run = world.GenerationScheduler.CurrentRun;
                drainage = new WorldDrainageCache(world.GenerationScheduler.CreateStopToken(run), memory);
                foreach (int cell in new[] { 0, 3 })
                {
                    var tile = new TileData(new Coord(cell, cell), new CoordRect(0, 0, 513, 513),
                        new Vector3(-3000 + cell * 1000, 0, -3000 + cell * 1000), new Vector3(1000, 900, 1000), 900);
                    tile.SetFullWorld(new Vector3(-3000, 0, -3000), new Vector3(6000, 900, 6000));
                    tile.AttachMemory(memory); tile.AssignRun(run); tile.drainageCache = drainage;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await world.GenerationScheduler.RunWorkerAsync(run, 1, new StopToken(), () =>
                        {
                            var evaluator = new GraphEvaluator(snapshot.Graph); tile.Bind(evaluator);
                            evaluator.PullNode(snapshot.Graph.GetNodeById("heightOut"), 0, tile, new StopToken());
                            ulong before = GuideHeightFingerprint(tile.outputs.heightmap);
                            evaluator.PullNode(snapshot.Graph.GetNodeById("splatOut"), 0, tile, new StopToken());
                            tile.FinishPublications(); tile.TrackProductMemory(tile.outputs, "guide-outputs");
                            Check(before == GuideHeightFingerprint(tile.outputs.heightmap), "UC guide: Biome Splat Output preserves Height Output at cell " + cell);
                            Check(tile.outputs.heightmap.GetLength(0) == 513 && tile.outputs.splatmap != null,
                                "UC guide: full-resolution height and splat outputs exist at cell " + cell);
                            Console.WriteLine("UC_GUIDE_CELL " + cell + " height=" + before.ToString("X16") + " elapsedMs=" + clock.ElapsedMilliseconds +
                                " resident=" + (memory.ManagedBytes + memory.NativeBytes) + " peak=" + memory.PeakBytes + " solves=" + drainage.SolveCount);
                        });
                    }
                    finally
                    {
                        tile.ReleaseProductMemory();
                        Console.WriteLine("UC_GUIDE_RETIRE cell=" + cell + " elapsedMs=" + clock.ElapsedMilliseconds + " resident=" +
                            (memory.ManagedBytes + memory.NativeBytes) + " peak=" + memory.PeakBytes + " limit=" + memory.LimitBytes);
                    }
                }
                Check(memory.PeakBytes <= memory.LimitBytes, "UC guide: exact quality baseline honors the unchanged 256 MiB admission limit.");
            }
            catch (Exception error) { Check(false, "UC guide memory exception: " + error); }
            finally
            {
                drainage?.Dispose(); snapshot?.Release();
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (Graph graph in graphs) UnityEngine.Object.DestroyImmediate(graph);
                memory.ClearRetained();
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC guide: all fixture payload leases retire.");
                Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
            }
        }

        /// <summary>Reproduces Green Coast cell (2,4) at 513/512 and reports managed weights versus native control-texture storage.</summary>
        /// <remarks>Isolated batch entry; uses the unchanged recipe and a hard 256 MiB worker budget.
        /// Exercises real TerrainData upload, finite weights and managed normalization. Native readback diagnostics establish storage precision;
        /// they do not replace the complete-world acceptance. Awaits the worker and retires all owned products before exiting.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode or recipe climate preparation fails.</exception>
        public static async void RunGuideSplatReadbackBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Splat readback requires isolated batch mode.");
            checks = 0; failures.Clear();
            GameObject root = null; TerrainData data = null; GenerationSnapshot snapshot = null;
            WorldDrainageCache drainage = null; TileData tile = null;
            var graphs = new List<Graph>(); var memory = new ProductMemoryBudget(256L * 1024 * 1024);
            try
            {
                string path = Path.Combine(Application.dataPath, "UniverseCreator/Core/Editor/Diagnostics/Fixtures/guide_worlds.json");
                var recipe = JsonUtility.FromJson<GuideRecipes>(File.ReadAllText(path)).worlds.Single(w => w.version == "V5.5");
                foreach (var definition in recipe.graphs) graphs.Add(BuildGuideGraph(definition));
                root = new GameObject("UC splat readback fixture"); root.SetActive(false);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = graphs.Single(g => g.Domain == UCGraphDomain.TerrainWater);
                world.worldSizeKm = 6; world.chunkCountPerSide = 6; world.resolution = 513; world.maxHeightMeters = 900;
                root.AddComponent<UCWeatherSystemManager>().enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.LightingGraph = graphs.Single(g => g.Domain == UCGraphDomain.LightingClimateWeather);
                await bindings.PrepareTerrainClimateAsync(); snapshot = GenerationSnapshot.Capture(world.graph, world, true);
                var run = world.GenerationScheduler.CurrentRun;
                drainage = new WorldDrainageCache(world.GenerationScheduler.CreateStopToken(run), memory);
                tile = new TileData(new Coord(2, 4), new CoordRect(0, 0, 513, 513), new Vector3(-1000, 0, 1000), new Vector3(1000, 900, 1000), 900);
                tile.SetFullWorld(new Vector3(-3000, 0, -3000), new Vector3(6000, 900, 6000));
                tile.AttachMemory(memory); tile.AssignRun(run); tile.drainageCache = drainage;
                await Task.Run(() =>
                {
                    var evaluator = new GraphEvaluator(snapshot.Graph); tile.Bind(evaluator); // UC repair 30: use the existing evaluator binding API.
                    evaluator.PullNode(snapshot.Graph.GetNodeById("heightOut"), 0, tile, new StopToken());
                    evaluator.PullNode(snapshot.Graph.GetNodeById("splatOut"), 0, tile, new StopToken());
                    tile.FinishPublications(); tile.TrackProductMemory(tile.outputs, "splat-readback-outputs");
                });
                data = new TerrainData();
                var prepared = TerrainBuilder.ReconcileSplatResolution(data, tile.outputs.splatmap);
                prepared = TerrainBuilder.ReconcileSplatLayers(data, prepared, "readback (2,4)", tile.outputs.splatLayerTextures);
                data.SetAlphamaps(0, 0, prepared);
                int x = data.alphamapWidth / 2, z = data.alphamapHeight / 2, layers = data.alphamapLayers;
                var readback = data.GetAlphamaps(x, z, 1, 1); double managed = 0, native = 0;
                for (int layer = 0; layer < layers; layer++)
                {
                    float source = prepared[z, x, layer], stored = readback[0, 0, layer];
                    managed += source; native += stored;
                    Console.WriteLine("UC_SPLAT_READBACK layer=" + layer + " managed=" + source.ToString("R", CultureInfo.InvariantCulture) + " native=" + stored.ToString("R", CultureInfo.InvariantCulture));
                    Check(!float.IsNaN(source) && !float.IsInfinity(source) && source >= 0 &&
                        !float.IsNaN(stored) && !float.IsInfinity(stored) && stored >= 0, "UC splat readback: finite nonnegative layer " + layer);
                }
                Console.WriteLine("UC_SPLAT_READBACK managedSum=" + managed.ToString("R", CultureInfo.InvariantCulture) + " nativeSum=" + native.ToString("R", CultureInfo.InvariantCulture) +
                    " format=" + data.alphamapTextures[0].format + " graphicsFormat=" + data.alphamapTextures[0].graphicsFormat);
                Check(Math.Abs(managed - 1) < 0.00001, "UC splat readback: producer coverage remains normalized before native upload.");
                Check(HasNormalizedNativeSplat(data, readback), "UC splat readback: native coverage fits verified 8-bit storage precision.");
                for (int layer = 0; layer < layers; layer++)
                    Check(Math.Abs(prepared[z, x, layer] - readback[0, 0, layer]) <= 0.5 / 255 + 0.000001,
                        "UC splat readback: native layer matches its source within half a storage step, layer " + layer);
            }
            catch (Exception error) { Check(false, "UC splat readback exception: " + error); }
            finally
            {
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                tile?.ReleaseProductMemory(); drainage?.Dispose(); snapshot?.Release();
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (Graph graph in graphs) UnityEngine.Object.DestroyImmediate(graph);
                memory.ClearRetained();
                Check(memory.ManagedBytes == 0 && memory.NativeBytes == 0, "UC splat readback: final product ownership retires.");
            }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        // UC repair 30: measured RGBA32 UNorm rounds each layer independently; 0.5 becomes 128/255.
        // Bound the sum by the layer count times half a quantization step, keeping finite/lattice checks.
        private static bool HasNormalizedNativeSplat(TerrainData data, float[,,] weights)
        {
            foreach (Texture2D control in data.alphamapTextures)
                if (control.format != TextureFormat.RGBA32 ||
                    control.graphicsFormat != UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm) return false;
            double sum = 0;
            int layers = weights.GetLength(2);
            for (int layer = 0; layer < layers; layer++)
            {
                float weight = weights[0, 0, layer];
                if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0 || weight > 1 ||
                    Math.Abs(weight * 255.0 - Math.Round(weight * 255.0)) > 0.0001) return false;
                sum += weight;
            }
            return Math.Abs(sum - 1) <= layers * (0.5 / 255) + 0.000001;
        }

        /// <summary>Builds all 36 Green Coast native terrain chunks at 513/512 using the production queue, staged commits and an explicit 1536 MiB payload limit.</summary>
        /// <remarks>Isolated batch entry only; keeps every accepted chunk resident until teardown.
        /// Reports managed/native admission separately, verifies native height/layer/collider acceptance and bounds execution to 30 minutes.
        /// Texture and vegetation graph application and real-camera appearance require their separate acceptance checks.</remarks>
        /// <exception cref="InvalidOperationException">Called outside isolated batch mode.</exception>
        public static void RunGuideNativeMemoryBatch() => RunGuideNativeMemory(6, 513);

        /// <summary>Checks cold preview, progressive native acceptance and settlement on a small Green Coast grid in isolated batch mode.</summary>
        /// <exception cref="InvalidOperationException">Called outside isolated batch mode.</exception>
        public static void RunGuideNativeProgressBatch() => RunGuideNativeMemory(2, 65);

        /// <summary>Times the first four 513-sample commits of a cold 6 km Green Coast rebuild, then cancels and retires the remaining work.</summary>
        /// <exception cref="InvalidOperationException">Called outside isolated batch mode.</exception>
        public static void RunGuideNativeTimingBatch() => RunGuideNativeMemory(6, 513, 4);

        private static async void RunGuideNativeMemory(int side, int samples, int timingCells = 0)
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Native guide acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            GameObject root = null;
            ChunkManager manager = null;
            ProductMemoryBudget memory = null;
            var graphs = new List<Graph>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string path = Path.Combine(Application.dataPath, "UniverseCreator/Core/Editor/Diagnostics/Fixtures/guide_worlds.json");
                var recipe = JsonUtility.FromJson<GuideRecipes>(File.ReadAllText(path)).worlds.Single(w => w.version == "V5.5");
                foreach (var definition in recipe.graphs) graphs.Add(BuildGuideGraph(definition));
                // UC repair 37: the fixture owns its pump. Editor startup recovery must not discover
                // and reinitialize this temporary world between an explicit rebuild and settlement.
                root = new GameObject("UC native Green Coast memory") { hideFlags = HideFlags.DontSave };
                root.SetActive(false);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = graphs.Single(g => g.Domain == UCGraphDomain.TerrainWater);
                world.worldSizeKm = side; world.chunkCountPerSide = side; world.resolution = samples; world.maxHeightMeters = 900;
                world.progressiveChunkReveal = true; world.showWireframePreview = true;
                world.maxConcurrentChunks = 1; world.generateCollider = true;
                // UC repair 30: an explicit test setting, never a hidden expansion of saved 256/512 limits.
                typeof(UniverseCreatorObject).GetField("productMemoryBudgetMiB", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(world, 1536);
                memory = world.ProductMemory;
                Check(memory.LimitBytes == 1536L * 1024 * 1024 && world.GetEffectiveResolution() == samples,
                    "UC guide native: explicit budget and authored " + samples + " height samples are honored.");
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>();
                bindings.LightingGraph = graphs.Single(g => g.Domain == UCGraphDomain.LightingClimateWeather);
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                // UC repair 30: Unity invokes destruction callbacks only for previously active GameObjects.
                // Keep automatic drivers disabled, but exercise the production owner's actual destruction lifecycle.
                bindings.enabled = false;
                root.SetActive(true);
                // UC repair 37: use the existing counters on the production scheduler and commit path.
                UCGenerationMetrics metrics = world.GenerationScheduler.Metrics;
                metrics.Enabled = true;
                metrics.Scenario = "Green Coast cold rebuild";
                manager.RebuildAll();
                int cellCount = side * side, accepted, settleCalls = 0, acceptedAtSettle = 0;
                GenerationRunId run = world.GenerationScheduler.CurrentRun;
                int ObserveAcceptance(out int visible, out int visibleWhilePending)
                {
                    int count = 0;
                    visible = 0; visibleWhilePending = 0;
                    for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
                        if (manager.TryGetChunkReadiness(new Coord(x, z), out var state) && state.TerrainAccepted && state.AcceptedRun.Equals(run))
                        {
                            count++;
                            if (!state.Visible) continue;
                            visible++;
                            if (!state.DependentsReady) visibleWhilePending++;
                        }
                    return count;
                }
                manager.RunAfterSettle(() => { settleCalls++; acceptedAtSettle = ObserveAcceptance(out _, out _); });
                bool sawColdPreview = false, sawEarlyGround = false;
                long coldNativeBytes = -1, firstGroundMs = -1;
                int reported = -1;
                do
                {
                    manager.PumpCompleted();
                    accepted = ObserveAcceptance(out int visible, out int visibleWhilePending);
                    if (!sawColdPreview && accepted == 0 &&
                        manager.GetComponentsInChildren<MeshRenderer>().Any(renderer => renderer.enabled && renderer.name == "WireframePreview"))
                    {
                        sawColdPreview = true;
                        coldNativeBytes = memory.NativeBytes;
                        var coldCells = root.GetComponentsInChildren<Terrain>(true);
                        Check(coldCells.Length == cellCount && coldCells.All(ground => ground.terrainData == null),
                            "UC guide native: active blue preview appears before any native placeholder is attached.");
                    }
                    if (!sawEarlyGround && accepted > 0 && accepted < cellCount && visibleWhilePending > 0 && settleCalls == 0)
                    {
                        sawEarlyGround = true;
                        firstGroundMs = timer.ElapsedMilliseconds;
                    }
                    if (accepted != reported)
                    {
                        reported = accepted;
                        Console.WriteLine("UC guide native progress: " + accepted + "/" + cellCount + " elapsedMs=" + timer.ElapsedMilliseconds +
                            " visible=" + visible + " visibleWhilePending=" + visibleWhilePending +
                            " managed=" + memory.ManagedBytes + " native=" + memory.NativeBytes + " peak=" + memory.PeakBytes);
                        Console.WriteLine("UC guide native ownership: " + memory.DescribeLiveSet());
                    }
                    if (!string.IsNullOrEmpty(manager.ReadinessDiagnostic)) throw new InvalidOperationException(manager.ReadinessDiagnostic);
                    if (timingCells > 0 && accepted >= timingCells) break;
                    if (timer.Elapsed.TotalMinutes >= 30) throw new TimeoutException("Native Green Coast build exceeded its 30-minute diagnostic limit.");
                    await Task.Delay(10); // Leave Unity's synchronization context free to retire workers and process commits.
                }
                while (manager.IsRebuildingAll || manager.HasWork);
                Check(sawColdPreview, "UC guide native: cold work has a visible wireframe outside the inactive terrain holder.");
                Check(sawEarlyGround, "UC guide native: accepted ground draws before the final terrain and before dependent readiness.");
                Console.WriteLine($"UC guide native timing: accepted={accepted} firstGroundMs={firstGroundMs} elapsedMs={timer.ElapsedMilliseconds} " +
                    $"metrics={JsonUtility.ToJson(metrics.Snapshot().Last(record => record.run == run.ToString()))}");
                foreach (var stage in (ChunkCommitStage[])Enum.GetValues(typeof(ChunkCommitStage)))
                    Console.WriteLine($"UC guide native stage: {stage} count={manager.CommitMetrics.Count(stage)} maxMs={manager.CommitMetrics.MaximumMilliseconds(stage):F3}");
                foreach (Generator node in world.graph.nodes)
                    if (metrics.TryGetNode(run, node.id, out var timing))
                        Console.WriteLine($"UC guide native node: {node.id} ({node.NodeName}) computes={timing.computes} localHits={timing.localHits} " +
                            $"retainedHits={timing.retainedHits} inclusiveMs={timing.inclusiveMilliseconds:F3} exclusiveMs={timing.exclusiveMilliseconds:F3}");
                if (timingCells > 0)
                {
                    Check(accepted >= timingCells && settleCalls == 0 && coldNativeBytes == 0,
                        "UC guide native timing: captures cold accepted ground before dependent settlement.");
                    foreach (Terrain ground in root.GetComponentsInChildren<Terrain>())
                    {
                        TerrainData data = ground.terrainData;
                        if (data == null || !ground.drawHeightmap) continue;
                        Console.WriteLine($"UC guide native height: {ground.name} hash={GuideHeightFingerprint(data.GetHeights(0, 0, samples, samples)):X16}");
                    }
                    return;
                }
                Check(settleCalls == 1 && acceptedAtSettle == cellCount,
                    "UC guide native: hydrology settlement hook runs once after all " + cellCount + " surfaces accept.");
                Check(!manager.GetComponentsInChildren<MeshRenderer>(true).Any(renderer => renderer.name == "WireframePreview"),
                    "UC guide native: committed previews are retired.");
                Console.WriteLine("UC guide native progressive: coldNativeBytes=" + coldNativeBytes +
                    " firstVisibleGroundMs=" + firstGroundMs + " settleCalls=" + settleCalls + " acceptedAtSettle=" + acceptedAtSettle);
                Terrain[] terrain = root.GetComponentsInChildren<Terrain>(true);
                Check(terrain.Length == cellCount, "UC guide native: all " + cellCount + " native chunks remain present.");
                for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
                    Check(manager.TryGetChunkReadiness(new Coord(x, z), out var state) && state.TerrainAccepted && state.AcceptedRun.Equals(run),
                        "UC guide native: accepted height and splat publication for cell " + x + "," + z);
                int layers = ((UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode)world.graph.GetNodeById("splatOut")).layerCount;
                foreach (Terrain ground in terrain)
                {
                    TerrainData data = ground.terrainData;
                    TerrainCollider collider = ground.GetComponent<TerrainCollider>();
                    Check(data != null && data.heightmapResolution == samples,
                        "UC guide native: retained " + samples + " height samples: " + ground.name);
                    Check(data != null && data.alphamapLayers == layers,
                        "UC guide native: retained all authored splat layers: " + ground.name);
                    Check(ground.gameObject.activeInHierarchy && ground.drawHeightmap,
                        "UC guide native: active terrain drawing after dependency settlement: " + ground.name);
                    Check(collider != null && collider.enabled && collider.terrainData == data,
                        "UC guide native: active collider matches accepted TerrainData: " + ground.name);
                    bool valid = data != null && data.alphamapLayers == layers;
                    if (valid)
                    {
                        float[,,] weights = data.GetAlphamaps(data.alphamapWidth / 2, data.alphamapHeight / 2, 1, 1);
                        valid = HasNormalizedNativeSplat(data, weights);
                    }
                    Check(valid, "UC guide native: finite nonnegative normalized splat weights: " + ground.name);
                }
                Console.WriteLine("UC guide native final: elapsedMs=" + timer.ElapsedMilliseconds + " managed=" + memory.ManagedBytes +
                    " native=" + memory.NativeBytes + " peak=" + memory.PeakBytes + " limit=" + memory.LimitBytes);
                Check(memory.NativeBytes > 0 && memory.ManagedBytes > 0 && memory.PeakBytes <= memory.LimitBytes,
                    "UC guide native: retained scene payload and worker scratch share the explicit hard limit.");
            }
            catch (Exception error)
            {
                Console.WriteLine("UC guide native failed ownership: " + memory?.DescribeLiveSet());
                Check(false, "UC guide native exception: " + error);
            }
            finally
            {
                if (manager != null)
                {
                    manager.CancelAll();
                    // UC repair 30: cancellation is not worker retirement; await real exits before destroying their inputs.
                    var tasks = new List<Task>();
                    var pool = (System.Collections.IEnumerable)typeof(ChunkManager).GetField("pool", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                    foreach (object chunk in pool)
                        if (chunk.GetType().GetField("task").GetValue(chunk) is Task task) tasks.Add(task);
                    try { await Task.WhenAll(tasks); }
                    catch (OperationCanceledException) { /* CancelAll deliberately cancels pending scheduler admissions. */ }
                    // UC repair 30: final worker input destruction is posted to Unity's main context.
                    // Let that already queued retirement execute before inspecting the final ledger.
                    await Task.Yield();
                }
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (Graph graph in graphs) UnityEngine.Object.DestroyImmediate(graph);
                memory?.ClearRetained();
                Console.WriteLine("UC guide native retired ownership: " + memory?.DescribeLiveSet());
                Check(memory == null || memory.ManagedBytes == 0 && memory.NativeBytes == 0,
                    "UC guide native: final worker/cache/native ownership retires completely.");
                Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
            }
        }

        /// <summary>Measures native terrain-only allocations for 11, 20 and 30 km world settings with 1 km cells.</summary>
        /// <remarks>Isolated Windows Editor batch entry. Builds one central buffered window per size with the effective height resolution,
        /// 512-square alphamaps and eight shared terrain layers. It does not generate a world, render frames or measure Android costs.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunLargeWorldTerrainMemoryBatch() => RunLargeWorldTerrainMemory(new[] { 11, 20, 30 }, false);

        /// <summary>Measures 512 terrain intervals (513 height samples) per 1 km cell for 20 km and 30 km worlds.</summary>
        /// <remarks>Measures native window allocations and encodes all cells of a synthetic terrain for storage comparison.
        /// The production resolution policy is logged separately. The encoded payload is a diagnostic format.</remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode.</exception>
        public static void RunLargeWorldFullDetailTerrainMemoryBatch() => RunLargeWorldTerrainMemory(new[] { 20, 30 }, true);

        private static void RunLargeWorldTerrainMemory(int[] worldSizesKm, bool measureRequestedHeightSamples)
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Terrain memory measurement requires isolated batch mode.");
            checks = 0; failures.Clear();
            var root = new GameObject("UC large world terrain memory"); root.SetActive(false);
            const int requestedHeightSamples = 513, alphaResolution = 512, layerCount = 8;
            var layers = new TerrainLayer[layerCount];
            try
            {
                for (int i = 0; i < layers.Length; i++) layers[i] = new TerrainLayer();
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.chunkSizeMeters = 1000; world.terrainLayers = layers;
                world.residentWindowPerSide = 5; world.bufferRingWidth = 1;
                var weights = new float[alphaResolution, alphaResolution, layerCount];
                for (int z = 0; z < alphaResolution; z++) for (int x = 0; x < alphaResolution; x++)
                    weights[z, x, (z / 64 + x / 64) % layerCount] = 1;

                foreach (int worldKm in worldSizesKm)
                {
                    world.worldSizeKm = worldKm;
                    world.resolution = requestedHeightSamples;
                    world.ValidateSettings();
                    int effective = world.GetChunkResolution();
                    int measured = measureRequestedHeightSamples ? requestedHeightSamples : effective;
                    int interior = world.residentWindowPerSide * world.residentWindowPerSide;
                    int windowSide = 2 * world.GetStreamRadius() + 1;
                    var terrain = new TerrainData[windowSide * windowSide];
                    var heights = new float[measured, measured];
                    for (int z = 0; z < measured; z++) for (int x = 0; x < measured; x++)
                        heights[z, x] = 0.2f + 0.1f * x / (measured - 1);
                    Check(world.chunkCountPerSide == worldKm && world.GetChunkSize() == 1000f && interior == 25 && terrain.Length == 49,
                        "UC terrain memory: " + worldKm + " km computes a 25-cell window plus buffer.");
                    Check(effective == (worldKm == 11 ? 513 : 257),
                        "UC terrain memory: " + worldKm + " km uses the current desktop terrain resolution policy.");
                    try
                    {
                        long objectBytes = 0, alphaTextureBytes = 0, interiorObjectBytes = 0, interiorAlphaTextureBytes = 0;
                        long before = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                        float cellSize = world.GetChunkSize();
                        var size = new Vector3(cellSize, world.maxHeightMeters, cellSize);
                        for (int i = 0; i < terrain.Length; i++)
                        {
                            TerrainData data = terrain[i] = TerrainBuilder.GetOrCreateTerrainData(null, measured, size);
                            data.alphamapResolution = alphaResolution;
                            TerrainBuilder.ApplyLayers(data, world);
                            data.SetHeights(0, 0, heights);
                            data.SetAlphamaps(0, 0, weights);
                            long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(data);
                            long alphaBytes = 0;
                            foreach (Texture2D texture in data.alphamapTextures)
                                alphaBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
                            objectBytes += bytes; alphaTextureBytes += alphaBytes;
                            if (i < interior) { interiorObjectBytes += bytes; interiorAlphaTextureBytes += alphaBytes; }
                        }
                        long allocatedDelta = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - before;
                        Check(terrain.All(data => data.heightmapResolution == measured && data.alphamapLayers == layerCount &&
                            data.alphamapResolution == alphaResolution) && objectBytes > 0 && alphaTextureBytes > 0,
                            $"UC terrain memory: {worldKm} km allocates {measured} native height samples and returns profiler sizes.");
                        Console.WriteLine($"UC terrain memory: worldKm={worldKm} gridSide={world.chunkCountPerSide} requested={requestedHeightSamples} " +
                            $"validated={world.resolution} effective={effective} measured={measured} heightIntervals={measured - 1} " +
                            $"interior={interior} buffered={terrain.Length} terrainDataBytes25={interiorObjectBytes} " +
                            $"alphaTextureBytes25={interiorAlphaTextureBytes} terrainDataBytes49={objectBytes} " +
                            $"alphaTextureBytes49={alphaTextureBytes} editorAllocatedDeltaBytes49={allocatedDelta} " +
                            $"terrainDataBytesPerCell={objectBytes / terrain.Length} alphaTextureBytesPerCell={alphaTextureBytes / terrain.Length} " +
                            $"editorFullGridPlanningMiB={world.RecommendedProductMemoryMiB()}");
                    }
                    finally { foreach (TerrainData data in terrain) if (data != null) UnityEngine.Object.DestroyImmediate(data); }
                    if (measureRequestedHeightSamples)
                        MeasureLargeWorldTerrainStorage(world.chunkCountPerSide, measured, alphaResolution, layerCount);
                }
            }
            catch (Exception error) { Check(false, "UC terrain memory diagnostic exception: " + error); }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                foreach (TerrainLayer layer in layers) if (layer != null) UnityEngine.Object.DestroyImmediate(layer);
            }
            Console.WriteLine(Report(failures.Count == 0)); EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        // UC repair 37: measure independent cell blocks so the disk comparison includes every cell and permits random access.
        private static void MeasureLargeWorldTerrainStorage(int gridSide, int heightSamples, int alphaResolution, int layerCount)
        {
            var noise = new Noise(37);
            var heights = new byte[heightSamples * heightSamples * sizeof(ushort)];
            var weights = new byte[alphaResolution * alphaResolution * layerCount];
            using var compressed = new MemoryStream();
            long heightBytes = 0, weightBytes = 0;
            int intervals = heightSamples - 1, encodedCells = 0;

            for (int cellZ = 0; cellZ < gridSide; cellZ++) for (int cellX = 0; cellX < gridSide; cellX++)
            {
                Array.Clear(weights, 0, weights.Length);
                for (int z = 0; z < heightSamples; z++) for (int x = 0; x < heightSamples; x++)
                {
                    int worldX = cellX * intervals + x, worldZ = cellZ * intervals + z;
                    float elevation = 0.8f * noise.Perlin(worldX / 4096f, worldZ / 4096f) +
                        0.2f * noise.Perlin(worldX / 97f, worldZ / 97f);
                    int height = Mathf.RoundToInt(Mathf.Clamp01(elevation) * ushort.MaxValue);
                    int sample = (z * heightSamples + x) * sizeof(ushort);
                    heights[sample] = (byte)(height & byte.MaxValue);
                    heights[sample + 1] = (byte)(height >> 8);

                    if (x >= alphaResolution || z >= alphaResolution) continue;
                    float band = Mathf.Clamp01((elevation - 0.25f) * 2f) * (layerCount - 1);
                    int lower = Mathf.Min((int)band, layerCount - 2);
                    int upperWeight = Mathf.RoundToInt((band - lower) * byte.MaxValue);
                    int pixel = (z * alphaResolution + x) * layerCount + lower;
                    weights[pixel] = (byte)(byte.MaxValue - upperWeight);
                    weights[pixel + 1] = (byte)upperWeight;
                }

                heightBytes += MeasureCompressedTerrainBlock(compressed, heights);
                weightBytes += MeasureCompressedTerrainBlock(compressed, weights);
                encodedCells++;
            }

            Check(encodedCells == gridSide * gridSide && heightBytes > 0 && weightBytes > 0,
                $"UC terrain storage: encoded all {gridSide * gridSide} cells with {heightSamples} height samples.");
            Console.WriteLine($"UC terrain storage: sideKm={gridSide} encodedCells={encodedCells} heightSamples={heightSamples} " +
                $"alphaResolution={alphaResolution} layers={layerCount} rawHeightBytes={(long)heights.Length * encodedCells} " +
                $"rawWeightBytes={(long)weights.Length * encodedCells} gzipHeightBytes={heightBytes} gzipWeightBytes={weightBytes} " +
                $"gzipTotalBytes={heightBytes + weightBytes}");
        }

        private static long MeasureCompressedTerrainBlock(MemoryStream output, byte[] bytes)
        {
            output.SetLength(0);
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(bytes, 0, bytes.Length);
            return output.Length;
        }

        private static Graph BuildGuideGraph(GuideGraph definition)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                graph.SetDomain((UCGraphDomain)Enum.Parse(typeof(UCGraphDomain), definition.domain));
                foreach (GuideNode record in definition.nodes)
                {
                    Generator node = NodeCatalog.Instantiate(NodeCatalog.All.Single(e => e.nodeName == record.name));
                    node.id = record.id;
                    foreach (GuideSetting setting in record.settings)
                    {
                        FieldInfo field = node.GetType().GetField(setting.name, BindingFlags.Instance | BindingFlags.Public);
                        if (field == null) throw new InvalidOperationException(record.name + " has no saved field " + setting.name);
                        field.SetValue(node, ParseGuideValue(field.FieldType, setting.value));
                    }
                    graph.nodes.Add(node);
                }
                foreach (GuideWire wire in definition.wires)
                {
                    Port[] outputs = graph.GetNodeById(wire.source).GetOutputs(), inputs = graph.GetNodeById(wire.target).GetInputs();
                    int outlet = Array.FindIndex(outputs, p => p.name == wire.output), inlet = Array.FindIndex(inputs, p => p.name == wire.input);
                    if (outlet < 0 || inlet < 0 || outputs[outlet].type != inputs[inlet].type)
                        throw new InvalidOperationException("Invalid guide wire: " + wire.source + "/" + wire.output + " -> " + wire.target + "/" + wire.input);
                    graph.connections.Add(new Connection(wire.source, outlet, wire.target, inlet));
                }
                return graph;
            }
            catch { UnityEngine.Object.DestroyImmediate(graph); throw; }
        }

        private static object ParseGuideValue(Type type, string text)
        {
            if (type == typeof(string)) return text;
            if (type.IsEnum) return Enum.Parse(type, text);
            if (type == typeof(Vector2) || type == typeof(Rect))
            {
                float[] values = text.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (type == typeof(Vector2) && values.Length == 2) return new Vector2(values[0], values[1]);
                if (type == typeof(Rect) && values.Length == 4) return new Rect(values[0], values[1], values[2], values[3]);
                throw new InvalidOperationException("Invalid guide vector/region: " + text);
            }
            return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        }

        private static ulong GuideHeightFingerprint(float[,] values)
        {
            if (values == null) throw new InvalidOperationException("The guide produced no Height Output.");
            ulong hash = 14695981039346656037UL;
            foreach (float value in values)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Nonfinite guide height.");
                hash = unchecked((hash ^ (uint)value.GetHashCode()) * 1099511628211UL);
            }
            return hash;
        }
    }
}
