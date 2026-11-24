// UC-T06-13: inspect real selected content explicitly; repaint never reads vertex arrays or scans asset folders.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Editor.Diagnostics;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    public static partial class UCVegetationValidation
    {
        internal sealed class PrototypeContent
        {
            internal UnityEngine.Object source;
            internal GameObject prefab;
            internal UCScatterKind kind;
            internal int slot;
            internal float baseY, heightM;
            internal bool invalid;
            internal string assetPath, digest;
            internal readonly List<string> notes = new List<string>();
        }

        internal sealed class PrototypeContentReport
        {
            internal readonly List<PrototypeContent> prototypes = new List<PrototypeContent>();
            internal bool truncated;
            internal int errors;

            internal string Describe()
            {
                var text = new StringBuilder("UC-T06-13 prototype content inspection\n");
                text.AppendLine("Existing buyer/project content is referenced. Source redistribution licences have not been verified by this tool.");
                foreach (var item in prototypes)
                {
                    text.AppendLine(item.kind + " slot " + item.slot + ": " + item.assetPath + "; dependency hash " + item.digest +
                        "; root-local base " + item.baseY + " m; authored native height " + item.heightM + " m.");
                    foreach (string note in item.notes) text.AppendLine("  " + note);
                }
                if (truncated) text.AppendLine("Inspection exceeded its 256-slot envelope; remaining content requires a separate selected subset.");
                text.AppendLine("Geometry/material inputs are inspected; rendered normals, wind motion, canopy/grass coverage and LOD transitions require the reference scene and device checks.");
                return text.ToString();
            }
        }

        private static readonly ConditionalWeakTable<UniverseCreatorObject, PrototypeContentReport> prototypeReports =
            new ConditionalWeakTable<UniverseCreatorObject, PrototypeContentReport>();

        /// <summary>Inspects up to 256 real library slots, retaining empty slots and bounded structural/material guidance.</summary>
        /// <param name="library">Selected world's borrowed prototype library; null returns an empty report.</param>
        /// <returns>Detached read-only evidence; errors/limits never repair or mutate source content.</returns>
        internal static PrototypeContentReport InspectPrototypeContent(ScatterPrototypeLibrary library)
        {
            var report = new PrototypeContentReport();
            if (library == null) return report;
            report.truncated = (long)library.trees.Count + library.shrubs.Count + library.details.Count + library.objects.Count > 256;
            for (int i = 0; i < library.trees.Count && report.prototypes.Count < 256; i++) Add(library.trees[i]?.prefab, UCScatterKind.Tree, i);
            for (int i = 0; i < library.shrubs.Count && report.prototypes.Count < 256; i++) Add(library.shrubs[i]?.prefab, UCScatterKind.Shrub, i);
            for (int i = 0; i < library.details.Count && report.prototypes.Count < 256; i++)
                Add(library.details[i]?.prefabMesh != null ? (UnityEngine.Object)library.details[i].prefabMesh : library.details[i]?.billboardTexture, UCScatterKind.Detail, i);
            for (int i = 0; i < library.objects.Count && report.prototypes.Count < 256; i++) Add(library.objects[i]?.prefab, UCScatterKind.Object, i);
            return report;

            void Add(UnityEngine.Object source, UCScatterKind kind, int slot)
            {
                if (report.prototypes.Count >= 256) { report.truncated = true; return; }
                var item = InspectPrototype(source, kind, slot); report.prototypes.Add(item);
                if (item.invalid) report.errors++;
            }
        }

        private static PrototypeContent InspectPrototype(UnityEngine.Object source, UCScatterKind kind, int slot)
        {
            var item = new PrototypeContent { source = source, prefab = source as GameObject, kind = kind, slot = slot,
                assetPath = source != null ? AssetDatabase.GetAssetPath(source) : "Empty slot", digest = "unsaved/unavailable" };
            if (source == null) { item.invalid = true; item.notes.Add("Assign the category's mesh/prefab or detail texture; this slot cannot render."); return item; }
            if (string.IsNullOrEmpty(item.assetPath)) item.assetPath = "Unsaved scene content: " + source.name;
            else item.digest = AssetDatabase.GetAssetDependencyHash(item.assetPath).ToString();
            if (source is Texture2D texture)
            { item.notes.Add("Native detail billboard source " + texture.width + " × " + texture.height + " pixels; authored detail width/height govern the reference patch."); return item; }
            if (item.prefab == null) { item.invalid = true; item.notes.Add("Unsupported prototype source type."); return item; }
            try
            {
                int remaining = 4096; CheckHierarchy(item.prefab.transform, 0, ref remaining);
                foreach (var finding in UCPrefabIntegrity.InspectPrototype(item.prefab))
                { item.notes.Add(finding.Detail); item.invalid |= UCPrefabIntegrity.IsDefect(finding.Kind); }
                Vector3 scale = item.prefab.transform.localScale;
                if (!ScatterApplier.Finite(scale) || scale.x == 0 || scale.y == 0 || scale.z == 0)
                { item.invalid = true; item.notes.Add("Root scale needs finite nonzero values for contact and bounds."); }
                else if (scale.x < 0 || scale.y < 0 || scale.z < 0) item.notes.Add("Mirrored root scale requires a rendered winding/normal/culling check.");
                item.baseY = ScatterApplier.MeasureRootBaseY(item.prefab); item.heightM = ScatterApplier.MeasureNativeHeightM(item.prefab);
                // UC-T06-13: native detail placement cannot use the per-instance root correction used by trees/shrubs/objects.
                if (kind == UCScatterKind.Detail && Mathf.Abs(item.baseY * scale.y) > 0.005f)
                { item.invalid = true; item.notes.Add("Native detail meshes need their base at root-local Y zero; correct the source pivot in an authored copy before using this slot."); }
                var filters = item.prefab.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length == 0 || item.heightM <= 0) { item.invalid = true; item.notes.Add("No finite positive mesh height is available for root/size calibration."); }
                foreach (var filter in filters)
                {
                    var mesh = filter.sharedMesh; if (mesh == null) continue;
                    if (!mesh.HasVertexAttribute(VertexAttribute.Normal)) { item.invalid = true; item.notes.Add(filter.name + " has no normal attribute."); }
                    if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) item.notes.Add(filter.name + " has no UV0; texture mapping requires inspection.");
                    var renderer = filter.GetComponent<Renderer>(); if (renderer == null) continue;
                    foreach (var material in renderer.sharedMaterials) InspectMaterial(material, mesh, kind, item);
                }
                foreach (var group in item.prefab.GetComponentsInChildren<LODGroup>(true))
                {
                    var thresholds = new StringBuilder("Authored LOD screen-height fractions: ");
                    foreach (var lod in group.GetLODs()) thresholds.Append(lod.screenRelativeTransitionHeight).Append(' ');
                    item.notes.Add(thresholds + "; fade " + group.fadeMode + "; transition width and rendered continuity require close/medium/far checks.");
                }
            }
            catch (InvalidOperationException error) { item.invalid = true; item.notes.Add(error.Message); }
            return item;
        }

        private static void CheckHierarchy(Transform node, int depth, ref int remaining)
        {
            if (depth > 128 || --remaining < 0) throw new InvalidOperationException("Prototype inspection exceeds 4,096 transforms or 128 hierarchy levels.");
            for (int i = 0; i < node.childCount; i++) CheckHierarchy(node.GetChild(i), depth + 1, ref remaining);
        }

        private static void InspectMaterial(Material material, Mesh mesh, UCScatterKind kind, PrototypeContent item)
        {
            if (material == null || material.shader == null) { item.notes.Add("Missing material/shader; native fallback remains available but surface appearance needs authoring."); return; }
            string shader = material.shader.name;
            item.notes.Add("Material " + material.name + "; shader " + shader + "; instancing " + material.enableInstancing + "; " + mesh.subMeshCount + " mesh submeshes.");
            if (shader != "UniverseCreator/VegetationLit" && shader != "UniverseCreator/WindDetail")
            { if (kind != UCScatterKind.Object) item.notes.Add("This shader's wind/WindZone contract is unverified; inspect actual motion before acceptance."); return; }
            item.notes.Add("UC wind weights use mesh-local height and Vegetation Class. Vertex colour supplies colour/AO rather than a wind-weight requirement.");
            // UC-T06-13: the inspected shaders anchor at mesh-local Y zero, regardless of the prefab's corrected world contact.
            if (Mathf.Abs(mesh.bounds.min.y) > 0.005f)
                item.notes.Add("Mesh-local base Y is " + mesh.bounds.min.y + " m; UC wind anchoring assumes Y zero. Inspect root motion and correct mesh coordinates in an authored copy.");
            if ((kind == UCScatterKind.Tree || kind == UCScatterKind.Shrub) && material.HasProperty("_VegetationClass"))
            {
                float expected = kind == UCScatterKind.Tree ? 2 : kind == UCScatterKind.Shrub ? 1 : 0;
                float actual = material.GetFloat("_VegetationClass");
                if (!ScatterApplier.Finite(actual) || Mathf.Abs(actual - expected) > 0.01f)
                    item.notes.Add("Vegetation Class " + actual + " differs from the " + kind + " wind model " + expected + "; choose a category-specific authored material copy.");
            }
            if (material.HasProperty("_PivotHeight"))
            {
                float height = material.GetFloat("_PivotHeight");
                if (!ScatterApplier.Finite(height) || height <= 0) { item.invalid = true; item.notes.Add("Mesh Height must be positive finite metres for root-anchored wind."); }
                else if (Mathf.Abs(mesh.bounds.size.y - height) > Mathf.Max(0.05f, mesh.bounds.size.y * 0.1f))
                    item.notes.Add("Mesh Height " + height + " m differs from this mesh-local Y span " + mesh.bounds.size.y + " m; inspect root/tip wind anchoring.");
            }
            if (material.HasProperty("_OcclusionFromVertexColor") && material.GetFloat("_OcclusionFromVertexColor") > 0.5f && !mesh.HasVertexAttribute(VertexAttribute.Color))
                item.notes.Add("Vertex-colour AO is enabled but this mesh has no colour attribute.");
            if (material.HasProperty("_WindResponse"))
            {
                float response = material.GetFloat("_WindResponse");
                if (!ScatterApplier.Finite(response) || response < 0)
                { item.invalid = true; item.notes.Add("Wind Response must be finite and nonnegative."); }
                else if (response == 0) item.notes.Add("Wind Response is zero; this material is deliberately rigid.");
            }
        }

        private static void DrawPrototypeTools(UniverseCreatorObject world)
        {
            if (world == null) return;
            if (GUILayout.Button("Inspect prototype content"))
            {
                var report = InspectPrototypeContent(world.scatterLibrary); prototypeReports.Remove(world); prototypeReports.Add(world, report);
                Debug.Log(report.Describe(), world);
            }
            if (GUILayout.Button("Create prototype reference scene"))
                UniverseCreator.Editor.Utilities.VegetationPrototypeReference.Create(world, true);
            if (prototypeReports.TryGetValue(world, out var last))
                EditorGUILayout.HelpBox("Last explicit content inspection: " + last.prototypes.Count + " slots, " + last.errors +
                    " content errors" + (last.truncated ? "; inspection limit reached." : ". Full measurements and guidance are in the Console."),
                    last.errors > 0 || last.truncated ? MessageType.Warning : MessageType.Info);
        }
    }
}
