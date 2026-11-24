// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HeightOutputNode.cs
//  Purpose: Writes the height matrix into OutputBag.heightmap for Unity's
//           TerrainData.SetHeights. The canonical output node every working
//           graph must have.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class HeightOutputNode : OutputNode, IPreparedGraphInputs, IGenerationSnapshotInputs
    {
        public override string NodeName { get { return "Height Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Write the height matrix to TerrainData."; } }

        public float heightScale = 600f;

        // -- Core sea-level floor -------------------------------------------------
        // Baked into the output writer rather than offered as a node, so it runs for
        // every world no matter how the graph is wired - and because it floors the
        // COMPOSED heightmap, it binds ALL base noise creators AND every combiner at
        // once. It lifts dry land that has fallen below sea level back ABOVE it (killing
        // the "continent below the sea" and river-levee-wall/dam-bank artifacts), while
        // leaving genuine ocean and closed lake basins alone so the sea keeps its depth.
        // seaFreeboardMetres raises lifted dry land STRICTLY above the waterline, never onto
        // it, by a real-world SAFETY margin (metres). It is a guard, not the shape: the
        // natural terrain math still decides where land eases down to the sea, but no dry
        // ground - and above all no flat plain - is ever left sitting AT or below the
        // waterline; a lifted dip clears the sea by at least this many metres. Expressed in
        // metres and converted through heightScale so the 20 m guarantee holds at any world
        // height. See SeaLevelFloor.cs for the ocean/basin classification it never touches.
        [System.NonSerialized] public bool enforceSeaLevelFloor = true;
        public float seaLevel = 0.35f;         // normalised waterline (system default)
        public float coastGrade = 0.03f;       // height band a lifted cell eases back over
        public float basinDepth = 0.01f;       // pooled depth above which a dip is a real basin
        public float seaFreeboardMetres = 20f; // METRES of clearance lifted dry land keeps above sea (safety guard)
        public float floorStrength = 1f;       // 0..1 blend of the lift

        // CONTINENTAL RISE (always on). SeaLevelFloor only lifts land that fell BELOW sea,
        // so a plain sitting exactly AT sea level is legal by that pass - and fatal to a
        // river, which then has no head to descend through and reads as "climbing". This
        // obliges the interior to stand above sea by an amount that GROWS with distance
        // from the ocean, so every inland cell has a downhill route to the sea by
        // construction. Metres, converted through heightScale like the freeboard above.
        [System.NonSerialized] public bool enforceContinentalRise = true;
        public float continentalRiseMetres = 120f;         // METRES the deep interior must stand above sea
        public float continentalRiseDistanceMetres = 4000f; // METRES inland over which that rise is reached
        public float continentalRiseStrength = 1f;          // 0..1 blend of the obligation
        public float shoreGuardDeadzoneMetres = 80f;         // METRES from the sea where the rise guard is OFF, so land meets the sea as a natural beach (§A5)

        // §A6 LAKE-AWARE BASIN GUARD. Pushed in by UCWaterSystemManager before the bake (scene
        // data, not graph asset - mirrors RiverCourseInputNode._courses). The continental-rise
        // guard exempts a sub-sea basin from being raised ONLY where a real lake footprint covers
        // it; every OTHER dry sub-sea depression is lifted to a plain instead of being left as a
        // below-sea pan (the "dry lake basin" artifact). Null (nothing pushed - e.g. a world's
        // first bake before lakes spawn, or no water manager) means the pass falls back to its old
        // concavity heuristic, so behaviour is unchanged until footprints exist.
        [System.NonSerialized] public System.Collections.Generic.List<LakeFootprint> lakeFootprints;

        /// <summary>One spawned/placed water body's world footprint, for the lake-aware guard.</summary>
        [System.Serializable] // UC-T03-LAKES / UC-T01-07: include complete geometry in prepared-input hashes.
        public sealed class LakeFootprint
        {
            /// <summary>UC-T03-06: stable world-local water-body identity; empty legacy inputs use solve-component identity.</summary>
            public string bodyId;
            /// <summary>UC-T03-07: true when surfaceMetres was captured from a placed still-water body.
            /// False preserves legacy footprint-only callers, which use the natural basin spill level.</summary>
            public bool hasSurfaceLevel;
            /// <summary>UC-T03-07: captured flat water elevation in world metres; finite when hasSurfaceLevel is true.</summary>
            public float surfaceMetres;
            public Vector2[] ringXZ;      // world XZ shore ring (even-odd tested)
            public Bounds planBounds;     // world-XZ plan bounds, to limit the rasterised scan
        }

        /// <summary>UC-T03-LAKES: shared serializable view of scene lake inputs, never saved in graph assets.</summary>
        [System.Serializable]
        internal sealed class PreparedLakeFootprints
        {
            public System.Collections.Generic.List<LakeFootprint> footprints;
        }

        [System.NonSerialized] private PreparedLakeFootprints preparedLakeState;

        /// <summary>Assigns the scene lake exclusions before hashing and dispatching a bake.</summary>
        /// <param name="footprints">World-metre rings/bounds; null or empty clears lake overrides.</param>
        /// <remarks>Keep the data stable until all bake consumers finish. Call on the main thread.</remarks>
        public void SetLakeFootprints(System.Collections.Generic.List<LakeFootprint> footprints)
        {
            lakeFootprints = footprints;
        }

        /// <summary>UC-T01-06: copies nonserialized guards and deep-copies lake geometry into a detached node.</summary>
        /// <param name="snapshot">Same-type generation node; another type is rejected. Main thread only.</param>
        public void CopyInputsTo(Generator snapshot)
        {
            HeightOutputNode target = snapshot as HeightOutputNode;
            if (target == null) throw new System.ArgumentException("Expected a Height Output snapshot.", "snapshot");
            target.enforceSeaLevelFloor = enforceSeaLevelFloor;
            target.enforceContinentalRise = enforceContinentalRise;
            target.enforceCoastalShelf = enforceCoastalShelf;
            PreparedLakeFootprints data = new PreparedLakeFootprints { footprints = lakeFootprints };
            target.SetLakeFootprints(JsonUtility.FromJson<PreparedLakeFootprints>(JsonUtility.ToJson(data)).footprints);
        }

        /// <summary>UC-T03-LAKES: exposes the actual lake exemptions to prepared-input hashing.</summary>
        /// <returns>A reusable serializable wrapper, or null when no lake override applies.</returns>
        /// <remarks>Main-thread only, before dispatch; this does not isolate concurrent graph runs.</remarks>
        public object GetPreparedInputsForHash()
        {
            if (lakeFootprints == null || lakeFootprints.Count == 0)
            {
                if (preparedLakeState != null) preparedLakeState.footprints = null;
                return null;
            }
            if (preparedLakeState == null) preparedLakeState = new PreparedLakeFootprints();
            preparedLakeState.footprints = lakeFootprints;
            return preparedLakeState;
        }

        /// <summary>
        /// §A6. Rasterises the pushed lake footprints onto the whole-world drainage grid (the same
        /// coarse lattice the basin flood-fill runs on), so the continental-rise guard can tell a
        /// real lake basin (exempt) from a dry sub-sea pan (lift to a plain). Returns null when no
        /// footprints are pushed - the guard reads that as "use the old heuristic", so a world with
        /// no lake objects yet is byte-unchanged. One bounded scan per footprint, once per bake
        /// grid, mobile-safe (only the cells inside each footprint's plan bounds are tested).
        /// </summary>
        private float[] BuildLakeMask(Matrix grid)
        {
            if (lakeFootprints == null || lakeFootprints.Count == 0) return null;
            if (grid == null) return null;

            int resX = grid.rect.size.x, resZ = grid.rect.size.z;
            if (resX < 2 || resZ < 2) return null;

            float[] mask = new float[resX * resZ];
            Vector2 ps = grid.PixelSize;
            float psx = Mathf.Max(1e-3f, ps.x);
            float psz = Mathf.Max(1e-3f, ps.y);
            float ox = grid.worldPos.x;
            float oz = grid.worldPos.z;

            for (int f = 0; f < lakeFootprints.Count; f++)
            {
                LakeFootprint fp = lakeFootprints[f];
                if (fp == null || fp.ringXZ == null || fp.ringXZ.Length < 3) continue;

                Bounds b = fp.planBounds;
                int x0 = Mathf.Max(0,        Mathf.FloorToInt((b.min.x - ox) / psx));
                int x1 = Mathf.Min(resX - 1, Mathf.CeilToInt ((b.max.x - ox) / psx));
                int z0 = Mathf.Max(0,        Mathf.FloorToInt((b.min.z - oz) / psz));
                int z1 = Mathf.Min(resZ - 1, Mathf.CeilToInt ((b.max.z - oz) / psz));

                for (int z = z0; z <= z1; z++)
                {
                    float wz = oz + z * psz;
                    int row = z * resX;
                    for (int x = x0; x <= x1; x++)
                    {
                        float wx = ox + x * psx;
                        if (PointInRingXZ(fp.ringXZ, wx, wz)) mask[row + x] = 1f;
                    }
                }
            }

            return mask;
        }

        /// <summary>Even-odd point-in-polygon on a flat XZ ring (ring.x = worldX, ring.y = worldZ).</summary>
        private static bool PointInRingXZ(Vector2[] ring, float x, float z)
        {
            bool inside = false;
            int n = ring.Length;
            int j = n - 1;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[j];
                if ((a.y > z) != (b.y > z))
                {
                    float cross = (b.x - a.x) * (z - a.y) / (b.y - a.y) + a.x;
                    if (x < cross) inside = !inside;
                }
                j = i;
            }
            return inside;
        }

        // -- Core coastal shelf ---------------------------------------------------
        // Runs in the same output post-pass, right after the sea-level floor and on the
        // same composed heightmap, so it too binds every base creator AND combiner at
        // once with no wiring. Where land meets the ocean it grades the GENTLE stretches
        // into beaches and coastal plains and leaves the STEEP ones standing as cliffs,
        // so the coast is varied instead of a boxy wall of hill dropping into deep water.
        // Land-side and lower-only, so it never touches the sea's own depth and never
        // builds a wall. See CoastalShelf.cs for the slope + regional-character gating.
        [System.NonSerialized] public bool enforceCoastalShelf = true;
        public float coastalRange = 500f;       // metres inland the coastal band reaches (ascent edge)
        public float beachFlat = 10f;           // metres of near-shore wet beach sitting at the waterline
        public float coastalPlainMetres = 40f;  // metres of FLAT coastal-plain rim beyond the beach
        public float coastalRise = 0.03f;       // normalised height the beach ramp rises onto the rim
        public float cliffThreshold = 0.05f;    // excess above the ramp beyond which a shore stays a cliff
        public float coastalVariety = 0.6f;     // 0..1 regional variation between beach / plain / hill
        public float coastalShelfStrength = 1f; // 0..1 blend of the grade

        // DEF-13 ITEM 4: THE COASTAL BANDS ARE WORLD-RELATIVE, AND CAN ONLY EVER SHRINK.
        //
        // `coastalRange`, `beachFlat` and `coastalPlainMetres` are absolute metres, which is the
        // same world-scale mistake DEF-3 found on the river side: 500 m of coastal band is a
        // narrow fringe on a 50 km world and a huge fraction of the landmass on a 2 km one. The
        // author's own graph authors exactly that (`coastalRange: 500`), which is why a correct
        // shaper still flattened their island - the shaper was right, the band was wider than
        // the land.
        //
        // WHY THIS TAKES THE MINIMUM AND NOT THE FRACTION OUTRIGHT, which is the part that
        // makes turning it on by default safe: the resolved band is
        // `Min(authored metres, fraction x world span)`. On a large world the authored value
        // still wins and nothing an author already tuned changes. On a small world the fraction
        // wins and the band shrinks. So this dial can only ever REDUCE the flattening - it can
        // never widen a band an author did not ask for, and it cannot make any existing world
        // worse. The span used is the SHORTER of the two world extents, so a long thin world
        // cannot get a band wider than its narrow dimension.
        public bool scaleCoastToWorld = true;
        [Range(0.002f,  0.40f)] public float coastalRangeFraction = 0.08f;   // 8% of the shorter span
        [Range(0.0002f, 0.05f)] public float beachFlatFraction    = 0.004f;
        [Range(0.0005f, 0.10f)] public float coastalPlainFraction = 0.015f;

        /// <summary>UC-T03-02: guards composed ground, restores only surviving river relief, then writes finite normalized native heights.</summary>
        /// <param name="src">Read-only composed height and optional matching pre-trench reference; normalized to heightScale.</param>
        /// <param name="tile">Owning output bag and captured world context.</param>
        /// <param name="stop">Optional cancellation; cancellation leaves the heightmap unpublished.</param>
        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            // UC-T03-02: procedural rivers already cut the graph height. Guard their
            // reference ground, then restore the remaining cut; scene water is separate.
            Matrix composed = src;
            bool guarded = false;
            bool wantFloor = enforceSeaLevelFloor && floorStrength > 1e-5f;
            bool wantShelf = enforceCoastalShelf && coastalShelfStrength > 1e-5f;
            bool wantRise  = enforceContinentalRise && continentalRiseStrength > 1e-5f;
            if (wantFloor || wantShelf || wantRise)
            {
                // One whole-world drainage solve serves both passes; the per-bake cache
                // means later chunks reuse it byte-for-byte rather than re-solving.
                WorldDrainage wd = WorldDrainage.Solve(this, 0, tile,
                    SeaLevelFloor.DefaultResolution, 0f, false, stop, useTrenchReference: true);
                if (stop != null && stop.StopRequested) return;

                if (wd != null)
                {
                    // Work on a copy: src may be a cached product other nodes still read.
                    Matrix work = src.CopyGuardGround(); // UC-T03-02: same guard math/order, applied before river relief.
                    // UC-T08-15: every guard reads the same accepted shores; fine protection also covers lakes smaller than the solve grid.
                    float[] lakeMask = BuildLakeMask(wd.height);

                    // ONE CONVERSION, SHARED BY ALL THREE PASSES (DEF-13).
                    // Normalised = metres / heightScale, so "20 m above sea" means the same
                    // clearance whatever the world's vertical scale is. This used to be
                    // computed twice - once inside the floor block, once inside the rise block
                    // - and the shelf had no copy at all, so it fell back to a hardcoded ~6 cm
                    // and graded the beach back down to the waterline that the other two passes
                    // had just lifted it off. Hoisting it is what makes `seaFreeboardMetres` a
                    // single dial with a single meaning instead of a dial two passes honour and
                    // a third quietly ignores.
                    float scaleM = heightScale > 1e-3f ? heightScale : 600f;
                    float seaFreeboardNorm = Mathf.Clamp01(seaFreeboardMetres / scaleM);

                    // DEF-13 item 4: resolve the coastal bands against the world's own size.
                    // Min(authored, fraction x span), so this can only ever shrink a band -
                    // see the field comments. Resolved once here and handed to BOTH the shelf
                    // and the continental rise, so the two cannot disagree about how far the
                    // coast reaches.
                    float effRange = coastalRange;
                    float effBeach = beachFlat;
                    float effPlain = coastalPlainMetres;
                    if (scaleCoastToWorld)
                    {
                        Vector2 wpix = wd.height != null ? wd.height.PixelSize : new Vector2(1f, 1f);
                        float spanXm = Mathf.Max(1f, wd.resX * Mathf.Max(1e-3f, wpix.x));
                        float spanZm = Mathf.Max(1f, wd.resZ * Mathf.Max(1e-3f, wpix.y));
                        float span   = Mathf.Min(spanXm, spanZm);

                        effRange = CoastalShelf.ResolveBandMetres(effRange, coastalRangeFraction, span, 0.002f,  0.40f);
                        effBeach = CoastalShelf.ResolveBandMetres(effBeach, beachFlatFraction,    span, 0.0002f, 0.05f);
                        effPlain = CoastalShelf.ResolveBandMetres(effPlain, coastalPlainFraction, span, 0.0005f, 0.10f);
                    }

                    if (wantFloor)
                    {
                        SeaLevelFloor.Apply(wd, work, seaLevel, coastGrade,
                                            basinDepth, seaFreeboardNorm, floorStrength, stop, lakeMask,
                                            effRange, shoreGuardDeadzoneMetres);
                        if (stop != null && stop.StopRequested) return;
                    }

                    if (wantShelf)
                    {
                        // After the floor: dry ground is now guaranteed >= sea, so the shelf
                        // only has to grade that coast into beach / plain, leaving cliffs. It
                        // grades DOWN to the same freeboard the floor just guaranteed, never
                        // below it.
                        CoastalShelf.Apply(wd, work, seaLevel, effRange, effBeach,
                                           effPlain, coastalRise, cliffThreshold,
                                           coastalVariety, coastalShelfStrength,
                                           seaFreeboardNorm, stop, lakeMask, effRange, shoreGuardDeadzoneMetres);
                        if (stop != null && stop.StopRequested) return;
                    }

                    if (wantRise)
                    {
                        // RUNS LAST, AND THAT ORDER IS THE GUARANTEE (author report
                        // 2026-10-06: a 150 m plain against a 150 m sea).
                        //
                        // This used to run between the floor and the shelf. But the shelf is
                        // LOWER-ONLY and used to grade its wet beach down to EXACTLY `sea`, so
                        // whatever head the rise had just established across the coastal band
                        // was immediately cut back to the waterline - a 500 m-wide plain
                        // sitting at sea level, which is both the artifact the author kept
                        // seeing and the reason a river laid across it has no head and reads as
                        // climbing. DEF-13 has since given the shelf the same freeboard this
                        // pass uses, so it no longer grades to the waterline at all; the order
                        // below stays as the belt-and-braces guarantee, not the only defence.
                        //
                        // Putting the rise last makes it the TERMINAL clamp on the composed
                        // heightmap: it is raise-only, so it cannot undo the shelf's beaches
                        // or cliffs - it can only re-assert the one invariant that no earlier
                        // pass is allowed to break. And because this whole post-pass runs on
                        // the COMPOSED height, that invariant binds every creator (Continental
                        // Shape Mask, Hybrid Multifractal, ...), every modifier and every
                        // combiner - Add, Subtract, Multiply, Blend alike - with no wiring and
                        // no way for a graph to opt out. Genuine ocean and above-sea lake
                        // basins stay exempt inside the pass, so the sea keeps its depth.
                        // Reuses the hoisted conversion above rather than recomputing it, so
                        // this pass cannot drift from the floor's and the shelf's idea of the
                        // same dial (DEF-13).
                        float riseMaxNorm = Mathf.Clamp01(continentalRiseMetres / scaleM);

                        // §A6: rasterise the pushed lake footprints onto the drainage grid so the
                        // guard exempts only real lake basins and lifts spurious dry ones. Null
                        // when nothing was pushed, which the pass reads as the old heuristic.
                        ContinentalRise.Apply(wd, work, seaLevel, seaFreeboardNorm, riseMaxNorm,
                                              continentalRiseDistanceMetres, basinDepth,
                                              effRange, shoreGuardDeadzoneMetres, lakeMask,
                                              continentalRiseStrength, stop);
                        if (stop != null && stop.StopRequested) return;
                    }

                    // UC-T08-15: a fixed accepted water plane cannot follow a rigid terrain lift. Preserve its actual bed reference exactly.
                    float[] fineLakeMask = BuildLakeMask(work);
                    if (fineLakeMask != null)
                        for (int i = 0; i < work.arr.Length; i++)
                        {
                            if (stop != null && (i & 8191) == 0 && stop.StopRequested) return;
                            if (fineLakeMask[i] > 0.5f) work.arr[i] = composed.arr[i] + composed.TrenchCut(i);
                        }
                    src = work;
                    guarded = true;
                }
            }

            int w = src.rect.size.x;
            int h = src.rect.size.z;

            // Unity's SetHeights expects [z, x] with z running north.
            float[,] heightmap = new float[h, w];
            float[,] trenchRelief = composed.trenchReference != null ? new float[h, w] : null; // UC-T03-10: preserve actual relief provenance across raised guards.

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;
                for (int x = 0; x < w; x++)
                {
                    float v = src.arr[rowStart + x];
                    // UC-T03-02: restore the actual downstream cut, not a mask-derived
                    // exemption. Unaffected ground/lakes retain every existing safeguard.
                    if (guarded) v -= composed.TrenchCut(rowStart + x);
                    if (trenchRelief != null) trenchRelief[z, x] = composed.TrenchCut(rowStart + x); // UC-T03-10: masks alone cannot establish a final trench.

                    // NaN and infinity are tested for by name, not clamped. Clamp01 is
                    // two inequalities and an else, and NaN fails both inequalities, so
                    // a clamp here passed every NaN straight through to SetHeights - the
                    // needle through the terrain. This runs off-thread on a buffer the
                    // graph already owns, so it is the cheapest place to stop one: the
                    // whole terrain path downstream then only ever sees real numbers.
                    //
                    // Zero rather than a neighbour: at this point there is no context to
                    // say what the surface was meant to do, and HeightGuard runs the
                    // neighbourhood repair on the main thread where there is.
                    if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;

                    heightmap[z, x] = v < 0f ? 0f : (v > 1f ? 1f : v);
                }
            }

            tile.outputs.heightmap = heightmap;
            tile.outputs.trenchRelief = trenchRelief;
            tile.outputs.requestColliderRebuild = true;
        }
    }
}
