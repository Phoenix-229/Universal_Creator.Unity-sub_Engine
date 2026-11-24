// UC-T04-07: deterministic dependency order, edited targets, cycle refusal and world isolation.
using System;
using UnityEngine;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWaterJunctionLifecycle()
        {
            var root = new GameObject("UC water dependency owner"); root.SetActive(false);
            var foreign = new GameObject("UC foreign water dependency owner"); foreign.SetActive(false);
            try
            {
                root.AddComponent<UniverseCreatorObject>(); foreign.AddComponent<UniverseCreatorObject>();
                var a = JunctionRiverBody(root.transform, "Upstream A", new Vector3(0, 40, 0), new Vector3(0, 39, 10));
                var b = JunctionRiverBody(root.transform, "Middle B", new Vector3(10, 39, 0), new Vector3(10, 38, 10));
                var c = JunctionRiverBody(root.transform, "Receiver C", new Vector3(20, 38, 0), new Vector3(20, 37, 10));
                a.joinsRiver = b; b.joinsRiver = c;
                foreach (var supplied in new[] { new[] { a, b, c }, new[] { c, a, b }, new[] { b, c, a } })
                {
                    var order = UCWaterBody.GetWaterRebuildOrder(supplied);
                    Check(order.Length == 3 && order[0] == c && order[1] == b && order[2] == a,
                        "UC-T04-07 three-river rebuild order prepares receivers first independently of enumeration order.");
                }
                c.joinsRiver = a;
                Check(UCWaterBody.GetWaterRebuildOrder(new[] { a, b, c }).Length == 0 && a.WaterJunctionDiagnostic.Contains("cycle") &&
                    b.LastBuild == null && c.LastBuild == null,
                    "UC-T04-07 a cyclic component withdraws owned surfaces and reports a diagnostic without recursive generation.");
                c.joinsRiver = null; a.joinsRiver = null; b.joinsRiver = null;
                var last = a.OwnedRiverNodes()[1]; last.connectJunction = true; last.junctionTarget = b;
                UCWaterBody.GetWaterRebuildOrder(new[] { a, b, c });
                last.connectJunction = false;
                UCWaterBody.GetWaterRebuildOrder(new[] { a, b, c });
                Check(a.joinsRiver == null,
                    "UC-T04-07 removing a node connection tick retires its previously managed receiving reference.");
                a.joinsRiver = b; b.transform.SetParent(foreign.transform, true);
                var valid = UCWaterBody.GetWaterRebuildOrder(new[] { a, b, c });
                Check(Array.IndexOf(valid, a) < 0 && a.WaterJunctionDiagnostic.Contains("foreign") && a.LastBuild == null,
                    "UC-T04-07 moving a target into another world cannot preserve or republish its incoming surface.");
                string id = a.StableWaterBodyId;
                Check(a.StableWaterBodyId == id && a.GetWaterJunctionRecords().Length == 0,
                    "UC-T04-07 failed topology preserves body identity while retiring runtime junction acceptance.");
                var world = root.GetComponent<UniverseCreatorObject>();
                var frozen = world.GenerationScheduler.CurrentRun;
                var foreignRun = foreign.GetComponent<UniverseCreatorObject>().GenerationScheduler.CurrentRun;
                UniverseCreator.Editor.Utilities.UCRiverPlacementTool.RequestRebuild(a);
                var pendingRun = world.GenerationScheduler.CurrentRun;
                bool published = false;
                Check(!world.GenerationScheduler.TryPublish(frozen, () => published = true) && !published &&
                    foreign.GetComponent<UniverseCreatorObject>().GenerationScheduler.IsCurrent(foreignRun),
                    "UC-T04-07 a deferred river edit immediately rejects old-run publication without cancelling another world.");
                UniverseCreator.Editor.Utilities.UCRiverPlacementTool.RequestRebuild(a);
                Check(world.GenerationScheduler.CurrentRun.Equals(pendingRun),
                    "UC-T04-07 repeated edits in one pending drag coalesce invalidation without advancing the world for every point change.");
                // UC-T04-11: real scene teardown runs through RunWaterJunctionSceneReloadBatch with an empty startup scene.
            }
            finally
            {
                foreach (var parent in new[] { root, foreign }) foreach (var body in parent.GetComponentsInChildren<UCWaterBody>(true))
                { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); body.Clear(); }
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(foreign);
            }
        }

        /// <summary>Verifies moved and never-active water publications through actual scene closure in an isolated batch.</summary>
        /// <remarks>Requires one saved or empty startup scene. Temporary scene assets are authored and removed through Unity APIs; exits zero on success, one on failure.</remarks>
        /// <exception cref="InvalidOperationException">Called outside Unity batch mode.</exception>
        public static void RunWaterJunctionSceneReloadBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Water scene lifetime requires isolated batch mode.");
            checks = 0; failures.Clear();
            try { TestWaterNativeSceneLifetime(); }
            catch (Exception error) { Check(false, "UC-T04-07 water scene lifetime exception: " + error); }
            bool ok = failures.Count == 0;
            Debug.Log(Report(ok)); Console.WriteLine(Report(ok)); UnityEditor.EditorApplication.Exit(ok ? 0 : 1);
        }

        private static void TestWaterNativeSceneLifetime()
        {
            var original = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool single = string.IsNullOrEmpty(original.path);
            if (UnityEngine.SceneManagement.SceneManager.sceneCount != 1 || (single && original.GetRootGameObjects().Length > 0))
                throw new InvalidOperationException("Water scene lifetime requires a saved or empty startup scene.");
            UnityEngine.SceneManagement.Scene first = default, second = default;
            string folder = "Assets/UCWaterLifetime_" + Guid.NewGuid().ToString("N");
            try
            {
                UnityEditor.AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                first = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    single ? UnityEditor.SceneManagement.NewSceneMode.Single : UnityEditor.SceneManagement.NewSceneMode.Additive);
                Check(UnityEditor.SceneManagement.EditorSceneManager.SaveScene(first, folder + "/First.unity"),
                    "UC-T04-07 the lifetime fixture saves before opening a second additive scene.");
                second = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Additive);
                var root = new GameObject("Never-active water publication"); root.SetActive(false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, first);
                var body = root.AddComponent<UCWaterBody>(); body.SetKindOnce(UniverseCreator.Nodes.WaterBodyKind.Lake);
                body.digTerrain = false; body.generateVolume = false; body.bedSource = UCWaterBody.BedSource.FlatFloor; body.meshResolution = 4;
                body.RebuildSurface();
                var mesh = root.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, second);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(first, true);
                Check(mesh != null && body.LastBuild != null,
                    "UC-T04-07 moving a never-active water body preserves its native publication when the previous scene closes.");
                if (single) UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                else UnityEditor.SceneManagement.EditorSceneManager.CloseScene(second, true);
                Check(mesh == null,
                    "UC-T04-07 closing the owning scene retires a never-active body's native mesh without relying on OnDestroy.");
            }
            finally
            {
                if (single && ((first.IsValid() && first.isLoaded) || (second.IsValid() && second.isLoaded)))
                    UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                        UnityEditor.SceneManagement.NewSceneMode.Single);
                else
                {
                    if (first.IsValid() && first.isLoaded) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(first, true);
                    if (second.IsValid() && second.isLoaded) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(second, true);
                }
                if (original.IsValid() && original.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(original);
                if (UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
