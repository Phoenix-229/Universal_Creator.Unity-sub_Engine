// UC-T03-05: select connected river families from one extracted drainage tree.
using System;
using System.Collections.Generic;
using UniverseCreator.Nodes;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Optional course-count targets; disabled preserves the existing slope/area network.
    /// Counts describe continuous main stems, not individual links between confluences.</summary>
    [Serializable]
    public sealed class RiverHierarchyTargets
    {
        /// <summary>Enables selection; false leaves the extracted network unchanged.</summary>
        [Tooltip("Select authored river families from the existing drainage tree. Off retains all eligible channels.")]
        public bool enabled;
        /// <summary>Independent terminal main stems at the reference area, 0..4096.</summary>
        [Range(0, 4096), Tooltip("Independent big rivers at the reference area, 0–4096. Scales by world area when Scale To World is on.")] public int trunks = 2;
        /// <summary>Total side rivers entering selected trunks at the reference area, 0..4096.</summary>
        [Range(0, 4096), Tooltip("Total side rivers entering selected trunks, 0–4096; counted across the whole world.")] public int rivers = 4;
        /// <summary>Side streams requested for EACH selected river, 0..64; unchanged by world scaling.</summary>
        [Range(0, 64), Tooltip("Streams requested for each selected side river, 0–64; does not scale with world area.")] public int streamsPerRiver = 4;
        /// <summary>Reference world area in square metres, finite and positive; default is 2 km by 2 km.</summary>
        [Tooltip("Square metres represented by the authored counts. 4,000,000 means a 2 km × 2 km world.")]
        public float referenceAreaM2 = 4000000f;
        /// <summary>Minimum distance between selected course heads in world metres, finite and nonnegative.
        /// With automatic scaling it scales with the square root of the world/reference area ratio.</summary>
        [Tooltip("Minimum course-head separation in metres. Scale To World scales this as a length; off preserves the authored metres.")]
        public float minimumSeparationMetres = 50f;
        /// <summary>Permitted fractional count shortfall, 0..1; zero demands the exact counts.
        /// Each selected river must also meet its own stream target within this tolerance.</summary>
        [Range(0f, 1f), Tooltip("Permitted fraction of missing courses, 0–1. Zero demands exact counts; 0.25 permits a 25% shortfall.")] public float tolerance = 0.25f;
    }

    /// <summary>Immutable achieved/requested course counts and constraints from one selection.
    /// This is generation evidence, not a promise that arbitrary terrain can meet an authored target.</summary>
    public sealed class RiverHierarchyReport
    {
        /// <summary>Requested terminal trunks after optional area scaling.</summary>
        public int RequestedTrunks { get; internal set; }
        /// <summary>Requested total rivers after optional area scaling.</summary>
        public int RequestedRivers { get; internal set; }
        /// <summary>Requested streams for each river, without area scaling.</summary>
        public int RequestedStreamsPerRiver { get; internal set; }
        /// <summary>Selected independent main stems.</summary>
        public int Trunks { get; internal set; }
        /// <summary>Selected side rivers connected to those stems.</summary>
        public int Rivers { get; internal set; }
        /// <summary>Selected side streams connected to those rivers.</summary>
        public int Streams { get; internal set; }
        /// <summary>Selected rivers whose stream count falls outside tolerance.</summary>
        public int RiversBelowStreamTarget { get; internal set; }
        /// <summary>Number of distinct candidate courses inspected; selection uses three bounded passes.</summary>
        public int CandidateCourses { get; internal set; }
        /// <summary>Candidates refused by the resolved head separation, in the relevant selection pass.</summary>
        public int SeparationRejected { get; internal set; }
        /// <summary>Resolved head separation in world metres.</summary>
        public double SeparationMetres { get; internal set; }
        /// <summary>True only when trunk, river and every selected river's stream counts meet tolerance.</summary>
        public bool Feasible { get; internal set; }
        /// <summary>Human-readable achieved/requested counts and the constraints behind a shortfall.</summary>
        public string Diagnostic { get; internal set; }
    }

    public static partial class RiverNetworkExtractor
    {
        private sealed class HierarchyCourse
        {
            internal int Outlet, HeadCell, Parent = -1, Level = -1, Children;
            internal double Area;
        }

        /// <summary>Selects connected trunks, rivers and streams without solving or changing drainage.
        /// Disabled targets return the supplied network. Enabled selection returns new reach records;
        /// their cell lists remain borrowed and must be treated as immutable.</summary>
        /// <param name="wd">The same finite, metre-scaled solve used to extract the network.</param>
        /// <param name="network">Directed reach tree indexed into that solve; not modified.</param>
        /// <param name="targets">Optional authored counts, separation and tolerance.</param>
        /// <param name="scaleToWorld">Scale trunk/river counts by world area and separation by its square root.
        /// False uses authored counts and metres unchanged; neither mode changes extraction thresholds.</param>
        /// <param name="minimumOrder">Minimum original Strahler order to include, at least one.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Selected network with its report, or null on cancellation.</returns>
        /// <exception cref="ArgumentException">Invalid enabled targets, grid or reach metadata.</exception>
        /// <exception cref="InvalidOperationException">The supplied reaches contain a cycle or broken downstream link.</exception>
        public static RiverNetwork SelectHierarchy(WorldDrainage wd, RiverNetwork network,
            RiverHierarchyTargets targets, bool scaleToWorld, int minimumOrder, StopToken stop)
        {
            if (targets == null || !targets.enabled) return network;
            if (stop != null && stop.StopRequested) return null;
            if (wd == null || wd.height == null || network == null || wd.accum == null || wd.dir == null || wd.filled == null ||
                wd.resX < 2 || wd.resZ < 2 || (long)wd.resX * wd.resZ != wd.accum.Length ||
                wd.filled.Length != wd.accum.Length || wd.dir.Length != wd.accum.Length ||
                network.resX != wd.resX || network.resZ != wd.resZ ||
                targets.trunks < 0 || targets.trunks > 4096 || targets.rivers < 0 || targets.rivers > 4096 ||
                targets.streamsPerRiver < 0 || targets.streamsPerRiver > 64 ||
                !FiniteHierarchy(targets.referenceAreaM2) || targets.referenceAreaM2 <= 0 ||
                !FiniteHierarchy(targets.minimumSeparationMetres) || targets.minimumSeparationMetres < 0 ||
                !FiniteHierarchy(targets.tolerance) || targets.tolerance < 0 || targets.tolerance > 1)
                throw new ArgumentException("Invalid river hierarchy targets or drainage grid.");

            Vector2 pixel = wd.height.PixelSize;
            if (!FiniteHierarchy(pixel.x) || !FiniteHierarchy(pixel.y) || pixel.x <= 0 || pixel.y <= 0)
                throw new ArgumentException("River hierarchy requires finite positive X/Z cell spacing.");
            double areaRatio = scaleToWorld
                ? (double)wd.height.worldSize.x * wd.height.worldSize.z / targets.referenceAreaM2 : 1;
            if (double.IsNaN(areaRatio) || double.IsInfinity(areaRatio) || areaRatio <= 0)
                throw new ArgumentException("River hierarchy requires a finite positive world area.");
            var report = new RiverHierarchyReport
            {
                RequestedTrunks = HierarchyCount(targets.trunks, areaRatio),
                RequestedRivers = HierarchyCount(targets.rivers, areaRatio),
                RequestedStreamsPerRiver = targets.streamsPerRiver,
                SeparationMetres = targets.minimumSeparationMetres * Math.Sqrt(areaRatio)
            };

            // Apply the authored order floor before course counting. Downstream closure is mandatory.
            int n = network.reaches.Count;
            if (n > wd.accum.Length) throw new ArgumentException("River hierarchy has more reaches than solve cells.");
            var keep = new bool[n];
            long cellVisits = 0;
            for (int i = 0; i < n; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) return null;
                RiverReach reach = network.reaches[i];
                if (reach == null || reach.cells == null || reach.cells.Count == 0)
                    throw new ArgumentException("River hierarchy requires nonempty reaches.");
                cellVisits += reach.cells.Count;
                if (cellVisits > (long)wd.accum.Length + n)
                    throw new ArgumentException("River hierarchy exceeds the solve's cell/link bound.");
                for (int station = 0; station < reach.cells.Count; station++)
                {
                    if (stop != null && (station & 1023) == 0 && stop.StopRequested) return null;
                    int cell = reach.cells[station];
                    if ((uint)cell >= (uint)wd.accum.Length || !FiniteHierarchy(wd.accum[cell]) || wd.accum[cell] < 0 ||
                        !FiniteHierarchy(wd.filled[cell]))
                        throw new ArgumentException("River hierarchy requires valid cells and finite nonnegative accumulation.");
                    if (station > 0 && (Hydrology.DownstreamCell(wd.dir, reach.cells[station - 1], wd.resX, wd.resZ) != cell ||
                        wd.filled[reach.cells[station - 1]] < wd.filled[cell]))
                        throw new ArgumentException("River hierarchy contains a disconnected or uphill course.");
                }
                if (reach.downstream < -1 || reach.downstream >= n || reach.downstream == i)
                    throw new InvalidOperationException("River hierarchy has a broken downstream link.");
                keep[i] = reach.order >= Math.Max(1, minimumOrder);
            }
            RiverNetwork eligible = CopyHierarchyNetwork(network, keep, null, null);
            if (stop != null && stop.StopRequested) return null;
            n = eligible.reaches.Count;
            int[] bestUpstream = new int[n], incoming = new int[n], queue = new int[n];
            double[] contribution = new double[n];
            for (int i = 0; i < n; i++)
            {
                bestUpstream[i] = -1;
                RiverReach r = eligible.reaches[i];
                // A shared confluence cell includes the sibling catchment: measure before that cell.
                int sample = r.cells.Count - (r.cells.Count > 1 && (r.downstream >= 0 || r.end == RiverReachEnd.Water) ? 2 : 1);
                contribution[i] = wd.accum[r.cells[sample]];
            }
            for (int i = 0; i < n; i++)
            {
                int d = eligible.reaches[i].downstream;
                if (d < 0) continue;
                incoming[d]++;
                int previous = bestUpstream[d];
                if (previous < 0 || contribution[i] > contribution[previous] ||
                    (contribution[i] == contribution[previous] && eligible.reaches[i].cells[0] < eligible.reaches[previous].cells[0]))
                    bestUpstream[d] = i;
            }
            int head = 0, tail = 0;
            for (int i = 0; i < n; i++) if (incoming[i] == 0) queue[tail++] = i;
            while (head < tail)
            {
                if (stop != null && (head & 1023) == 0 && stop.StopRequested) return null;
                int d = eligible.reaches[queue[head++]].downstream;
                if (d >= 0 && --incoming[d] == 0) queue[tail++] = d;
            }
            if (tail != n) throw new InvalidOperationException("River hierarchy contains a directed cycle.");

            // Every link belongs to exactly one continuous main stem. Work is linear in reach count.
            var courses = new List<HierarchyCourse>();
            int[] courseByReach = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) return null;
                int d = eligible.reaches[i].downstream;
                if (d >= 0 && bestUpstream[d] == i) continue;
                var course = new HierarchyCourse { Outlet = i, Area = contribution[i] };
                int current = i;
                while (current >= 0)
                {
                    if (stop != null && stop.StopRequested) return null;
                    courseByReach[current] = courses.Count;
                    course.HeadCell = eligible.reaches[current].cells[0];
                    current = bestUpstream[current];
                }
                courses.Add(course);
            }
            for (int i = 0; i < courses.Count; i++)
            {
                int d = eligible.reaches[courses[i].Outlet].downstream;
                if (d >= 0) courses[i].Parent = courseByReach[d];
            }
            int[] ranked = new int[courses.Count];
            for (int i = 0; i < ranked.Length; i++) ranked[i] = i;
            Array.Sort(ranked, (a, b) =>
            {
                int area = courses[b].Area.CompareTo(courses[a].Area);
                return area != 0 ? area : courses[a].HeadCell.CompareTo(courses[b].HeadCell);
            });
            report.CandidateCourses = courses.Count;
            var heads = new Dictionary<(double, double), List<int>>();
            double separation = report.SeparationMetres;
            for (int level = 0; level < 3; level++)
            {
                foreach (int index in ranked)
                {
                    if (stop != null && stop.StopRequested) return null;
                    HierarchyCourse course = courses[index];
                    if (level == 0)
                    {
                        if (course.Parent >= 0 || report.Trunks >= report.RequestedTrunks ||
                            eligible.reaches[course.Outlet].end == RiverReachEnd.Confluence) continue;
                    }
                    else
                    {
                        if (course.Parent < 0 || courses[course.Parent].Level != level - 1) continue;
                        if (level == 1 && report.Rivers >= report.RequestedRivers) continue;
                        if (level == 2 && courses[course.Parent].Children >= targets.streamsPerRiver) continue;
                    }
                    if (!AdmitHierarchyHead(course.HeadCell, wd.resX, pixel, separation, heads))
                    { report.SeparationRejected++; continue; }
                    course.Level = level;
                    if (level == 0) report.Trunks++;
                    else if (level == 1) report.Rivers++;
                    else { report.Streams++; courses[course.Parent].Children++; }
                }
            }
            foreach (HierarchyCourse course in courses)
                if (course.Level == 1 && course.Children + 1e-9 < targets.streamsPerRiver * (1 - (double)targets.tolerance))
                    report.RiversBelowStreamTarget++;
            report.Feasible = report.Trunks + 1e-9 >= report.RequestedTrunks * (1 - (double)targets.tolerance) &&
                report.Rivers + 1e-9 >= report.RequestedRivers * (1 - (double)targets.tolerance) && report.RiversBelowStreamTarget == 0;
            report.Diagnostic = "River hierarchy " + (report.Feasible ? "within tolerance" : "target infeasible for the selected largest-catchment families") +
                ": trunks " + report.Trunks + "/" + report.RequestedTrunks + ", rivers " + report.Rivers + "/" + report.RequestedRivers +
                ", streams " + report.Streams + "/" + ((long)report.RequestedRivers * targets.streamsPerRiver) +
                "; " + report.RiversBelowStreamTarget + " rivers below their stream target; " + courses.Count +
                " candidate courses; " + report.SeparationRejected + " rejected by head separation. " +
                "Available channels are constrained by drainage direction, water coverage, initiation thresholds, minimum reach length and order.";
            var selected = new bool[n];
            for (int i = 0; i < n; i++) selected[i] = courses[courseByReach[i]].Level >= 0;
            RiverNetwork result = CopyHierarchyNetwork(eligible, selected, courses, courseByReach);
            result.hierarchy = report;
            return stop != null && stop.StopRequested ? null : result;
        }

        private static int HierarchyCount(int count, double ratio) => count == 0 ? 0 :
            (int)Math.Min(int.MaxValue, Math.Max(1, Math.Floor(count * ratio + 0.5)));

        private static bool FiniteHierarchy(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool AdmitHierarchyHead(int cell, int resX, Vector2 pixel, double separation,
            Dictionary<(double, double), List<int>> occupied)
        {
            if (separation <= 0) return true;
            double x = cell % resX * (double)pixel.x, z = cell / resX * (double)pixel.y;
            double bx = Math.Floor(x / separation), bz = Math.Floor(z / separation);
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                if (occupied.TryGetValue((bx + dx, bz + dz), out List<int> neighbours))
                    foreach (int other in neighbours)
                    {
                        double rx = x - other % resX * (double)pixel.x, rz = z - other / resX * (double)pixel.y;
                        if (rx * rx + rz * rz < separation * separation) return false;
                    }
            if (!occupied.TryGetValue((bx, bz), out List<int> bucket)) occupied.Add((bx, bz), bucket = new List<int>());
            bucket.Add(cell);
            return true;
        }

        private static RiverNetwork CopyHierarchyNetwork(RiverNetwork source, bool[] keep,
            List<HierarchyCourse> courses, int[] courseByReach)
        {
            var result = new RiverNetwork { resX = source.resX, resZ = source.resZ };
            int[] map = new int[keep.Length];
            for (int i = 0; i < keep.Length; i++)
            {
                map[i] = -1;
                if (!keep[i]) continue;
                RiverReach original = source.reaches[i];
                map[i] = result.reaches.Count;
                HierarchyCourse course = courses == null ? null : courses[courseByReach[i]];
                result.reaches.Add(new RiverReach
                {
                    cells = original.cells, order = original.order, downstream = original.downstream, end = original.end,
                    outletAreaM2 = original.outletAreaM2, outletDischargeM3PerS = original.outletDischargeM3PerS,
                    outletHalfWidthM = original.outletHalfWidthM, outletDepthM = original.outletDepthM,
                    outletFloodplainHalfWidthM = original.outletFloodplainHalfWidthM,
                    hierarchyLevel = course == null ? -1 : course.Level,
                    courseId = course == null ? -1 : course.HeadCell,
                    parentCourseId = course == null || course.Parent < 0 ? -1 : courses[course.Parent].HeadCell
                });
            }
            foreach (RiverReach reach in result.reaches)
                if (reach.downstream >= 0)
                {
                    if (map[reach.downstream] < 0) throw new InvalidOperationException("River selection would sever a downstream connection.");
                    reach.downstream = map[reach.downstream];
                }
            return result;
        }
    }
}
