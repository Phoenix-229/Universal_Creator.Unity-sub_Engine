// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WeatherCellField.cs
//  Purpose: The space-time pressure field. Answers "what is the pressure here,
//           now?" in closed form - the zonal mean profile plus the synoptic
//           anomaly on top of it - and it is the only place the weather layer
//           gets its structure from.
//
//  WHY A LATTICE OF MOVING CENTRES AND NOT LAYERED NOISE. Four noise lookups for
//  cloud, dryness, humidity and storm activity are deterministic and cheap, and
//  they produce weather with no structure: nothing arrives, nothing passes,
//  nothing has a direction of travel, and a storm sits where it sits for no reason
//  anybody can point at. What a sky actually reads as is a pressure system - a
//  smooth bump in a field - crossing overhead. So the bumps are placed by
//  construction, one per lattice cell per generation, and the field is their sum.
//
//  WHY IT STILL CANNOT DRIFT OUT OF SYNC. Nothing here is integrated. A centre's
//  position is its birthplace plus its steering velocity times the time elapsed,
//  and both of those are functions of the cell index and the seed, so sampling any
//  point at any moment costs the same and returns the same answer on the first
//  visit and on the thousandth. There is no accumulated state to depend on frame
//  rate, on chunk order, or on which machine is asking.
//
//  THE SAMPLE POINT MOVES, NOT THE CENTRES. A system travelling a thousand
//  kilometres a day leaves its home cell within hours, and looking for it in the
//  cells around the sample point would then find nothing there. Every centre in one
//  generation shares one steering velocity, so the query point is shifted back
//  along that velocity instead and the centres stay where the lattice put them.
//  That is exact rather than an approximation: the only thing it assumes is that
//  the steering at the sample point is also the steering of the systems reaching
//  it, which across a neighbourhood a few hundred kilometres wide it is.
//
//  WHY THE KERNEL IS COMPACT AND NOT A LITERAL GAUSSIAN. A Gaussian never reaches
//  zero, so a search wide enough to hide its truncation is eleven cells across -
//  a hundred and twenty-one lookups per generation for a fraction of a hectopascal.
//  A bump that reaches zero exactly at the centre's own radius has the same bell
//  shape over the range that matters and makes a five-by-five search provably
//  sufficient instead of approximately sufficient: no centre outside it can be
//  within its own radius of the sample point, whatever the jitter did. The field is
//  then continuous by construction rather than by tolerance.
//
//  COST. Two generations, twenty-five cells each, two hashes per cell to place it
//  and four more only for the handful close enough to matter. Around a hundred and
//  fifty integer hashes, one sine for the steering and one power for each
//  contributing centre - affordable per chunk per tick, and cheap enough that the
//  wind gradient can afford to sample it four more times.
//
//  Allocation-free and thread-safe once bound. The lattice's permutation table is
//  read-only and every local here is a value.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// A deterministic field of pressure systems that are born, deepen, travel,
    /// fill and die. Bind it to a seed once, then ask for the anomaly at a
    /// position and a time.
    /// </summary>
    public sealed class WeatherCellField
    {
        // =====================================================================
        //  HASH STREAMS
        //
        //  Six attributes are drawn per centre and each one needs its own stream.
        //  Sharing a stream ties two attributes together, and the memorable version
        //  of that bug uses one draw for both jitter axes: every centre then sits on
        //  its cell's diagonal and the lattice shows through the world as banding at
        //  forty-five degrees. Offsetting the lookup coordinates rather than the
        //  sub-seed makes independence structural - two attributes of one centre
        //  read different lattice indices by construction, whatever the seed did,
        //  because the offset pairs are distinct.
        // =====================================================================

        private const int LatticeHashOffset = 22801763;

        private const int JitterXHashX = 0;
        private const int JitterXHashZ = 0;
        private const int JitterZHashX = 7919;
        private const int JitterZHashZ = 10007;
        private const int BirthHashX = 1543;
        private const int BirthHashZ = 3011;
        private const int AmplitudeHashX = 4001;
        private const int AmplitudeHashZ = 6007;
        private const int RadiusHashX = 8009;
        private const int RadiusHashZ = 9011;
        private const int PolarityHashX = 11003;
        private const int PolarityHashZ = 12007;

        private Noise lattice;
        private float cellM = AtmoConstants.WeatherCellSpacingKm
                              * AtmoConstants.MetresPerKilometreLattice;

        // World north as a unit vector in XZ, cached because the rotation is the
        // same for every sample and a sine per sample for a value that cannot have
        // changed is a sine wasted.
        private float northX;
        private float northZ = 1f;

        /// <summary>
        /// Binds the field to a world seed, with world north pointing along +Z.
        /// </summary>
        public void Bind(int seed)
        {
            Bind(seed, 0f);
        }

        /// <summary>
        /// Binds the field to a world seed and the world's compass bearing of north,
        /// which is what decides whether "weather arrives from the west" means from
        /// -X or from somewhere else. Bearing zero is +Z and the bearing turns
        /// clockwise seen from above, the same convention the sun rig reads.
        /// </summary>
        public void Bind(int seed, float northBearingDeg)
        {
            lattice = new Noise(seed + LatticeHashOffset);

            cellM = Mathf.Max(1f, AtmoConstants.WeatherCellSpacingKm
                                  * AtmoConstants.MetresPerKilometreLattice);

            float bearingRad = northBearingDeg * Mathf.Deg2Rad;
            northX = Mathf.Sin(bearingRad);
            northZ = Mathf.Cos(bearingRad);
        }

        /// <summary>
        /// False until <see cref="Bind"/> has been called. An unbound field reports a
        /// flat pressure everywhere rather than throwing, so a world that has not
        /// switched the synoptic model on behaves exactly as it did before it existed.
        /// </summary>
        public bool IsBound { get { return lattice != null; } }

        // =====================================================================
        //  STEERING
        //
        //  The three circulation cells are 30 degrees wide each, which is the whole
        //  reason one sine reproduces all of them: sin(pi * lat / 30) is zero at the
        //  equator, at the subtropical high, at the subpolar low and at the pole,
        //  and it changes sign between each pair. Negate it and the tropics get
        //  easterlies, the mid-latitudes get westerlies and the polar cap gets
        //  easterlies again - which is why weather arrives from the west at fifty
        //  degrees and from the east at fifteen, out of one term and no branching
        //  over what kind of place this is.
        //
        //  The band speeds differ, so they are applied piecewise. That introduces no
        //  discontinuity, because the speed only changes at a latitude where the sine
        //  is exactly zero.
        // =====================================================================

        /// <summary>
        /// m/s. Mean speed and direction systems travel at this latitude, as
        /// (eastward, northward). Symmetric across the equator in the eastward
        /// component - westerlies are westerly in both hemispheres - and mirrored in
        /// the northward one, since a track tilts towards whichever pole it is under.
        /// </summary>
        public static Vector2 SteeringCompassMPerS(float latitudeDeg)
        {
            float clamped = Mathf.Clamp(latitudeDeg,
                                        -ClockMath.PoleLatitudeDeg,
                                        ClockMath.PoleLatitudeDeg);

            float phi = Mathf.Abs(clamped);

            float shape = -Mathf.Sin(Mathf.PI * phi
                                     / AtmoConstants.SubtropicalHighLatitudeDeg);

            float speed;

            if (phi < AtmoConstants.SubtropicalHighLatitudeDeg)
                speed = AtmoConstants.TradeWindSteeringMPerS;
            else if (phi < AtmoConstants.SubpolarLowLatitudeDeg)
                speed = AtmoConstants.WesterlySteeringMPerS;
            else
                speed = AtmoConstants.PolarEasterlySteeringMPerS;

            float eastward = shape * speed;

            // Poleward in the westerlies, equatorward in the trades, because the tilt
            // follows the flow rather than being imposed on it.
            float hemisphere = clamped < 0f ? -1f : 1f;
            float northward = eastward * AtmoConstants.SynopticTrackTiltFraction
                              * hemisphere;

            return new Vector2(eastward, northward);
        }

        /// <summary>
        /// m/s. The same steering rotated into world XZ using the world's north
        /// bearing, which is the frame the lattice itself lives in.
        /// </summary>
        public Vector2 SteeringWorldMPerS(float latitudeDeg)
        {
            return CompassToWorldXZ(SteeringCompassMPerS(latitudeDeg));
        }

        /// <summary>
        /// Turns a compass vector - x eastward, y northward - into world XZ. Here
        /// rather than in each consumer so that a world whose north is not +Z is
        /// handled in one place: north is (northX, northZ) and east is that turned a
        /// quarter clockwise seen from above, which is (northZ, -northX).
        /// </summary>
        public Vector2 CompassToWorldXZ(Vector2 compass)
        {
            return new Vector2(compass.y * northX + compass.x * northZ,
                               compass.y * northZ - compass.x * northX);
        }

        /// <summary>
        /// Unit vector in world XZ pointing to compass north. Exposed because
        /// anything that differentiates this field has to probe along the compass
        /// and not along the axes, and the bearing is held here.
        /// </summary>
        public Vector2 WorldNorthXZ { get { return new Vector2(northX, northZ); } }

        /// <summary>
        /// Unit vector in world XZ pointing to compass east - north turned a quarter
        /// clockwise seen from above, which is the same relation the steering above
        /// is composed with.
        /// </summary>
        public Vector2 WorldEastXZ { get { return new Vector2(northZ, -northX); } }

        // =====================================================================
        //  TIME
        //
        //  The field's clock is a continuous count of days, and it is derived from
        //  the calendar rather than from elapsed simulated seconds on purpose: the
        //  promise is that the same world at the same day and hour gives the same
        //  weather, and a counter that remembers how the author got there cannot
        //  keep that promise.
        // =====================================================================

        /// <summary>
        /// days. Continuous time from a calendar reading. Precision is the one thing
        /// to know about it: a float holds a day to within a minute out to a few
        /// hundred years of simulated calendar, which is far past any world that has
        /// been authored, and degrades gracefully rather than jumping past that.
        /// </summary>
        public static float TimeDays(int year, int dayOfYear, float localHour)
        {
            return year * AtmoConstants.DaysPerCalendarYear
                   + (dayOfYear - 1)
                   + localHour / ClockState.HoursPerDay;
        }

        /// <summary>days. Continuous time from a resolved clock.</summary>
        public static float TimeDays(ClockState clock)
        {
            return TimeDays(clock.year, clock.dayOfYear, clock.localHour);
        }

        // =====================================================================
        //  SAMPLING
        //
        //  WHY EXACTLY TWO GENERATIONS. Each centre is born somewhere inside its
        //  generation's epoch and lives for exactly one epoch, so at any moment the
        //  living centres belong to the current generation and the one before it and
        //  to no others - the arithmetic is closed, not a truncation. A centre whose
        //  birth phase has not been reached yet is simply not there, and one whose
        //  lifetime has run out is simply gone, both of them at an amplitude of zero,
        //  so a generation boundary is not an event the field can be caught at.
        //
        //  Per cell that means one live centre on average and never two of the same
        //  cell at once: at the instant a cell's older centre dies its newer one is
        //  born, both at zero. That handover is the whole reason systems fade in and
        //  out here rather than popping.
        // =====================================================================

        /// <summary>
        /// hPa. Deviation from the mean sea-level pressure at this position and time.
        /// Negative under a low, positive under a high, and bounded by the deepest
        /// amplitude the model can draw. This is an anomaly and not a pressure: the
        /// mean profile it sits on belongs to the general-circulation baseline, which
        /// owns it so that there is one place a latitude's mean pressure is decided.
        /// </summary>
        public float PressureAnomalyHpa(float worldX, float worldZ,
                                        float latitudeDeg, float timeDays)
        {
            if (lattice == null) return 0f;

            Vector2 steer = SteeringWorldMPerS(latitudeDeg);

            float epochs = timeDays / AtmoConstants.SynopticEpochDays;
            int current = Mathf.FloorToInt(epochs);

            return GenerationAnomalyHpa(worldX, worldZ, steer, epochs, current)
                   + GenerationAnomalyHpa(worldX, worldZ, steer, epochs, current - 1);
        }

        /// <summary>
        /// hPa. One generation's contribution. <paramref name="generation"/> is the
        /// epoch index whose centres are being summed; <paramref name="epochs"/> is
        /// the current time in epochs, so their difference is this generation's age.
        /// </summary>
        private float GenerationAnomalyHpa(float worldX, float worldZ,
                                           Vector2 steerMPerS,
                                           float epochs, int generation)
        {
            float ageEpochs = epochs - generation;

            float driftSeconds = ageEpochs * AtmoConstants.SynopticEpochDays
                                 * ClockState.SecondsPerDay;

            // Into this generation's own moving frame. Shared drift is what makes the
            // shift exact and the search finite.
            float queryX = worldX - steerMPerS.x * driftSeconds;
            float queryZ = worldZ - steerMPerS.y * driftSeconds;

            int baseX = Mathf.FloorToInt(queryX / cellM);
            int baseZ = Mathf.FloorToInt(queryZ / cellM);

            float widestM = AtmoConstants.SynopticRadiusMaxKm
                            * AtmoConstants.MetresPerKilometreLattice;

            float widest2 = widestM * widestM;

            int span = AtmoConstants.SynopticSearchCells;
            float sum = 0f;

            for (int oz = -span; oz <= span; oz++)
            {
                int cz = baseZ + oz;

                for (int ox = -span; ox <= span; ox++)
                {
                    int cx = baseX + ox;

                    float px = (cx + lattice.Random(cx + JitterXHashX, generation,
                                                    cz + JitterXHashZ)) * cellM;

                    float pz = (cz + lattice.Random(cx + JitterZHashX, generation,
                                                    cz + JitterZHashZ)) * cellM;

                    float dx = queryX - px;
                    float dz = queryZ - pz;
                    float d2 = dx * dx + dz * dz;

                    // The cheapest rejection there is, taken before any further
                    // hashing: nothing beyond the widest radius the model can draw
                    // can reach this point, whatever it drew.
                    if (d2 >= widest2) continue;

                    float bornAtEpochs = lattice.Random(cx + BirthHashX, generation,
                                                        cz + BirthHashZ);

                    float life01 = ageEpochs - bornAtEpochs;
                    if (life01 <= 0f || life01 >= 1f) continue;

                    float radiusM = Mathf.Lerp(AtmoConstants.SynopticRadiusMinKm,
                                               AtmoConstants.SynopticRadiusMaxKm,
                                               lattice.Random(cx + RadiusHashX,
                                                              generation,
                                                              cz + RadiusHashZ))
                                    * AtmoConstants.MetresPerKilometreLattice;

                    float shape01 = Kernel01(d2, radiusM * radiusM);
                    if (shape01 <= 0f) continue;

                    float depth = lattice.Random(cx + AmplitudeHashX, generation,
                                                 cz + AmplitudeHashZ);

                    float amplitudeHpa =
                        AtmoConstants.SynopticAmplitudeMinHpa
                        + (AtmoConstants.SynopticAmplitudeMaxHpa
                           - AtmoConstants.SynopticAmplitudeMinHpa)
                          * Mathf.Pow(depth,
                                      AtmoConstants.SynopticAmplitudeRarityExponent);

                    float polarity =
                        lattice.Random(cx + PolarityHashX, generation,
                                       cz + PolarityHashZ)
                        < AtmoConstants.SynopticLowFraction01 ? -1f : 1f;

                    sum += polarity * amplitudeHpa * shape01 * Envelope01(life01);
                }
            }

            return sum;
        }

        // =====================================================================
        //  THE GENERAL-CIRCULATION BASELINE
        //
        //  WHY THE MEAN PROFILE LIVES BESIDE THE ANOMALIES. A pressure is a mean
        //  plus a departure from it, and splitting the two across two files would
        //  mean a consumer could add one without the other and get a plausible
        //  number that is wrong by fifteen hectopascals. One file, two functions,
        //  and a third that is their sum so there is nothing to forget.
        //
        //  WHY THIS IS THE WHOLE OF THE RAINY SEASON. The profile is measured from
        //  the ITCZ rather than from the equator, so the entire circulation marches
        //  with the sun instead of only its middle node. A place at twenty-five
        //  degrees then sits under the trough for part of the year and under the
        //  subtropical ridge for the rest, and its cloud and rain follow from
        //  dayOfYear with nothing seasonal authored anywhere.
        //
        //  WHY THE BANDS ARE BLENDED WITH A SMOOTHSTEP AND NOT A STRAIGHT LINE. A
        //  piecewise-linear profile is continuous but kinked, and the wind that the
        //  gradient task takes out of this field would jump at every band edge. A
        //  smoothstep between the nodes is flat at both ends of each segment, so the
        //  profile and its slope are both continuous across a node.
        // =====================================================================

        /// <summary>
        /// deg. Latitude of the intertropical convergence zone on this day. It
        /// follows the sun, so it is a share of the solar declination rather than a
        /// migration of its own - which is what makes a world with no axial tilt hold
        /// it on the equator all year instead of marching it about for no reason.
        /// </summary>
        public static float ItczLatitudeDeg(int dayOfYear, float axialTiltDeg)
        {
            return ClockMath.DeclinationDegFromWinterPhase01(
                       ClockMath.WinterPhase01(dayOfYear), axialTiltDeg)
                   * AtmoConstants.ItczMigrationFractionOfDeclination;
        }

        /// <summary>
        /// hPa. The zonal mean sea-level pressure at this latitude, with the bands
        /// shifted to wherever the ITCZ is today. This is the profile the anomalies
        /// sit on, and it is the only place a latitude's mean pressure is decided.
        /// </summary>
        public static float MeanSeaLevelPressureHpa(float latitudeDeg,
                                                    float itczLatitudeDeg)
        {
            // Distance from the trough, capped at the pole so a latitude on the far
            // side of a migrated ITCZ lands on the polar node rather than running
            // off the end of the profile.
            float phi = Mathf.Min(Mathf.Abs(latitudeDeg - itczLatitudeDeg),
                                  ClockMath.PoleLatitudeDeg);

            float offsetHpa;

            if (phi < AtmoConstants.SubtropicalHighLatitudeDeg)
            {
                offsetHpa = Mathf.SmoothStep(
                    AtmoConstants.ItczPressureOffsetHpa,
                    AtmoConstants.SubtropicalHighPressureOffsetHpa,
                    phi / AtmoConstants.SubtropicalHighLatitudeDeg);
            }
            else if (phi < AtmoConstants.SubpolarLowLatitudeDeg)
            {
                offsetHpa = Mathf.SmoothStep(
                    AtmoConstants.SubtropicalHighPressureOffsetHpa,
                    AtmoConstants.SubpolarLowPressureOffsetHpa,
                    (phi - AtmoConstants.SubtropicalHighLatitudeDeg)
                    / (AtmoConstants.SubpolarLowLatitudeDeg
                       - AtmoConstants.SubtropicalHighLatitudeDeg));
            }
            else
            {
                offsetHpa = Mathf.SmoothStep(
                    AtmoConstants.SubpolarLowPressureOffsetHpa,
                    AtmoConstants.PolarHighPressureOffsetHpa,
                    (phi - AtmoConstants.SubpolarLowLatitudeDeg)
                    / (ClockMath.PoleLatitudeDeg
                       - AtmoConstants.SubpolarLowLatitudeDeg));
            }

            return AtmoConstants.SeaLevelPressureHpa + offsetHpa;
        }

        /// <summary>
        /// hPa. Absolute mean sea-level pressure here and now: the zonal profile with
        /// this moment's synoptic anomaly on top of it. Reduce it to an altitude with
        /// the barometric formula in the thermodynamics file; do not re-derive it.
        /// </summary>
        public float PressureHpa(float worldX, float worldZ, float latitudeDeg,
                                 float itczLatitudeDeg, float timeDays)
        {
            return MeanSeaLevelPressureHpa(latitudeDeg, itczLatitudeDeg)
                   + PressureAnomalyHpa(worldX, worldZ, latitudeDeg, timeDays);
        }

        // =====================================================================
        //  SHAPE
        // =====================================================================

        /// <summary>
        /// 0-1 across a centre's life: nothing at birth, full at maturity, nothing
        /// again at death, and flat at all three so a system deepens and fills
        /// instead of switching on. The flatness at the ends is what matters - a
        /// linear rise from zero still arrives at a visible rate.
        /// </summary>
        private static float Envelope01(float life01)
        {
            // Distance to the nearer end of the life, so the curve is symmetric and
            // maturity is the half-way point.
            float towardMaturity = Mathf.Min(life01, 1f - life01);

            return Mathf.SmoothStep(0f, 1f, towardMaturity / 0.5f);
        }

        /// <summary>
        /// 0-1 fall-off from a centre. Squared distances throughout, because the
        /// bump is a function of the square and a square root here would be taken a
        /// hundred times a sample to be squared again.
        /// </summary>
        private static float Kernel01(float distance2, float radius2)
        {
            if (radius2 <= 0f) return 0f;

            float q2 = distance2 / radius2;
            if (q2 >= 1f) return 0f;

            float remaining = 1f - q2;
            return remaining * remaining;
        }
    }
}
