// UC-T04-07: never-active scene objects still retire their owned native water on scene/domain unload.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Nodes.Creators; // UC-T04-07: native and graph-course ownership retire together.

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWaterBody
    {
        // Main-thread publication owner only. Handles survive destruction long enough
        // to retire Mesh/Material objects; this is not a static hydrology/geometry cache.
        private readonly struct WaterPublicationOwner
        {
            internal readonly ulong SceneHandle; // UC-T04-07: preserve Unity 6000.5's full scene identity without obsolete int conversions.
            internal readonly int CourseId;
            internal WaterPublicationOwner(ulong sceneHandle, int courseId) { SceneHandle = sceneHandle; CourseId = courseId; }
        }
        private static readonly Dictionary<UCWaterBody, WaterPublicationOwner> nativeWaterPublications = new Dictionary<UCWaterBody, WaterPublicationOwner>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeWaterPublicationLifetime()
        {
            RetireAllWaterPublications();
            SceneManager.sceneUnloaded -= RetireWaterScene;
            SceneManager.sceneUnloaded += RetireWaterScene;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeEditorWaterPublicationLifetime()
        {
            SceneManager.sceneUnloaded -= RetireWaterScene;
            SceneManager.sceneUnloaded += RetireWaterScene;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing -= RetireClosingWaterScene;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing += RetireClosingWaterScene;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= RetireAllWaterPublications;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += RetireAllWaterPublications;
        }

        private static void RetireClosingWaterScene(Scene scene, bool removingScene) => RetireWaterScene(scene);
#endif

        private static void RetireWaterScene(Scene scene)
        {
            var retiring = new List<UCWaterBody>();
            var moved = new List<UCWaterBody>();
            foreach (var pair in nativeWaterPublications)
            {
                // A live body moved into another scene keeps its current publication.
                ulong ownerScene = pair.Key != null && pair.Key.gameObject.scene.IsValid() ? pair.Key.gameObject.scene.handle.GetRawData() : pair.Value.SceneHandle;
                if (ownerScene == scene.handle.GetRawData()) retiring.Add(pair.Key);
                else if (ownerScene != pair.Value.SceneHandle) moved.Add(pair.Key);
            }
            foreach (var body in moved) nativeWaterPublications[body] = new WaterPublicationOwner(body.gameObject.scene.handle.GetRawData(), nativeWaterPublications[body].CourseId);
            foreach (var body in retiring) RetireWaterPublication(body);
        }

        private static void RetireAllWaterPublications()
        {
            var retiring = new List<UCWaterBody>(nativeWaterPublications.Keys);
            foreach (var body in retiring) RetireWaterPublication(body);
        }

        private static void RetireWaterPublication(UCWaterBody body)
        {
            // The saved course key is usable after Unity has destroyed a never-active
            // object; querying its native EntityId at sceneUnloaded would be too late.
            RiverCourseInputNode.UnpublishBodyCourse(nativeWaterPublications[body].CourseId);
            WaterCarveRegistry.Unregister(body);
            body.ClearNativeGeometry(); body.ClearWaterJunctions();
        }
    }
}
