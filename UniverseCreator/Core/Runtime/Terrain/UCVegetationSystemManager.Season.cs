// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    UCVegetationSystemManager.Season.cs
//  Purpose: Solves each species' leaf thresholds against this world's climate,
//           and runs the coarse ecology tick.
//
//  Why the split. Phenology.cs holds the maths and knows nothing about this
//  project's managers. This file is the half that reads the world, and it reads
//  only from the managers that already own each fact:
//
//      temperature at an altitude and a phase   <- UCWeatherSystemManager
//      day length at a phase                    <- UCLightingSystemManager
//      wetness, snow, the season clock itself    <- UCWeatherSystemManager
//
//  Nothing here stores a second copy of any of those. The year walk lives here
//  rather than in Ecology precisely because the temperature curve belongs to the
//  weather manager, and re-deriving it next to the plant maths would be the one
//  kind of duplication that silently drifts.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCVegetationSystemManager
    {
        // Five-day steps. Coarse enough that a whole year costs 73 evaluations of
        // a sine, fine enough that budburst lands within a day once the crossing
        // step is interpolated. Both walks use the same grid so they cannot
        // disagree about where the year is.
        public const int YearWalkSteps = 73;

        // A snow line is a band, not an edge; 200 m is the usual transition over
        // which patchy cover becomes continuous. Public and shared because the
        // shader side reproduces this same arithmetic, and two copies of the number
        // would eventually disagree - putting the painted snow line on the terrain
        // at a different height from the snow on the plants standing in it.
        public const float SnowLineBandMetres = 200f;

        // Above the line, cover held at this much even with nothing falling: a
        // settled pack does not melt the instant a storm stops.
        public const float SettledPackFloor = 0.6f;

        // Snow's angle of repose on foliage. Fresh snow holds to about 40 degrees
        // and sloughs off anything steeper, which is why a branch is white on top
        // and bare underneath rather than evenly frosted. The shader gates on the
        // cosine of this, so it is a dot product there and not a trig call.
        public const float SnowHoldingAngleDegrees = 40f;

        /// <summary>Days per step of the year walk.</summary>
        public static float YearWalkStepDays
        {
            get { return EcoConstants.DaysPerTropicalYear / YearWalkSteps; }
        }

        // ---------------------------------------------------------------------
        //  V-40  Coarse tick state
        // ---------------------------------------------------------------------

        // Where the season was at the last tick and where it is at this one. Every
        // frame between the two interpolates, so appearance still moves smoothly
        // while the ecology maths runs once every ecologyTickSeconds.
        private float tickStartPhase;
        private float tickEndPhase;
        private float tickAccumulator;
        private bool tickPrimed;

        // The phase the world's vegetation was last actually built at, so the tick
        // can tell when the season has moved far enough to be worth reapplying.
        private float lastAppliedSeasonPhase = -1f;

        /// <summary>
        /// The season phase to render with. Interpolated between ticks, so nothing
        /// visible steps at the tick rate even though the maths does. (V-40)
        /// </summary>
        public float InterpolatedSeasonPhase
        {
            get
            {
                if (!tickPrimed) return ResolvedSeasonPhase;
                if (ecologyTickSeconds <= 0f) return tickEndPhase;

                float t = Mathf.Clamp01(tickAccumulator / ecologyTickSeconds);
                float forward = Phenology.CyclicDelta(tickStartPhase, tickEndPhase);
                return Mathf.Repeat(tickStartPhase + forward * t, 1f);
            }
        }

        /// <summary>How many ticks have run. Diagnostics and the self-test read this.</summary>
        public int EcologyTickCount { get; private set; }

        // Update, not LateUpdate, and deliberately this short: the whole per-frame
        // cost of Module 3's ecology is one add and one compare. Everything past
        // the early return happens once every ecologyTickSeconds, allocates
        // nothing, and touches no per-instance data. (V-40)
        private void Update()
        {
            if (!vegetationEnabled) return;

            // Before the early return, and deliberately so. The ecology maths can
            // afford to run once every ecologyTickSeconds because a forest does not
            // change composition in a frame - but a shader global is read every
            // frame by every material, and publishing it at the tick rate would step
            // the rain and the snow line visibly. InterpolatedSeasonPhase is already
            // built to be smooth between ticks; this is the call that lets the GPU
            // see that smoothness instead of the tick behind it.
            PublishShaderGlobals();

            tickAccumulator += Time.deltaTime;
            if (tickPrimed && tickAccumulator < ecologyTickSeconds) return;

            float elapsed = tickPrimed ? tickAccumulator : 0f;
            tickAccumulator = 0f;

            EcologyTick(elapsed);
        }

        /// <summary>
        /// One coarse step of the world's ecology. Reads where the season has got
        /// to and decides whether the standing vegetation is now wrong for it.
        ///
        /// It does not advance the season. It used to: this method moved the weather
        /// manager's seasonPhase forward by its own tick accumulator, which made the
        /// year run at the ecology tick rate while the day ran at the frame rate -
        /// two clocks that could not agree, and neither of them reproducible on a
        /// second machine. The weather manager's Update owns time now, and this
        /// reads the answer it produced. (W-03)
        ///
        /// The elapsed argument is kept: the between-tick season interpolation is
        /// measured against it.
        /// </summary>
        public void EcologyTick(float elapsedSeconds)
        {
            tickStartPhase = tickPrimed ? tickEndPhase : ResolvedSeasonPhase;
            tickEndPhase = ResolvedSeasonPhase;
            tickPrimed = true;
            EcologyTickCount++;

            if (lastAppliedSeasonPhase < 0f)
            {
                lastAppliedSeasonPhase = tickEndPhase;
                return;
            }

            // A rebuild is what makes the new season visible, and a rebuild is not
            // cheap, so it waits until the year has actually moved. Measured in days
            // rather than in ticks: the same threshold then behaves the same way at
            // any year length.
            float movedDays = Phenology.CyclicDelta(lastAppliedSeasonPhase, tickEndPhase) *
                              EcoConstants.DaysPerTropicalYear;

            if (movedDays >= Mathf.Max(0.5f, seasonRefreshDays))
            {
                lastAppliedSeasonPhase = tickEndPhase;
                MarkSettingsDirty();
            }
        }

        // ---------------------------------------------------------------------
        //  V-35  Threshold solving
        // ---------------------------------------------------------------------

        /// <summary>
        /// Phase of the year at which dormancy breaks: the coldest point of the
        /// year at this altitude. Found by scanning rather than assumed, so a
        /// southern-hemisphere or long-year world needs no special case and there
        /// is no date constant to be wrong.
        /// </summary>
        public float DormancyPhaseAt(float altitudeMetres)
        {
            UCWeatherSystemManager w = Weather;
            if (w == null) return 0f;

            float coldestPhase = 0f;
            float coldest = float.MaxValue;

            for (int i = 0; i < YearWalkSteps; i++)
            {
                float phase = (float)i / YearWalkSteps;
                float t = w.TempAtSeasonC(altitudeMetres, phase);
                if (t < coldest)
                {
                    coldest = t;
                    coldestPhase = phase;
                }
            }

            return coldestPhase;
        }

        /// <summary>
        /// L-24, V-35. Walks the year forward from dormancy accumulating growing
        /// degree days, and returns the phase at which the species' sum is met.
        /// Negative when a full year cannot supply it - a site genuinely too cold
        /// for this species to leaf out, which the suitability pass then excludes.
        /// </summary>
        public float SolveBudburstPhase(float altitudeMetres, float budburstGdd, float gddBaseC)
        {
            UCWeatherSystemManager w = Weather;
            if (w == null) return -1f;

            float start = DormancyPhaseAt(altitudeMetres);
            if (budburstGdd <= 0f) return start;

            float stepDays = YearWalkStepDays;
            float sum = 0f;

            for (int i = 0; i < YearWalkSteps; i++)
            {
                float phase = Mathf.Repeat(start + (float)i / YearWalkSteps, 1f);
                float tMean = w.TempAtSeasonC(altitudeMetres, phase);
                float step = EcoMath.GrowingDegreeDays(tMean, gddBaseC, stepDays);

                if (Phenology.BudburstReached(sum + step, budburstGdd))
                {
                    // Where inside this step the sum crossed. Without this the
                    // answer is quantised to five days, which is coarser than the
                    // altitude difference the task asks to be visible.
                    float needed = budburstGdd - sum;
                    float within = step > 0f ? Mathf.Clamp01(needed / step) : 0f;
                    return Mathf.Repeat(start + ((float)i + within) / YearWalkSteps, 1f);
                }

                sum += step;
            }

            return -1f;
        }

        /// <summary>
        /// L-03, V-35. The first phase after midsummer at which the day is shorter
        /// than the species' trigger. Negative where the day never gets that short,
        /// which is how the tropics come out evergreen without being told to be.
        /// </summary>
        public float SolveSenescencePhase(float senescenceDayLengthH)
        {
            UCLightingSystemManager l = Lighting;
            if (l == null || senescenceDayLengthH <= 0f) return -1f;

            // Midsummer is where the day is longest. Scanned, not assumed, for the
            // same reason as dormancy above.
            float longestPhase = 0f;
            float longest = -1f;

            for (int i = 0; i < YearWalkSteps; i++)
            {
                float phase = (float)i / YearWalkSteps;
                float h = l.DayLengthHours(phase);
                if (h > longest)
                {
                    longest = h;
                    longestPhase = phase;
                }
            }

            // Only the shortening half of the year can trigger it, so the walk stops
            // at midwinter instead of wrapping into next spring's lengthening days.
            int half = YearWalkSteps / 2;

            for (int i = 1; i <= half; i++)
            {
                float phase = Mathf.Repeat(longestPhase + (float)i / YearWalkSteps, 1f);
                float h = l.DayLengthHours(phase);

                if (Phenology.SenescenceTriggered(h, senescenceDayLengthH))
                {
                    float prevH = l.DayLengthHours(
                        Mathf.Repeat(longestPhase + (float)(i - 1) / YearWalkSteps, 1f));

                    float span = prevH - h;
                    float within = span > 0.0001f
                        ? Mathf.Clamp01((prevH - senescenceDayLengthH) / span)
                        : 0f;

                    return Mathf.Repeat(longestPhase + ((float)(i - 1) + within) / YearWalkSteps, 1f);
                }
            }

            return -1f;
        }

        /// <summary>
        /// One species' leaf state at one altitude, right now. This is the entry
        /// point the scatter path calls; it is not called per instance per frame.
        /// </summary>
        public PhenologyState PhenologyFor(EcoEnvelope eco, float altitudeMetres)
        {
            if (eco == null) return Phenology.Resolve(0f, -1f, -1f, 0f);

            float phase = InterpolatedSeasonPhase;
            float budburst = SolveBudburstPhase(altitudeMetres, eco.budburstGdd, eco.gddBaseC);
            float senescence = SolveSenescencePhase(eco.senescenceDayLengthH);

            return Phenology.Resolve(phase, budburst, senescence, eco.leafOffFraction);
        }

        // ---------------------------------------------------------------------
        //  V-38  Weather terms the appearance chain needs
        // ---------------------------------------------------------------------

        /// <summary>
        /// 0-1 surface wetness, read from the weather manager. Vegetation never
        /// writes this; § 6 is read-only on weather.
        /// </summary>
        public float ResolvedWetness
        {
            get
            {
                UCWeatherSystemManager w = Weather;
                return w != null ? Mathf.Clamp01(w.surfaceWetness) : 0f;
            }
        }

        /// <summary>
        /// 0-1 lying snow at an altitude, from the weather manager's climatic snow
        /// line and how hard it is snowing. This is not the texture graph's snow
        /// cover node: that node paints ground from an authored artistic line in
        /// one graph, while this reads the world's climate for canopy interception.
        /// Different owners, different inputs, and neither is derived from the other.
        /// </summary>
        public float SnowCoverFractionAt(float altitudeMetres)
        {
            UCWeatherSystemManager w = Weather;
            if (w == null) return 0f;

            float above = altitudeMetres - w.snowLineMetres;
            float cover = Mathf.Clamp01(0.5f + above / SnowLineBandMetres);

            // Snow has to have fallen. Held at a floor above the line so a settled
            // pack does not vanish the moment the storm stops.
            float supply = Mathf.Clamp01(Mathf.Max(w.snowIntensity, cover >= 1f ? SettledPackFloor : 0f));

            return Mathf.Clamp01(cover * supply);
        }

        /// <summary>
        /// V-36, V-38. The colour one species shows at one altitude right now, with
        /// season, rain and snow applied in physical order. One call so no caller
        /// has to assemble the chain itself.
        /// </summary>
        public Color SeasonalAppearance(EcoEnvelope eco, float altitudeMetres)
        {
            if (eco == null) return Color.white;

            PhenologyState state = PhenologyFor(eco, altitudeMetres);

            return Phenology.AppearanceColour(eco, InterpolatedSeasonPhase, state,
                                              ResolvedWetness,
                                              SnowCoverFractionAt(altitudeMetres));
        }
    }
}
