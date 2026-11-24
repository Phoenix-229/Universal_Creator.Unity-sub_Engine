// UC-T09-04: display-only distant terrain uses dormant accepted content and the existing measured commit pump.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniverseCreator.Nodes; // UC-T09-04: retain the existing shared product-budget owner.

namespace UniverseCreator.TerrainSystem
{
    public partial class ChunkManager
    {
        private sealed class FarTerrainView
        {
            internal Chunk Chunk;
            internal UCTerrainDormantState Source;
            internal GameObject Holder;
            internal Terrain Terrain;
            internal TerrainData Data;
            internal ProductMemoryBudget.Lease Memory;
            internal long Bytes;
        }

        private readonly List<FarTerrainView> farTerrainViews = new List<FarTerrainView>(16);
        private FarTerrainView preparingFarTerrain;
        private IEnumerator<ChunkCommitStep> farTerrainCommit;
        private long farTerrainBytes;
        private int farLastRadius = -1, farLastBudget = -1;

        /// <summary>Number of fully uploaded display-only distant terrains owned by this world, at most 16. Does not certify residency or collision readiness.</summary>
        public int FarTerrainCount => farTerrainViews.Count;
        /// <summary>Estimated live/preparing distant native payload bytes. Deferred destruction remains charged separately by the existing ProductMemory retirement owner.</summary>
        public long FarTerrainBytes => farTerrainBytes;
        /// <summary>Latest distant display refusal, empty after successful admission. A refused view never destroys its dormant source or certifies host movement.</summary>
        public string FarTerrainDiagnostic { get; private set; } = string.Empty;

        private bool WantsFarTerrain(UniverseCreatorObject world, Chunk chunk) => world != null &&
            world.retireDormantTerrain && world.farTerrainRadiusCells > 0 && !world.ShouldRenderWholeWorld() &&
            centreCell != null && !chunk.resident && IsLiveCell(world, chunk) && chunk.go != null && chunk.terrain != null && HasDormantTerrain(chunk) && !chunk.inFlight &&
            !chunk.publicationInProgress && !chunk.presentationPending &&
            DualLODSystem.RingDistance(chunk.cell.x, chunk.cell.z, centreCell.x, centreCell.z) <= world.farTerrainRadiusCells;

        private IEnumerable<ChunkCommitStep> PrepareFarTerrain(UniverseCreatorObject world, FarTerrainView view)
        {
            ProductMemoryBudget.Lease scratch = null;
            try
            {
                view.Source.ValidatePayload(); AdmitDormantMemory(world, view.Chunk);
                view.Bytes = view.Source.FarNativeBytes;
                if (view.Bytes > (long)world.farTerrainBudgetMiB * 1024 * 1024 - farTerrainBytes)
                    throw new InvalidOperationException("Distant terrain exceeds the authored MiB cap.");
                view.Memory = world.ProductMemory.Reserve(0, view.Bytes); farTerrainBytes += view.Bytes;
                scratch = world.ProductMemory.Reserve(256 * 1024);
                view.Data = new TerrainData { name = "UC Distant " + view.Chunk.cell, hideFlags = HideFlags.DontSave };
                foreach (var step in view.Source.RestoreFarStages(view.Data)) yield return step;
                if (view.Chunk.dormantTerrain != view.Source || !WantsFarTerrain(world, view.Chunk)) yield break;
                PublishFarTerrain(view);
                yield return new ChunkCommitStep(ChunkCommitStage.PublishContent);
            }
            finally { scratch?.Dispose(); }
        }

        private void PublishFarTerrain(FarTerrainView view)
        {
            // A detached scene root prevents the display copy from entering world-owned ground, hydrology or adoption scans.
            // Only this manager holds it; DontSave prevents a second persisted terrain authority on scene/domain reload.
            view.Holder = new GameObject("UC Distant " + view.Chunk.cell) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(view.Holder, gameObject.scene);
            view.Holder.SetActive(false); view.Holder.layer = view.Chunk.go.layer;
            view.Holder.transform.SetPositionAndRotation(view.Chunk.go.transform.position, view.Chunk.go.transform.rotation);
            view.Holder.transform.localScale = view.Chunk.go.transform.lossyScale;
            view.Terrain = view.Holder.AddComponent<Terrain>(); view.Terrain.terrainData = view.Data;
            view.Terrain.materialTemplate = view.Chunk.terrain.materialTemplate;
            view.Terrain.drawInstanced = view.Chunk.terrain.drawInstanced;
            view.Terrain.heightmapPixelError = Mathf.Max(32, view.Chunk.terrain.heightmapPixelError);
            view.Terrain.basemapDistance = 0; view.Terrain.drawTreesAndFoliage = false;
            view.Terrain.allowAutoConnect = false;
            view.Data.SetBaseMapDirty(); view.Terrain.Flush();
            farTerrainViews.Add(view);
        }

        private bool PumpFarTerrain(UniverseCreatorObject world)
        {
            if (world == null || !built) return false;
            if (farLastRadius != world.farTerrainRadiusCells || farLastBudget != world.farTerrainBudgetMiB)
            { farLastRadius = world.farTerrainRadiusCells; farLastBudget = world.farTerrainBudgetMiB; dormantEpoch++; }
            bool changed = false, linksChanged = false;
            for (int i = farTerrainViews.Count - 1; i >= 0; i--)
            {
                var view = farTerrainViews[i];
                if (!Application.isPlaying || !WantsFarTerrain(world, view.Chunk) || view.Source != view.Chunk.dormantTerrain)
                { ReleaseFarView(view); farTerrainViews.RemoveAt(i); RelinkFarTerrainCell(view.Chunk.cell); changed = true; continue; }
                bool visible = DependenciesSettled(world) && !HasAcceptedTerrain(world, view.Chunk);
                if (view.Holder.activeSelf != visible) { view.Holder.SetActive(visible); changed = true; linksChanged = true; }
                view.Holder.transform.SetPositionAndRotation(view.Chunk.go.transform.position, view.Chunk.go.transform.rotation);
                view.Holder.transform.localScale = view.Chunk.go.transform.lossyScale;
                view.Terrain.materialTemplate = view.Chunk.terrain.materialTemplate; // Borrowed material remains owned by its original world.
            }
            if (preparingFarTerrain != null && (!Application.isPlaying || !WantsFarTerrain(world, preparingFarTerrain.Chunk) ||
                preparingFarTerrain.Source != preparingFarTerrain.Chunk.dormantTerrain))
            { DisposeFarCommit(); changed = true; }
            // A reduced authored cap withdraws far displays, never the saved source or a near terrain.
            if (farTerrainBytes > (long)world.farTerrainBudgetMiB * 1024 * 1024 && preparingFarTerrain != null)
            { DisposeFarCommit(); changed = true; }
            while (farTerrainBytes > (long)world.farTerrainBudgetMiB * 1024 * 1024 && farTerrainViews.Count > 0)
            {
                var view = farTerrainViews[farTerrainViews.Count - 1];
                if (preparingFarTerrain == view) DisposeFarCommit();
                ReleaseFarView(view); farTerrainViews.RemoveAt(farTerrainViews.Count - 1);
                RelinkFarTerrainCell(view.Chunk.cell); changed = true;
            }
            long headroom = world.ProductMemory.LimitBytes - world.ProductMemory.ManagedBytes - world.ProductMemory.NativeBytes;
            if (preparingFarTerrain == null && !CommitBudgetSpent(world) && residencyChunk == null && Application.isPlaying && DependenciesSettled(world) &&
                world.retireDormantTerrain && world.farTerrainRadiusCells > 0 && farTerrainViews.Count < 16)
            {
                Chunk nearest = null; int nearestRing = int.MaxValue;
                foreach (var chunk in pool)
                {
                    if (chunk.resident || !WantsFarTerrain(world, chunk) || FindFarView(chunk) != null ||
                        chunk.farAttemptEpoch == dormantEpoch && headroom <= chunk.farFailureHeadroom) continue;
                    int ring = DualLODSystem.RingDistance(chunk.cell.x, chunk.cell.z, centreCell.x, centreCell.z);
                    if (ring < nearestRing) { nearest = chunk; nearestRing = ring; }
                }
                if (nearest != null)
                {
                    preparingFarTerrain = new FarTerrainView { Chunk = nearest, Source = nearest.dormantTerrain };
                    farTerrainCommit = PrepareFarTerrain(world, preparingFarTerrain).GetEnumerator();
                }
            }
            if (preparingFarTerrain != null && residencyChunk == null)
            {
                try
                {
                    while (!CommitBudgetSpent(world))
                    {
                        double started = Time.realtimeSinceStartupAsDouble;
                        if (!farTerrainCommit.MoveNext()) { DisposeFarCommit(); break; }
                        RecordCommitStep(world, farTerrainCommit.Current, started); changed = true;
                        FarTerrainDiagnostic = string.Empty;
                    }
                }
                catch (Exception error)
                {
                    FarTerrainDiagnostic = "Distant terrain refused: " + error.Message;
                    Chunk failed = preparingFarTerrain.Chunk;
                    DisposeFarCommit(); failed.farAttemptEpoch = dormantEpoch;
                    failed.farFailureHeadroom = world.ProductMemory.LimitBytes - world.ProductMemory.ManagedBytes - world.ProductMemory.NativeBytes;
                }
            }
            if (linksChanged) LinkFarTerrainNeighbours(); // Upload strips do not allocate neighbour coordinates or refresh links each frame.
            return changed;
        }

        private FarTerrainView FindFarView(Chunk chunk)
        {
            foreach (var view in farTerrainViews) if (view.Chunk == chunk) return view;
            return null;
        }

        private Terrain ActiveFarTerrain(Chunk chunk)
        {
            var view = FindFarView(chunk);
            return view != null && view.Holder != null && view.Holder.activeInHierarchy ? view.Terrain : null;
        }

        private void LinkFarTerrainNeighbours()
        {
            foreach (var view in farTerrainViews)
            {
                RelinkFarTerrainCell(view.Chunk.cell);
            }
        }

        private void RelinkFarTerrainCell(Coord cell)
        {
            if (cell == null) return;
            LinkRenderNeighbours(cell);
            LinkRenderNeighbours(new Coord(cell.x - 1, cell.z)); LinkRenderNeighbours(new Coord(cell.x + 1, cell.z));
            LinkRenderNeighbours(new Coord(cell.x, cell.z - 1)); LinkRenderNeighbours(new Coord(cell.x, cell.z + 1));
        }

        private void LinkRenderNeighbours(Coord cell)
        {
            if (!byCell.TryGetValue(cell, out Chunk chunk)) return;
            Terrain terrain = HasSurface(chunk) ? chunk.terrain : ActiveFarTerrain(chunk);
            if (terrain == null) return;
            int resolution = terrain.terrainData.heightmapResolution;
            terrain.SetNeighbors(ResolveRenderNeighbour(new Coord(cell.x - 1, cell.z), resolution),
                ResolveRenderNeighbour(new Coord(cell.x, cell.z + 1), resolution),
                ResolveRenderNeighbour(new Coord(cell.x + 1, cell.z), resolution),
                ResolveRenderNeighbour(new Coord(cell.x, cell.z - 1), resolution));
            // Render linking has no SyncHeightmap, seam writes, accepted-ground refresh or vegetation publication.
        }

        private Terrain ResolveRenderNeighbour(Coord cell, int resolution)
        {
            // UC-T09-04: display-only linking admits the matching far surface; height stitching still uses accepted near data.
            if (!byCell.TryGetValue(cell, out Chunk chunk)) return null;
            Terrain terrain = HasSurface(chunk) ? chunk.terrain : ActiveFarTerrain(chunk);
            return terrain != null && terrain.terrainData != null && terrain.terrainData.heightmapResolution == resolution ? terrain : null;
        }

        private void DisposeFarCommit()
        {
            var pending = preparingFarTerrain; preparingFarTerrain = null;
            var commit = farTerrainCommit; farTerrainCommit = null; commit?.Dispose();
            if (pending != null && !farTerrainViews.Contains(pending)) ReleaseFarView(pending);
        }

        private void ReleaseFarView(FarTerrainView view)
        {
            if (view.Holder != null)
            {
                view.Holder.SetActive(false);
                if (view.Terrain != null) { view.Terrain.SetNeighbors(null, null, null, null); view.Terrain.terrainData = null; }
                if (Application.isPlaying) Destroy(view.Holder); else DestroyImmediate(view.Holder);
            }
            if (view.Memory != null) farTerrainBytes -= view.Bytes;
            DestroyCommitData(view.Data, view.Memory); view.Memory = null; view.Data = null;
            view.Holder = null; view.Terrain = null;
        }

        private void ReleaseFarTerrain(Chunk chunk)
        {
            if (preparingFarTerrain != null && preparingFarTerrain.Chunk == chunk) DisposeFarCommit();
            for (int i = farTerrainViews.Count - 1; i >= 0; i--)
                if (farTerrainViews[i].Chunk == chunk)
                { ReleaseFarView(farTerrainViews[i]); farTerrainViews.RemoveAt(i); }
            RelinkFarTerrainCell(chunk.cell);
        }

        private void ReleaseAllFarTerrain()
        {
            DisposeFarCommit();
            foreach (var view in farTerrainViews) ReleaseFarView(view);
            foreach (var view in farTerrainViews) RelinkFarTerrainCell(view.Chunk.cell);
            farTerrainViews.Clear();
            dormantEpoch++;
        }
    }
}
