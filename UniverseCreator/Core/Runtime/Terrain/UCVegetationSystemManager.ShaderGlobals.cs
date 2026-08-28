// -----------------------------------------------------------------------------
//  Universe Creator - publishing the world's season and weather to the shaders.
//
//  WHY THIS FILE EXISTS
//
//  Before it, the system published wind, ripple and underwater globals but nothing
//  about the season or the weather. That left every material unable to know
//  something the simulation already knew: no bark could darken in rain, no branch
//  could hold snow, no canopy could go bare in November. The maths was all there
//  and simply never reached the GPU.
//
//  THE RULE THIS FILE ENFORCES: THE CPU OWNS EVERY CONVENTION.
//
//  The obvious way to do seasons on the GPU is to send the phase and let each
//  shader work out where in the year that is. That is also how the two sides drift
//  apart - the shader rounds a ramp differently, or reads a threshold as
//  inclusive, and a scattered instance ends up a different colour from the mesh
//  standing next to it in the same clearing.
//
//  So nothing here sends a raw phase for a shader to interpret. Phenology.
//  SeasonalColour is the single authority on where the year is, and this file
//  resolves its piecewise ramp into four weights that reproduce it exactly. The
//  GPU only ever performs the blend the CPU already decided on. There is one
//  season model in this project and this is a transport for it, not a second
//  implementation of it.
//
//  WHAT IS SPLIT, AND WHY
//
//    Timing comes from here.      When the world leafs out and when it goes bare
//                                 is a property of climate and daylight, which
//                                 belong to the world, not to a material.
//    Amplitude comes from there.  How bare a given species goes, how much snow its
//                                 crown catches, how dark its bark wets - those are
//                                 properties of the plant, authored on the material
//                                 as _LeafOffFraction, _SnowCanopyAccumulation and
//                                 _WetDarkening.
//
//  That split is why one global set can serve every plant in the world without
//  flattening them into looking like each other.
//
//  COST
//
//  Four Shader.SetGlobalVector calls a frame, and the year walks behind them run
//  on the ecology tick, not per frame. Nothing here allocates.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCVegetationSystemManager
    {
        // ---------------------------------------------------------------------
        //  Shader global IDs. Resolved once - Shader.PropertyToID does a string
        //  hash and a dictionary probe, and this runs every frame.
        // ---------------------------------------------------------------------

        private static readonly int IdSeasonPhase   = Shader.PropertyToID("_UCSeasonPhase");
        private static readonly int IdSeasonWeights = Shader.PropertyToID("_UCSeasonWeights");
        private static readonly int IdSnowParams    = Shader.PropertyToID("_UCSnowParams");

        // ---------------------------------------------------------------------
        //  The reference plant the world's leaf-off clock is read from
        // ---------------------------------------------------------------------

        // A world-level timing signal has to come from *a* plant, and it must not
        // come from whichever species happen to be in the library: adding one shrub
        // would then shift the leaf-off date of every material in the world. So it
        // is read from a fixed reference broadleaf, stated here where it can be
        // argued with and retuned.
        //
        // 200 degree-days over a 5 C base is the standard temperate-broadleaf
        // budburst sum. The base is 5 C because that is where cambial growth
        // effectively begins; below it warmth does not accumulate toward leaf-out.
        [Header("Shader Globals - reference plant for world leaf-off timing")]
        [Tooltip("Growing degree-days above the base temperature needed for budburst. " +
                 "Temperate deciduous broadleaf reference.")]
        public float referenceBudburstGdd = 200f;

        [Tooltip("Base temperature for the degree-day sum, in Celsius.")]
        public float referenceGddBaseC = 5f;

        // Senescence is photoperiod-triggered, and 12.5 hours is the daylength
        // threshold temperate deciduous species are generally observed to turn at.
        [Tooltip("Day length in hours below which autumn senescence begins.")]
        public float referenceSenescenceDayLengthH = 12.5f;

        [Tooltip("Publish season and weather globals to the shaders. Off leaves every " +
                 "material at its unseasonal default: full leaf, dry, no snow.")]
        public bool publishSeasonGlobals = true;

        // ---------------------------------------------------------------------
        //  Cached year-walk results
        // ---------------------------------------------------------------------

        // The two year walks are 73 evaluations each and their answers only change
        // when the climate does, not when the clock ticks forward. Solved on the
        // ecology tick and reused by every frame in between.
        private float cachedBudburstSeaLevel = -1f;
        private float cachedBudburstOneKm = -1f;
        private float cachedSenescence = -1f;
        private bool seasonSolvesValid;

        /// <summary>
        /// Re-solves the reference plant's budburst and senescence phases. Called
        /// from the ecology tick; the per-frame publish reuses the answers.
        /// </summary>
        private void SolveSeasonGlobals()
        {
            cachedBudburstSeaLevel = SolveBudburstPhase(0f, referenceBudburstGdd, referenceGddBaseC);

            // The same solve a kilometre up. The difference between the two is the
            // altitude gradient - measured, not assumed, so a world with a shallow
            // lapse rate correctly gets a shallow gradient.
            cachedBudburstOneKm = SolveBudburstPhase(1000f, referenceBudburstGdd, referenceGddBaseC);

            // No altitude argument, and that is the physics: senescence is triggered
            // by day length, and day length does not vary with altitude. This is why
            // an autumn treeline turns all at once while its spring creeps up over
            // weeks, and why only the budburst term above carries a per-km number.
            cachedSenescence = SolveSenescencePhase(referenceSenescenceDayLengthH);

            seasonSolvesValid = true;
        }

        /// <summary>
        /// Pushes the world's season and weather to every shader that reads
        /// UC_Season.hlsl. Safe to call with no weather or lighting manager present:
        /// it then publishes the zeroes those shaders read as "high summer, dry, no
        /// snow", which is a correct still world rather than a broken one.
        /// </summary>
        public void PublishShaderGlobals()
        {
            if (!publishSeasonGlobals)
            {
                Shader.SetGlobalVector(IdSeasonPhase, Vector4.zero);
                Shader.SetGlobalVector(IdSeasonWeights, Vector4.zero);
                Shader.SetGlobalVector(IdSnowParams, Vector4.zero);
                global::UniverseCreator.Weather.WetnessCoupling.Clear();
                return;
            }

            if (!seasonSolvesValid) SolveSeasonGlobals();

            float phase = InterpolatedSeasonPhase;

            // --- season phase and the dormancy gradient ----------------------

            // leafOffFraction is passed as 1 so what comes back is pure dormancy,
            // 0 = full leaf and 1 = fully bare. The per-species amount is applied on
            // the GPU from the material's own _LeafOffFraction, which is what lets
            // one global serve an oak and a pine at once.
            PhenologyState atSea = Phenology.Resolve(
                phase, cachedBudburstSeaLevel, cachedSenescence, 1f);

            PhenologyState atKm = Phenology.Resolve(
                phase, cachedBudburstOneKm, cachedSenescence, 1f);

            float dormancySea = 1f - Mathf.Clamp01(atSea.canopyFraction);
            float dormancyKm = 1f - Mathf.Clamp01(atKm.canopyFraction);

            // Only while budburst is the live transition. Through autumn the two
            // altitudes senesce together, and letting a stale spring gradient leak
            // into that would hand photoperiod an altitude dependence it has not
            // got - a treeline turning in the wrong order.
            float perKm = 0f;
            if (dormancySea > 0.001f || dormancyKm > 0.001f)
            {
                bool springSide = SeasonIsInBudburst(phase, atSea);
                if (springSide) perKm = dormancyKm - dormancySea;
            }

            bool live = Weather != null || Lighting != null;

            Shader.SetGlobalVector(IdSeasonPhase, new Vector4(
                phase,
                Mathf.Clamp01(dormancySea),
                perKm,
                live ? 1f : 0f));

            // --- season weights ----------------------------------------------

            Shader.SetGlobalVector(IdSeasonWeights, ResolveSeasonWeights(phase, atSea));

            // --- snow --------------------------------------------------------

            UCWeatherSystemManager w = Weather;

            // cos, not the angle: the shader compares it against a dot product, so
            // the trig happens once here instead of per pixel.
            float holdCos = Mathf.Cos(SnowHoldingAngleDegrees * Mathf.Deg2Rad);

            Shader.SetGlobalVector(IdSnowParams, w != null
                ? new Vector4(w.snowLineMetres, SnowLineBandMetres,
                              Mathf.Clamp01(w.snowIntensity), holdCos)
                : new Vector4(0f, SnowLineBandMetres, 0f, holdCos));

            // --- rain and wetness --------------------------------------------

            // WetnessCoupling is the single writer of the wetness global; the manager
            // supplies the numbers and it owns the upload.
            if (w != null)
                global::UniverseCreator.Weather.WetnessCoupling.Publish(
                    w.surfaceWetness, w.rainIntensity, w.humidity);
            else
                global::UniverseCreator.Weather.WetnessCoupling.Clear();
        }

        /// <summary>
        /// True when the world is on the spring side of the year - the half where
        /// warmth drives the transition and altitude therefore delays it.
        /// </summary>
        private static bool SeasonIsInBudburst(float phase, in PhenologyState state)
        {
            if (state.evergreen) return false;
            if (state.budburstPhase < 0f || state.senescencePhase < 0f) return false;

            // Distance forward from budburst to here, against the distance forward
            // from budburst to senescence. Inside that span the world is in leaf and
            // spring is the transition behind it; past it, autumn is.
            float here = Phenology.CyclicDelta(state.budburstPhase, phase);
            float leafOn = Phenology.CyclicDelta(state.budburstPhase, state.senescencePhase);

            return here < leafOn;
        }

        /// <summary>
        /// Resolves Phenology.SeasonalColour's piecewise ramp into four weights that
        /// reproduce it exactly.
        ///
        /// Every branch below is the same branch that function takes, rewritten to
        /// emit the blend rather than perform it: where it returns Lerp(a, b, u) this
        /// returns weight (1-u) on a and u on b. That is the whole reason this is
        /// safe - it is not a second season model, it is the same one expressed as a
        /// vector so the GPU can apply it to a texture instead of to a Color.
        /// </summary>
        private static Vector4 ResolveSeasonWeights(float phase, in PhenologyState state)
        {
            // x = spring, y = summer, z = autumn, w = winter

            if (state.evergreen)
                return new Vector4(0f, 1f, 0f, 0f);

            // A site too cold to ever reach budburst, or with no daylight cycle to
            // senesce by, sits at its winter colour all year. That is a real outcome
            // for a treeline or a polar world, not a failure to resolve.
            if (state.budburstPhase < 0f || state.senescencePhase < 0f)
                return new Vector4(0f, 0f, 0f, 1f);

            float springRamp = Phenology.PhaseFromDays(Phenology.BudburstRampDays);
            float autumnRamp = Phenology.PhaseFromDays(Phenology.SenescenceRampDays);

            float t = Phenology.CyclicDelta(state.budburstPhase, phase);
            float leafOn = Phenology.CyclicDelta(state.budburstPhase, state.senescencePhase);

            // Leaf expansion: the spring flush is its own colour while the leaves
            // are still growing, then settles into summer.
            if (t < springRamp)
            {
                float u = springRamp > 0f ? Mathf.Clamp01(t / springRamp) : 1f;
                return new Vector4(1f - u, u, 0f, 0f);
            }

            // Full leaf, drying slowly. The quarter cap is what keeps this reading as
            // late summer rather than as an autumn that starts in July.
            if (t < leafOn)
            {
                float toward = leafOn > springRamp
                    ? Mathf.Clamp01((t - springRamp) / (leafOn - springRamp))
                    : 0f;
                float k = toward * 0.25f;
                return new Vector4(0f, 1f - k, k, 0f);
            }

            // Leaf fall.
            float intoAutumn = t - leafOn;
            if (intoAutumn < autumnRamp)
            {
                float u = autumnRamp > 0f ? Mathf.Clamp01(intoAutumn / autumnRamp) : 1f;
                return new Vector4(0f, 0f, 1f - u, u);
            }

            return new Vector4(0f, 0f, 0f, 1f);
        }
    }
}
