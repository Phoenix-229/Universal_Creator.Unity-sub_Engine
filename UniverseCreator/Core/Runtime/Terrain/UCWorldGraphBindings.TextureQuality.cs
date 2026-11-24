// UC-T07-11: the existing world binding applies reversible view quality after explicit material Apply; no terrain rebuild or shared-material mutation.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    public sealed partial class UCWorldGraphBindings
    {
        [SerializeField, Tooltip("Optional existing AdaptiveQuality source. Empty uses this world's component; absent/disabled sources preserve authored material quality.")]
        private AdaptiveQuality textureQualitySource;
        private readonly Dictionary<UnityEngine.Terrain, TextureQualityState> textureQualityTerrains = new Dictionary<UnityEngine.Terrain, TextureQualityState>();
        private readonly List<UnityEngine.Terrain> retiredTextureQuality = new List<UnityEngine.Terrain>();
        private MaterialPropertyBlock textureQualityBlock;
        private ProductMemoryBudget.Lease textureQualityMemory;
        private int lastTextureSampling = int.MinValue;
        private float nextTextureRetirement;
        private static readonly int textureSamplingProperty = Shader.PropertyToID(UCShaderProps.TextureQuality);

        /// <summary>Current per-terrain material sampling state; absent telemetry/controllers preserve authored quality.</summary>
        public string TextureQualityStatus { get; private set; } = "Pending explicit Texture Graph Apply.";

        private sealed class TextureQualityState
        {
            internal Material Material;
            internal float Original;
            internal int Applied = -1;
        }

        internal void RegisterTextureQuality(UnityEngine.Terrain terrain)
        {
            GraphExecutionGuard.RequireMainThread("Texture quality registration");
            Material material = terrain != null ? terrain.materialTemplate : null;
            if (material == null || !material.HasProperty(textureSamplingProperty)) return;
            if (terrain.GetComponentInParent<UniverseCreatorObject>(true) != Owner) throw new ArgumentException("Texture quality terrain belongs to another world."); // UC-T07-11: loaded inactive regions retain the same owner as texture Apply.
            if (textureQualityBlock == null)
            {
                textureQualityMemory = Owner.ProductMemory.Reserve(32768, 2048); // One reusable native property block; Unity copies its scalar into each terrain consumer.
                textureQualityBlock = new MaterialPropertyBlock();
            }
            if (!textureQualityTerrains.TryGetValue(terrain, out var state))
            {
                if (textureQualityTerrains.Count == 256) throw new InvalidOperationException("Texture quality exceeds the 256-terrain binding budget.");
                state = new TextureQualityState();
                textureQualityTerrains.Add(terrain, state);
            }
            else RestoreTextureSampling(terrain, state);
            textureQualityBlock.Clear(); terrain.GetSplatMaterialPropertyBlock(textureQualityBlock);
            // UC-T07-11: an absent block value inherits the authored material; GetFloat alone would mistake it for zero.
            state.Original = textureQualityBlock.HasFloat(textureSamplingProperty) ? textureQualityBlock.GetFloat(textureSamplingProperty) : material.GetFloat(textureSamplingProperty);
            state.Material = material; state.Applied = -1; lastTextureSampling = int.MinValue;
            if (textureQualitySource == null) textureQualitySource = Owner.GetComponent<AdaptiveQuality>();
        }

        private void InitializeTextureQuality()
        {
            if (!Application.isPlaying || Owner == null) return;
            foreach (var terrain in Owner.GetComponentsInChildren<UnityEngine.Terrain>(true)) RegisterTextureQuality(terrain);
        }

        private void LateUpdate()
        {
            if (textureQualityTerrains.Count == 0) return;
            int sampling = textureQualitySource != null && textureQualitySource.isActiveAndEnabled ?
                textureQualitySource.CurrentTier == AdaptiveQuality.QualityTier.Low ? 2 :
                textureQualitySource.CurrentTier == AdaptiveQuality.QualityTier.Medium ? 1 : 0 : -1;
            if (sampling == lastTextureSampling && Time.unscaledTime < nextTextureRetirement) return;
            nextTextureRetirement = Time.unscaledTime + 1; // Retire moved/destroyed streamed terrains even when measured quality is unchanged.
            retiredTextureQuality.Clear();
            foreach (var pair in textureQualityTerrains)
            {
                var terrain = pair.Key; var state = pair.Value;
                if (terrain == null || terrain.materialTemplate != state.Material) { RestoreTextureSampling(terrain, state); retiredTextureQuality.Add(terrain); continue; }
                if (sampling == state.Applied) continue;
                textureQualityBlock.Clear(); terrain.GetSplatMaterialPropertyBlock(textureQualityBlock);
                textureQualityBlock.SetFloat(textureSamplingProperty, sampling < 0 ? state.Original : Mathf.Max(state.Original, sampling));
                terrain.SetSplatMaterialPropertyBlock(textureQualityBlock); state.Applied = sampling;
            }
            foreach (var terrain in retiredTextureQuality) textureQualityTerrains.Remove(terrain);
            retiredTextureQuality.Clear(); lastTextureSampling = sampling;
            TextureQualityStatus = sampling < 0 ? "No active quality source; authored sampling is preserved." :
                sampling == 0 ? "Full authored texture sampling." : sampling == 1 ? "Measured tier: projected normal samples reduced; material coverage and scale retained." :
                "Measured tier: cliff/snow tint retained with reduced map sampling; material coverage and scale retained.";
        }

        private void RestoreTextureSampling(UnityEngine.Terrain terrain, TextureQualityState state)
        {
            if (terrain == null || state.Applied < 0 || textureQualityBlock == null) return;
            textureQualityBlock.Clear(); terrain.GetSplatMaterialPropertyBlock(textureQualityBlock);
            if (Mathf.Approximately(textureQualityBlock.GetFloat(textureSamplingProperty), Mathf.Max(state.Original, state.Applied)))
            { textureQualityBlock.SetFloat(textureSamplingProperty, state.Original); terrain.SetSplatMaterialPropertyBlock(textureQualityBlock); }
        }

        private void RetireTextureQuality()
        {
            foreach (var pair in textureQualityTerrains) RestoreTextureSampling(pair.Key, pair.Value);
            textureQualityTerrains.Clear(); retiredTextureQuality.Clear(); lastTextureSampling = int.MinValue; nextTextureRetirement = 0;
            textureQualityBlock?.Clear(); textureQualityBlock = null;
            textureQualityMemory?.Dispose(); textureQualityMemory = null;
        }
    }
}
