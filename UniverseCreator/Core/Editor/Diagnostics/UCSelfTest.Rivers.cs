// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.Rivers.cs
//  Purpose: Pins the rebuilt river's data layer (step 2 of the river rebuild):
//           RiverProfile's forward-fill inherit rule and BakedRiverProfile's
//           arc-length sampling, driven directly over a Polyline course with no
//           scene, no GameObject and no TerrainData - so it runs headless in the
//           batch self test with the rest.
//
//  WHY THIS EXISTS
//  ---------------
//  The river payload (half-width, depth, discharge, velocity per course node) is
//  the foundation steps 3-10 build on, and two promises in it are easy to break
//  silently and neither is a compile error:
//    * "0 at a node inherits the previous authored value" (the same convention the
//      lake ring depths use). A regression that reads 0 as a real "0 metres wide"
//      would collapse a channel to nothing between authored nodes.
//    * An UNAUTHORED channel must read back 0 ("derive"), distinct from an authored
//      value, so the step-4 solver can tell which channels to compute from Leopold
//      and drainage rather than overwrite real author intent.
//  Real-world metres, no random numbers: the course and channel values are fixed.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Scatter;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.Nodes.Combiners;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>
        /// A 3-node course at X = 0, 100, 300 m (arc 0, 100, 300). Width authored
        /// with an inherit gap, discharge authored only mid-course, depth and
        /// velocity left unauthored. Asserts forward-fill, arc-length interpolation,
        /// end clamping, and that an unauthored channel derives (reads 0).
        /// </summary>
        private static void TestRiverProfileSamplingAndForwardFill()
        {
            Polyline course = new Polyline();
            course.Add(new Vector3(0f, 0f, 0f));
            course.Add(new Vector3(100f, 0f, 0f));
            course.Add(new Vector3(300f, 0f, 0f));
            course.closed = false;

            RiverProfile profile = new RiverProfile();
            // Width: node 1 is 0 = "inherit node 0", so [5, (5), 15] after fill.
            profile.halfWidthM.Add(5f);
            profile.halfWidthM.Add(0f);
            profile.halfWidthM.Add(15f);
            // Discharge authored only at the middle node: the leading node inherits
            // forward from the first authored value, so the whole channel is 50.
            profile.dischargeM3PerS.Add(0f);
            profile.dischargeM3PerS.Add(50f);
            profile.dischargeM3PerS.Add(0f);
            // Depth and velocity: left unauthored - they must read back as derive (0).

            BakedRiverProfile baked = profile.Bake(course);
            Check(baked != null, "RiverProfile.Bake returns a sampler for a valid course");
            if (baked == null) return;

            Check(Mathf.Abs(baked.TotalLengthM - 300f) < 1e-3f,
                "Baked course length is the arc total (300 m), was " + baked.TotalLengthM);

            Check(baked.HasWidth && baked.HasDischarge,
                "Authored width and discharge channels report present");
            Check(!baked.HasDepth && !baked.HasVelocity,
                "Unauthored depth and velocity channels report absent (derive)");

            // Width: 5 at the head, still 5 at node 1 (inherited), 10 half-way into
            // the 100->300 segment (lerp 5->15 at t=0.5), 15 at the mouth.
            Check(Mathf.Abs(baked.SampleAtArc(0f).halfWidthM - 5f) < 1e-3f,
                "Width at the head is 5 m, was " + baked.SampleAtArc(0f).halfWidthM);
            Check(Mathf.Abs(baked.SampleAtArc(100f).halfWidthM - 5f) < 1e-3f,
                "Width at node 1 inherited 5 m (0 means inherit), was " + baked.SampleAtArc(100f).halfWidthM);
            Check(Mathf.Abs(baked.SampleAtArc(200f).halfWidthM - 10f) < 1e-3f,
                "Width half-way node1->node2 lerps to 10 m, was " + baked.SampleAtArc(200f).halfWidthM);
            Check(Mathf.Abs(baked.SampleAtArc(300f).halfWidthM - 15f) < 1e-3f,
                "Width at the mouth is 15 m, was " + baked.SampleAtArc(300f).halfWidthM);

            // Leading inherit: discharge is 50 everywhere, including before its node.
            Check(Mathf.Abs(baked.SampleAtArc(10f).dischargeM3PerS - 50f) < 1e-3f,
                "Discharge leading node inherited forward to 50 m3/s, was " + baked.SampleAtArc(10f).dischargeM3PerS);

            // Unauthored channel derives: reads exactly 0, not an interpolated guess.
            Check(baked.SampleAtArc(150f).depthM == 0f,
                "Unauthored depth reads 0 (derive), was " + baked.SampleAtArc(150f).depthM);

            // Arc beyond the ends clamps rather than extrapolating.
            Check(Mathf.Abs(baked.SampleAtArc(-50f).halfWidthM - 5f) < 1e-3f,
                "Arc before the head clamps to the head width");
            Check(Mathf.Abs(baked.SampleAtArc(999f).halfWidthM - 15f) < 1e-3f,
                "Arc past the mouth clamps to the mouth width");

            // Trim keeps an authored channel course-length (padding new nodes with
            // inherit-0), and leaves an unauthored channel empty rather than
            // promoting it to a column of zeros.
            RiverProfile shortP = new RiverProfile();
            shortP.halfWidthM.Add(5f);          // one value, course has three nodes
            shortP.Trim(course);
            Check(shortP.halfWidthM.Count == 3, "Trim pads an authored channel to course length");
            Check(shortP.depthM.Count == 0, "Trim leaves an unauthored channel empty (not zero-filled)");
            BakedRiverProfile shortBaked = shortP.Bake(course);
            Check(shortBaked != null && Mathf.Abs(shortBaked.SampleAtArc(300f).halfWidthM - 5f) < 1e-3f,
                "A single authored width inherits forward across every node");
        }

        /// <summary>
        /// The child-GameObject authoring model (step 3): a river body with child
        /// UCRiverNode handles must bake, in sibling order, into the body's open
        /// outline (positions, body-local) and riverProfile (channels) - the single
        /// baked source the rest of the pipeline reads. Driven over real components
        /// in edit mode, then torn down.
        /// </summary>
        private static void TestRiverCourseGathersFromChildNodes()
        {
            GameObject bodyGo = new GameObject("UC Self Test River");
            try
            {
                bodyGo.transform.position = Vector3.zero;
                UCWaterBody body = bodyGo.AddComponent<UCWaterBody>();

                // Three nodes along X at 0 / 100 / 300 m, added in course order so
                // GetComponentsInChildren returns them in that order.
                AddRiverNode(bodyGo.transform, new Vector3(0f, 0f, 0f),   5f);
                AddRiverNode(bodyGo.transform, new Vector3(100f, 0f, 0f), 0f);   // inherit
                AddRiverNode(bodyGo.transform, new Vector3(300f, 0f, 0f), 15f);

                int gathered = body.GatherCourseFromNodes();
                Check(gathered == 3, "Gather reads all three child nodes, got " + gathered);
                Check(body.outline != null && body.outline.Count == 3 && !body.outline.closed,
                    "Gathered outline is an open 3-point course");
                Check(Mathf.Abs(body.outline.points[1].x - 100f) < 1e-3f,
                    "Node 1 baked to body-local X = 100, was " + body.outline.points[1].x);
                Check(body.riverProfile.halfWidthM.Count == 3
                      && Mathf.Abs(body.riverProfile.halfWidthM[0] - 5f) < 1e-3f
                      && body.riverProfile.halfWidthM[1] == 0f
                      && Mathf.Abs(body.riverProfile.halfWidthM[2] - 15f) < 1e-3f,
                    "Gathered width channel is [5, 0(inherit), 15]");

                BakedRiverProfile baked = body.riverProfile.Bake(body.outline);
                Check(baked != null && Mathf.Abs(baked.SampleAtArc(200f).halfWidthM - 10f) < 1e-3f,
                    "Baked gathered course interpolates width to 10 m half-way node1->node2");
                Check(baked != null && !baked.HasDepth,
                    "An all-zero depth channel bakes to derive (absent)");
            }
            finally
            {
                Object.DestroyImmediate(bodyGo);
            }
        }

        private static void AddRiverNode(Transform parent, Vector3 worldPos, float halfWidthM)
        {
            GameObject go = new GameObject("Node");
            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;
            UCRiverNode node = go.AddComponent<UCRiverNode>();
            node.halfWidthM = halfWidthM;
        }

        /// <summary>
        /// Step 4, the fought-bug fix: the solved bed must descend from head to mouth
        /// EVEN WHERE THE GROUND RISES into a hill mid-course. A course whose middle
        /// node sits on a 130 m hump between 100 m and 90/80 m ground must still cut a
        /// strictly descending bed - the hump is cut through, never climbed.
        /// </summary>
        private static void TestRiverBedProfileAlwaysDescends()
        {
            System.Collections.Generic.List<Vector3> course = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f,   100f, 0f),
                new Vector3(100f, 130f, 0f),   // a hill the course crosses
                new Vector3(200f,  90f, 0f),
                new Vector3(300f,  80f, 0f),
            };

            SolvedRiverBed bed = RiverCourseSolver.SolveBedProfile(course, null, 3f);
            Check(bed.valid && bed.bedElevationM != null && bed.bedElevationM.Length == 4,
                "Bed profile solves for a 4-node course");
            if (!bed.valid) return;

            for (int i = 1; i < bed.bedElevationM.Length; i++)
            {
                Check(bed.bedElevationM[i] < bed.bedElevationM[i - 1],
                    "Bed strictly descends at node " + i + " (was "
                    + bed.bedElevationM[i] + " vs " + bed.bedElevationM[i - 1] + ")");
            }

            Check(bed.bedElevationM[1] < bed.bedElevationM[0],
                "The hump node's bed holds the descending line (cut through, not climbed), was "
                + bed.bedElevationM[1]);
            Check(!bed.drawnUphill, "A course from 100 m to 80 m is not flagged uphill");
            Check(bed.sinuosity >= 1f, "Sinuosity is at least 1");
            Check(Mathf.Abs(bed.arcM[3] - 300f) < 1e-3f, "Arc total is 300 m");
        }

        /// <summary>
        /// A course drawn UPHILL overall (mouth ground above head ground) is flagged
        /// for the step-5 validator, but the bed is still forced strictly downhill so
        /// water can never be asked to flow up.
        /// </summary>
        private static void TestRiverBedUphillCourseStillDescends()
        {
            System.Collections.Generic.List<Vector3> course = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f,   80f, 0f),
                new Vector3(100f, 90f, 0f),
                new Vector3(200f, 100f, 0f),   // drawn climbing
            };

            SolvedRiverBed bed = RiverCourseSolver.SolveBedProfile(course, null, 2f);
            Check(bed.valid, "Uphill-drawn course still solves");
            Check(bed.drawnUphill, "An 80 m -> 100 m course is flagged drawn-uphill");
            for (int i = 1; i < bed.bedElevationM.Length; i++)
            {
                Check(bed.bedElevationM[i] < bed.bedElevationM[i - 1],
                    "Even an uphill-drawn course gets a strictly descending bed at node " + i);
            }
        }

        /// <summary>
        /// Step 5, the drainage guarantee: a drawn course is validated, not auto-routed.
        /// A mouth within a lake's reach reads as "at water"; one far from any body is
        /// flagged orphaned; an uphill course is flagged; a one-node course is rejected.
        /// </summary>
        private static void TestRiverRouteValidation()
        {
            System.Collections.Generic.List<Vector3> course = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f,   100f, 0f),
                new Vector3(100f,  90f, 0f),
                new Vector3(300f,  80f, 0f),
            };
            SolvedRiverBed bed = RiverCourseSolver.SolveBedProfile(course, null, 3f);

            // Mouth sits on a lake centred at (300,0) with 10 m reach: at water.
            var atLake = new System.Collections.Generic.List<WaterJoinTarget>
            {
                new WaterJoinTarget { centreXZ = new Vector2(300f, 0f), reachM = 10f }
            };
            var f1 = RiverRouteValidator.Validate(course, bed, atLake, out bool atWater1);
            Check(atWater1, "Mouth within a lake's reach counts as at water");
            Check(!HasIssue(f1, RiverRouteIssue.MouthNotAtWater), "No orphaned-mouth finding when the mouth reaches a lake");
            Check(!HasIssue(f1, RiverRouteIssue.DrawnUphill), "A 100->80 m course is not flagged uphill");

            // Lake far away: orphaned mouth.
            var farLake = new System.Collections.Generic.List<WaterJoinTarget>
            {
                new WaterJoinTarget { centreXZ = new Vector2(1000f, 0f), reachM = 10f }
            };
            var f2 = RiverRouteValidator.Validate(course, bed, farLake, out bool atWater2);
            Check(!atWater2 && HasIssue(f2, RiverRouteIssue.MouthNotAtWater),
                "A mouth far from any body is flagged orphaned");

            // Uphill-drawn course is flagged.
            System.Collections.Generic.List<Vector3> up = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f, 80f, 0f), new Vector3(200f, 100f, 0f)
            };
            SolvedRiverBed bedUp = RiverCourseSolver.SolveBedProfile(up, null, 2f);
            var f3 = RiverRouteValidator.Validate(up, bedUp, null, out _);
            Check(HasIssue(f3, RiverRouteIssue.DrawnUphill), "An uphill-drawn course is flagged");

            // One node is not a channel.
            System.Collections.Generic.List<Vector3> one = new System.Collections.Generic.List<Vector3> { Vector3.zero };
            var f4 = RiverRouteValidator.Validate(one, default, null, out _);
            Check(HasIssue(f4, RiverRouteIssue.TooFewNodes), "A one-node course is rejected");
        }

        private static bool HasIssue(System.Collections.Generic.List<RiverRouteFinding> findings, RiverRouteIssue issue)
        {
            for (int i = 0; i < findings.Count; i++) if (findings[i].issue == issue) return true;
            return false;
        }

        /// <summary>
        /// Step 6, the terrain-blend carve: a solved river course applied to flat
        /// ground must cut a channel down to the bed at the centreline, and that cut
        /// must descend from head to mouth. Driven over a float[,] heightfield, no
        /// scene, like the chunk-surface test.
        /// </summary>
        private static void TestRiverCarveCutsDescendingChannel()
        {
            WaterCarveLayer carve = new WaterCarveLayer();
            carve.enabled = true;
            carve.bevelMetres = 4f;
            carve.corridorMetres = 0f;

            // Course straight along X at z = 100 (mid-chunk), bed descending 48->44->40.
            System.Collections.Generic.List<Vector3> course = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f,   50f, 100f),
                new Vector3(100f, 46f, 100f),
                new Vector3(200f, 42f, 100f),
            };
            float[] bedY = { 48f, 44f, 40f };
            float[] arc = { 0f, 100f, 200f };
            float[] hw = { 5f, 5f, 5f };
            float[] dep = { 2f, 2f, 2f };
            carve.SetRiverCourse(course, bedY, arc, hw, dep);
            carve.RecomputeBounds();

            // Flat ground at 60 m over a 200x200 m chunk at the origin, 65x65 posts.
            const int R = 65;
            const float max = 100f;
            float[,] h = new float[R, R];
            for (int z = 0; z < R; z++)
                for (int x = 0; x < R; x++)
                    h[z, x] = 60f / max;

            Bounds chunk = new Bounds(new Vector3(100f, 0f, 100f), new Vector3(200f, 200f, 200f));
            float mps = 200f / (R - 1);
            carve.ApplyTo(h, chunk, mps, max);

            int cz = Mathf.RoundToInt(100f / mps);   // z = 100 m
            int cxMid = Mathf.RoundToInt(100f / mps); // arc 100, bed 44
            int cxUp = Mathf.RoundToInt(50f / mps);   // arc  50, bed 46
            int cxDn = Mathf.RoundToInt(150f / mps);  // arc 150, bed 42

            float mid = h[cz, cxMid] * max;
            Check(mid < 59f, "River carve lowered the channel centre below flat ground (was " + mid + " m)");
            Check(Mathf.Abs(mid - 44f) < 3f, "Channel centre cut to about the bed (44 m), was " + mid);

            float up = h[cz, cxUp] * max;
            float dn = h[cz, cxDn] * max;
            Check(up > dn, "Carved channel descends head->mouth (upstream " + up + " m > downstream " + dn + " m)");

            // A cell well off the course (corner) is untouched flat ground.
            Check(Mathf.Abs(h[2, 2] * max - 60f) < 0.5f, "Ground far from the course is left flat");
        }

        /// <summary>
        /// Step 7, the mouth blend: where the river meets a lake, the bed is pulled DOWN
        /// to the lake surface over the estuary (never raised), fixing the §38 raised
        /// mouth. Upstream nodes outside the estuary are untouched, and a lake surface
        /// above the bed must not lift it (Min-blend).
        /// </summary>
        private static void TestRiverMouthBlendsToWaterSurface()
        {
            float[] arc = { 0f, 100f, 200f, 300f };

            // Lake surface at 35 m, below the mouth bed (38). Estuary 0.4 -> blendStart
            // at 180 m, so nodes at 200 and 300 blend, nodes at 0 and 100 do not.
            float[] bed = { 48f, 44f, 40f, 38f };
            RiverCourseSolver.BlendMouthToSurface(bed, arc, 35f, 0.4f);

            Check(bed[0] == 48f && bed[1] == 44f, "Upstream nodes outside the estuary are untouched by the blend");
            Check(Mathf.Abs(bed[3] - 35f) < 1e-3f, "The mouth bed is pulled down to the lake surface (35 m), was " + bed[3]);
            Check(bed[2] < 40f && bed[2] > 35f, "The node just inside the estuary eases part way, was " + bed[2]);
            for (int i = 1; i < bed.Length; i++)
                Check(bed[i] <= bed[i - 1] + 1e-4f, "The blended bed stays monotonically descending at node " + i);

            // A lake surface ABOVE the mouth bed must never raise it.
            float[] bed2 = { 48f, 44f, 40f, 38f };
            RiverCourseSolver.BlendMouthToSurface(bed2, arc, 50f, 0.4f);
            Check(Mathf.Abs(bed2[3] - 38f) < 1e-3f, "A higher lake surface never raises the mouth bed (Min-blend)");
        }

        /// <summary>
        /// Step 9, the bank dressing bands: lateral distance off the channel classifies
        /// into Channel / Waterline / Bank / Floodplain / Dry, and the deterministic
        /// density gate never dresses open water or dry ground and repeats for a seed.
        /// </summary>
        private static void TestRiverBankZonesAndDressing()
        {
            // half-width 5 m, bank band 3 m, floodplain 10 m.
            Check(RiverBankZones.Classify(0f, 5f, 3f, 10f) == RiverBankZone.Channel, "Centre is open channel");
            Check(RiverBankZones.Classify(4.6f, 5f, 3f, 10f) == RiverBankZone.Waterline, "Just inside the edge is the waterline band");
            Check(RiverBankZones.Classify(6f, 5f, 3f, 10f) == RiverBankZone.Bank, "1 m past the edge is the bank");
            Check(RiverBankZones.Classify(10f, 5f, 3f, 10f) == RiverBankZone.Floodplain, "5 m past the edge is floodplain");
            Check(RiverBankZones.Classify(20f, 5f, 3f, 10f) == RiverBankZone.Dry, "Well past the floodplain is dry ground");

            Check(!RiverBankZones.ShouldDress(RiverBankZone.Channel, 1f, 1, 1, 7), "Open channel is never dressed");
            Check(!RiverBankZones.ShouldDress(RiverBankZone.Dry, 1f, 1, 1, 7), "Dry ground is never dressed by the river");
            Check(RiverBankZones.ShouldDress(RiverBankZone.Waterline, 1f, 1, 1, 7), "Full density always dresses");
            Check(!RiverBankZones.ShouldDress(RiverBankZone.Bank, 0f, 1, 1, 7), "Zero density never dresses");

            bool a = RiverBankZones.ShouldDress(RiverBankZone.Bank, 0.5f, 3, 4, 9);
            bool b = RiverBankZones.ShouldDress(RiverBankZone.Bank, 0.5f, 3, 4, 9);
            Check(a == b, "Dressing is deterministic for the same cell and seed");

            float d = RiverBankZones.Density01(5, 6, 7);
            Check(d >= 0f && d < 1f, "Density hash is in [0,1), was " + d);
        }

        /// <summary>
        /// §5a G-X1: on a bend the carved pool skews to the OUTER bank and the inner bank
        /// shallows into a point-bar; on a straight reach the section stays symmetric.
        /// A course turning LEFT (+X then +Z) must carve deeper on the right (outer) side
        /// of the incoming reach than on the left (inner) side at equal offset.
        /// </summary>
        private static void TestRiverCarveCurvatureAsymmetry()
        {
            float[] arc = { 0f, 100f, 200f };
            float[] bedY = { 498f, 496f, 494f };
            float[] hw = { 10f, 10f, 10f };
            float[] dep = { 5f, 5f, 5f };

            // Left turn: flow +X along z=0, then +Z along x=100.
            System.Collections.Generic.List<Vector3> bend = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f, 500f, 0f), new Vector3(100f, 500f, 0f), new Vector3(100f, 500f, 100f)
            };
            // riverRuggedness = 0 is the clean geometric channel. This gate tests the G-X1 curvature
            // LAW, so the natural pool/riffle, gravel and bank noise (which is keyed on world
            // position and deliberately differs left vs right) must not be part of it.
            WaterCarveLayer bendCarve = new WaterCarveLayer { enabled = true, riverRuggedness = 0f };
            bendCarve.SetRiverCourse(bend, bedY, arc, hw, dep);
            bendCarve.SetRiverCurvature(RiverCourseSolver.SignedCurvature(bend));
            bendCarve.RecomputeBounds();

            // Sample the incoming reach at x=50, two metres either side of the centreline.
            float right = bendCarve.HeightAt(50f, -2f, 1000f);   // outer bank of a left turn
            float left = bendCarve.HeightAt(50f, 2f, 1000f);     // inner bank
            Check(right < left - 1e-3f,
                "Pool skews to the outer (right) bank of a left turn (right " + right + " < left " + left + ")");

            // Straight control: no curvature -> symmetric section (both banks equal).
            System.Collections.Generic.List<Vector3> straight = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f, 500f, 0f), new Vector3(100f, 500f, 0f), new Vector3(200f, 500f, 0f)
            };
            WaterCarveLayer straightCarve = new WaterCarveLayer { enabled = true, riverRuggedness = 0f };
            straightCarve.SetRiverCourse(straight, bedY, arc, hw, dep);
            straightCarve.SetRiverCurvature(RiverCourseSolver.SignedCurvature(straight));
            straightCarve.RecomputeBounds();
            float sr = straightCarve.HeightAt(50f, -2f, 1000f);
            float sl = straightCarve.HeightAt(50f, 2f, 1000f);
            Check(Mathf.Abs(sr - sl) < 1e-2f, "A straight reach carves a symmetric section (|" + sr + " - " + sl + "|)");

            // Natural noise ON (the default): the two banks are allowed to differ, because the gravel
            // term is keyed on world position, but only by a bounded amount. Left/right can differ
            // only through that term: depth * nz * 0.16 * riverRuggedness (nz <= 1), here 5 * 0.16 = 0.8 m.
            // The section must also stay below the waterline (the bed never rises above the bank top).
            WaterCarveLayer naturalCarve = new WaterCarveLayer { enabled = true, riverRuggedness = 1f };
            naturalCarve.SetRiverCourse(straight, bedY, arc, hw, dep);
            naturalCarve.SetRiverCurvature(RiverCourseSolver.SignedCurvature(straight));
            naturalCarve.RecomputeBounds();
            float nr = naturalCarve.HeightAt(50f, -2f, 1000f);
            float nl = naturalCarve.HeightAt(50f, 2f, 1000f);
            Check(Mathf.Abs(nr - nl) <= 5f * 0.16f + 1e-3f,
                "Natural noise keeps a straight reach's two sides within the gravel bound (|" + nr + " - " + nl + "|)");
            Check(nr < 1000f && nl < 1000f,
                "A natural-noise channel still digs below the surrounding ground (" + nr + ", " + nl + ")");
        }

        /// <summary>
        /// §5a G-C1 / Guarantee C: hydraulic geometry sizes the channel from flow -
        /// more discharge widens and deepens it (Leopold w=a·Q^0.5, d∝w^0.8), the width
        /// clamps to the real-world 4-80 m band, and the Shields floor makes a gentler
        /// slope at least as deep as a steep one for grain stability.
        /// </summary>
        private static void TestRiverHydraulicGeometry()
        {
            float hwSmall, dSmall, hwBig, dBig;
            RiverCourseSolver.HydraulicGeometry(5f, 0.001f, out hwSmall, out dSmall);
            RiverCourseSolver.HydraulicGeometry(500f, 0.001f, out hwBig, out dBig);
            Check(hwBig > hwSmall, "Leopold: more discharge -> wider channel (" + hwBig + " > " + hwSmall + ")");
            Check(dBig > dSmall, "Leopold: more discharge -> deeper channel (" + dBig + " > " + dSmall + ")");

            float hwHuge, dHuge;
            RiverCourseSolver.HydraulicGeometry(1e9f, 0.001f, out hwHuge, out dHuge);
            Check(hwHuge <= RiverCourseSolver.MaxChannelWidthM * 0.5f + 1e-3f, "Width clamps to the max band");

            float hwTiny, dTiny;
            RiverCourseSolver.HydraulicGeometry(1e-6f, 0.001f, out hwTiny, out dTiny);
            Check(hwTiny >= RiverCourseSolver.MinChannelWidthM * 0.5f - 1e-3f, "Width floors at the min band");

            float hwG, dGentle, dSteep;
            RiverCourseSolver.HydraulicGeometry(50f, 0.0005f, out hwG, out dGentle);
            RiverCourseSolver.HydraulicGeometry(50f, 0.05f, out hwG, out dSteep);
            Check(dGentle >= dSteep - 1e-3f, "Shields floor: a gentler slope is at least as deep (" + dGentle + " >= " + dSteep + ")");
        }

        /// <summary>
        /// §7 T7: the mobile per-cell instance ceiling guard. An authored 20000 (or -1
        /// "unlimited") must be capped to the mobile floor so a dense cell cannot OOM the
        /// minimum target device; a value already under the cap is kept.
        /// </summary>
        private static void TestVegetationMobileCeiling()
        {
            int cap = UCVegetationSystemManager.MobileMaxInstancesPerCell;
            Check(UCVegetationSystemManager.MobileCappedInstancesPerCell(20000) == cap,
                "A 20000/cell ceiling caps to the mobile floor");
            Check(UCVegetationSystemManager.MobileCappedInstancesPerCell(-1) == cap,
                "An unlimited (-1) ceiling caps to the mobile floor");
            Check(UCVegetationSystemManager.MobileCappedInstancesPerCell(1000) == 1000,
                "A ceiling already under the mobile floor is kept");
        }

        /// <summary>
        /// §2 / gap #1 de-facet: the river course is tessellated through centripetal
        /// Catmull-Rom (what BuildRiverDig now feeds the carve/bed/mesh), so a meander is
        /// a smooth curve, not faceted straight segments ("joined stones"). A right-angle
        /// control bend must densify and round its corner.
        /// </summary>
        private static void TestRiverCourseDeFacet()
        {
            Polyline ctrl = new Polyline();
            ctrl.Add(new Vector3(0f, 0f, 0f));
            ctrl.Add(new Vector3(100f, 0f, 0f));
            ctrl.Add(new Vector3(100f, 0f, 100f));   // a hard right-angle bend

            Polyline smooth = SplineOperations.TessellateCatmullRom(ctrl, 5f, 0.5f);
            Check(smooth != null && smooth.Count > ctrl.Count,
                "Catmull-Rom tessellation densifies the course (" + (smooth != null ? smooth.Count : 0) + " > 3)");

            // "Not faceted" means the smooth curve DEPARTS from the straight control
            // legs somewhere (whichever way it rounds the bend). Measure each tessellated
            // point's distance to the control polyline; a straight-segment course would
            // leave every point exactly on it, a curved one pulls off it at the bend.
            float maxDev = 0f;
            if (smooth != null)
                for (int i = 0; i < smooth.Count; i++)
                {
                    Vector3 p = smooth.points[i];
                    float dSqr = SplineOperations.SqrDistanceToXZ(ctrl, p.x, p.z);
                    if (dSqr > maxDev) maxDev = dSqr;
                }
            Check(maxDev > 0.25f,
                "The tessellated curve departs from the straight legs at the bend (max dev " +
                Mathf.Sqrt(maxDev).ToString("0.00") + " m)");
        }

        /// <summary>
        /// PROCEDURAL RIVER NETWORK (RiverNetworkExtractor). Builds a drainage tree whose
        /// answer is known BY CONSTRUCTION - two headwater streams meeting at one junction
        /// and a single trunk running on into the sea - and asserts the four things the
        /// author asked the procedural half to deliver: that channels are found, that they
        /// JOIN at a confluence, that the trunk is ordered above its tributaries, and that
        /// the network DRAINS into water rather than stopping in dry ground.
        ///
        /// Written because the extractor compiled green while having zero callers, which
        /// proves nothing at all: a compile is not evidence the math runs. This is the
        /// first gate that actually executes it.
        ///
        /// The grid, 8x8, index = z*8 + x, cells 100 m across (so one cell = 10 000 m2):
        ///
        ///     stream A: (2,0)->(2,1)->(2,2)->(2,3) -\
        ///                                            >-- junction (3,4) -> (3,5) -> (3,6) -> (3,7) = SEA
        ///     stream B: (4,0)->(4,1)->(4,2)->(4,3) -/
        ///
        /// Every other cell has dir = -1 (no downstream neighbour), so it cannot become a
        /// channel and cannot perturb the tree.
        /// </summary>
        private static void TestRiverNetworkExtractsConfluencesAndMouth()
        {
            const int res = 8;
            const int count = res * res;

            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos  = Vector3.zero;
            height.worldSize = new Vector3(800f, 600f, 800f);   // 100 m per cell
            for (int i = 0; i < count; i++) height.arr[i] = 0.80f;   // high ground

            int[] dir = new int[count];
            float[] accum = new float[count];
            for (int i = 0; i < count; i++) { dir[i] = -1; accum[i] = 1f; }

            // Stream A, descending so the along-flow slope is positive.
            int a0 = 0 * res + 2, a1 = 1 * res + 2, a2 = 2 * res + 2, a3 = 3 * res + 2;
            // Stream B.
            int b0 = 0 * res + 4, b1 = 1 * res + 4, b2 = 2 * res + 4, b3 = 3 * res + 4;
            // Junction and trunk, ending in the sea.
            int j = 4 * res + 3, t1 = 5 * res + 3, t2 = 6 * res + 3, mouth = 7 * res + 3;

            height.arr[a0] = 0.70f; height.arr[a1] = 0.65f; height.arr[a2] = 0.60f; height.arr[a3] = 0.55f;
            height.arr[b0] = 0.70f; height.arr[b1] = 0.65f; height.arr[b2] = 0.60f; height.arr[b3] = 0.55f;
            height.arr[j]  = 0.50f; height.arr[t1] = 0.45f; height.arr[t2] = 0.40f;
            height.arr[mouth] = 0.30f;                        // below sea: the sea cell

            // NEIGHBOUR CODES, not cell indices - the production encoding. See
            // Hydrology.DownstreamCell for why a flat index here hid the real defect.
            dir[a0] = DirCode(0, 1); dir[a1] = DirCode(0, 1); dir[a2] = DirCode(0, 1);
            dir[a3] = DirCode(1, 1);
            dir[b0] = DirCode(0, 1); dir[b1] = DirCode(0, 1); dir[b2] = DirCode(0, 1);
            dir[b3] = DirCode(-1, 1);
            dir[j]  = DirCode(0, 1); dir[t1] = DirCode(0, 1); dir[t2] = DirCode(0, 1);
            // mouth itself keeps dir = -1

            // Accumulated cells, growing downstream as a real catchment does.
            accum[a0] = 1f; accum[a1] = 2f; accum[a2] = 3f; accum[a3] = 4f;
            accum[b0] = 1f; accum[b1] = 2f; accum[b2] = 3f; accum[b3] = 4f;
            accum[j] = 9f; accum[t1] = 10f; accum[t2] = 11f; accum[mouth] = 12f;

            // filled == height: no pooling anywhere, so slopes are the real ones.
            WorldDrainage wd = WorldDrainage.CreateForTests(height, null, dir, accum);
            Check(wd != null, "RN1: could not build a flow-bearing test WorldDrainage");
            if (wd == null) return;

            bool[] water = new bool[count];
            water[mouth] = true;                              // the sea

            RiverNetworkOptions opt = RiverNetworkOptions.Default();
            opt.minDrainageAreaM2 = 10000f;   // one cell, so every channel cell qualifies
            opt.slopeExponent = 0f;           // pure support-area rule for a known answer
            opt.slopeAreaThreshold = 1f;
            opt.minReachCells = 3;

            RiverNetwork net = RiverNetworkExtractor.Extract(wd, opt, water, new StopToken());
            Check(net != null, "RN1: the extractor returned no network at all");
            if (net == null) return;

            // 1) Three reaches: two headwaters and the trunk below the junction.
            Check(net.reaches.Count == 3,
                  "RN1: expected 3 reaches (2 headwaters + 1 trunk), got " + net.reaches.Count);

            // 2) The streams JOIN: exactly two reaches end at a confluence, and both name
            //    the same downstream reach. This is the "trenches that join each other"
            //    requirement - without it the two streams would cross, not merge.
            int confluences = 0, mouths = 0, trunkIdx = -1;
            for (int r = 0; r < net.reaches.Count; r++)
            {
                if (net.reaches[r].end == RiverReachEnd.Confluence) confluences++;
                if (net.reaches[r].end == RiverReachEnd.Water) { mouths++; trunkIdx = r; }
            }
            Check(confluences == 2,
                  "RN1: expected 2 reaches ending at a confluence, got " + confluences);

            // 3) It DRAINS: exactly one reach reaches the sea, and its last cell IS the
            //    sea cell, so the trench arrives at the waterline with no dry gap.
            Check(mouths == 1, "RN1: expected exactly 1 reach to reach water, got " + mouths);
            if (trunkIdx >= 0)
            {
                RiverReach trunk = net.reaches[trunkIdx];
                Check(trunk.cells.Count > 0 && trunk.cells[trunk.cells.Count - 1] == mouth,
                      "RN1: the draining reach does not end ON the sea cell");

                // Both tributaries must point at the trunk, or the network is not connected.
                int pointingAtTrunk = 0;
                for (int r = 0; r < net.reaches.Count; r++)
                    if (net.reaches[r].downstream == trunkIdx) pointingAtTrunk++;
                Check(pointingAtTrunk == 2,
                      "RN1: expected both headwaters to flow into the trunk, got "
                      + pointingAtTrunk);

                // 4) Strahler (1952): two order-1 tributaries meeting make an order-2
                //    trunk. This is what makes a trunk read as a trunk.
                Check(trunk.order == 2,
                      "RN1: the trunk below a 1+1 junction should be Strahler order 2, got "
                      + trunk.order);
                Check(net.MaxOrder() == 2,
                      "RN1: network max order should be 2, got " + net.MaxOrder());

                // Downstream hydraulic geometry actually produced a channel: a positive
                // width and depth, and a floodplain wider than the channel itself.
                Check(trunk.outletHalfWidthM > 0f && trunk.outletDepthM > 0f,
                      "RN1: the trunk outlet has no channel size (halfWidth "
                      + trunk.outletHalfWidthM.ToString("F3") + ", depth "
                      + trunk.outletDepthM.ToString("F3") + ")");
                Check(trunk.outletFloodplainHalfWidthM > trunk.outletHalfWidthM,
                      "RN1: the floodplain must be wider than the channel it belongs to");
            }

            // 5) NO REACH CLIMBS. The bed is cut below the depression-filled surface, so
            //    a non-descending reach would mean the trench itself runs uphill - the
            //    exact bug the whole river rebuild exists to retire.
            bool climbed = false;
            for (int r = 0; r < net.reaches.Count; r++)
            {
                List<int> cells = net.reaches[r].cells;
                for (int k = 1; k < cells.Count; k++)
                    if (wd.filled[cells[k]] > wd.filled[cells[k - 1]] + 1e-6f) climbed = true;
            }
            Check(!climbed,
                  "RN1: a reach runs UPHILL along its own flow path - the trench would climb");
        }

        // ---------------------------------------------------------------------
        //  THE "IT NEVER SPAWNS" GATES (2026-10-18)
        // ---------------------------------------------------------------------
        //  Author report: "I connected the nodes but the river trenches and network is
        //  not spawn on the world." It was true on EVERY world, and the existing RN1
        //  gate could not see it because it sets slopeExponent = 0 - deleting the slope
        //  term from the very law it was checking.
        //
        //  The defect: RiverNetworkExtractor measured slope by dividing a NORMALISED
        //  height drop by a run in METRES, then compared A * S against a threshold in
        //  square metres. On a 600 m world the slope came out ~600x too small, so no
        //  cell ever qualified as a channel, the extractor returned an empty network,
        //  and the node passed the height through untouched. One missing factor removed
        //  the entire feature.
        //
        //  These three tests fail loudly if it ever comes back:
        //    RIV-NET1 - the slope is in real metres per metre (two thresholds straddle
        //               the physically correct answer; the broken version fails both).
        //    RIV-NET2 - the threshold C scales with the world, so a small world is not
        //               held to an Earth-sized catchment.
        //    RIV-NET3 - the whole node, pulled through a real GraphEvaluator, actually
        //               lowers the height. This is the end-to-end gate: it would have
        //               caught the defect even if the extractor's own test were wrong.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The D8 code that steps from a cell to the neighbour at (dx, dz). Test worlds
        /// must store CODES in `dir`, because that is what Hydrology.FlowDirection
        /// produces; writing a flat cell index there is the bug these gates missed.
        /// </summary>
        private static int DirCode(int dx, int dz)
        {
            for (int d = 0; d < 8; d++)
            {
                int ox, oz;
                Hydrology.NeighbourOffset(d, out ox, out oz);
                if (ox == dx && oz == dz) return d;
            }
            return -1;
        }

        /// <summary>
        /// The direction encoding itself, pinned against the real producer. A `dir` array
        /// holds neighbour codes 0..7; a consumer that indexes `filled` with the value
        /// reads row 0 instead, gets no positive drop, and silently produces nothing.
        /// </summary>
        private static void TestHydrologyDirectionCodes()
        {
            const int res = 3;
            float[] h = new float[res * res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    h[z * res + x] = 1f - 0.1f * x;        // descends toward +X

            int[] dir = Hydrology.FlowDirection(h, res, res, null);
            int centre = 1 * res + 1;

            Check(dir[centre] >= 0 && dir[centre] <= 7,
                  "HYD-DIR: FlowDirection did not store a neighbour code at a cell with a "
                  + "clear downhill neighbour (got " + dir[centre] + ")");

            int down = Hydrology.DownstreamCell(dir, centre, res, res);
            Check(down == centre + 1,
                  "HYD-DIR: the decoder did not resolve the east neighbour - got " + down
                  + ", expected " + (centre + 1) + ". Every slope-driven feature reads 0 if "
                  + "a direction code is treated as a cell index");

            Check(Hydrology.DownstreamCell(new int[] { -1 }, 0, 1, 1) < 0,
                  "HYD-DIR: code -1 must mean 'no downstream cell', not a valid neighbour");
        }

        /// <summary>
        /// An 8x8 world with ONE straight descending channel column. Cell = 100 m,
        /// world height = 600 m, and the column drops 0.001 (0.6 m) per cell, so the
        /// along-flow slope is 0.6 / 100 = 0.006 m/m. Accumulation grows 2..8 cells
        /// (20 000 .. 80 000 m2). The last cell drains into the sea.
        /// </summary>
        private static WorldDrainage BuildRiverSlopeTestWorld(out bool[] water)
        {
            const int res = 8;
            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos = Vector3.zero;
            height.worldSize = new Vector3(800f, 600f, 800f);
            height.worldHeight = 600f;               // the unit the slope must be scaled by
            for (int i = 0; i < res * res; i++) height.arr[i] = 0.80f;

            int[] dir = new int[res * res];
            float[] accum = new float[res * res];
            for (int i = 0; i < dir.Length; i++) { dir[i] = -1; accum[i] = 1f; }

            const int col = 4;
            for (int z = 0; z < res; z++)
            {
                int i = z * res + col;
                height.arr[i] = 0.80f - 0.001f * z;
                accum[i] = 2f + z;
                dir[i] = z < res - 1 ? DirCode(0, 1) : -1;
            }

            water = new bool[res * res];
            water[(res - 1) * res + col] = true;     // the sea the channel empties into
            return WorldDrainage.CreateForTests(height, null, dir, accum);
        }

        private static void TestRiverNetworkSlopeUnits()
        {
            bool[] water;
            WorldDrainage wd = BuildRiverSlopeTestWorld(out water);
            Check(wd != null, "RIV-NET1: could not build the slope-unit test world");
            if (wd == null) return;

            RiverNetworkOptions opt = RiverNetworkOptions.Default();
            opt.minDrainageAreaM2 = 1000f;          // below every chain cell
            opt.slopeExponent = 1f;
            opt.minReachCells = 2;
            opt.scaleThresholdToWorld = false;      // judge the ABSOLUTE law

            // A * S at the head is 20 000 * 0.006 = 120. A threshold under that must
            // find channels; one above the largest term (80 000 * 0.006 = 480) must not.
            // Measured in normalised units instead, every term is ~0.1 and BOTH fail -
            // which is exactly how the feature was silently dead.
            opt.slopeAreaThreshold = 100f;
            RiverNetwork net = RiverNetworkExtractor.Extract(wd, opt, water, new StopToken());
            Check(net != null && net.reaches.Count > 0,
                  "RIV-NET1: a threshold BELOW A*S found no channel. The slope is not in "
                  + "metres per metre - the network is empty on every world, so no trench "
                  + "is ever carved.");

            opt.slopeAreaThreshold = 1000f;
            net = RiverNetworkExtractor.Extract(wd, opt, water, new StopToken());
            Check(net == null || net.reaches.Count == 0,
                  "RIV-NET1: a threshold ABOVE every A*S still produced a network - the "
                  + "slope-area law is not gating at all");
        }

        private static void TestRiverNetworkScalesThresholdToWorld()
        {
            bool[] water;
            WorldDrainage wd = BuildRiverSlopeTestWorld(out water);
            Check(wd != null, "RIV-NET2: could not build the world-scale test world");
            if (wd == null) return;

            RiverNetworkOptions opt = RiverNetworkOptions.Default();
            opt.minDrainageAreaM2 = 10000f;         // one cell: the initiation floor
            opt.slopeExponent = 1f;
            opt.slopeAreaThreshold = 1e5f;          // the SHIPPED absolute default
            opt.minReachCells = 2;

            // The shipped absolute C applied to this world: the largest term is 480, so
            // it correctly finds nothing. This is the Earth figure meeting a small world.
            opt.scaleThresholdToWorld = false;
            RiverNetwork net = RiverNetworkExtractor.Extract(wd, opt, water, new StopToken());
            Check(net == null || net.reaches.Count == 0,
                  "RIV-NET2: an Earth-scale threshold unexpectedly qualified on a small world");

            // Scaled to this world, C becomes 10 000 * 0.006 = 60, and the network grows.
            // This is the second half of "nothing spawns": scaling only the initiation
            // area and the runoff left C absolute, so a 4 km world still needed an
            // eighth of itself for one channel head.
            opt.scaleThresholdToWorld = true;
            net = RiverNetworkExtractor.Extract(wd, opt, water, new StopToken());
            Check(net != null && net.reaches.Count > 0,
                  "RIV-NET2: with the threshold scaled to the world, the same heightfield "
                  + "still produced no network");

            // And it is derived from the world, not from the authored number: doubling
            // the authored C must change nothing in this mode.
            opt.slopeAreaThreshold = 1e9f;
            net = RiverNetworkExtractor.Extract(wd, opt, water, new StopToken());
            Check(net != null && net.reaches.Count > 0,
                  "RIV-NET2: the authored absolute C still gates while world scaling is ON - "
                  + "the world-derived threshold is not actually replacing it");
        }

        /// <summary>
        /// A test-only Creator emitting a plane that descends toward +Z, as a pure
        /// function of world position (so it is identical on the chunk and on the
        /// coarse whole-world view, which is what makes TileWindows legal).
        /// </summary>
        [System.Serializable]
        private class RiverTestRampNode : Generator
        {
            public override string NodeName { get { return "River Test Ramp"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            private static readonly Port[] s_ins = new Port[0];
            private static readonly Port[] s_outs =
                { new Port("Out", PortType.Matrix, PortDirection.Output) };
            public override Port[] GetInputs() { return s_ins; }
            public override Port[] GetOutputs() { return s_outs; }

            public override object Generate(int outputIndex, TileData tile, StopToken stop)
            {
                Matrix m = tile.CreateMatrix();
                int w = m.rect.size.x, h = m.rect.size.z;
                for (int z = 0; z < h; z++)
                {
                    float f = h > 1 ? z / (float)(h - 1) : 0f;
                    float v = 0.95f - 0.35f * f;    // 0.95 -> 0.60 across the world
                    int row = z * w;
                    for (int x = 0; x < w; x++) m.arr[row + x] = v;
                }
                return m;
            }
        }

        /// <summary>
        /// The end-to-end gate: a REAL GraphEvaluator pulls River Network's Height and
        /// River Mask outputs over a real TileData, exactly as a chunk does. Asserts the
        /// network reaches the chunk AND that the returned height is lower than the
        /// ground that went in - i.e. a trench exists in the world, not just in a
        /// reach list.
        /// </summary>
        private static void TestRiverNetworkNodeCarvesTheTerrain()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                RiverTestRampNode ramp = new RiverTestRampNode();
                RiverNetworkNode node = new RiverNetworkNode();
                node.seaLevel = 0.605f;             // the plane's last row is 0.60
                node.solveResolution = 64;
                node.channelInitiationFraction = 0.001f;
                node.trunkChannelWidthMetres = 18f;

                graph.nodes.Add(ramp);
                graph.nodes.Add(node);
                graph.AddConnection(new Connection(ramp.id, 0, node.id, 0));

                // 65 samples over the same span as the 64-cell solve grid, so the read
                // back is lattice-aligned and a channel cell is sampled at full strength.
                const int res = 65;
                TileData tile = new TileData(
                    Coord.Zero, new CoordRect(new Coord(0, 0), new Coord(res, res)),
                    Vector3.zero, new Vector3(4096f, 600f, 4096f), 600f);
                GraphEvaluator ev = new GraphEvaluator(graph);
                tile.Bind(ev);

                Matrix mask = ev.PullNode(node, 1, tile, new StopToken()) as Matrix;
                Matrix carved = ev.PullNode(node, 0, tile, new StopToken()) as Matrix;
                Matrix plain = ramp.Generate(0, tile, new StopToken()) as Matrix;

                Check(mask != null && carved != null && plain != null,
                      "RIV-NET3: the node did not produce both outputs over a real graph");
                if (mask == null || carved == null || plain == null) return;

                float maskMax = 0f;
                for (int i = 0; i < mask.arr.Length; i++)
                    if (mask.arr[i] > maskMax) maskMax = mask.arr[i];

                // STAGE ISOLATION. A zero mask is the node's own "no network" early
                // return, which can come from three different places: the input never
                // pulled through the whole-world view, the drainage solve produced no
                // downhill directions, or the extractor rejected the world's own
                // parameters. Reporting all three at once is what turns one five-minute
                // batch run into an answer instead of another guess.
                if (maskMax <= 0.02f)
                {
                    WorldDrainage wd = WorldDrainage.Solve(node, 0, tile,
                                                           Mathf.Clamp(node.solveResolution, 16, 512),
                                                           0f, true, new StopToken());
                    if (wd == null)
                    {
                        Check(false, "RIV-NET3 stage: WorldDrainage.Solve returned NULL - the "
                                   + "node's input could not be pulled through the whole-world view");
                    }
                    else
                    {
                        int draining = 0;
                        float maxAccum = 0f;
                        double slopeSum = 0d;
                        int slopeCount = 0;
                        Vector2 pix = wd.height.PixelSize;
                        for (int i = 0; i < wd.dir.Length; i++)
                        {
                            int d = Hydrology.DownstreamCell(wd.dir, i, wd.resX, wd.resZ);
                            if (d < 0) continue;
                            draining++;
                            if (wd.accum != null && wd.accum[i] > maxAccum) maxAccum = wd.accum[i];

                            float dz = (wd.filled[i] - wd.filled[d]) * wd.height.worldHeight;
                            float run = Mathf.Sqrt(Mathf.Pow(((d % wd.resX) - (i % wd.resX)) * pix.x, 2f)
                                                 + Mathf.Pow(((d / wd.resX) - (i / wd.resX)) * pix.y, 2f));
                            if (run > 1e-4f && dz > 0f) { slopeSum += dz / run; slopeCount++; }
                        }

                        // Probe A: every gate open. If this is still empty, the tracing
                        // itself is broken rather than any threshold.
                        RiverNetworkOptions open = new RiverNetworkOptions();
                        open.minDrainageAreaM2 = 0f;
                        open.slopeExponent = 0f;
                        open.slopeAreaThreshold = 0f;
                        open.minReachCells = 1;
                        RiverNetwork openNet = RiverNetworkExtractor.Extract(wd, open, null, new StopToken());

                        // Probe B: exactly what the node resolves for this world.
                        RiverNetworkOptions real = new RiverNetworkOptions();
                        real.minDrainageAreaM2 = Mathf.Max(wd.cellArea,
                                                           Mathf.Clamp(node.channelInitiationFraction, 0.0002f, 0.2f)
                                                           * wd.dir.Length * wd.cellArea);
                        real.slopeExponent = Mathf.Max(0f, node.slopeExponent);
                        real.slopeAreaThreshold = Mathf.Max(0f, node.slopeAreaThreshold);
                        real.scaleThresholdToWorld = node.scaleToWorld;
                        real.minReachCells = Mathf.Max(1, node.minReachCells);
                        RiverNetwork realNet = RiverNetworkExtractor.Extract(wd, real, null, new StopToken());

                        int openCells = 0;
                        if (openNet != null)
                            for (int r = 0; r < openNet.reaches.Count; r++) openCells += openNet.reaches[r].cells.Count;
                        int realCells = 0;
                        if (realNet != null)
                            for (int r = 0; r < realNet.reaches.Count; r++) realCells += realNet.reaches[r].cells.Count;

                        float meanSlope = slopeCount > 0 ? (float)(slopeSum / slopeCount) : 0f;

                        Check(false, "RIV-NET3 stage: solve " + wd.resX + "x" + wd.resZ
                                   + ", " + draining + "/" + wd.dir.Length + " draining, max accum "
                                   + maxAccum.ToString("F1") + " cells, cellArea "
                                   + wd.cellArea.ToString("F1") + " m2, mean slope "
                                   + meanSlope.ToString("F4") + " m/m | every-gate-open extract: "
                                   + (openNet == null ? "NULL" : openNet.reaches.Count.ToString()
                                      + " reaches (" + openCells + " cells)") + " | node's own "
                                   + "minArea " + real.minDrainageAreaM2.ToString("F0")
                                   + " scaleC " + (real.scaleThresholdToWorld ? "on" : "off") + ": "
                                   + (realNet == null ? "NULL" : realNet.reaches.Count.ToString()
                                      + " reaches (" + realCells + " cells)"));
                    }
                }

                Check(maskMax > 0.02f,
                      "RIV-NET3: the River Mask is empty on this chunk (max "
                      + maskMax.ToString("F4") + ") - the network never reached the world, "
                      + "which is the author's 'trenches and network not spawning'");

                // PER-PIXEL drop, not (min of plain - min of carved). The lowest pixel of
                // this world is the uncut ocean row, so two global minima are equal even
                // when a trench runs right down the middle - the earlier form reported 0
                // for a perfectly carved world.
                float maxDrop = 0f;
                int lowered = 0;
                int n = Mathf.Min(plain.arr.Length, carved.arr.Length);
                for (int i = 0; i < n; i++)
                {
                    float drop = plain.arr[i] - carved.arr[i];
                    if (drop <= 0f) continue;
                    lowered++;
                    if (drop > maxDrop) maxDrop = drop;
                }

                const float oneStep = 1f / 65535f;   // one 16-bit heightmap step, normalised

                Check(lowered > 0,
                      "RIV-NET3: the Height output is identical to the input - the node "
                      + "carved nothing into this chunk");
                Check(maxDrop > oneStep * 20f,
                      "RIV-NET3: the deepest cut is " + (maxDrop / oneStep).ToString("0")
                      + " heightmap steps over " + lowered + " pixels, too shallow to survive "
                      + "being written to a 16-bit terrain");

                // UC-T03-LAKES / UC-T01-07: exercise lake edits through retained and native outputs.
                CheckRiverLakeInvalidation(graph, node);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void CheckRiverLakeInvalidation(Graph graph, RiverNetworkNode river)
        {
            var output = new UniverseCreator.Nodes.Outputs.HeightOutputNode();
            graph.nodes.Add(output);
            graph.AddConnection(new Connection(river.id, 0, output.id, 0));
            var retained = new Dictionary<string, RetainedProduct>();
            var lake = new UniverseCreator.Nodes.Outputs.HeightOutputNode.LakeFootprint
            {
                planBounds = new Bounds(new Vector3(2048f, 0f, 2048f), new Vector3(4352f, 0f, 4352f)),
                ringXZ = new[] { new Vector2(-128f, -128f), new Vector2(4224f, -128f),
                                new Vector2(4224f, 4224f), new Vector2(-128f, 4224f) }
            };
            var lakes = new List<UniverseCreator.Nodes.Outputs.HeightOutputNode.LakeFootprint> { lake };
            string authoredJson = JsonUtility.ToJson(river);
            Check(river.SetLakeFootprints(lakes), "UC-T03-LAKES: first footprint changes the river input");
            output.SetLakeFootprints(lakes);
            Matrix coveredHeight, coveredMask;
            BakeRiverLakeFixture(graph, river, output, retained, out coveredHeight, out coveredMask);
            ulong coveredHash = river.contentHash;
            ulong coveredOutputHash = output.contentHash;
            Check(!river.SetLakeFootprints(lakes), "UC-T03-LAKES: unchanged footprint is not dirty");
            Matrix reusedHeight, reusedMask;
            BakeRiverLakeFixture(graph, river, output, retained, out reusedHeight, out reusedMask);
            Check(ReferenceEquals(coveredHeight, reusedHeight) && ReferenceEquals(coveredMask, reusedMask),
                  "UC-T03-LAKES: unchanged height and mask reuse retained products");
            bool emptyMask = coveredMask != null;
            if (emptyMask)
                for (int i = 0; i < coveredMask.arr.Length; i++)
                    if (coveredMask.arr[i] != 0f) { emptyMask = false; break; }
            Check(emptyMask, "UC-T03-LAKES: a covering lake suppresses procedural channels");

            // Same vertex count and bounds; only the actual polygon changes. The former
            // count/centre/X-size signature missed this edit, including in-place edits.
            lake.ringXZ = new[] { new Vector2(2048f, -128f), new Vector2(4224f, 2048f),
                                 new Vector2(2048f, 4224f), new Vector2(-128f, 2048f) };
            Check(river.SetLakeFootprints(lakes), "UC-T03-LAKES: same-bounds outline edit marks dirty");
            Matrix editedHeight, editedMask;
            TileData edited = BakeRiverLakeFixture(graph, river, output, retained, out editedHeight, out editedMask);
            Check(river.contentHash != coveredHash && output.contentHash != coveredOutputHash,
                  "UC-T03-LAKES: lake outline invalidates river and downstream height output");
            Matrix coldHeight, coldMask;
            BakeRiverLakeFixture(graph, river, output, null, out coldHeight, out coldMask);
            CheckCacheFieldsMatch(editedHeight, coldHeight, "UC-T03-LAKES: retained height equals cold after outline edit");
            CheckCacheFieldsMatch(editedMask, coldMask, "UC-T03-LAKES: retained mask equals cold after outline edit");
            CheckRiverFixtureCut(coveredHeight, editedHeight,
                                 "UC-T03-LAKES: outline edit restores measurable channels outside the lake");

            TerrainData native = new TerrainData();
            try
            {
                native.heightmapResolution = 65;
                native.size = new Vector3(4096f, 600f, 4096f);
                TerrainBuilder.ApplyOutputs(native, edited.outputs, null);
                float[,] readback = native.GetHeights(0, 0, 65, 65);
                bool equal = edited.outputs.heightmap != null;
                float cut = 0f;
                for (int z = 0; z < 65; z++)
                    for (int x = 0; x < 65; x++)
                    {
                        if (equal && Mathf.Abs(readback[z, x] - edited.outputs.heightmap[z, x]) > 2f / 65535f)
                            equal = false;
                        cut = Mathf.Max(cut, coveredHeight.arr[z * 65 + x] - readback[z, x]);
                    }
                Check(equal, "UC-T03-LAKES: final Height Output survives native terrain commit within quantization");
                Check(cut > 20f / 65535f, "UC-T03-LAKES: edited lake exposes a measurable native terrain trench");
            }
            finally { UnityEngine.Object.DestroyImmediate(native); }

            lake.ringXZ[0] += new Vector2(0.01f, 0f);
            Check(river.SetLakeFootprints(lakes), "UC-T03-LAKES: sub-quantization vertex edit marks dirty");
            GraphContentHasher.ComputeHashes(graph);
            ulong beforeBounds = river.contentHash;
            lake.planBounds = new Bounds(lake.planBounds.center, lake.planBounds.size + new Vector3(0f, 0f, 1f));
            Check(river.SetLakeFootprints(lakes), "UC-T03-LAKES: Z bounds edit marks dirty");
            GraphContentHasher.ComputeHashes(graph);
            Check(river.contentHash != beforeBounds, "UC-T03-LAKES: Z bounds participate in dependency hashing");
            Check(JsonUtility.ToJson(river) == authoredJson, "UC-T03-LAKES: scene footprints stay out of authored node JSON");

            // Isolate Height Output's own scene dependency from the river dependency.
            ulong beforeOutputOnlyEdit = output.contentHash;
            output.SetLakeFootprints(null);
            GraphContentHasher.ComputeHashes(graph);
            Check(output.contentHash != beforeOutputOnlyEdit,
                  "UC-T03-LAKES: Height Output hashes its own lake exemption inputs");
            Check(river.SetLakeFootprints(null), "UC-T03-LAKES: removing lake exclusions marks dirty");
            Matrix restoredHeight, restoredMask;
            BakeRiverLakeFixture(graph, river, output, retained, out restoredHeight, out restoredMask);
            BakeRiverLakeFixture(graph, river, output, null, out coldHeight, out coldMask);
            CheckCacheFieldsMatch(restoredHeight, coldHeight, "UC-T03-LAKES: removed-lake height matches cold regeneration");
            CheckCacheFieldsMatch(restoredMask, coldMask, "UC-T03-LAKES: removed-lake mask matches cold regeneration");
            CheckRiverFixtureCut(coveredHeight, restoredHeight, "UC-T03-LAKES: removing lakes restores trenches");
            ulong emptyHash = river.contentHash;
            Check(!river.SetLakeFootprints(new List<UniverseCreator.Nodes.Outputs.HeightOutputNode.LakeFootprint>()),
                  "UC-T03-LAKES: null and empty footprint lists have equivalent dirty state");
            GraphContentHasher.ComputeHashes(graph);
            Check(river.contentHash == emptyHash, "UC-T03-LAKES: null and empty footprint hashes agree");
        }

        private static TileData BakeRiverLakeFixture(Graph graph, RiverNetworkNode river,
            UniverseCreator.Nodes.Outputs.HeightOutputNode output, Dictionary<string, RetainedProduct> retained,
            out Matrix height, out Matrix mask)
        {
            GraphContentHasher.ComputeHashes(graph);
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 65, 65), Vector3.zero,
                                    new Vector3(4096f, 600f, 4096f), 600f);
            tile.retained = retained;
            var evaluator = new GraphEvaluator(graph);
            tile.Bind(evaluator);
            height = evaluator.PullNode(river, 0, tile, new StopToken()) as Matrix;
            mask = evaluator.PullNode(river, 1, tile, new StopToken()) as Matrix;
            evaluator.PullNode(output, 0, tile, new StopToken());
            return tile;
        }

        private static void CheckRiverFixtureCut(Matrix uncut, Matrix cut, string label)
        {
            float maxDrop = 0f;
            bool deepensOnly = uncut != null && cut != null && uncut.arr.Length == cut.arr.Length;
            if (deepensOnly)
                for (int i = 0; i < cut.arr.Length; i++)
                {
                    float drop = uncut.arr[i] - cut.arr[i];
                    if (float.IsNaN(drop) || drop < -1e-6f) deepensOnly = false;
                    maxDrop = Mathf.Max(maxDrop, drop);
                }
            Check(deepensOnly && maxDrop > 20f / 65535f, label);
        }

        /// <summary>
        /// VEG-W1: the scatter water exclusion (ScatterNodeBase.WaterRejects).
        ///
        /// Exists because the guard it protects was found UNWIRED on six of eight scatter
        /// paths (TreeScatter, ShrubScatter, GrassDetailScatter, RockScatter, ObjectScatter,
        /// DebrisScatter had zero water-gate references), which is why trees appeared in the
        /// author's ocean. Having fixed that in the shared solver, this pins the decision so
        /// it cannot silently invert: HydroField's distance is NEGATIVE OR ZERO inside a
        /// water body and POSITIVE on dry land, and reading that sign backwards would plant
        /// only in the sea while clearing the land - a failure no compile can catch.
        /// </summary>
        private static void TestScatterWaterExclusion()
        {
            // Inside the water (distance <= 0): a land type is always rejected.
            Check(ScatterNodeBase.WaterRejects(-5f, 0f, false, true),
                  "VEG-W1: a land plant 5 m INSIDE a water body was admitted");
            Check(ScatterNodeBase.WaterRejects(0f, 0f, false, true),
                  "VEG-W1: a land plant exactly ON the waterline was admitted");

            // Dry land with no clearance asked for: admitted right to the water's edge.
            Check(!ScatterNodeBase.WaterRejects(0.5f, 0f, false, true),
                  "VEG-W1: with clearance 0 a plant just inland of the waterline was rejected "
                  + "- refusing to plant IN water must not clear the whole beach");

            // The shore strip: rejected inside it, admitted beyond it.
            Check(ScatterNodeBase.WaterRejects(4f, 10f, false, true),
                  "VEG-W1: a plant 4 m from the water survived a 10 m shore clearance");
            Check(!ScatterNodeBase.WaterRejects(12f, 10f, false, true),
                  "VEG-W1: a plant 12 m from the water was rejected by a 10 m clearance");

            // An aquatic type opts IN: it belongs in water and is never rejected there.
            Check(!ScatterNodeBase.WaterRejects(-5f, 0f, true, true),
                  "VEG-W1: an AQUATIC type was excluded from water - marine and lake "
                  + "planting must still be possible");

            // The author's switch off disables the whole exclusion, even under water.
            Check(!ScatterNodeBase.WaterRejects(-5f, 10f, false, false),
                  "VEG-W1: avoidWater = false still rejected a submerged position");
        }

        /// <summary>
        /// RF1: River Fill wets the CHANNEL and leaves the dry bank alone (RiverFillNode.IsWet).
        /// Pins the one decision a compile cannot: if the channel test or the depth floor were
        /// read backwards the node would fill the levees and leave the river empty. Open water
        /// needs BOTH a real channel strength and standing depth above the bed.
        /// </summary>
        private static void TestRiverFillWetsChannelNotBank()
        {
            const float floor = 0.0015f;
            const float chanMin = 0.02f;

            // In the channel, deep enough: wet.
            Check(RiverFillNode.IsWet(0.01f, 0.6f, floor, chanMin),
                  "RF1: a deep cell in the channel was left dry");

            // On the floodplain / dry bank (below the channel threshold): never wet, however
            // deep the filled surface reads - that is the bank, not the river.
            Check(!RiverFillNode.IsWet(0.01f, 0.0f, floor, chanMin),
                  "RF1: a cell OFF the channel was filled - the river must not flood its banks");
            Check(!RiverFillNode.IsWet(0.01f, 0.01f, floor, chanMin),
                  "RF1: a sub-threshold channel strength was treated as open water");

            // In the channel but shallower than the floor: dry (a damp bed, not open water).
            Check(!RiverFillNode.IsWet(0.0005f, 0.6f, floor, chanMin),
                  "RF1: water shallower than the floor was reported as open water");
        }

        /// <summary>
        /// RCI1 (N2): River Course Input turns a hand-drawn river into a graph River Mask so Banks,
        /// Water Mask and the scatter gate can see it, and passes Height through (no re-carve - the
        /// manual river carves and fills itself). Pins the mask geometry/sign (RiverCourseInputNode.MaskAt)
        /// and the port contract that lets a manual river feed the same tail as River Network.
        /// </summary>
        private static void TestRiverCourseInputMaskWire()
        {
            var node = new UniverseCreator.Nodes.Creators.RiverCourseInputNode();
            Check(node.NodeName == "River Course Input", "RCI1: node is named River Course Input");

            Port[] outs = node.GetOutputs();
            Check(outs.Length == 2 && outs[0].name == "Height" && outs[1].name == "River Mask",
                "RCI1: outputs are Height + River Mask, mirroring River Network");
            Check(outs[1].allowMultiple,
                "RCI1: River Mask fans out (drives Banks AND Water Mask AND scatter at once)");

            // A straight reach along X at z=0, half-width 5 m. Feather 2 m.
            var course = new UniverseCreator.Nodes.Creators.RiverCourseInputNode.ManualRiverCourse
            {
                pointsXZ = new Vector2[] { new Vector2(0f, 0f), new Vector2(100f, 0f) },
                halfWidthM = new float[] { 5f, 5f }
            };
            var courses = new System.Collections.Generic.List<
                UniverseCreator.Nodes.Creators.RiverCourseInputNode.ManualRiverCourse> { course };
            const float feather = 2f;

            CheckClose(UniverseCreator.Nodes.Creators.RiverCourseInputNode.MaskAt(50f, 0f, courses, feather), 1f,
                "RCI1: the channel centreline is fully wet");
            CheckClose(UniverseCreator.Nodes.Creators.RiverCourseInputNode.MaskAt(50f, 4.9f, courses, feather), 1f,
                "RCI1: a point inside the half-width reads full");
            CheckClose(UniverseCreator.Nodes.Creators.RiverCourseInputNode.MaskAt(50f, 20f, courses, feather), 0f,
                "RCI1: well outside the channel is dry");

            float edge = UniverseCreator.Nodes.Creators.RiverCourseInputNode.MaskAt(50f, 6f, courses, feather);
            Check(edge > 0f && edge < 1f,
                "RCI1: the feathered bank edge is a soft 0..1 band, not a one-pixel line");

            CheckClose(UniverseCreator.Nodes.Creators.RiverCourseInputNode.MaskAt(200f, 0f, courses, feather), 0f,
                "RCI1: past the ends of the drawn course there is no channel");

            CheckClose(UniverseCreator.Nodes.Creators.RiverCourseInputNode.MaskAt(50f, 0f,
                new System.Collections.Generic.List<
                    UniverseCreator.Nodes.Creators.RiverCourseInputNode.ManualRiverCourse>(), feather), 0f,
                "RCI1: with no manual river pushed the mask is empty (inert, never wrong)");
        }

        /// <summary>
        /// WM1 (N3): Water Mask is the UNION of its optional inputs, and a missing input
        /// reads as DRY, never as water everywhere. Pins WaterMaskNode.Union.
        /// </summary>
        private static void TestWaterMaskUnionOfOptionalInputs()
        {
            Matrix river = new Matrix(new CoordRect(0, 0, 2, 2));
            Matrix ocean = new Matrix(new CoordRect(0, 0, 2, 2));
            river.arr[0] = 0.3f; river.arr[1] = 0.9f;
            ocean.arr[0] = 0.7f; ocean.arr[1] = 0.2f;

            // Union takes the maximum presence across the wired masks (lake left null).
            Check(Mathf.Abs(WaterMaskNode.Union(river, null, ocean, 0) - 0.7f) < 1e-5f,
                  "WM1: the union did not take the deeper of two overlapping bodies");
            Check(Mathf.Abs(WaterMaskNode.Union(river, null, ocean, 1) - 0.9f) < 1e-5f,
                  "WM1: the union dropped the stronger single-body mask");

            // All inputs null = a dry world, never water everywhere.
            Check(WaterMaskNode.Union(null, null, null, 0) == 0f,
                  "WM1: an all-unwired Water Mask reported water (must read dry)");
        }

        /// <summary>
        /// ST1 (N4): Slope To Density is flat-favouring by default and invert flips it.
        /// Pins SlopeToDensityNode.BuildBand so the slope->density curve cannot read upside
        /// down (dense cliffs, bare flats).
        /// </summary>
        private static void TestSlopeToDensityBandOrientation()
        {
            RangeFilter flatFav = SlopeToDensityNode.BuildBand(0f, 25f, 8f, false, false);
            Check(flatFav.Evaluate(0f) > 0.99f,
                  "ST1: flat ground did not score full density under a flat-favouring band");
            Check(flatFav.Evaluate(60f) < 1e-3f,
                  "ST1: a 60-degree cliff scored density under a flat-favouring band");

            RangeFilter steepFav = SlopeToDensityNode.BuildBand(0f, 25f, 8f, false, true);
            Check(steepFav.Evaluate(0f) < 1e-3f && steepFav.Evaluate(60f) > 0.99f,
                  "ST1: invert did not swap the slope band (steep should score, flat should not)");
        }

        /// <summary>
        /// CANOPY1 (§4O item 3): a deposited crown actually lands in the canopy grid and is
        /// read back as shade. Pins the cross-layer occlusion WIRE that was dead - the grid
        /// was never bound and never deposited into, so TotalLai was a flat zero and tree
        /// shade never reached the undergrowth. Under the crown the leaf area must be
        /// positive; well outside it, still zero (shade is local to the tree).
        /// </summary>
        private static void TestCanopyDepositIsReadBackAsShade()
        {
            var grid = new UniverseCreator.Ecology.CanopyLightGrid();
            grid.Bind(0f, 0f, 100f, 100f);
            Check(grid.IsBound, "CANOPY1: the canopy grid did not bind");

            // Baseline: an empty grid is full daylight (zero leaf area) everywhere.
            Check(grid.TotalLai(50f, 50f) <= 1e-6f,
                  "CANOPY1: an empty canopy grid already reported shade");

            grid.Deposit(50f, 50f, 8f, 3f, UniverseCreator.Ecology.CanopyLayer.Canopy);

            Check(grid.TotalLai(50f, 50f) > 0.01f,
                  "CANOPY1: a crown deposited at a point cast NO shade there - the deposit "
                  + "did not reach the grid (the cross-layer occlusion wire is still dead)");
            Check(grid.TotalLai(5f, 5f) <= 1e-6f,
                  "CANOPY1: a crown shaded a point far outside its radius - shade must be local");
        }

        /// <summary>
        /// AGE1 (§4O item 4): age-driven size variation. Off (0) leaves every plant mature
        /// (×1, so existing worlds are untouched); on, the youngest sit at the seedling
        /// fraction and size rises with the age draw, giving a stand its spread of generations.
        /// Pins SpeciesAssignment.AgeSizeMultiplier.
        /// </summary>
        private static void TestAgeSizeVariationSpreadsAStand()
        {
            // Off: no variation, whatever the draw.
            Check(Mathf.Approximately(SpeciesAssignment.AgeSizeMultiplier(0f, 0.2f, 0f), 1f)
               && Mathf.Approximately(SpeciesAssignment.AgeSizeMultiplier(0f, 0.2f, 1f), 1f),
                  "AGE1: age variation OFF still rescaled a plant - existing worlds must be untouched");

            // Full variation: youngest draw → seedling fraction, oldest → mature (1).
            float young = SpeciesAssignment.AgeSizeMultiplier(1f, 0.2f, 0f);
            float old = SpeciesAssignment.AgeSizeMultiplier(1f, 0.2f, 1f);
            Check(Mathf.Abs(young - 0.2f) < 1e-4f,
                  "AGE1: the youngest plant was not the seedling fraction (" + young.ToString("F3") + ")");
            Check(Mathf.Abs(old - 1f) < 1e-4f,
                  "AGE1: the oldest plant was not full mature size (" + old.ToString("F3") + ")");

            // Monotonic: an older draw is never smaller than a younger one.
            Check(SpeciesAssignment.AgeSizeMultiplier(1f, 0.2f, 0.7f)
                > SpeciesAssignment.AgeSizeMultiplier(1f, 0.2f, 0.3f),
                  "AGE1: size did not grow with age (the draw is not monotonic)");

            // Partial strength blends toward 1: half variation on the youngest = (1+0.2)/2.
            Check(Mathf.Abs(SpeciesAssignment.AgeSizeMultiplier(0.5f, 0.2f, 0f) - 0.6f) < 1e-4f,
                  "AGE1: partial age-variation strength did not blend toward mature size");
        }

        /// <summary>
        /// CULL1 (§4O item 5): per-prototype LOD cull distance. A prototype can state a metre
        /// range past which its whole instanced batch is skipped (not merely shadow-stripped);
        /// an unauthored prototype resolves to -1 so GPUInstancingManager keeps drawing it at
        /// every distance - the behaviour a world that never set a cull distance must keep.
        /// Pins EcoEnvelope.ResolveCullDistanceM, which the ScatterApplier feeds the manager.
        /// </summary>
        private static void TestPrototypeCullDistanceResolves()
        {
            var eco = new UniverseCreator.Ecology.EcoEnvelope();

            // Default: never authored -> -1 -> the manager's "do not cull" sentinel.
            Check(eco.ResolveCullDistanceM() < 0f,
                  "CULL1: an unauthored prototype did not resolve to -1 (never-cull) - "
                  + "an existing world would start culling instances it used to draw");

            // Authored positive distance passes straight through.
            eco.cullDistanceM = 140f;
            Check(Mathf.Abs(eco.ResolveCullDistanceM() - 140f) < 1e-4f,
                  "CULL1: an authored cull distance of 140 did not survive resolution");

            // Zero or negative is treated as unset, not as "cull everything at 0m".
            eco.cullDistanceM = 0f;
            Check(eco.ResolveCullDistanceM() < 0f,
                  "CULL1: a cull distance of 0 was taken literally - it must mean unset, or a "
                  + "prototype would vanish the instant the camera is any distance away");
        }
    }
}
