// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    PlacementClearance.cs
//  Purpose: The two exclusion gates that keep vegetation out of places it has no
//           business being: open water (V-22) and ground the road bridge has cut
//           (V-23). Both settings shipped inert; this is where they become live.
//
//  WHY THESE ARE GATES AND NOT SUITABILITY TERMS. Suitability answers "how well
//  does this species do here", and it is graded - 0.3 is a real answer. These two
//  questions are not graded. A tree is not slightly in the middle of a road. It is
//  either on the carriageway or it is not, and the answer has nothing to do with
//  how much the tree would have liked the soil there. Folding them into the score
//  would let a highly suitable species buy its way onto the tarmac.
//
//  THE SHORE BAND IS ALREADY CONTINUOUS, AND THAT IS NOT AN ACCIDENT. V-22 asks
//  for a shore band that does not break at chunk boundaries. It does not, because
//  the water probes live on EcoScatterContext at world scope and ForChunk passes
//  the same array to every worker - so a site near a chunk edge measures its
//  distance to the same lake the neighbouring chunk measures to. Had the probes
//  been gathered per chunk, every chunk boundary near water would carry a visible
//  seam, and no amount of care inside this file would have fixed it. Worth stating
//  because it is the kind of correctness that looks like nothing was done.
//
//  A BRIDGE IS NOT A CUT. Cut regions carry a full Bounds, not a footprint, and the
//  vertical extent is honoured: ground buried under a road cutting is excluded, and
//  ground twenty metres below a viaduct is not. Testing in plan view only would
//  strip a valley of trees because a bridge crosses the sky above it, which is the
//  wrong answer in the one case where the distinction is most visible.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// One volume the road system has claimed. A flattened Bounds, because a worker
    /// thread may not read the List the road bridge appends to.
    /// </summary>
    public struct CutRegion
    {
        public float minX, minZ, minY;
        public float maxX, maxZ, maxY;

        public static CutRegion FromBounds(Bounds b)
        {
            CutRegion r;
            Vector3 lo = b.min;
            Vector3 hi = b.max;

            r.minX = lo.x; r.minY = lo.y; r.minZ = lo.z;
            r.maxX = hi.x; r.maxY = hi.y; r.maxZ = hi.z;

            return r;
        }

        /// <summary>
        /// Whether this region claims the given ground point, allowing a margin.
        /// The margin widens the footprint but not the vertical band: a verge is
        /// horizontal, and inflating the height would exclude ground under a bridge
        /// for no reason.
        /// </summary>
        public bool Claims(float worldX, float worldZ, float groundY, float marginM)
        {
            if (worldX < minX - marginM || worldX > maxX + marginM) return false;
            if (worldZ < minZ - marginM || worldZ > maxZ + marginM) return false;

            return groundY >= minY && groundY <= maxY;
        }
    }

    public static class PlacementClearance
    {
        /// <summary>
        /// V-22. Whether open water rules this site out for this species.
        ///
        /// An aquatic species states a depth range and belongs in it. A land species
        /// states none, and its flood tolerance is the only standing water it will
        /// take - which is what separates a willow on a wet bank from a pine that
        /// will not stand it at all. The same test SpeciesSuitability makes, repeated
        /// here because the settings-level avoidWater switch has to be able to
        /// override a species that would otherwise be admitted.
        /// </summary>
        public static bool WaterAdmits(EcoEnvelope eco, in EnvironmentSample site,
                                        bool avoidWater, float clearanceM)
        {
            bool aquatic = eco != null && eco.submergedDepthRangeM.y > 0f;

            if (site.submergedDepthM > 0f)
            {
                // Under water. Only a species that asked to be goes here, and the
                // author's switch cannot put a land plant in a lake but can keep an
                // aquatic one out if the whole feature is off.
                if (!aquatic) return false;

                return !avoidWater
                    && site.submergedDepthM >= eco.submergedDepthRangeM.x
                    && site.submergedDepthM <= eco.submergedDepthRangeM.y;
            }

            // Dry land. An aquatic species is as wrong here as a pine on the seabed.
            if (aquatic) return false;

            if (!avoidWater) return true;

            // The clearance band. Kept clear of the waterline so instances do not
            // intersect the water surface where the ground meets it at a shallow
            // angle - the case where a metre of horizontal error is centimetres of
            // vertical, and a half-submerged trunk is the result.
            if (site.hasWaterInRange
                && site.distanceToWaterM < clearanceM
                && eco != null
                && site.submergedDepthM > eco.floodTolerance)
                return false;

            return true;
        }

        /// <summary>
        /// V-23. Whether any cut region claims this site. Linear in the region count
        /// on purpose: a world has tens of road cuts, not thousands, and a spatial
        /// index over ten boxes costs more to build than it saves.
        /// </summary>
        public static bool CutRegionsAdmit(CutRegion[] regions, int count,
                                            float worldX, float worldZ, float groundY,
                                            bool avoidCutRegions, float clearanceM)
        {
            if (!avoidCutRegions || regions == null || count <= 0) return true;

            if (count > regions.Length) count = regions.Length;

            float margin = Mathf.Max(0f, clearanceM);

            for (int i = 0; i < count; i++)
                if (regions[i].Claims(worldX, worldZ, groundY, margin)) return false;

            return true;
        }

        /// <summary>
        /// T-PLACE. The species-independent half of the placement law: ground too
        /// steep reads as rock and carries no plant of any kind, and the world's
        /// altitude band rules out anything below <c>minAltitudeM</c> or above the
        /// <c>maxAltitudeM</c> backstop. Tested once per candidate BEFORE a species is
        /// chosen, because the answer does not depend on which species would have won -
        /// a maple is no more able to stand on a cliff face than a pine, so asking per
        /// species would be the same rejection computed N times.
        ///
        /// Slope is the shipped meaning of "rock" for vegetation placement
        /// (UCVegetationSystemManager.maxSlopeDegrees: "past this the slope reads as
        /// rock"), which is why this is a slope test and not a control-map read: the
        /// placement pass runs on a worker holding the height field, not the painted
        /// splat. A non-positive cap is read as "no limit" rather than "reject all", so
        /// a settings block that leaves a gate at zero does not empty the world.
        /// </summary>
        public static bool TerrainAdmits(in EcoSampleSettings settings,
                                          in EnvironmentSample site)
        {
            if (settings.maxSlopeDeg > 0f && site.slopeDeg > settings.maxSlopeDeg)
                return false;

            // minAltitudeM defaults to sea level and is a real floor - nothing grows
            // below it, which is also where standing water begins. maxAltitudeM is a
            // backstop above the temperature treeline, so zero means "no ceiling"
            // rather than "nothing grows anywhere".
            if (site.altitudeM < settings.minAltitudeM) return false;
            if (settings.maxAltitudeM > 0f && site.altitudeM > settings.maxAltitudeM)
                return false;

            return true;
        }

        /// <summary>
        /// Both gates in the order that rejects fastest. Cut regions are tested first
        /// because the test is a handful of compares against a short list, where the
        /// water test reads several sample fields.
        ///
        /// <paramref name="groundY"/> is world Y, passed in rather than read off the
        /// sample: the sample carries altitude above sea level, and the two agree only
        /// when sea level sits at the origin. Cut bounds come from the road system in
        /// world space, so world space is what they must be compared against.
        /// </summary>
        public static bool Admits(EcoEnvelope eco, in EnvironmentSample site,
                                   CutRegion[] regions, int regionCount,
                                   float worldX, float worldZ, float groundY,
                                   bool avoidWater, bool avoidCutRegions, float clearanceM)
        {
            if (!CutRegionsAdmit(regions, regionCount, worldX, worldZ,
                                 groundY, avoidCutRegions, clearanceM))
                return false;

            return WaterAdmits(eco, in site, avoidWater, clearanceM);
        }
    }
}
