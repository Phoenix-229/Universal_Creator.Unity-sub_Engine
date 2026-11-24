// UC-T03-04: bounded search, physical costs, reuse and actual world/chunk sampling.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Selectors;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestBoundedRiverSearch()
        {
            var workspace = new RiverSearchWorkspace(35);
            float[] field = { 1, 0, 1, 0, 1, 1, 1, 1, 1 };
            RiverSearchResult north = RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2f, new Vector2(10, 1), 100, 9, workspace);
            RiverSearchResult west = RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2f, new Vector2(1, 10), 100, 9, workspace);
            Check(north.Status == RiverSearchStatus.Found && west.Status == RiverSearchStatus.Found &&
                north.Course.Count == 2 && north.Course[1] == 1 && west.Course[1] == 3 && north.Cost == 1 && west.Cost == 1,
                "UC-T03-04 rectangular cell spacing chooses the physically nearer outlet; returned courses survive workspace reuse.");
            Func<float[], int, int, int, float, float, List<int>> original = RiverCourseTracer.TraceDownhill;
            Check(original(field, 3, 3, 1, 0.1f, 2f) == null && original(null, 3, 3, 4, 0.1f, 2f) == null,
                "UC-T03-04 original tracer method-group API retains submerged and missing-grid behavior.");

            var random = new System.Random(31415);
            bool optimal = true, bounded = true;
            int totalVisited = 0, peakFrontier = 0;
            double coldMs = 0, warmMs = 0;
            for (int run = 0; run < 16; run++)
            {
                var terrain = new float[35];
                for (int i = 0; i < terrain.Length; i++) terrain[i] = 0.2f + (float)random.NextDouble() * 0.8f;
                terrain[6] = terrain[34] = 0;
                Vector2 spacing = new Vector2(2 + run % 3, 7);
                RiverSearchResult result = RiverCourseTracer.TraceDownhill(terrain, 7, 5, 14, 0.1f, 2f, spacing, 50, 35, workspace);
                double expected = ExhaustiveRiverCost(terrain, 7, 14, 0.1f, spacing, 50, 2);
                optimal &= result.Status == RiverSearchStatus.Found && Math.Abs(result.Cost - expected) < 1e-8;
                bounded &= result.VisitedCount <= 35 && result.TouchedCount <= 35 && result.PeakFrontierCount <= 35;
                RiverSearchResult repeated = RiverCourseTracer.TraceDownhill(terrain, 7, 5, 14, 0.1f, 2f, spacing, 50, 35, workspace);
                optimal &= result.Cost == repeated.Cost && SameRiverPath(result.Course, repeated.Course);
                coldMs += result.ElapsedMilliseconds; warmMs += repeated.ElapsedMilliseconds;
                totalVisited += result.VisitedCount; peakFrontier = Math.Max(peakFrontier, result.PeakFrontierCount);
            }
            Check(optimal && bounded, "UC-T03-04 indexed search matches exhaustive relaxation on sixteen irregular rectangular grids and repeats deterministically.");
            Debug.Log("UC-T03-04 search pairs: changedFieldMs=" + coldMs + "; repeatedFieldMs=" + warmMs +
                "; settledCells=" + totalVisited + "; peakActiveFrontierBytes=" + (peakFrontier * 4L) +
                "; workspacePayloadBytes=" + workspace.PayloadBytes + "; capacity=" + workspace.Capacity);

            var closed = new float[35]; for (int i = 0; i < closed.Length; i++) closed[i] = 1;
            RiverSearchResult noOutlet = RiverCourseTracer.TraceDownhill(closed, 7, 5, 0, 0.1f, 2, Vector2.one, 100, 35, workspace);
            RiverSearchResult limited = RiverCourseTracer.TraceDownhill(closed, 7, 5, 0, 0.1f, 2, Vector2.one, 100, 1, workspace);
            RiverSearchResult early = RiverCourseTracer.TraceDownhill(closed, 7, 5, 0, 0.1f, 2, Vector2.one, 100, 35, workspace, () => true);
            int polls = 0;
            RiverSearchResult middle = RiverCourseTracer.TraceDownhill(closed, 7, 5, 0, 0.1f, 2, Vector2.one, 100, 35, workspace, () => ++polls == 6);
            Check(noOutlet.Status == RiverSearchStatus.NoOutlet && noOutlet.VisitedCount == 35 &&
                limited.Status == RiverSearchStatus.VisitLimitExceeded && limited.VisitedCount == 1 && limited.Course == null &&
                early.Status == RiverSearchStatus.Cancelled && early.VisitedCount == 0 && middle.Status == RiverSearchStatus.Cancelled &&
                middle.VisitedCount > 0 && middle.VisitedCount < 35 && middle.Course == null,
                "UC-T03-04 closed terrain, exhausted visits and cancellation before/during settlement have distinct outcomes without partial courses.");

            float[] pit = { 0.3f, 0.7f, 0f };
            RiverSearchResult escape = RiverCourseTracer.TraceDownhill(pit, 3, 1, 0, 0.1f, 2, Vector2.one, 100, 3, workspace);
            Check(escape.Status == RiverSearchStatus.Found && escape.Course.Count == 3 && escape.Cost > 2,
                "UC-T03-04 a pit with a legal uphill lip reaches its outlet and pays the physical climb cost.");
            pit[1] = float.NaN;
            bool invalid = RiverCourseTracer.TraceDownhill(pit, 3, 1, 0, 0.1f, 2, Vector2.one, 100, 3, workspace).Status == RiverSearchStatus.InvalidInput;
            invalid &= RiverCourseTracer.TraceDownhill(field, int.MaxValue, int.MaxValue, 0, 0.1f, 2, Vector2.one, 100, 3, workspace).Status == RiverSearchStatus.InvalidInput;
            invalid &= RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, -1, Vector2.one, 100, 9, workspace).Status == RiverSearchStatus.InvalidInput;
            invalid &= RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2, Vector2.zero, 100, 9, workspace).Status == RiverSearchStatus.InvalidInput;
            pit[1] = float.MaxValue;
            RiverSearchResult extreme = RiverCourseTracer.TraceDownhill(pit, 3, 1, 0, 0.1f, float.MaxValue,
                new Vector2(float.MaxValue, 1), float.MaxValue, 3, workspace);
            Check(invalid && extreme.Status == RiverSearchStatus.Found && !double.IsInfinity(extreme.Cost) && !double.IsNaN(extreme.Cost),
                "UC-T03-04 rejects malformed settings/dimensions/encountered samples and keeps finite extreme costs representable.");

            bool rejectedOverlap = false;
            RiverSearchResult guarded = RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2, Vector2.one, 100, 9, workspace, () => {
                try { RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2, Vector2.one, 100, 9, workspace); }
                catch (InvalidOperationException) { rejectedOverlap = true; }
                return true;
            });
            Task<RiverSearchResult> first = Task.Run(() => RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2, Vector2.one, 100, 9, workspace));
            Task<RiverSearchResult> second = Task.Run(() => RiverCourseTracer.TraceDownhill(field, 3, 3, 4, 0.1f, 2, Vector2.one, 100, 9, new RiverSearchWorkspace(9)));
            Task.WaitAll(first, second);
            Check(rejectedOverlap && guarded.Status == RiverSearchStatus.Cancelled && SameRiverPath(first.Result.Course, second.Result.Course),
                "UC-T03-04 a workspace rejects overlapping ownership, releases it on cancellation and permits independent concurrent searches.");
            var cut = new float[49]; var mask = new float[49];
            RiverSeedCourseNode.Stamp(24, 7, 7, new Vector2(2, 1), 1, 4, 0.1f, cut, mask, null);
            Check(mask[25] > 0 && mask[25] < 1 && Mathf.Abs(mask[25] - mask[38]) < 1e-6f &&
                Mathf.Abs(cut[25] - cut[38]) < 1e-6f && mask[31] == 1,
                "UC-T03-04 the production stamp has equal width at equal physical X/Z distances on a rectangular lattice.");
            TestRiverSearchChunkSampling();
        }

        // Independent Bellman-Ford relaxation on tiny fixtures; no heap, touched-list or tracer code is reused.
        private static double ExhaustiveRiverCost(float[] heights, int width, int source, float sea,
                                                   Vector2 spacing, double heightScale, double penalty)
        {
            var cost = new double[heights.Length];
            for (int i = 0; i < cost.Length; i++) cost[i] = double.PositiveInfinity;
            cost[source] = 0;
            for (int pass = 1; pass < cost.Length; pass++)
            {
                bool changed = false;
                for (int i = 0; i < cost.Length; i++)
                    for (int j = 0; j < cost.Length; j++)
                    {
                        int dx = Math.Abs(i % width - j % width), dz = Math.Abs(i / width - j / width);
                        if ((dx == 0 && dz == 0) || dx > 1 || dz > 1) continue;
                        double length = Math.Sqrt(dx * (double)spacing.x * spacing.x + dz * (double)spacing.y * spacing.y);
                        double next = cost[i] + length + Math.Max(0, (double)heights[j] - heights[i]) * heightScale * penalty;
                        if (next < cost[j]) { cost[j] = next; changed = true; }
                    }
                if (!changed) break;
            }
            double answer = double.PositiveInfinity;
            for (int i = 0; i < cost.Length; i++) if (heights[i] < sea) answer = Math.Min(answer, cost[i]);
            return answer;
        }

        private static bool SameRiverPath(List<int> a, List<int> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        [Serializable]
        private sealed class SearchWorldField : CreatorNode
        {
            public bool sources;
            public override string NodeName => "Search world fixture";
            public override NodeCategory Category => NodeCategory.Creator;
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                Vector2 pixel = matrix.PixelSize;
                for (int z = 0; z < matrix.rect.size.z; z++)
                    for (int x = 0; x < matrix.rect.size.x; x++)
                    {
                        float u = (matrix.worldPos.x + x * pixel.x - tile.fullWorldPos.x) / tile.fullWorldSize.x;
                        float v = (matrix.worldPos.z + z * pixel.y - tile.fullWorldPos.z) / tile.fullWorldSize.z;
                        matrix.arr[z * matrix.rect.size.x + x] = sources ? (u < 0.1f && Mathf.Abs(v - 0.5f) < 0.08f ? 1f : 0f) : 1f - 0.95f * u;
                    }
            }
        }

        private static void TestRiverSearchChunkSampling()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tiles = new List<TileData>();
            try
            {
                var ground = new SearchWorldField { id = "search-world-ground" };
                var seeds = new SearchWorldField { id = "search-world-seeds", sources = true };
                var river = new RiverSeedCourseNode { id = "search-world-river", maxRivers = 1, solveResolution = 32,
                    seaLevel = 0.2f, channelHalfWidthMetres = 45, floodplainMetres = 100, channelDepth = 0.02f };
                graph.nodes.AddRange(new Generator[] { ground, seeds, river });
                graph.AddConnection(new Connection(ground.id, 0, river.id, 0));
                graph.AddConnection(new Connection(seeds.id, 0, river.id, 1));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph);
                bool equal = true, carved = false;
                for (int scenario = 0; scenario < 4; scenario++)
                {
                    float scale = scenario < 2 ? 1 : 2;
                    Vector3 origin = scenario % 2 == 0 ? new Vector3(-600, 0, -900) : new Vector3(300, 0, 200);
                    Vector3 whole = new Vector3(1200 * scale, 600, 600 * scale);
                    TileData west = SearchTile(evaluator, origin, new Vector3(whole.x * 0.5f, 600, whole.z), origin, whole, 17);
                    TileData east = SearchTile(evaluator, origin + new Vector3(whole.x * 0.5f, 0, 0), new Vector3(whole.x * 0.5f, 600, whole.z), origin, whole, 33);
                    TileData full = SearchTile(evaluator, origin, whole, origin, whole, 33);
                    tiles.Add(west); tiles.Add(east); tiles.Add(full);
                    Matrix left, right;
                    if (scenario % 2 == 0) { left = (Matrix)evaluator.PullNode(river, 0, west, null); right = (Matrix)evaluator.PullNode(river, 0, east, null); }
                    else { right = (Matrix)evaluator.PullNode(river, 0, east, null); left = (Matrix)evaluator.PullNode(river, 0, west, null); }
                    Matrix leftMask = (Matrix)evaluator.PullNode(river, 1, west, null), rightMask = (Matrix)evaluator.PullNode(river, 1, east, null);
                    Matrix fullHeight = (Matrix)evaluator.PullNode(river, 0, full, null);
                    for (int z = 0; z < 17; z++)
                    {
                        int l = z * 17 + 16, r = z * 2 * 33;
                        equal &= Mathf.Abs(left.arr[l] - right.arr[r]) < 1e-6f && Mathf.Abs(leftMask.arr[l] - rightMask.arr[r]) < 1e-6f &&
                            Mathf.Abs(left.arr[l] - fullHeight.arr[r + 16]) < 1e-6f;
                        carved |= left.arr[l] < 0.525f - 1e-5f && leftMask.arr[l] > 0;
                    }
                }
                Check(equal && carved, "UC-T03-04 actual seeded height/mask agree across mixed-resolution borders, world scales, signed origins and reversed generation order.");

                TileData budgetTile = SearchTile(evaluator, Vector3.zero, new Vector3(1200, 600, 600), Vector3.zero, new Vector3(1200, 600, 600), 33);
                tiles.Add(budgetTile); river.maxSearchVisits = 1;
                bool refused = false;
                try { evaluator.PullNode(river, 0, budgetTile, null); }
                catch (InvalidOperationException ex) { refused = ex.Message.Contains("VisitLimitExceeded"); }
                river.maxSearchVisits = 0;
                Matrix retry = evaluator.PullNode(river, 0, budgetTile, null) as Matrix;
                Check(refused && retry != null, "UC-T03-04 production rejects a budget-exhausted search and permits a complete retry without retaining a partial node product.");

                WorldDrainage solve = WorldDrainage.Solve(river, 0, budgetTile, 32, 0, false, null);
                Func<float[], Matrix, WorldRead, float[]> originalRead = solve.ReadAtChunk;
                Check(FieldsIdentical(originalRead(solve.filled, retry, WorldRead.Bilinear), solve.ReadAtChunk(solve.filled, retry, WorldRead.Bilinear, null)),
                    "UC-T03-04 original resampling method-group signature preserves complete reads.");
                var stop = new StopToken(); stop.RequestStop(); bool cancelled = false;
                try { solve.ReadAtChunk(solve.filled, retry, WorldRead.Bilinear, stop); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "UC-T03-04 cancelled resampling returns no partially filled chunk field.");
                int rowPolls = 0; bool cancelledInRows = false;
                try { solve.ReadAtChunkCancellable(solve.filled, retry, WorldRead.Bilinear, () => ++rowPolls == 3); }
                catch (OperationCanceledException) { cancelledInRows = true; }
                Check(cancelledInRows && rowPolls == 3,
                    "UC-T03-04 cancellation inside the production sampling row loop discards the incomplete field.");
            }
            finally
            {
                foreach (TileData tile in tiles) tile.ReleaseProductMemory();
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static TileData SearchTile(GraphEvaluator evaluator, Vector3 origin, Vector3 size, Vector3 wholeOrigin, Vector3 wholeSize, int resolution)
        {
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, resolution, resolution), origin, size, 600);
            tile.SetFullWorld(wholeOrigin, wholeSize); tile.Bind(evaluator); return tile;
        }
    }
}
