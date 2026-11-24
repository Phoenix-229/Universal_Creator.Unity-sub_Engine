// UC-T03-07: source records from existing flat lake levels and the existing directed drainage tree.
using System;
using System.Collections.Generic;
using UniverseCreator.Nodes;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>A classified lake and its physically available spill; closed lakes carry no source cell.</summary>
    public sealed class LakeOutletSource
    {
        /// <summary>World-local lake identity from the captured water classification.</summary>
        public string LakeId { get; internal set; }
        /// <summary>Flat captured water surface in world metres.</summary>
        public float SurfaceMetres { get; internal set; }
        /// <summary>Last source-water sample at the flat surface, in world metres; zero for a closed body.</summary>
        public Vector3 SpillPosition { get; internal set; }
        /// <summary>World XZ bounds of classified lake samples, expanded by half a solve cell.</summary>
        public Rect Bounds { get; internal set; }
        /// <summary>True when an existing non-uphill route reaches distinct receiving water.</summary>
        public bool HasValidSpill { get; internal set; }
        /// <summary>True when the source is admitted by coverage and retained by the final river selection.</summary>
        public bool Selected { get; internal set; }
        /// <summary>Last source-lake cell before outflow; -1 for a closed lake.</summary>
        public int SpillCell { get; internal set; } = -1;
        /// <summary>First dry downstream cell, or -1 for closed/direct-water connections.</summary>
        public int SourceCell { get; internal set; } = -1;
        /// <summary>First receiving water cell, or -1 without a valid spill.</summary>
        public int ReceivingCell { get; internal set; } = -1;
        /// <summary>Receiving lake ID or "ocean"; null for a closed lake.</summary>
        public string ReceivingBodyId { get; internal set; }
        /// <summary>Actual selected reach containing the source; null if no dry channel survives selection.</summary>
        public string DownstreamReachId { get; internal set; }
        /// <summary>Runoff-times-catchment discharge assumption in cubic metres per second.</summary>
        public float DischargeM3PerSecond { get; internal set; }
        /// <summary>Why a lake has no active outflow, or empty when its channel is active.</summary>
        public string Diagnostic { get; internal set; }
        internal float Level;
        internal float Barrier = float.PositiveInfinity;
        internal int MinX = int.MaxValue, MinZ = int.MaxValue, MaxX = -1, MaxZ = -1;
    }

    /// <summary>UC-T03-07: immutable generation lake outflows owned by a world and graph producer.
    /// These candidate levels await final terrain reconciliation under UC-T03-09.</summary>
    public sealed class LakeOutletSnapshot
    {
        /// <summary>World lifetime owning the captured lakes.</summary>
        public Guid WorldId { get; }
        /// <summary>Graph node owning these sources.</summary>
        public string ProducerId { get; }
        /// <summary>Stable lake-ID order, including closed and unselected bodies; read after generation completes.</summary>
        public IReadOnlyList<LakeOutletSource> Lakes { get; }
        internal LakeOutletSnapshot(Guid worldId, string producerId, List<LakeOutletSource> lakes)
        { WorldId = worldId; ProducerId = producerId; Lakes = lakes.AsReadOnly(); }
    }

    public static partial class RiverNetworkExtractor
    {
        /// <summary>Finds at most one spill per existing lake without changing height or re-solving drainage.
        /// Memoized dry-cell destinations bound routing work to the solve size, including shared downstream tails.</summary>
        /// <param name="routing">Existing D8 routing solve; finite normalized heights and positive world metres.</param>
        /// <param name="flat">Same input/lattice solved with zero fill epsilon for standing-water levels.</param>
        /// <param name="water">Captured lake/ocean identities on that lattice.</param>
        /// <param name="authoredLevels">Optional placed-lake surface elevations in world metres, keyed by lake ID.</param>
        /// <param name="seaLevel">Finite normalized receiving sea elevation.</param>
        /// <param name="runoff">Finite nonnegative effective runoff in metres per second.</param>
        /// <param name="maximumSources">Coverage admission cap; -1 means all valid dry outflows, zero means none.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Lake records sorted by ID, including closed/inactive reasons; null on cancellation.</returns>
        /// <exception cref="ArgumentException">Invalid lattices, levels, limits or nonfinite samples.</exception>
        /// <exception cref="InvalidOperationException">Ambiguous water or cyclic drainage.</exception>
        public static List<LakeOutletSource> ResolveLakeOutlets(WorldDrainage routing, WorldDrainage flat, RiverOutletWater water,
            IReadOnlyDictionary<string, float> authoredLevels, float seaLevel, float runoff, int maximumSources, StopToken stop)
        {
            if (routing == null || flat == null || water == null || routing.height == null || flat.height == null ||
                routing.dir == null || routing.accum == null || routing.filled == null || flat.filled == null ||
                routing.resX != flat.resX || routing.resZ != flat.resZ || routing.resX != water.ResX || routing.resZ != water.ResZ ||
                routing.height.worldPos != flat.height.worldPos || routing.height.worldSize != flat.height.worldSize ||
                routing.height.worldHeight != flat.height.worldHeight ||
                !FiniteHierarchy(seaLevel) || !FiniteHierarchy(runoff) || runoff < 0 || maximumSources < -1)
                throw new ArgumentException("Invalid lake outlet solve or controls.");
            int count = routing.filled.Length, resX = routing.resX;
            if ((long)resX * routing.resZ != count || flat.filled.Length != count || routing.dir.Length != count ||
                routing.accum.Length != count || routing.height.arr.Length != count || flat.height.arr.Length != count ||
                !FiniteHierarchy(routing.cellArea) || routing.cellArea <= 0 ||
                !FiniteHierarchy(routing.height.worldHeight) || routing.height.worldHeight <= 0)
                throw new ArgumentException("Invalid lake outlet lattice dimensions or height scale.");
            if (stop != null && stop.StopRequested) return null;
            Vector2 pixel = routing.height.PixelSize;
            if (!FiniteHierarchy(pixel.x) || !FiniteHierarchy(pixel.y) || pixel.x <= 0 || pixel.y <= 0 ||
                !FiniteHierarchy(routing.height.worldPos.x) || !FiniteHierarchy(routing.height.worldPos.y) ||
                !FiniteHierarchy(routing.height.worldPos.z))
                throw new ArgumentException("Lake outlet coordinates require finite origins and positive cell spacing.");
            var bodies = new Dictionary<string, LakeOutletSource>(StringComparer.Ordinal);
            for (int c = 0; c < count; c++)
            {
                if (stop != null && (c & 1023) == 0 && stop.StopRequested) return null;
                if (!FiniteHierarchy(routing.height.arr[c]) || flat.height.arr[c] != routing.height.arr[c] ||
                    !FiniteHierarchy(routing.filled[c]) || !FiniteHierarchy(flat.filled[c]) ||
                    !FiniteHierarchy(routing.accum[c]) || routing.accum[c] < 0 ||
                    routing.filled[c] + 4e-7f < routing.height.arr[c] || flat.filled[c] + 4e-7f < routing.height.arr[c])
                    throw new ArgumentException("Lake outlet samples require finite heights, fill at or above ground and nonnegative accumulation.");
                string id = water.Lakes[c];
                if (string.IsNullOrEmpty(id)) continue;
                if (water.Ocean[c]) throw new InvalidOperationException("Lake outlet classification overlaps the ocean ambiguously.");
                if (!bodies.TryGetValue(id, out LakeOutletSource body))
                    bodies.Add(id, body = new LakeOutletSource { LakeId = id, Level = float.PositiveInfinity });
                body.Level = Math.Min(body.Level, flat.filled[c]);
                int x = c % resX, z = c / resX;
                body.MinX = Math.Min(body.MinX, x); body.MaxX = Math.Max(body.MaxX, x);
                body.MinZ = Math.Min(body.MinZ, z); body.MaxZ = Math.Max(body.MaxZ, z);
            }
            foreach (LakeOutletSource body in bodies.Values)
            {
                if (authoredLevels != null && authoredLevels.TryGetValue(body.LakeId, out float metres))
                {
                    if (!FiniteHierarchy(metres)) throw new ArgumentException("Authored lake level must be finite metres.");
                    body.Level = (float)(((double)metres - routing.height.worldPos.y) / routing.height.worldHeight);
                }
                body.SurfaceMetres = (float)(routing.height.worldPos.y + body.Level * (double)routing.height.worldHeight);
                if (!FiniteHierarchy(body.Level) || !FiniteHierarchy(body.SurfaceMetres)) throw new ArgumentException("Lake level is outside the supported world range.");
                body.Bounds = new Rect(routing.height.worldPos.x + (body.MinX - 0.5f) * pixel.x,
                    routing.height.worldPos.z + (body.MinZ - 0.5f) * pixel.y,
                    (body.MaxX - body.MinX + 1) * pixel.x, (body.MaxZ - body.MinZ + 1) * pixel.y);
                body.Diagnostic = "Closed at the captured water level: no downhill route to distinct receiving water.";
            }

            var receiver = new int[count]; var maximum = new float[count]; var state = new byte[count]; var path = new int[count];
            for (int c = 0; c < count; c++)
            {
                receiver[c] = -1;
                if (!water.Ocean[c] && string.IsNullOrEmpty(water.Lakes[c])) continue;
                receiver[c] = c; state[c] = 2;
                maximum[c] = water.Ocean[c] ? seaLevel : bodies[water.Lakes[c]].Level;
            }
            for (int start = 0; start < count; start++)
            {
                if (stop != null && (start & 1023) == 0 && stop.StopRequested) return null;
                if (state[start] == 2) continue;
                int current = start, length = 0;
                while (current >= 0 && state[current] == 0)
                {
                    if (stop != null && (length & 1023) == 0 && stop.StopRequested) return null;
                    state[current] = 1; path[length++] = current;
                    int next = Hydrology.DownstreamCell(routing.dir, current, resX, routing.resZ);
                    // UC-T03-07: filled routing alone can cross a dry pit uphill. A lake source
                    // is admitted only when the actual dry ground also descends to receiving water.
                    float nextGround = next < 0 ? float.PositiveInfinity : water.Ocean[next] ? seaLevel :
                        !string.IsNullOrEmpty(water.Lakes[next]) ? bodies[water.Lakes[next]].Level : routing.height.arr[next];
                    // UC-T03-07: routing epsilon cannot turn an equal-level receiving lake into an uphill barrier.
                    float nextFill = next >= 0 && (water.Ocean[next] || !string.IsNullOrEmpty(water.Lakes[next]))
                        ? nextGround : next >= 0 ? routing.filled[next] : float.PositiveInfinity;
                    if (next >= 0 && (nextFill > routing.filled[current] ||
                        nextGround > routing.height.arr[current] + 4e-7f)) { current = -1; break; }
                    current = next;
                }
                if (current >= 0 && state[current] == 1) throw new InvalidOperationException("Cyclic lake-outlet drainage.");
                int target = current >= 0 ? receiver[current] : -1;
                float highest = current >= 0 ? maximum[current] : float.PositiveInfinity;
                while (length > 0)
                {
                    int c = path[--length]; highest = Math.Max(highest, routing.height.arr[c]);
                    receiver[c] = target; maximum[c] = highest; state[c] = 2;
                }
            }

            // One grid pass visits every possible lake boundary; no full trace per candidate or threshold trial.
            for (int c = 0; c < count; c++)
            {
                if (stop != null && (c & 1023) == 0 && stop.StopRequested) return null;
                string id = water.Lakes[c];
                if (string.IsNullOrEmpty(id)) continue;
                int next = Hydrology.DownstreamCell(routing.dir, c, resX, routing.resZ);
                if (next < 0 || water.Lakes[next] == id) continue;
                float nextFill = water.Ocean[next] ? seaLevel : !string.IsNullOrEmpty(water.Lakes[next])
                    ? bodies[water.Lakes[next]].Level : routing.filled[next]; // UC-T03-07: use standing water at direct lake mouths too.
                if (nextFill > routing.filled[c]) continue;
                int end = receiver[next];
                if (end < 0 || water.Lakes[end] == id) continue;
                LakeOutletSource body = bodies[id];
                float barrier = Math.Max(routing.height.arr[c], maximum[next]);
                // Four float ulps near unit height tolerate representation noise, not a terrain grade.
                if (barrier > body.Level + 4e-7f) continue;
                double q = routing.accum[c] * (double)routing.cellArea * runoff;
                float discharge = (float)Math.Min(float.MaxValue, q);
                if (body.HasValidSpill && (barrier > body.Barrier || (barrier == body.Barrier &&
                    (discharge < body.DischargeM3PerSecond || (discharge == body.DischargeM3PerSecond && c >= body.SpillCell))))) continue;
                body.HasValidSpill = true; body.SpillCell = c; body.ReceivingCell = end; body.Barrier = barrier;
                body.SpillPosition = new Vector3(routing.height.worldPos.x + (c % resX) * pixel.x,
                    body.SurfaceMetres, routing.height.worldPos.z + (c / resX) * pixel.y);
                body.SourceCell = !water.Ocean[next] && string.IsNullOrEmpty(water.Lakes[next]) ? next : -1;
                body.ReceivingBodyId = water.Ocean[end] ? "ocean" : water.Lakes[end];
                body.DischargeM3PerSecond = discharge;
                body.Diagnostic = body.SourceCell < 0 ? "Lake meets receiving water directly; no dry outlet channel is required." : "Valid spill outside the requested outlet coverage.";
            }
            var result = new List<LakeOutletSource>(bodies.Values);
            // UC-T03-07: water cells terminate dry routing, so validate the lake-to-lake
            // edges separately; equal-level artificial cycles must not publish circular sources.
            var pending = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LakeOutletSource body in result) pending.Add(body.LakeId, 0);
            foreach (LakeOutletSource body in result)
                if (body.HasValidSpill && bodies.ContainsKey(body.ReceivingBodyId)) pending[body.ReceivingBodyId]++;
            var ready = new Queue<LakeOutletSource>();
            foreach (LakeOutletSource body in result) if (pending[body.LakeId] == 0) ready.Enqueue(body);
            int resolved = 0;
            while (ready.Count > 0)
            {
                if (stop != null && (resolved & 1023) == 0 && stop.StopRequested) return null;
                LakeOutletSource body = ready.Dequeue(); resolved++;
                if (body.HasValidSpill && bodies.TryGetValue(body.ReceivingBodyId, out LakeOutletSource next) && --pending[next.LakeId] == 0)
                    ready.Enqueue(next);
            }
            if (resolved != result.Count) throw new InvalidOperationException("Cyclic lake outflow connections.");
            result.Sort((a, b) => { int q = b.DischargeM3PerSecond.CompareTo(a.DischargeM3PerSecond); return q != 0 ? q : string.CompareOrdinal(a.LakeId, b.LakeId); });
            int selected = 0;
            foreach (LakeOutletSource body in result)
                if (body.HasValidSpill && body.SourceCell >= 0 && (maximumSources < 0 || selected < maximumSources))
                { body.Selected = true; selected++; body.Diagnostic = string.Empty; }
            result.Sort((a, b) => string.CompareOrdinal(a.LakeId, b.LakeId));
            return stop != null && stop.StopRequested ? null : result;
        }

        // UC-T03-07: bind once after hierarchy/order selection; published records are never edited afterwards.
        internal static LakeOutletSnapshot BindLakeOutlets(WorldDrainage wd, RiverNetwork network,
            List<LakeOutletSource> sources, Guid worldId, string producerId, int minimumOrder, StopToken stop)
        {
            var reachAt = new string[wd.filled.Length];
            for (int r = 0; network != null && r < network.reaches.Count; r++)
            {
                if (stop != null && stop.StopRequested) return null;
                RiverReach reach = network.reaches[r];
                if (reach.order < minimumOrder) continue;
                string reachId = producerId + ":reach:" + reach.cells[0];
                foreach (int cell in reach.cells)
                    if (reachAt[cell] == null || cell == reach.cells[0]) reachAt[cell] = reachId;
            }
            foreach (LakeOutletSource source in sources)
            {
                if (!source.Selected) continue;
                source.DownstreamReachId = reachAt[source.SourceCell];
                if (source.DownstreamReachId != null) continue;
                source.Selected = false;
                source.Diagnostic = "Valid spill excluded by the authored river hierarchy/order selection.";
            }
            return new LakeOutletSnapshot(worldId, producerId, sources);
        }
    }
}
