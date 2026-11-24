// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    DebrisPlacement.cs
//  Purpose: Places the non-living scatter - scree, boulders, erratics, cobble,
//           shingle, deadfall, frost rubble - by the process that put it there
//           (V-19). Every deposit obeys the angle of repose (L-23).
//
//  WHY STONES ARE NOT JUST ANOTHER SPECIES. A plant is where it is because it can
//  live there. A stone is where it is because something carried it there and it
//  stopped. Those are different questions and they need different answers: rockiness
//  and slope tell you a stone COULD rest here, but only the process tells you
//  whether one ARRIVED. Scoring stones on an environment envelope gives an even
//  dusting of rock over all steep ground, which is the single most recognisable
//  tell of procedural terrain.
//
//  THE ANGLE OF REPOSE IS THE WHOLE THING. Loose material cannot rest on ground
//  steeper than its own repose angle - about 35 degrees for angular rock, 30 for
//  rounded gravel, 45 for wet sand. That one constraint is why a real scree slope
//  has a dead straight profile at a constant angle: material arriving from above
//  slides until the slope it is building reaches repose, and then it stops. Applied
//  honestly it also removes the commonest artefact in generated terrain, which is
//  boulders sitting on cliff faces.
//
//  SIZE SORTS ITSELF, ALWAYS. Every one of these deposits grades, and each grades
//  for a reason worth respecting:
//    - talus coarsens downslope, because a big block carries more momentum and rolls
//      past the fines. The toe of a scree cone is the coarsest part of it, which is
//      the opposite of what looks intuitive.
//    - river cobble fines downstream, because transport rounds and breaks it.
//    - beach shingle sorts by wave energy, coarse where the water hits hardest.
//  Grading these correctly is most of what makes a slope read as a real one.
//
//  DOWNSLOPE POSITION IS INFERRED, NOT TRACED. Distance below the cliff base is not
//  a field the sampler carries, and getting it exactly would need an upslope trace
//  per stone. The substitute is the contributing area the site already reports
//  through its wetness index: on a cone, area accumulates downslope, so a high index
//  means a site low on the cone. Same ordering, measured differently, no ray march.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    public enum DebrisKind
    {
        /// <summary>No process delivers loose material here.</summary>
        None = 0,

        /// <summary>Angular rock shed from a cliff, resting at repose. Coarsens downslope.</summary>
        Talus = 1,

        /// <summary>Sparse large blocks on a rocky bench. Weathered in place, not transported.</summary>
        BoulderField = 2,

        /// <summary>Rounded, isolated, and out of place. The giveaway of a glaciated landscape.</summary>
        GlacialErratic = 3,

        /// <summary>Rounded stone in a channel or on a bar. Fines downstream.</summary>
        RiverCobble = 4,

        /// <summary>Wave-sorted stone banded parallel to the shore.</summary>
        BeachShingle = 5,

        /// <summary>Fallen timber on a forest floor, lying across the slope.</summary>
        Deadfall = 6,

        /// <summary>Timber cast up at the strandline, lying along the shore.</summary>
        Driftwood = 7,

        /// <summary>Angular rubble split off by frost on level ground above the frost line.</summary>
        FrostRubble = 8
    }

    /// <summary>What to place at a site, and how it should sit.</summary>
    public struct DebrisForm
    {
        public DebrisKind kind;

        /// <summary>0-1 chance a piece is placed here at all. The scatter node thins against this.</summary>
        public float density;

        /// <summary>Multiplier on the prototype's own size. Carries the grading.</summary>
        public float sizeFactor;

        /// <summary>0-1 alignment to the ground normal. Loose stone lies with the slope; a wedged block does not.</summary>
        public float normalAlignment;

        /// <summary>Compass yaw in degrees for elongated pieces, or -1 when the piece may face anywhere.</summary>
        public float yawDeg;

        /// <summary>Fraction of the piece buried. Weathered-in-place stone sits deeper than freshly fallen stone.</summary>
        public float embedFraction;
    }

    public static class DebrisPlacement
    {
        // ---- thresholds, with the reason each one is where it is -------------

        /// <summary>0-1. Below this, ground is soil-covered and no loose stone is exposed.</summary>
        public const float RockyGroundMin = 0.45f;

        /// <summary>Wetness index above which a site is a channel rather than a slope.</summary>
        public const float ChannelTwiMin = 9f;

        /// <summary>Wetness index below which a site sheds water and cannot be a bar or bench.</summary>
        public const float BenchTwiMax = 5f;

        /// <summary>deg. Level enough for frost rubble to stay where it was split.</summary>
        public const float FrostFlatMaxSlopeDeg = 12f;

        /// <summary>degC. Mean annual temperature below which freeze-thaw cycles dominate weathering.</summary>
        public const float FrostLineMeanAnnualC = 2f;

        /// <summary>m2/m2. Leaf area above which there is a canopy to fall out of.</summary>
        public const float DeadfallCanopyLaiMin = 1.5f;

        /// <summary>0-1. Exposure above which a shore is taking real wave energy.</summary>
        public const float HighWaveExposure = 0.55f;

        /// <summary>0-1. How much of a boulder is normally buried once soil has crept around it.</summary>
        public const float BouldersEmbedFraction = 0.3f;

        /// <summary>
        /// The deposit at this site, if any. <paramref name="hash01"/> is the site's
        /// own deterministic draw and is used only for the isolated deposits, where
        /// "few and far apart" is the defining property rather than an aesthetic.
        /// </summary>
        public static DebrisForm Resolve(in EnvironmentSample site, float hash01,
                                          float glaciationAltitudeM)
        {
            DebrisForm form = new DebrisForm();
            form.kind = DebrisKind.None;
            form.yawDeg = -1f;
            form.sizeFactor = 1f;

            // Nothing loose rests under water; what is down there is a different
            // problem and belongs to the aquatic roster.
            if (site.submergedDepthM > 0f) return form;

            LandformZone zone = LandformAssemblage.Classify(in site);

            // ---- 1. Talus. The dominant deposit wherever a cliff sheds -------
            if (zone == LandformZone.Talus)
            {
                float stability = ReposeStability(
                    site.slopeDeg, EcoConstants.AngleOfReposeDegrees);

                if (stability > 0f)
                {
                    // Position on the cone, low is 1. Blocks coarsen toward the toe
                    // because momentum carries them past the fines, so the biggest
                    // stone on a scree slope is at the bottom of it.
                    float down = DownslopePosition(site.twi);

                    form.kind = DebrisKind.Talus;
                    form.density = stability;
                    form.sizeFactor = Mathf.Lerp(0.35f, 1.7f, down);

                    // Loose angular rock lies however it landed, which is close to the
                    // surface it landed on.
                    form.normalAlignment = 0.85f;

                    // Barely buried. A scree slope is stone on stone, and burying it
                    // would read as gravel scattered on soil.
                    form.embedFraction = 0.08f;
                    return form;
                }
            }

            // ---- 2. Frost rubble. Level ground above the frost line ----------
            if (site.slopeDeg <= FrostFlatMaxSlopeDeg
                && site.meanAnnualTempC <= FrostLineMeanAnnualC
                && site.rockiness >= RockyGroundMin)
            {
                form.kind = DebrisKind.FrostRubble;

                // Dense and angular: the rock has been split in place and gone nowhere,
                // so the pieces still nearly fit together.
                form.density = Mathf.Clamp01(site.rockiness);
                form.sizeFactor = Mathf.Lerp(0.4f, 0.8f, hash01);
                form.normalAlignment = 0.7f;
                form.embedFraction = 0.15f;
                return form;
            }

            // ---- 3. Beach shingle. Wave-sorted, banded, parallel to shore ----
            if (zone == LandformZone.Intertidal
                || (zone == LandformZone.CoastalDune
                    && site.distanceToWaterM <= EcoConstants.ShingleBandMetres))
            {
                // Wave energy is not a sampled field; exposure to the open sky over the
                // upwind arc is, and fetch is what sets wave height. A sheltered cove
                // gets sand, a headland gets cobble, and this is the difference between
                // them expressed with what the sampler already computes.
                float energy = Mathf.Clamp01(site.windExposure);

                if (energy >= HighWaveExposure * 0.5f)
                {
                    // Banding. Stone is thrown highest by the biggest waves and left
                    // there, so the coarse material is at the top of the band and the
                    // fines are at the water's edge.
                    float band = 1f - Mathf.Clamp01(
                        site.distanceToWaterM / EcoConstants.ShingleBandMetres);

                    form.kind = DebrisKind.BeachShingle;
                    form.density = energy;
                    form.sizeFactor = Mathf.Lerp(0.25f, 1.1f, energy * (1f - band * 0.5f));

                    // Shingle lies flat, imbricated against the slope of the beach.
                    form.normalAlignment = 1f;
                    form.embedFraction = 0.2f;
                    return form;
                }

                // Low-energy shore. Driftwood collects at the strandline instead, lying
                // along the shore because that is the direction the water left it in.
                form.kind = DebrisKind.Driftwood;
                form.density = 0.25f;
                form.sizeFactor = Mathf.Lerp(0.7f, 1.4f, hash01);
                form.normalAlignment = 0.9f;
                form.yawDeg = ShoreTangentDeg(site.aspectDeg);
                form.embedFraction = 0.1f;
                return form;
            }

            // ---- 4. River cobble. In the channel and on its bars -------------
            if (site.hasWaterInRange
                && site.nearestWaterKind == WaterBodyKind.River
                && site.twi >= ChannelTwiMin
                && site.slopeDeg <= EcoConstants.AngleOfReposeRoundedGravelDegrees)
            {
                float down = DownslopePosition(site.twi);

                form.kind = DebrisKind.RiverCobble;
                form.density = Mathf.Clamp01(0.4f + 0.6f * down);

                // Fines downstream: transport rounds a stone and breaks it, so the same
                // river carries boulders in its headwaters and sand at its mouth.
                form.sizeFactor = Mathf.Lerp(1.1f, 0.3f, down);

                // Imbricated - stacked leaning downstream against the flow - which reads
                // as lying flat on the bed.
                form.normalAlignment = 1f;
                form.embedFraction = 0.35f;
                return form;
            }

            // ---- 5. Glacial erratics. Isolated, and deliberately misplaced ---
            if (site.altitudeM >= glaciationAltitudeM
                && site.slopeDeg <= EcoConstants.AngleOfReposeRoundedGravelDegrees)
            {
                // Rare by construction. An erratic is remarkable precisely because
                // there is one of it, so the hash gate is tight and the density that
                // survives it is what makes the deposit isolated rather than a field.
                if (hash01 > 0.985f)
                {
                    form.kind = DebrisKind.GlacialErratic;
                    form.density = 1f;

                    // Large. A boulder a glacier moved is a boulder nothing local could
                    // have produced, and its size is the reason it is obvious.
                    form.sizeFactor = Mathf.Lerp(1.8f, 3.2f, (hash01 - 0.985f) / 0.015f);

                    // Standing where it was dropped, not conformed to the slope. Being
                    // out of place is the point - erratics look wrong, and that is how
                    // a glaciated landscape announces itself.
                    form.normalAlignment = 0.15f;
                    form.embedFraction = 0.25f;
                    return form;
                }
            }

            // ---- 6. Boulder field. Weathered in place on a rocky bench ------
            if (site.rockiness >= RockyGroundMin + 0.15f
                && site.twi <= BenchTwiMax
                && site.slopeDeg <= EcoConstants.AngleOfReposeDegrees)
            {
                form.kind = DebrisKind.BoulderField;

                // Sparse. These have not been delivered from anywhere; they are what is
                // left of the bedrock after the softer parts of it weathered away, so
                // there are as many as the rock was jointed.
                form.density = Mathf.Clamp01((site.rockiness - RockyGroundMin) * 1.2f);
                form.sizeFactor = Mathf.Lerp(1.2f, 2.1f, hash01);

                // Seated on the surface it grew out of, and soil has crept up around it.
                form.normalAlignment = 1f;
                form.embedFraction = BouldersEmbedFraction;
                return form;
            }

            // ---- 7. Deadfall. Under a canopy that has something to shed -----
            if (site.canopyLai >= DeadfallCanopyLaiMin
                && site.slopeDeg <= EcoConstants.AngleOfReposeDegrees)
            {
                // Snow load is what brings timber down in quantity - the branch breaks
                // under the weight - so the deposit tracks the load rather than the
                // canopy alone. A canopy with no snow still sheds, slowly, which is the
                // floor under the density here.
                float load = Mathf.Clamp01(site.snowLoadKgPerM2 / 200f);

                form.kind = DebrisKind.Deadfall;
                form.density = Mathf.Clamp01(0.15f + 0.6f * load);
                form.sizeFactor = Mathf.Lerp(0.6f, 1.3f, hash01);

                // Lying across the slope. A trunk that fell downhill rolled until it
                // came to rest broadside, which is why a real forest floor is a set of
                // near-parallel contour lines rather than a scatter of angles.
                form.normalAlignment = 1f;
                form.yawDeg = AcrossSlopeDeg(site.aspectDeg);
                form.embedFraction = 0.12f;
                return form;
            }

            return form;
        }

        /// <summary>
        /// How much loose material stays on a slope. Fully stable well below the
        /// repose angle, thinning to nothing as it is approached, and zero above it -
        /// which is what gives a real scree slope its straight constant-angle profile.
        /// </summary>
        public static float ReposeStability(float slopeDeg, float reposeDeg)
        {
            float repose = Mathf.Max(1f, reposeDeg);
            if (slopeDeg >= repose) return 0f;

            float onset = repose * EcoConstants.ReposeStabilityOnsetFraction;
            if (slopeDeg <= onset) return 1f;

            return Mathf.Clamp01((repose - slopeDeg) / Mathf.Max(1e-3f, repose - onset));
        }

        /// <summary>
        /// The repose angle the material of a deposit actually has. Angular rock holds
        /// a steeper slope than rounded gravel does, and that difference is visible:
        /// a scree cone is steeper than a shingle beach.
        /// </summary>
        public static float ReposeAngleFor(DebrisKind kind)
        {
            switch (kind)
            {
                case DebrisKind.Talus:
                case DebrisKind.FrostRubble:
                    return EcoConstants.AngleOfReposeDegrees;

                case DebrisKind.RiverCobble:
                case DebrisKind.BeachShingle:
                    return EcoConstants.AngleOfReposeRoundedGravelDegrees;

                case DebrisKind.BoulderField:
                case DebrisKind.GlacialErratic:
                    // A block wide enough to bridge the surface is not really loose;
                    // it wedges, and wedged blocks sit on ground that would shed gravel.
                    return EcoConstants.AngleOfReposeWetSandDegrees;

                default:
                    return EcoConstants.AngleOfReposeDegrees;
            }
        }

        /// <summary>
        /// 0 at the top of a slope, 1 at its foot, inferred from contributing area.
        /// </summary>
        public static float DownslopePosition(float twi)
        {
            // The wetness index is a logarithm of area over slope, so it spans roughly
            // 2 to 14 in real terrain. Mapping that span rather than clamping at zero
            // keeps the whole of a cone inside the grading rather than saturating the
            // lower half of it.
            return Mathf.Clamp01((twi - 3f) / 9f);
        }

        /// <summary>
        /// Yaw across the slope, from the downslope aspect. A fallen trunk comes to
        /// rest broadside, so it lies at right angles to the fall line.
        /// </summary>
        public static float AcrossSlopeDeg(float aspectDeg)
        {
            if (aspectDeg < 0f) return -1f;

            float yaw = aspectDeg + 90f;
            if (yaw >= EcoConstants.DegreesPerCircle) yaw -= EcoConstants.DegreesPerCircle;

            return yaw;
        }

        /// <summary>
        /// Yaw along the shore. The shore runs at right angles to the direction the
        /// land falls toward the water, which is what the aspect reports here.
        /// </summary>
        public static float ShoreTangentDeg(float aspectDeg)
        {
            return AcrossSlopeDeg(aspectDeg);
        }
    }
}
