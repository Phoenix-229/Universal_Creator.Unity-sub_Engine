// -----------------------------------------------------------------------------
//  Universe Creator - Path Tool Integration (Module 6 seam)
//  File:    DamReservoirReconciler.cs
//  Purpose: The UC side of a RaiseWaterBody request. Where TerrainShapeApplier
//           folds the two heightfield kinds (ConformCorridor, KeyAbutments) into a
//           chunk's heights, a reservoir is a WATER-SURFACE change, not a
//           heightfield write (see TerrainShapeApplier's header), so it is answered
//           here, against UC's own water bodies, and never by moving ground.
//
//  WHAT A DAM REQUEST ASKS FOR, AND WHAT UC OWNS
//  ---------------------------------------------
//  The tool states the levels and the dam line; UC owns the water. For each
//  RaiseWaterBody request this reconciler finds the author-placed UCWaterBody the
//  dam impounds, raises it to the held surface level, and clips it at the dam line
//  so the reservoir stands behind the structure and not through it. It creates no
//  body -- "NOTHING HERE IS AUTOMATIC": the author drops a lake in the valley (the
//  reservoir extent the tool reports is the hint for which one), and the dam then
//  governs its level and edge. A request that impounds no placed body is an honest
//  zero, exactly as the tool's own flood fill treats a dam that holds back nothing.
//
//  WHY IT GOVERNS RATHER THAN EDITS
//  --------------------------------
//  The raise and the clip are transient governance set on the body, never a write
//  to its serialised level or outline. UC re-derives every shaping from the request
//  set each rebuild (the discipline the whole seam follows), so withdrawing the dam
//  and rebuilding restores the author's own lake at its own level and shape, with no
//  trace of the reservoir it briefly became. Governing is therefore a full sync: a
//  body matched by a request is governed to it; a body no longer matched is released.
//
//  WHERE IT RUNS
//  -------------
//  ChunkManager.SettleCarvers, the water-side re-cut driver, calls this once per
//  regeneration alongside the river re-fit. A body whose governance changed is
//  rebuilt here (its basin is dug to the new level and clipped to the dam line), and
//  the count it returns feeds the single hoisted ReapplyCarves so the terrain the
//  reservoir sits in is re-derived once, not once per body.
//
//  IT ALSO CARRIES U-2'S SPILLWAY
//  ------------------------------
//  The spillway flow illusion (UCSpillwayEffect) is transient governance too, so it is
//  attached and removed on the same sync: a body this pass governs gets the effect
//  ensured on it, a body it releases gets the effect removed. The effect then reads the
//  governance set here - barrier centre, upstream normal, crest length, and the
//  reservoir-minus-tailwater head - and renders itself. This side sets no shader state
//  and emits no foam; it only decides which bodies are spillways and lets the effect do
//  the rest, so the seam stays a data hand-off even between U-1 and U-2.
//
//  NO DOTS, NO BURST, NO NATIVEARRAY. Plain managed maths.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Integration;

namespace UniverseCreator.TerrainSystem
{
    /// <summary> Syncs the dam-reservoir governance of a world's water bodies to the <see cref="TerrainShapeKind.
    /// RaiseWaterBody"/> requests a path tool left in the <see cref="TerrainShapeInbox"/>. Static and stateless like
    /// <see cref="TerrainShapeApplier"/>: the bodies and the requests arrive as arguments, so it runs the same from the
    /// settle pass and from a self test. </summary>
    public static class DamReservoirReconciler
    {
        /// <summary> Matches every level water body in <paramref name="carvers"/> against the RaiseWaterBody requests and
        /// governs or releases it accordingly, rebuilding any body whose governance actually changed. Returns how many
        /// bodies were reshaped -- a number the settle pass folds into its "something moved" flag and a self test can
        /// read rather than a claim. Rivers are never governed: a reservoir is a level body, and a river answers a dam
        /// through its tailwater, not by becoming the pond. </summary>
        public static int Reconcile(IReadOnlyList<TerrainShapeRequest> requests, List<IUCTerrainCarver> carvers)
        {
            if (carvers == null || carvers.Count == 0) { return 0; }

            int reshaped = 0;

            for (int i = 0; i < carvers.Count; i++)
            {
                UCWaterBody body = carvers[i] as UCWaterBody;
                if (body == null || !body.IsLevelBody) { continue; }

                TerrainShapeRequest gov = FindImpounding(requests, body);

                bool changed;
                if (gov != null)
                {
                    Vector2 upstream = UpstreamNormal(gov.upstreamHeadingDeg);
                    changed = body.GovernAsReservoir(gov.waterSurfaceLevelMetres, gov.tailwaterLevelMetres,
                        gov.barrierCentreX, gov.barrierCentreZ, upstream, gov.crestLengthMetres, gov.sourceId);

                    // U-2: the spillway illusion rides the governance just set. Ensure is idempotent and builds its
                    // spray lazily, so this is at most a component add per dam, never a cost inside this loop's body.
                    UCSpillwayEffect.Ensure(body);
                }
                else
                {
                    changed = body.ClearReservoirGovernance();

                    // A body no dam impounds loses its spillway with its governance - the same reversible sync the
                    // level itself follows, so a withdrawn dam leaves no foam and no spray behind.
                    UCSpillwayEffect.Remove(body);
                }

                if (!changed) { continue; }

                try
                {
                    // The basin is dug to the new level and clipped to the dam line, and the surface re-meshed on it.
                    // Same completion an applier makes when it writes heights: the reconciler owns the whole effect.
                    body.Rebuild();
                }
                catch (System.Exception e)
                {
                    // One malformed reservoir must not stop every chunk in the world from finishing, exactly as a bad
                    // river re-fit is caught and skipped in the settle loop that calls this.
                    Debug.LogError("Universe Creator: reservoir reshape failed on '" + body.name + "'. " + e.Message,
                                   body);
                    continue;
                }

                reshaped++;
            }

            return reshaped;
        }

        /// <summary> The RaiseWaterBody request that impounds this body, or null. A body qualifies when its plan
        /// footprint overlaps the flooded extent the tool reported (the search hint, not a paint region -- see the
        /// field doc on TerrainShapeRequest.reservoirMinX) AND at least one corner of that footprint lies on the
        /// upstream side of the dam line. The upstream test rejects a lake sitting wholly below the dam, which the clip
        /// would empty anyway; a lake that straddles the line is kept and the clip trims it to its upstream part. First
        /// match wins, so two dams cannot fight over one pond within a single pass. </summary>
        private static TerrainShapeRequest FindImpounding(IReadOnlyList<TerrainShapeRequest> requests, UCWaterBody body)
        {
            if (requests == null || requests.Count == 0) { return null; }
            if (!body.TryGetPlanBounds(out Bounds fp)) { return null; }

            for (int i = 0; i < requests.Count; i++)
            {
                TerrainShapeRequest r = requests[i];
                if (r == null || r.kind != TerrainShapeKind.RaiseWaterBody || !r.HasWork) { continue; }
                if (r.reservoirMaxX <= r.reservoirMinX || r.reservoirMaxZ <= r.reservoirMinZ) { continue; }

                if (fp.max.x < r.reservoirMinX || fp.min.x > r.reservoirMaxX
                    || fp.max.z < r.reservoirMinZ || fp.min.z > r.reservoirMaxZ) { continue; }

                Vector2 upstream = UpstreamNormal(r.upstreamHeadingDeg);
                if (MaxUpstreamSignedDistance(fp, r.barrierCentreX, r.barrierCentreZ, upstream) < 0f) { continue; }

                return r;
            }

            return null;
        }

        /// <summary> The unit upstream direction in world XZ from the request's heading (0 deg = +Z, 90 deg = +X, the
        /// same convention the abutment applier uses). A point p impounds when (p - barrierCentre) . upstreamNormal &gt;=
        /// 0 -- the infinite dam-line half-plane test the tool's flood fill uses, reproduced so both sides of the seam
        /// draw the water on the same side of the wall. </summary>
        private static Vector2 UpstreamNormal(float upstreamHeadingDeg)
        {
            float rad = upstreamHeadingDeg * Mathf.Deg2Rad;
            Vector2 n = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
            float m = n.magnitude;
            return m > 1e-6f ? n / m : new Vector2(0f, 1f);
        }

        /// <summary> The largest signed upstream distance over a footprint's four plan corners. Non-negative means some
        /// of the footprint is upstream of the dam line and the body is worth governing. </summary>
        private static float MaxUpstreamSignedDistance(Bounds fp, float centreX, float centreZ, Vector2 upstream)
        {
            float best = float.NegativeInfinity;

            for (int cx = 0; cx < 2; cx++)
            {
                float x = cx == 0 ? fp.min.x : fp.max.x;
                for (int cz = 0; cz < 2; cz++)
                {
                    float z = cz == 0 ? fp.min.z : fp.max.z;
                    float s = (x - centreX) * upstream.x + (z - centreZ) * upstream.y;
                    if (s > best) { best = s; }
                }
            }

            return best;
        }
    }
}
