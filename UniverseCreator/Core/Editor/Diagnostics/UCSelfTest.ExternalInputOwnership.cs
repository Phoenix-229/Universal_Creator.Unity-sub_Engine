using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Geology;
using Object = UnityEngine.Object;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // UC-T01-06/07: hash the copied values while leaving serialized source-asset IDs stable across captures.
        private static void TestSnapshotLithologyOwnership()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            LithologyProfile profile = ScriptableObject.CreateInstance<LithologyProfile>();
            GenerationSnapshot first = null, same = null, edited = null, structural = null, removed = null;
            GenerationSnapshot.Lease lease = null;
            TileData consumer = null;
            try
            {
                profile.PopulateExampleColumn();
                foreach (LithoUnit unit in profile.units) unit.erodibilityKlsu = 1f;
                var source = new ConstantNode { id = "source", value = 0.5f };
                var lithology = new LithologyOutput { id = "lithology", profile = profile };
                var micro = new MicroReliefOutput { id = "micro", profile = profile };
                var erosion = new LithologyErosionNode { id = "erosion", profile = profile, strength = 0.7f };
                graph.nodes.AddRange(new Generator[] { source, lithology, micro, erosion, new HeightOutputNode { id = "height" } });
                graph.connections.Add(new Connection(source.id, 0, lithology.id, 0));
                graph.connections.Add(new Connection(source.id, 0, micro.id, 0));
                graph.connections.Add(new Connection(source.id, 0, erosion.id, 0));
                first = GenerationSnapshot.Capture(graph);
                same = GenerationSnapshot.Capture(graph);
                LithologyProfile captured = ((ILithologySnapshotInput)first.Graph.GetNodeById("lithology")).CapturedProfile;
                Check(!ReferenceEquals(captured, profile) && ReferenceEquals(captured,
                      ((ILithologySnapshotInput)first.Graph.GetNodeById("micro")).CapturedProfile) && ReferenceEquals(captured,
                      ((ILithologySnapshotInput)first.Graph.GetNodeById("erosion")).CapturedProfile),
                    "UC-T01-06 three geology consumers share one detached native profile per run.");
                Check(!ReferenceEquals(captured.units, profile.units) && !ReferenceEquals(captured.units[0], profile.units[0]) &&
                      !ReferenceEquals(captured.structure, profile.structure),
                    "UC-T01-06 lithology units and structural inputs are detached from the authored asset.");
                Check(first.Graph.GetNodeById("lithology").contentHash == same.Graph.GetNodeById("lithology").contentHash &&
                      first.Graph.GetNodeById("micro").contentHash == same.Graph.GetNodeById("micro").contentHash &&
                      first.Graph.GetNodeById("erosion").contentHash == same.Graph.GetNodeById("erosion").contentHash,
                    "UC-T01-07 unchanged profile captures retain stable hashes despite new native copies.");
                var retained = new Dictionary<string, RetainedProduct>();
                Matrix old = SnapshotPull(first.Graph, "lithology", 3, retained);
                SnapshotPull(first.Graph, "micro", 0, retained);
                SnapshotPull(first.Graph, "erosion", 0, retained);
                foreach (LithoUnit unit in profile.units) unit.erodibilityKlsu = 3f;
                edited = GenerationSnapshot.Capture(graph);
                Task<Matrix> oldWorker = Task.Run(() => SnapshotPull(first.Graph, "lithology", 3));
                if (!oldWorker.Wait(5000)) throw new TimeoutException("Copied lithology worker did not finish.");
                Check(SameSnapshotSamples(old, oldWorker.Result) && captured.units[0].erodibilityKlsu == 1f,
                    "UC-T01-06 a worker reads its captured profile after in-place source-asset edits.");
                bool equal = true;
                foreach (string id in new[] { "lithology", "micro", "erosion" })
                {
                    int port = id == "lithology" ? 3 : 0;
                    equal &= SameSnapshotSamples(SnapshotPull(edited.Graph, id, port, retained), SnapshotPull(edited.Graph, id, port));
                    equal &= edited.Graph.GetNodeById(id).contentHash != first.Graph.GetNodeById(id).contentHash;
                }
                Check(equal && !SameSnapshotSamples(old, SnapshotPull(edited.Graph, "lithology", 3)) &&
                      edited.Graph.GetNodeById("source").contentHash == first.Graph.GetNodeById("source").contentHash,
                    "UC-T01-07 lithology edits refresh all three consumers and retained results match cold results without invalidating the source.");
                profile.structure.datumElevationM += 0.01f;
                structural = GenerationSnapshot.Capture(graph);
                Check(structural.Graph.GetNodeById("lithology").contentHash != edited.Graph.GetNodeById("lithology").contentHash &&
                      captured.structure.datumElevationM != profile.structure.datumElevationM,
                    "UC-T01-07 sub-metre structural edits invalidate captured geology without changing existing inputs.");
                lithology.profile = null; micro.profile = null; erosion.profile = null;
                removed = GenerationSnapshot.Capture(graph);
                Check(removed.Graph.GetNodeById("lithology").contentHash != structural.Graph.GetNodeById("lithology").contentHash &&
                      SnapshotPull(removed.Graph, "lithology", 3, retained).arr[0] == 0f,
                    "UC-T01-07 removing a lithology profile clears its former retained result.");

                lease = first.Acquire();
                consumer = SnapshotTile(first.Graph); consumer.RetainGenerationInputs(first);
                first.Release();
                Check(captured != null, "UC-T01-06 copied native profiles remain alive after owner cancellation while consumers retain leases.");
                lease.Dispose(); lease = null;
                Check(captured != null, "UC-T01-06 a published tile retains copied profiles after its last worker retires.");
                consumer.ReleaseGenerationInputs(); consumer = null;
                Check(captured == null && profile != null,
                    "UC-T01-06 final tile release destroys the copied native profile and preserves the authored asset.");

                lithology.profile = profile;
                graph.nodes.Add(new SnapshotPreparationFailureProbe());
                int profileCount = Resources.FindObjectsOfTypeAll<LithologyProfile>().Length;
                int graphCount = Resources.FindObjectsOfTypeAll<Graph>().Length;
                bool failed = false;
                try { GenerationSnapshot.Capture(graph).Release(); }
                catch (InvalidOperationException exception) { failed = exception.Message == "UC snapshot preparation failure probe"; }
                Check(failed && Resources.FindObjectsOfTypeAll<LithologyProfile>().Length == profileCount &&
                      Resources.FindObjectsOfTypeAll<Graph>().Length == graphCount && lithology.profile == profile,
                    "UC-T01-06 failed preparation releases copied native profiles/graph and preserves the authored binding.");
            }
            finally
            {
                if (lease != null) lease.Dispose(); if (consumer != null) consumer.ReleaseGenerationInputs();
                if (first != null) first.Release(); if (same != null) same.Release(); if (edited != null) edited.Release();
                if (structural != null) structural.Release(); if (removed != null) removed.Release();
                Object.DestroyImmediate(graph); Object.DestroyImmediate(profile);
            }
        }

        private static bool SameSnapshotSamples(Matrix a, Matrix b)
        {
            if (a == null || b == null || a.arr.Length != b.arr.Length) return false;
            for (int i = 0; i < a.arr.Length; i++) if (a.arr[i] != b.arr[i]) return false;
            return true;
        }

        [Serializable]
        private sealed class SnapshotPreparationFailureProbe : Generator
        {
            public override string NodeName { get { return "UC snapshot failure fixture"; } }
            public override NodeCategory Category { get { return NodeCategory.Utility; } }
            public override Port[] GetInputs() { return SharedPorts.None; }
            public override Port[] GetOutputs() { return SharedPorts.None; }
            public override object Generate(int outputIndex, TileData tile, StopToken stop) { return null; }
            public override void PrepareMainThread() { throw new InvalidOperationException("UC snapshot preparation failure probe"); }
        }

        // UC-T01-06/07: reproduce nested-world selection, ocean edits/removal and retained captured settings.
        private static void TestSnapshotManagerOwnership()
        {
            GameObject root = new GameObject("UC owned-input parent");
            root.SetActive(false);
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            GameObject standalone = new GameObject("UC unrelated ocean");
            GenerationSnapshot first = null, nested = null, edited = null, removed = null;
            try
            {
                UniverseCreatorObject world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false;
                GameObject child = new GameObject("UC owned-input nested"); child.transform.SetParent(root.transform, false);
                UniverseCreatorObject childWorld = child.AddComponent<UniverseCreatorObject>(); childWorld.enabled = false;
                UCVegetationSystemManager childVeg = child.AddComponent<UCVegetationSystemManager>();
                childVeg.enabled = false; childVeg.useEcology = true;
                GameObject weatherOwner = new GameObject("UC nested weather"); weatherOwner.transform.SetParent(child.transform, false);
                GameObject lightingOwner = new GameObject("UC nested lighting"); lightingOwner.transform.SetParent(child.transform, false);
                UCWeatherSystemManager childWeather = weatherOwner.AddComponent<UCWeatherSystemManager>(); childWeather.enabled = false;
                UCLightingSystemManager childLighting = lightingOwner.AddComponent<UCLightingSystemManager>(); childLighting.enabled = false;
                UCWaterVolume ocean = root.AddComponent<UCWaterVolume>(); ocean.kind = UCWaterVolume.BodyKind.Ocean; ocean.surfaceLevel = 5f;
                UCWaterVolume childOcean = child.AddComponent<UCWaterVolume>(); childOcean.kind = UCWaterVolume.BodyKind.Ocean; childOcean.surfaceLevel = 15f;
                UCWaterVolume otherOcean = standalone.AddComponent<UCWaterVolume>(); otherOcean.kind = UCWaterVolume.BodyKind.Ocean; otherOcean.surfaceLevel = 80f;
                root.SetActive(true);

                Check(UCVegetationSystemManager.ForWorld(root.transform) == null && EcoScatterContextBuilder.BuildFor(world) == null,
                    "UC-T01-06 a parent without vegetation settings cannot capture its nested world's manager.");
                Check(UCVegetationSystemManager.ForWorld(child.transform) == childVeg,
                    "UC-T01-06 a nested world resolves its own vegetation manager.");
                UCVegetationSystemManager veg = root.AddComponent<UCVegetationSystemManager>();
                veg.enabled = false; veg.useEcology = true;
                Check(veg.Weather == null && veg.Lighting == null && childVeg.Weather == childWeather && childVeg.Lighting == childLighting,
                    "UC-T01-06 weather and lighting inputs do not cross nested-world ownership.");
                Check(veg.SeaLevelY == 5f && childVeg.SeaLevelY == 15f,
                    "UC-T01-06 each world's ecology reads only its own ocean despite overlapping/global volumes.");

                world.graph = graph; childWorld.graph = graph;
                graph.nodes.Add(new ConstantNode { id = "source", value = 0.3f });
                graph.nodes.Add(new BiomeDensityTableNode { id = "density" });
                graph.nodes.Add(new HeightOutputNode { id = "height" });
                first = GenerationSnapshot.Capture(graph, world);
                nested = GenerationSnapshot.Capture(graph, childWorld);
                Check(first.Ecology.settings.seaLevelY == 5f && nested.Ecology.settings.seaLevelY == 15f,
                    "UC-T01-06 captured ecology retains world-scoped ocean inputs.");
                ulong sourceHash = first.Graph.GetNodeById("source").contentHash;
                ulong densityHash = first.Graph.GetNodeById("density").contentHash;
                ocean.surfaceLevel = 7f;
                edited = GenerationSnapshot.Capture(graph, world);
                Check(first.Ecology.settings.seaLevelY == 5f && edited.Ecology.settings.seaLevelY == 7f && nested.Ecology.settings.seaLevelY == 15f,
                    "UC-T01-06 ocean edits affect replacement capture without changing existing or sibling inputs.");
                Check(edited.Graph.GetNodeById("source").contentHash == sourceHash && edited.Graph.GetNodeById("density").contentHash != densityHash,
                    "UC-T01-07 an owned ocean edit invalidates ecology while unrelated terrain remains reusable.");
                ocean.enabled = false;
                removed = GenerationSnapshot.Capture(graph, world);
                Check(removed.Ecology.settings.seaLevelY == 0f && removed.Graph.GetNodeById("density").contentHash != edited.Graph.GetNodeById("density").contentHash,
                    "UC-T01-07 ocean removal clears its captured dependency without borrowing another world's ocean.");

                weatherOwner.transform.SetParent(root.transform, false);
                Check(childVeg.Weather == null && veg.Weather == childWeather,
                    "UC-T01-07 manager reparenting refreshes world ownership on the next resolve.");
            }
            finally
            {
                if (first != null) first.Release(); if (nested != null) nested.Release();
                if (edited != null) edited.Release(); if (removed != null) removed.Release();
                Object.DestroyImmediate(root); Object.DestroyImmediate(standalone); Object.DestroyImmediate(graph);
            }
        }
    }
}
