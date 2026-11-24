// UC-T06-09: verify the actual native update and pump, not a parallel appearance-only mock.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestVegetationAppearance()
        {
            var root = new GameObject("UC appearance acceptance"); root.SetActive(false);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(16, 40, 16) }; data.SetDetailResolution(16, 8);
            var terrainRoot = Terrain.CreateTerrainGameObject(data); terrainRoot.transform.SetParent(root.transform, false);
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube); prefab.SetActive(false);
            var texture = new Texture2D(2, 2);
            var graph = ScriptableObject.CreateInstance<Graph>(); graph.SetDomain(UCGraphDomain.TerrainWater);
            var spawned = new List<GameObject>();
            GenerationSnapshot first = null, second = null;
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var manager = root.AddComponent<ChunkManager>(); manager.Initialize(world);
                var vegetation = root.AddComponent<UCVegetationSystemManager>(); vegetation.enableSeasonalPhenology = true;
                vegetation.seasonFollowsWeather = false; vegetation.manualDayOfYear = 180;
                var library = world.scatterLibrary;
                library.trees.Add(new TreePrototypeDef { prefab = prefab }); library.trees.Add(new TreePrototypeDef { prefab = prefab });
                library.shrubs.Add(new ShrubPrototypeDef { prefab = prefab });
                library.details.Add(new DetailPrototypeDef { billboardTexture = texture });
                foreach (var definition in library.trees) { definition.eco.leafOffFraction = 0; definition.eco.summerColour = Color.green; }
                library.details[0].eco.leafOffFraction = 0; library.details[0].eco.summerColour = Color.green;
                var a = IdentityInstance("appearance", "trees:layer", "species-a", 4, 4);
                var b = IdentityInstance("appearance", "trees:layer", "species-b", 8, 8); b.prototypeIndex = 1;
                var bag = new OutputBag(); bag.AddScatter("trees", new List<ScatterInstance> { a, b });
                var detailGrid = new int[16, 16]; detailGrid[8, 8] = 3; bag.detailLayers[0] = detailGrid;
                var terrain = terrainRoot.GetComponent<Terrain>();
                ScatterApplier.Apply(terrain, data, bag, library, null, null, 9009, vegetation, spawned);
                Check(data.treeInstanceCount == 2, "UC-T06-09 native appearance fixture commits both actual tree species.");
                var before = data.treeInstances;
                int density = data.GetDetailLayer(8, 8, 1, 1, 0)[0, 0];
                var nativeDetail = data.detailPrototypes[0];
                library.trees[0].eco.summerColour = Color.red; library.trees[1].eco.summerColour = Color.blue;
                library.details[0].eco.summerColour = Color.yellow;
                long placement = vegetation.PlacementRevision, appearance = vegetation.AppearanceRevision;
                vegetation.MarkAppearanceDirty();
                var update = new ScatterApplier.AppearanceUpdate(terrain, data, library, vegetation);
                Check(!update.Step(1) && update.Step(1), "UC-T06-09 actual native colour updates obey a one-tree work bound and complete after two steps.");
                bool retained = true;
                for (int i = 0; i < before.Length; i++)
                {
                    var after = data.GetTreeInstance(i);
                    retained &= after.position == before[i].position && after.rotation == before[i].rotation && after.widthScale == before[i].widthScale &&
                        after.heightScale == before[i].heightScale && after.prototypeIndex == before[i].prototypeIndex && after.lightmapColor.Equals(before[i].lightmapColor);
                }
                Check(retained && data.GetTreeInstance(0).color.r == 255 && data.GetTreeInstance(1).color.b == 255 &&
                    vegetation.PlacementRevision == placement && vegetation.AppearanceRevision > appearance && bag.scatter["trees"][0].identity.Equals(a.identity),
                    "UC-T06-09 native tint retains transforms, native slots, full accepted identities and exact species provenance even for a shared prefab.");
                var afterDetail = data.detailPrototypes[0];
                Check(data.GetDetailLayer(8, 8, 1, 1, 0)[0, 0] == density && afterDetail.minHeight == nativeDetail.minHeight &&
                    afterDetail.maxHeight == nativeDetail.maxHeight && afterDetail.healthyColor == Color.yellow,
                    "UC-T06-09 native detail appearance keeps accepted density/geometry while updating its real tint consumer.");

                var shrubRoot = UnityEngine.Object.Instantiate(prefab); spawned.Add(shrubRoot);
                var shrub = shrubRoot.AddComponent<UCShrubInstance>(); shrub.Configure(library.shrubs[0], 2, true);
                var collider = shrubRoot.GetComponent<CapsuleCollider>(); Vector3 position = shrubRoot.transform.position;
                library.shrubs[0].windResponse = 1.75f; shrub.RefreshAppearance();
                var block = new MaterialPropertyBlock(); shrubRoot.GetComponent<Renderer>().GetPropertyBlock(block);
                Check(shrubRoot.GetComponent<CapsuleCollider>() == collider && shrubRoot.transform.position == position &&
                    block.GetFloat(Shader.PropertyToID("_WindResponse")) == 1.75f,
                    "UC-T06-09 cached shrub material uploads change wind without recreating collision or transforms.");

                Type chunkType = typeof(ChunkManager).GetNestedType("Chunk", BindingFlags.NonPublic);
                object chunk = Activator.CreateInstance(chunkType, true);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                SetCommitField(chunkType, chunk, "go", terrainRoot); SetCommitField(chunkType, chunk, "terrain", terrain); SetCommitField(chunkType, chunk, "data", data);
                SetCommitField(chunkType, chunk, "bakedThisSession", true); SetCommitField(chunkType, chunk, "applied", true);
                SetCommitField(chunkType, chunk, "retainedScatter", bag); SetCommitField(chunkType, chunk, "scatterObjects", spawned);
                ((IList)typeof(ChunkManager).GetField("pool", flags).GetValue(manager)).Add(chunk);
                ((IDictionary)typeof(ChunkManager).GetField("byCell", flags).GetValue(manager)).Add(new Coord(0, 0), chunk);
                root.SetActive(true);
                vegetation.MarkAppearanceDirty();
                Check(manager.HasWork && manager.PumpCompleted(), "UC-T06-09 a real explicit appearance request reaches the existing editor/runtime pump.");
                manager.PumpCompleted();
                Check(!manager.HasWork && ReferenceEquals(chunkType.GetField("retainedScatter").GetValue(chunk), bag) &&
                    shrubRoot.GetComponent<CapsuleCollider>() == collider && data.treeInstanceCount == 2,
                    "UC-T06-09 the appearance pump drains without replacing accepted placement, spawned objects or native tree counts.");

                vegetation.EcologyTick(0); placement = vegetation.PlacementRevision; appearance = vegetation.AppearanceRevision;
                vegetation.manualDayOfYear = 210; vegetation.EcologyTick(4);
                Check(vegetation.PlacementRevision == placement && vegetation.AppearanceRevision > appearance,
                    "UC-T06-09 advancing the season invalidates appearance independently of placement.");
                bool invalidTick = false; try { vegetation.EcologyTick(float.NaN); } catch (ArgumentOutOfRangeException) { invalidTick = true; }
                Check(invalidTick && vegetation.PlacementRevision == placement, "UC-T06-09 nonfinite clock input is rejected without placement invalidation.");

                graph.SetDomain(UCGraphDomain.Vegetation); // UC-T06-09: placement hashing must use the admitted plant-authoring domain.
                var node = new TreeScatterNode { id = "appearance-hash-tree" }; graph.nodes.Add(node); world.graph = graph; vegetation.useEcology = true;
                ulong libraryHash = ScatterApplier.PlacementLibraryHash(library);
                first = GenerationSnapshot.Capture(graph, world);
                var capturedNode = first.Graph.GetNodeById(node.id);
                if (capturedNode == null) throw new InvalidOperationException("Captured appearance fixture lost source ID " + node.id +
                    "; copied node IDs: " + string.Join(",", first.Graph.nodes.ConvertAll(item => item.id))); // UC-T06-09: preserve failure and expose the actual copied identity before correcting its cause.
                ulong hash = capturedNode.contentHash;
                library.trees[0].eco.autumnColour = Color.magenta; library.trees[0].eco.leafOffFraction = 0.8f;
                second = GenerationSnapshot.Capture(graph, world);
                Check(second.Graph.GetNodeById(node.id).contentHash == hash && ScatterApplier.PlacementLibraryHash(library) == libraryHash,
                    "UC-T06-09 actual captured graph and library placement hashes exclude pure tint/leaf appearance edits.");
                library.trees[0].weight += 1;
                Check(ScatterApplier.PlacementLibraryHash(library) != libraryHash,
                    "UC-T06-09 prototype selection edits remain real placement inputs rather than disappearing from the hash.");
                TestVegetationInstancedAppearance(prefab);
            }
            finally
            {
                second?.Release(); first?.Release(); ScatterApplier.Release(spawned, null, 9009);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(data); UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestVegetationInstancedAppearance(GameObject prefab)
        {
            var root = new GameObject("UC instanced appearance acceptance");
            try
            {
                var manager = root.AddComponent<UniverseCreator.Optimization.GPUInstancingManager>();
                var mesh = prefab.GetComponent<MeshFilter>().sharedMesh; var material = prefab.GetComponent<Renderer>().sharedMaterial;
                var a = new ShrubPrototypeDef { windResponse = 0.5f, meshHeight = 1.2f };
                var b = new ShrubPrototypeDef { windResponse = 1.5f, meshHeight = 2f };
                manager.AddInstance(42, mesh, material, Matrix4x4.identity, 0, shrubAppearance: a);
                manager.AddInstance(42, mesh, material, Matrix4x4.Translate(Vector3.right), 0, shrubAppearance: b);
                var batches = (IDictionary)typeof(UniverseCreator.Optimization.GPUInstancingManager).GetField("batches", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                Check(batches.Count == 2 && manager.InstanceCount == 2,
                    "UC-T06-09 identical mesh/material owners retain separate compatible batches for different shrub appearance parameters.");
                a.windResponse = 1.75f; manager.RefreshAppearance(42);
                bool updated = false;
                foreach (object batch in batches.Values)
                    if (ReferenceEquals(batch.GetType().GetField("appearance").GetValue(batch), a))
                    {
                        var properties = (MaterialPropertyBlock)batch.GetType().GetField("properties").GetValue(batch);
                        updated = properties.GetFloat(Shader.PropertyToID("_WindResponse")) == 1.75f && properties.GetFloat(Shader.PropertyToID("_PivotHeight")) == 1.2f;
                    }
                Check(updated && manager.InstanceCount == 2,
                    "UC-T06-09 explicit GPU appearance refresh updates the actual cached shader block without replacing matrices; pivot height stays mesh-local.");
                manager.ClearOwner(42);
                Check(manager.InstanceCount == 0 && batches.Count == 0, "UC-T06-09 owner withdrawal retires every distinct appearance batch.");
                manager.AddInstance(7, mesh, material, Matrix4x4.identity, 0);
                manager.AddInstance(7, mesh, material, Matrix4x4.identity, 0, UnityEngine.Rendering.ShadowCastingMode.Off);
                Check(batches.Count == 2, "UC-T06-09 incompatible shadow policies cannot merge merely because mesh/material lookup hashes match.");
                manager.ClearAll();
                var retiredMesh = UnityEngine.Object.Instantiate(mesh); // UC-T06-10: a real submesh keeps this fixture valid under native-input validation.
                manager.AddInstance(8, retiredMesh, material, Matrix4x4.identity, 0);
                UnityEngine.Object.DestroyImmediate(retiredMesh); manager.ClearOwner(8);
                Check(batches.Count == 0 && manager.InstanceCount == 0,
                    "UC-T06-09 destroying a borrowed mesh does not change a registered key's hash or prevent exact owner withdrawal.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
