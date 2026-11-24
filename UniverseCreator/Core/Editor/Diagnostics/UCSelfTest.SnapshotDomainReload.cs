using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Geology;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private const string SnapshotReloadKey = "UC.SelfTest.SnapshotDomainReload.";

        /// <summary>UC-T01-07: runs an actual managed-domain reload between two captures of saved fixture inputs.</summary>
        /// <remarks>
        /// Isolated batch only. Preserves a saved startup scene additively; an untitled startup scene must be empty.
        /// SessionState carries only fixture paths/check counts/hashes through the reload. Creates/deletes only its own
        /// temporary assets, resumes via InitializeOnLoadMethod, and exits zero on success or one on failure.
        /// This checks input/cache reconstruction; active-worker cancellation is covered by the separate worker gate.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Called outside batch mode or with an unsafe startup scene.</exception>
        public static void RunSnapshotDomainReloadBatch()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Domain reload acceptance requires an isolated batch process.");
            checks = 0; failures.Clear();
            Scene original = SceneManager.GetActiveScene();
            bool single = string.IsNullOrEmpty(original.path);
            if (!original.IsValid() || SceneManager.sceneCount != 1 || (single && original.GetRootGameObjects().Length != 0))
                throw new InvalidOperationException("Start domain reload acceptance with one saved scene or an empty untitled scene.");
            string folder = "Assets/UCSnapshotDomainReload_" + Guid.NewGuid().ToString("N");
            SessionState.SetString(SnapshotReloadKey + "Original", original.path);
            SessionState.SetBool(SnapshotReloadKey + "Single", single);
            GenerationSnapshot captured = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                Scene fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                    single ? NewSceneMode.Single : NewSceneMode.Additive);
                SceneManager.SetActiveScene(fixture);
                Graph graph = ScriptableObject.CreateInstance<Graph>();
                Texture2D raster = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                SetReloadRaster(raster, 0.25f);
                AssetDatabase.CreateAsset(raster, folder + "/Raster.asset");
                LithologyProfile profile = ScriptableObject.CreateInstance<LithologyProfile>();
                profile.PopulateExampleColumn();
                foreach (LithoUnit unit in profile.units) unit.erodibilityKlsu = 1f;
                AssetDatabase.CreateAsset(profile, folder + "/Lithology.asset");
                var curve = new CurveNode { id = "curve", curve = AnimationCurve.Linear(0f, 0.2f, 1f, 0.2f) };
                var removedRaster = new ImportHeightmapNode { id = "removedRaster", heightmap = raster };
                graph.nodes.AddRange(new Generator[] {
                    new ConstantNode { id = "source", value = 0.25f }, curve,
                    new ImportHeightmapNode { id = "raster", heightmap = raster },
                    removedRaster,
                    new LithologyOutput { id = "lithology", profile = profile },
                    new BiomeDensityTableNode { id = "density" }, new HeightOutputNode { id = "height" }
                });
                graph.connections.Add(new Connection("source", 0, "curve", 0));
                graph.connections.Add(new Connection("source", 0, "lithology", 0));
                AssetDatabase.CreateAsset(graph, folder + "/Graph.asset");
                GameObject root = new GameObject("UC domain reload world"); root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, fixture);
                UniverseCreatorObject world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.graph = graph;
                // UC climate restore: keep the legacy density fixture separate from explicit TerrainWater bindings.
                Graph climateGraph = ScriptableObject.CreateInstance<Graph>();
                climateGraph.nodes.Add(new HeightOutputNode { id = "climate-height" });
                AssetDatabase.CreateAsset(climateGraph, folder + "/ClimateTerrain.asset");
                GameObject climateRoot = new GameObject("UC climate domain reload world"); climateRoot.SetActive(false);
                SceneManager.MoveGameObjectToScene(climateRoot, fixture);
                UniverseCreatorObject climateWorld = climateRoot.AddComponent<UniverseCreatorObject>();
                climateWorld.enabled = false; climateWorld.graph = climateGraph;
                SessionState.SetString(SnapshotReloadKey + "climate", PrepareClimateReloadFixture(climateWorld, folder).ToString(CultureInfo.InvariantCulture));
                UCVegetationSystemManager veg = root.AddComponent<UCVegetationSystemManager>(); veg.enabled = false;
                veg.useEcology = true; veg.suitabilityThreshold = 0.2f;
                captured = GenerationSnapshot.Capture(graph, world);
                foreach (string id in new[] { "source", "curve", "raster", "removedRaster", "lithology", "density" })
                    SessionState.SetString(SnapshotReloadKey + id, captured.Graph.GetNodeById(id).contentHash.ToString(CultureInfo.InvariantCulture));
                Graph oldGraph = captured.Graph;
                LithologyProfile oldProfile = ((ILithologySnapshotInput)oldGraph.GetNodeById("lithology")).CapturedProfile;
                captured.Release(); captured = null;
                Check(oldGraph == null && oldProfile == null,
                    "UC-T01-07 domain fixture retires its first capture before requesting a managed reload.");
                curve.curve = AnimationCurve.Linear(0f, 0.8f, 1f, 0.8f);
                // UC-T01-07: white is exact in RGBA32; fractional channel values undergo 8-bit quantization.
                SetReloadRaster(raster, 1f);
                removedRaster.heightmap = null;
                foreach (LithoUnit unit in profile.units) unit.erodibilityKlsu = 3f;
                veg.suitabilityThreshold = 0.6f;
                EditorUtility.SetDirty(graph); EditorUtility.SetDirty(raster); EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssetIfDirty(graph); AssetDatabase.SaveAssetIfDirty(raster); AssetDatabase.SaveAssetIfDirty(profile);
                Check(EditorSceneManager.SaveScene(fixture, folder + "/World.unity"),
                    "UC-T01-07 domain fixture saves only its authored graph/assets/scene edits.");
                if (failures.Count != 0) throw new InvalidOperationException("Domain fixture setup did not pass.");
                SessionState.SetInt(SnapshotReloadKey + "Checks", checks);
                SessionState.SetString(SnapshotReloadKey + "Folder", folder);
                Debug.Log("UC-T01-07 requesting managed domain reload; the next domain resumes the saved fixture.");
                EditorUtility.RequestScriptReload();
            }
            catch (Exception exception)
            {
                Check(false, "UC-T01-07 domain reload setup exception: " + exception);
                CleanupSnapshotDomainReload(folder);
                ExitSnapshotDomainReload();
            }
            finally { if (captured != null) captured.Release(); }
        }

        private static void SetReloadRaster(Texture2D raster, float value)
        {
            Color color = new Color(value, value, value, 1f);
            raster.SetPixels(new[] { color, color, color, color }); raster.Apply(false, false);
        }

        [InitializeOnLoadMethod]
        private static void ResumeSnapshotDomainReload()
        {
            if (!Application.isBatchMode || string.IsNullOrEmpty(SessionState.GetString(SnapshotReloadKey + "Folder", string.Empty))) return;
            EditorApplication.delayCall += FinishSnapshotDomainReload;
        }

        private static void FinishSnapshotDomainReload()
        {
            string folder = SessionState.GetString(SnapshotReloadKey + "Folder", string.Empty);
            SessionState.EraseString(SnapshotReloadKey + "Folder");
            checks = SessionState.GetInt(SnapshotReloadKey + "Checks", 0); failures.Clear();
            GenerationSnapshot captured = null;
            LithologyProfile copied = null;
            try
            {
                ValidateSnapshotReloadFolder(folder);
                Graph graph = AssetDatabase.LoadAssetAtPath<Graph>(folder + "/Graph.asset");
                Scene scene = SceneManager.GetSceneByPath(folder + "/World.unity");
                if (graph == null || !scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("The saved domain fixture is unavailable.");
                UniverseCreatorObject world = scene.GetRootGameObjects()[0].GetComponent<UniverseCreatorObject>();
                if (world == null) throw new InvalidOperationException("The fixture's world did not reload.");
                // UC climate restore: validate persisted acceptance without migrating the legacy graph under test.
                GameObject climateRoot = Array.Find(scene.GetRootGameObjects(), root => root.name == "UC climate domain reload world");
                UniverseCreatorObject climateWorld = climateRoot != null ? climateRoot.GetComponent<UniverseCreatorObject>() : null;
                if (climateWorld == null) throw new InvalidOperationException("The climate fixture's world did not reload.");
                CheckClimateReloadFixture(climateWorld, ReloadBaselineHash("climate"));
                Check(graph.GetNodeById("source").contentHash == 0UL && graph.GetNodeById("density").preparedContextHash == 0UL,
                    "UC-T01-07 managed reload clears transient authored-node hashes/context.");
                captured = GenerationSnapshot.Capture(graph, world);
                Check(captured.Graph.GetNodeById("source").contentHash == ReloadBaselineHash("source"),
                    "UC-T01-07 managed reload keeps an unrelated unchanged source hash reusable.");
                bool changed = true;
                foreach (string id in new[] { "curve", "raster", "lithology", "density" })
                    changed &= captured.Graph.GetNodeById(id).contentHash != ReloadBaselineHash(id);
                Check(changed, "UC-T01-07 managed reload rebuilds curve/raster/geology/ecology dependency revisions.");
                Check(Mathf.Abs(SnapshotPull(captured.Graph, "curve").arr[0] - 0.8f) < 1e-6f &&
                      SnapshotPull(captured.Graph, "raster").arr[0] == 1f,
                    "UC-T01-07 managed reload prepares saved curve and raster edits instead of former samples.");
                Check(captured.Graph.GetNodeById("removedRaster").contentHash != ReloadBaselineHash("removedRaster") &&
                      SnapshotPull(captured.Graph, "removedRaster").arr[0] == 0f,
                    "UC-T01-07 managed reload clears a removed raster binding and its former samples.");
                Check(SnapshotPull(captured.Graph, "lithology", 3).arr[0] == 3f &&
                      Mathf.Abs(captured.Ecology.settings.suitabilityThreshold - 0.6f) < 1e-6f,
                    "UC-T01-07 managed reload captures saved geology and manager settings instead of former context.");
                copied = ((ILithologySnapshotInput)captured.Graph.GetNodeById("lithology")).CapturedProfile;
                captured.Release(); captured = null;
                Check(copied == null, "UC-T01-07 the post-reload capture releases its copied native profile.");
            }
            catch (Exception exception) { Check(false, "UC-T01-07 domain reload continuation exception: " + exception); }
            finally
            {
                if (captured != null) captured.Release();
                CleanupSnapshotDomainReload(folder);
            }
            ExitSnapshotDomainReload();
        }

        private static ulong ReloadBaselineHash(string id)
        {
            return ulong.Parse(SessionState.GetString(SnapshotReloadKey + id, string.Empty), CultureInfo.InvariantCulture);
        }

        private static void CleanupSnapshotDomainReload(string folder)
        {
            try { CleanupSnapshotDomainReloadAssets(folder); }
            catch (Exception exception) { Check(false, "UC-T01-07 domain fixture cleanup exception: " + exception); }
        }

        // UC-T01-07: SessionState survives the domain, so validate its fixture path before any asset removal.
        private static void ValidateSnapshotReloadFolder(string folder)
        {
            const string prefix = "Assets/UCSnapshotDomainReload_";
            Guid id;
            if (folder == null || !folder.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(folder.Substring(prefix.Length), "N", out id))
                throw new InvalidOperationException("The saved domain fixture path is not a scoped UC fixture.");
        }

        private static void CleanupSnapshotDomainReloadAssets(string folder)
        {
            ValidateSnapshotReloadFolder(folder);
            Scene fixture = SceneManager.GetSceneByPath(folder + "/World.unity");
            if (SessionState.GetBool(SnapshotReloadKey + "Single", false))
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            else if (fixture.IsValid() && fixture.isLoaded && !EditorSceneManager.CloseScene(fixture, true))
                throw new InvalidOperationException("Could not close the domain fixture scene during cleanup.");
            string originalPath = SessionState.GetString(SnapshotReloadKey + "Original", string.Empty);
            Scene original = SceneManager.GetSceneByPath(originalPath);
            if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            // UC-T01-07: cleanup is part of acceptance; Unity can report failure without throwing.
            if (AssetDatabase.IsValidFolder(folder) && !AssetDatabase.DeleteAsset(folder))
                throw new InvalidOperationException("Could not remove the scoped domain fixture assets during cleanup.");
            foreach (string key in new[] { "Folder", "Original", "source", "curve", "raster", "removedRaster", "lithology", "density", "climate" })
                SessionState.EraseString(SnapshotReloadKey + key);
            SessionState.EraseBool(SnapshotReloadKey + "Single"); SessionState.EraseInt(SnapshotReloadKey + "Checks");
        }

        private static void ExitSnapshotDomainReload()
        {
            bool ok = failures.Count == 0;
            Debug.Log(Report(ok)); Console.WriteLine(Report(ok)); EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
