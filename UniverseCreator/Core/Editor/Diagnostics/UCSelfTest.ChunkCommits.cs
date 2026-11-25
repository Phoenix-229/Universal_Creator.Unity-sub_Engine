using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T02-04: actual native terrain fixtures drive the same iterator and pump used in production.
        private static void TestStagedChunkCommits()
        {
            TestStagedOutputMaps();
            var root = new GameObject("UC staged commit acceptance"); root.SetActive(false);
            var originals = new List<TerrainData>();
            var terrains = new List<UnityEngine.Terrain>();
            var chunks = new List<object>();
            var layer = new TerrainLayer();
            ChunkManager manager = null;
            ProductMemoryBudget memory = null; // UC-T02-06: retain the ledger after native owner teardown for release assertions.
            Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                UniverseCreatorObject world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false;
                memory = world.ProductMemory;
                world.autoRegenerate = false; world.chunkCountPerSide = 3; world.worldSizeKm = 0.096f;
                world.maxHeightMeters = 100f; world.generateCollider = true; world.terrainLayers = new[] { layer };
                typeof(UniverseCreatorObject).GetField("chunkCommitBudgetMs", flags).SetValue(world, 0.1f);
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                IList pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager);
                IDictionary cells = (IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager);
                for (int index = 0; index < 3; index++)
                {
                    var data = new TerrainData { heightmapResolution = 129, size = new Vector3(32f, 100f, 32f), terrainLayers = new[] { layer } };
                    originals.Add(data);
                    float[,] accepted = CommitHeights(129, 0.2f);
                    data.SetHeights(0, 0, accepted);
                    var ground = new GameObject("Terrain_" + index + "_0"); ground.transform.SetParent(root.transform, false);
                    ground.transform.position = new Vector3(index * 32f, 0f, 0f);
                    var terrain = ground.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = data; terrains.Add(terrain);
                    var collider = ground.AddComponent<TerrainCollider>(); collider.terrainData = data;
                    object chunk = Activator.CreateInstance(chunkType, true);
                    SetCommitField(chunkType, chunk, "go", ground); SetCommitField(chunkType, chunk, "terrain", terrain);
                    SetCommitField(chunkType, chunk, "data", data); SetCommitField(chunkType, chunk, "resolution", 129);
                    SetCommitField(chunkType, chunk, "cell", new Coord(index, 0)); SetCommitField(chunkType, chunk, "lod", LODLevel.High);
                    SetCommitField(chunkType, chunk, "appliedHeights", accepted);
                    SetCommitField(chunkType, chunk, "bakedThisSession", true);
                    QueueCommitFixture(world, chunkType, chunk, index, 0.6f);
                    pool.Add(chunk); cells.Add(new Coord(index, 0), chunk); chunks.Add(chunk);
                }
                typeof(ChunkManager).GetField("activeWorkers", flags).SetValue(manager, 3);

                var stages = (IEnumerable<ChunkCommitStep>)typeof(ChunkManager).GetMethod("StageChunk", flags)
                    .Invoke(manager, new[] { (object)world, chunks[0] });
                // UC-T02-06: native replacement admission must fail before touching the accepted terrain/collider.
                bool memoryRefused = false;
                using (memory.Reserve(memory.LimitBytes - memory.ManagedBytes - memory.NativeBytes))
                using (IEnumerator<ChunkCommitStep> rejected = stages.GetEnumerator())
                {
                    try { rejected.MoveNext(); } catch (InvalidOperationException) { memoryRefused = true; }
                }
                Check(memoryRefused && terrains[0].terrainData == originals[0] && memory.NativeBytes == 0,
                    "UC-T02-06 budget rejection preserves the accepted native surface and releases failed staging reservations.");
                IEnumerator<ChunkCommitStep> partial = stages.GetEnumerator();
                SetCommitField(chunkType, chunks[0], "commit", partial);
                bool uploaded = false;
                for (int step = 0; step < 32 && partial.MoveNext(); step++)
                    if (partial.Current.Stage == ChunkCommitStage.UploadHeights) { uploaded = true; break; }
                TerrainData abandoned = (TerrainData)chunkType.GetField("stagedData").GetValue(chunks[0]);
                Check(memory.NativeBytes > 0 && memory.PinnedBytes >= memory.NativeBytes,
                    "UC-T02-06 detached native staging remains charged while its upload is incomplete.");
                Check(!RiverChannelRegistry.TrySample(world, 16, 16, 25, out _, out _),
                    "UC-T02-07 partially uploaded terrain has not exposed its channel publication.");
                Check(uploaded && abandoned != null && abandoned != originals[0] && terrains[0].terrainData == originals[0] &&
                      terrains[0].GetComponent<TerrainCollider>().terrainData == originals[0] && terrains[0].drawHeightmap,
                    "UC-T02-04 partial uploads keep the accepted visible terrain and collider together.");
                world.GenerationScheduler.BeginRun();
                manager.PumpCompleted();
                Check(abandoned == null && chunkType.GetField("commit").GetValue(chunks[0]) == null &&
                      Mathf.Abs(originals[0].GetHeights(0, 0, 1, 1)[0, 0] - 0.2f) < 1e-4f,
                    "UC-T02-04 restart retires detached native staging data without altering accepted heights.");
                Check(memory.NativeBytes == 0 && memory.ManagedBytes == 0,
                    "UC-T02-06 cancelled native staging releases its native and upload reservations.");

                // UC biome repair: native drawHeightmap is independent of Hierarchy visibility eyes.
                originals[0].SetHoles(4, 4, new bool[1, 1]);
                float savedSample = originals[0].GetHeights(16, 16, 1, 1)[0, 0];
                typeof(ChunkManager).GetMethod("ShowWireframe", flags).Invoke(manager, new[] { (object)world, chunks[0] });
                Check(terrains[0].drawHeightmap, "UC biome edit preview preserves the last accepted native surface while a replacement is staged.");
                terrains[0].drawHeightmap = false; // Seed a retired preview's native drawing state for cancellation recovery.
                manager.CancelAll();
                Check(terrains[0].drawHeightmap && terrains[0].terrainData == originals[0] &&
                    originals[0].GetHeights(16, 16, 1, 1)[0, 0] == savedSample && !originals[0].GetHoles(4, 4, 1, 1)[0, 0] &&
                    terrains[0].GetComponent<TerrainCollider>().terrainData == originals[0],
                    "UC biome cancelled preview restores drawing and preserves accepted heights, holes and collision.");
                SetCommitField(chunkType, chunks[0], "presentationParked", true); terrains[0].drawHeightmap = false;
                typeof(ChunkManager).GetMethod("HideWireframe", flags).Invoke(manager, new[] { chunks[0] });
                Check(!terrains[0].drawHeightmap, "UC biome preview retirement never bypasses an intentional dependency hold.");
                SetCommitField(chunkType, chunks[0], "presentationParked", false);
                typeof(ChunkManager).GetMethod("HideWireframe", flags).Invoke(manager, new[] { chunks[0] });

                world.GenerationScheduler.BeginRun(); // Cancellation retires the old revision; a replacement must join a fresh run.
                for (int index = 0; index < chunks.Count; index++) QueueCommitFixture(world, chunkType, chunks[index], index, 0.65f);
                typeof(ChunkManager).GetField("activeWorkers", flags).SetValue(manager, chunks.Count);
                int pumps = 0;
                while (pumps++ < 256)
                {
                    manager.PumpCompleted();
                    bool pending = false;
                    foreach (object chunk in chunks) if (!(bool)chunkType.GetField("applied").GetValue(chunk)) pending = true;
                    if (!pending && !manager.HasWork) break; // UC-T09-03: native publication is followed by seam/water settlement before presentation.
                }
                Check(pumps < 256 && manager.CommitMetrics.Count(ChunkCommitStage.PublishContent) == 3 &&
                      manager.CommitMetrics.Count(ChunkCommitStage.UploadHeights) >= 15 && manager.CommitMetrics.PeakUploadBytes <= 256 * 1024,
                    "UC-T02-04 multiple native chunks drain through measured bounded upload stages.");
                foreach (object chunk in chunks)
                {
                    var retained = (OutputBag)chunkType.GetField("retainedScatter").GetValue(chunk);
                    var readback = (float[,])chunkType.GetField("appliedHeights").GetValue(chunk);
                    var lease = (ProductMemoryBudget.Lease)chunkType.GetField("committedOutputMemory").GetValue(chunk);
                    Check(retained != null && retained.heightmap == null && retained.splatmap == null && retained.channelPublications == null &&
                        retained.zoneHistogram[0] == 7 && readback != null && lease != null && !(lease.Entry.Product is OutputBag) &&
                        lease.Entry.Managed == 64L + readback.LongLength * sizeof(float) + ProductMemoryFootprint.Estimate(retained),
                        "UC repair 30: accepted readback and scatter stay admitted without retaining uploaded output maps.");
                }
                root.SetActive(true); Physics.SyncTransforms();
                foreach (UnityEngine.Terrain terrain in terrains)
                {
                    TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
                    bool hit = collider.Raycast(new Ray(terrain.transform.position + new Vector3(16f, 150f, 16f), Vector3.down), out RaycastHit sample, 200f);
                    Check(terrain.terrainData == collider.terrainData && hit && Mathf.Abs(sample.point.y - 65f) < 0.05f && terrain.drawHeightmap,
                        "UC-T02-04 revealed terrain and native collision both use the completed replacement height.");
                    Check(RiverChannelRegistry.TrySample(world, terrain.transform.position.x + 16, 16, 25, out float width, out float depth) && width >= 2 && depth == 1,
                        "UC-T02-07 the real staged terrain commit publishes channel metadata with its accepted surface.");
                }

                // The artificial expensive step controls only elapsed work; the real production pump decides when to yield.
                int probeSteps = 0;
                QueueCommitFixture(world, chunkType, chunks[0], 0, 0.7f);
                var acceptedLease = (ProductMemoryBudget.Lease)chunkType.GetField("committedOutputMemory").GetValue(chunks[0]);
                TerrainData acceptedData = terrains[0].terrainData;
                stages = (IEnumerable<ChunkCommitStep>)typeof(ChunkManager).GetMethod("StageChunk", flags)
                    .Invoke(manager, new[] { (object)world, chunks[0] });
                memoryRefused = false;
                using (memory.Reserve(memory.LimitBytes - memory.ManagedBytes - memory.NativeBytes))
                using (IEnumerator<ChunkCommitStep> rejected = stages.GetEnumerator())
                {
                    try { rejected.MoveNext(); } catch (InvalidOperationException) { memoryRefused = true; }
                }
                Check(memoryRefused && terrains[0].terrainData == acceptedData && acceptedLease.Entry.Pins == 1 &&
                    ReferenceEquals(acceptedLease, chunkType.GetField("committedOutputMemory").GetValue(chunks[0])),
                    "UC repair 30: rejected replacement preserves the previous accepted surface and managed lease.");
                SetCommitField(chunkType, chunks[0], "commit", TimedCommitProbe(() => probeSteps++).GetEnumerator());
                typeof(ChunkManager).GetField("activeWorkers", flags).SetValue(manager, 1);
                long before = manager.CommitMetrics.OverBudgetOperations;
                manager.PumpCompleted();
                Check(probeSteps == 1 && manager.CommitMetrics.OverBudgetOperations > before,
                    "UC-T02-04 an indivisible over-budget operation is recorded and defers the next stage.");
                manager.PumpCompleted();
                Check(probeSteps == 2 && manager.CommitMetrics.MaximumPumpMilliseconds >= 2.0,
                    "UC-T02-04 work resumes on the next pump without a fixed inter-chunk delay.");
            }
            finally
            {
                if (manager != null) manager.CancelAll();
                // UC-T02-06: production teardown must destroy its own data before fixture fallback cleanup.
                var replacements = new List<TerrainData>();
                foreach (UnityEngine.Terrain terrain in terrains)
                {
                    if (terrain == null) continue;
                    TerrainData current = terrain.terrainData;
                    if (current != null && !originals.Contains(current)) replacements.Add(current);
                }
                UnityEngine.Object.DestroyImmediate(root);
                if (memory != null) Check(memory.NativeBytes == 0 && memory.ManagedBytes == 0,
                    "UC-T02-06 native owner teardown releases accepted terrain reservations. Native=" + memory.NativeBytes + "; managed=" + memory.ManagedBytes + ".");
                foreach (TerrainData replacement in replacements)
                {
                    Check(replacement == null, "UC-T02-06 native owner teardown destroys its accepted generated TerrainData.");
                    if (replacement != null) UnityEngine.Object.DestroyImmediate(replacement);
                }
                foreach (TerrainData data in originals) if (data != null) UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(layer);
            }
        }

        private static void TestStagedOutputMaps()
        {
            var layers = new[] { new TerrainLayer(), new TerrainLayer() };
            var data = new TerrainData { heightmapResolution = 129, alphamapResolution = 64, terrainLayers = layers };
            var detailTexture = new Texture2D(2, 2);
            try
            {
                var output = new OutputBag { splatmap = new float[64, 64, 2], holes = new bool[128, 128] };
                for (int z = 0; z < 64; z++) for (int x = 0; x < 64; x++)
                { output.splatmap[z, x, 0] = x / 63f; output.splatmap[z, x, 1] = 1f - x / 63f; }
                for (int z = 0; z < 128; z++) for (int x = 0; x < 128; x++) output.holes[z, x] = true;
                output.holes[80, 70] = false;
                int weights = 0, holes = 0; long peak = 0;
                foreach (ChunkCommitStep step in TerrainBuilder.ApplyOutputStages(data, output, null, null, default(Bounds), 0f, null))
                {
                    peak = Math.Max(peak, step.UploadBytes);
                    if (step.Stage == ChunkCommitStage.UploadTextures) weights++;
                    if (step.Stage == ChunkCommitStage.UploadHoles) holes++;
                }
                float[,,] sampled = data.GetAlphamaps(0, 31, 64, 2);
                Check(weights == 2 && holes == 4 && peak <= 256 * 1024 &&
                      sampled[0, 0, 0] < 0.01f && sampled[1, 63, 0] > 0.99f && !data.GetHoles(70, 80, 1, 1)[0, 0],
                    "UC-T02-04 rectangular weight and hole uploads preserve coordinates across strip boundaries.");
                data.SetDetailResolution(64, 8);
                data.detailPrototypes = new[] { new DetailPrototype { prototypeTexture = detailTexture,
                    minWidth = 1f, maxWidth = 1f, minHeight = 1f, maxHeight = 1f, renderMode = DetailRenderMode.GrassBillboard } };
                var detail = new int[64, 64]; detail[31, 3] = 2; detail[32, 60] = 3;
                typeof(ScatterApplier).GetMethod("SetDetailLayerBounded", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { data, 0, detail });
                // UC-T02-04: sample exact native coordinates; rectangular readback layout differs from alphamaps.
                int firstCount = data.GetDetailLayer(3, 31, 1, 1, 0)[0, 0];
                int secondCount = data.GetDetailLayer(60, 32, 1, 1, 0)[0, 0];
                Check(firstCount == 2 && secondCount == 3,
                    "UC-T02-04 bounded detail writes preserve exact instance counts on both sides of a strip boundary.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(data);
                foreach (TerrainLayer layer in layers) UnityEngine.Object.DestroyImmediate(layer);
                UnityEngine.Object.DestroyImmediate(detailTexture);
            }
        }

        private static float[,] CommitHeights(int resolution, float value)
        {
            var heights = new float[resolution, resolution];
            for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++) heights[z, x] = value;
            return heights;
        }

        private static void SetCommitField(Type type, object chunk, string field, object value) => type.GetField(field).SetValue(chunk, value);

        private static void QueueCommitFixture(UniverseCreatorObject world, Type type, object chunk, int index, float height)
        {
            var tile = new TileData(new Coord(index, 0), new CoordRect(0, 0, 129, 129), new Vector3(index * 32f, 0f, 0f), new Vector3(32f, 100f, 32f), 100f);
            tile.AssignRun(world.GenerationScheduler.CurrentRun); tile.outputs.heightmap = CommitHeights(129, height);
            // UC repair 30: exercise real evaluation accounting, including a map used only for native upload.
            tile.AttachMemory(world.ProductMemory);
            tile.outputs.splatmap = new float[64, 64, 1];
            for (int z = 0; z < 64; z++) for (int x = 0; x < 64; x++) tile.outputs.splatmap[z, x, 0] = 1;
            tile.outputs.zoneHistogram[0] = 7;
            // UC-T02-07: the native fixture now carries the same deferred payload as RiverNetworkNode.
            float width = index + 2;
            tile.AddPublication(new ChannelGraphPublication("staged-channel", new[] { width, width, width, width },
                new[] { 1f, 1f, 1f, 1f }, 2, 2, index * 32f, 0, 32, 32));
            tile.FinishPublications();
            tile.TrackProductMemory(tile.outputs);
            SetCommitField(type, chunk, "tile", tile); SetCommitField(type, chunk, "workerDone", true);
            SetCommitField(type, chunk, "inFlight", true); SetCommitField(type, chunk, "applied", false);
            SetCommitField(type, chunk, "stop", world.GenerationScheduler.CreateStopToken(tile.RunId));
        }

        private static IEnumerable<ChunkCommitStep> TimedCommitProbe(Action visited)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalMilliseconds < 2.0) System.Threading.Thread.SpinWait(32);
            visited(); yield return new ChunkCommitStep(ChunkCommitStage.SyncHeightmap);
            visited(); yield return new ChunkCommitStep(ChunkCommitStage.PublishContent);
        }
    }
}
