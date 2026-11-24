// UC-T04-05B: test the real authoring owners rather than a parallel point-creation fixture.
using UnityEditor;
using UnityEngine;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestProgressiveRiverAuthoring()
        {
            var root = new GameObject("UC progressive river authoring"); root.SetActive(false);
            TerrainData native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32));
            GameObject terrainRoot = null; UCWaterBody body = null;
            Object previousSelection = Selection.activeObject;
            try
            {
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.4f + Mathf.Min(0.1f, Mathf.Abs(x - 16) * 0.025f);
                native.SetHeights(0, 0, heights);
                heights = native.GetHeights(0, 0, 33, 33); // UC-T04-05B: compare exact native buffers after Unity's height quantization.
                terrainRoot = Terrain.CreateTerrainGameObject(native);
                body = UniverseCreatorMenus.SpawnWater(UniverseCreatorMenus.WaterPreset.River, root.transform);
                body.meander = 0; body.ruggedness = 0; body.digTerrain = false; body.valleySnapMetres = 40;
                Check(body.OwnedRiverNodes().Length == 0 && body.CarveLayer == null && body.LastBuild == null,
                    "UC-T04-05B the real river preset creates an empty parent without an automatic course or water mesh.");

                var source = UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 6));
                Check(source.transform.position == new Vector3(16, 49, 6) && source.halfWidthM == 0.15f && source.depthM == 0.2f &&
                    body.CarveLayer == null && body.LastBuild == null && body.RiverSurfaceDiagnostic.Contains("second point"),
                    "UC-T04-05B the first click keeps its exact source position/defaults and waits for a physical segment.");
                var second = UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 12));
                var third = UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 18));
                Undo.IncrementCurrentGroup(); int appendGroup = Undo.GetCurrentGroup();
                var fourth = UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 26));
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(appendGroup);
                Undo.IncrementCurrentGroup(); // UC-T04-05B: close the creation transaction before Undo and the ensuing native rebuild.
                Undo.PerformUndo(); UCRiverPlacementTool.FlushPendingEdits();
                Check(body.OwnedRiverNodes().Length == 3 && body.outline.Count == 3,
                    "UC-T04-05B Undo removes the clicked point and refreshes the course.");
                Undo.PerformRedo(); int beforeRefresh = body.OwnedRiverNodes().Length; UCRiverPlacementTool.FlushPendingEdits();
                var restoredNodes = body.OwnedRiverNodes();
                Check(restoredNodes.Length == 4, $"UC-T04-05B Redo restores all four owned points. beforeRefresh={beforeRefresh}, afterRefresh={restoredNodes.Length}");
                if (restoredNodes.Length != 4) return; // Named failure retains the diagnostic without an unrelated index exception.
                fourth = restoredNodes[3];
                Check(fourth.transform.position == new Vector3(16, 49, 26) && fourth.halfWidthM == 0 && fourth.depthM == 0,
                    "UC-T04-05B Redo restores the clicked position and unset dimension provenance.");
                Check(second.halfWidthM == 0 && third.depthM == 0 && fourth.halfWidthM == 0 && body.LastBuild != null,
                    "UC-T04-05B later real clicks preserve unset dimensions and build water with digging disabled.");

                third.halfWidthM = 0.8f; third.depthM = 0.6f;
                UCRiverPlacementTool.RebuildAfterEdit(body);
                var baked = body.riverProfile.Bake(body.outline);
                var arc = body.outline.BuildArcTable();
                Check(Mathf.Abs(baked.SampleAtArc(arc[1]).halfWidthM - 0.15f) < 1e-5f &&
                    Mathf.Abs(baked.SampleAtArc(arc[3]).halfWidthM - 0.8f) < 1e-5f &&
                    Mathf.Abs(baked.SampleAtArc(arc[3]).depthM - 0.6f) < 1e-5f && fourth.depthM == 0,
                    "UC-T04-05B an override carries through later unset points without rewriting their authored values.");

                var nested = new GameObject("Nested river"); nested.transform.SetParent(body.transform, false);
                var nestedBody = nested.AddComponent<UCWaterBody>(); nestedBody.shape = UCWaterBody.ShapeMode.RiverSpline;
                RiverModePoint(nested.transform, new Vector3(200, 10, 200), false);
                var group = new GameObject("Course group"); group.transform.SetParent(body.transform, false);
                third.transform.SetParent(group.transform, true); fourth.transform.SetParent(group.transform, true);
                var inserted = UCRiverPlacementTool.InsertBefore(body, third, new Vector3(16, 49, 15));
                var owned = body.OwnedRiverNodes();
                Check(owned.Length == 5 && owned[2] == inserted && owned[3] == third && inserted.halfWidthM == 0 &&
                    inserted.transform.parent == third.transform.parent,
                    "UC-T04-05B insertion respects grouped hierarchy order and excludes a nested river's points.");

                Undo.IncrementCurrentGroup(); int undoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(third, "Override river width"); third.halfWidthM = 1.25f; // UC-T04-05B: exercise immediate Undo in a synchronous fixture, including subsequent rebuilds.
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undoGroup);
                Undo.IncrementCurrentGroup(); // UC-T04-05B: the next rebuild belongs outside the authored override transaction.
                UCRiverPlacementTool.RebuildAfterEdit(body); Undo.PerformUndo(); UCRiverPlacementTool.FlushPendingEdits();
                Check(Mathf.Abs(body.riverProfile.Bake(body.outline).SampleAtArc(body.outline.Length()).halfWidthM - 0.8f) < 1e-5f,
                    "UC-T04-05B Undo rebuilds the baked inherited widths through the production editor owner.");
                Undo.PerformRedo(); UCRiverPlacementTool.FlushPendingEdits();
                Check(Mathf.Abs(body.riverProfile.Bake(body.outline).SampleAtArc(body.outline.Length()).halfWidthM - 1.25f) < 1e-5f,
                    "UC-T04-05B Redo reapplies the inherited override without moving authored points.");

                foreach (var point in body.OwnedRiverNodes()) { point.halfWidthM = 0; point.depthM = 0; }
                body.GatherCourseFromNodes(); baked = body.riverProfile.Bake(body.outline);
                Check(!baked.HasWidth && !baked.HasDepth, "UC-T04-05B existing all-unset courses retain hydraulic sizing semantics.");
                body.SetRiverTerrainMode(RiverTerrainMode.ReadProceduralTrench);
                var readPoint = UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 29));
                Check(readPoint.depthM == 0 && body.CarveLayer == null && RiverReadHeightsEqual(heights, native.GetHeights(0, 0, 33, 33)),
                    "UC-T04-05B editing in read mode cannot excavate when accepted trench coverage is absent.");

                bool bad = false; int before = body.OwnedRiverNodes().Length;
                try { UCRiverPlacementTool.AppendNode(body, new Vector3(float.NaN, 0, 0)); }
                catch (System.ArgumentException) { bad = true; }
                Check(bad && body.OwnedRiverNodes().Length == before, "UC-T04-05B invalid point coordinates fail before changing the hierarchy.");

                foreach (var point in body.OwnedRiverNodes()) if (point != source) Object.DestroyImmediate(point.gameObject);
                UCRiverPlacementTool.FlushPendingEdits();
                Check(body.outline.Count == 1 && body.LastBuild == null && body.GetComponentInChildren<MeshFilter>(true) == null,
                    "UC-T04-05B deleting the final segment withdraws the old owned water surface.");
                TestRiverOwnedGroundPicking(body, terrainRoot.GetComponent<Terrain>());
            }
            finally
            {
                if (body != null) { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); body.Clear(); WaterCarveRegistry.Unregister(body); }
                Object.DestroyImmediate(root); if (terrainRoot != null) Object.DestroyImmediate(terrainRoot); Object.DestroyImmediate(native);
                UCRiverPlacementTool.FlushPendingEdits(); Selection.activeObject = previousSelection;
            }
        }

        /// <summary>Verifies progressive river creation and inherited field Undo/Redo across actual Editor frames in an isolated batch.</summary>
        /// <remarks>Creates temporary native terrain and scene objects, observes deferred rebuilds, then cleans up and exits zero on success or one on failure.</remarks>
        /// <exception cref="System.InvalidOperationException">Called outside Unity batch mode.</exception>
        public static void RunRiverAuthoringUndoBatch()
        {
            if (!Application.isBatchMode) throw new System.InvalidOperationException("River Undo acceptance requires isolated batch mode.");
            checks = 0; failures.Clear();
            GameObject root = null, terrainRoot = null; TerrainData native = null; UCWaterBody body = null;
            Object previousSelection = Selection.activeObject;
            float[,] heights = null; int phase = 0;
            void Finish()
            {
                EditorApplication.delayCall -= Advance;
                if (body != null) { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); body.Clear(); WaterCarveRegistry.Unregister(body); }
                if (root != null) Object.DestroyImmediate(root);
                if (terrainRoot != null) Object.DestroyImmediate(terrainRoot);
                if (native != null) Object.DestroyImmediate(native);
                UCRiverPlacementTool.FlushPendingEdits(); Selection.activeObject = previousSelection;
                bool ok = failures.Count == 0;
                Debug.Log(Report(ok)); System.Console.WriteLine(Report(ok)); EditorApplication.Exit(ok ? 0 : 1);
            }
            void Advance()
            {
                try
                {
                    if (phase == 0 || phase == 4) Undo.PerformUndo();
                    else if (phase == 1)
                    {
                        UCRiverPlacementTool.FlushPendingEdits();
                        Check(body.OwnedRiverNodes().Length == 3 && body.outline.Count == 3,
                            "UC-T04-05B Editor-frame Undo removes the fourth click and refreshes the production course.");
                        Undo.PerformRedo();
                    }
                    else if (phase == 2)
                    {
                        UCRiverPlacementTool.FlushPendingEdits(); var nodes = body.OwnedRiverNodes();
                        Check(nodes.Length == 4 && nodes[3].transform.position == new Vector3(16, 49, 26) &&
                            nodes[3].halfWidthM == 0 && nodes[3].depthM == 0 && body.LastBuild != null,
                            "UC-T04-05B Editor-frame Redo restores the actual clicked point, inherited provenance and native water.");
                        if (nodes.Length != 4) { Finish(); return; }
                        var surface = body.GetComponentInChildren<MeshFilter>(true);
                        Check(surface != null && (surface.gameObject.hideFlags & (HideFlags.DontSave | HideFlags.NotEditable)) ==
                            (HideFlags.DontSave | HideFlags.NotEditable),
                            "UC-T04-05B regenerated water children are transient and read-only without a new picking Undo operation.");
                    }
                    else if (phase == 3)
                    {
                        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
                        var third = body.OwnedRiverNodes()[2]; Undo.RecordObject(third, "Override river width"); third.halfWidthM = 1.25f;
                        Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); Undo.IncrementCurrentGroup();
                        UCRiverPlacementTool.RebuildAfterEdit(body);
                    }
                    else if (phase == 5 || phase == 6)
                    {
                        UCRiverPlacementTool.FlushPendingEdits(); float expected = phase == 5 ? 0.8f : 1.25f;
                        Check(Mathf.Abs(body.riverProfile.Bake(body.outline).SampleAtArc(body.outline.Length()).halfWidthM - expected) < 1e-5f,
                            "UC-T04-05B Editor-frame " + (phase == 5 ? "Undo" : "Redo") + " refreshes the inherited width through the production editor owner.");
                        if (phase == 5) Undo.PerformRedo();
                        else
                        {
                            Check(RiverReadHeightsEqual(heights, native.GetHeights(0, 0, 33, 33)),
                                "UC-T04-05B real-frame creation and field Undo/Redo preserve non-excavating native heights exactly.");
                            Finish(); return;
                        }
                    }
                    phase++; EditorApplication.delayCall += Advance;
                }
                catch (System.Exception error) { Check(false, "UC-T04-05B Editor-frame Undo exception: " + error); Finish(); }
            }
            try
            {
                root = new GameObject("UC Editor-frame river authoring"); root.SetActive(false);
                native = TerrainBuilder.GetOrCreateTerrainData(null, 33, new Vector3(32, 100, 32)); heights = new float[33, 33];
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = 0.4f + Mathf.Min(0.1f, Mathf.Abs(x - 16) * 0.025f);
                native.SetHeights(0, 0, heights); heights = native.GetHeights(0, 0, 33, 33); terrainRoot = Terrain.CreateTerrainGameObject(native);
                body = UniverseCreatorMenus.SpawnWater(UniverseCreatorMenus.WaterPreset.River, root.transform);
                body.meander = 0; body.ruggedness = 0; body.digTerrain = false; body.generateVolume = false;
                UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 6)); UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 12));
                var third = UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 18)); third.halfWidthM = 0.8f; third.depthM = 0.6f;
                UCRiverPlacementTool.RebuildAfterEdit(body); Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup(); int appendGroup = Undo.GetCurrentGroup();
                UCRiverPlacementTool.AppendNode(body, new Vector3(16, 49, 26)); Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(appendGroup); Undo.IncrementCurrentGroup();
                EditorApplication.delayCall += Advance;
            }
            catch (System.Exception error) { Check(false, "UC-T04-05B Editor-frame setup exception: " + error); Finish(); }
        }

        private static void TestRiverOwnedGroundPicking(UCWaterBody body, Terrain terrain)
        {
            Physics.SyncTransforms();
            var ray = new Ray(new Vector3(16, 200, 16), Vector3.down);
            Check(UCRiverPlacementTool.RaycastGround(body, ray, out var hit) && Mathf.Abs(hit.y - 40) < 0.01f && hit.x == 16 && hit.z == 16,
                "UC-T04-05B terrain picking keeps the clicked XZ and actual native ground height.");
            var collider = terrain.GetComponent<TerrainCollider>(); collider.enabled = false;
            Check(UCRiverPlacementTool.RaycastGround(body, ray, out hit) && Mathf.Abs(hit.y - 40) < 0.01f,
                "UC-T04-05B bounded native-height fallback works without an enabled terrain collider.");
            var holes = terrain.terrainData.GetHoles(0, 0, 32, 32); holes[16, 16] = false;
            terrain.terrainData.SetHoles(0, 0, holes);
            Check(!UCRiverPlacementTool.RaycastGround(body, ray, out _) &&
                !UCRiverPlacementTool.RaycastGround(body, new Ray(new Vector3(100000, 200, 100000), Vector3.down), out _),
                "UC-T04-05B terrain holes and missing coverage do not create a synthetic ground-plane hit.");
            var foreign = new GameObject("Foreign river world"); foreign.SetActive(false);
            Transform parent = body.transform.parent;
            try
            {
                foreign.AddComponent<UniverseCreatorObject>(); body.transform.SetParent(foreign.transform, true);
                Check(!UCRiverPlacementTool.RaycastGround(body, new Ray(new Vector3(10, 200, 10), Vector3.down), out _),
                    "UC-T04-05B a river cannot place a point on overlapping terrain owned by another world.");
            }
            finally { body.transform.SetParent(parent, true); Object.DestroyImmediate(foreign); }
        }
    }
}
