// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RiverRouteValidator.cs
//  Purpose: Step 5 of the river rebuild - the drainage guarantee (B), manual-first.
//           The human draws the course; the system does NOT auto-route it. Instead
//           it VALIDATES what was drawn and surfaces honest findings the author can
//           act on, so a bad route is caught at authoring time rather than built
//           into a world that then looks wrong.
//
//  WHAT IT CHECKS (and what it deliberately does not)
//  --------------------------------------------------
//   * Drawn uphill - the mouth's ground sits at or above the head's. The step-4
//     solver already forces the bed to descend, so the river still flows, but the
//     author almost certainly meant the course to run the other way.
//   * Mouth not at water - the course's end is not within reach of any lake or
//     ocean, so the river ends in dry land (the orphaned-mouth failure). The actual
//     geometric weld onto the body it reaches is step 7; this only reports whether
//     a body is in range to weld to.
//   * Deep cut - the monotonic bed had to cut far below the ground to keep
//     descending (a hill the course crosses head-on). Not an error - the river is
//     valid - but the author may prefer to route around the hill.
//
//  Closed-basin SILL BREACHING is not a separate pass here: the step-4 monotonic
//  thalweg already cuts the bed through any sill along the course (the descending
//  line is always at or below the ground), so a drawn course cannot leave an
//  un-drainable trap on its own path. There is nothing left for a breach step to do
//  that the profile has not already done - adding one would duplicate it.
//
//  Pure managed C#: geometry over arrays, no Unity scene access, exercised headless.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>A lake/ocean the river mouth might reach: its plan centre and how far its edge reaches.</summary>
    public struct WaterJoinTarget
    {
        public Vector2 centreXZ;
        public float reachM;
    }

    public enum RiverRouteIssue
    {
        DrawnUphill,
        MouthNotAtWater,
        BedCutsDeep,
        TooFewNodes
    }

    /// <summary>One thing the validator wants the author to know about a drawn course.</summary>
    public struct RiverRouteFinding
    {
        public RiverRouteIssue issue;
        public int nodeIndex;      // -1 when it is about the whole course
        public string message;
    }

    /// <summary>Validates a drawn river course against the drainage guarantee (B).</summary>
    public static class RiverRouteValidator
    {
        /// <summary>A mouth within this slack of a body's reach counts as touching it.</summary>
        public const float DefaultMouthJoinSlackM = 5f;

        /// <summary>Bed-below-ground beyond this is reported as a deep cut (metres).</summary>
        public const float DeepCutWarnM = 25f;

        /// <summary>
        /// Returns every finding for a drawn course. <paramref name="courseWorld"/> are
        /// the nodes (Y = ground under each), <paramref name="bed"/> the step-4 solve,
        /// and <paramref name="targets"/> the lakes/oceans the mouth could join (empty =
        /// "no body known", in which case mouth proximity is not judged rather than
        /// wrongly flagged). <paramref name="mouthAtWater"/> is set true when the mouth
        /// reaches a target.
        /// </summary>
        public static List<RiverRouteFinding> Validate(IList<Vector3> courseWorld,
                                                       SolvedRiverBed bed,
                                                       IList<WaterJoinTarget> targets,
                                                       out bool mouthAtWater)
        {
            List<RiverRouteFinding> findings = new List<RiverRouteFinding>();
            mouthAtWater = false;

            if (courseWorld == null || courseWorld.Count < 2)
            {
                findings.Add(new RiverRouteFinding
                {
                    issue = RiverRouteIssue.TooFewNodes,
                    nodeIndex = -1,
                    message = "A river needs at least two course nodes to be a channel."
                });
                return findings;
            }

            int n = courseWorld.Count;

            if (bed.valid && bed.drawnUphill)
            {
                findings.Add(new RiverRouteFinding
                {
                    issue = RiverRouteIssue.DrawnUphill,
                    nodeIndex = n - 1,
                    message = "The mouth's ground is at or above the head's: the course was drawn uphill. " +
                              "The bed is forced to descend anyway, but you may have meant to draw it the other way."
                });
            }

            // Mouth proximity to a known body. Only judged when targets exist, so a
            // river authored before its lake is not wrongly flagged.
            if (targets != null && targets.Count > 0)
            {
                Vector3 mouth = courseWorld[n - 1];
                Vector2 mouthXZ = new Vector2(mouth.x, mouth.z);
                for (int i = 0; i < targets.Count; i++)
                {
                    float d = Vector2.Distance(mouthXZ, targets[i].centreXZ);
                    if (d <= targets[i].reachM + DefaultMouthJoinSlackM) { mouthAtWater = true; break; }
                }

                if (!mouthAtWater)
                {
                    findings.Add(new RiverRouteFinding
                    {
                        issue = RiverRouteIssue.MouthNotAtWater,
                        nodeIndex = n - 1,
                        message = "The mouth does not reach any lake or ocean - the river ends in dry land. " +
                                  "Extend the course to a water body, or one will not be welded on at step 7."
                    });
                }
            }

            // Deep cut: where the descending bed sits far below the ground it crosses.
            if (bed.valid && bed.bedElevationM != null && bed.bedElevationM.Length == n)
            {
                for (int i = 0; i < n; i++)
                {
                    float cut = courseWorld[i].y - bed.bedElevationM[i];
                    if (cut > DeepCutWarnM)
                    {
                        findings.Add(new RiverRouteFinding
                        {
                            issue = RiverRouteIssue.BedCutsDeep,
                            nodeIndex = i,
                            message = "Node " + i + ": the bed cuts " + Mathf.RoundToInt(cut) +
                                      " m below the ground to keep descending. The river is valid, but you " +
                                      "may prefer to route around the high ground here."
                        });
                    }
                }
            }

            return findings;
        }
    }
}
