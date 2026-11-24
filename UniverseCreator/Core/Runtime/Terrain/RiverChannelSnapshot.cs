// UC-T03-09: accepted ground is frozen once per cell; profiles read it without a second terrain solve.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Result of a world-scoped accepted-channel query; failure never supplies another world's profile.</summary>
    public enum RiverChannelValidity
    {
        /// <summary>A reconciled profile is available; SignedBankDistanceMetres determines containment.</summary>
        Valid,
        /// <summary>The query contains a null world, nonfinite coordinate or invalid radius.</summary>
        InvalidInput,
        /// <summary>This world has no accepted channel owners.</summary>
        NoCommittedWorld,
        /// <summary>No accepted cell covers the query point.</summary>
        OutsideCoverage,
        /// <summary>The covering cell has no channel within the bounded search.</summary>
        NoChannel,
        /// <summary>Accepted centre or bank ground is unavailable, including a terrain hole or uncommitted adjacent cell.</summary>
        MissingGround,
        /// <summary>The final terrain no longer contains the candidate excavation or positive bank relief.</summary>
        NoTrench,
        /// <summary>A captured lake source surface is below the final bed or above a sampled bank; no uphill fill is invented.</summary>
        OutletLevelMismatch
    }

    /// <summary>Immutable cross-section of accepted ground. All positions, heights, widths and distances use world metres;
    /// surface is a conservative 75%-full terrain-derived candidate, not a spawned water mesh or hydraulic certification.</summary>
    public readonly struct RiverChannelSample
    {
        /// <summary>World/run that committed the sampled terrain.</summary>
        public readonly GenerationRunId Run;
        /// <summary>Unique accepted ground commit; changes after seam or authored re-cut updates within the same generation run.</summary>
        public readonly Guid GroundVersion;
        /// <summary>Producer's stable directed reach identity.</summary>
        public readonly string ReachId;
        /// <summary>Accepted bed centre, and bank positions at the nominal candidate half-width.</summary>
        public readonly Vector3 Bed, LeftBank, RightBank;
        /// <summary>Unit downstream XZ tangent of the candidate station segment.</summary>
        public readonly Vector2 DirectionXZ;
        /// <summary>Nominal candidate channel half-width in metres; final bank relief is measured separately.</summary>
        public readonly float HalfWidthMetres;
        /// <summary>Terrain-derived water level, bounded below both sampled bank heights.</summary>
        public readonly float WaterSurfaceY;
        /// <summary>Signed distance to the nominal bank in metres; nonpositive means inside coverage.</summary>
        public readonly float SignedBankDistanceMetres;
        /// <summary>Available water depth above the accepted bed in metres.</summary>
        public float DepthMetres => Mathf.Max(0, WaterSurfaceY - Bed.y);
        internal RiverChannelSample(GenerationRunId run, Guid groundVersion, string reach, Vector3 bed, Vector3 left, Vector3 right,
            Vector2 direction, float width, float surface, float distance)
        { Run = run; GroundVersion = groundVersion; ReachId = reach; Bed = bed; LeftBank = left; RightBank = right; DirectionXZ = direction;
          HalfWidthMetres = width; WaterSurfaceY = surface; SignedBankDistanceMetres = distance; }
    }

    /// <summary>Versioned runtime-only river profiles over a frozen final chunk heightmap. Graph candidate arrays and topology
    /// remain borrowed immutable data; the commit owns this ground copy and its budget pin until replacement or unload.</summary>
    public sealed class RiverChannelSnapshot
    {
        /// <summary>Snapshot schema version.</summary>
        public const int SchemaVersion = 1;
        /// <summary>Accepted world/run identity; runtime-only and rebuilt after reload.</summary>
        public GenerationRunId Run { get; }
        /// <summary>Unique accepted native-ground commit, including edits within the same generation run.</summary>
        public Guid GroundVersion { get; } = Guid.NewGuid(); // UC-T03-10: a re-cut cannot masquerade as its earlier terrain.
        /// <summary>Accepted cell publication owner.</summary>
        public string OwnerId { get; }
        /// <summary>Exact final-ground XZ bounds in world metres, including edge samples.</summary>
        public Rect Bounds { get; }
        /// <summary>Candidate directed topologies captured by this cell; final sections come from committed ground.</summary>
        public IReadOnlyList<RiverOutletTopology> Topologies { get; }
        private readonly float[,] heights;
        private readonly float baseY, heightScale;
        private readonly bool[,] surface; // UC-T03-10: true = native surface, false = hole; the commit transfers ownership.
        private readonly float[,] trenchRelief; // UC-T03-10: normalized surviving cut; guards can raise both reference and bed.
        private readonly Matrix biomeClassification; // UC-T07-03: private accepted label copy; never interpolate its numeric enum encoding.
        internal readonly ChannelGraphPublication[] Channels;

        internal RiverChannelSnapshot(GenerationRunId run, string owner, Rect bounds, ChannelGraphPublication[] channels,
            float[,] finalHeights, float originY, float scale, bool[,] finalSurface = null, float[,] survivingRelief = null, Matrix biomes = null)
        {
            if (!run.IsAssigned || string.IsNullOrEmpty(owner) || channels == null ||
                !Finite(bounds.x) || !Finite(bounds.y) || !Finite(bounds.width) || !Finite(bounds.height) || bounds.width <= 0 || bounds.height <= 0 ||
                !Finite(originY) || !Finite(scale) || scale <= 0 ||
                (finalHeights != null && (finalHeights.GetLength(0) < 2 || finalHeights.GetLength(1) < 2)))
                throw new ArgumentException("Invalid accepted river ground geometry.");
            if ((finalSurface != null && (finalSurface.GetLength(0) == 0 || finalSurface.GetLength(1) == 0)) ||
                (survivingRelief != null && (finalHeights == null || survivingRelief.GetLength(0) != finalHeights.GetLength(0) ||
                survivingRelief.GetLength(1) != finalHeights.GetLength(1)))) throw new ArgumentException("Invalid accepted relief/surface dimensions."); // UC-T03-10.
            Run = run; OwnerId = owner; Bounds = bounds; Channels = channels; baseY = originY; heightScale = scale;
            // UC-T03-09: clone only after final shaping and native upload; a cached graph must never alias native ground.
            heights = finalHeights == null ? null : (float[,])finalHeights.Clone();
            surface = finalSurface;
            trenchRelief = survivingRelief == null ? null : (float[,])survivingRelief.Clone();
            biomeClassification = biomes == null ? null : new Matrix(biomes); // UC-T07-03: final consumers cannot mutate or outlive the source tile's label storage.
            if (heights != null) foreach (float value in heights)
                if (!Finite(value)) throw new ArgumentException("Accepted river ground must be finite.");
            var topology = new List<RiverOutletTopology>();
            foreach (var channel in channels) if (channel.Outlets != null) topology.Add(channel.Outlets);
            Topologies = topology.AsReadOnly();
        }

        internal RiverChannelSnapshot WithGround(float[,] finalHeights, bool[,] finalSurface)
            => new RiverChannelSnapshot(Run, OwnerId, Bounds, Channels, finalHeights, baseY, heightScale, finalSurface, trenchRelief,
                biomeClassification); // UC-T07-03: native ground edits preserve the accepted categorical provenance.

        internal bool TryBiomeMembership(float x, float z, int biome, out float weight)
        {
            weight = 0;
            if (biomeClassification == null || !Covers(x, z)) return false;
            weight = BiomeMembershipSampling.SampleWorld(biomeClassification, x, z, biome); return true;
        }

        internal bool Covers(float x, float z) => x >= Bounds.xMin && x <= Bounds.xMax && z >= Bounds.yMin && z <= Bounds.yMax;
        internal bool TryGround(float x, float z, out float y)
        {
            y = 0;
            if (heights == null || !Covers(x, z)) return false;
            float fx = (x - Bounds.xMin) / Bounds.width * (heights.GetLength(1) - 1);
            float fz = (z - Bounds.yMin) / Bounds.height * (heights.GetLength(0) - 1);
            if (surface != null)
            {
                int sx = Mathf.Min(surface.GetLength(1) - 1, Mathf.FloorToInt((x - Bounds.xMin) / Bounds.width * surface.GetLength(1)));
                int sz = Mathf.Min(surface.GetLength(0) - 1, Mathf.FloorToInt((z - Bounds.yMin) / Bounds.height * surface.GetLength(0)));
                if (!surface[sz, sx]) return false; // UC-T03-10: a removed native quad cannot be sampled as valid ground.
            }
            int ix = Mathf.Min(Mathf.FloorToInt(fx), heights.GetLength(1) - 2), iz = Mathf.Min(Mathf.FloorToInt(fz), heights.GetLength(0) - 2);
            float low = Mathf.Lerp(heights[iz, ix], heights[iz, ix + 1], fx - ix);
            float high = Mathf.Lerp(heights[iz + 1, ix], heights[iz + 1, ix + 1], fx - ix);
            y = baseY + Mathf.Lerp(low, high, fz - iz) * heightScale;
            return Finite(y);
        }

        internal bool TryRelief(float x, float z, out float metres)
        {
            metres = 0;
            if (trenchRelief == null || !Covers(x, z)) return false;
            float fx = (x - Bounds.xMin) / Bounds.width * (trenchRelief.GetLength(1) - 1);
            float fz = (z - Bounds.yMin) / Bounds.height * (trenchRelief.GetLength(0) - 1);
            int ix = Mathf.Min(Mathf.FloorToInt(fx), trenchRelief.GetLength(1) - 2), iz = Mathf.Min(Mathf.FloorToInt(fz), trenchRelief.GetLength(0) - 2);
            metres = Mathf.Lerp(Mathf.Lerp(trenchRelief[iz, ix], trenchRelief[iz, ix + 1], fx - ix),
                Mathf.Lerp(trenchRelief[iz + 1, ix], trenchRelief[iz + 1, ix + 1], fx - ix), fz - iz) * heightScale;
            return true;
        }

        // UC-T05-06: physical normals use accepted native spacing, with one-sided derivatives at borders and holes.
        internal Vector3 GroundNormal(float x, float z)
        {
            if (heights == null || !TryGround(x, z, out float centre)) return Vector3.up;
            float dx = Bounds.width / (heights.GetLength(1) - 1), dz = Bounds.height / (heights.GetLength(0) - 1);
            float leftX = Mathf.Max(Bounds.xMin, x - dx), rightX = Mathf.Min(Bounds.xMax, x + dx);
            float downZ = Mathf.Max(Bounds.yMin, z - dz), upZ = Mathf.Min(Bounds.yMax, z + dz);
            if (!TryGround(leftX, z, out float left)) { leftX = x; left = centre; }
            if (!TryGround(rightX, z, out float right)) { rightX = x; right = centre; }
            if (!TryGround(x, downZ, out float down)) { downZ = z; down = centre; }
            if (!TryGround(x, upZ, out float up)) { upZ = z; up = centre; }
            float slopeX = rightX > leftX ? (right - left) / (rightX - leftX) : 0;
            float slopeZ = upZ > downZ ? (up - down) / (upZ - downZ) : 0;
            return new Vector3(-slopeX, 1, -slopeZ).normalized;
        }

        // UC-T03-09: one bounded 13x13 station search per publication, with segment projection instead of a global nearest channel.
        internal bool FindProfile(float x, float z, float radius, out Candidate best)
        {
            best = default; double bestDistance = double.PositiveInfinity;
            foreach (var channel in Channels)
            {
                int cx = Mathf.RoundToInt((x - channel.OriginX) / channel.CellX), cz = Mathf.RoundToInt((z - channel.OriginZ) / channel.CellZ);
                for (int dz = -6; dz <= 6; dz++) for (int dx = -6; dx <= 6; dx++)
                {
                    int ix = cx + dx, iz = cz + dz;
                    if (ix < 0 || ix >= channel.ResolutionX || iz < 0 || iz >= channel.ResolutionZ) continue;
                    int c = iz * channel.ResolutionX + ix;
                    if (channel.HalfWidth[c] <= 0 || channel.Depth[c] <= 0 || channel.ReferenceGround == null) continue;
                    int next = channel.Downstream == null ? -1 : channel.Downstream[c];
                    if (next < 0 || next >= channel.HalfWidth.Length || channel.HalfWidth[next] <= 0) next = c;
                    Vector2 a = new Vector2(channel.OriginX + ix * channel.CellX, channel.OriginZ + iz * channel.CellZ);
                    Vector2 b = new Vector2(channel.OriginX + next % channel.ResolutionX * channel.CellX,
                        channel.OriginZ + next / channel.ResolutionX * channel.CellZ);
                    Vector2 delta = b - a, point = new Vector2(x, z);
                    float t = delta.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(point - a, delta) / delta.sqrMagnitude) : 0;
                    Vector2 centre = a + delta * t;
                    float distance = Vector2.Distance(point, centre), width = Mathf.Lerp(channel.HalfWidth[c], channel.HalfWidth[next], t);
                    double bankDistance = Math.Max(0, (double)distance - width);
                    if (bankDistance > radius || distance >= bestDistance) continue;
                    float reference = channel.ReferenceOriginY + Mathf.Lerp(channel.ReferenceGround[c], channel.ReferenceGround[next], t) * channel.ReferenceHeight;
                    if (!Finite(reference)) continue;
                    bestDistance = distance;
                    best = new Candidate { Centre = centre, Direction = delta.sqrMagnitude > 1e-6f ? delta.normalized : Vector2.up,
                        Width = width, Depth = Mathf.Lerp(channel.Depth[c], channel.Depth[next], t), ReferenceY = reference,
                        FixedSurface = channel.FixedSurface != null && t <= 1e-5f ? channel.FixedSurface[c] : float.NaN,
                        SignedDistance = distance - width, Reach = channel.ReachIds == null ? channel.Key + ":" + c : channel.ReachIds[c] };
                }
            }
            return bestDistance < double.PositiveInfinity;
        }

        internal struct Candidate
        {
            internal Vector2 Centre, Direction;
            internal float Width, Depth, ReferenceY, SignedDistance, FixedSurface;
            internal string Reach;
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
