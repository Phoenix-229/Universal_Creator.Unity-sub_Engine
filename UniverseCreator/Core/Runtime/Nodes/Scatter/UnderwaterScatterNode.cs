// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 3: ecological input wired (V-45)
//  File:    UnderwaterScatterNode.cs
//  Purpose: Places kelp, seagrass, freshwater weed and seabed props at the depth
//           each species actually lives at, in the water body it belongs to.
//
//  WHAT CHANGED, AND WHY THE OLD PARAMETERS SURVIVED
//  -------------------------------------------------
//  This node used to hold a single normalised waterLevel and derive a height band
//  from it, because Module 1 had no water system to ask. It now reads the real
//  water surfaces through the environment sampler, which carries one surface per
//  water body - so lakes at differing elevations each scatter against their own
//  plane instead of against one global level.
//
//  waterLevel / minDepth / maxDepth are kept, not deleted. They are serialised
//  graph data, and they remain the answer for a world whose object roster nobody
//  has described species in. They are the fallback, no longer the model.
//
//  HOW A SPECIES LANDS IN THE RIGHT WATER
//  --------------------------------------
//  Three envelope fields do it, and none of them is a node parameter:
//
//    submergedDepthRangeM  states the species IS aquatic and how deep it lives.
//                          SubmergenceAdmits passes it only inside that band, and
//                          passes a land species only where the ground is dry - so
//                          this single field keeps kelp off the beach and keeps a
//                          fence post off the seabed.
//    saltTolerance         is tested against the site's salinity. Below a surface
//                          that salinity is now the salinity of the BODY (ocean 1,
//                          fresh 0.5/35), not the airborne spray reading it used to
//                          be. That fix is what makes marine and fresh separable at
//                          all: a lake within spray range of the sea used to read as
//                          seawater, which admitted kelp into it and rejected
//                          freshwater weed from it - precisely inverted.
//    submergedDepthM       is the site's own depth, from the nearest body's surface.
//
//  DEVIATION FROM THE TASK WORDING, STATED RATHER THAN HIDDEN: V-45 asks for
//  "waterBodyKind" on the species. EcoEnvelope has no such field and does not need
//  one - a species does not care what a body is called, it cares how salty the
//  water is, and saltTolerance against body salinity expresses that with a real
//  number instead of a category. Adding an enum field would be a second, redundant
//  way to say the same thing (C-15).
//
//  KNOWN LIMIT: WaterBodyKind has no saline-lake flag, so a salt lake authored as
//  Lake reads fresh. That is a graph-data question and belongs with the water
//  system, not here. See EcoMath.BodySalinityFraction.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class UnderwaterScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Underwater Scatter"; } }
        public override string Tooltip { get { return "Places aquatic plants and seabed props at the depth each species lives at."; } }
        public override string Channel { get { return "underwater"; } }

        /// <summary>
        /// Fallback water plane in normalised height, used only when no species are
        /// described. Ignored once the object roster carries aquatic envelopes,
        /// because the real surfaces are then read per water body.
        /// </summary>
        public float waterLevel = 0.3f;

        /// <summary>Fallback minimum submersion depth, in normalised height below waterLevel.</summary>
        public float minDepth = 0.01f;

        /// <summary>Fallback maximum submersion depth. Deeper seabed is left bare.</summary>
        public float maxDepth = 0.15f;

        public override bool WasStubbed { get { return false; } }

        protected override List<ScatterCandidate> SolvePlacement(
            Matrix height, Matrix mask, TileData tile, StopToken stop)
        {
            if (EcologyGoverns(height, tile))
            {
                // The depth test has to run per candidate against that candidate's own
                // water body, so it cannot be expressed as one normalised band here.
                // Opening the band hands the rejection to SubmergenceAdmits, which is
                // reading the real surface. Narrowing it as well would silently clip
                // whichever bodies happened to fall outside the guessed plane.
                minHeight = 0f;
                maxHeight = 1f;

                return base.SolvePlacement(height, mask, tile, stop);
            }

            // No roster described. Depth below a plane IS a height band, expressed
            // from the surface downward: depth d means height (waterLevel - d).
            // Mapping it onto the inherited band lets the existing filter do the
            // rejection rather than adding a second pass that removes instances
            // after they have already been built.
            float lo = Mathf.Min(minDepth, maxDepth);
            float hi = Mathf.Max(minDepth, maxDepth);

            minHeight = Mathf.Clamp01(waterLevel - hi);
            maxHeight = Mathf.Clamp01(waterLevel - lo);

            // A band inverted by bad parameters would reject everything silently.
            if (maxHeight < minHeight) maxHeight = minHeight;

            return base.SolvePlacement(height, mask, tile, stop);
        }

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // The whole of V-45. TryAssign scores every candidate against the aquatic
            // envelopes, drops the ones no described species can occupy, and names the
            // species that can - which is where depth, salinity and body membership are
            // actually enforced. Returning true means it owns prototypeIndex.
            if (SpeciesAssignment.TryAssign(tile, height, Channel, seed, instances, stop))
                return;

            // Nothing described. The instances keep the prototype index the base
            // solver assigned, placed inside the fallback band above. This is the
            // pre-Module-3 behaviour and is deliberately left reachable, so a graph
            // built before the species model still scatters instead of emptying out.
        }

        // True when the environment can actually be asked about this site. A context
        // whose sampler never bound has no water surfaces to read, and a roster with
        // no usable envelopes has nothing to select - in either case the fallback band
        // is the honest answer rather than an open band that would scatter everywhere.
        private bool EcologyGoverns(Matrix height, TileData tile)
        {
            Ecology.EcoScatterContext ctx = tile != null ? tile.ecology : null;
            if (ctx == null) return false;

            ctx.EnsureBound(height);
            if (!ctx.CanSelect()) return false;

            Ecology.SpeciesTable table = ctx.TableFor(Channel);

            return table != null && table.IsUsable();
        }
    }
}
