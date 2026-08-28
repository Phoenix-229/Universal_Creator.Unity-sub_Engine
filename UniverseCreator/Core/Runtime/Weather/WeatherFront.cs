// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    WeatherFront.cs
//  Purpose: What the pressure field implies once you stand in it: the wind, and
//           the fronts that arrive with the systems. Nothing here holds state -
//           every answer is a function of the field, a position and a moment.
//
//  WHY THE WIND IS DIFFERENTIATED FROM THE FIELD AND NOT AUTHORED BESIDE IT. Wind
//  and pressure are not two facts about a place, they are one fact seen twice. An
//  authored wind that happens to blow near a low is a coincidence that breaks the
//  moment the low moves; a wind taken as the gradient of the field cannot be
//  inconsistent with the field, because it is the field. So the sky's barometer
//  and the grass bending in the same breeze come from one source, and the day a
//  system's radius is retuned they both change together.
//
//  WHY ONLY THE ANOMALY IS DIFFERENTIATED. The zonal mean profile also has a
//  slope, and taking the gradient of the whole pressure would recover a mean wind
//  from it - a second answer to a question the steering velocity already answers.
//  Physically they are the same thing: systems drift with the mean flow, so the
//  flow that carries them is the flow you feel. One of them has to own it, and the
//  steering owns it because the field already uses it to move the centres. What is
//  added here is the departure from it, which is what a passing low actually is.
//
//  BUYS BALLOT, AND WHY THE SIGN IS NOT A CONVENTION. Air pushed from high to low
//  is turned by the planet's rotation until the turning balances the push, and it
//  settles blowing along the isobars rather than across them - low pressure on the
//  left in the northern hemisphere, on the right in the southern. That reversal is
//  not a hemisphere flag anywhere in this file: it is the sign of sin(latitude) in
//  the Coriolis parameter, and everything that reverses reverses because of it.
//
//  THE EQUATOR IS A REAL LIMIT, NOT AN EDGE CASE. The Coriolis parameter vanishes
//  there, so the balanced speed divides by zero: the honest reading of that is not
//  an infinite wind but that nothing is balancing the push, and the air simply
//  flows down the gradient. So the balance is faded out below a latitude where it
//  stops describing the flow, and the same fade carries the hemisphere sign - which
//  is what keeps the wind continuous across the equator instead of reversing
//  through a fifty degree jump.
//
//  COST. Four field samples for the central difference and four sines, against a
//  field sample that is itself around a hundred and fifty integer hashes. The
//  gradient is what this costs, and it is why the probe is a fixed width rather
//  than an adaptive one.
//
//  Allocation-free and thread-safe: static, stateless, and every local is a value.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// The wind a pressure field implies, and the fronts that travel with its
    /// systems. Pass in a bound <see cref="WeatherCellField"/>; nothing is cached.
    /// </summary>
    public static class WeatherFront
    {
        // =====================================================================
        //  CORIOLIS
        //
        //  Three layers, because the self-test has to be able to fail on one of
        //  them rather than on their product, and because the balance below is
        //  wanted in two places and must not be spelled out twice.
        // =====================================================================

        /// <summary>
        /// The 2 in f = 2 Omega sin(phi). It falls out of the derivation, so it is
        /// named here rather than made tunable in the constants file.
        /// </summary>
        private const float CoriolisParameterFactor = 2f;

        /// <summary>
        /// 1/s. The Coriolis parameter. Signed: positive in the northern hemisphere,
        /// negative in the southern, zero on the equator - which is the whole of the
        /// hemisphere reversal in the wind that follows.
        /// </summary>
        public static float CoriolisParameterPerS(
            float latitudeDeg,
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            return CoriolisParameterFactor * angularVelocityRadPerS
                   * Mathf.Sin(latitudeDeg * Mathf.Deg2Rad);
        }

        /// <summary>
        /// 1/s. Magnitude of the parameter at the latitude where geostrophic balance
        /// stops describing the flow. Used as a floor, so the balanced speed has a
        /// ceiling instead of a singularity at the equator.
        /// </summary>
        public static float CoriolisFloorPerS(
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            return Mathf.Abs(CoriolisParameterPerS(
                AtmoConstants.GeostrophicFloorLatitudeDeg, angularVelocityRadPerS));
        }

        /// <summary>
        /// 1/s. The floored magnitude that the balanced speed actually divides by.
        /// </summary>
        public static float CoriolisMagnitudePerS(
            float latitudeDeg,
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            return Mathf.Max(
                Mathf.Abs(CoriolisParameterPerS(latitudeDeg, angularVelocityRadPerS)),
                CoriolisFloorPerS(angularVelocityRadPerS));
        }

        /// <summary>
        /// -1 to 1. How far rotation governs the flow here, carrying the hemisphere
        /// with it: 1 well into the northern mid-latitudes, -1 in the southern, and
        /// passing smoothly through zero on the equator where nothing balances the
        /// pressure gradient. One number does two jobs - it blends the wind between
        /// balanced and down-gradient, and it turns the surface wind the right way
        /// across the isobars - which is what makes both of them reverse together
        /// and neither of them jump at the equator.
        /// </summary>
        public static float RotationalBalance(
            float latitudeDeg,
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            return CoriolisParameterPerS(latitudeDeg, angularVelocityRadPerS)
                   / CoriolisMagnitudePerS(latitudeDeg, angularVelocityRadPerS);
        }

        // =====================================================================
        //  THE PRESSURE GRADIENT
        //
        //  WHY A CENTRAL DIFFERENCE AND NOT AN ANALYTIC DERIVATIVE. The field is
        //  differentiable in closed form, and differentiating it by hand would be
        //  two more formulae to keep in step with the kernel and the envelope for
        //  a saving of two samples. Two copies of one shape is exactly how a
        //  retuned kernel ends up with a wind that no longer matches it.
        //
        //  WHY THE PROBE IS ALONG THE COMPASS AND NOT ALONG X AND Z. Coriolis is
        //  about the planet's rotation, so the frame the balance is written in has
        //  to be the frame the latitude is measured in. A world whose north is not
        //  +Z would otherwise get a wind rotated away from its own weather.
        // =====================================================================

        /// <summary>
        /// hPa per metre, in the compass frame: x is the eastward derivative, y the
        /// northward. Of the anomaly alone - see the note at the head of this file
        /// for why the mean profile is deliberately left out of it.
        /// </summary>
        public static Vector2 AnomalyGradientHpaPerM(WeatherCellField field,
                                                     float worldX, float worldZ,
                                                     float latitudeDeg,
                                                     float timeDays)
        {
            if (field == null) return Vector2.zero;

            float probeM = AtmoConstants.GeostrophicProbeKm
                           * AtmoConstants.MetresPerKilometre;

            Vector2 east = field.WorldEastXZ;
            Vector2 north = field.WorldNorthXZ;

            // One latitude for all four probes, not each probe's own. The probes
            // are half a tenth of a degree apart, so the difference that makes to
            // the steering is nothing - but taking it means the difference is no
            // longer a pure pressure difference, and a gradient that is partly a
            // change of steering is not a gradient.
            float eastPlus = field.PressureAnomalyHpa(
                worldX + east.x * probeM, worldZ + east.y * probeM,
                latitudeDeg, timeDays);
            float eastMinus = field.PressureAnomalyHpa(
                worldX - east.x * probeM, worldZ - east.y * probeM,
                latitudeDeg, timeDays);
            float northPlus = field.PressureAnomalyHpa(
                worldX + north.x * probeM, worldZ + north.y * probeM,
                latitudeDeg, timeDays);
            float northMinus = field.PressureAnomalyHpa(
                worldX - north.x * probeM, worldZ - north.y * probeM,
                latitudeDeg, timeDays);

            float span = probeM + probeM;

            return new Vector2((eastPlus - eastMinus) / span,
                               (northPlus - northMinus) / span);
        }

        // =====================================================================
        //  THE WIND
        //
        //  v = |grad p| / (rho * f) along the isobars, plus the mean flow the
        //  steering owns, faded to straight down-gradient flow near the equator,
        //  then turned across the isobars and slowed by friction at the surface.
        // =====================================================================

        /// <summary>
        /// m/s, compass frame - x eastward, y northward. The wind above the friction
        /// layer: the mean flow plus the balanced wind of the anomaly on top of it.
        /// Density belongs to the thermodynamics file - take it from
        /// <c>AtmoMath.AirDensityKgPerM3</c> rather than assuming a sea-level value,
        /// because a plateau's thinner air is a faster wind for the same gradient.
        /// </summary>
        public static Vector2 BalancedWindCompassMPerS(
            WeatherCellField field, float worldX, float worldZ,
            float latitudeDeg, float timeDays, float airDensityKgPerM3,
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            Vector2 mean = WeatherCellField.SteeringCompassMPerS(latitudeDeg);

            if (field == null || airDensityKgPerM3 <= 0f) return mean;

            Vector2 gradHpaPerM = AnomalyGradientHpaPerM(
                field, worldX, worldZ, latitudeDeg, timeDays);

            float gradHpaMagnitude = gradHpaPerM.magnitude;
            float gradPaPerM = gradHpaMagnitude
                               * AtmoConstants.PascalsPerHectopascal;

            // A flat anomaly has no direction to give, and asking for one would be
            // asking for the direction of a zero vector.
            if (gradPaPerM <= 0f) return mean;

            // The gradient points uphill, toward higher pressure. Turned a quarter
            // anticlockwise it lies along the isobars with low pressure on its left,
            // which is the northern hemisphere sense; the balance below carries the
            // sign, so the southern hemisphere needs nothing of its own.
            //
            // Divide by the magnitude by hand rather than through Vector2.normalized:
            // a gradient in hectopascals per metre is a naturally tiny number - even a
            // sharp one-hectopascal-per-hundred-kilometre front is 1e-5 - and
            // normalized snaps anything under its 1e-5 epsilon to zero, which would
            // throw the wind's whole direction away and leave nothing but the mean
            // flow. The zero-length case is already ruled out one line above.
            Vector2 gradUnit = gradHpaPerM / gradHpaMagnitude;
            Vector2 balancedDir = new Vector2(-gradUnit.y, gradUnit.x);

            float balance = RotationalBalance(latitudeDeg, angularVelocityRadPerS);

            // Perpendicular unit vectors, so the blend is never shorter than its own
            // diagonal and never degenerate - it only has to be renormalised.
            Vector2 dir = Vector2.Lerp(-gradUnit,
                                       balancedDir * Mathf.Sign(balance),
                                       Mathf.Abs(balance)).normalized;

            float speed = gradPaPerM
                          / (airDensityKgPerM3
                             * CoriolisMagnitudePerS(latitudeDeg,
                                                     angularVelocityRadPerS));

            return mean + dir * Mathf.Min(speed, AtmoConstants.BalancedWindMaxMPerS);
        }

        /// <summary>
        /// m/s, compass frame. The wind at the ground: the balanced wind turned
        /// across the isobars toward low pressure and slowed, both by friction. The
        /// turn scales with the same balance that shapes the wind aloft, so it
        /// reverses with the hemisphere and fades out at the equator rather than
        /// flipping through fifty degrees as the latitude changes sign.
        /// </summary>
        public static Vector2 SurfaceWindCompassMPerS(
            WeatherCellField field, float worldX, float worldZ,
            float latitudeDeg, float timeDays, float airDensityKgPerM3,
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            Vector2 aloft = BalancedWindCompassMPerS(
                field, worldX, worldZ, latitudeDeg, timeDays,
                airDensityKgPerM3, angularVelocityRadPerS);

            float turnDeg = AtmoConstants.SurfaceFrictionTurnDeg
                            * RotationalBalance(latitudeDeg,
                                                angularVelocityRadPerS);

            return TurnCompass(aloft, turnDeg)
                   * AtmoConstants.SurfaceFrictionSpeedFactor;
        }

        /// <summary>
        /// m/s in world XZ. What a shader, a grass blade or a sail wants. Zero for a
        /// null field, since without one there is no bearing to turn the compass by.
        /// </summary>
        public static Vector2 SurfaceWindWorldMPerS(
            WeatherCellField field, float worldX, float worldZ,
            float latitudeDeg, float timeDays, float airDensityKgPerM3,
            float angularVelocityRadPerS = AtmoConstants.EarthAngularVelocityRadPerS)
        {
            if (field == null) return Vector2.zero;

            return field.CompassToWorldXZ(SurfaceWindCompassMPerS(
                field, worldX, worldZ, latitudeDeg, timeDays,
                airDensityKgPerM3, angularVelocityRadPerS));
        }

        /// <summary>
        /// Turns a compass vector anticlockwise - east toward north - by an angle in
        /// degrees. Anticlockwise is the direction of low pressure in the northern
        /// hemisphere, which is why the friction turn above passes its angle in
        /// signed rather than turning one way and negating.
        /// </summary>
        private static Vector2 TurnCompass(Vector2 compass, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);

            return new Vector2(compass.x * cos - compass.y * sin,
                               compass.x * sin + compass.y * cos);
        }

        // =====================================================================
        //  FRONTS - reading the passage from the field's own time series
        //
        //  A front is not a thing the field stores: it is what a fixed observer
        //  feels as one of the moving lows sweeps past. Everything below is read
        //  from that one point's barometer over a short window.
        //
        //    phase - from the pressure tendency dp/dt. Falling ahead of the trough
        //            is approaching, flat at the trough is overhead, rising behind
        //            it is departing.
        //    kind  - ahead of the trough the air is warming (warm front); behind it
        //            the cold air pushes in (cold front); deep near the centre the
        //            warm and cold sectors have wrapped into an occlusion; where the
        //            steering barely advects, the trough lingers (stationary).
        //    shift - the qualitative veer or back. A cold front veers, a warm front
        //            backs, and both reverse south of the equator. That reversal is
        //            stated as the rule it is: the balanced wind vector is symmetric
        //            across the equator, so a veer that flips hemisphere cannot be
        //            differenced back out of it.
        // =====================================================================

        public enum FrontKind { None, Warm, Cold, Occluded, Stationary }

        public enum FrontPhase { None, Approaching, Overhead, Departing }

        public struct FrontReading
        {
            public FrontKind Kind;
            public FrontPhase Phase;
            public float DepthHpa;
            public float TendencyHpaPerDay;
            public float WindShiftSense;
        }

        /// <summary>
        /// hPa per day, signed. Central time difference of the pressure anomaly at a
        /// fixed point over the front window: negative while a low approaches, near
        /// zero as the trough sits overhead, positive as it departs.
        /// </summary>
        public static float PressureTendencyHpaPerDay(
            WeatherCellField field, float worldX, float worldZ,
            float latitudeDeg, float timeDays)
        {
            if (field == null) return 0f;

            float dt = AtmoConstants.FrontProbeDays;
            float before = field.PressureAnomalyHpa(
                worldX, worldZ, latitudeDeg, timeDays - dt);
            float after = field.PressureAnomalyHpa(
                worldX, worldZ, latitudeDeg, timeDays + dt);

            return (after - before) / (dt + dt);
        }

        /// <summary>
        /// +1 for a veer - the wind turning clockwise - and -1 for a back, 0 where
        /// there is no clear shift. A cold front veers and a warm front backs; the
        /// hemisphere sign rides on the same rotational balance the wind does, so both
        /// reverse south of the equator and fade to nothing across it.
        /// </summary>
        public static float WindShiftSense(FrontKind kind, float latitudeDeg)
        {
            float hemisphere = RotationalBalance(latitudeDeg);
            if (kind == FrontKind.Cold) return hemisphere;
            if (kind == FrontKind.Warm) return -hemisphere;
            return 0f;
        }

        /// <summary>
        /// Reads the whole front at a point in one pass: how deep the trough is, which
        /// way the barometer is moving, and from those the kind, the phase, and the
        /// sense the wind will shift. Depth is hPa below the mean, positive in a low;
        /// a point not below the mean by the threshold carries no front at all.
        /// </summary>
        public static FrontReading ResolveFront(
            WeatherCellField field, float worldX, float worldZ,
            float latitudeDeg, float timeDays)
        {
            FrontReading reading = default;
            if (field == null) return reading;

            float depth = -field.PressureAnomalyHpa(
                worldX, worldZ, latitudeDeg, timeDays);
            reading.DepthHpa = depth;
            if (depth < AtmoConstants.FrontDepthThresholdHpa) return reading;

            float tendency = PressureTendencyHpaPerDay(
                field, worldX, worldZ, latitudeDeg, timeDays);
            reading.TendencyHpaPerDay = tendency;

            float active = AtmoConstants.FrontActiveTendencyHpaPerDay;
            if (tendency < -active) reading.Phase = FrontPhase.Approaching;
            else if (tendency > active) reading.Phase = FrontPhase.Departing;
            else reading.Phase = FrontPhase.Overhead;

            float steering =
                WeatherCellField.SteeringCompassMPerS(latitudeDeg).magnitude;
            if (depth >= AtmoConstants.FrontOccludedDepthHpa)
                reading.Kind = FrontKind.Occluded;
            else if (steering < AtmoConstants.FrontStationarySpeedMPerS)
                reading.Kind = FrontKind.Stationary;
            else if (tendency < 0f)
                reading.Kind = FrontKind.Warm;
            else
                reading.Kind = FrontKind.Cold;

            reading.WindShiftSense = WindShiftSense(reading.Kind, latitudeDeg);
            return reading;
        }
    }
}
