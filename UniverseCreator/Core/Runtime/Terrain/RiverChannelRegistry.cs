// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RiverChannelRegistry.cs
//  Purpose: The procedural River Network node cuts trenches in the graph; a hand-placed river body
//           lives in the scene. This is the one-way bridge between them: the network publishes the
//           channel width and depth it cut (on its coarse whole-world grid) and a hand-placed river
//           reads it, so a river object dropped onto a procedural trench INHERITS that trench's
//           width and depth instead of inventing its own and digging a second trench inside it.
//
//  UC-T02-07: workers capture immutable metadata with graph products. Accepted main-thread commits
//  replace records by world/cell, while retained fields replay the same metadata without a new solve.
//  Records are runtime-only and locked; scene consumers never sample another world's publication.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement; // UC-T03-11: inactive worlds still require accepted-publication retirement on scene unload.
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public static class RiverChannelRegistry
    {
        private static readonly object Gate = new object();

        private static float[] _halfWidthM;
        private static float[] _depthM;
        private static int _resX, _resZ;
        private static float _originX, _originZ, _cellX, _cellZ;

        // UC-T02-07: accepted records belong to a world and committed cell, not whichever worker finished last.
        private sealed class OwnedChannels
        {
            internal GenerationRunId Run;
            internal Rect Bounds;
            internal ChannelGraphPublication[] Channels;
            internal ProductMemoryBudget.Lease Memory;
            internal RiverChannelSnapshot Snapshot; // UC-T03-09: reconciled with this owner's accepted native ground.
        }
        private static readonly Dictionary<Guid, Dictionary<string, OwnedChannels>> worlds =
            new Dictionary<Guid, Dictionary<string, OwnedChannels>>();
        private static readonly Dictionary<Guid, UniverseCreatorObject> worldOwners = new Dictionary<Guid, UniverseCreatorObject>(); // UC-T03-11: queried only by main-thread lifecycle hooks.
        private static bool observingScenes;
        // UC-T08-12: cell publication/withdrawal can change ground without changing the generation run.
        private static readonly Dictionary<Guid, long> groundVersions = new Dictionary<Guid, long>();
        private static long groundVersion;

        /// <summary>Returns this world's accepted ground publication stamp, including same-run cell changes; zero means no accepted cells.</summary>
        /// <param name="world">Existing world owner; null returns zero. Query on the main thread.</param>
        internal static long GroundPublicationVersion(UniverseCreatorObject world)
        {
            if (world == null) return 0;
            lock (Gate) return groundVersions.TryGetValue(world.GenerationScheduler.CurrentRun.WorldId, out long version) ? version : 0;
        }

        // UC-T03-11: Unity does not guarantee OnDestroy for a component that was never active.
        // Scene retirement owns that case; moving a live world to another scene preserves its records.
        private static void ObserveScenes()
        {
            if (observingScenes) return;
            SceneManager.sceneUnloaded += WithdrawScene;
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += WithdrawScene;
#endif
            observingScenes = true;
        }

        private static void WithdrawScene(Scene scene)
        {
            GraphExecutionGuard.RequireMainThread("Scene channel retirement");
            lock (Gate)
            {
                List<Guid> retired = null;
                foreach (var pair in worldOwners)
                    if (pair.Value == null || pair.Value.gameObject.scene.handle == scene.handle)
                    { retired ??= new List<Guid>(); retired.Add(pair.Key); }
                if (retired != null) foreach (Guid world in retired) Withdraw(world);
            }
        }

        /// <summary>UC-T03-07: true when legacy data or a committed network has positive channel dimensions;
        /// closed-lake diagnostics alone do not report a channel.</summary>
        public static bool HasData
        {
            get
            {
                lock (Gate)
                {
                    if (_halfWidthM != null) return true;
                    foreach (var owners in worlds.Values) foreach (OwnedChannels owner in owners.Values)
                        foreach (ChannelGraphPublication channel in owner.Channels) if (channel.HasChannelCoverage) return true;
                    return false;
                }
            }
        }

        /// <summary>Clears legacy and committed channel publications and releases their owned managed-payload pins.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _halfWidthM = null; _depthM = null;
                foreach (var owners in worlds.Values) foreach (OwnedChannels owner in owners.Values) owner.Memory?.Dispose();
                worlds.Clear(); worldOwners.Clear(); // UC-T03-11: lifetime witnesses share the accepted registry lifetime.
                groundVersions.Clear();
            }
        }

        internal static bool ReplaceOwned(WorldGenerationScheduler scheduler, GenerationRunId run, string ownerKey,
            Rect bounds, ChannelGraphPublication[] channels, ProductMemoryBudget.Lease memory, RiverChannelSnapshot snapshot = null,
            UniverseCreatorObject ownerWorld = null)
        {
            GraphExecutionGuard.RequireMainThread("Channel publication commit");
            if (ownerWorld != null) ObserveScenes(); // UC-T03-11: register outside the authority lock, before a native scene can retire.
            bool accepted = scheduler.TryPublish(run, () =>
            {
                lock (Gate)
                {
                    if (!worlds.TryGetValue(run.WorldId, out var owners))
                        worlds.Add(run.WorldId, owners = new Dictionary<string, OwnedChannels>(StringComparer.Ordinal));
                    if (owners.TryGetValue(ownerKey, out OwnedChannels previous)) previous.Memory?.Dispose();
                    // Empty accepted cells are tombstones: a newer layout with no river must suppress older overlapping metadata.
                    owners[ownerKey] = new OwnedChannels { Run = run, Bounds = bounds, Channels = channels, Memory = memory, Snapshot = snapshot };
                    groundVersions[run.WorldId] = ++groundVersion;
                    if (ownerWorld != null) worldOwners[run.WorldId] = ownerWorld; // UC-T03-11: capture only an accepted world witness.
                }
            });
            if (!accepted) memory?.Dispose();
            return accepted;
        }

        internal static void Withdraw(Guid world, string ownerKey = null)
        {
            lock (Gate)
            {
                if (!worlds.TryGetValue(world, out var owners)) { worldOwners.Remove(world); groundVersions.Remove(world); return; }
                if (ownerKey == null)
                {
                    foreach (OwnedChannels owner in owners.Values) owner.Memory?.Dispose();
                    worlds.Remove(world); worldOwners.Remove(world); // UC-T03-11: do not retain an unloaded native component wrapper.
                    groundVersions.Remove(world);
                }
                else
                {
                    if (owners.TryGetValue(ownerKey, out OwnedChannels owner)) owner.Memory?.Dispose();
                    if (owners.Remove(ownerKey)) groundVersions[world] = ++groundVersion;
                    if (owners.Count == 0) { worlds.Remove(world); worldOwners.Remove(world); groundVersions.Remove(world); }
                }
            }
        }

        /// <summary>UC-T02-07: samples only metadata accepted for this world's terrain at the requested point.</summary>
        /// <param name="world">Owning world; null returns false and never borrows another world's channels.</param>
        /// <param name="worldX">World X coordinate in metres.</param>
        /// <param name="worldZ">World Z coordinate in metres.</param>
        /// <param name="searchRadiusM">Finite nonnegative search radius in metres; grid search is bounded to six cells per axis.</param>
        /// <param name="halfWidthM">Resulting half width in metres, or zero.</param>
        /// <param name="depthM">Resulting depth in metres, or zero.</param>
        /// <returns>True for a nearby committed channel; previous accepted metadata remains valid until its cell is replaced.</returns>
        public static bool TrySample(UniverseCreatorObject world, float worldX, float worldZ, float searchRadiusM,
            out float halfWidthM, out float depthM)
        {
            halfWidthM = depthM = 0;
            if (world == null || float.IsNaN(searchRadiusM) || float.IsInfinity(searchRadiusM) || searchRadiusM < 0) return false;
            Guid id = world.GenerationScheduler.CurrentRun.WorldId;
            lock (Gate)
            {
                if (!worlds.TryGetValue(id, out var owners)) return false;
                float best = float.MaxValue;
                long bestRevision = 0;
                string bestOwner = null, bestChannel = null;
                foreach (var owner in owners)
                {
                    Rect bounds = owner.Value.Bounds;
                    if (worldX < bounds.xMin || worldX > bounds.xMax || worldZ < bounds.yMin || worldZ > bounds.yMax) continue;
                    if (owner.Value.Run.Revision < bestRevision) continue;
                    if (owner.Value.Run.Revision > bestRevision)
                    {
                        bestRevision = owner.Value.Run.Revision; best = float.MaxValue;
                        bestOwner = bestChannel = null; halfWidthM = depthM = 0;
                    }
                    foreach (ChannelGraphPublication channel in owner.Value.Channels)
                    {
                        if (!Sample(channel.HalfWidth, channel.Depth, channel.ResolutionX, channel.ResolutionZ,
                            channel.OriginX, channel.OriginZ, channel.CellX, channel.CellZ, worldX, worldZ, searchRadiusM,
                            out float width, out float depth, out float distance)) continue;
                        int order = string.CompareOrdinal(owner.Key, bestOwner);
                        if (distance < best || (distance == best && (order < 0 || (order == 0 && string.CompareOrdinal(channel.Key, bestChannel) < 0))))
                        { best = distance; bestOwner = owner.Key; bestChannel = channel.Key; halfWidthM = width; depthM = depth; }
                    }
                }
                return bestOwner != null;
            }
        }

        /// <summary>UC-T03-09: queries the final accepted bed/banks in one world. A nearby profile can be outside its bank;
        /// consumers must use SignedBankDistanceMetres to distinguish proximity from containment.</summary>
        /// <param name="world">Owning world; null yields InvalidInput.</param>
        /// <param name="worldX">Finite world X in metres.</param>
        /// <param name="worldZ">Finite world Z in metres.</param>
        /// <param name="searchRadiusM">Finite nonnegative bank search radius in metres; bounded to six solve cells per axis.</param>
        /// <param name="sample">Immutable accepted cross-section, or default on failure.</param>
        /// <param name="validity">Exact absence or reconciliation result.</param>
        /// <returns>True only when final centre and both banks have matching accepted-revision ground and positive trench relief.</returns>
        public static bool TrySampleCommitted(UniverseCreatorObject world, float worldX, float worldZ, float searchRadiusM,
            out RiverChannelSample sample, out RiverChannelValidity validity)
        {
            if (world == null) { sample = default; validity = RiverChannelValidity.InvalidInput; return false; }
            return TrySampleCommitted(world.GenerationScheduler.CurrentRun.WorldId, worldX, worldZ, searchRadiusM, 0, out sample, out validity);
        }

        internal static bool TrySampleCommitted(Guid world, float x, float z, float radius, long requiredRevision,
            out RiverChannelSample sample, out RiverChannelValidity validity)
        {
            sample = default; validity = RiverChannelValidity.InvalidInput;
            if (world == Guid.Empty || !RiverChannelSnapshot.Finite(x) || !RiverChannelSnapshot.Finite(z) ||
                !RiverChannelSnapshot.Finite(radius) || radius < 0) return false;
            lock (Gate)
            {
                validity = RiverChannelValidity.NoCommittedWorld;
                if (!worlds.TryGetValue(world, out var owners)) return false;
                OwnedChannels chosen = null; string chosenKey = null;
                foreach (var pair in owners)
                {
                    var value = pair.Value;
                    if (value.Snapshot == null || !value.Snapshot.Covers(x, z) ||
                        (requiredRevision > 0 && value.Run.Revision != requiredRevision)) continue;
                    if (chosen == null || value.Run.Revision > chosen.Run.Revision ||
                        (value.Run.Revision == chosen.Run.Revision && string.CompareOrdinal(pair.Key, chosenKey) < 0))
                    { chosen = value; chosenKey = pair.Key; }
                }
                validity = RiverChannelValidity.OutsideCoverage;
                if (chosen == null) return false;
                validity = RiverChannelValidity.NoChannel;
                if (!chosen.Snapshot.FindProfile(x, z, radius, out var profile)) return false;
                Vector2 normal = new Vector2(-profile.Direction.y, profile.Direction.x);
                Vector2 left = profile.Centre + normal * profile.Width, right = profile.Centre - normal * profile.Width;
                validity = RiverChannelValidity.MissingGround;
                if (!TryGround(owners, chosen.Run, profile.Centre.x, profile.Centre.y, out float bed) ||
                    !TryGround(owners, chosen.Run, left.x, left.y, out float leftY) ||
                    !TryGround(owners, chosen.Run, right.x, right.y, out float rightY)) return false;
                float relief = Mathf.Min(leftY, rightY) - bed;
                validity = RiverChannelValidity.NoTrench;
                bool hasMeasuredCut = chosen.Snapshot.TryRelief(profile.Centre.x, profile.Centre.y, out float survivingCut); // UC-T03-10: guard-raised trenches retain their actual relief.
                if (relief <= 1e-4f || (hasMeasuredCut ? !RiverChannelSnapshot.Finite(survivingCut) || survivingCut <= 1e-4f : bed >= profile.ReferenceY - 1e-4f)) return false;
                float surface = bed + 0.75f * Mathf.Min(relief, profile.Depth);
                if (RiverChannelSnapshot.Finite(profile.FixedSurface))
                {
                    validity = RiverChannelValidity.OutletLevelMismatch; // UC-T03-09: authoritative lake levels require actual native clearance.
                    if (profile.FixedSurface <= bed || profile.FixedSurface > Mathf.Min(leftY, rightY) + 1e-4f) return false;
                    surface = profile.FixedSurface;
                }
                sample = new RiverChannelSample(chosen.Run, chosen.Snapshot.GroundVersion, profile.Reach, new Vector3(profile.Centre.x, bed, profile.Centre.y),
                    new Vector3(left.x, leftY, left.y), new Vector3(right.x, rightY, right.y), profile.Direction,
                    profile.Width, surface, profile.SignedDistance);
                validity = RiverChannelValidity.Valid; return true;
            }
        }

        // UC-T03-09: adjacent accepted cells supply bank samples only for the same revision, never pending/older ground.
        private static bool TryGround(Dictionary<string, OwnedChannels> owners, GenerationRunId run, float x, float z, out float y)
        {
            y = 0; string chosenKey = null; RiverChannelSnapshot chosen = null;
            foreach (var pair in owners)
                if (pair.Value.Run.Equals(run) && pair.Value.Snapshot != null && pair.Value.Snapshot.Covers(x, z) &&
                    (chosenKey == null || string.CompareOrdinal(pair.Key, chosenKey) < 0))
                { chosen = pair.Value.Snapshot; chosenKey = pair.Key; }
            // UC-T03-10: select the surface owner before sampling; another overlapping cell cannot fill its native hole.
            return chosen != null && chosen.TryGround(x, z, out y);
        }

        internal static bool HasCommittedChannels(GenerationRunId run)
        {
            lock (Gate)
            {
                if (!run.IsAssigned || !worlds.TryGetValue(run.WorldId, out var owners)) return false;
                foreach (var owner in owners.Values) if (owner.Run.Equals(run) && owner.Snapshot != null)
                    foreach (var channel in owner.Channels) if (channel.HasChannelCoverage) return true;
                return false;
            }
        }

        /// <summary>UC-T03-09: returns immutable accepted metadata for the newest cell covering one world point.</summary>
        /// <param name="world">Owning world; null returns false.</param>
        /// <param name="worldX">Finite world X in metres.</param>
        /// <param name="worldZ">Finite world Z in metres.</param>
        /// <param name="snapshot">Borrowed immutable snapshot, or null outside accepted coverage. Check its Run when retaining it.</param>
        /// <returns>True for accepted coverage, including a no-channel tombstone; this does not assert water containment.</returns>
        public static bool TryGetCommittedSnapshot(UniverseCreatorObject world, float worldX, float worldZ, out RiverChannelSnapshot snapshot)
        {
            snapshot = null;
            if (world == null || !RiverChannelSnapshot.Finite(worldX) || !RiverChannelSnapshot.Finite(worldZ)) return false;
            Guid worldId = world.GenerationScheduler.CurrentRun.WorldId; // UC-T03-10: resolve authority before taking the registry lock.
            lock (Gate)
            {
                if (!worlds.TryGetValue(worldId, out var owners)) return false;
                string chosenKey = null;
                foreach (var pair in owners) if (pair.Value.Snapshot != null && pair.Value.Snapshot.Covers(worldX, worldZ) &&
                    (snapshot == null || pair.Value.Run.Revision > snapshot.Run.Revision ||
                    (pair.Value.Run.Revision == snapshot.Run.Revision && string.CompareOrdinal(pair.Key, chosenKey) < 0)))
                { snapshot = pair.Value.Snapshot; chosenKey = pair.Key; }
                return snapshot != null;
            }
        }

        /// <summary>UC-T03-10: samples frozen accepted ground, including all water/path/sculpt/guard modifications; holes are absent.</summary>
        /// <param name="world">Owning world; null returns false.</param>
        /// <param name="worldX">Finite world X in metres.</param>
        /// <param name="worldZ">Finite world Z in metres.</param>
        /// <param name="heightMetres">Accepted world-space height in metres, or zero on absence.</param>
        /// <param name="run">Accepted terrain revision, or default on absence.</param>
        /// <returns>True only for an accepted native surface quad in this world; no clamped outside-world fallback.</returns>
        public static bool TrySampleGround(UniverseCreatorObject world, float worldX, float worldZ, out float heightMetres, out GenerationRunId run)
        {
            heightMetres = 0; run = default;
            return world != null && TrySampleGround(world.GenerationScheduler.CurrentRun.WorldId, worldX, worldZ, 0, out heightMetres, out run);
        }

        internal static bool TrySampleGround(Guid world, float x, float z, long requiredRevision, out float y, out GenerationRunId run)
        {
            y = 0; run = default;
            if (!RiverChannelSnapshot.Finite(x) || !RiverChannelSnapshot.Finite(z)) return false;
            lock (Gate)
            {
                if (!worlds.TryGetValue(world, out var owners)) return false;
                OwnedChannels chosen = null; string chosenKey = null;
                foreach (var pair in owners) if (pair.Value.Snapshot != null && pair.Value.Snapshot.Covers(x, z) &&
                    (requiredRevision <= 0 || pair.Value.Run.Revision == requiredRevision) &&
                    (chosen == null || pair.Value.Run.Revision > chosen.Run.Revision ||
                    (pair.Value.Run.Revision == chosen.Run.Revision && string.CompareOrdinal(pair.Key, chosenKey) < 0)))
                { chosen = pair.Value; chosenKey = pair.Key; }
                if (chosen == null || !chosen.Snapshot.TryGround(x, z, out y)) return false;
                run = chosen.Run; return true;
            }
        }

        /// <summary>UC-T03-09: withdraws a runtime cell's channel and ground metadata, releasing its commit pin.</summary>
        /// <param name="world">Owning world; null is a no-op.</param>
        /// <param name="ownerId">Exact cell owner from RiverChannelSnapshot.OwnerId; null/empty is a no-op.</param>
        public static void WithdrawOwner(UniverseCreatorObject world, string ownerId)
        {
            if (world != null && !string.IsNullOrEmpty(ownerId)) Withdraw(world.GenerationScheduler.CurrentRun.WorldId, ownerId);
        }

        // UC-T03-10: synchronous seam/re-cut paths change already accepted native terrain, without a new graph solve.
        // Replace only the exact accepted owner witness. On allocation/capture failure withdraw it and propagate the error;
        // the changed native surface must never keep an old ground snapshot.
        internal static bool RefreshGround(UniverseCreatorObject world, GenerationRunId run, string ownerId, float[,] heights, TerrainData native)
        {
            GraphExecutionGuard.RequireMainThread("Accepted ground refresh");
            if (world == null || ownerId == null || heights == null || native == null) return false;
            OwnedChannels previous;
            lock (Gate)
            {
                if (!worlds.TryGetValue(run.WorldId, out var owners) || !owners.TryGetValue(ownerId, out previous) ||
                    !previous.Run.Equals(run) || previous.Snapshot == null) return false;
            }
            ProductMemoryBudget.Lease memory = null; bool accepted = false;
            try
            {
                memory = world.ProductMemory.Reserve(4096L + ProductMemoryFootprint.Estimate(previous.Snapshot));
                var snapshot = previous.Snapshot.WithGround(heights, native.GetHoles(0, 0, native.holesResolution, native.holesResolution));
                lock (Gate)
                {
                    if (!worlds.TryGetValue(run.WorldId, out var owners) || !owners.TryGetValue(ownerId, out var current) || !ReferenceEquals(current, previous)) return false;
                    owners[ownerId] = new OwnedChannels { Run = run, Bounds = snapshot.Bounds, Channels = previous.Channels, Snapshot = snapshot, Memory = memory };
                    groundVersions[run.WorldId] = ++groundVersion;
                    previous.Memory?.Dispose(); accepted = true; memory = null; return true;
                }
            }
            finally
            {
                memory?.Dispose();
                if (!accepted) lock (Gate)
                    if (worlds.TryGetValue(run.WorldId, out var owners) && owners.TryGetValue(ownerId, out var current) && ReferenceEquals(current, previous))
                    { previous.Memory?.Dispose(); owners.Remove(ownerId); groundVersions[run.WorldId] = ++groundVersion;
                        if (owners.Count == 0) { worlds.Remove(run.WorldId); worldOwners.Remove(run.WorldId); groundVersions.Remove(run.WorldId); } } // UC-T03-11: failed refresh also retires the final world witness.
            }
        }

        /// <summary>Publishes the network's channel half-width and depth (metres) per coarse cell.
        /// Takes ownership of the arrays: the caller must not modify them afterwards.</summary>
        public static void Publish(float[] halfWidthM, float[] depthM, int resX, int resZ,
                                   float originX, float originZ, float cellX, float cellZ)
        {
            if (halfWidthM == null || depthM == null || resX < 2 || resZ < 2) return;
            if (halfWidthM.Length != resX * resZ || depthM.Length != resX * resZ) return;
            lock (Gate)
            {
                _halfWidthM = halfWidthM; _depthM = depthM;
                _resX = resX; _resZ = resZ;
                _originX = originX; _originZ = originZ;
                _cellX = Mathf.Max(1e-3f, cellX); _cellZ = Mathf.Max(1e-3f, cellZ);
            }
        }

        /// <summary>
        /// The nearest procedural channel to a world point, within <paramref name="searchRadiusM"/>.
        /// Returns the channel's half-width and depth in metres. False when there is no data or no
        /// channel nearby (the river is then not on a procedural trench).
        /// </summary>
        /// <remarks>Legacy unowned publications only. UC scene bodies use the world-aware overload.</remarks>
        public static bool TrySample(float worldX, float worldZ, float searchRadiusM,
                                     out float halfWidthM, out float depthM)
        {
            lock (Gate)
                return Sample(_halfWidthM, _depthM, _resX, _resZ, _originX, _originZ, _cellX, _cellZ,
                    worldX, worldZ, searchRadiusM, out halfWidthM, out depthM, out _);
        }

        private static bool Sample(float[] widths, float[] depths, int resX, int resZ, float originX, float originZ,
            float cellX, float cellZ, float worldX, float worldZ, float radius, out float halfWidthM, out float depthM, out float distance)
        {
            halfWidthM = depthM = 0; distance = float.MaxValue;
            if (widths == null || depths == null || float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0 ||
                float.IsNaN(worldX) || float.IsInfinity(worldX) || float.IsNaN(worldZ) || float.IsInfinity(worldZ)) return false;
            int cx = Mathf.RoundToInt((worldX - originX) / cellX);
            int cz = Mathf.RoundToInt((worldZ - originZ) / cellZ);
            int rx = Mathf.Clamp(Mathf.CeilToInt(radius / cellX), 1, 6);
            int rz = Mathf.Clamp(Mathf.CeilToInt(radius / cellZ), 1, 6);

            float best = float.MaxValue; int bi = -1;
            for (int dz = -rz; dz <= rz; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= resZ) continue;
                for (int dx = -rx; dx <= rx; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= resX) continue;
                    int i = z * resX + x;
                    if (widths[i] <= 0f) continue;

                    float ex = (originX + x * cellX) - worldX;
                    float ez = (originZ + z * cellZ) - worldZ;
                    float d = ex * ex + ez * ez;
                    if (d < best) { best = d; bi = i; }
                }
            }

            if (bi < 0 || best > radius * radius) return false;
            halfWidthM = widths[bi]; depthM = depths[bi]; distance = best;
            return true;
        }
    }
}
