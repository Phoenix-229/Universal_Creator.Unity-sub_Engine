// UC-T06-13: synthetic geometry verifies real inspector/reference owners; it supplies no visual/device or redistributed-art claim.
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Editor.Inspectors;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationPrototypeContent()
        {
            var root = new GameObject("UC prototype content acceptance"); root.SetActive(false);
            var prototype = new GameObject("Offset root prototype"); prototype.SetActive(false); prototype.transform.localScale = new Vector3(1, 2, 1);
            var shape = GameObject.CreatePrimitive(PrimitiveType.Cube); shape.transform.SetParent(prototype.transform, false); shape.transform.localPosition = Vector3.up * 2;
            var material = new Material(Shader.Find("UniverseCreator/VegetationLit")); shape.GetComponent<Renderer>().sharedMaterial = material;
            var texture = new Texture2D(2, 2); var invalidMesh = new Mesh();
            var invalid = new GameObject("Missing normal prototype"); invalid.SetActive(false);
            var groupRoot = new GameObject("Missing LOD renderer prototype"); groupRoot.SetActive(false);
            VegetationPrototypeReference.Result reference = null;
            Scene previous = SceneManager.GetActiveScene();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.scatterLibrary.trees.Add(new TreePrototypeDef { prefab = prototype });
                world.scatterLibrary.shrubs.Add(new ShrubPrototypeDef { prefab = prototype });
                world.scatterLibrary.objects.Add(new ObjectPrototypeDef { prefab = prototype });
                world.scatterLibrary.details.Add(new DetailPrototypeDef { billboardTexture = texture });
                world.scatterLibrary.objects.Add(new ObjectPrototypeDef());
                material.SetFloat("_PivotHeight", 123); material.SetFloat("_OcclusionFromVertexColor", 1);
                string worldBefore = EditorJsonUtility.ToJson(world), materialBefore = EditorJsonUtility.ToJson(material);
                var report = UCVegetationValidation.InspectPrototypeContent(world.scatterLibrary);
                Check(report.prototypes.Count == 5 && report.errors == 1 && report.prototypes.Last().source == null &&
                    report.prototypes.Last().notes.Any(n => n.Contains("cannot render")),
                    "UC-T06-13 actual prototype inspection retains and explains empty buyer slots without renumbering valid content.");
                var tree = report.prototypes.First();
                Check(Mathf.Abs(tree.baseY - 1.5f) < 0.001f && Mathf.Abs(tree.heightM - 2) < 0.001f && !tree.invalid,
                    "UC-T06-13 actual native contact/height owners measure offset pivots and apply authored root scale exactly once.");
                Check(tree.notes.Any(n => n.Contains("differs from this mesh-local Y span")) && tree.notes.Any(n => n.Contains("wind weights use mesh-local height")) &&
                    tree.notes.Any(n => n.Contains("Vertex-colour AO")),
                    "UC-T06-13 inspected UC shader contracts distinguish mesh-height wind anchoring from missing vertex-colour AO.");
                Check(tree.notes.Any(n => n.Contains("wind anchoring assumes Y zero")) &&
                    report.prototypes[1].notes.Any(n => n.Contains("differs from the Shrub wind model")),
                    "UC-T06-13 mesh-local wind base and per-category material mismatch produce guidance without altering borrowed materials.");
                var offsetDetails = new ScatterPrototypeLibrary(); offsetDetails.details.Add(new DetailPrototypeDef { prefabMesh = prototype });
                var offsetReport = UCVegetationValidation.InspectPrototypeContent(offsetDetails);
                Check(offsetReport.errors == 1 && offsetReport.prototypes[0].notes.Any(n => n.Contains("base at root-local Y zero")),
                    "UC-T06-13 native detail validation rejects offset mesh bases that native placement cannot correct.");
                invalidMesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; invalidMesh.triangles = new[] { 0, 1, 2 }; invalidMesh.RecalculateBounds();
                invalid.AddComponent<MeshFilter>().sharedMesh = invalidMesh; invalid.AddComponent<MeshRenderer>().sharedMaterial = material;
                var malformed = new ScatterPrototypeLibrary(); malformed.objects.Add(new ObjectPrototypeDef { prefab = invalid });
                var bad = UCVegetationValidation.InspectPrototypeContent(malformed);
                Check(bad.errors == 1 && bad.prototypes[0].notes.Any(n => n.Contains("no normal attribute")),
                    "UC-T06-13 actual mesh normal-attribute validation identifies invalid content without generating replacement normals.");
                var lod = groupRoot.AddComponent<LODGroup>(); lod.SetLODs(new[] { new LOD(0.5f, new Renderer[] { null }) });
                Check(UCPrefabIntegrity.InspectPrototype(groupRoot).Any(f => f.Kind == UCPrefabIntegrity.Kind.NullRenderer),
                    "UC-T06-13 selected-prototype inspection reuses the existing native LOD renderer defect check.");
                reference = VegetationPrototypeReference.Create(world, false);
                Check(reference.scene.IsValid() && reference.scene.isLoaded && reference.modelCount == 3 && reference.detailCount == 1 &&
                    reference.root.GetComponentsInChildren<Camera>(true).Length == 3,
                    "UC-T06-13 Editor APIs create a bounded tree/shrub/object gallery, native detail patch and close/medium/far reference cameras.");
                var renderers = reference.root.GetComponentsInChildren<MeshRenderer>(true);
                Check(renderers.Length == 3 && renderers.All(r => Mathf.Abs(r.bounds.min.y) < 0.005f) &&
                    reference.ground.GetDetailLayer(2, 20, 1, 1, 0)[0, 0] == 8,
                    "UC-T06-13 reference copies use actual final-ground root seating and declared per-square-metre native detail counts.");
                Check(reference.evidence.text.Contains("licences have not been verified") && reference.evidence.text.Contains("20/60/160") &&
                    EditorJsonUtility.ToJson(world) == worldBefore && EditorJsonUtility.ToJson(material) == materialBefore,
                    "UC-T06-13 reference evidence keeps content/licence limits explicit and never mutates source world/material authoring or runs a graph.");
                reference.Dispose(); reference = null;
                Check(prototype != null && material != null && texture != null, "UC-T06-13 reference cleanup retains borrowed source prototypes/materials/textures.");
                var many = new ScatterPrototypeLibrary(); for (int i = 0; i < 257; i++) many.objects.Add(new ObjectPrototypeDef());
                var bounded = UCVegetationValidation.InspectPrototypeContent(many);
                Check(bounded.truncated && bounded.prototypes.Count == 256, "UC-T06-13 content inspection reports its 256-slot work envelope explicitly.");
            }
            catch (Exception error) { Check(false, "UC-T06-13 prototype/reference acceptance exception: " + error); }
            finally
            {
                reference?.Dispose();
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(prototype); UnityEngine.Object.DestroyImmediate(invalid);
                UnityEngine.Object.DestroyImmediate(groupRoot); UnityEngine.Object.DestroyImmediate(invalidMesh);
                UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
