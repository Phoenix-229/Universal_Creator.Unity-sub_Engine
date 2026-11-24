// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    BareGround.cs
//  Purpose: Produces bare ground on purpose, with a named cause for every square
//           metre of it (V-24).
//
//  THE DIFFERENCE THIS FILE MAKES. Bare ground in a generated world is normally
//  what is left when the vegetation rules all happened to say no. That produces a
//  fade: density trails off, instances get sparser, and there is no edge anywhere -
//  which is why generated mountains look soft at the top and real ones do not. A
//  real bare zone has a cause and therefore a boundary. The snowline is a line. The
//  edge of a scree field is a line. A saline pan has a shore.
//
//  So bare ground is asserted here rather than inferred: eight causes, each with a
//  threshold it is crossed at, and each producing coverage that goes to full over a
//  stated width instead of asymptotically approaching it. The test of whether this
//  worked is whether you can point at the edge and say what put it there.
//
//  WHY THE CAUSE IS RETURNED AND NOT JUST THE FRACTION. Two reasons, both practical.
//  The splat pass needs to know which surface to paint - snow, rock, salt crust and
//  burnt ground are four different materials, and painting soil under all of them is
//  the artefact this task exists to remove. And when a world comes out wrong, "38%
//  bare, cause: RidgeLeeScour" is a diagnosis, where "38% bare" is a symptom.
//
//  FIRE IS THE ONE THAT NEEDED THINKING ABOUT. The other seven causes are readable
//  straight off the environment record. Fire is a history, and section 1.2 forbids
//  simulating one. What is available is fire's climatology, which is genuinely
//  bimodal: fire needs fuel AND dryness, so it is rare in a desert (nothing to burn)
//  and rare in a rainforest (never dry enough) and common in between. That curve is
//  real, it is a function of aridity alone, and a coarse lattice of scars weighted by
//  it gives burnt patches where burnt patches belong - contiguous, of a plausible
//  size, in fire country. What it does not give is a fire that happened at a time,
//  and nothing here pretends otherwise.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Why the ground here is bare. One cause, the strongest one acting, because the
    /// splat pass has to paint one surface.
    /// </summary>
    public enum BareGroundCause
    {
        /// <summary>Not bare.</summary>
        None = 0,

        /// <summary>Too steep to hold soil. Rock, and the commonest cause by area.</summary>
        RockAboveSlopeLimit = 1,

        /// <summary>Snow that never melts. The hardest edge in any landscape.</summary>
        PermanentSnow = 2,

        /// <summary>Evaporite crust in a closed basin. Salt, not soil.</summary>
        SalinePan = 3,

        /// <summary>Burnt over. Ash and standing dead, in fire country.</summary>
        RecentBurn = 4,

        /// <summary>Scoured gravel in a channel that still runs.</summary>
        ActiveChannelBed = 5,

        /// <summary>Wave-scoured rock and sand between the tides.</summary>
        TidalScour = 6,

        /// <summary>Sand blown out of a hollow until it reached the water table.</summary>
        DeflationHollow = 7,

        /// <summary>Stripped by wind on an exposed crest. Why summits are bald.</summary>
        RidgeLeeScour = 8
    }

    public struct BareGroundState
    {
        public BareGroundCause cause;

        /// <summary>0 fully vegetated, 1 fully bare. Vegetation density scales by (1 - this).</summary>
        public float bareFraction;

        public bool IsBare { get { return bareFraction > 0f; } }
    }

    /// <summary>
    /// One chunk's bare ground, at the resolution the caller asks for.
    ///
    /// TWO PARALLEL ARRAYS RATHER THAN NINE MASKS. A mask per cause would be nine
    /// float grids where eight of them are zero over almost all of any real chunk,
    /// which at a 512 control-map resolution is 9 MB of mostly nothing. A coverage
    /// float plus a cause byte carries the same information in a ninth of the memory,
    /// and it enforces the thing that is true anyway: ground has one reason for being
    /// bare, because the splat pass can only paint one surface over it.
    ///
    /// WHY THIS IS SEPARATE FROM THE SPLAT WEIGHTS. The splat pass owns layer indices
    /// and this file must not: how many layers a graph wired, and in what order, is an
    /// authoring decision that changes per world. So the mask states what the ground IS
    /// and the applier decides which of its layers says that. A cause the graph has no
    /// layer for degrades to the rock layer rather than silently painting soil.
    /// </summary>
    public sealed class BareGroundMask
    {
        public int resolution;

        /// <summary>0-1 per cell, row-major, indexed [z * resolution + x].</summary>
        public float[] coverage;

        /// <summary>The cause per cell, as the enum's byte value.</summary>
        public byte[] cause;

        public void Resize(int res)
        {
            if (res < 1) res = 1;

            int n = res * res;
            if (coverage == null || coverage.Length < n)
            {
                coverage = new float[n];
                cause = new byte[n];
            }
            else
            {
                // Reused rather than reallocated, but it must still be cleared: a stale
                // cell from the last chunk is bare ground with no reason behind it, which
                // is precisely the artefact this whole task removes.
                System.Array.Clear(coverage, 0, n);
                System.Array.Clear(cause, 0, n);
            }

            resolution = res;
        }

        public void Write(int x, int z, in BareGroundState state)
        {
            if (coverage == null) return;
            if (x < 0 || z < 0 || x >= resolution || z >= resolution) return;

            int i = z * resolution + x;

            // Strongest cause wins the cell. Two causes can genuinely overlap - a
            // wind-scoured crest above the snowline - and whichever removes more cover
            // is the one that is visible, so it is the one worth recording.
            if (state.bareFraction <= coverage[i]) return;

            coverage[i] = state.bareFraction;
            cause[i] = (byte)state.cause;
        }

        public float Sample(int x, int z)
        {
            if (coverage == null) return 0f;
            if (x < 0 || z < 0 || x >= resolution || z >= resolution) return 0f;

            return coverage[z * resolution + x];
        }

        public BareGroundCause CauseAt(int x, int z)
        {
            if (cause == null) return BareGroundCause.None;
            if (x < 0 || z < 0 || x >= resolution || z >= resolution) return BareGroundCause.None;

            return (BareGroundCause)cause[z * resolution + x];
        }

        /// <summary>
        /// Share of the chunk that is bare, and the cause responsible for most of it.
        /// The number a bake report quotes: a mountain chunk that comes back 0% bare has
        /// a broken snowline, and no amount of looking at the terrain says so as quickly.
        /// </summary>
        public float BareShare(out BareGroundCause dominant)
        {
            dominant = BareGroundCause.None;
            if (coverage == null || resolution < 1) return 0f;

            int n = resolution * resolution;
            float total = 0f;

            // Nine buckets on the stack, indexed by the enum. Cheaper and clearer than
            // a dictionary for a set this small and this fixed.
            float bySnow = 0f, byRock = 0f, bySalt = 0f, byBurn = 0f;
            float byChannel = 0f, byTide = 0f, byHollow = 0f, byCrest = 0f;

            for (int i = 0; i < n; i++)
            {
                float c = coverage[i];
                if (c <= 0f) continue;

                total += c;

                switch ((BareGroundCause)cause[i])
                {
                    case BareGroundCause.PermanentSnow: bySnow += c; break;
                    case BareGroundCause.RockAboveSlopeLimit: byRock += c; break;
                    case BareGroundCause.SalinePan: bySalt += c; break;
                    case BareGroundCause.RecentBurn: byBurn += c; break;
                    case BareGroundCause.ActiveChannelBed: byChannel += c; break;
                    case BareGroundCause.TidalScour: byTide += c; break;
                    case BareGroundCause.DeflationHollow: byHollow += c; break;
                    case BareGroundCause.RidgeLeeScour: byCrest += c; break;
                }
            }

            float best = 0f;
            Consider(byRock, BareGroundCause.RockAboveSlopeLimit, ref best, ref dominant);
            Consider(bySnow, BareGroundCause.PermanentSnow, ref best, ref dominant);
            Consider(byCrest, BareGroundCause.RidgeLeeScour, ref best, ref dominant);
            Consider(byBurn, BareGroundCause.RecentBurn, ref best, ref dominant);
            Consider(bySalt, BareGroundCause.SalinePan, ref best, ref dominant);
            Consider(byChannel, BareGroundCause.ActiveChannelBed, ref best, ref dominant);
            Consider(byTide, BareGroundCause.TidalScour, ref best, ref dominant);
            Consider(byHollow, BareGroundCause.DeflationHollow, ref best, ref dominant);

            return total / n;
        }

        private static void Consider(float amount, BareGroundCause cause,
                                      ref float best, ref BareGroundCause winner)
        {
            if (amount <= best) return;

            best = amount;
            winner = cause;
        }
    }

    public static class BareGround
    {
        // ---- thresholds and the widths that give each edge its shape ---------

        /// <summary>deg past the vegetation slope limit over which cover falls to nothing.</summary>
        public const float SlopeEdgeWidthDeg = 6f;

        /// <summary>degC over which the snowline resolves. Narrow, because a snowline is narrow.</summary>
        public const float SnowlineEdgeWidthC = 0.8f;

        /// <summary>0-1 salt loading at which crust forms and nothing roots.</summary>
        public const float SalinePanSaltMin = 0.75f;

        /// <summary>deg. A pan is the flat bottom of a basin; a slope drains and does not crust.</summary>
        public const float SalinePanMaxSlopeDeg = 4f;

        /// <summary>Wetness index above which a river bed is scoured rather than vegetated.</summary>
        public const float ActiveChannelTwiMin = 11f;

        /// <summary>m. Within this of a river, the bed is still worked by the flow.</summary>
        public const float ActiveChannelReachM = 6f;

        /// <summary>0-1 exposure above which a shore is scoured instead of colonised.</summary>
        public const float TidalScourExposureMin = 0.5f;

        /// <summary>0-1 exposure above which wind strips a crest bare.</summary>
        public const float RidgeScourExposureMin = 0.85f;

        /// <summary>0-1 width of the exposure band the crest edge resolves over.</summary>
        public const float RidgeScourEdgeWidth = 0.08f;

        /// <summary>1/m convexity a crest carries. Shared with the ridge zone for consistency.</summary>
        public const float RidgeScourConvexityMin = LandformAssemblage.RidgeConvexityMin;

        /// <summary>0-1 exposure above which a hollow in dry sand deflates.</summary>
        public const float DeflationExposureMin = 0.55f;

        /// <summary>1/m. A deflation hollow is concave - that is what lets wind recirculate in it.</summary>
        public const float DeflationConvexityMax = -0.003f;

        /// <summary>m. Deep water table, so sand is dry enough to move.</summary>
        public const float DeflationWaterTableMinM = 2f;

        /// <summary>P/PET at which fire is most frequent - enough fuel, and dry enough to carry.</summary>
        public const float FirePeakAridity = 0.5f;

        /// <summary>Width of the fire-frequency curve in aridity units.</summary>
        public const float FireAridityTolerance = 0.25f;

        /// <summary>m. Scale of one burn scar. Roughly the size of a real fire's footprint.</summary>
        public const float BurnScarCellMetres = 260f;

        /// <summary>Share of scar cells that have burnt recently enough to still show.</summary>
        public const float BurnScarShare = 0.1f;

        /// <summary>Its own hash stream, so scars do not line up with groves or with darts.</summary>
        private const int BurnHashOffset = 68301841;

        /// <summary>
        /// Why this ground is bare and how bare it is.
        ///
        /// Order is severity, not convenience: the causes that remove the substrate
        /// itself are tested before the ones that only remove the plants, because a
        /// snowfield on a cliff is a cliff and painting it as a burn scar would be a
        /// stranger error than any of the individual thresholds getting slightly wrong.
        /// </summary>
        public static BareGroundState Resolve(in EnvironmentSample site,
                                              float vegetationMaxSlopeDeg,
                                              Noise hash)
        {
            BareGroundState state = new BareGroundState();
            state.cause = BareGroundCause.None;

            // ---- 1. Permanent snow ------------------------------------------
            // Tested first because it hides everything under it, whatever that is.
            float snowBare = RampDown(site.warmestMonthTempC,
                                      EcoConstants.FreezingPointC,
                                      SnowlineEdgeWidthC);
            if (snowBare > 0f)
                return Set(ref state, BareGroundCause.PermanentSnow, snowBare);

            // ---- 2. Rock above the slope limit ------------------------------
            // Uses the vegetation system's own limit rather than a number of its own,
            // so moving that one slider moves the bare zone with it. An author who
            // raises the slope limit expects the bare rock to recede, and it does.
            float slopeBare = RampUp(site.slopeDeg,
                                     vegetationMaxSlopeDeg,
                                     SlopeEdgeWidthDeg);
            if (slopeBare > 0f)
                return Set(ref state, BareGroundCause.RockAboveSlopeLimit, slopeBare);

            // ---- 3. Tidal scour ---------------------------------------------
            if (site.hasWaterInRange
                && site.nearestWaterKind == WaterBodyKind.Ocean
                && site.distanceToWaterM <= EcoConstants.TidalRangeMetres
                && site.windExposure >= TidalScourExposureMin)
            {
                float scour = Mathf.Clamp01(
                    (site.windExposure - TidalScourExposureMin)
                    / Mathf.Max(1e-3f, 1f - TidalScourExposureMin));

                return Set(ref state, BareGroundCause.TidalScour, Mathf.Max(0.4f, scour));
            }

            // ---- 4. Active channel bed --------------------------------------
            if (site.hasWaterInRange
                && site.nearestWaterKind == WaterBodyKind.River
                && site.distanceToWaterM <= ActiveChannelReachM
                && site.twi >= ActiveChannelTwiMin)
                return Set(ref state, BareGroundCause.ActiveChannelBed, 1f);

            // ---- 5. Saline pan ----------------------------------------------
            // Salt plus flat plus dry. Salt alone is a sea cliff, flat alone is a
            // meadow, and dry alone is a desert that still carries shrubs.
            if (site.saltFraction >= SalinePanSaltMin
                && site.slopeDeg <= SalinePanMaxSlopeDeg
                && site.aridity < LandformAssemblage.AridityDesertMax)
                return Set(ref state, BareGroundCause.SalinePan, 1f);

            // ---- 6. Wind-scoured crest --------------------------------------
            if (site.curvature >= RidgeScourConvexityMin)
            {
                float stripped = RampUp(site.windExposure,
                                        RidgeScourExposureMin,
                                        RidgeScourEdgeWidth);
                if (stripped > 0f)
                    return Set(ref state, BareGroundCause.RidgeLeeScour, stripped);
            }

            // ---- 7. Deflation hollow ----------------------------------------
            // Dry, windy, concave and with the water table well down. All four,
            // because a wet hollow becomes a pond and a sheltered one becomes a wood.
            if (site.aridity < LandformAssemblage.AriditySteppeMax
                && site.windExposure >= DeflationExposureMin
                && site.curvature <= DeflationConvexityMax
                && site.waterTableDepthM >= DeflationWaterTableMinM
                && site.rockiness < DebrisPlacement.RockyGroundMin)
                return Set(ref state, BareGroundCause.DeflationHollow, 0.85f);

            // ---- 8. Recent burn ---------------------------------------------
            float burn = BurnScarFraction(site.aridity, hash,
                                          site.worldPos.x, site.worldPos.z);
            if (burn > 0f)
                return Set(ref state, BareGroundCause.RecentBurn, burn);

            return state;
        }

        /// <summary>
        /// How much of this site has burnt recently enough to still read as burnt.
        ///
        /// The lattice is coarse so a scar is a patch rather than a speckle, and the
        /// fire-frequency curve is the whole of the climatology: peak in the middle of
        /// the moisture range, tailing off toward both the desert and the rainforest.
        /// </summary>
        public static float BurnScarFraction(float aridity, Noise hash,
                                              float worldX, float worldZ)
        {
            if (hash == null) return 0f;

            // Fire needs fuel and dryness at once, which is a peak and not a slope.
            float frequency = EcoMath.GaussianTolerance(
                aridity, FirePeakAridity, FireAridityTolerance);

            if (frequency <= 0.01f) return 0f;

            int cx = Mathf.FloorToInt(worldX / BurnScarCellMetres);
            int cz = Mathf.FloorToInt(worldZ / BurnScarCellMetres);

            float draw = hash.Random(cx + BurnHashOffset, cz);

            float threshold = BurnScarShare * frequency;
            if (draw >= threshold) return 0f;

            // Inside a scar. Coverage varies across it because a fire does not burn
            // evenly - it leaves unburnt islands where fuel ran out or the wind turned,
            // and those islands are most of what makes a real burn read as one.
            float within = draw / Mathf.Max(1e-5f, threshold);
            float mosaic = hash.Random(cz, cx + BurnHashOffset);

            return Mathf.Clamp01(0.45f + 0.55f * within * mosaic);
        }

        /// <summary>
        /// Suppression factor for vegetation density. 1 where nothing is bare, 0 where
        /// it is fully bare. Multiplied into the density ceiling rather than tested,
        /// so a partly bare site thins instead of switching off.
        /// </summary>
        public static float VegetationFactor(in BareGroundState state)
        {
            return Mathf.Clamp01(1f - state.bareFraction);
        }

        /// <summary>
        /// Which surface the splat pass should paint instead of soil. Returning the
        /// intent rather than a layer index keeps this file free of any knowledge of
        /// how many layers a graph wired or in what order.
        /// </summary>
        public static string SurfaceHint(BareGroundCause cause)
        {
            switch (cause)
            {
                case BareGroundCause.PermanentSnow: return "snow";
                case BareGroundCause.RockAboveSlopeLimit: return "rock";
                case BareGroundCause.SalinePan: return "salt";
                case BareGroundCause.RecentBurn: return "burnt";
                case BareGroundCause.ActiveChannelBed: return "gravel";
                case BareGroundCause.TidalScour: return "shingle";
                case BareGroundCause.DeflationHollow: return "sand";
                case BareGroundCause.RidgeLeeScour: return "rock";
                default: return null;
            }
        }

        /// <summary>Inspector and self-test label.</summary>
        public static string Name(BareGroundCause cause)
        {
            switch (cause)
            {
                case BareGroundCause.RockAboveSlopeLimit: return "Rock above slope limit";
                case BareGroundCause.PermanentSnow: return "Permanent snow";
                case BareGroundCause.SalinePan: return "Saline pan";
                case BareGroundCause.RecentBurn: return "Recent burn";
                case BareGroundCause.ActiveChannelBed: return "Active channel bed";
                case BareGroundCause.TidalScour: return "Tidal scour";
                case BareGroundCause.DeflationHollow: return "Deflation hollow";
                case BareGroundCause.RidgeLeeScour: return "Wind-scoured crest";
                default: return "Vegetated";
            }
        }

        private static BareGroundState Set(ref BareGroundState state,
                                            BareGroundCause cause, float fraction)
        {
            state.cause = cause;
            state.bareFraction = Mathf.Clamp01(fraction);
            return state;
        }

        /// <summary>0 at the threshold, 1 a full width past it.</summary>
        private static float RampUp(float value, float threshold, float widthM)
        {
            if (value <= threshold) return 0f;
            return Mathf.Clamp01((value - threshold) / Mathf.Max(1e-3f, widthM));
        }

        /// <summary>0 at the threshold, 1 a full width below it.</summary>
        private static float RampDown(float value, float threshold, float width)
        {
            if (value >= threshold) return 0f;
            return Mathf.Clamp01((threshold - value) / Mathf.Max(1e-3f, width));
        }
    }
}
