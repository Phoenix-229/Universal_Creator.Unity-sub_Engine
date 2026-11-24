// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    PoissonDisc.cs
//  Purpose: Blue-noise point distribution with a guaranteed minimum spacing.
//           This is the placement solver behind every Scatter node.
//
//  WHY NOT BRIDSON
//  ---------------
//  The textbook Bridson dart-throwing algorithm grows an active front from a
//  random seed point. That makes the result depend on where the front started,
//  which means a chunk cannot be solved without knowing its neighbours - and two
//  chunks solved independently would disagree about the points on their shared
//  edge, leaving a visible seam of doubled or missing trees.
//
//  So this uses jittered-grid blue noise instead: the world is divided into
//  cells in ABSOLUTE world space, and each cell's candidate point is derived
//  from a hash of its integer world cell coordinate. A cell resolves to the same
//  point no matter which chunk asks, or whether a neighbour was ever generated.
//  Rejection then runs against a SpatialHash seeded with the surrounding ring of
//  cells, so spacing holds across the boundary as well as inside it.
//
//  The distribution is not a strict maximal Poisson set - jittered grid never
//  is - but it is blue-noise in the way that matters visually (no clumps, no
//  lattice) and it is deterministic and chunk-local, which strict Poisson is not.
//
//  Managed C# on plain arrays and Lists. No Burst, no NativeArray.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>One accepted placement candidate, in absolute world space.</summary>
    public struct ScatterCandidate
    {
        /// <summary>World X, metres.</summary>
        public float worldX;

        /// <summary>World Z, metres.</summary>
        public float worldZ;

        /// <summary>Normalised height sampled at this position, 0-1.</summary>
        public float height;

        /// <summary>Slope at this position, degrees.</summary>
        public float slope;

        /// <summary>Mask value at this position, 0-1. 1 when no mask is wired.</summary>
        public float maskValue;

        /// <summary>Deterministic 0-1 value unique to this candidate, for scale and yaw.</summary>
        public float random;
    }

    /// <summary>
    /// Parameters for one scatter solve. Grouped into a struct because seven
    /// loose arguments at a call site is where transposed-parameter bugs live.
    /// </summary>
    public struct PoissonSettings
    {
        /// <summary>Minimum world-metre distance between any two accepted points.</summary>
        public float minSpacing;

        /// <summary>
        /// Attempts per grid cell. Above 1 the extra tries let a cell recover
        /// when its first candidate lands on rejected ground, which fills the
        /// edges of a mask instead of leaving them bald.
        /// </summary>
        public int attemptsPerCell;

        /// <summary>
        /// 0 places every point at its cell centre - a visible lattice. 1 lets a
        /// point land anywhere in its cell, which is the natural look but allows
        /// two neighbours to approach closely enough that spacing rejection has
        /// to do real work.
        /// </summary>
        public float jitter;

        /// <summary>Seed, combined with world cell coordinates for determinism.</summary>
        public int seed;

        /// <summary>Hard cap on accepted points, guarding against a runaway density.</summary>
        public int maxPoints;

        /// <summary>UC-T06-05: use a cap-independent world lattice and bounded local-priority spacing. False preserves the serialized legacy distribution.</summary>
        /// <remarks>One candidate per spacing cell; attemptsPerCell is a legacy-only setting. Caps select a stable ranked subset. Very large requests fail rather than coarsen the lattice silently.</remarks>
        public bool stableWorldGrid;

        public static PoissonSettings Default
        {
            get
            {
                PoissonSettings s;
                s.minSpacing = 2f;
                s.attemptsPerCell = 2;
                s.jitter = 1f;
                s.seed = 1000;
                s.maxPoints = 100000;
                s.stableWorldGrid = false;
                return s;
            }
        }
    }

    /// <summary>
    /// Deterministic, chunk-seamless blue-noise point placement.
    /// </summary>
    public static class PoissonDisc
    {
        /// <summary>
        /// Solves placement over one tile's world footprint.
        /// </summary>
        /// <param name="settings">Spacing, jitter, seed, and caps.</param>
        /// <param name="worldMinX">Tile's minimum world X.</param>
        /// <param name="worldMinZ">Tile's minimum world Z.</param>
        /// <param name="worldWidth">Tile extent along X, world metres.</param>
        /// <param name="worldDepth">Tile extent along Z, world metres.</param>
        /// <param name="accept">
        /// Per-candidate predicate. Receives world X/Z and returns the acceptance
        /// weight: 0 rejects outright, 1 always accepts, values between are
        /// treated as a probability so a density mask thins the field rather than
        /// cutting it off with a hard edge. Called on the worker thread, so it
        /// must not touch the Unity API.
        /// </param>
        /// <param name="report">Optional worker callback, called once after a successful sampling pass with immutable work/rejection counts.</param>
        /// <param name="isCancelled">
        /// Optional cancellation probe, checked once per grid row and again every
        /// 4096 cells within a row. Returning true stops the solve and returns
        /// whatever was accepted so far.
        /// </param>
        /// <returns>Accepted candidates in deterministic order; the stable mode returns an empty list on cancellation.</returns>
        /// <exception cref="ArgumentException">Stable mode receives nonfinite or nonpositive world dimensions/spacing, or nonfinite jitter.</exception>
        /// <exception cref="InvalidOperationException">Stable mode exceeds its fixed 262144-cell work limit, signed world-cell range or float-coordinate precision envelope.</exception>
        public static List<ScatterCandidate> Solve(
            PoissonSettings settings,
            float worldMinX, float worldMinZ,
            float worldWidth, float worldDepth,
            Func<float, float, float> accept,
            Func<bool> isCancelled = null, Action<PoissonSamplingReport> report = null)
        {
            if (settings.stableWorldGrid)
                return SolveStable(settings, worldMinX, worldMinZ, worldWidth, worldDepth, accept, isCancelled, report); // UC-T06-05: preserve the old path for saved graphs.
            List<ScatterCandidate> results = new List<ScatterCandidate>();

            float spacing = Mathf.Max(0.01f, settings.minSpacing);
            if (worldWidth <= 0f || worldDepth <= 0f) return results;

            int maxPoints = settings.maxPoints > 0 ? settings.maxPoints : 100000;
            int attempts = Mathf.Clamp(settings.attemptsPerCell, 1, 8);
            float jitter = Mathf.Clamp01(settings.jitter);

            // Cell size is the spacing itself. One point per cell then sits at
            // most sqrt(2)*spacing from its diagonal neighbour and at least 0
            // from its side neighbour, so rejection still has to run - but the
            // count of tests per candidate stays at a handful.
            float cellSize = spacing;

            // Runaway guard (P-HANG root cause). The grid iterates one cell at a
            // time and each cell can contribute at most one accepted point, while
            // the whole solve accepts at most maxPoints. So a grid finer than what
            // maxPoints points could ever fill is pure wasted iteration: at the
            // 0.01 m spacing floor a 256 m tile is ~6.5e8 cells, and when the
            // accept predicate rejects nearly all of them - a species pinned to a
            // narrow height or slope band - the loop scans every one of those cells
            // to accept a handful, which is the "stuck on chunk 1 at 0 %" hang.
            //
            // Bound the cell to the finest USEFUL size, sqrt(tileArea / maxPoints),
            // so the grid never holds materially more than maxPoints cells. This is
            // a no-op at every realistic density (spacing is already coarser than
            // this floor until roughly 1.8 M instances per km^2), so normal scatter
            // results are byte-identical. It bites only in the pathological regime
            // that used to hang, and even there the total is still capped at
            // maxPoints, unchanged - only the wasted empty-cell scan is removed.
            //
            // tileArea and maxPoints are identical for every chunk of a species, so
            // cellSize is identical across a seam and the absolute-world cell grid
            // (Floor(worldMinX / cellSize)) stays aligned. Spacing rejection below
            // keeps using the true `spacing`, so the requested minimum distance is
            // never loosened by this clamp.
            float usefulCell = Mathf.Sqrt((worldWidth * worldDepth) / maxPoints);
            if (usefulCell > cellSize) cellSize = usefulCell;

            // Integer cell coordinates in ABSOLUTE world space. Deriving them
            // from the world origin rather than the tile origin is what makes a
            // cell resolve identically from either side of a chunk seam.
            int cellMinX = Mathf.FloorToInt(worldMinX / cellSize);
            int cellMinZ = Mathf.FloorToInt(worldMinZ / cellSize);
            int cellMaxX = Mathf.FloorToInt((worldMinX + worldWidth) / cellSize);
            int cellMaxZ = Mathf.FloorToInt((worldMinZ + worldDepth) / cellSize);

            // One ring of margin. Points in the neighbouring chunk's edge cells
            // are generated and inserted into the index but never emitted; they
            // exist purely so a point near our boundary is rejected against them
            // exactly as it would be if both chunks were solved together.
            const int Margin = 1;
            cellMinX -= Margin; cellMinZ -= Margin;
            cellMaxX += Margin; cellMaxZ += Margin;

            SpatialHash index = new SpatialHash(
                worldMinX - cellSize * (Margin + 1),
                worldMinZ - cellSize * (Margin + 1),
                worldWidth + cellSize * (Margin + 1) * 2f,
                worldDepth + cellSize * (Margin + 1) * 2f,
                cellSize);

            // Default table size. Random() masks its arguments to the table size,
            // so a smaller table would make world cells repeat their offsets on a
            // visible period once a world grew past a few thousand cells.
            Noise hash = new Noise(settings.seed);

            float worldMaxX = worldMinX + worldWidth;
            float worldMaxZ = worldMinZ + worldDepth;

            // Scanned-cell counter drives an in-row cancellation probe. The
            // per-row check below is already responsive once the runaway guard
            // has bounded a row to ~sqrt(maxPoints) cells, but a very large
            // maxPoints can still make a single row long, so also probe every
            // 4096 cells. The mask is cheap and the probe itself is only called
            // when a delegate was supplied.
            long cellsScanned = 0;
            int attempted = 0, spacingRejected = 0, densityRejected = 0, invalidDensity = 0; // UC-T06-05: no per-point diagnostic allocation.

            for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
            {
                if (isCancelled != null && isCancelled()) return results;
                if (results.Count >= maxPoints) break;

                for (int cx = cellMinX; cx <= cellMaxX; cx++)
                {
                    if (results.Count >= maxPoints) break;
                    if ((++cellsScanned & 4095L) == 0L &&
                        isCancelled != null && isCancelled()) return results;

                    for (int a = 0; a < attempts; a++)
                    {
                        // Three decorrelated hashes of the same world cell: two
                        // for the in-cell offset, one carried on the candidate
                        // for downstream scale and yaw. The arguments are swapped
                        // and offset by large primes so rx and rz do not track
                        // each other - feeding the same pair in the same order
                        // would place every point on the cell diagonal. Folding
                        // the attempt index in keeps a retry from landing exactly
                        // where the rejected candidate did.
                        float rx = hash.Random(cx + a * 7919, cz);
                        float rz = hash.Random(cz + a * 104729 + 31, cx + 15485863);
                        float rv = hash.Random(cx + 1299709, cz + a * 32452843);

                        float px = (cx + 0.5f + (rx - 0.5f) * jitter) * cellSize;
                        float pz = (cz + 0.5f + (rz - 0.5f) * jitter) * cellSize;

                        // Spacing is enforced over the margin ring too, so this
                        // runs before the footprint test rather than after.
                        bool inside = px >= worldMinX && px < worldMaxX &&
                                      pz >= worldMinZ && pz < worldMaxZ;
                        if (inside) attempted++;
                        if (index.HasNeighbourWithin(px, pz, spacing)) { if (inside) spacingRejected++; continue; }

                        // Margin points are indexed but never evaluated or
                        // emitted: the accept predicate samples this tile's
                        // matrices, which do not cover the neighbour's cells.
                        if (!inside)
                        {
                            index.Insert(px, pz);
                            break;
                        }

                        float weight = accept != null ? accept(px, pz) : 1f;
                        if (!Finite(weight)) { invalidDensity++; continue; } // UC-T06-05: invalid masks cannot bypass probability checks.
                        if (weight <= 0f) { densityRejected++; continue; }

                        // Fractional weight thins probabilistically. Using the
                        // same deterministic hash rather than a live RNG keeps
                        // the result stable across regenerations.
                        if (weight < 1f && rv > weight) { densityRejected++; continue; }

                        index.Insert(px, pz);

                        ScatterCandidate c;
                        c.worldX = px;
                        c.worldZ = pz;
                        c.height = 0f;
                        c.slope = 0f;
                        c.maskValue = weight;
                        c.random = rv;
                        results.Add(c);

                        // One accepted point per cell. Further attempts would
                        // land within spacing of it and be rejected anyway.
                        break;
                    }
                }
            }

            report?.Invoke(new PoissonSamplingReport(false, cellsScanned, attempted, spacingRejected, densityRejected, invalidDensity, 0, results.Count));
            return results;
        }

        // UC-T06-05: every cell competes against the same finite neighbourhood, including cells outside the demand rectangle.
        // A rival veto is independent of its own admission and of masks/budgets: two close points cannot both beat each other.
        // This is a sparse, non-maximal distribution. Geometry/quality are measured separately; it is not a promise of authored stem density.
        private static List<ScatterCandidate> SolveStable(PoissonSettings settings, float minX, float minZ,
            float width, float depth, Func<float, float, float> accept, Func<bool> cancelled, Action<PoissonSamplingReport> report)
        {
            if (!Finite(minX) || !Finite(minZ) || !Finite(width) || !Finite(depth) || width <= 0 || depth <= 0 ||
                !Finite(settings.minSpacing) || settings.minSpacing <= 0 || !Finite(settings.jitter))
                throw new ArgumentException("Stable scatter requires finite world bounds, positive dimensions/spacing and finite jitter.");
            float spacing = Mathf.Max(0.01f, settings.minSpacing), jitter = Mathf.Clamp01(settings.jitter);
            double maxX = (double)minX + width, maxZ = (double)minZ + depth;
            double firstX = Math.Floor((double)minX / spacing), firstZ = Math.Floor((double)minZ / spacing);
            double lastX = Math.Floor(maxX / spacing), lastZ = Math.Floor(maxZ / spacing);
            const int workLimit = 262144;
            // UC-T06-05: float positions must resolve sub-cell offsets. Beyond 2^21 cells,
            // rounding can collapse distant cells and invalidate the finite spacing halo.
            const double precisionCells = 2097152;
            if (Math.Max(Math.Max(Math.Abs(firstX), Math.Abs(firstZ)), Math.Max(Math.Abs(lastX), Math.Abs(lastZ))) > precisionCells)
                throw new InvalidOperationException("Stable scatter exceeds float world-position precision for this spacing. Rebase the world or increase authored spacing; no candidates were emitted.");
            if (firstX < int.MinValue + 2 || firstZ < int.MinValue + 2 || lastX > int.MaxValue - 2 || lastZ > int.MaxValue - 2 ||
                (lastX - firstX + 1) * (lastZ - firstZ + 1) > workLimit)
                throw new InvalidOperationException("Stable scatter exceeds its 262144-cell work limit or world-coordinate range. Increase authored spacing or request a smaller region; the candidate lattice was preserved.");
            int cap = settings.maxPoints > 0 ? Math.Min(settings.maxPoints, workLimit) : 100000;
            var kept = new List<ScatterCandidate>(Math.Min(cap, 1024));
            double spacing2 = (double)spacing * spacing;
            int firstCellX = (int)firstX, firstCellZ = (int)firstZ, lastCellX = (int)lastX, lastCellZ = (int)lastZ;
            long cellsVisited = 0;
            int attempted = 0, spacingRejected = 0, densityRejected = 0, invalidDensity = 0, accepted = 0;
            for (int z = firstCellZ; z <= lastCellZ; z++)
            {
                if (cancelled != null && cancelled()) return new List<ScatterCandidate>();
                for (int x = firstCellX; x <= lastCellX; x++)
                {
                    if (((x - firstCellX) & 255) == 0 && cancelled != null && cancelled()) return new List<ScatterCandidate>();
                    cellsVisited++;
                    ScatterCandidate candidate = StableCandidate(settings.seed, x, z, spacing, jitter);
                    if (candidate.worldX < minX || candidate.worldX >= maxX || candidate.worldZ < minZ || candidate.worldZ >= maxZ) continue;
                    attempted++;
                    uint priority = CellHash(settings.seed, x, z, 3);
                    bool admitted = true;
                    for (int dz = -2; dz <= 2 && admitted; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            uint rivalPriority = CellHash(settings.seed, x + dx, z + dz, 3);
                            if (rivalPriority > priority || (rivalPriority == priority && (dz > 0 || dz == 0 && dx > 0))) continue;
                            ScatterCandidate rival = StableCandidate(settings.seed, x + dx, z + dz, spacing, jitter);
                            double separationX = (double)candidate.worldX - rival.worldX, separationZ = (double)candidate.worldZ - rival.worldZ;
                            if (separationX * separationX + separationZ * separationZ < spacing2) { admitted = false; break; }
                        }
                    if (!admitted) { spacingRejected++; continue; }
                    float weight = accept != null ? accept(candidate.worldX, candidate.worldZ) : 1;
                    if (!Finite(weight)) { invalidDensity++; continue; }
                    if (weight <= 0 || candidate.random >= Mathf.Clamp01(weight)) { densityRejected++; continue; }
                    candidate.maskValue = Mathf.Clamp01(weight);
                    accepted++;
                    KeepRanked(kept, candidate, cap);
                }
            }
            kept.Sort((a, b) => a.worldZ != b.worldZ ? a.worldZ.CompareTo(b.worldZ) : a.worldX.CompareTo(b.worldX));
            report?.Invoke(new PoissonSamplingReport(true, cellsVisited, attempted, spacingRejected, densityRejected, invalidDensity, accepted - kept.Count, kept.Count));
            return kept;
        }

        private static ScatterCandidate StableCandidate(int seed, int x, int z, float cellSize, float jitter)
        {
            return new ScatterCandidate {
                worldX = (float)((x + 0.5 + (CellUnit(seed, x, z, 1) - 0.5) * jitter) * cellSize),
                worldZ = (float)((z + 0.5 + (CellUnit(seed, x, z, 2) - 0.5) * jitter) * cellSize),
                random = CellUnit(seed, x, z, 4)
            };
        }

        private static float CellUnit(int seed, int x, int z, uint stream) => (CellHash(seed, x, z, stream) >> 8) * (1f / 16777216f);

        // Pure integer avalanche; full world coordinates participate rather than a repeating noise-table address.
        private static uint CellHash(int seed, int x, int z, uint stream)
        {
            unchecked
            {
                uint value = (uint)x * 0x8da6b343U ^ (uint)z * 0xd8163841U ^ (uint)seed ^ stream * 0x9e3779b9U;
                value ^= value >> 16; value *= 0x7feb352dU;
                value ^= value >> 15; value *= 0x846ca68bU;
                return value ^ (value >> 16);
            }
        }

        // A bounded max heap keeps the best cap entries without retaining the entire candidate field.
        private static void KeepRanked(List<ScatterCandidate> heap, ScatterCandidate candidate, int cap)
        {
            if (heap.Count < cap)
            {
                int index = heap.Count; heap.Add(candidate);
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (CompareRank(heap[parent], candidate) >= 0) break;
                    heap[index] = heap[parent]; index = parent;
                }
                heap[index] = candidate;
            }
            else if (CompareRank(candidate, heap[0]) < 0)
            {
                int index = 0;
                while (index * 2 + 1 < heap.Count)
                {
                    int child = index * 2 + 1;
                    if (child + 1 < heap.Count && CompareRank(heap[child + 1], heap[child]) > 0) child++;
                    if (CompareRank(candidate, heap[child]) >= 0) break;
                    heap[index] = heap[child]; index = child;
                }
                heap[index] = candidate;
            }
        }

        private static int CompareRank(ScatterCandidate a, ScatterCandidate b)
        {
            int rank = a.random.CompareTo(b.random);
            if (rank != 0) return rank;
            rank = a.worldZ.CompareTo(b.worldZ);
            return rank != 0 ? rank : a.worldX.CompareTo(b.worldX);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// Converts a density in instances per square kilometre into the minimum
        /// spacing that yields it. Scatter nodes expose density because that is
        /// what an artist reasons in; the solver needs a distance.
        /// </summary>
        /// <remarks>
        /// A hexagonal packing of discs of radius r/2 covers sqrt(3)/2 of the
        /// plane, giving area per point of (sqrt(3)/2) * r^2. Inverting that for
        /// r is the closest closed form to the density a jittered grid actually
        /// achieves.
        /// </remarks>
        public static float SpacingForDensity(float instancesPerSqKm)
        {
            if (instancesPerSqKm <= 0f) return float.MaxValue;

            const float SqKmInSqM = 1000000f;
            float areaPerPoint = SqKmInSqM / instancesPerSqKm;

            // 1.1547 = 2/sqrt(3)
            return Mathf.Sqrt(areaPerPoint * 1.1547f);
        }

        /// <summary>
        /// Biases an existing candidate set toward clumps, for groves and thickets.
        /// Points in low-density neighbourhoods are dropped, which leaves the
        /// clusters that were already there and clears the ground between them.
        /// </summary>
        /// <param name="candidates">Set to thin. Not modified.</param>
        /// <param name="clusterRadius">Neighbourhood radius, world metres.</param>
        /// <param name="strength">
        /// 0 returns the input unchanged; 1 keeps only points whose local density
        /// is at or above the set average. Stable world-field mode instead thins by a world-space clump field.
        /// </param>
        /// <param name="isCancelled">Optional worker cancellation probe.</param>
        /// <param name="worldSeed">World/node seed for stable world-field mode; ignored by the legacy neighbourhood mode.</param>
        /// <param name="stableWorldField">Use an absolute world clump field independent of region bounds and candidate caps.</param>
        /// <returns>Thinned candidates without moving points; null/empty input and disabled bias retain the input.</returns>
        /// <exception cref="ArgumentException">Stable mode receives a nonfinite radius/strength.</exception>
        public static List<ScatterCandidate> ApplyClusterBias(
            List<ScatterCandidate> candidates, float clusterRadius, float strength,
            Func<bool> isCancelled = null, int worldSeed = 0, bool stableWorldField = false)
        {
            if (candidates == null || candidates.Count == 0) return candidates;
            if (stableWorldField && (!Finite(clusterRadius) || !Finite(strength)))
                throw new ArgumentException("Stable cluster bias requires a finite world-metre radius and strength.");

            float t = Mathf.Clamp01(strength);
            if (t <= 0.001f) return candidates;
            if (clusterRadius <= 0f) return candidates;
            if (stableWorldField)
            {
                // UC-T06-05: reuse the same world-noise clustering model as shrub clumping; no tile-average feedback or spatial index is needed.
                var noise = new Noise(unchecked(worldSeed + 7717));
                var stable = new List<ScatterCandidate>();
                float scale = 1f / Mathf.Max(0.01f, clusterRadius);
                foreach (var candidate in candidates)
                {
                    if (isCancelled != null && isCancelled()) return new List<ScatterCandidate>();
                    float clump = Mathf.Clamp01(noise.Perlin(candidate.worldX * scale, candidate.worldZ * scale) * 0.5f + 0.5f);
                    if (candidate.random < Mathf.Lerp(1, clump, t)) stable.Add(candidate);
                }
                return stable;
            }

            // Index bounds come from the candidates themselves; a scatter set is
            // often a small island inside a much larger tile, and sizing the grid
            // to the tile would leave nearly every cell empty.
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                ScatterCandidate c = candidates[i];
                if (c.worldX < minX) minX = c.worldX;
                if (c.worldX > maxX) maxX = c.worldX;
                if (c.worldZ < minZ) minZ = c.worldZ;
                if (c.worldZ > maxZ) maxZ = c.worldZ;
            }

            SpatialHash index = new SpatialHash(
                minX - clusterRadius, minZ - clusterRadius,
                (maxX - minX) + clusterRadius * 2f,
                (maxZ - minZ) + clusterRadius * 2f,
                clusterRadius);

            for (int i = 0; i < candidates.Count; i++)
                index.Insert(candidates[i].worldX, candidates[i].worldZ);

            int[] localCount = new int[candidates.Count];
            float total = 0f;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (isCancelled != null && isCancelled()) return candidates;

                localCount[i] = index.CountWithin(candidates[i].worldX, candidates[i].worldZ, clusterRadius);
                total += localCount[i];
            }

            float average = candidates.Count > 0 ? total / candidates.Count : 0f;
            if (average <= 0f) return candidates;

            List<ScatterCandidate> kept = new List<ScatterCandidate>(candidates.Count);

            for (int i = 0; i < candidates.Count; i++)
            {
                // Ratio of local density to the set average, remapped so a point
                // exactly at average survives at strength 1 and sparser points
                // fall off from there.
                float ratio = localCount[i] / average;
                float keepChance = Mathf.Lerp(1f, Mathf.Clamp01(ratio), t);

                if (candidates[i].random <= keepChance) kept.Add(candidates[i]);
            }

            return kept;
        }
    }
}
