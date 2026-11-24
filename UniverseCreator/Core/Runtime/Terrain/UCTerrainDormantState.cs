// UC-T09-04: native terrain retirement keeps a versioned, lossless payload on its original holder.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>World-owned dormant terrain payload, attached only by ChunkManager on the main thread. Keeps actual heights, painting, holes, native foliage and terrain settings independently of generation.</summary>
    /// <remarks>Version 1 uses serialized flat arrays so a domain reload cannot erase retired terrain. Shared layers and prototypes remain borrowed. This is a bounded residency cache, not a compressed disk archive or a host-game save service.</remarks>
    [AddComponentMenu(""), DisallowMultipleComponent]
    public sealed class UCTerrainDormantState : MonoBehaviour
    {
        [SerializeField] private int version = 1;
        [SerializeField] private int heightResolution, alphaResolution, alphaLayers, detailResolution, detailPatchResolution, baseResolution;
        [SerializeField] private Vector3 terrainSize;
        [SerializeField] private float[] heights, alphamaps;
        [SerializeField] private bool[] holes;
        [SerializeField] private TerrainLayer[] layers;
        [SerializeField] private SavedTreePrototype[] treePrototypes;
        [SerializeField] private SavedTree[] trees;
        [SerializeField] private SavedDetailPrototype[] detailPrototypes;
        [SerializeField] private DetailGrid[] details;
        [SerializeField] private DetailScatterMode scatterMode;
        [SerializeField] private bool compressHoles;
        [SerializeField] private float grassAmount, grassSpeed, grassStrength;
        [SerializeField] private Color grassTint;

        [Serializable] private sealed class DetailGrid { public int[] Samples; }

        // UC-T09-04: Unity's native foliage types lack Serializable; explicit managed records survive reload.
        [Serializable] private struct SavedTreePrototype
        {
            public GameObject Prefab; public float BendFactor; public int NavMeshLod;
            internal TreePrototype Restore() => new TreePrototype { prefab = Prefab, bendFactor = BendFactor, navMeshLod = NavMeshLod };
        }
        [Serializable] private struct SavedTree
        {
            public Vector3 Position; public float WidthScale, HeightScale, Rotation;
            public int PrototypeIndex; public Color32 Color, LightmapColor;
            internal TreeInstance Restore() => new TreeInstance { position = Position, widthScale = WidthScale,
                heightScale = HeightScale, rotation = Rotation, prototypeIndex = PrototypeIndex, color = Color, lightmapColor = LightmapColor };
        }
        [Serializable] private struct SavedDetailPrototype
        {
            public GameObject Prototype; public Texture2D Texture;
            public float MinWidth, MaxWidth, MinHeight, MaxHeight, NoiseSpread, HolePadding, Density, Align, Jitter;
            public int NoiseSeed; public Color HealthyColor, DryColor; public DetailRenderMode RenderMode;
            public bool UseMesh, UseInstancing, UseDensityScaling;
            internal DetailPrototype Restore() => new DetailPrototype { prototype = Prototype, prototypeTexture = Texture,
                minWidth = MinWidth, maxWidth = MaxWidth, minHeight = MinHeight, maxHeight = MaxHeight,
                noiseSpread = NoiseSpread, noiseSeed = NoiseSeed, holeEdgePadding = HolePadding, density = Density,
                alignToGround = Align, positionJitter = Jitter, healthyColor = HealthyColor, dryColor = DryColor,
                renderMode = RenderMode, usePrototypeMesh = UseMesh, useInstancing = UseInstancing, useDensityScaling = UseDensityScaling };
        }

        internal long PayloadBytes => 4096L + (heights?.LongLength ?? 0) * 4 + (alphamaps?.LongLength ?? 0) * 4 +
            (holes?.LongLength ?? 0) + (trees?.LongLength ?? 0) * 64 + DetailBytes();

        // UC-T09-04: a wide multilayer alpha row can exceed the ordinary 256 KiB strip cap.
        internal long UploadScratchBytes => Math.Max(256 * 1024L, (long)alphaResolution * alphaLayers * 4);

        private long DetailBytes()
        {
            long bytes = 0;
            if (details != null) foreach (var detail in details) bytes += (detail?.Samples?.LongLength ?? 0) * 4 + 64;
            return bytes;
        }

        // The reservation includes a full getter result while it is flattened. Native calls are indivisible and measured by the existing pump.
        internal static long EstimateCaptureBytes(TerrainData data)
        {
            long h = (long)data.heightmapResolution * data.heightmapResolution * 4;
            long a = (long)data.alphamapWidth * data.alphamapHeight * data.alphamapLayers * 4;
            long hole = (long)data.holesResolution * data.holesResolution;
            long d = (long)data.detailWidth * data.detailHeight * 4;
            long t = (long)data.treeInstanceCount * 64;
            return checked(8192 + h + a + hole + d * data.detailPrototypes.Length + t + Math.Max(Math.Max(h, a), Math.Max(d, hole)));
        }

        internal static UCTerrainDormantState Capture(Terrain terrain)
        {
            TerrainData source = terrain.terrainData;
            if (source == null) throw new ArgumentException("Dormant capture requires attached native terrain.", nameof(terrain));
            var state = terrain.gameObject.AddComponent<UCTerrainDormantState>();
            state.hideFlags = HideFlags.HideInInspector; // Keep serialization; HideAndDontSave would lose the only retired surface on reload.
            try
            {
                state.heightResolution = source.heightmapResolution; state.terrainSize = source.size;
                state.alphaResolution = source.alphamapResolution; state.alphaLayers = source.alphamapLayers;
                state.layers = source.terrainLayers; state.baseResolution = source.baseMapResolution;
                state.compressHoles = source.enableHolesTextureCompression;
                state.detailResolution = source.detailResolution; state.detailPatchResolution = source.detailResolutionPerPatch;
                state.scatterMode = source.detailScatterMode;
                state.detailPrototypes = Array.ConvertAll(source.detailPrototypes, p => new SavedDetailPrototype {
                    Prototype = p.prototype, Texture = p.prototypeTexture, MinWidth = p.minWidth, MaxWidth = p.maxWidth,
                    MinHeight = p.minHeight, MaxHeight = p.maxHeight, NoiseSpread = p.noiseSpread, NoiseSeed = p.noiseSeed,
                    HolePadding = p.holeEdgePadding, Density = p.density, Align = p.alignToGround, Jitter = p.positionJitter,
                    HealthyColor = p.healthyColor, DryColor = p.dryColor, RenderMode = p.renderMode, UseMesh = p.usePrototypeMesh,
                    UseInstancing = p.useInstancing, UseDensityScaling = p.useDensityScaling });
                state.treePrototypes = Array.ConvertAll(source.treePrototypes, p => new SavedTreePrototype {
                    Prefab = p.prefab, BendFactor = p.bendFactor, NavMeshLod = p.navMeshLod });
                state.trees = Array.ConvertAll(source.treeInstances, t => new SavedTree { Position = t.position,
                    WidthScale = t.widthScale, HeightScale = t.heightScale, Rotation = t.rotation,
                    PrototypeIndex = t.prototypeIndex, Color = t.color, LightmapColor = t.lightmapColor });
                state.grassAmount = source.wavingGrassAmount; state.grassSpeed = source.wavingGrassSpeed;
                state.grassStrength = source.wavingGrassStrength; state.grassTint = source.wavingGrassTint;
                state.heights = Flatten<float>(source.GetHeights(0, 0, state.heightResolution, state.heightResolution), 4);
                state.holes = Flatten<bool>(source.GetHoles(0, 0, source.holesResolution, source.holesResolution), 1);
                state.alphamaps = state.alphaLayers == 0 ? Array.Empty<float>() :
                    Flatten<float>(source.GetAlphamaps(0, 0, state.alphaResolution, state.alphaResolution), 4);
                state.details = new DetailGrid[state.detailPrototypes.Length];
                for (int i = 0; i < state.details.Length; i++)
                    state.details[i] = new DetailGrid { Samples = state.detailResolution == 0 ? Array.Empty<int>() :
                        Flatten<int>(source.GetDetailLayer(0, 0, state.detailResolution, state.detailResolution, i), 4) };
                return state;
            }
            catch { DestroyState(state); throw; } // Native source still belongs to the terrain; a failed capture cannot retire it.
        }

        private static T[] Flatten<T>(Array values, int elementBytes)
        {
            var result = new T[values.Length]; Buffer.BlockCopy(values, 0, result, 0, checked(values.Length * elementBytes));
            return result;
        }

        internal void ValidatePayload()
        {
            bool h = heightResolution >= 33 && heightResolution <= 4097 &&
                ((heightResolution - 1) & (heightResolution - 2)) == 0;
            if (version != 1 || !h || !FinitePositive(terrainSize.x) || !FinitePositive(terrainSize.y) || !FinitePositive(terrainSize.z) ||
                heights == null || heights.LongLength != (long)heightResolution * heightResolution ||
                holes == null || holes.LongLength != (long)(heightResolution - 1) * (heightResolution - 1) ||
                alphaResolution < 16 || alphaResolution > 4096 || (alphaResolution & (alphaResolution - 1)) != 0 || alphaLayers < 0 || alphaLayers > 64 ||
                alphamaps == null || alphamaps.LongLength != (long)alphaResolution * alphaResolution * alphaLayers ||
                layers == null || layers.Length != alphaLayers || treePrototypes == null || trees == null ||
                detailPrototypes == null || details == null || details.Length != detailPrototypes.Length ||
                detailResolution < 0 || detailResolution > 4048 || detailPatchResolution < 8 || detailPatchResolution > 128 ||
                baseResolution < 16 || baseResolution > 4096 || (baseResolution & (baseResolution - 1)) != 0 ||
                (scatterMode != DetailScatterMode.InstanceCountMode && scatterMode != DetailScatterMode.CoverageMode))
                throw new InvalidOperationException("Dormant terrain payload version or dimensions are invalid; native restoration was refused.");
            foreach (var detail in details)
                if (detail?.Samples == null || detail.Samples.LongLength != (long)detailResolution * detailResolution)
                    throw new InvalidOperationException("Dormant detail samples do not match the saved native grid.");
        }

        private static bool FinitePositive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0;

        // UC-T09-04: distant rendering borrows the same saved geometry, without publishing another ground/collision authority.
        internal long FarNativeBytes => checked(8192L + (long)heightResolution * heightResolution * 8 +
            (long)(heightResolution - 1) * (heightResolution - 1) * 2 +
            (long)Math.Min(128, alphaResolution) * Math.Min(128, alphaResolution) * Math.Max(1, alphaLayers) * 8 + 128L * 128 * 8);

        internal IEnumerable<ChunkCommitStep> RestoreStages(TerrainData target) => RestoreStages(target, false);
        internal IEnumerable<ChunkCommitStep> RestoreFarStages(TerrainData target) => RestoreStages(target, true);

        private IEnumerable<ChunkCommitStep> RestoreStages(TerrainData target, bool far)
        {
            ValidatePayload();
            target.heightmapResolution = heightResolution; target.size = terrainSize;
            target.alphamapResolution = far ? Math.Min(128, alphaResolution) : alphaResolution;
            target.baseMapResolution = far ? Math.Min(128, baseResolution) : baseResolution;
            target.terrainLayers = layers; target.enableHolesTextureCompression = compressHoles;
            target.SetDetailResolution(far ? 0 : detailResolution, detailPatchResolution); target.SetDetailScatterMode(scatterMode);
            target.detailPrototypes = far ? Array.Empty<DetailPrototype>() : Array.ConvertAll(detailPrototypes, p => p.Restore());
            target.treePrototypes = far ? Array.Empty<TreePrototype>() : Array.ConvertAll(treePrototypes, p => p.Restore());
            target.wavingGrassAmount = grassAmount; target.wavingGrassSpeed = grassSpeed;
            target.wavingGrassStrength = grassStrength; target.wavingGrassTint = grassTint;
            yield return new ChunkCommitStep(ChunkCommitStage.AllocateTerrain);
            foreach (var step in UploadGrid(target, heights, heightResolution, heightResolution, false)) yield return step;
            int holeSide = heightResolution - 1;
            for (int z = 0; z < holeSide; z += 32)
            {
                int rows = Math.Min(32, holeSide - z); var strip = new bool[rows, holeSide];
                Buffer.BlockCopy(holes, z * holeSide, strip, 0, rows * holeSide); target.SetHoles(0, z, strip);
                yield return new ChunkCommitStep(ChunkCommitStage.UploadHoles, (long)rows * holeSide);
            }
            if (alphaLayers > 0)
            {
                if (far && alphaResolution > 128)
                { foreach (var step in UploadFarAlphas(target)) yield return step; }
                else
                { foreach (var step in UploadGrid(target, alphamaps, alphaResolution, alphaResolution, true)) yield return step; }
            }
            int detailRows = Math.Max(1, Math.Min(32, 256 * 1024 / Math.Max(4, detailResolution * 4)));
            for (int layer = 0; !far && layer < details.Length; layer++)
                for (int z = 0; z < detailResolution; z += detailRows)
                {
                    int rows = Math.Min(detailRows, detailResolution - z); var strip = new int[rows, detailResolution];
                    Buffer.BlockCopy(details[layer].Samples, z * detailResolution * 4, strip, 0, rows * detailResolution * 4);
                    target.SetDetailLayer(0, z, layer, strip);
                    yield return new ChunkCommitStep(ChunkCommitStage.Rescatter, (long)rows * detailResolution * 4);
                }
            target.SyncHeightmap();
            yield return new ChunkCommitStep(ChunkCommitStage.SyncHeightmap);
            // UC-T09-04: SyncHeightmap resnaps existing trees, so restore authored transforms AFTER it.
            if (!far)
            {
                target.SetTreeInstances(Array.ConvertAll(trees, t => t.Restore()), false);
                yield return new ChunkCommitStep(ChunkCommitStage.Rescatter);
            }
        }

        private IEnumerable<ChunkCommitStep> UploadFarAlphas(TerrainData target)
        {
            // Area filtering preserves small painted contributions; the source grid is a power-of-two multiple of 128.
            const int size = 128;
            int scale = alphaResolution / size, reads = 0;
            int rowsPerStrip = Math.Max(1, Math.Min(32, 256 * 1024 / (size * alphaLayers * 4)));
            for (int z = 0; z < size; z += rowsPerStrip)
            {
                int rows = Math.Min(rowsPerStrip, size - z); var strip = new float[rows, size, alphaLayers];
                for (int row = 0; row < rows; row++) for (int x = 0; x < size; x++)
                    for (int layer = 0; layer < alphaLayers; layer++)
                    {
                        double total = 0;
                        for (int dz = 0; dz < scale; dz++) for (int dx = 0; dx < scale; dx++)
                        {
                            total += alphamaps[(((z + row) * scale + dz) * alphaResolution + x * scale + dx) * alphaLayers + layer];
                            if (++reads == 65536)
                            { yield return new ChunkCommitStep(ChunkCommitStage.PrepareTextures, reads * 4L); reads = 0; }
                        }
                        strip[row, x, layer] = (float)(total / ((long)scale * scale));
                    }
                target.SetAlphamaps(0, z, strip);
                yield return new ChunkCommitStep(ChunkCommitStage.UploadTextures, (long)rows * size * alphaLayers * 4);
            }
        }

        private IEnumerable<ChunkCommitStep> UploadGrid(TerrainData target, float[] samples, int width, int height, bool alpha)
        {
            int channels = alpha ? alphaLayers : 1;
            int rowBytes = checked(width * channels * 4), rowsPerStrip = Math.Max(1, Math.Min(32, 256 * 1024 / rowBytes));
            for (int z = 0; z < height; z += rowsPerStrip)
            {
                int rows = Math.Min(rowsPerStrip, height - z);
                if (alpha)
                {
                    var strip = new float[rows, width, channels]; Buffer.BlockCopy(samples, z * rowBytes, strip, 0, rows * rowBytes);
                    target.SetAlphamaps(0, z, strip);
                }
                else
                {
                    var strip = new float[rows, width]; Buffer.BlockCopy(samples, z * rowBytes, strip, 0, rows * rowBytes);
                    target.SetHeightsDelayLOD(0, z, strip);
                }
                yield return new ChunkCommitStep(alpha ? ChunkCommitStage.UploadTextures : ChunkCommitStage.UploadHeights, (long)rows * rowBytes);
            }
        }

        internal static void DestroyState(UCTerrainDormantState state)
        {
            if (state == null) return;
            // Drop serialized arrays before deferred component destruction; the memory reservation can then retire honestly.
            state.heights = null; state.holes = null; state.alphamaps = null; state.details = null; state.trees = null;
            if (Application.isPlaying) Destroy(state); else DestroyImmediate(state);
        }
    }
}
