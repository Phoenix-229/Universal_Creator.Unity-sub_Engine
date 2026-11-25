// UC repair 29: exact guide inputs, actual climate preparation and bounded worker evaluation.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        public static async void RunGuideNativeMemoryBatch()
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
                root = new GameObject("UC full native Green Coast memory"); root.SetActive(false);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = graphs.Single(g => g.Domain == UCGraphDomain.TerrainWater);
                world.worldSizeKm = 6; world.chunkCountPerSide = 6; world.resolution = 513; world.maxHeightMeters = 900;
                world.maxConcurrentChunks = 1; world.generateCollider = true;
                // UC repair 30: an explicit test setting, never a hidden expansion of saved 256/512 limits.
                typeof(UniverseCreatorObject).GetField("productMemoryBudgetMiB", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(world, 1536);
                memory = world.ProductMemory;
                Check(memory.LimitBytes == 1536L * 1024 * 1024 && world.GetEffectiveResolution() == 513,
                    "UC guide native: explicit budget and authored 513 height samples are honored.");
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                var bindings = root.AddComponent<UCWorldGraphBindings>();
                bindings.LightingGraph = graphs.Single(g => g.Domain == UCGraphDomain.LightingClimateWeather);
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                // UC repair 30: Unity invokes destruction callbacks only for previously active GameObjects.
                // Keep automatic drivers disabled, but exercise the production owner's actual destruction lifecycle.
                bindings.enabled = false;
                root.SetActive(true);
                manager.RebuildAll();
                int reported = -1;
                do
                {
                    manager.PumpCompleted();
                    int accepted = 0;
                    for (int z = 0; z < 6; z++) for (int x = 0; x < 6; x++)
                        if (manager.TryGetChunkReadiness(new Coord(x, z), out var state) && state.TerrainAccepted) accepted++;
                    if (accepted != reported)
                    {
                        reported = accepted;
                        Console.WriteLine("UC guide native progress: " + accepted + "/36 elapsedMs=" + timer.ElapsedMilliseconds +
                            " managed=" + memory.ManagedBytes + " native=" + memory.NativeBytes + " peak=" + memory.PeakBytes);
                        Console.WriteLine("UC guide native ownership: " + memory.DescribeLiveSet());
                    }
                    if (!string.IsNullOrEmpty(manager.ReadinessDiagnostic)) throw new InvalidOperationException(manager.ReadinessDiagnostic);
                    if (timer.Elapsed.TotalMinutes >= 30) throw new TimeoutException("Native Green Coast build exceeded its 30-minute diagnostic limit.");
                    await Task.Delay(10); // Leave Unity's synchronization context free to retire workers and process commits.
                }
                while (manager.IsRebuildingAll || manager.HasWork);
                Terrain[] terrain = root.GetComponentsInChildren<Terrain>(true);
                Check(terrain.Length == 36, "UC guide native: all 36 native chunks remain present.");
                for (int z = 0; z < 6; z++) for (int x = 0; x < 6; x++)
                    Check(manager.TryGetChunkReadiness(new Coord(x, z), out var state) && state.TerrainAccepted,
                        "UC guide native: accepted height and splat publication for cell " + x + "," + z);
                int layers = ((UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode)world.graph.GetNodeById("splatOut")).layerCount;
                foreach (Terrain ground in terrain)
                {
                    TerrainData data = ground.terrainData;
                    TerrainCollider collider = ground.GetComponent<TerrainCollider>();
                    Check(data != null && data.heightmapResolution == 513,
                        "UC guide native: retained 513 height samples: " + ground.name);
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
