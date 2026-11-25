// UC-T09-03: exercise real native terrain, window admission and the host-facing bounded landing contract.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestChunkReadiness()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC-T09-03 readiness world"); root.SetActive(false);
            var foreign = new GameObject("UC-T09-03 foreign water world"); foreign.SetActive(false);
            var graph = ScriptableObject.CreateInstance<Graph>();
            var layer = new TerrainLayer(); // UC-T09-03: use the existing native terrain-layer construction contract.
            var native = new List<TerrainData>();
            ChunkManager manager = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false;
                world.autoRegenerate = false; world.chunkCountPerSide = 3; world.worldSizeKm = 0.096f;
                world.maxHeightMeters = 100; world.generateCollider = true; world.terrainLayers = new[] { layer }; world.graph = graph;
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                Type type = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                var pool = (IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager);
                var cells = (IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager);
                for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++)
                {
                    var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 100, 32), terrainLayers = new[] { layer } }; native.Add(data);
                    var cellGround = new GameObject("Terrain_" + x + "_" + z); cellGround.transform.SetParent(root.transform, false);
                    cellGround.transform.position = new Vector3(-48 + x * 32, 0, -48 + z * 32);
                    var terrain = cellGround.AddComponent<UnityEngine.Terrain>(); terrain.terrainData = data;
                    cellGround.AddComponent<TerrainCollider>().terrainData = data;
                    object chunk = Activator.CreateInstance(type, true);
                    SetCommitField(type, chunk, "go", cellGround); SetCommitField(type, chunk, "terrain", terrain);
                    SetCommitField(type, chunk, "data", data); SetCommitField(type, chunk, "resolution", 33);
                    SetCommitField(type, chunk, "cell", new Coord(x, z)); SetCommitField(type, chunk, "lod", LODLevel.High);
                    SetCommitField(type, chunk, "appliedHeights", CommitHeights(33, 0));
                    pool.Add(chunk); cells.Add(new Coord(x, z), chunk);
                }
                typeof(ChunkManager).GetField("built", flags).SetValue(manager, true);
                var window = typeof(ChunkManager).GetMethod("ApplyStreamWindow", flags);
                window.Invoke(manager, new object[] { world });
                bool pending = true;
                foreach (object chunk in pool)
                    pending &= (bool)type.GetField("resident").GetValue(chunk) && !((GameObject)type.GetField("go").GetValue(chunk)).activeSelf;
                Check(pending && manager.TryGetChunkReadiness(new Coord(1, 1), out var initial) && !initial.TerrainAccepted && !initial.Ready,
                    "UC-T09-03 initial authored-graph flat placeholders remain hidden and resident for generation, even with recorded sculpt baselines.");
                Check(!manager.TryGetAcceptedTerrainAt(Vector3.zero, out var pendingGround, out var pendingGroundState) &&
                    pendingGround == null && !pendingGroundState.TerrainAccepted,
                    "UC repair 32: camera ground query rejects native placeholders before acceptance.");
                Check((int)typeof(ChunkManager).GetField("activeWorkers", flags).GetValue(manager) == 0,
                    "UC-T09-03 window/readiness observations never bake synchronously or claim a worker.");

                world.graph = null; window.Invoke(manager, new object[] { world }); root.SetActive(true); Physics.SyncTransforms();
                Check(manager.TryGetChunkReadiness(new Coord(1, 1), out var flat) && flat.TerrainAccepted && flat.CollisionReady && flat.Visible && flat.Ready,
                    "UC-T09-03 intentional graphless native flat terrain is complete and collidable without waiting for a generation callback.");
                int request = manager.RequestLanding(Vector3.zero, 1, 30);
                Check(request > 0 && manager.GetLandingStatus(request) == ChunkLandingStatus.Ready && native.Count == 9,
                    "UC-T09-03 bounded landing observes all nine native cells without changing terrain allocation or starting a synchronous bake.");
                object centre = cells[new Coord(1, 1)];
                var acceptedGround = (UnityEngine.Terrain)type.GetField("terrain").GetValue(centre);
                Check(manager.TryGetAcceptedTerrainAt(new Vector3(0, 150, 0), out var queriedGround, out var queriedState) &&
                    queriedGround == acceptedGround && queriedGround.terrainData == native[4] && queriedState.Ready && queriedState.Visible,
                    "UC repair 32: camera height does not change accepted native ground selection or its TerrainData identity.");
                Check(!manager.TryGetAcceptedTerrainAt(new Vector3(float.NaN, 0, 0), out _, out _) &&
                    !manager.TryGetAcceptedTerrainAt(new Vector3(0, float.PositiveInfinity, 0), out _, out _) &&
                    !manager.TryGetAcceptedTerrainAt(new Vector3(0, 0, float.NegativeInfinity), out _, out _) &&
                    !manager.TryGetAcceptedTerrainAt(new Vector3(1000, 0, 0), out var outsideGround, out var outsideState) &&
                    outsideGround == null && !outsideState.Resident &&
                    (int)typeof(ChunkManager).GetField("activeWorkers", flags).GetValue(manager) == 0 && native.Count == 9,
                    "UC repair 32: invalid/outside camera queries return no ground and never allocate or start a bake.");
                var collider = ((GameObject)type.GetField("go").GetValue(centre)).GetComponent<TerrainCollider>(); collider.enabled = false;
                Check(manager.GetLandingStatus(request) == ChunkLandingStatus.Pending,
                    "UC-T09-03 missing active matching collision holds the host landing even when heights and visible terrain exist.");
                collider.enabled = true;
                typeof(ChunkManager).GetField("settleStitchPending", flags).SetValue(manager, true);
                Check(manager.GetLandingStatus(request) == ChunkLandingStatus.Pending,
                    "UC-T09-03 unfinished seam/water settlement holds a landing rather than certifying height-only readiness.");
                typeof(ChunkManager).GetField("settleStitchPending", flags).SetValue(manager, false);

                // UC-T09-03: exercise the production-created surface/horizon, not a renderer stand-in.
                var ocean = JunctionLevelBody(root.transform, "Owned readiness ocean", new Vector3(0, 10, 0), WaterBodyKind.Ocean);
                ocean.digTerrain = false; ocean.generateVolume = false; ocean.bedSource = UCWaterBody.BedSource.FlatFloor;
                ocean.meshResolution = 8; ocean.horizonDistanceMetres = 24; ocean.RebuildSurface();
                var otherWorld = foreign.AddComponent<UniverseCreatorObject>(); otherWorld.enabled = false; otherWorld.autoRegenerate = false;
                var other = JunctionLevelBody(foreign.transform, "Foreign readiness lake", new Vector3(0, 10, 0), WaterBodyKind.Lake);
                other.digTerrain = false; other.generateVolume = false; other.bedSource = UCWaterBody.BedSource.FlatFloor;
                other.meshResolution = 8; other.RebuildSurface();
                var waterRenderers = ocean.GetComponentsInChildren<MeshRenderer>(true);
                var otherRenderer = other.GetComponentInChildren<MeshRenderer>(true);
                Check(ocean.LastBuild != null && waterRenderers.Length == 2 && otherRenderer != null,
                    "UC-T09-03 readiness fixtures use actual published water surface and ocean horizon renderers.");
                var ground = (UnityEngine.Terrain)type.GetField("terrain").GetValue(centre);
                ground.drawTreesAndFoliage = false;
                typeof(ChunkManager).GetField("waterPresentationPending", flags).SetValue(manager, true);
                typeof(ChunkManager).GetField("readinessFailure", flags).SetValue(manager, "Readiness fixture retained water failure");
                typeof(ChunkManager).GetMethod("RequireReadinessRefresh", flags).Invoke(manager, new object[] { new Coord(1, 1) });
                manager.PumpCompleted();
                bool waterHeld = waterRenderers.Length == 2;
                foreach (var renderer in waterRenderers) waterHeld &= renderer.forceRenderingOff && renderer.enabled;
                bool hit = collider.Raycast(new Ray(new Vector3(0, 150, 0), Vector3.down), out _, 200);
                Check(waterHeld && hit && collider.enabled && ground.gameObject.activeInHierarchy && !ground.drawHeightmap &&
                    otherRenderer != null && !otherRenderer.forceRenderingOff && manager.GetLandingStatus(request) == ChunkLandingStatus.Pending,
                    "UC-T09-03 changed water/terrain presentation is hidden while accepted native ground/collision remain available, with no foreign-world suppression.");
                Check(manager.TryGetAcceptedTerrainAt(Vector3.zero, out var hiddenGround, out var hiddenGroundState) &&
                    hiddenGround == ground && hiddenGroundState.TerrainAccepted && !hiddenGroundState.Visible && !hiddenGroundState.DependentsReady,
                    "UC repair 32: camera query separates hidden/pending presentation from accepted ground without revealing it.");
                manager.PumpCompleted();
                Check(manager.ReadinessDiagnostic.Contains("retained water failure") && !ground.drawHeightmap,
                    "UC-T09-03 idle pumps retain a real dependency failure instead of releasing incoherent presentation.");
                typeof(ChunkManager).GetField("readinessFailure", flags).SetValue(manager, null);
                SetCommitField(type, centre, "generationFailure", "Readiness fixture retained generation failure");
                manager.PumpCompleted();
                Check(manager.ReadinessDiagnostic.Contains("retained generation failure") && manager.GetLandingStatus(request) == ChunkLandingStatus.Pending,
                    "UC-T09-03 current resident generation failure cannot certify old accepted collision as a completed replacement.");
                SetCommitField(type, centre, "generationFailure", null);
                var bindings = root.AddComponent<UCWorldGraphBindings>(); bindings.enabled = false;
                var completion = new TaskCompletionSource<bool>();
                typeof(UCWorldGraphBindings).GetField("pendingTask", flags).SetValue(bindings, completion.Task);
                typeof(ChunkManager).GetMethod("RequireReadinessRefresh", flags).Invoke(manager, new object[] { new Coord(1, 1) });
                manager.PumpCompleted();
                Check(!ground.drawHeightmap && manager.GetLandingStatus(request) == ChunkLandingStatus.Pending,
                    "UC-T09-03 an explicit companion evaluation also holds changed terrain/water presentation beneath a stationary observer.");
                completion.SetResult(true);
                typeof(UCWorldGraphBindings).GetField("<StreamingVegetationFailed>k__BackingField", flags).SetValue(bindings, true);
                manager.PumpCompleted();
                Check(!ground.drawHeightmap && manager.GetLandingStatus(request) == ChunkLandingStatus.Pending,
                    "UC-T09-03 completed failed companion work does not count as accepted vegetation.");
                typeof(UCWorldGraphBindings).GetField("<StreamingVegetationFailed>k__BackingField", flags).SetValue(bindings, false);
                typeof(ChunkManager).GetMethod("NotifyReadinessDependencyCompleted", flags).Invoke(manager, null);
                manager.PumpCompleted();
                bool waterReady = true;
                foreach (var renderer in waterRenderers) waterReady &= !renderer.forceRenderingOff && renderer.enabled;
                Check(waterReady && ground.drawHeightmap && !ground.drawTreesAndFoliage && manager.GetLandingStatus(request) == ChunkLandingStatus.Ready && !manager.HasWork,
                    "UC-T09-03 stationary successful completion restores owned water/terrain once, preserves authored foliage state and leaves the pump idle.");
                var resources = (IList)typeof(ChunkManager).GetField("readinessWater", flags).GetValue(manager);
                ocean.Clear();
                Check(resources.Count == 0 && ocean.GetComponentsInChildren<MeshRenderer>(true).Length == 0,
                    "UC-T09-03 water retirement removes its native renderers and registration without retaining another world's body.");
                other.Clear();
                SetCommitField(type, centre, "publicationInProgress", true);
                Check(manager.TryGetChunkReadiness(new Coord(1, 1), out var publishing) && !publishing.TerrainAccepted && !publishing.Ready,
                    "UC-T09-03 a native upload inside the content-publication transaction cannot be reported as accepted ground.");
                Check(!manager.TryGetAcceptedTerrainAt(Vector3.zero, out var uploadingGround, out _) && uploadingGround == null,
                    "UC repair 32: camera queries cannot expose an unaccepted native upload.");
                SetCommitField(type, centre, "publicationInProgress", false);
                Check(manager.RequestLanding(Vector3.zero, 2) == 0 && manager.RequestLanding(new Vector3(float.NaN, 0, 0)) == 0 &&
                    manager.RequestLanding(new Vector3(1000, 0, 0)) == 0 && manager.RequestLanding(Vector3.zero, 1, float.PositiveInfinity) == 0,
                    "UC-T09-03 landing rejects over-budget footprints, nonfinite inputs and out-of-world positions without replacing valid demand.");
                manager.CancelLanding(request);
                Check(manager.GetLandingStatus(request) == ChunkLandingStatus.Cancelled && flat.TerrainAccepted,
                    "UC-T09-03 cancellation removes only the matching landing contract and keeps accepted terrain.");
                request = manager.RequestLanding(Vector3.zero, 0);
                typeof(ChunkManager).GetField("landingExpires", flags).SetValue(manager, Time.realtimeSinceStartupAsDouble - 1);
                Check(manager.GetLandingStatus(request) == ChunkLandingStatus.Expired,
                    "UC-T09-03 expiry uses unscaled time and cannot silently grant a completed landing.");
                request = manager.RequestLanding(Vector3.zero, 0); manager.CancelAll();
                Check(manager.GetLandingStatus(request) == ChunkLandingStatus.Cancelled,
                    "UC-T09-03 restart/rebind cancels old landing IDs instead of reusing readiness from another run.");
                world.generateCollider = false;
                Check(manager.RequestLanding(Vector3.zero) == 0 && !manager.TryGetChunkReadiness(new Coord(3, 3), out _),
                    "UC-T09-03 collision-disabled authoring and unknown finite-world cells cannot admit a collision-critical landing.");
            }
            finally
            {
                manager?.CancelAll(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(foreign);
                foreach (var data in native) if (data != null) UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(graph); UnityEngine.Object.DestroyImmediate(layer);
            }
            TestChunkCameraAndWindow(); // UC Scene/Play repair: native view ownership and finite Dual LOD boundaries.
        }

        private static void TestChunkCameraAndWindow()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = new GameObject("UC camera/window world"); root.SetActive(false);
            var views = new GameObject("UC native host views");
            var firstTarget = new RenderTexture(256, 144, 0);
            var resizedTarget = new RenderTexture(256, 288, 0);
            var secondTarget = new RenderTexture(256, 216, 0);
            ChunkManager manager = null;
            Camera first = null, second = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var host = new GameObject("ChunkManager"); host.transform.SetParent(root.transform, false);
                manager = host.AddComponent<ChunkManager>(); manager.enabled = false; manager.Initialize(world);
                var firstObject = new GameObject("Explicit host camera"); firstObject.transform.SetParent(views.transform, false);
                first = firstObject.AddComponent<Camera>(); first.fieldOfView = 37; first.targetTexture = firstTarget;
                first.nearClipPlane = 0.2f; first.farClipPlane = 120000; first.cullingMask = 1 << 8;
                first.transform.position = new Vector3(123, 120, -450);
                var secondObject = new GameObject("Second host camera"); secondObject.transform.SetParent(views.transform, false);
                if (Camera.main == null) secondObject.tag = "MainCamera";
                second = secondObject.AddComponent<Camera>(); second.fieldOfView = 73; second.targetTexture = secondTarget;
                second.transform.position = new Vector3(-200, 120, 300);
                var resolve = typeof(ChunkManager).GetMethod("ResolveView", flags);
                object[] ReadView()
                {
                    var args = new object[] { Vector3.zero, 0f, 0 }; resolve.Invoke(manager, args); return args;
                }
                manager.focus = first.transform;
                object[] initial = ReadView();
                Check((Vector3)initial[0] == first.transform.position && (float)initial[1] == 37 && (int)initial[2] == 144,
                    "UC Scene/Play repair: density uses the explicit host camera position, vertical lens and render-target height.");
                var density = new TerrainDensityController();
                bool initialWrite = density.TryResolve(0, 0, 2, 1, 25, (float)initial[1], (int)initial[2], 1, 0.75f, out float near);
                bool idleWrite = density.TryResolve(0, 0, 2, 1, 25, (float)initial[1], (int)initial[2], 1, 0.75f, out _);
                first.fieldOfView = 42; first.targetTexture = resizedTarget;
                object[] resized = ReadView();
                bool lensWrite = density.TryResolve(0, 0, 2, 1, 25, (float)resized[1], (int)resized[2], 1, 0.75f, out float zoomed);
                density.TryResolve(1, 0, 2, 1, 25000, (float)resized[1], (int)resized[2], 1, 0.75f, out float far);
                Check((float)resized[1] == 42 && (int)resized[2] == 288 && initialWrite && !idleWrite && lensWrite &&
                    zoomed > near && far >= 1 && !float.IsNaN(far) && !float.IsInfinity(zoomed),
                    "UC Scene/Play repair: a stationary zoom/target change updates bounded screen-error density while an idle view writes nothing.");
                manager.focus = second.transform;
                object[] switched = ReadView();
                Check((Vector3)switched[0] == second.transform.position && (float)switched[1] == 73 && (int)switched[2] == 216,
                    "UC Scene/Play repair: switching focus cameras replaces cached lens ownership without a terrain bake.");
                Camera main = Camera.main;
                manager.focus = world.transform;
                object[] noncamera = ReadView();
                Check(main != null && (Vector3)noncamera[0] == world.transform.position && (float)noncamera[1] == main.fieldOfView && (int)noncamera[2] == main.pixelHeight,
                    "UC Scene/Play repair: a host actor focus retains its position and uses the main-camera lens fallback.");
                manager.focus = null;
                object[] fallback = ReadView();
                Check(main != null && (Vector3)fallback[0] == main.transform.position && (float)fallback[1] == main.fieldOfView && (int)fallback[2] == main.pixelHeight,
                    "UC Scene/Play repair: an unassigned focus resolves the host main camera without creating a camera or character.");
                Check(first.cullingMask == 1 << 8 && first.nearClipPlane == 0.2f && first.farClipPlane == 120000,
                    "UC Scene/Play repair: view resolution preserves authored culling layers and clipping planes for a large world.");

                var fits = typeof(UniverseCreatorObject).GetMethod("FitsStreamWindow", flags);
                bool Fits() => (bool)fits.Invoke(world, null);
                world.chunkSizeMeters = 1000; world.worldSizeKm = 7; world.residentWindowPerSide = 5; world.bufferRingWidth = 1; world.ValidateSettings();
                Check(world.GetStreamRadius() == 3 && Fits(),
                    "UC Scene/Play repair: default five-by-five interior plus one surrounding buffer ring fits a seven-cell side, not 26 total terrains.");
                world.worldSizeKm = 8; world.ValidateSettings();
                Check(!Fits() && world.ShouldRenderWholeWorld(),
                    "UC Scene/Play repair: an eight-cell side exceeds the buffered runtime window while edit-mode authoring still keeps the whole world.");
                world.worldSizeKm = 4; world.residentWindowPerSide = 4; world.bufferRingWidth = 0; world.ValidateSettings();
                Check(world.residentWindowPerSide == 3 && world.GetStreamRadius() == 1 && !Fits(),
                    "UC Scene/Play repair: an even capped world retains an odd resident window without admitting an extra row or column.");
                world.worldSizeKm = 1; world.residentWindowPerSide = 1; world.ValidateSettings();
                Check(world.GetStreamRadius() == 0 && Fits(),
                    "UC Scene/Play repair: a one-cell world with no buffer stays whole without manufacturing neighboring cells.");
                world.worldSizeKm = 2; world.residentWindowPerSide = 2; world.ValidateSettings();
                Check(world.residentWindowPerSide == 1 && world.GetStreamRadius() == 0 && !Fits(),
                    "UC Scene/Play repair: a one-cell runtime window cannot silently retain a larger two-cell world.");
                world.worldSizeKm = 56; world.residentWindowPerSide = 40; world.ValidateSettings();
                Check(world.residentWindowPerSide == UniverseCreatorObject.MaxResidentWindowPerSide && !Fits(),
                    "UC Scene/Play repair: large-world residency honors the existing finite window cap.");
                Check(DualLODSystem.Classify(0, 3, 5) == LODLevel.High && DualLODSystem.Classify(1, 3, 5) == LODLevel.High &&
                    DualLODSystem.Classify(2, 3, 5) == LODLevel.Low && DualLODSystem.Classify(3, 3, 5) == LODLevel.Buffer,
                    "UC Scene/Play repair: Dual LOD retains the detailed central square, outer ring and precomputed buffer.");
                bool commonGrid = true;
                foreach (int resolution in new[] { 33, 257, 1025 }) foreach (LODLevel level in new[] { LODLevel.High, LODLevel.Low, LODLevel.Buffer })
                    commonGrid &= DualLODSystem.ResolutionFor(level, resolution) == resolution;
                Check(commonGrid && DualLODSystem.ShouldHaveCollider(LODLevel.High, true) && DualLODSystem.ShouldHaveCollider(LODLevel.Low, true) &&
                    !DualLODSystem.ShouldHaveCollider(LODLevel.Buffer, true) && !DualLODSystem.ShouldHaveCollider(LODLevel.High, false),
                    "UC Scene/Play repair: all tiers share the native sample grid and both visible tiers retain default collision.");
                Check(!manager.HasWork && (int)typeof(ChunkManager).GetField("activeWorkers", flags).GetValue(manager) == 0,
                    "UC Scene/Play repair: camera, density and window observations do not start graph execution or allocate terrain.");
            }
            finally
            {
                if (first != null) first.targetTexture = null;
                if (second != null) second.targetTexture = null;
                manager?.CancelAll(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(views);
                UnityEngine.Object.DestroyImmediate(firstTarget); UnityEngine.Object.DestroyImmediate(resizedTarget); UnityEngine.Object.DestroyImmediate(secondTarget);
            }
        }
    }
}
