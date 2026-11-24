// UC-T07-09: explicit world Run/Apply captures one graph revision and stages each region against its own accepted fields.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Utilities
{
    internal sealed class TextureBakeCache : IDisposable
    {
        internal sealed class Region
        {
            internal ProductMemoryBudget Memory;
            internal Vector3 Position, Size;
            internal CoordRect Rect;
            internal Guid GroundVersion;
            internal Func<bool> InputsCurrent;
            internal readonly Dictionary<string, RetainedProduct> Products = new Dictionary<string, RetainedProduct>();
        }
        private readonly Dictionary<UnityEngine.Terrain, Region> regions = new Dictionary<UnityEngine.Terrain, Region>();
        private readonly ProductMemoryBudget standaloneMemory = new ProductMemoryBudget(512L * 1024 * 1024); // UC-T07-09: warm standalone bakes share the same admitted owner.
        private Graph graph;
        internal ProductMemoryBudget MemoryFor(UniverseCreatorObject world) => world != null ? world.ProductMemory : standaloneMemory;
        internal Dictionary<string, RetainedProduct> ForRegion(Graph source, UnityEngine.Terrain terrain, TileData tile, Guid version, Func<bool> inputsCurrent)
        {
            if (!ReferenceEquals(graph, source)) { Dispose(); graph = source; }
            if (!regions.TryGetValue(terrain, out Region region))
            {
                if (regions.Count >= 256) throw new InvalidOperationException("Texture bake cache exceeds 256 terrain regions.");
                region = new Region(); regions.Add(terrain, region);
            }
            if (!ReferenceEquals(region.Memory, tile.MemoryBudget) || region.Position != tile.worldPos || region.Size != tile.worldSize ||
                !region.Rect.Equals(tile.rect) || region.GroundVersion != version || (region.InputsCurrent != null && !region.InputsCurrent()) || region.Products.Count > 4096)
            { Forget(region); region.Memory = tile.MemoryBudget; region.Position = tile.worldPos; region.Size = tile.worldSize; region.Rect = tile.rect; region.GroundVersion = version; }
            region.InputsCurrent = inputsCurrent;
            return new Dictionary<string, RetainedProduct>(region.Products); // UC-T07-09: cancelled staging cannot overwrite the last accepted cache.
        }
        internal void Accept(UnityEngine.Terrain terrain, TileData tile)
        {
            Region region = regions[terrain];
            tile.AcceptRetainedProducts(region.Products); region.Products.Clear();
            foreach (var product in tile.retained) region.Products.Add(product.Key, product.Value);
        }
        private static void Forget(Region region)
        {
            foreach (RetainedProduct product in region.Products.Values) region.Memory?.Forget(product.memoryEntry);
            region.Products.Clear();
        }
        public void Dispose() { foreach (Region region in regions.Values) Forget(region); regions.Clear(); graph = null; }
    }

    internal sealed class TextureGraphBake : IDisposable
    {
        private static readonly HashSet<TextureGraphBake> active = new HashSet<TextureGraphBake>(); // UC-T07-10: scene/reload/shutdown retires captured bakes through this same owner.
        private sealed class Region
        {
            internal UnityEngine.Terrain Terrain;
            internal TileData Tile;
            internal RiverChannelSnapshot Ground;
            internal IncrementalGraphEvaluation Evaluation;
            internal Vector3 Position, Size;
        }
        private readonly Graph source;
        private readonly UniverseCreatorObject world;
        private readonly GenerationSnapshot snapshot;
        private readonly GenerationRunId run;
        private readonly StopToken stop;
        private readonly TextureBakeCache cache;
        private readonly Func<bool> graphCurrent;
        private readonly WorldGraphWaterInput water;
        private readonly WorldGraphEnvironmentInput environment;
        private readonly List<Region> regions = new List<Region>();
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private int index;
        private bool disposed;
        internal string Status { get; private set; }
        internal bool IsComplete { get; private set; }
        internal bool Succeeded { get; private set; }
        internal int AppliedRegions { get; private set; }

        internal TextureGraphBake(Graph graph, UnityEngine.Terrain selected, UniverseCreatorObject selectedWorld, int resolution, TextureBakeCache cache)
        {
            GraphExecutionGuard.RequireMainThread("Texture Run/Apply capture");
            if (graph == null || selected == null || selected.terrainData == null || cache == null) throw new ArgumentException("Select a Texture Graph and a terrain with TerrainData.");
            if (resolution != 129 && resolution != 257 && resolution != 513) throw new ArgumentOutOfRangeException(nameof(resolution));
            if (!graph.HasSupportedDomainMetadata || (graph.Domain != UCGraphDomain.TextureMaterialShader && graph.Domain != UCGraphDomain.LegacyMixed)) // UC-T07-09: actual serialized Texture Graph domain.
                throw new ArgumentException("Select an explicit Texture Graph or migrate the legacy graph.");
            source = graph; this.cache = cache; world = selected.GetComponentInParent<UniverseCreatorObject>(true);
            if (selectedWorld != null && world != selectedWorld) throw new ArgumentException("Choose a terrain owned by the selected UC world.");
            run = world != null ? world.GenerationScheduler.CaptureRun() : default;
            stop = world != null ? world.GenerationScheduler.CreateStopToken(run) : new StopToken();
            graphCurrent = GraphPreviewScheduler.CaptureDependencies(graph, 4096);
            try
            {
                snapshot = GenerationSnapshot.CaptureForDomains(graph, NodeExecutionDomain.Worker | NodeExecutionDomain.MainThread | NodeExecutionDomain.MainThreadGpu, world, true);
                bool acceptedRequired = TextureFunctionCallNode.UsesAcceptedWorldInputs(snapshot.Graph);
                if (acceptedRequired && world == null) throw new InvalidOperationException("Bind this graph to a UC world and Run its Main Graph to publish accepted ground.");
                water = world != null ? WorldGraphWaterInput.Capture(world) : null;
                var bindings = world != null ? world.GetComponent<UCWorldGraphBindings>() : null;
                environment = bindings?.CaptureEnvironmentInputs(true);
                UnityEngine.Terrain[] targets = world != null ? Array.FindAll(world.GetComponentsInChildren<UnityEngine.Terrain>(true),
                    terrain => terrain.GetComponentInParent<UniverseCreatorObject>(true) == world) : new[] { selected }; // UC-T07-09: nested foreign worlds retain their own Apply authority.
                if (targets.Length == 0 || targets.Length > 256) throw new InvalidOperationException("Texture Run/Apply requires 1…256 loaded terrain regions.");
                var memory = cache.MemoryFor(world);
                foreach (UnityEngine.Terrain terrain in targets)
                {
                    if (terrain == null || terrain.terrainData == null) throw new InvalidOperationException("Restore missing TerrainData before applying the world texture graph.");
                    var tile = new TileData(Coord.Zero, new CoordRect(0, 0, resolution, resolution), terrain.transform.position, terrain.terrainData.size, terrain.terrainData.size.y);
                    tile.AttachMemory(memory); tile.AssignRun(run); tile.RetainGenerationInputs(snapshot);
                    if (world != null) tile.SetFullWorld(snapshot.Settings.worldPos, snapshot.Settings.worldSize); // Accepted world origin includes the authored origin offset.
                    var region = new Region { Terrain = terrain, Tile = tile, Position = terrain.transform.position, Size = terrain.terrainData.size };
                    regions.Add(region);
                    if (world != null && source.Domain == UCGraphDomain.TextureMaterialShader)
                    {
                        Vector3 centre = tile.worldPos + tile.worldSize * 0.5f;
                        if (!WorldGraphProducts.TryAcquireGround(world, null, centre, out var handle, out var diagnostic))
                            throw new InvalidOperationException(diagnostic);
                        tile.OwnGraphInput(handle);
                        if (!handle.TryGet(run, GraphProductUnits.WorldMetres, out region.Ground)) throw new InvalidOperationException("Run the Main Graph; this terrain's accepted ground is stale.");
                        if (bindings != null) bindings.BindAcceptedInputs(tile, world.graph, region.Ground, water, environment);
                        else tile.BindWorldGraphInputs(world.graph, region.Ground, water, environment);
                    }
                    else if (acceptedRequired) throw new InvalidOperationException("Migrate this world-input graph to the Texture domain before Run/Apply.");
                    Guid version = region.Ground?.GroundVersion ?? Guid.Empty;
                    tile.retained = cache.ForRegion(source, terrain, tile, version, () => InputsCurrent(region));
                    region.Evaluation = new IncrementalGraphEvaluation(snapshot.Graph, tile, stop);
                }
                Status = "Run: captured " + regions.Count + " terrain regions; applied materials are unchanged while baking.";
                active.Add(this);
            }
            catch { Dispose(); throw; }
        }

        private bool InputsCurrent(Region region)
        {
            if (region.Terrain == null || region.Terrain.terrainData == null || region.Terrain.transform.position != region.Position || region.Terrain.terrainData.size != region.Size) return false;
            if (world == null) return !run.IsAssigned;
            if (!world.GenerationScheduler.IsCurrent(run) || (water != null && !water.IsCurrent()) || (environment != null && !environment.IsCurrent())) return false;
            if (region.Ground == null) return true;
            Vector3 centre = region.Position + region.Size * 0.5f;
            return RiverChannelRegistry.TryGetCommittedSnapshot(world, centre.x, centre.z, out var current) && current.GroundVersion == region.Ground.GroundVersion;
        }

        internal void Pump()
        {
            if (disposed || IsComplete) return;
            try
            {
                if (stop.StopRequested) { Finish(false, "Texture bake cancelled; previously applied materials remain available."); return; }
                if (!graphCurrent()) { Finish(false, "Pending: graph/function settings changed. Run/Apply again to use the new revision."); return; }
                foreach (Region region in regions) if (!InputsCurrent(region)) { Finish(false, "Pending: accepted world inputs or terrain bounds changed. Run/Apply again."); return; }
                var slice = Stopwatch.StartNew();
                while (index < regions.Count && slice.Elapsed.TotalMilliseconds < 3 && !stop.StopRequested)
                {
                    Region region = regions[index];
                    region.Evaluation.StepNext();
                    if (region.Evaluation.IsComplete)
                    {
                        if (region.Tile.outputs.surface == null) throw new InvalidOperationException("Connect a Shader Output before applying terrain materials.");
                        region.Tile.outputs.surface.ValidateTextureChannels();
                        bool existing = region.Tile.outputs.surfaceShaderTarget == (int)ShaderTarget.ExistingMaterial;
                        Shader shader = existing ? region.Terrain.materialTemplate?.shader : Shader.Find(UCShaderProps.ShaderName);
                        if (shader == null) throw new InvalidOperationException("Assign the requested shader/material before Run/Apply.");
                        ShaderOutputDescriptor.ValidateProperties(region.Tile.outputs.surface, shader, existing);
                        index++;
                    }
                }
                if (index < regions.Count)
                {
                    var current = regions[index];
                    Status = "Baking region " + (index + 1) + "/" + regions.Count + ": " + current.Evaluation.Completed + "/" + current.Evaluation.Total + " node outputs. Cancel keeps applied materials.";
                    return;
                }
                // All graph/pixel work finishes before the commit. Unity's asset/terrain writes remain one
                // bounded main-thread commit, with no yield or cancellation between coherent region assignments.
                foreach (Region region in regions) cache.Accept(region.Terrain, region.Tile);
                var binding = world != null ? world.GetComponent<UCWorldGraphBindings>() : null;
                if (world != null && source.Domain == UCGraphDomain.TextureMaterialShader && binding == null) binding = Undo.AddComponent<UCWorldGraphBindings>(world.gameObject);
                foreach (Region region in regions)
                {
                    if (regions.Count > 1)
                    {
                        string owner = region.Ground != null ? region.Ground.OwnerId : regions.IndexOf(region).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        using (var digest = System.Security.Cryptography.SHA256.Create())
                        {
                            string suffix = BitConverter.ToString(digest.ComputeHash(System.Text.Encoding.UTF8.GetBytes(owner))).Replace("-", string.Empty);
                            // Separate region folders preserve spatially baked layer references; sharing one layer asset
                            // would overwrite every prior cell with the final cell's maps. The full owner digest is path-safe.
                            region.Tile.outputs.surfaceOutputFolder = ShaderOutputNode.SanitiseFolder(region.Tile.outputs.surfaceOutputFolder) + "/Region_" + suffix;
                        }
                    }
                    if (!UCTextureGraphApplier.Apply(region.Tile.outputs, region.Terrain, region.Tile.MemoryBudget, out SurfaceProduct surface))
                        throw new InvalidOperationException("Material Apply failed for " + region.Terrain.name + ". Inspect the graph's shader/resources and retry Run/Apply.");
                    if (binding != null && region.Ground != null && source.Domain == UCGraphDomain.TextureMaterialShader) binding.AcceptTexture(source, region.Ground, surface);
                    AppliedRegions++;
                }
                if (binding != null) EditorUtility.SetDirty(binding);
                AssetDatabase.SaveAssets();
                Finish(true, "Applied captured Texture Graph to " + AppliedRegions + " terrain regions in " + elapsed.Elapsed.TotalMilliseconds.ToString("F1") + " ms elapsed. Shader parameter edits can use their runtime parameter path.");
            }
            catch (Exception error) { Finish(false, error.Message + (AppliedRegions > 0 ? " " + AppliedRegions + " completed regions remain applied; retry the whole world." : string.Empty)); }
        }

        internal void Cancel() => stop?.RequestStop();
        internal static void CancelAll()
        {
            foreach (var bake in new List<TextureGraphBake>(active))
                bake.Finish(false, "Texture bake retired for scene/Play mode/reload/shutdown; Run/Apply again when ready."); // UC-T07-10: no pending GPU readback or partially evaluated tile survives.
        }
        private void Finish(bool success, string message) { Succeeded = success; IsComplete = true; Status = message; Dispose(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; stop?.RequestStop(); active.Remove(this);
            foreach (Region region in regions) region.Tile.ReleaseProductMemory(); snapshot?.Release();
        }
    }
}
