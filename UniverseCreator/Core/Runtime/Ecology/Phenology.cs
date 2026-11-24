// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    Phenology.cs
//  Purpose: Leaf phenology and seasonal appearance, as thresholds not dates.
//
//  There is no calendar in this file and there is not meant to be one. Budburst
//  happens when a site has accumulated enough warmth (L-24), and senescence when
//  the day has grown short enough (L-03). Those two rules are why a cold spring
//  at altitude is genuinely late while autumn arrives on time every year - the
//  asymmetry a date constant cannot reproduce.
//
//  Everything here is pure: it reads no manager and holds no state. The year walk
//  that finds the thresholds lives beside the weather manager that owns the
//  temperature curve (UCVegetationSystemManager.Season.cs), because that manager
//  is the single owner of a site's climate and this file must not restate it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Ecology
{
    /// <summary>One species' leaf state at one moment of one year, at one site.</summary>
    public struct PhenologyState
    {
        // Phase of the year (0..1) at which each threshold is crossed. Negative
        // means never: a site too cold to reach its budburst sum has no leaf-out,
        // which is a real outcome and not an error.
        public float budburstPhase;
        public float senescencePhase;

        // How much of the full-leaf canopy is standing now, 0..1.
        public float canopyFraction;

        // Progress through the autumn ramp, 0..1. Drives colour, not density.
        public float autumnFraction;

        public bool evergreen;
    }

    public static class Phenology
    {
        // Leaf expansion and leaf fall are both processes of weeks. Held in days
        // and converted, because days are the unit the literature reports and the
        // unit an author can argue with.
        public const float BudburstRampDays = 21f;
        public const float SenescenceRampDays = 18f;

        // A canopy that has lost its leaves is not black. Snow-covered canopy is
        // not pure white either - fresh snow on foliage reads near 0.85.
        public static readonly Color SnowOnCanopyColour = new Color(0.85f, 0.86f, 0.88f, 1f);

        // Below this leaf-off fraction a species is evergreen for every purpose
        // here: no colour swing, no canopy loss, no understory brightening.
        public const float EvergreenLeafOffThreshold = 0.02f;

        /// <summary>Days expressed as a fraction of the year, for phase arithmetic.</summary>
        public static float PhaseFromDays(float days)
        {
            return days / EcoConstants.DaysPerTropicalYear;
        }

        /// <summary>Forward distance from one phase to another around the cycle, 0..1.</summary>
        public static float CyclicDelta(float from, float to)
        {
            return Mathf.Repeat(to - from, 1f);
        }

        /// <summary>L-24. Warmth accumulated since dormancy broke has reached the species' sum.</summary>
        public static bool BudburstReached(float accumulatedGdd, float budburstGdd)
        {
            if (budburstGdd <= 0f) return true;
            return accumulatedGdd >= budburstGdd;
        }

        /// <summary>L-03. Shortening days, not falling temperature, are what start leaf fall.</summary>
        public static bool SenescenceTriggered(float dayLengthH, float senescenceDayLengthH)
        {
            if (senescenceDayLengthH <= 0f) return false;
            return dayLengthH < senescenceDayLengthH;
        }

        /// <summary>
        /// Standing leaf area as a fraction of full canopy. Evergreens sit at 1 and
        /// so does anything whose site never reaches budburst - a species that never
        /// leafs out also never sheds, and pretending otherwise would strip a
        /// conifer bare on a cold mountain.
        /// </summary>
        public static float CanopyFraction(float phase, float budburstPhase,
                                           float senescencePhase, float leafOffFraction)
        {
            float leafOff = Mathf.Clamp01(leafOffFraction);
            if (leafOff <= EvergreenLeafOffThreshold) return 1f;
            if (budburstPhase < 0f || senescencePhase < 0f) return 1f;

            float dormant = 1f - leafOff;

            float springRamp = PhaseFromDays(BudburstRampDays);
            float autumnRamp = PhaseFromDays(SenescenceRampDays);

            // Measured forward from budburst, so the whole function is one cycle
            // with no branch on which side of new year the thresholds fell.
            float t = CyclicDelta(budburstPhase, phase);
            float leafOn = CyclicDelta(budburstPhase, senescencePhase);

            if (t < springRamp)
                return Mathf.Lerp(dormant, 1f, t / springRamp);

            if (t < leafOn) return 1f;

            float intoAutumn = t - leafOn;
            if (intoAutumn < autumnRamp)
                return Mathf.Lerp(1f, dormant, intoAutumn / autumnRamp);

            return dormant;
        }

        /// <summary>Progress through the autumn ramp: 0 before it, 1 once leaf fall is complete.</summary>
        public static float AutumnFraction(float phase, float senescencePhase)
        {
            if (senescencePhase < 0f) return 0f;

            float autumnRamp = PhaseFromDays(SenescenceRampDays);
            if (autumnRamp <= 0f) return 1f;

            float into = CyclicDelta(senescencePhase, phase);

            // Only the ramp itself counts. Past it the year is in winter, which is
            // its own colour, so autumn is not left latched at 1 for six months.
            if (into > autumnRamp) return 0f;
            return Mathf.Clamp01(into / autumnRamp);
        }

        /// <summary>Fills the derived fields of a state from its two thresholds.</summary>
        public static PhenologyState Resolve(float phase, float budburstPhase,
                                             float senescencePhase, float leafOffFraction)
        {
            PhenologyState s = new PhenologyState();
            s.budburstPhase = budburstPhase;
            s.senescencePhase = senescencePhase;
            s.evergreen = Mathf.Clamp01(leafOffFraction) <= EvergreenLeafOffThreshold;
            s.canopyFraction = CanopyFraction(phase, budburstPhase, senescencePhase, leafOffFraction);
            s.autumnFraction = s.evergreen ? 0f : AutumnFraction(phase, senescencePhase);
            return s;
        }

        /// <summary>
        /// The four authored colours blended by where the year actually is for this
        /// species at this site. Spring is the flush right after budburst, summer
        /// the settled canopy, autumn the senescence ramp, winter the dormant rest.
        /// </summary>
        public static Color SeasonalColour(EcoEnvelope eco, float phase, in PhenologyState state)
        {
            if (eco == null) return Color.white;
            if (state.evergreen) return eco.summerColour;
            if (state.budburstPhase < 0f || state.senescencePhase < 0f) return eco.winterColour;

            float springRamp = PhaseFromDays(BudburstRampDays);
            float autumnRamp = PhaseFromDays(SenescenceRampDays);

            float t = CyclicDelta(state.budburstPhase, phase);
            float leafOn = CyclicDelta(state.budburstPhase, state.senescencePhase);

            // The spring flush is a colour of its own for about as long as the
            // leaves are expanding, then it settles. Two ramps, one per transition.
            if (t < springRamp)
                return Color.Lerp(eco.springColour, eco.summerColour,
                                  Mathf.Clamp01(t / springRamp));

            if (t < leafOn)
            {
                // Late summer already carries some of the autumn note. Held to a
                // quarter so it reads as drying rather than as an early autumn.
                float toward = leafOn > springRamp
                    ? Mathf.Clamp01((t - springRamp) / (leafOn - springRamp))
                    : 0f;
                return Color.Lerp(eco.summerColour, eco.autumnColour, toward * 0.25f);
            }

            float intoAutumn = t - leafOn;
            if (intoAutumn < autumnRamp)
                return Color.Lerp(eco.autumnColour, eco.winterColour,
                                  Mathf.Clamp01(intoAutumn / autumnRamp));

            return eco.winterColour;
        }

        /// <summary>
        /// V-37. Grass height between its winter and summer values. Driven by the
        /// same canopy fraction as everything else, so a graminoid does not need a
        /// second season model of its own.
        /// </summary>
        public static float SeasonalHeightScale(Vector2 winterToSummer, float canopyFraction)
        {
            float winter = Mathf.Max(0.01f, winterToSummer.x);
            float summer = Mathf.Max(winter, winterToSummer.y);
            return Mathf.Lerp(winter, summer, Mathf.Clamp01(canopyFraction));
        }

        /// <summary>
        /// V-36. Effective leaf area index once leaf-off is accounted for. This is
        /// the term that feeds back into canopy light (V-16), which is why a
        /// deciduous forest floor genuinely brightens in winter.
        /// </summary>
        public static float EffectiveLai(float fullCanopyLai, float canopyFraction)
        {
            return Mathf.Max(0f, fullCanopyLai) * Mathf.Clamp01(canopyFraction);
        }

        /// <summary>
        /// V-38. Wet surfaces are darker: water fills the surface micro-relief and
        /// suppresses the diffuse scattering that made the dry surface look pale.
        /// Alpha is left alone - wetness does not make a leaf transparent.
        /// </summary>
        public static Color WetDarken(Color dry, float wetness, float wetDarkening)
        {
            float k = Mathf.Clamp01(wetness) * Mathf.Clamp01(wetDarkening);
            if (k <= 0f) return dry;

            float f = 1f - k;
            return new Color(dry.r * f, dry.g * f, dry.b * f, dry.a);
        }

        /// <summary>
        /// V-38. How much of the canopy is carrying snow. Interception scales with
        /// the leaf area actually standing, so a bare deciduous crown holds very
        /// little and a dense conifer holds a lot - the reason a winter conifer
        /// stand reads white while the birch next to it reads grey.
        /// </summary>
        public static float CanopySnowFraction(float snowOnGroundFraction,
                                               float snowCanopyAccumulation,
                                               float canopyFraction)
        {
            float ground = Mathf.Clamp01(snowOnGroundFraction);
            if (ground <= 0f) return 0f;

            return Mathf.Clamp01(ground *
                                 Mathf.Clamp01(snowCanopyAccumulation) *
                                 Mathf.Clamp01(canopyFraction));
        }

        /// <summary>Blends a foliage colour toward snow-laden. Applied after wetness.</summary>
        public static Color SnowBlend(Color foliage, float canopySnowFraction)
        {
            float k = Mathf.Clamp01(canopySnowFraction);
            if (k <= 0f) return foliage;

            Color c = Color.Lerp(foliage, SnowOnCanopyColour, k);
            c.a = foliage.a;
            return c;
        }

        /// <summary>
        /// The full appearance chain in the order the physics happens: the season
        /// sets the leaf colour, rain darkens it, snow settles on top. One entry
        /// point so no caller has to remember the order.
        /// </summary>
        public static Color AppearanceColour(EcoEnvelope eco, float phase,
                                             in PhenologyState state,
                                             float wetness, float snowOnGroundFraction)
        {
            if (eco == null) return Color.white;

            Color c = SeasonalColour(eco, phase, state);
            c = WetDarken(c, wetness, eco.wetDarkening);
            return SnowBlend(c, CanopySnowFraction(snowOnGroundFraction,
                                                   eco.snowCanopyAccumulation,
                                                   state.canopyFraction));
        }
    }
}
