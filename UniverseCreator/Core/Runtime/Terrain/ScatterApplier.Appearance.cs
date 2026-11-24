// UC-T06-09: retain exact native species provenance and update appearance without re-emitting instances.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.TerrainSystem
{
    public static partial class ScatterApplier
    {
        private sealed class TreeAppearanceSources
        {
            internal readonly TreePrototypeDef[] definitions;
            internal TreeAppearanceSources(TreePrototypeDef[] definitions) { this.definitions = definitions; }
        }

        // Weak keys do not keep retired native terrain alive. The value never references its TerrainData key.
        private static readonly ConditionalWeakTable<TerrainData, TreeAppearanceSources> treeAppearanceSources = new ConditionalWeakTable<TerrainData, TreeAppearanceSources>();

        private static void RecordTreeAppearance(TerrainData data, List<TreePrototypeDef> sources)
        {
            treeAppearanceSources.Remove(data);
            if (sources != null) treeAppearanceSources.Add(data, new TreeAppearanceSources(sources.ToArray()));
        }

        // Only these envelope fields are absent from the inspected placement/habitat/allometry callers.
        internal static EcoEnvelope PlacementEnvelope(EcoEnvelope source)
        {
            if (source == null) return null;
            var copy = JsonUtility.FromJson<EcoEnvelope>(JsonUtility.ToJson(source));
            ClearEnvelopeAppearance(copy);
            return copy;
        }

        private static void ClearEnvelopeAppearance(EcoEnvelope copy)
        {
            if (copy == null) return;
            copy.deciduous = false; copy.budburstGdd = 0; copy.senescenceDayLengthH = 0;
            copy.springColour = copy.summerColour = copy.autumnColour = copy.winterColour = Color.clear;
            copy.leafOffFraction = copy.snowCanopyAccumulation = copy.wetDarkening = 0;
            copy.seasonalHeightScale = Vector2.zero;
        }

        // Authoring-time only: normalize a detached copy, preserving every placement, selection and resource input.
        internal static ulong PlacementLibraryHash(ScatterPrototypeLibrary library)
        {
            if (library == null) return 0;
            var copy = JsonUtility.FromJson<ScatterPrototypeLibrary>(JsonUtility.ToJson(library));
            foreach (var def in copy.trees) if (def != null) ClearEnvelopeAppearance(def.eco);
            foreach (var def in copy.shrubs) if (def != null) { ClearEnvelopeAppearance(def.eco); def.windResponse = def.flutter = def.meshHeight = 0; }
            foreach (var def in copy.details) if (def != null) { ClearEnvelopeAppearance(def.eco); def.healthyColour = def.dryColour = Color.clear; }
            foreach (var def in copy.objects) if (def != null) ClearEnvelopeAppearance(def.eco);
            string json = JsonUtility.ToJson(copy);
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (char c in json) { hash ^= c; hash *= 1099511628211UL; } }
            return hash;
        }

        internal sealed class AppearanceUpdate
        {
            private readonly TerrainData data;
            private readonly TreeAppearanceSources sources;
            private readonly Dictionary<TreePrototypeDef, Color> colours = new Dictionary<TreePrototypeDef, Color>();
            private int cursor;

            internal AppearanceUpdate(Terrain terrain, TerrainData data, ScatterPrototypeLibrary library, UCVegetationSystemManager vegetation)
            {
                this.data = data;
                treeAppearanceSources.TryGetValue(data, out sources);
                Vector3 centre = terrain.GetPosition() + new Vector3(data.size.x * 0.5f, 0, data.size.z * 0.5f);
                float altitude = terrain.GetPosition().y + terrain.SampleHeight(centre);
                if (sources != null)
                    foreach (var definition in library.trees)
                        if (definition != null && !colours.ContainsKey(definition))
                        {
                            Color colour = vegetation.enableSeasonalPhenology ? vegetation.SeasonalAppearance(definition.eco, altitude) : Color.white;
                            ValidateAppearanceColour(colour); colours.Add(definition, colour);
                        }

                // Keep native detail geometry, noise, roster and density grids unchanged during a clock/tint refresh.
                var details = data.detailPrototypes;
                int slot = 0; bool changed = false;
                foreach (var definition in library.details)
                {
                    if (definition == null || (definition.prefabMesh == null && definition.billboardTexture == null)) continue;
                    if (slot >= details.Length) break;
                    var detail = details[slot++];
                    if (detail == null) continue;
                    if (detail.prototype != definition.prefabMesh || detail.prototypeTexture != definition.billboardTexture) continue;
                    Color healthy = definition.healthyColour, dry = definition.dryColour;
                    if (vegetation.enableSeasonalPhenology && definition.eco != null && !DetailShaderOwnsSeason(definition, vegetation))
                    {
                        var state = vegetation.PhenologyFor(definition.eco, altitude);
                        healthy = vegetation.SeasonalAppearance(definition.eco, altitude);
                        dry = Phenology.SnowBlend(Phenology.WetDarken(dry, vegetation.ResolvedWetness, definition.eco.wetDarkening),
                            Phenology.CanopySnowFraction(vegetation.SnowCoverFractionAt(altitude), definition.eco.snowCanopyAccumulation, state.canopyFraction));
                    }
                    ValidateAppearanceColour(healthy); ValidateAppearanceColour(dry);
                    if (detail.healthyColor == healthy && detail.dryColor == dry) continue;
                    detail.healthyColor = healthy; detail.dryColor = dry; changed = true;
                }
                if (changed) data.detailPrototypes = details;
                ApplyDetailWindClass(library);
            }

            // Unity 6000.5 TerrainModule.xml confirms colour-only SetTreeInstance leaves position/prototype unchanged.
            internal bool Step(int maximumTrees)
            {
                if (maximumTrees <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTrees));
                if (data == null || sources == null || data.treeInstanceCount != sources.definitions.Length ||
                    !treeAppearanceSources.TryGetValue(data, out var current) || !ReferenceEquals(current, sources)) return true;
                int end = (int)Math.Min((long)cursor + maximumTrees, data.treeInstanceCount);
                while (cursor < end)
                {
                    int index = cursor++;
                    var definition = sources.definitions[index];
                    if (definition == null || !colours.TryGetValue(definition, out Color colour)) continue;
                    var instance = data.GetTreeInstance(index);
                    Color32 wanted = colour;
                    if (instance.color.r == wanted.r && instance.color.g == wanted.g && instance.color.b == wanted.b && instance.color.a == wanted.a) continue;
                    instance.color = colour;
                    data.SetTreeInstance(index, instance);
                }
                return cursor >= data.treeInstanceCount;
            }
        }

        private static void ValidateAppearanceColour(Color colour)
        {
            if (!Finite(colour.r) || !Finite(colour.g) || !Finite(colour.b) || !Finite(colour.a))
                throw new InvalidOperationException("Vegetation appearance colours must contain finite components.");
        }
    }
}
