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
        /// <param name="isCancelled">
        /// Optional cancellation probe, checked once per grid row. Returning true
        /// stops the solve and returns whatever was accepted so far.
        /// </param>
        public static List<ScatterCandidate> Solve(
            PoissonSettings settings,
            float worldMinX, float worldMinZ,
            float worldWidth, float worldDepth,
            Func<float, float, float> accept,
            Func<bool> isCancelled = null)
        {
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

            for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
            {
                if (isCancelled != null && isCancelled()) return results;
                if (results.Count >= maxPoints) break;

                for (int cx = cellMinX; cx <= cellMaxX; cx++)
                {
                    if (results.Count >= maxPoints) break;

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
                        if (index.HasNeighbourWithin(px, pz, spacing)) continue;

                        bool inside = px >= worldMinX && px < worldMaxX &&
                                      pz >= worldMinZ && pz < worldMaxZ;

                        // Margin points are indexed but never evaluated or
                        // emitted: the accept predicate samples this tile's
                        // matrices, which do not cover the neighbour's cells.
                        if (!inside)
                        {
                            index.Insert(px, pz);
                            break;
                        }

                        float weight = accept != null ? accept(px, pz) : 1f;
                        if (weight <= 0f) continue;

                        // Fractional weight thins probabilistically. Using the
                        // same deterministic hash rather than a live RNG keeps
                        // the result stable across regenerations.
                        if (weight < 1f && rv > weight) continue;

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

            return results;
        }

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
        /// is at or above the set average.
        /// </param>
        public static List<ScatterCandidate> ApplyClusterBias(
            List<ScatterCandidate> candidates, float clusterRadius, float strength,
            Func<bool> isCancelled = null)
        {
            if (candidates == null || candidates.Count == 0) return candidates;

            float t = Mathf.Clamp01(strength);
            if (t <= 0.001f) return candidates;
            if (clusterRadius <= 0f) return candidates;

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
