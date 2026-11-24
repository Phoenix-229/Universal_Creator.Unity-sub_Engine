// UC-T06-10: main-thread commit templates preserve submeshes, hierarchy transforms and authored LOD bands.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem
{
    public static partial class ScatterApplier
    {
        internal sealed partial class FinalGroundContext
        {
            private readonly Dictionary<GameObject, PrefabDrawTemplate> drawTemplates = new Dictionary<GameObject, PrefabDrawTemplate>();

            internal bool TryAddInstancedPrefab(GPUInstancingManager manager, int owner, GameObject prefab, Vector3 position,
                Quaternion rotation, Vector3 scale, Transform parent, float shadowDistance, float cullDistance, ShrubPrototypeDef appearance)
            {
                if (!drawTemplates.TryGetValue(prefab, out var template))
                { template = PrefabDrawTemplate.Capture(prefab); drawTemplates.Add(prefab, template); }
                if (template == null) return false;
                Matrix4x4 rootMatrix = Matrix4x4.TRS(position, rotation, Vector3.Scale(prefab.transform.localScale, scale));
                if (parent != null) // UC-T06-10: match Instantiate(world pose, parent) followed by the existing authored local-scale assignment.
                    rootMatrix = parent.localToWorldMatrix * Matrix4x4.TRS(parent.InverseTransformPoint(position),
                        Quaternion.Inverse(parent.rotation) * rotation, Vector3.Scale(prefab.transform.localScale, scale));
                int layer = parent != null ? parent.gameObject.layer : prefab.layer;
                // Full source validation precedes registration; a missing later slot must not produce half a plant plus a fallback clone.
                foreach (var draw in template.draws)
                    if (draw.mesh == null || draw.material == null || !draw.material.enableInstancing || draw.submesh >= draw.mesh.subMeshCount) return false;
                foreach (var draw in template.draws) // UC-T06-10: reject an invalid later transform/slot before any native registration.
                {
                    Matrix4x4 groupMatrix = rootMatrix * draw.groupLocal;
                    GPUInstancingManager.ValidateRecord(rootMatrix * draw.childLocal, layer, shadowDistance, cullDistance,
                        draw.mesh, draw.submesh, draw.lodRange, groupMatrix.MultiplyPoint3x4(draw.groupCentre),
                        draw.groupSize * LargestScale(groupMatrix));
                }
                foreach (var draw in template.draws)
                {
                    Matrix4x4 groupMatrix = rootMatrix * draw.groupLocal;
                    float size = draw.groupSize * LargestScale(groupMatrix);
                    manager.RegisterInstance(owner, draw.mesh, draw.material, rootMatrix * draw.childLocal, layer,
                        draw.shadows, draw.receiveShadows, shadowDistance, cullDistance, appearance, draw.submesh,
                        draw.lodRange, groupMatrix.MultiplyPoint3x4(draw.groupCentre), size);
                }
                return true;
            }

            private static float LargestScale(Matrix4x4 matrix)
            { Vector3 scale = matrix.lossyScale; return Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))); }
        }

        private sealed class PrefabDrawTemplate
        {
            internal readonly List<PrefabDraw> draws = new List<PrefabDraw>();

            internal static PrefabDrawTemplate Capture(GameObject prefab)
            {
                var renderers = new List<Renderer>(); var groups = new List<LODGroup>();
                int remaining = 4096;
                Collect(prefab.transform, true, 0, ref remaining, renderers, groups);
                if (renderers.Count == 0 || groups.Count > 1) return null;
                var bands = new Dictionary<Renderer, Vector2>();
                LODGroup group = groups.Count == 1 ? groups[0] : null;
                if (group != null)
                {
                    // Native billboard and crossfade shaders depend on Unity-owned per-renderer state; retain that supported path.
                    if (group.fadeMode != LODFadeMode.None || !Finite(group.size) || group.size <= 0f || !Finite(group.localReferencePoint)) return null;
                    var lods = group.GetLODs();
                    if (lods.Length == 0 || lods.Length > 8) return null;
                    float upper = float.MaxValue;
                    foreach (var lod in lods)
                    {
                        float lower = lod.screenRelativeTransitionHeight;
                        if (!Finite(lower) || lower < 0f || lower > 1f || lower >= upper || lod.renderers == null || lod.renderers.Length == 0) return null;
                        foreach (var renderer in lod.renderers)
                        {
                            if (renderer == null || !renderers.Contains(renderer) || bands.ContainsKey(renderer)) return null;
                            bands.Add(renderer, new Vector2(lower, upper));
                        }
                        upper = lower;
                    }
                }
                var result = new PrefabDrawTemplate();
                foreach (var renderer in renderers)
                {
                    if (!(renderer is MeshRenderer meshRenderer)) return null;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) return null;
                    var materials = renderer.sharedMaterials;
                    // Unity can draw extra material passes on the final submesh; delegate those layouts intact rather than guessing.
                    if (materials.Length == 0 || materials.Length != filter.sharedMesh.subMeshCount) return null;
                    bool usesLod = bands.TryGetValue(renderer, out var band);
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        if (materials[slot] == null || !materials[slot].enableInstancing) return null;
                        result.draws.Add(new PrefabDraw { mesh = filter.sharedMesh, material = materials[slot], submesh = slot,
                            childLocal = prefab.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                            groupLocal = usesLod ? prefab.transform.worldToLocalMatrix * group.transform.localToWorldMatrix : Matrix4x4.identity,
                            groupCentre = usesLod ? group.localReferencePoint : Vector3.zero, groupSize = usesLod ? group.size : 0f,
                            lodRange = usesLod ? band : Vector2.zero, shadows = meshRenderer.shadowCastingMode, receiveShadows = meshRenderer.receiveShadows });
                    }
                }
                return result;
            }

            private static void Collect(Transform node, bool ancestorsActive, int depth, ref int remaining, List<Renderer> renderers, List<LODGroup> groups)
            {
                if (depth > 128 || --remaining < 0) throw new InvalidOperationException("Scatter draw template exceeds 4,096 transforms or 128 hierarchy levels.");
                bool active = ancestorsActive && node.gameObject.activeSelf;
                if (active)
                {
                    var group = node.GetComponent<LODGroup>(); if (group != null && group.enabled) groups.Add(group);
                    foreach (var renderer in node.GetComponents<Renderer>()) if (renderer.enabled) renderers.Add(renderer);
                }
                for (int i = 0; i < node.childCount; i++) Collect(node.GetChild(i), active, depth + 1, ref remaining, renderers, groups);
            }
        }

        private struct PrefabDraw
        {
            internal Mesh mesh;
            internal Material material;
            internal int submesh;
            internal Matrix4x4 childLocal, groupLocal;
            internal Vector3 groupCentre;
            internal float groupSize;
            internal Vector2 lodRange;
            internal UnityEngine.Rendering.ShadowCastingMode shadows;
            internal bool receiveShadows;
        }
    }
}
