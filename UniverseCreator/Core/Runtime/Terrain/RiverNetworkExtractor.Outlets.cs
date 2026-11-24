// UC-T03-06: directed, world-owned outlet records over the existing drainage topology.
using System;
using System.Collections.Generic;
using UniverseCreator.Nodes;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Destination of a directed river link; a receiving river is a connection, not a terminal mouth.</summary>
    public enum RiverOutletKind
    {
        /// <summary>Edge-connected ocean water.</summary>
        Sea,
        /// <summary>Identified standing-water body.</summary>
        Lake,
        /// <summary>Another reach in this same network and world.</summary>
        ReceivingRiver,
        /// <summary>Interior cell with no downstream flow.</summary>
        Endorheic,
        /// <summary>World boundary, threshold gap or missing receiver; inspect the diagnostic.</summary>
        UnresolvedBoundary
    }

    /// <summary>Immutable directed reach endpoint measured on the generation solve.
    /// Width/depth are candidate carve dimensions, not a certification of later committed bed height.</summary>
    public sealed class RiverOutletRecord
    {
        /// <summary>Producer ID plus upstream cell; stable across reach-list ordering on the same solve.</summary>
        public string ReachId { get; internal set; }
        /// <summary>Receiving reach ID for a connection; null for a terminal/unresolved destination.</summary>
        public string ReceivingReachId { get; internal set; }
        /// <summary>World-local ocean/lake identity, or null for a river/unresolved destination.</summary>
        public string ReceivingBodyId { get; internal set; }
        /// <summary>Distinct destination classification.</summary>
        public RiverOutletKind Kind { get; internal set; }
        /// <summary>Upstream solve-ground position in world metres.</summary>
        public Vector3 SourcePosition { get; internal set; }
        /// <summary>Destination solve-ground position in world metres; includes the junction or first water cell.</summary>
        public Vector3 Position { get; internal set; }
        /// <summary>Last segment's unit XZ direction downstream; zero for a single-cell reach.</summary>
        public Vector2 DirectionXZ { get; internal set; }
        /// <summary>Strahler order recomputed on the included directed links.</summary>
        public int Order { get; internal set; }
        /// <summary>Optional authored hierarchy: 0 trunk, 1 river, 2 stream, -1 unselected.</summary>
        public int HierarchyLevel { get; internal set; }
        /// <summary>Candidate half-width in metres at the shared junction, or last land cell before water.</summary>
        public float HalfWidthMetres { get; internal set; }
        /// <summary>Candidate cut depth in metres at the same sample as half-width.</summary>
        public float DepthMetres { get; internal set; }
        /// <summary>Receiver's half-width at a shared river junction; zero for a non-river destination.</summary>
        public float ReceivingHalfWidthMetres { get; internal set; }
        /// <summary>Receiver's cut depth at a shared river junction; zero for a non-river destination.</summary>
        public float ReceivingDepthMetres { get; internal set; }
        /// <summary>Explanation for an unresolved endpoint, otherwise an empty string.</summary>
        public string Diagnostic { get; internal set; }
        /// <summary>True only for classified sea/lake mouths; tributary connections never count as mouths.</summary>
        public bool IsWaterMouth => Kind == RiverOutletKind.Sea || Kind == RiverOutletKind.Lake;
    }

    /// <summary>Immutable world/producer-scoped topology; generation metadata awaits accepted-terrain publication.</summary>
    public sealed class RiverOutletTopology
    {
        /// <summary>Runtime world lifetime. Empty is allowed only for an unowned preview/test.</summary>
        public Guid WorldId { get; }
        /// <summary>Producing graph node identity.</summary>
        public string ProducerId { get; }
        /// <summary>Directed records sorted by reach ID; no mutable list is exposed.</summary>
        public IReadOnlyList<RiverOutletRecord> Reaches { get; }
        internal RiverOutletTopology(Guid worldId, string producerId, List<RiverOutletRecord> reaches)
        { WorldId = worldId; ProducerId = producerId; Reaches = reaches.AsReadOnly(); }
    }

    /// <summary>Owned classification of the solve's existing water cells, captured before outlet extraction.</summary>
    public sealed class RiverOutletWater
    {
        /// <summary>World lifetime that supplied these masks; validated against the topology consumer.</summary>
        public Guid WorldId { get; }
        /// <summary>Producing node identity; prevents mixing classifications from unrelated solves.</summary>
        public string ProducerId { get; }
        internal readonly int ResX, ResZ;
        internal readonly bool[] Ocean;
        internal readonly string[] Lakes;
        private RiverOutletWater(Guid worldId, string producerId, int resX, int resZ, bool[] ocean, string[] lakes)
        { WorldId = worldId; ProducerId = producerId; ResX = resX; ResZ = resZ; Ocean = ocean; Lakes = lakes; }

        /// <summary>Captures existing ocean/lake masks and names unnamed lake components by their lowest solve cell.
        /// Takes ownership of the supplied arrays; callers must not change them afterwards.</summary>
        /// <param name="worldId">Owning world lifetime, or empty for an unowned preview.</param>
        /// <param name="producerId">Nonempty producing node ID.</param>
        /// <param name="resX">Solve columns, at least two.</param>
        /// <param name="resZ">Solve rows, at least two.</param>
        /// <param name="ocean">Exactly resX*resZ edge-connected sea flags.</param>
        /// <param name="lake">Exactly resX*resZ existing lake flags; no water is created.</param>
        /// <param name="namedLakes">Optional same-sized placed-body IDs; null/empty cells use component IDs.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Captured classification, or null on cancellation.</returns>
        /// <exception cref="ArgumentException">Dimensions, IDs or arrays are invalid.</exception>
        public static RiverOutletWater Capture(Guid worldId, string producerId, int resX, int resZ,
            bool[] ocean, bool[] lake, string[] namedLakes, StopToken stop)
        {
            long length = (long)resX * resZ;
            if (resX < 2 || resZ < 2 || length > int.MaxValue || string.IsNullOrEmpty(producerId) ||
                ocean == null || lake == null || ocean.Length != length || lake.Length != length ||
                (namedLakes != null && namedLakes.Length != length))
                throw new ArgumentException("Invalid river outlet water classification.");
            if (stop != null && stop.StopRequested) return null;
            int count = (int)length;
            string[] ids = namedLakes ?? new string[count];
            var seen = new bool[count]; var queue = new int[count];
            for (int start = 0; start < count; start++)
            {
                if (stop != null && (start & 1023) == 0 && stop.StopRequested) return null;
                if (!lake[start])
                {
                    if (!string.IsNullOrEmpty(ids[start])) throw new ArgumentException("A named lake cell is outside the lake mask.");
                    continue;
                }
                if (seen[start]) continue;
                string component = producerId + ":lake:" + start;
                int head = 0, tail = 1; queue[0] = start; seen[start] = true;
                while (head < tail)
                {
                    if (stop != null && (head & 1023) == 0 && stop.StopRequested) return null;
                    int cell = queue[head++];
                    if (string.IsNullOrEmpty(ids[cell])) ids[cell] = component;
                    int x = cell % resX, z = cell / resX;
                    if (x > 0) Enqueue(cell - 1);
                    if (x + 1 < resX) Enqueue(cell + 1);
                    if (z > 0) Enqueue(cell - resX);
                    if (z + 1 < resZ) Enqueue(cell + resX);
                    void Enqueue(int next)
                    {
                        if (!lake[next] || seen[next]) return;
                        seen[next] = true; queue[tail++] = next;
                    }
                }
            }
            return new RiverOutletWater(worldId, producerId, resX, resZ, ocean, ids);
        }
    }

    public static partial class RiverNetworkExtractor
    {
        /// <summary>Builds distinct directed destinations from an existing network and water classification.
        /// Uses no new drainage solve; confluences reference their receiving reach and share its dimensions.</summary>
        /// <param name="wd">Source solve, including finite normalized heights and D8 flow codes.</param>
        /// <param name="network">Existing directed links into that solve; not modified.</param>
        /// <param name="water">Owned masks from the same world, producer and lattice.</param>
        /// <param name="worldId">Expected world lifetime; a foreign classification is rejected.</param>
        /// <param name="producerId">Expected nonempty graph node ID.</param>
        /// <param name="halfWidth">Candidate carved half-width per solve cell, finite nonnegative metres.</param>
        /// <param name="depth">Candidate carved depth per solve cell, finite nonnegative metres.</param>
        /// <param name="minimumOrder">Original order floor used by the production carve, at least one.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Sorted topology with explicit unresolved destinations, or null on cancellation.</returns>
        /// <exception cref="ArgumentException">Malformed grid, dimensions or samples.</exception>
        /// <exception cref="InvalidOperationException">Foreign, ambiguous, disconnected or cyclic reach metadata.</exception>
        public static RiverOutletTopology BuildOutlets(WorldDrainage wd, RiverNetwork network, RiverOutletWater water,
            Guid worldId, string producerId, float[] halfWidth, float[] depth, int minimumOrder, StopToken stop)
        {
            if (wd == null || wd.height == null || network == null || water == null || wd.dir == null || wd.filled == null ||
                wd.resX < 2 || wd.resZ < 2 || (long)wd.resX * wd.resZ != wd.filled.Length ||
                wd.height.arr.Length != wd.filled.Length || wd.dir.Length != wd.filled.Length ||
                halfWidth == null || depth == null || halfWidth.Length != wd.filled.Length || depth.Length != wd.filled.Length ||
                network.resX != wd.resX || network.resZ != wd.resZ || water.ResX != wd.resX || water.ResZ != wd.resZ)
                throw new ArgumentException("Invalid river outlet grid or dimensions.");
            if (water.WorldId != worldId || water.ProducerId != producerId)
                throw new InvalidOperationException("River outlet classification belongs to another world or producer.");
            if (stop != null && stop.StopRequested) return null;
            int n = network.reaches.Count, count = wd.filled.Length;
            if (n > count) throw new ArgumentException("Too many river links for the solve.");
            var include = new bool[n]; var pending = new int[n]; var order = new int[n];
            var equalOrders = new int[n]; var queue = new int[n]; var starts = new HashSet<int>();
            long visits = 0;
            for (int r = 0; r < n; r++)
            {
                if (stop != null && (r & 1023) == 0 && stop.StopRequested) return null;
                RiverReach reach = network.reaches[r];
                if (reach == null || reach.cells == null || reach.cells.Count == 0 || (visits += reach.cells.Count) > (long)count + n)
                    throw new ArgumentException("Invalid or unbounded river link cells.");
                if (!starts.Add(reach.cells[0]) || reach.downstream < -1 || reach.downstream >= n || reach.downstream == r)
                    throw new InvalidOperationException("Ambiguous or invalid river reach identity/receiver.");
                for (int k = 0; k < reach.cells.Count; k++)
                {
                    if (stop != null && (k & 1023) == 0 && stop.StopRequested) return null;
                    int c = reach.cells[k];
                    if ((uint)c >= (uint)count || !FiniteHierarchy(wd.height.arr[c]) || !FiniteHierarchy(wd.filled[c]) ||
                        !FiniteHierarchy(halfWidth[c]) || !FiniteHierarchy(depth[c]) || halfWidth[c] < 0 || depth[c] < 0)
                        throw new ArgumentException("Nonfinite or invalid river outlet sample.");
                    if (k > 0 && (Hydrology.DownstreamCell(wd.dir, reach.cells[k - 1], wd.resX, wd.resZ) != c ||
                        wd.filled[reach.cells[k - 1]] < wd.filled[c]))
                        throw new InvalidOperationException("River outlet link is disconnected or uphill.");
                }
                include[r] = reach.order >= Math.Max(1, minimumOrder);
            }
            for (int r = 0; r < n; r++)
            {
                int d = network.reaches[r].downstream;
                if (d < 0) continue;
                RiverReach source = network.reaches[r];
                if (source.cells[source.cells.Count - 1] != network.reaches[d].cells[0])
                    throw new InvalidOperationException("River receiver does not share the declared junction.");
                pending[d]++;
            }
            int head = 0, tail = 0;
            for (int r = 0; r < n; r++) if (pending[r] == 0) queue[tail++] = r;
            while (head < tail)
            {
                if (stop != null && (head & 1023) == 0 && stop.StopRequested) return null;
                int r = queue[head++];
                order[r] = !include[r] ? 0 : order[r] == 0 ? 1 : order[r] + (equalOrders[r] >= 2 ? 1 : 0);
                int d = network.reaches[r].downstream;
                if (d < 0) continue;
                if (include[r] && !include[d]) throw new InvalidOperationException("Order selection severs a river receiver.");
                if (order[r] > order[d]) { order[d] = order[r]; equalOrders[d] = 1; }
                else if (order[r] > 0 && order[r] == order[d]) equalOrders[d]++;
                if (--pending[d] == 0) queue[tail++] = d;
            }
            if (tail != n) throw new InvalidOperationException("River outlet topology contains a cycle.");

            var records = new List<RiverOutletRecord>();
            for (int r = 0; r < n; r++)
            {
                if (stop != null && (r & 1023) == 0 && stop.StopRequested) return null;
                if (!include[r]) continue;
                RiverReach reach = network.reaches[r];
                int end = reach.cells[reach.cells.Count - 1];
                bool ocean = water.Ocean[end], lake = !string.IsNullOrEmpty(water.Lakes[end]);
                if ((ocean && lake) || ((ocean || lake) && reach.downstream >= 0))
                    throw new InvalidOperationException("River outlet has ambiguous water/river destinations.");
                int sample = (ocean || lake) && reach.cells.Count > 1 ? reach.cells[reach.cells.Count - 2] : end;
                Vector3 position = OutletPosition(wd, end), sourcePosition = OutletPosition(wd, reach.cells[0]);
                Vector3 previous = reach.cells.Count > 1 ? OutletPosition(wd, reach.cells[reach.cells.Count - 2]) : position;
                var record = new RiverOutletRecord
                {
                    ReachId = producerId + ":reach:" + reach.cells[0], SourcePosition = sourcePosition, Position = position,
                    DirectionXZ = OutletDirection(position, previous),
                    Order = order[r], HierarchyLevel = reach.hierarchyLevel, HalfWidthMetres = halfWidth[sample], DepthMetres = depth[sample],
                    Diagnostic = string.Empty
                };
                if (reach.downstream >= 0)
                {
                    record.Kind = RiverOutletKind.ReceivingRiver;
                    record.ReceivingReachId = producerId + ":reach:" + network.reaches[reach.downstream].cells[0];
                    record.ReceivingHalfWidthMetres = halfWidth[end]; record.ReceivingDepthMetres = depth[end];
                }
                else if (ocean) { record.Kind = RiverOutletKind.Sea; record.ReceivingBodyId = "ocean"; }
                else if (lake) { record.Kind = RiverOutletKind.Lake; record.ReceivingBodyId = water.Lakes[end]; }
                else
                {
                    int x = end % wd.resX, z = end / wd.resX;
                    bool boundary = x == 0 || z == 0 || x == wd.resX - 1 || z == wd.resZ - 1;
                    record.Kind = !boundary && reach.end != RiverReachEnd.Confluence && Hydrology.DownstreamCell(wd.dir, end, wd.resX, wd.resZ) < 0
                        ? RiverOutletKind.Endorheic : RiverOutletKind.UnresolvedBoundary;
                    record.Diagnostic = record.Kind == RiverOutletKind.Endorheic ? "Interior drainage has no downstream cell." :
                        boundary ? "Course reaches the solve boundary without classified water." : "Course ends at a threshold gap or an omitted receiving reach.";
                }
                records.Add(record);
            }
            records.Sort((a, b) => string.CompareOrdinal(a.ReachId, b.ReachId));
            return stop != null && stop.StopRequested ? null : new RiverOutletTopology(worldId, producerId, records);
        }

        private static Vector3 OutletPosition(WorldDrainage wd, int cell)
        {
            Vector2 pixel = wd.height.PixelSize;
            Vector3 p = new Vector3((float)(wd.height.worldPos.x + (cell % wd.resX) * (double)pixel.x),
                (float)(wd.height.worldPos.y + wd.height.arr[cell] * (double)wd.height.worldHeight),
                (float)(wd.height.worldPos.z + (cell / wd.resX) * (double)pixel.y));
            if (!FiniteHierarchy(p.x) || !FiniteHierarchy(p.y) || !FiniteHierarchy(p.z) || pixel.x <= 0 || pixel.y <= 0 ||
                !FiniteHierarchy(wd.height.worldHeight) || wd.height.worldHeight <= 0)
                throw new ArgumentException("River outlet world position is not finite or its spacing is invalid.");
            return p;
        }

        private static Vector2 OutletDirection(Vector3 end, Vector3 previous)
        {
            double x = (double)end.x - previous.x, z = (double)end.z - previous.z;
            double length = Math.Sqrt(x * x + z * z);
            return length > 0 ? new Vector2((float)(x / length), (float)(z / length)) : Vector2.zero;
        }
    }
}
