// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//
//  File:    HydroField.cs
//  Purpose: Ecology's FIRST real water awareness. Turns the whole-world drainage
//           solve the terrain already runs into the four water facts a species
//           needs at any point: how far the nearest water is, how far the nearest
//           SALT water is, the water surface elevation over it, and whether that
//           water is ocean or lake. Before this, ctx.water (WaterProbe[]) was never
//           filled by any builder, so every site read "distance = search radius,
//           submerged = 0" - a forest that could not tell a shore from an interior.
//
//  WHY WHOLE-WORLD, WHY SHARED
//  ---------------------------
//  "Where is the nearest water" is a map-sized question exactly like the sea-level
//  floor's "is this the ocean": a chunk cannot see a coast that begins two chunks
//  away. So it is answered ONCE, on the same coarse whole-world drainage solve the
//  terrain post-pass uses (WorldDrainage), and read back per chunk pixel. The one
//  derived field per solve is memoised against the WorldDrainage instance, and
//  because tile.drainageCache hands every chunk of a bake the SAME solve instance
//  for a fixed call site, that memo means one derivation per bake, shared across
//  all its chunks - no seam, no per-chunk restart.
//
//  ONE DEFINITION OF THE SHORE
//  ---------------------------
//  The ocean is classified by SeaLevelFloor.FloodOceanFromEdges and the coastline
//  distance is the SAME chamfer transform (CoastalShelf.ChamferDistanceToOcean)
//  the coastal shelf grades on. Both are reused, not re-implemented, so terrain and
//  ecology can never drift about where the water is.
//
//  Immutable after construction; wd is immutable and the arrays are never written
//  again, so any number of scatter workers may sample one instance at once.
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Per-point water awareness derived once from a whole-world drainage solve:
    /// distance to any water, distance to salt water, water surface elevation, and
    /// the nearest body's kind. Read at chunk resolution through the solve it wraps.
    /// </summary>
    public sealed class HydroField
    {
        // One field per whole-world solve, shared across a bake's chunks. Keyed by the
        // solve instance because drainageCache hands the same instance to every chunk.
        private static readonly ConditionalWeakTable<WorldDrainage, HydroField> Cache
            = new ConditionalWeakTable<WorldDrainage, HydroField>();

        // Normalised depression depth above which a non-ocean pit is a lake rather than
        // a dry dip. Same meaning as SeaLevelFloor's basin test (default 0.01), so the
        // terrain and ecology agree on what pools water: the floor lifts dry dips, and
        // what survives as a real basin is exactly what ecology reads here as a lake.
        private const float LakeBasinDepthNorm = 0.01f;

        private readonly WorldDrainage wd;
        private readonly float[] distAnyM;   // metres to nearest ocean OR lake
        private readonly float[] distSaltM;  // metres to nearest ocean (the salt source)
        private readonly float[] surfaceY;   // world metres: water surface, else own ground
        private readonly byte[]  kind;       // 0 none, 1 ocean, 2 lake
        private readonly float[] flow01;     // 0..1 bed-flow / scour proxy, null when no flow solve
        private readonly bool    hasAnyWater;
        private readonly TerrainSystem.GenerationRunId committedRun; // UC-T03-09: a derived field never reads a foreign or older channel revision.

        /// <summary>False when the whole world is dry: callers then skip the water branch.</summary>
        public bool HasAnyWater { get { return hasAnyWater || TerrainSystem.RiverChannelRegistry.HasCommittedChannels(committedRun); } }

        private HydroField(WorldDrainage solve, float[] distAnyM, float[] distSaltM,
                           float[] surfaceY, byte[] kind, float[] flow01, bool hasAnyWater)
        {
            this.wd = solve;
            this.distAnyM = distAnyM;
            this.distSaltM = distSaltM;
            this.surfaceY = surfaceY;
            this.kind = kind;
            this.flow01 = flow01;
            this.hasAnyWater = hasAnyWater;
        }

        /// <summary>
        /// The field for a solve, derived on first request and memoised against it.
        /// <paramref name="seaLevelY"/> is the ecology sea plane in world metres (the
        /// same one the sampler measures altitude from), so ocean and altitude never
        /// disagree about where the waterline is. Null when the solve is unusable.
        /// </summary>
        public static HydroField For(WorldDrainage solve, float seaLevelY)
        {
            if (solve == null || solve.height == null || solve.height.arr == null || solve.filled == null)
                return null;

            // GetValue may run the factory more than once under contention, but the
            // derivation is pure and one result is stored - the loser's is identical.
            return Cache.GetValue(solve, key => Derive(key, seaLevelY));
        }

        /// <summary>UC-T03-09: attaches exact accepted river context to the existing drainage-derived ocean/lake field.</summary>
        /// <param name="solve">Captured whole-world drainage solve; null/unusable returns null.</param>
        /// <param name="seaLevelY">Finite sea level in world metres, as for the original overload.</param>
        /// <param name="run">Owning captured world/revision; unassigned returns the original field. Pending or withdrawn channels are absent.</param>
        /// <returns>A small immutable wrapper sharing existing ocean/lake arrays; river samples use the matching accepted commit.</returns>
        public static HydroField For(WorldDrainage solve, float seaLevelY, TerrainSystem.GenerationRunId run)
        {
            HydroField basis = For(solve, seaLevelY);
            return basis == null || !run.IsAssigned ? basis : new HydroField(basis, run);
        }

        private HydroField(HydroField basis, TerrainSystem.GenerationRunId run)
            : this(basis.wd, basis.distAnyM, basis.distSaltM, basis.surfaceY, basis.kind, basis.flow01, basis.hasAnyWater)
        { committedRun = run; }

        /// <summary>UC-T03-09: reads accepted river kind/bed/surface/bank distance/reach identity from the same provider used by scene water.</summary>
        /// <param name="worldX">Finite world X in metres.</param>
        /// <param name="worldZ">Finite world Z in metres.</param>
        /// <param name="sample">Exact matching accepted river profile, or default on absence.</param>
        /// <returns>True for a nearby reconciled profile within the bounded station search; bank sign determines containment.</returns>
        public bool TryRiverContext(float worldX, float worldZ, out TerrainSystem.RiverChannelSample sample)
        {
            return TerrainSystem.RiverChannelRegistry.TrySampleCommitted(committedRun.WorldId, worldX, worldZ,
                Mathf.Max(wd.height.worldSize.x, wd.height.worldSize.z), committedRun.Revision, out sample, out _);
        }

        /// <summary>UC-T03-10: samples this field's exact accepted terrain revision without another drainage solve.</summary>
        /// <param name="worldX">Finite world X in metres.</param>
        /// <param name="worldZ">Finite world Z in metres.</param>
        /// <param name="heightMetres">Final world height in metres, or zero when pending, unloaded, outside coverage or a hole.</param>
        /// <returns>True only for matching accepted ground; unowned fields retain their original drainage-only behavior.</returns>
        public bool TryAcceptedGround(float worldX, float worldZ, out float heightMetres)
        {
            heightMetres = 0;
            return committedRun.IsAssigned && TerrainSystem.RiverChannelRegistry.TrySampleGround(committedRun.WorldId,
                worldX, worldZ, committedRun.Revision, out heightMetres, out _);
        }

        private static HydroField Derive(WorldDrainage wd, float seaLevelY)
        {
            int resX = wd.resX, resZ = wd.resZ;
            int count = resX * resZ;
            float[] h = wd.height.arr;
            float[] filled = wd.filled;

            float baseY = wd.height.worldPos.y;
            float scale = wd.height.worldHeight;

            // Sea in the solve's normalised height space, from the ecology sea plane.
            float seaNorm = scale > 1e-4f ? Mathf.Clamp01((seaLevelY - baseY) / scale) : 0f;
            float seaSurfaceY = baseY + seaNorm * scale;

            bool[] ocean = SeaLevelFloor.FloodOceanFromEdges(h, resX, resZ, seaNorm);

            bool[] water    = new bool[count];
            byte[] kind     = new byte[count];
            float[] surface = new float[count];
            bool any = false;

            for (int i = 0; i < count; i++)
            {
                float g = h[i];
                if (ocean[i])
                {
                    water[i] = true;
                    kind[i] = 1;
                    surface[i] = seaSurfaceY;         // flat sea plane
                    any = true;
                }
                else if ((filled[i] - g) > LakeBasinDepthNorm)
                {
                    water[i] = true;
                    kind[i] = 2;
                    surface[i] = baseY + filled[i] * scale;  // pooled lake level
                    any = true;
                }
                else
                {
                    kind[i] = 0;
                    // Land carries its OWN ground height so a Minimum read at the shore
                    // keeps the water level rather than ramping it up the bank.
                    surface[i] = baseY + g * scale;
                }
            }

            Vector2 pix = wd.height.PixelSize;
            float mppX = Mathf.Max(1e-3f, pix.x);
            float mppZ = Mathf.Max(1e-3f, pix.y);

            // Cap the chamfer at the world's own reach so a distance is meaningful
            // everywhere, not clipped to a coastal band (this is not the shelf).
            float cap = Mathf.Max(resX * mppX, resZ * mppZ);

            float[] distAnyM  = CoastalShelf.ChamferDistanceToOcean(water, resX, resZ, mppX, mppZ, cap);
            float[] distSaltM = CoastalShelf.ChamferDistanceToOcean(ocean, resX, resZ, mppX, mppZ, cap);

            // Bed-flow / scour proxy from the drainage flow-accumulation field the solve
            // already carries (null when the flow pass was skipped, e.g. filled-only or
            // the two-argument test seam - then there is simply no flow to read). Cells
            // count upstream contributing area, which spans orders of magnitude between a
            // ridge and a trunk channel, so a log transform normalised by the world's own
            // maximum spreads that into a 0..1 energy: ~0 on still hillslopes and lake
            // beds, climbing to ~1 down a main channel where the current scours to rock.
            float[] flow01 = null;
            if (wd.accum != null && wd.accum.Length == count)
            {
                float maxAccum = 0f;
                for (int i = 0; i < count; i++)
                    if (wd.accum[i] > maxAccum) maxAccum = wd.accum[i];

                float denom = maxAccum > 0f ? Mathf.Log(1f + maxAccum) : 1f;
                flow01 = new float[count];
                for (int i = 0; i < count; i++)
                    flow01[i] = Mathf.Clamp01(Mathf.Log(1f + Mathf.Max(0f, wd.accum[i])) / denom);
            }

            return new HydroField(wd, distAnyM, distSaltM, surface, kind, flow01, any);
        }

        /// <summary>Metres to the nearest water body edge (ocean or lake) at a world XZ.</summary>
        public float DistanceToWaterM(float worldX, float worldZ)
        {
            float distance = wd.Sample(distAnyM, worldX, worldZ, WorldRead.Bilinear);
            return TryRiverContext(worldX, worldZ, out var river) ? Mathf.Min(distance, Mathf.Max(0, river.SignedBankDistanceMetres)) : distance;
        }

        /// <summary>Metres to the nearest SALT body (ocean) - the source of spray inland.</summary>
        public float DistanceToSaltWaterM(float worldX, float worldZ)
        {
            return wd.Sample(distSaltM, worldX, worldZ, WorldRead.Bilinear);
        }

        /// <summary>
        /// Water surface elevation in world metres at a world XZ. Minimum read: inside a
        /// body all four corners hold the surface so it is flat; at the rim the land
        /// corners sit higher, so the lowest keeps the true spill level (WorldRead docs).
        /// </summary>
        public float WaterSurfaceY(float worldX, float worldZ)
        {
            if (TryRiverContext(worldX, worldZ, out var river) && Mathf.Max(0, river.SignedBankDistanceMetres) <= wd.Sample(distAnyM, worldX, worldZ, WorldRead.Bilinear))
                return river.WaterSurfaceY; // UC-T03-09: identical accepted level for water and environment consumers.
            return wd.Sample(surfaceY, worldX, worldZ, WorldRead.Minimum);
        }

        /// <summary>
        /// Kind of the nearest body. Nearest-cell: a kind cannot be averaged. Only
        /// meaningful where a body is actually near - callers gate on distance first.
        /// </summary>
        public WaterBodyKind NearestKind(float worldX, float worldZ)
        {
            byte k = kind[wd.NearestCell(worldX, worldZ)];
            if (TryRiverContext(worldX, worldZ, out var river) && Mathf.Max(0, river.SignedBankDistanceMetres) <= wd.Sample(distAnyM, worldX, worldZ, WorldRead.Bilinear))
                return WaterBodyKind.River; // UC-T03-09: river context has a distinct category.
            return k == 1 ? WaterBodyKind.Ocean : WaterBodyKind.Lake;
        }

        /// <summary>
        /// The bed-flow / scour energy at a world XZ, 0 (still) to 1 (a scouring trunk
        /// channel), the flow reading W1 deferred. Zero everywhere when the solve
        /// carried no flow-accumulation field, so a caller that has no flow simply sees
        /// a calm bed rather than an error. Bilinear: energy grades smoothly, unlike a
        /// body kind. This is the current a submerged bed feels, which the ecology
        /// substrate turns into scoured rock (HydroExclusion) exactly as the underwater
        /// texturing does - so where the bed reads as rock, plants see rock too.
        /// </summary>
        public float BedFlow01(float worldX, float worldZ)
        {
            if (flow01 == null) return 0f;
            return wd.Sample(flow01, worldX, worldZ, WorldRead.Bilinear);
        }
    }
}
