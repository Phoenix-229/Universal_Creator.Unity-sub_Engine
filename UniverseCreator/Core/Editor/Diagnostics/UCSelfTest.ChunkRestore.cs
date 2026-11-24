// UC-T08-15: partial scene adoption must preserve saved cell identities and native heights.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestChunkRestore()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC partial saved world"); root.SetActive(false);
            var native = new List<TerrainData>();
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = null; world.chunkCountPerSide = 3; world.worldSizeKm = 0.096f;
                var manager = root.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var terrainRoot = new GameObject("Terrain"); terrainRoot.transform.SetParent(root.transform, false);
                GameObject Saved(string name, float height)
                {
                    var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32) };
                    native.Add(data); data.SetHeights(0, 0, CommitHeights(33, height));
                    var holder = new GameObject(name); holder.transform.SetParent(terrainRoot.transform, false);
                    holder.AddComponent<UnityEngine.Terrain>().terrainData = data;
                    holder.AddComponent<TerrainCollider>().terrainData = data;
                    return holder;
                }
                var corner = Saved("Terrain_2_2", 0.73f);
                var origin = Saved("Terrain_0_0", 0.21f);
                var ensure = typeof(ChunkManager).GetMethod("EnsurePool", flags);
                ensure.Invoke(manager, new object[] { world });
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager);
                var type = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                GameObject Holder(int index) => (GameObject)type.GetField("go").GetValue(pool[index]);
                Check(pool.Count == 9 && Holder(0) == origin && Holder(8) == corner,
                    "UC restore partial shuffled scene fills the complete grid without indexing past the saved pool.");
                Vector3 cornerPosition = corner.transform.position;
                ensure.Invoke(manager, new object[] { world });
                Check(Holder(8) == corner && corner.GetComponent<UnityEngine.Terrain>().terrainData == native[0] &&
                    Math.Abs(native[0].GetHeights(0, 0, 1, 1)[0, 0] - 0.73f) < 0.0001f,
                    "UC restore repeated layout retains the original native object and saved sculpt height.");
                world.chunkCountPerSide = 1; ensure.Invoke(manager, new object[] { world });
                Check(Holder(0) == origin && !corner.activeSelf && corner.transform.position == cornerPosition &&
                    corner.name == "Terrain_2_2" && native[0] != null,
                    "UC restore shrinking parks saved out-of-grid terrain without moving, renaming or destroying it.");
                world.chunkCountPerSide = 3; ensure.Invoke(manager, new object[] { world });
                Check(Holder(8) == corner, "UC restore regrowing reclaims the original cell rather than a replacement.");
                // A fresh managed pool reproduces scene/domain reload with duplicate saved coordinates.
                pool.Clear();
                var duplicate = Saved("Terrain_2_2", 0.41f);
                var outside = Saved("Terrain_8_8", 0.52f);
                ensure.Invoke(manager, new object[] { world });
                Check(Holder(8) == corner && !duplicate.activeSelf && !outside.activeSelf &&
                    duplicate.name == "Terrain_2_2" && outside.name == "Terrain_8_8" && pool.Count == 11,
                    "UC restore duplicate and outside saved surfaces remain parked instead of filling a different cell.");
                Check(manager.RestoreSurfaces() == 0 && corner.activeSelf && !duplicate.activeSelf && !outside.activeSelf &&
                    !(bool)type.GetField("resident").GetValue(pool[9]) && !(bool)type.GetField("resident").GetValue(pool[10]),
                    "UC restore actual surface restoration never reactivates or admits a parked duplicate into residency.");
                manager.RestoreSurfaces();
                Check(terrainRoot.transform.childCount == 11 && Holder(8) == corner && !duplicate.activeSelf &&
                    Math.Abs(duplicate.GetComponent<UnityEngine.Terrain>().terrainData.GetHeights(0, 0, 1, 1)[0, 0] - 0.41f) < 0.0001f,
                    "UC restore repeated surface restoration creates no extra holders and preserves the parked duplicate heightmap.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                foreach (var data in native) if (data != null) UnityEngine.Object.DestroyImmediate(data);
            }
            TestSavedGraphBoundChunkSetup();
        }

        // UC Scene/Play repair: exercise the actual saved-data adoption and Play setup owner without starting a worker.
        private static void TestSavedGraphBoundChunkSetup()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC saved graph-bound setup"); root.SetActive(false);
            var graph = ScriptableObject.CreateInstance<Graph>();
            var layer = new TerrainLayer();
            var saved = new TerrainData { heightmapResolution = 33, size = new Vector3(1000, 100, 1000), terrainLayers = new[] { layer } };
            var unrelated = new TerrainData { heightmapResolution = 33, size = saved.size, terrainLayers = new[] { layer } };
            ChunkManager manager = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                world.graph = graph; world.resolution = 33; world.worldSizeKm = 1; world.chunkSizeMeters = 1000;
                world.maxHeightMeters = 100; world.generateCollider = true; world.terrainLayers = new[] { layer }; world.ValidateSettings();
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var terrainRoot = new GameObject("Terrain"); terrainRoot.transform.SetParent(root.transform, false);
                var holder = new GameObject("Terrain_0_0"); holder.transform.SetParent(terrainRoot.transform, false); holder.layer = 8;
                var terrain = holder.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = saved; terrain.drawHeightmap = false;
                var collider = holder.AddComponent<TerrainCollider>(); collider.terrainData = saved;
                float[,] heights = CommitHeights(33, 0.3f);
                for (int z = 1; z < 32; z++) heights[z, 16] = 0.12f; // Retained carved river bed.
                heights[8, 23] = 0.56f; saved.SetHeights(0, 0, heights);
                float[,] expected = saved.GetHeights(0, 0, 33, 33);
                typeof(ChunkManager).GetMethod("EnsurePool", flags).Invoke(manager, new object[] { world });
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager);
                Type type = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                object chunk = pool[0];
                Check((TerrainData)type.GetField("restoredAcceptedData").GetValue(chunk) == saved && !(bool)type.GetField("bakedThisSession").GetValue(chunk),
                    "UC Scene/Play repair: canonical saved ground is accepted without falsely marking its carved heights as a pristine session bake.");
                var setup = typeof(ChunkManager).GetMethod("EnsureFlatDataAsync", flags);
                Task Prepare() => (Task)setup.Invoke(manager, new object[] { world, world.GenerationScheduler.CurrentRun });
                Task prepared = Prepare();
                Check(prepared.IsCompleted, "UC Scene/Play repair: one-cell native setup finishes without a worker or blocked editor continuation.");
                if (!prepared.IsCompleted) return;
                prepared.GetAwaiter().GetResult(); root.SetActive(true); Physics.SyncTransforms();
                Check(manager.TryGetChunkReadiness(new Coord(0, 0), out var ready) && ready.TerrainAccepted && ready.CollisionReady && ready.Visible && ready.Ready,
                    "UC Scene/Play repair: saved graph-bound native ground is visible and collidable after the actual Play setup path.");
                bool unchanged = terrain.terrainData == saved && collider.terrainData == saved && holder.layer == 8 && saved.terrainLayers[0] == layer;
                float[,] actual = saved.GetHeights(0, 0, 33, 33);
                for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) unchanged &= actual[z, x] == expected[z, x];
                Check(unchanged && !(bool)type.GetField("bakedThisSession").GetValue(chunk),
                    "UC Scene/Play repair: setup retains native identity, authored layer, sculpt and every saved river-bed sample without recarving.");
                Check(manager.RestoreSurfaces() == 0 && (bool)typeof(ChunkManager).GetField("built", flags).GetValue(manager) && !manager.HasWork,
                    "UC Scene/Play repair: Play startup's restoration path arms streaming without queueing regeneration of saved graph-bound ground.");
                Check(terrain.terrainData == saved && saved.GetHeights(16, 16, 1, 1)[0, 0] == expected[16, 16] &&
                    saved.GetHeights(23, 8, 1, 1)[0, 0] == expected[8, 23],
                    "UC Scene/Play repair: startup preserves uncaptured sculpt and river beds even when capture-on-Regenerate is disabled.");
                SetCommitField(type, chunk, "lod", LODLevel.Low);
                int landing = manager.RequestLanding(Vector3.zero, 0);
                typeof(ChunkManager).GetMethod("RefreshLandingDemand", flags).Invoke(manager, new object[] { world });
                Check(landing > 0 && manager.GetLandingStatus(landing) == ChunkLandingStatus.Ready && !manager.HasWork &&
                    terrain.terrainData == saved && !(bool)type.GetField("bakedThisSession").GetValue(chunk) &&
                    saved.GetHeights(16, 16, 1, 1)[0, 0] == expected[16, 16] && saved.GetHeights(23, 8, 1, 1)[0, 0] == expected[8, 23],
                    "UC Scene/Play repair: landing promotes saved river/sculpt ground to High LOD without queueing a bake or replacing native terrain.");
                manager.CancelLanding(landing);
                var window = typeof(ChunkManager).GetMethod("ApplyStreamWindow", flags);
                terrain.terrainData = unrelated;
                Check(manager.TryGetChunkReadiness(new Coord(0, 0), out var mismatch) && !mismatch.TerrainAccepted,
                    "UC Scene/Play repair: a different native terrain cannot inherit the saved surface's acceptance record.");
                terrain.terrainData = saved;
                SetCommitField(type, chunk, "publicationInProgress", true); window.Invoke(manager, new object[] { world });
                Check(manager.TryGetChunkReadiness(new Coord(0, 0), out var publishing) && !publishing.TerrainAccepted && !publishing.Visible,
                    "UC Scene/Play repair: saved acceptance does not bypass an unfinished native publication.");
                SetCommitField(type, chunk, "publicationInProgress", false); window.Invoke(manager, new object[] { world });
                terrain.drawTreesAndFoliage = false;
                SetCommitField(type, chunk, "presentationPending", true); window.Invoke(manager, new object[] { world });
                Prepare().GetAwaiter().GetResult(); Physics.SyncTransforms();
                Check(!terrain.drawHeightmap && holder.activeInHierarchy && collider.enabled &&
                    collider.Raycast(new Ray(holder.transform.position + new Vector3(500, 150, 500), Vector3.down), out _, 200),
                    "UC Scene/Play repair: native setup cannot undo pending presentation while accepted river ground remains available to collision.");
                SetCommitField(type, chunk, "presentationPending", false); window.Invoke(manager, new object[] { world });
                Check(terrain.drawHeightmap && !terrain.drawTreesAndFoliage && terrainRoot.transform.childCount == 1,
                    "UC Scene/Play repair: settlement restores drawing and authored foliage without duplicate saved holders.");
                collider.terrainData = unrelated; window.Invoke(manager, new object[] { world });
                Check(!terrain.drawHeightmap && holder.activeSelf,
                    "UC Scene/Play repair: required mismatched collision parks presentation rather than certifying unsafe ground.");
                collider.terrainData = saved; window.Invoke(manager, new object[] { world });
                world.generateCollider = false; window.Invoke(manager, new object[] { world });
                Check(terrain.drawHeightmap && !collider.enabled,
                    "UC Scene/Play repair: explicitly collision-free terrain still renders through the independent presentation policy.");
                Check((int)typeof(ChunkManager).GetField("activeWorkers", flags).GetValue(manager) == 0 && terrain.terrainData == saved,
                    "UC Scene/Play repair: saved restoration and readiness transitions do not regenerate or replace accepted native terrain.");
            }
            finally
            {
                manager?.CancelAll(); UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(saved); UnityEngine.Object.DestroyImmediate(unrelated);
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(layer);
            }
        }

        private static void TestChunkRestoreSceneReload()
        {
            var original = SceneManager.GetActiveScene();
            bool single = string.IsNullOrEmpty(original.path);
            if (SceneManager.sceneCount != 1 || single && original.GetRootGameObjects().Length != 0)
                throw new InvalidOperationException("Chunk restore requires an empty startup scene or one saved scene.");
            string folder = "Assets/UCChunkRestore_" + Guid.NewGuid().ToString("N");
            Scene fixture = default;
            try
            {
                AssetDatabase.CreateFolder("Assets", folder.Substring(7));
                fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
                SceneManager.SetActiveScene(fixture);
                var root = new GameObject("Saved UC World"); root.SetActive(false);
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                // UC-T08-15: disk reload validates authored dimensions; a derived count on a sub-1 km fixture cannot survive OnValidate.
                var graph = ScriptableObject.CreateInstance<Graph>(); AssetDatabase.CreateAsset(graph, folder + "/WorldGraph.asset");
                world.graph = graph; world.resolution = 33; world.worldSizeKm = 3f;
                world.chunkSizeMeters = 1000f; world.maxHeightMeters = 100f; world.ValidateSettings();
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                var manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var terrainRoot = new GameObject("Terrain"); terrainRoot.transform.SetParent(root.transform, false);
                // UC Scene/Play repair: persist complete graph-bound ground; each native cell has its own saved identity.
                for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++)
                {
                    bool corner = x == 2 && z == 2;
                    var data = new TerrainData { heightmapResolution = 33, size = new Vector3(1000, 100, 1000) };
                    float[,] heights = CommitHeights(33, corner ? 0.64f : 0.3f);
                    if (corner) for (int row = 2; row < 31; row++) heights[row, 16] = 0.12f;
                    data.SetHeights(0, 0, heights);
                    AssetDatabase.CreateAsset(data, folder + (corner ? "/Corner.asset" : "/Cell_" + x + "_" + z + ".asset"));
                    var holder = new GameObject("Terrain_" + x + "_" + z); holder.transform.SetParent(terrainRoot.transform, false); holder.layer = 8;
                    holder.AddComponent<UnityEngine.Terrain>().terrainData = data;
                    holder.AddComponent<TerrainCollider>().terrainData = data;
                }
                string scenePath = folder + "/World.unity";
                Check(EditorSceneManager.SaveScene(fixture, scenePath), "UC restore graph-bound terrain scene saves to disk.");
                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else EditorSceneManager.CloseScene(fixture, true);
                fixture = EditorSceneManager.OpenScene(scenePath, single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                root = fixture.GetRootGameObjects()[0]; world = root.GetComponent<UniverseCreatorObject>();
                manager = root.transform.Find("ChunkManager").GetComponent<ChunkManager>(); manager.Initialize(world);
                Check(world.chunkCountPerSide == 3 && world.GetChunkSize() == 1000f,
                    "UC restore disk reopen retains the authored world and chunk dimensions after validation.");
                Check(manager.RestoreSurfaces() == 0 && world.graph == AssetDatabase.LoadAssetAtPath<Graph>(folder + "/WorldGraph.asset"),
                    "UC restore disk-reopened graph-bound world restores its graph and ground without a bake or array exception.");
                terrainRoot = root.transform.Find("Terrain").gameObject;
                var saved = terrainRoot.transform.Find("Terrain_2_2").GetComponent<UnityEngine.Terrain>();
                Check(terrainRoot.transform.childCount == 9 && saved.terrainData == AssetDatabase.LoadAssetAtPath<TerrainData>(folder + "/Corner.asset") &&
                    saved.gameObject.layer == 8 && Math.Abs(saved.terrainData.GetHeights(0, 0, 1, 1)[0, 0] - 0.64f) < 0.0001f &&
                    Math.Abs(saved.terrainData.GetHeights(16, 16, 1, 1)[0, 0] - 0.12f) < 0.0001f,
                    "UC restore disk reopen retains canonical holders, authored layer, sculpt and carved river bed.");
                Check(manager.RestoreSurfaces() == 0, "UC restore repeated post-reopen restoration accepts saved cells without regeneration.");
                Check(terrainRoot.transform.childCount == 9,
                    "UC restore repeated post-reload restoration never stacks duplicate terrain holders.");
                // UC-T08-15: a second real save/unload/reopen catches duplicated holders across persisted restoration.
                Check(EditorSceneManager.SaveScene(fixture, scenePath), "UC restore repaired world saves again without a replacement scene.");
                if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else EditorSceneManager.CloseScene(fixture, true);
                fixture = EditorSceneManager.OpenScene(scenePath, single ? OpenSceneMode.Single : OpenSceneMode.Additive);
                root = fixture.GetRootGameObjects()[0]; world = root.GetComponent<UniverseCreatorObject>();
                manager = root.transform.Find("ChunkManager").GetComponent<ChunkManager>(); manager.Initialize(world);
                Check(manager.RestoreSurfaces() == 0, "UC restore second graph-bound reopen accepts all saved cells without regeneration.");
                terrainRoot = root.transform.Find("Terrain").gameObject;
                saved = terrainRoot.transform.Find("Terrain_2_2").GetComponent<UnityEngine.Terrain>();
                Check(terrainRoot.transform.childCount == 9 && saved.terrainData == AssetDatabase.LoadAssetAtPath<TerrainData>(folder + "/Corner.asset") &&
                    saved.GetComponent<TerrainCollider>().terrainData == saved.terrainData &&
                    saved.gameObject.layer == 8 && Math.Abs(saved.terrainData.GetHeights(0, 0, 1, 1)[0, 0] - 0.64f) < 0.0001f &&
                    Math.Abs(saved.terrainData.GetHeights(16, 16, 1, 1)[0, 0] - 0.12f) < 0.0001f,
                    "UC restore second disk reopen keeps one holder per cell and the same saved terrain/collider/sculpt/river data.");
            }
            finally
            {
                if (fixture.IsValid() && fixture.isLoaded)
                {
                    if (single) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    else EditorSceneManager.CloseScene(fixture, true);
                }
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
