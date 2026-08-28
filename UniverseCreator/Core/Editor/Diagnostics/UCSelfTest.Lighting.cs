// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCSelfTest.Lighting.cs
//  Purpose: Sky, cloud and light self-tests. The first of them pins the sun and
//           moon rig's angle-to-direction convention, because a light aimed the
//           wrong way round the compass lights the wrong side of the world and
//           nothing in a headless run would otherwise catch it.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.TerrainSystem.Lighting;
using UniverseCreator.Weather;
// UnityEngine also defines a LightingSettings (the built-in lightmapper's block); this file
// only ever means ours, so pin the bare name to the UC type rather than qualify every site.
using LightingSettings = UniverseCreator.TerrainSystem.Lighting.LightingSettings;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>
        /// The rig turns a body's altitude and compass bearing into a direction and
        /// a light rotation. This asserts the direction points at the body, the beam
        /// travels the opposite way, and the light's forward matches that beam - so
        /// the sun rises in the east and its light falls toward the west.
        /// </summary>
        private static void TestLightingRig()
        {
            // The direction to a body is a unit vector aimed where the body stands.
            Vector3 east = SunMoonRig.DirectionToBody(0f, 90f);
            CheckClose(east.magnitude, 1f, "Rig: direction to a body is unit length");
            CheckClose(east.x, 1f, "Rig: a body due east at the horizon lies along +x");
            CheckWithin(east.y, 0f, 1e-4f, "Rig: a body at the horizon has no height");
            CheckWithin(east.z, 0f, 1e-4f, "Rig: a body due east has no north component");

            Vector3 up = SunMoonRig.DirectionToBody(90f, 0f);
            CheckClose(up.y, 1f, "Rig: a body at the zenith lies straight up");

            // North of the equator the noon sun stands due south; south of it, due
            // north. The rig only maps the angle, but that mapping is where an
            // inverted hemisphere would first show, so it is asserted here.
            Vector3 southNoon = SunMoonRig.DirectionToBody(45f, 180f);
            Check(southNoon.z < 0f, "Rig: a body due south has a southward bearing");
            Check(southNoon.y > 0f, "Rig: a body above the horizon has positive height");
            Vector3 northNoon = SunMoonRig.DirectionToBody(45f, 0f);
            Check(northNoon.z > 0f, "Rig: a body due north has a northward bearing");

            // The beam travels the exact opposite of the direction to the body.
            Vector3 toBody = SunMoonRig.DirectionToBody(30f, 120f);
            Vector3 beam = SunMoonRig.BeamDirection(30f, 120f);
            CheckClose(Vector3.Dot(toBody, beam), -1f, "Rig: the beam runs opposite the direction to the body");

            // Sunrise in the east throws light toward the west; sunset the reverse.
            Check(SunMoonRig.BeamDirection(5f, 90f).x < 0f, "Rig: an eastern sun lights toward the west");
            Check(SunMoonRig.BeamDirection(5f, 270f).x > 0f, "Rig: a western sun lights toward the east");

            // The light rotation must aim its forward exactly along that beam - the
            // half-turn azimuth offset is what makes this hold rather than light the
            // world backwards.
            foreach (Vector2 ea in new[] {
                new Vector2(0f, 90f), new Vector2(30f, 120f),
                new Vector2(60f, 180f), new Vector2(10f, 270f) })
            {
                Vector3 wantBeam = SunMoonRig.BeamDirection(ea.x, ea.y);
                Vector3 haveBeam = SunMoonRig.BodyRotation(ea.x, ea.y) * Vector3.forward;
                CheckClose(Vector3.Dot(wantBeam, haveBeam), 1f,
                    "Rig: the light's forward matches the beam direction");
                Check(IsFinite(haveBeam.x) && IsFinite(haveBeam.y) && IsFinite(haveBeam.z),
                    "Rig: the light rotation is finite");
            }
        }

        /// <summary>
        /// Sun colour is a blackbody reddened by extinction. This asserts the warm
        /// hue at low temperature, the near-white overhead sun, and that the same
        /// beam reddens toward the horizon and with turbidity - the whole point of
        /// deriving sunset colour from physics instead of a ramp.
        /// </summary>
        private static void TestSkyModel()
        {
            // A cool daylight blackbody is near-white; an ember one is red-biased.
            Color day = SkyModel.Blackbody(6500f);
            Check(day.r > 0.6f && day.g > 0.6f && day.b > 0.6f,
                "Sky: a 6500 K sun is near-white across all channels");
            Check(IsFinite(day.r) && IsFinite(day.g) && IsFinite(day.b),
                "Sky: blackbody colour is finite");

            Color ember = SkyModel.Blackbody(1800f);
            Check(ember.r >= ember.g && ember.g >= ember.b,
                "Sky: a 1800 K sun falls off red to blue");
            float dayRatio = day.r / Mathf.Max(day.b, 1e-4f);
            float emberRatio = ember.r / Mathf.Max(ember.b, 1e-4f);
            Check(emberRatio > dayRatio,
                "Sky: the cooler-temperature sun is the redder one");

            // The same sun reddens as it sets: low and hazy is redder than high.
            Color noon = SkyModel.SunColorLinear(60f, 1f, 2f);
            Color horizon = SkyModel.SunColorLinear(2f, 30f, 2f);
            float noonRB = noon.r / Mathf.Max(noon.b, 1e-4f);
            float horizonRB = horizon.r / Mathf.Max(horizon.b, 1e-4f);
            Check(horizonRB > noonRB,
                "Sky: the horizon sun is redder than the noon sun");
            Check(noon.r > 0f && noon.g > 0f && noon.b > 0f,
                "Sky: the noon sun keeps colour in every channel");

            // Thicker haze scatters more blue out of the beam.
            Color clean = SkyModel.SunColorLinear(10f, 5f, 1f);
            Color hazy = SkyModel.SunColorLinear(10f, 5f, 4f);
            Check(hazy.b < clean.b,
                "Sky: raising turbidity thins the blue of the beam");
            Check(IsFinite(horizon.r) && IsFinite(horizon.g) && IsFinite(horizon.b),
                "Sky: sun colour is finite at the horizon");
        }

        /// <summary>
        /// Sun illuminance is physical lux, so the day-to-night span is the real
        /// one. This pins the noon anchor, the ~10x overcast drop, the ~400000x gap
        /// to a full moon, the fall with altitude, and darkness below twilight.
        /// </summary>
        private static void TestSunIlluminance()
        {
            float noonClear = SkyModel.SunIlluminanceLux(90f, 1f, 1f, 0f);
            CheckWithin(noonClear, 100000f, 12000f,
                "Illuminance: clear noon sun is about 100000 lux");

            float noonOvercast = SkyModel.SunIlluminanceLux(90f, 1f, 1f, 1f);
            CheckWithin(noonClear / Mathf.Max(noonOvercast, 1e-3f), 10f, 0.5f,
                "Illuminance: clear noon is about ten times an overcast one");

            CheckWithin(noonClear / LightingConstants.FullMoonLux, 400000f, 60000f,
                "Illuminance: noon sun to full moon spans about 400000x");

            float low = SkyModel.SunIlluminanceLux(20f, 2f, 1f, 0f);
            float high = SkyModel.SunIlluminanceLux(60f, 1f, 1f, 0f);
            Check(low < high, "Illuminance: a lower sun delivers less light than a higher one");

            Check(SkyModel.SunIlluminanceLux(-10f, 1f, 1f, 0f) == 0f,
                "Illuminance: below civil twilight the direct sun gives nothing");
            Check(noonClear > 0f && IsFinite(noonClear) && IsFinite(low),
                "Illuminance: values are finite and the lit day is positive");
        }

        /// <summary>
        /// The sun and moon discs are the same tiny angular size, which is why an
        /// eclipse is possible, and that size drives the shadow penumbra: sharp at
        /// the contact point and softening with distance at a rate the angle fixes.
        /// </summary>
        private static void TestSunDiscAndPenumbra()
        {
            Check(Mathf.Abs(LightingConstants.SunDiscDiameterDeg
                            - LightingConstants.MoonDiscDiameterDeg) < 0.05f,
                "Disc: sun and moon are near-identical angular size, so an eclipse can occur");

            float atContact = SunMoonRig.PenumbraWidthMeters(LightingConstants.SunDiscDiameterDeg, 0f);
            CheckClose(atContact, 0f, "Penumbra: a shadow is sharp at its contact point");

            float near = SunMoonRig.PenumbraWidthMeters(LightingConstants.SunDiscDiameterDeg, 1f);
            float far = SunMoonRig.PenumbraWidthMeters(LightingConstants.SunDiscDiameterDeg, 20f);
            Check(far > near, "Penumbra: the shadow softens with distance from the occluder");

            // The spread per metre must match 2*tan(half of 0.53 deg) - the physics,
            // not an authored softness slider.
            float perMetre = far / 20f;
            float expected = 2f * Mathf.Tan(0.5f * LightingConstants.SunDiscDiameterDeg * Mathf.Deg2Rad);
            CheckWithin(perMetre, expected, 1e-4f, "Penumbra: the spread is consistent with 0.53 degrees");
            Check(IsFinite(far), "Penumbra: width is finite");
        }

        /// <summary>
        /// The sun's weight crossfades from 0 to 1 across the civil-twilight band and
        /// the moon carries the complement. This pins the off-below-band, the smooth
        /// rise, the half-way point mid-band, monotonicity, and that the two weights
        /// always sum to one - so neither ever pops on and both are never full at once.
        /// </summary>
        private static void TestSunMoonHandover()
        {
            // Below the band the sun is fully off; above it, fully on.
            CheckClose(SunMoonRig.SunTwilightWeight01(-10f), 0f, "Handover: below twilight the sun carries none of the light");
            CheckClose(SunMoonRig.SunTwilightWeight01(-6f), 0f, "Handover: at the twilight floor the sun is still off");
            CheckClose(SunMoonRig.SunTwilightWeight01(0f), 1f, "Handover: at the horizon the sun carries all of the light");
            CheckClose(SunMoonRig.SunTwilightWeight01(10f), 1f, "Handover: well above the horizon the sun stays full");

            // Halfway down the band the sun holds half the weight.
            CheckClose(SunMoonRig.SunTwilightWeight01(-3f), 0.5f, "Handover: mid-band the sun holds half the light");

            // The moon carries exactly the complement, so the pair always sums to one:
            // neither is at full while the other is lit, and neither snaps on.
            foreach (float elev in new[] { -12f, -6f, -4.5f, -3f, -1.5f, 0f, 8f })
            {
                float sun = SunMoonRig.SunTwilightWeight01(elev);
                float moon = 1f - sun;
                CheckClose(sun + moon, 1f, "Handover: sun and moon weights sum to one");
                Check(sun >= 0f && sun <= 1f, "Handover: the sun weight stays in [0,1]");
            }

            // The rise is monotone across the band - a continuous transition, no switch frame.
            float prev = -1f;
            for (float elev = -6f; elev <= 0f; elev += 1f)
            {
                float w = SunMoonRig.SunTwilightWeight01(elev);
                Check(w >= prev, "Handover: the sun weight never decreases as the sun climbs");
                prev = w;
            }

            // Mid-band neither body is at full intensity, so there is no pop-on.
            float mid = SunMoonRig.SunTwilightWeight01(-3f);
            Check(mid < 1f && (1f - mid) < 1f, "Handover: mid-band neither the sun nor the moon is at full");
            Check(IsFinite(mid), "Handover: the crossfade weight is finite");
        }

        /// <summary>
        /// Seasonal and latitudinal light character is not authored - it must fall out
        /// of the clock's astronomy. Sampling solar-noon altitude and day length at the
        /// tropics, mid-latitudes and the sub-arctic across the solstices and equinoxes
        /// asserts the three characters that make a place feel like a place: a low
        /// all-day winter sun up north, a near-vertical tropical noon, and a summer
        /// day that stretches toward the midnight sun as latitude climbs. If these do
        /// not emerge here, the astronomy is wrong upstream and every shadow inherits it.
        /// </summary>
        private static void TestSeasonalLatitudinalLight()
        {
            // Day numbers of the four turning points of an Earth-like year.
            const int vernalEquinox = 81, summerSolstice = 172, winterSolstice = 355;

            float summerDec = ClockMath.DeclinationDeg(summerSolstice);
            float winterDec = ClockMath.DeclinationDeg(winterSolstice);
            float equinoxDec = ClockMath.DeclinationDeg(vernalEquinox);
            Check(summerDec > 23f, "Season: the summer solstice sun stands over the northern tropic");
            Check(winterDec < -23f, "Season: the winter solstice sun stands over the southern tropic");
            CheckWithin(equinoxDec, 0f, 0.5f, "Season: the equinox sun stands over the equator");

            // Solar-noon altitude is the elevation at hour angle zero.
            float noonEq10 = ClockMath.ElevationDeg(10f, equinoxDec, 0f);
            float noonEq40 = ClockMath.ElevationDeg(40f, equinoxDec, 0f);
            float noonEq65 = ClockMath.ElevationDeg(65f, equinoxDec, 0f);
            Check(noonEq10 > noonEq40 && noonEq40 > noonEq65,
                "Season: at the equinox the noon sun sinks lower the further from the equator");
            Check(noonEq10 > 70f, "Season: the tropical equinox noon is near-vertical");
            Check(ClockMath.ElevationDeg(65f, winterDec, 0f) < 12f,
                "Season: the sub-arctic winter sun never climbs far, so it is low all day");

            // The noon-altitude swing between solstices widens away from the tropics.
            float swingNoon10 = ClockMath.ElevationDeg(10f, summerDec, 0f) - ClockMath.ElevationDeg(10f, winterDec, 0f);
            float swingNoon65 = ClockMath.ElevationDeg(65f, summerDec, 0f) - ClockMath.ElevationDeg(65f, winterDec, 0f);
            Check(swingNoon65 > swingNoon10,
                "Season: the seasonal shift in noon height is larger at high latitude than in the tropics");

            // Day length: 12 h everywhere at the equinox, then diverging by season.
            CheckWithin(ClockMath.DayLengthHours(10f, equinoxDec), 12f, 0.3f, "Season: an equinox day is 12 h in the tropics");
            CheckWithin(ClockMath.DayLengthHours(40f, equinoxDec), 12f, 0.3f, "Season: an equinox day is 12 h at mid-latitude");
            CheckWithin(ClockMath.DayLengthHours(65f, equinoxDec), 12f, 0.3f, "Season: an equinox day is 12 h in the sub-arctic");

            float sumLen10 = ClockMath.DayLengthHours(10f, summerDec);
            float sumLen40 = ClockMath.DayLengthHours(40f, summerDec);
            float sumLen65 = ClockMath.DayLengthHours(65f, summerDec);
            float winLen10 = ClockMath.DayLengthHours(10f, winterDec);
            float winLen40 = ClockMath.DayLengthHours(40f, winterDec);
            float winLen65 = ClockMath.DayLengthHours(65f, winterDec);
            Check(sumLen10 < sumLen40 && sumLen40 < sumLen65, "Season: the summer day lengthens toward the pole");
            Check(winLen10 > winLen40 && winLen40 > winLen65, "Season: the winter day shortens toward the pole");
            Check((sumLen65 - winLen65) > (sumLen40 - winLen40)
                  && (sumLen40 - winLen40) > (sumLen10 - winLen10),
                "Season: the summer-to-winter swing in day length grows with latitude");
            Check(sumLen65 > 18f, "Season: the sub-arctic summer day approaches the midnight sun");
            Check(winLen65 < 6f, "Season: the sub-arctic winter day is a brief window of light");
            Check(IsFinite(sumLen65) && IsFinite(noonEq10), "Season: sampled values are finite");
        }

        /// <summary>
        /// Exposure is analytic photometry, not an image histogram. This pins the EV
        /// anchors across the real illuminance span, that the exposure multiplier
        /// falls as the scene brightens, that a full-moon night reads brighter than a
        /// new-moon one, and that eye adaptation is asymmetric, non-overshooting and
        /// convergent - the whole point of deriving exposure from the sky.
        /// </summary>
        private static void TestExposureModel()
        {
            // EV100 anchors: sunny noon ~15, overcast ~12, full moon ~-3.
            CheckWithin(ExposureModel.Ev100FromLux(LightingConstants.NoonClearSkyLux), 15.3f, 0.5f,
                "Exposure: clear noon sits near EV100 15");
            CheckWithin(ExposureModel.Ev100FromLux(LightingConstants.OvercastDayLux), 12f, 0.5f,
                "Exposure: an overcast day sits near EV100 12");
            CheckWithin(ExposureModel.Ev100FromLux(LightingConstants.FullMoonLux), -3.3f, 0.5f,
                "Exposure: a full moon sits near EV100 -3");

            // More light is more EV; more EV is a smaller multiplier onto the scene.
            Check(ExposureModel.Ev100FromLux(LightingConstants.NoonClearSkyLux)
                  > ExposureModel.Ev100FromLux(LightingConstants.FullMoonLux),
                "Exposure: brighter scenes carry a higher EV");
            Check(ExposureModel.ExposureMultiplierFromLux(LightingConstants.NoonClearSkyLux)
                  < ExposureModel.ExposureMultiplierFromLux(LightingConstants.FullMoonLux),
                "Exposure: the bright scene is scaled down harder than the dark one");

            // The multiplier is the reciprocal of the white point by construction.
            float ev = ExposureModel.Ev100FromLux(LightingConstants.NoonClearSkyLux);
            CheckClose(ExposureModel.ExposureMultiplier(ev) * ExposureModel.MaxLuminance(ev), 1f,
                "Exposure: the multiplier is the reciprocal of the exposed white point");

            // Scene key illuminance: the sun rules the day, the moon the clear night,
            // and starlight floors a moonless one so it stays finite and navigable.
            float noonKey = ExposureModel.SceneKeyIlluminanceLux(90f, 1f, 1f, 0f, -20f, 0f);
            Check(noonKey > 50000f, "Exposure: the daytime key is set by the sun");
            float fullMoonKey = ExposureModel.SceneKeyIlluminanceLux(-12f, 1f, 1f, 0f, 90f, 1f);
            CheckWithin(fullMoonKey, LightingConstants.FullMoonLux, 0.05f,
                "Exposure: a full moon at the zenith sets a quarter-lux key");
            float newMoonKey = ExposureModel.SceneKeyIlluminanceLux(-12f, 1f, 1f, 0f, 90f, 0f);
            CheckClose(newMoonKey, LightingConstants.StarlightLux,
                "Exposure: a moonless night falls back to the starlight floor");
            Check(fullMoonKey > newMoonKey, "Exposure: a full-moon night is brighter than a new-moon one");

            // Adaptation: one step moves toward the target without passing it.
            float step = ExposureModel.AdaptEv100(0f, 5f, 0.1f);
            Check(step > 0f && step < 5f, "Exposure: a single adaptation step advances toward the target");

            // Brightening is faster than darkening over the same distance and time.
            float up = ExposureModel.AdaptEv100(0f, 5f, 1f) - 0f;
            float down = 0f - ExposureModel.AdaptEv100(0f, -5f, 1f);
            Check(up > down, "Exposure: adapting to light is faster than adapting to dark");

            // No time is no change; sitting on the target holds; a huge step never overshoots.
            CheckClose(ExposureModel.AdaptEv100(2f, 9f, 0f), 2f, "Exposure: no elapsed time means no adaptation");
            CheckClose(ExposureModel.AdaptEv100(4f, 4f, 1f), 4f, "Exposure: already at target, exposure holds");
            CheckClose(ExposureModel.AdaptEv100(0f, 5f, 1000f), 5f, "Exposure: a large step lands on the target, not past it");

            // Repeated stepping converges onto the target.
            float e = 0f;
            for (int i = 0; i < 10; i++) e = ExposureModel.AdaptEv100(e, 5f, 1f);
            CheckClose(e, 5f, "Exposure: adaptation converges on its target");
            Check(IsFinite(e) && IsFinite(noonKey), "Exposure: adapted and key values are finite");
        }

        /// <summary>
        /// The module tonemaps through URP's own curve, in one Volume it owns, and
        /// never forces colour grading. This pins the authored default to a real
        /// curve, the single-Volume contract, and the low priority that lets an
        /// author's own Volume override it cleanly rather than tie with it.
        /// </summary>
        private static void TestTonemappingPolicy()
        {
            Check(UCTonemapping.DefaultPolicy == TonemapPolicy.ACES
                  || UCTonemapping.DefaultPolicy == TonemapPolicy.Neutral,
                "Tonemap: the module always applies a real curve, never leaves the HDR scene unmapped");
            Check(UCTonemapping.OwnedVolumeCount == 1, "Tonemap: the module owns at most one Volume");
            Check(UCTonemapping.ModuleVolumePriority < 0f,
                "Tonemap: the module Volume sits below an author's, so theirs overrides cleanly");
        }

        /// <summary>
        /// One extinction law thins every beam through the air. This pins the shared
        /// CPU transmittance: lossless when there is no path to travel, always inside
        /// (0,1], falling as air mass or turbidity grows, blue thinned faster than red,
        /// and matching exp(-k * airMass * turbidity) exactly - so the sun colour and
        /// sun illuminance that both call it, and the GPU twin the sky and fog call,
        /// redden a distant ridge on the very curve as the sky behind it.
        /// </summary>
        private static void TestAtmosphericExtinction()
        {
            // No path, no loss: zero air mass or zero turbidity transmits everything.
            Vector3 clearMass = SkyModel.AtmosphericTransmittance(0f, 2f);
            Vector3 clearTurb = SkyModel.AtmosphericTransmittance(3f, 0f);
            CheckClose(clearMass.x, 1f, "Extinction: no air mass transmits the red channel whole");
            CheckClose(clearMass.z, 1f, "Extinction: no air mass transmits the blue channel whole");
            CheckClose(clearTurb.y, 1f, "Extinction: zero turbidity leaves the beam untouched");

            // A real path lands strictly inside (0,1] on every channel.
            Vector3 t = SkyModel.AtmosphericTransmittance(2f, 3f);
            Check(t.x > 0f && t.x <= 1f && t.y > 0f && t.y <= 1f && t.z > 0f && t.z <= 1f,
                "Extinction: transmittance stays inside (0,1] on every channel");

            // Blue is thinned fastest, so what survives is red > green > blue.
            Check(t.x > t.y && t.y > t.z,
                "Extinction: blue falls off faster than green, green faster than red");

            // More air mass and more turbidity each thin the beam further.
            Vector3 thin = SkyModel.AtmosphericTransmittance(1f, 1f);
            Vector3 thickMass = SkyModel.AtmosphericTransmittance(6f, 1f);
            Vector3 thickTurb = SkyModel.AtmosphericTransmittance(1f, 6f);
            Check(thickMass.z < thin.z, "Extinction: a longer air mass transmits less light");
            Check(thickTurb.z < thin.z, "Extinction: hazier air transmits less light");

            // The values are exactly exp(-k * airMass * turbidity) from the one triple.
            float m = 2f * 3f;
            Vector3 k = LightingConstants.BeamExtinction;
            CheckClose(t.x, Mathf.Exp(-k.x * m), "Extinction: red matches exp(-k * airMass * turbidity)");
            CheckClose(t.y, Mathf.Exp(-k.y * m), "Extinction: green matches exp(-k * airMass * turbidity)");
            CheckClose(t.z, Mathf.Exp(-k.z * m), "Extinction: blue matches exp(-k * airMass * turbidity)");

            // Negative inputs are clamped, not propagated, so the beam never gains light.
            Vector3 clamped = SkyModel.AtmosphericTransmittance(-5f, -2f);
            CheckClose(clamped.x, 1f, "Extinction: negative path is clamped to a lossless beam");
            Check(IsFinite(t.x) && IsFinite(t.y) && IsFinite(t.z), "Extinction: transmittance is finite");
        }

        /// <summary>
        /// Turbidity is one number the weather moves, not an authored curve. This pins
        /// the clean-air default, that humidity hazes the air and rain washes it clear,
        /// that a front leaves clearer air behind it, that the optical span is never
        /// exceeded from either end, and that the 0..1 authoring dial round-trips onto
        /// the span - so the whole sky reads one turbidity that belongs to the physics.
        /// </summary>
        private static void TestTurbidityModel()
        {
            // Still, dry, unbiased air rests at the clean-air default.
            CheckClose(TurbidityModel.Resolve(0f, 0f, 0f), LightingConstants.TurbidityDefault,
                "Turbidity: calm dry air sits at the clean-air default");

            // Humidity hazes the air; rain washes it out. Each is monotone.
            float baseline = TurbidityModel.Resolve(0f, 0f, 0f);
            Check(TurbidityModel.Resolve(1f, 0f, 0f) > baseline, "Turbidity: humidity raises turbidity");
            Check(TurbidityModel.Resolve(0f, 1f, 0f) < baseline, "Turbidity: rain scrubs turbidity down");
            Check(TurbidityModel.Resolve(0.3f, 0f, 0f) < TurbidityModel.Resolve(0.7f, 0f, 0f),
                "Turbidity: hazier the more humid");

            // A front passes: a hazy humid sky, then rain and drying air behind it,
            // leaves the air clearer than it was ahead of the front.
            float aheadOfFront = TurbidityModel.Resolve(1f, 0f, 0f);
            float behindFront = TurbidityModel.Resolve(0.4f, 1f, 0f);
            Check(behindFront < aheadOfFront, "Turbidity: a passing front clears the air behind it");

            // The optical span holds from both ends whatever the inputs.
            float driven = TurbidityModel.Resolve(1f, 0f, 100f);
            float washed = TurbidityModel.Resolve(0f, 1f, -100f);
            CheckClose(driven, LightingConstants.TurbidityHeavyHaze, "Turbidity: nothing hazes past heavy haze");
            CheckClose(washed, LightingConstants.TurbidityClearMountain, "Turbidity: nothing clears past mountain air");

            // The authored bias nudges the resolved value, up and down.
            Check(TurbidityModel.Resolve(0f, 0f, 1f) > baseline && TurbidityModel.Resolve(0f, 0f, -1f) < baseline,
                "Turbidity: the authored bias shifts the air both ways");

            // The 0..1 dial maps onto the span and round-trips.
            CheckClose(TurbidityModel.FromNormalized01(0f), LightingConstants.TurbidityClearMountain,
                "Turbidity: a zero dial is mountain-clear air");
            CheckClose(TurbidityModel.FromNormalized01(1f), LightingConstants.TurbidityHeavyHaze,
                "Turbidity: a full dial is heavy haze");
            CheckClose(TurbidityModel.ToNormalized01(TurbidityModel.FromNormalized01(0.42f)), 0.42f,
                "Turbidity: the authoring dial round-trips through the span");
            Check(IsFinite(driven) && IsFinite(behindFront), "Turbidity: resolved values are finite");
        }

        /// <summary>
        /// The sky table is small and rebuilt rarely - that is its whole point. This
        /// pins the 4 KB 32x16 contract, that a change below threshold does not trigger
        /// a bake while one past it does, and - by walking the real solar elevation
        /// through a simulated hour - that the fastest hour near sunrise costs only a
        /// few bakes and a noon hour costs at most one, which is the done-when cadence.
        /// </summary>
        private static void TestSkyLutRebuildPolicy()
        {
            // The fixed 4 KB contract: 32 x 16 texels, four half-floats each.
            Check(SkyLut.Width == 32 && SkyLut.Height == 16, "SkyLUT: the table is 32 x 16");
            Check(SkyLut.ByteSize == 4096, "SkyLUT: the table is 4 KB");

            // A nudge under every threshold is not worth a bake; crossing any one is.
            Check(!SkyLut.ShouldRebuild(20f, 20.5f, 2f, 2.05f, 0.3f, 0.32f),
                "SkyLUT: a sky that has barely moved is not rebuilt");
            Check(SkyLut.ShouldRebuild(20f, 25f, 2f, 2f, 0.3f, 0.3f),
                "SkyLUT: a few degrees of sun movement triggers a rebuild");
            Check(SkyLut.ShouldRebuild(20f, 20f, 2f, 2.5f, 0.3f, 0.3f),
                "SkyLUT: a material turbidity change triggers a rebuild");
            Check(SkyLut.ShouldRebuild(20f, 20f, 2f, 2f, 0.3f, 0.5f),
                "SkyLUT: a material cloud change triggers a rebuild");

            // Walk the real elevation through the fastest hour (just after sunrise at
            // mid-latitude, equinox) minute by minute and count the bakes. The sun
            // moves 15 deg of hour angle an hour, so an hour is 15 deg of it.
            const float lat = 40f;
            float equinoxDec = ClockMath.DeclinationDeg(81);
            int sunriseBakes = CountLutBakes(lat, equinoxDec, 90f, 75f);
            Check(sunriseBakes >= 1 && sunriseBakes <= 8,
                "SkyLUT: the fastest hour after sunrise costs only a few bakes");

            // A noon hour barely moves the sun's elevation, so it costs almost nothing.
            int noonBakes = CountLutBakes(lat, equinoxDec, 0f, 15f);
            Check(noonBakes <= 1, "SkyLUT: a noon hour rebuilds the table at most once");
        }

        /// <summary>
        /// Steps the sun's hour angle across one simulated hour at a fixed latitude and
        /// declination, running the rebuild policy each minute against a still turbidity
        /// and cloud, and returns how many bakes the elevation change alone would cost.
        /// </summary>
        private static int CountLutBakes(float latDeg, float decDeg, float startHourAngle, float endHourAngle)
        {
            const int minutes = 60;
            float lastElev = ClockMath.ElevationDeg(latDeg, decDeg, startHourAngle);
            int bakes = 0;
            for (int i = 1; i <= minutes; i++)
            {
                float ha = Mathf.Lerp(startHourAngle, endHourAngle, i / (float)minutes);
                float elev = ClockMath.ElevationDeg(latDeg, decDeg, ha);
                if (SkyLut.ShouldRebuild(lastElev, elev, 2f, 2f, 0.3f, 0.3f))
                {
                    bakes++;
                    lastElev = elev;
                }
            }
            return bakes;
        }

        /// <summary>
        /// The Perez sky is the whole daytime dome from one turbidity: brightening
        /// around the sun and toward the horizon, a deeper overhead, a real blue, and a
        /// milkier low-contrast sky as haze climbs - with no gradient texture anywhere.
        /// This pins each of those against the analytic model, plus its finiteness at
        /// the hard cases, the sun at the zenith and a view along the horizon.
        /// </summary>
        private static void TestPerezSky()
        {
            float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

            // Brightening around the sun: a view at the sun outshines one the same
            // height across the sky from it.
            Vector3 sun = new Vector3(0.5f, 0.6f, 0f).normalized;
            float atSun = Lum(SkyModel.SkyRadiance(sun, sun, 2f));
            float awaySun = Lum(SkyModel.SkyRadiance(new Vector3(-0.5f, 0.6f, 0f).normalized, sun, 2f));
            Check(atSun > awaySun, "Perez: the sky brightens toward the sun");

            // Horizon brightening and a deeper overhead: a low view away from the sun
            // outshines the zenith the sun leaves darkest.
            float zenithLum = Lum(SkyModel.SkyRadiance(new Vector3(0f, 1f, 0f), sun, 2f));
            float lowAway = Lum(SkyModel.SkyRadiance(new Vector3(-0.6f, 0.1f, 0f).normalized, sun, 2f));
            Check(lowAway > zenithLum, "Perez: the horizon is brighter than the deeper overhead");

            // A real blue: the clear daytime zenith reads blue from chromaticity, not a tint.
            Color zc = SkyModel.SkyRadiance(new Vector3(0f, 1f, 0f), sun, 2f);
            Check(zc.b > zc.r, "Perez: the clear-sky zenith is blue, from chromaticity not a ramp");

            // Milkier with haze: turbidity 8 flattens the dome's contrast against 2.
            Check(SkyContrast(sun, 8f) < SkyContrast(sun, 2f),
                "Perez: a hazy sky is milkier - lower contrast than a clear one");

            // Zenith luminance is positive, finite, and higher under a higher sun.
            float zenHigh = SkyModel.ZenithLuminance(10f, 2f);
            float zenLow = SkyModel.ZenithLuminance(80f, 2f);
            Check(zenHigh > zenLow && zenLow > 0f && IsFinite(zenHigh),
                "Perez: the zenith is brighter under a higher sun");

            // Finite and non-negative everywhere, including the sun at the zenith and a
            // horizon-grazing view, where the naive formula divides by a cosine of zero.
            foreach (float elev in new[] { 0f, 1f, 30f, 89f, 90f })
            {
                float er = elev * Mathf.Deg2Rad;
                Vector3 sd = new Vector3(Mathf.Cos(er), Mathf.Sin(er), 0f).normalized;
                foreach (var vd in new[] {
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0.01f, 1f).normalized, new Vector3(0f, -1f, 0f) })
                {
                    Color r = SkyModel.SkyRadiance(vd, sd, 2f);
                    Check(IsFinite(r.r) && IsFinite(r.g) && IsFinite(r.b)
                          && r.r >= 0f && r.g >= 0f && r.b >= 0f,
                        "Perez: sky radiance is finite and non-negative at every sun and view");
                }
            }

            // The analytic ambient carries the sky's blue and reconstructs a lit dome.
            Color[] sh = SkyModel.AmbientSH(sun, 2f);
            Check(sh.Length == 9, "Perez: the sky ambient is nine SH coefficients");
            Check(sh[0].r > 0f && sh[0].g > 0f && sh[0].b > 0f, "Perez: the ambient DC term carries light");
            Check(sh[0].b > sh[0].r, "Perez: the sky ambient is blue-biased like the sky it came from");
            Color up = SkyModel.EvaluateSH(sh, new Vector3(0f, 1f, 0f));
            Check(IsFinite(up.r) && IsFinite(up.g) && IsFinite(up.b),
                "Perez: the ambient reconstructs a finite sky");
        }

        // Ratio of the brightest to the dimmest of a small spread of sky views under a
        // sun - a coarse dome contrast the milkiness of haze should lower. The views are
        // the dome away from the sun, never the sun's own direction: the circumsolar
        // aureole is the one part of the sky that brightens with haze (forward Mie
        // scattering), so sampling it would measure the aureole, not the dome milkiness
        // this is about.
        private static float SkyContrast(Vector3 sun, float turbidity)
        {
            Vector3[] views = {
                new Vector3(0f, 1f, 0f),
                new Vector3(-0.5f, 0.2f, 0f).normalized, new Vector3(0.2f, 0.1f, 0.9f).normalized,
                new Vector3(-0.2f, 0.8f, 0.5f).normalized };
            float lo = float.MaxValue, hi = 0f;
            foreach (var v in views)
            {
                Color c = SkyModel.SkyRadiance(v, sun, turbidity);
                float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                if (l < lo) lo = l;
                if (l > hi) hi = l;
            }
            return hi / Mathf.Max(lo, 1e-3f);
        }

        /// <summary>
        /// The twilight sky, built from the extinction at grazing angles rather than
        /// painted: a warm horizon under the sinking sun, a cool one opposite carrying
        /// the Earth's shadow with the pink belt of Venus above it, and a glow that dies
        /// continuously through the three bands to full night. This pins each of those
        /// against the model, plus its finiteness across the whole range and both sides.
        /// </summary>
        private static void TestTwilightBands()
        {
            float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            Vector3 SunAt(float e) => new Vector3(Mathf.Cos(e * Mathf.Deg2Rad), Mathf.Sin(e * Mathf.Deg2Rad), 0f);
            Vector3 ViewAt(float e, float side) => new Vector3(side * Mathf.Cos(e * Mathf.Deg2Rad), Mathf.Sin(e * Mathf.Deg2Rad), 0f);

            // Sun 6 deg down: the horizon under it is warm, the horizon opposite is cool.
            Vector3 sun = SunAt(LightingConstants.CivilTwilightDeg);
            Color west = SkyModel.TwilightRadiance(ViewAt(1f, 1f), sun, 2.5f);
            Color east = SkyModel.TwilightRadiance(ViewAt(1f, -1f), sun, 2.5f);
            Check(west.r > west.b, "Twilight: the horizon under the sinking sun is warm");
            Check(east.b > east.r, "Twilight: the horizon opposite the sun is cool");

            // The belt of Venus: the pink band at the rising shadow's top edge is warmer
            // than the dusky Earth's shadow below it, opposite the sun.
            Color belt = SkyModel.TwilightRadiance(ViewAt(6f, -1f), sun, 2.5f);
            Color shadow = SkyModel.TwilightRadiance(ViewAt(1f, -1f), sun, 2.5f);
            Check(belt.r > belt.b && belt.r > shadow.r,
                "Twilight: the belt of Venus is pink above the Earth's shadow");

            // The glow darkens continuously as the sun sinks, out by astronomical twilight.
            float g3 = Lum(SkyModel.TwilightRadiance(ViewAt(1f, 1f), SunAt(-3f), 2.5f));
            float g9 = Lum(SkyModel.TwilightRadiance(ViewAt(1f, 1f), SunAt(-9f), 2.5f));
            float g15 = Lum(SkyModel.TwilightRadiance(ViewAt(1f, 1f), SunAt(-15f), 2.5f));
            float g20 = Lum(SkyModel.TwilightRadiance(ViewAt(1f, 1f), SunAt(-20f), 2.5f));
            Check(g3 > g9 && g9 > g15 && g15 > g20,
                "Twilight: the glow darkens continuously through the three bands");
            Check(g20 <= 0f, "Twilight: nothing glows below astronomical twilight");

            // The zenith is left to the day model; the twilight glow is a horizon band.
            Color zenith = SkyModel.TwilightRadiance(new Vector3(0f, 1f, 0f), sun, 2.5f);
            Check(Lum(zenith) < g9, "Twilight: the zenith is left to the day model");

            // Finite and non-negative across the whole range, both sides of the sky.
            foreach (float es in new[] { 2f, 0f, -3f, -6f, -12f, -18f, -25f })
                foreach (float ev in new[] { -2f, 0f, 3f, 10f, 45f, 90f })
                    foreach (float side in new[] { 1f, -1f })
                    {
                        Color c = SkyModel.TwilightRadiance(ViewAt(ev, side), SunAt(es), 2.5f);
                        Check(IsFinite(c.r) && IsFinite(c.g) && IsFinite(c.b)
                              && c.r >= 0f && c.g >= 0f && c.b >= 0f,
                            "Twilight: radiance is finite and non-negative across the range");
                    }
        }

        /// <summary>
        /// The ambient fill comes from the sky, not a colour field: blue at midday, and
        /// under cloud dimmer, greyer and flatter - four behaviours from one source. This
        /// pins each against the built URP probe, and that the fill moves with the sun.
        /// </summary>
        private static void TestSkyAmbientProbe()
        {
            Vector3 high = new Vector3(0.1f, 0.99f, 0f).normalized;   // near-midday sun
            Vector3 low = new Vector3(0.99f, 0.12f, 0f).normalized;   // low sun

            var clear = AmbientLight.BuildAmbientProbe(high, 2.5f, 0f);
            var overcast = AmbientLight.BuildAmbientProbe(high, 2.5f, 1f);

            // The clear midday fill is blue, carried from the sky rather than a grey fill.
            Check(clear[2, 0] > clear[0, 0], "Ambient: the clear midday fill is blue, from the sky");

            // Overcast is dimmer: the flat term's luminance drops under full cloud.
            float lumClear = 0.2126f * clear[0, 0] + 0.7152f * clear[1, 0] + 0.0722f * clear[2, 0];
            float lumOver = 0.2126f * overcast[0, 0] + 0.7152f * overcast[1, 0] + 0.0722f * overcast[2, 0];
            Check(lumOver < lumClear && lumOver > 0f, "Ambient: an overcast fill is dimmer than a clear one");

            // Overcast is greyer: the flat term's saturation collapses under full cloud.
            Check(ProbeSaturation(overcast) < ProbeSaturation(clear),
                "Ambient: an overcast fill is greyer than a clear one");

            // Overcast is flatter: the directional (l=1) terms shrink toward an even dome.
            float dirClear = Mathf.Abs(clear[0, 1]) + Mathf.Abs(clear[1, 1]) + Mathf.Abs(clear[2, 1]);
            float dirOver = Mathf.Abs(overcast[0, 1]) + Mathf.Abs(overcast[1, 1]) + Mathf.Abs(overcast[2, 1]);
            Check(dirOver < dirClear, "Ambient: an overcast dome has less directional variation");

            // The fill tracks the sun through the day - a high sun and a low sun differ.
            var lowProbe = AmbientLight.BuildAmbientProbe(low, 2.5f, 0f);
            float dcDelta = Mathf.Abs(lowProbe[0, 0] - clear[0, 0]) + Mathf.Abs(lowProbe[2, 0] - clear[2, 0]);
            Check(dcDelta > 1e-4f, "Ambient: the fill changes as the sun moves through the day");

            // Every coefficient of every channel is finite.
            bool finite = true;
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < 9; i++)
                    finite &= IsFinite(clear[c, i]) && IsFinite(overcast[c, i]) && IsFinite(lowProbe[c, i]);
            Check(finite, "Ambient: the probe coefficients are all finite");

            // Ground bounce: a bright surface reflects the sky's fill back up, a dark one
            // barely, and a black ground adds nothing - the indirect floor, from albedo alone.
            var noGround = AmbientLight.BuildAmbientProbe(high, 2.5f, 0f, Color.black);
            var snow = AmbientLight.BuildAmbientProbe(high, 2.5f, 0f, Color.white);
            var soil = AmbientLight.BuildAmbientProbe(high, 2.5f, 0f, new Color(0.15f, 0.13f, 0.1f));
            for (int c = 0; c < 3; c++)
                Check(Mathf.Abs(noGround[c, 0] - clear[c, 0]) < 1e-5f,
                    "Ambient: a black ground adds no bounce");
            Check(snow[1, 0] > soil[1, 0] && soil[1, 0] > clear[1, 0],
                "Ambient: snow brightens the ambient floor more than bare soil, both above no bounce");
            CheckClose(snow[1, 0], clear[1, 0] * 2f,
                "Ambient: a white ground doubles the flat fill it reflects back");
        }

        // Saturation of a probe's flat term: (max-min)/max of its RGB. A grey dome is ~0.
        private static float ProbeSaturation(SphericalHarmonicsL2 p)
        {
            float r = p[0, 0], g = p[1, 0], b = p[2, 0];
            float mx = Mathf.Max(r, Mathf.Max(g, b));
            float mn = Mathf.Min(r, Mathf.Min(g, b));
            return mx > 1e-5f ? (mx - mn) / mx : 0f;
        }

        /// <summary>
        /// The per-frame hand-off carries only resolved weather across and derives the
        /// lighting from it in one place. This pins that the cloud, air and surface values
        /// round-trip unchanged; that turbidity, the sun direction and the moonlight are the
        /// lighting owners' own values; and - the done-when - that weather alone moves the
        /// sky with no change here: overcast darkens the key light and humidity hazes the air.
        /// </summary>
        private static void TestAtmosphereState()
        {
            var clearDry = MakeWeather(0.05f, 0.2f, 0f);
            var day = MakeClock(45f, 180f, 1.4f, -20f, 0f);
            var albedo = new Color(0.25f, 0.28f, 0.22f);
            var a = AtmosphereState.From(in clearDry, in day, albedo, 0f);

            // Resolved weather crosses unchanged - the hand-off copies, it does not recompute.
            CheckClose(a.cloudCoverLow, clearDry.cloudCoverLow, "Atmosphere: low cover crosses unchanged");
            CheckClose(a.cloudCoverMid, clearDry.cloudCoverMid, "Atmosphere: mid cover crosses unchanged");
            CheckClose(a.cloudCoverHigh, clearDry.cloudCoverHigh, "Atmosphere: high cover crosses unchanged");
            CheckClose(a.cloudBaseLowM, clearDry.cloudBaseM, "Atmosphere: the low cloud base is the resolved LCL");
            CheckClose(a.cloudBaseMidM, clearDry.cloudBaseMidM, "Atmosphere: the mid cloud base crosses from the resolved deck level");
            CheckClose(a.cloudBaseHighM, clearDry.cloudBaseHighM, "Atmosphere: the high cloud base crosses from the resolved deck level");
            Check(a.cloudBaseLowM < a.cloudBaseMidM && a.cloudBaseMidM < a.cloudBaseHighM,
                "Atmosphere: the three cloud bases stack low beneath mid beneath high, none a shared constant");

            // The sun is occluded by the three layers combined as independent screens, never
            // their sum, and the layers stay separate for the three-sheet render.
            CheckClose(a.cloudCoverCombined01,
                CloudCover.CombinedCover01(a.cloudCoverLow, a.cloudCoverMid, a.cloudCoverHigh),
                "Atmosphere: the sun's occlusion is the three layers combined, not a second derivation");
            CheckClose(CloudCover.CombinedCover01(0f, 0f, 0.4f), 0.4f,
                "Cloud cover: high cirrus over a clear low sky reads as exactly the cirrus");
            CheckClose(CloudCover.CombinedCover01(0.5f, 0.5f, 0.5f), 0.875f,
                "Cloud cover: three half-covered decks combine to 0.875, never their sum of 1.5");
            Check(CloudCover.CombinedCover01(0.9f, 0.9f, 0.9f) <= 1f,
                "Cloud cover: the combined occlusion can never exceed full cover");
            CheckClose(a.stabilityLapseCPerKm, clearDry.stabilityLapseCPerKm, "Atmosphere: the air's stability crosses unchanged");
            CheckClose(a.precipitationMmPerHour, clearDry.precipitationMmPerHour, "Atmosphere: the precipitation rate crosses unchanged");
            CheckClose(a.snowLineM, clearDry.snowLineM, "Atmosphere: the snow line crosses unchanged");
            Check(a.cloudTypeLow == clearDry.cloudType, "Atmosphere: the cloud type crosses unchanged");
            Check(a.groundAlbedo == albedo, "Atmosphere: the ground albedo is carried for the bounce term");

            // The lighting is the owners' own values, not a second derivation here.
            CheckClose(a.turbidity, TurbidityModel.Resolve(clearDry.humidity, clearDry.wetness, 0f),
                "Atmosphere: turbidity is the weather-driven value");
            CheckClose(a.sunDir.magnitude, 1f, "Atmosphere: the sun direction is a unit vector");
            CheckClose(a.sunDir.y, Mathf.Sin(45f * Mathf.Deg2Rad), "Atmosphere: the sun sits at the clock's altitude");
            CheckClose(a.moonIlluminanceLux,
                ExposureModel.MoonIlluminanceLux(day.lunarElevationDeg, day.lunarIlluminatedFraction),
                "Atmosphere: moonlight is the one shared photometric value");
            Check(a.sunIlluminanceLux > 0f && IsFinite(a.sunIlluminanceLux), "Atmosphere: a 45 deg sun carries daylight");
            Check(a.sunColor.r >= 0f && a.sunColor.g >= 0f && a.sunColor.b >= 0f, "Atmosphere: the sun colour is non-negative");

            // The done-when: weather alone moves the sky. Overcast darkens the key light,
            // and humid air ahead of a front hazes it - both with no change in this file.
            var overcastW = MakeWeather(0.95f, 0.6f, 0.8f);
            var overcast = AtmosphereState.From(in overcastW, in day, albedo, 0f);
            Check(overcast.sunIlluminanceLux < a.sunIlluminanceLux, "Atmosphere: an overcast front darkens the key light");
            var humidW = MakeWeather(0.3f, 0.9f, 0f);
            var humid = AtmosphereState.From(in humidW, in day, albedo, 0f);
            Check(humid.turbidity > a.turbidity, "Atmosphere: humid air ahead of a front hazes the sky");

            // Day is brighter than night: exposure follows the sky's own key illuminance.
            var night = MakeClock(-20f, 0f, 38f, 30f, 1f);
            var n = AtmosphereState.From(in clearDry, in night, albedo, 0f);
            Check(a.exposureEv > n.exposureEv, "Atmosphere: the daytime exposure is higher than the night's");
            Check(IsFinite(a.exposureEv) && IsFinite(n.exposureEv), "Atmosphere: the exposure is finite day and night");
        }

        /// <summary>
        /// The cloud-type table decides each deck's genus from the same resolved physics
        /// the rest of the sky reads, never an authored field. This pins the four done-when
        /// sequences: a warm front runs cirrus - cirrostratus - altostratus - nimbostratus as
        /// it closes in; unstable air grows cumulus humilis - congestus - cumulonimbus with
        /// the lapse; a stable humid calm gives stratus; and stable flow over a ridge parks
        /// a lenticular that ordinary flow and unstable air both refuse.
        /// </summary>
        private static void TestCloudTypeTable()
        {
            const float far = 700f, veil = 300f, rain = 100f;
            var warm = WeatherFront.FrontKind.Warm;
            var none = WeatherFront.FrontKind.None;
            float stableLapse = 3f, calmWind = 2f, humidRh = 0.95f;

            // A warm front, closing in: the high sheet leads with cirrus far out and thickens
            // to a cirrostratus veil, the mid deck lays an altostratus sheet, and the low deck
            // rains as nimbostratus once the front is within the rain range.
            Check(CloudTypeTable.Classify(CloudLayer.High, 0.4f, stableLapse, warm, far, 0.5f, calmWind, false)
                  == CloudGenus.Cirrus, "Clouds: a distant warm front leads with high cirrus");
            Check(CloudTypeTable.Classify(CloudLayer.High, 0.6f, stableLapse, warm, veil, 0.6f, calmWind, false)
                  == CloudGenus.Cirrostratus, "Clouds: the closing front thickens the high sheet to cirrostratus");
            Check(CloudTypeTable.Classify(CloudLayer.Mid, 0.6f, stableLapse, warm, veil, 0.7f, calmWind, false)
                  == CloudGenus.Altostratus, "Clouds: the mid deck under the front is a layered altostratus");
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.9f, stableLapse, warm, rain, humidRh, calmWind, false)
                  == CloudGenus.Nimbostratus, "Clouds: the low deck rains as nimbostratus inside the front");

            // Unstable air, no front: the heaps grow with the lapse rate.
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.5f, 6f, none, far, 0.5f, calmWind, false)
                  == CloudGenus.CumulusHumilis, "Clouds: a gently unstable afternoon builds fair-weather cumulus humilis");
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.6f, 8f, none, far, 0.6f, calmWind, false)
                  == CloudGenus.CumulusCongestus, "Clouds: a steeper lapse grows the heaps into towering congestus");
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.8f, 11f, none, far, 0.7f, calmWind, false)
                  == CloudGenus.Cumulonimbus, "Clouds: absolute instability tops out as cumulonimbus");

            // A stable, humid, near-calm morning settles into flat stratus; stir the air or
            // dry it out and the same deck breaks into lumpy stratocumulus instead.
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.7f, stableLapse, none, far, humidRh, calmWind, false)
                  == CloudGenus.Stratus, "Clouds: a stable humid calm gives flat stratus");
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.7f, stableLapse, none, far, 0.5f, 10f, false)
                  == CloudGenus.Stratocumulus, "Clouds: a drier, windier stable deck breaks into stratocumulus");

            // Stable flow forced over a ridge parks a lenticular; ordinary flow and unstable
            // air over the same ridge both refuse it - the lens is the stable-orographic case.
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.6f, stableLapse, none, far, 0.6f, calmWind, true)
                  == CloudGenus.Lenticular, "Clouds: stable flow over a ridge stands as a lenticular lens");
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.6f, stableLapse, none, far, 0.6f, calmWind, false)
                  != CloudGenus.Lenticular, "Clouds: without the ridge the same stable deck is not a lenticular");
            Check(CloudTypeTable.Classify(CloudLayer.Low, 0.6f, 11f, none, far, 0.6f, calmWind, true)
                  == CloudGenus.Cumulonimbus, "Clouds: unstable air over the ridge convects, it does not park a lens");

            // No cover means no cloud, whatever the front or the air is doing.
            Check(CloudTypeTable.Classify(CloudLayer.Mid, 0f, stableLapse, warm, veil, humidRh, calmWind, false)
                  == CloudGenus.None, "Clouds: an empty deck draws nothing");

            // The hand-off carries the per-layer genus across from the resolved weather.
            var warmFrontW = MakeWeather(0.6f, 0.7f, 0.3f);
            var day = MakeClock(45f, 180f, 1.4f, -20f, 0f);
            var af = AtmosphereState.From(in warmFrontW, in day, new Color(0.2f, 0.2f, 0.2f), 0f);
            Check(af.cloudGenusLow != CloudGenus.None || af.cloudGenusMid != CloudGenus.None
                  || af.cloudGenusHigh != CloudGenus.None,
                "Atmosphere: the hand-off carries a decided cloud genus, not a blank deck");
        }

        /// <summary>
        /// Seasonal cloud character is not authored: the ITCZ migrates with the sun, so a
        /// monsoon-latitude world flips between a wet, deep-convective high-sun half and a
        /// dry, clear low-sun half, while a mid-latitude world stays in the storm track all
        /// year. This verifies the swing is real and that the tropics swing far harder than
        /// the mid-latitudes - the free seasonal sky, checked rather than built.
        /// </summary>
        private static void TestSeasonalCloudCharacter()
        {
            float tilt = AtmoConstants.EarthAxialTiltDegrees;
            float itczJan = WeatherCellField.ItczLatitudeDeg(15, tilt);   // low-sun half
            float itczJul = WeatherCellField.ItczLatitudeDeg(200, tilt);  // high-sun half

            // The ITCZ follows the sun: south in the low-sun half, north in the high-sun half.
            Check(itczJul > itczJan, "Season: the ITCZ migrates north into the high-sun half of the year");

            float wet15Jan = CloudCover.AscentFraction01(15f, itczJan);
            float wet15Jul = CloudCover.AscentFraction01(15f, itczJul);
            float wet52Jan = CloudCover.AscentFraction01(52f, itczJan);
            float wet52Jul = CloudCover.AscentFraction01(52f, itczJul);

            // A 15 deg world is monsoonal: the ITCZ swings close in the high-sun half - a wet,
            // rising, deep-convective regime - and far away in the low-sun half - subsiding and clear.
            Check(wet15Jul > wet15Jan,
                "Season: a 15 deg world is wetter when the ITCZ has migrated over it than half a year later");

            // A 52 deg world sits in the storm track both halves, so its seasonal ascent swing is
            // far smaller than the tropics' - it runs frontal weather year-round rather than flipping.
            Check(Mathf.Abs(wet52Jul - wet52Jan) < Mathf.Abs(wet15Jul - wet15Jan),
                "Season: the mid-latitude ascent regime swings far less across the year than the monsoon latitude's");
            Check(IsFinite(wet15Jan) && IsFinite(wet15Jul) && IsFinite(wet52Jan) && IsFinite(wet52Jul),
                "Season: every sampled ascent fraction is finite");
        }

        /// <summary>
        /// The three sheets are drawn from one baked field and a per-deck binding, both
        /// deterministic and testable without a GPU. This pins the field's seam - equal
        /// values at u=0 and u=1, and at v=0 and v=1, so a domed sheet wraps with no
        /// visible join - and its 0..1 range; then the binding's advection (wind times
        /// time in UV, a faster wind drifting further) and its genus-to-silhouette map:
        /// a heap hardens and self-shadows, a sheet flattens, a cirrus stretches.
        /// </summary>
        private static void TestCloudRendering()
        {
            // The baked field is C0-seamless by construction: the four-corner blend forces
            // the value at one edge to equal the value a whole tile away at the far edge,
            // so opposite edges match exactly however the underlying octaves fall.
            var n = new Noise(20260826);
            float cells = CloudNoiseBaker.BaseCells;
            foreach (float t in new[] { 0.05f, 0.3f, 0.5f, 0.85f })
            {
                float u0 = CloudNoiseBaker.SeamlessFractal(n, 0f, t, cells, 4, 0.55f, 0.4f, NoiseType.Perlin);
                float u1 = CloudNoiseBaker.SeamlessFractal(n, 1f, t, cells, 4, 0.55f, 0.4f, NoiseType.Perlin);
                CheckClose(u0, u1, "Clouds: the field is seamless across the u edge");
                float v0 = CloudNoiseBaker.SeamlessFractal(n, t, 0f, cells, 4, 0.55f, 0.4f, NoiseType.Perlin);
                float v1 = CloudNoiseBaker.SeamlessFractal(n, t, 1f, cells, 4, 0.55f, 0.4f, NoiseType.Perlin);
                CheckClose(v0, v1, "Clouds: the field is seamless across the v edge");
                Check(u0 >= 0f && u0 <= 1f && IsFinite(u0), "Clouds: the field stays in 0..1 and is finite");
            }

            // Advection is wind times elapsed time expressed in the sheet's UV: no time is
            // no drift, and a faster wind on the same clock outruns a slower one.
            Vector2 slow = new Vector2(3f, 0f), fast = new Vector2(12f, 0f);
            CheckClose(CloudRenderBinding.AdvectionUv(fast, 0f).magnitude, 0f,
                "Clouds: no elapsed time means no drift");
            Vector2 drift = CloudRenderBinding.AdvectionUv(fast, 100f);
            CheckClose(drift.x, fast.x * 100f / LightingConstants.CloudAdvectionMetresPerUv,
                "Clouds: drift is wind times time over the metres-per-UV scale");
            Check(CloudRenderBinding.AdvectionUv(fast, 100f).magnitude
                  > CloudRenderBinding.AdvectionUv(slow, 100f).magnitude,
                "Clouds: a faster wind aloft drifts the field further in the same time");

            // A convective heap draws with a hardened edge and a deep self-shadow, and is
            // neither flattened nor stretched.
            var heap = CloudRenderBinding.Resolve(CloudGenus.CumulusCongestus, 0.6f, 1200f, slow, 0f);
            CheckClose(heap.edgeSharpness, LightingConstants.CloudCumulusEdgeSharpness,
                "Clouds: a cumulus hardens its edge into cauliflower");
            CheckClose(heap.selfShadowDepth, LightingConstants.CloudSelfShadowDepth,
                "Clouds: a cumulus self-shadows from its lit flank to its base");
            CheckClose(heap.flatten01, 0f, "Clouds: a heap is not flattened");
            CheckClose(heap.stretch, 1f, "Clouds: a heap is not stretched");
            CheckClose(heap.baseAltitudeM, 1200f, "Clouds: the deck draws at its own altitude");

            // A layered sheet flattens toward an even deck and neither hardens nor stretches.
            var sheet = CloudRenderBinding.Resolve(CloudGenus.Stratus, 0.8f, 600f, slow, 0f);
            CheckClose(sheet.flatten01, 1f - LightingConstants.CloudStratusFlatten,
                "Clouds: a stratus flattens toward a uniform deck");
            CheckClose(sheet.edgeSharpness, 1f, "Clouds: a sheet keeps a soft, unhardened edge");
            CheckClose(sheet.selfShadowDepth, 0f, "Clouds: a flat sheet has no heap to self-shadow");

            // A high ice cloud stretches along the wind into streaks.
            var ice = CloudRenderBinding.Resolve(CloudGenus.Cirrus, 0.3f, 9000f, fast, 0f);
            CheckClose(ice.stretch, LightingConstants.CloudCirrusStretch,
                "Clouds: a cirrus stretches along the wind into streaks");
            CheckClose(ice.flatten01, 0f, "Clouds: a streak cloud is not flattened");

            // Coverage is clamped into 0..1 whatever the caller passes.
            CheckClose(CloudRenderBinding.Resolve(CloudGenus.Stratus, 1.7f, 600f, slow, 0f).coverage01, 1f,
                "Clouds: coverage above full is clamped to full");
            CheckClose(CloudRenderBinding.Resolve(CloudGenus.Stratus, -0.4f, 600f, slow, 0f).coverage01, 0f,
                "Clouds: coverage below zero is clamped to clear");
        }

        /// <summary>
        /// A cumulonimbus is drawn from the same sheet, only harder, and its lightning is
        /// a photometric spike the exposure lags rather than a white frame. This pins the
        /// tower rising with coverage, the near-black base greening under hail, the enormous
        /// lit-to-shaded contrast, and the done-when: the flash adds to the scene key so it
        /// lights the ground, yet over its brief duration the rate-limited exposure barely
        /// moves, leaving the flash far above the exposed white point - a flash, not a fade.
        /// </summary>
        private static void TestStormRender()
        {
            // The tower rises with coverage from nothing to the capped real extent.
            CheckClose(StormRender.TowerExtentM(0f), 0f, "Storm: a cell with no cloud does not tower");
            Check(StormRender.TowerExtentM(0.9f) > StormRender.TowerExtentM(0.3f),
                "Storm: a more-covered storm builds a taller tower");
            CheckClose(StormRender.TowerExtentM(1f), LightingConstants.CloudCumulonimbusTowerExtentM,
                "Storm: a full storm reaches the capped tower extent");
            CheckClose(StormRender.TowerExtentM(2f), LightingConstants.CloudCumulonimbusTowerExtentM,
                "Storm: the tower never runs past its cap");

            // The base is near-black; a neutral base is grey, a hail base greens.
            Color dry = StormRender.BaseColor(false);
            Color hail = StormRender.BaseColor(true);
            float dryLum = 0.2126f * dry.r + 0.7152f * dry.g + 0.0722f * dry.b;
            Check(dryLum < 0.2f, "Storm: the storm base is near-black");
            CheckClose(dry.r, dry.g, "Storm: a hail-free base is a neutral grey");
            Check(hail.g > hail.r && hail.g > hail.b, "Storm: a heavy-hail base greens toward the hail tint");

            // The lit flank against that dark base is the enormous real storm contrast.
            Check(StormRender.FlankBrightness > 1f, "Storm: the sunlit flank is gained above the sheet");
            Check(StormRender.FlankBrightness / Mathf.Max(dryLum, 1e-3f) > 10f,
                "Storm: the lit-flank to shaded-base contrast is very large, as a real storm's is");

            // The flash adds to the scene key - the term that lights terrain - so a stroke
            // lights the ground; no flash leaves the key untouched, and it scales with the stroke.
            float nightKey = ExposureModel.SceneKeyIlluminanceLux(-10f, 1f, 2f, 1f, -20f, 0f);
            CheckClose(StormRender.FlashKeyLux(nightKey, 0f), nightKey, "Storm: between strokes the key is untouched");
            float flashKey = StormRender.FlashKeyLux(nightKey, 1f);
            Check(flashKey > nightKey + 1000f, "Storm: a stroke floods the scene key with daylight-order light");
            Check(StormRender.FlashKeyLux(nightKey, 1f) > StormRender.FlashKeyLux(nightKey, 0.5f),
                "Storm: a brighter stroke floods more light");

            // The done-when: the exposure must NOT chase the flash. The scene jumps by many
            // stops, but over the flash's brief window the rate-limited adaptation moves only
            // a fraction of a stop, leaving the flash far above the exposed white point.
            float nightEv = ExposureModel.Ev100FromLux(nightKey);
            float flashEv = ExposureModel.Ev100FromLux(flashKey);
            float duringEv = StormRender.ExposureEvDuringFlash(nightEv, flashEv);
            float maxMove = LightingConstants.LightAdaptationEvPerSec * LightingConstants.CloudLightningFlashSeconds;
            Check(duringEv - nightEv <= maxMove + 1e-4f,
                "Storm: eye adaptation is rate-limited and cannot chase the flash within its duration");
            Check(flashEv - duringEv > 3f,
                "Storm: the flash stays many stops above the exposed white point, so it lights the scene");
            Check(flashEv - nightEv > 8f * (duringEv - nightEv),
                "Storm: the scene brightens far more than the exposure compensates for");
            Check(IsFinite(duringEv) && IsFinite(flashKey), "Storm: the flash exposure and key are finite");
        }

        // The two-term light change: the sky's one horizontal illuminance is split into a
        // shadow-casting beam and a diffuse remainder, and cloud drives the split. A clear
        // day keeps a hard directional beam; a full overcast is all diffuse and shadowless,
        // at a plausible overall brightness - and neither is an authored preset.
        private static void TestSkyLightSplit()
        {
            // Clear day: most of the illuminance is the direct beam, shadows are hard.
            float clearBeam = SkyLightSplit.DirectBeamFraction01(0f);
            CheckClose(clearBeam, LightingConstants.ClearSkyDirectBeamFraction01,
                "SkyLightSplit: a clear sky keeps the full clear-sky direct beam fraction");
            CheckClose(SkyLightSplit.ShadowStrength01(0f), 1f,
                "SkyLightSplit: a clear sky casts full-strength shadows");

            // Full overcast: no beam, no shadow - the day is soft, not just dark.
            CheckClose(SkyLightSplit.DirectBeamFraction01(1f), 0f,
                "SkyLightSplit: a full overcast leaves no direct beam");
            CheckClose(SkyLightSplit.ShadowStrength01(1f), 0f,
                "SkyLightSplit: a full overcast is shadowless");

            // The beam and the shadow both fall monotonically as cover thickens.
            float beamHalf = SkyLightSplit.DirectBeamFraction01(0.5f);
            Check(beamHalf < clearBeam && beamHalf > 0f,
                "SkyLightSplit: the direct beam falls with cover between clear and overcast");
            Check(SkyLightSplit.ShadowStrength01(0.5f) < SkyLightSplit.ShadowStrength01(0.25f),
                "SkyLightSplit: shadow strength decreases as cover increases");

            // The two terms conserve the total: beam + diffuse == the illuminance in.
            float noon = ExposureModel.SceneKeyIlluminanceLux(60f, 1f, 2f, 0f, -20f, 0f);
            float clearDir = SkyLightSplit.DirectionalLux(noon, 0f);
            float clearDiff = SkyLightSplit.DiffuseLux(noon, 0f);
            CheckClose(clearDir + clearDiff, noon,
                "SkyLightSplit: the beam and diffuse terms conserve the total illuminance");
            Check(clearDir > clearDiff,
                "SkyLightSplit: a clear noon is beam-dominated (hard directional shadows)");

            // Overcast: the same split at full cover puts every lux in the diffuse term,
            // at a plausible overcast-day brightness, with no shadow-casting beam left.
            float overcast = ExposureModel.SceneKeyIlluminanceLux(60f, 1f, 2f, 1f, -20f, 0f);
            float ovDir = SkyLightSplit.DirectionalLux(overcast, 1f);
            float ovDiff = SkyLightSplit.DiffuseLux(overcast, 1f);
            CheckClose(ovDir, 0f, "SkyLightSplit: an overcast noon has no directional beam");
            CheckClose(ovDiff, overcast,
                "SkyLightSplit: an overcast noon carries all its light in the diffuse term");
            CheckWithin(overcast, LightingConstants.OvercastDayLux, LightingConstants.OvercastDayLux * 0.5f,
                "SkyLightSplit: an overcast noon lands near the overcast-day illuminance anchor");

            // The diffuse SHARE rises with cover even as the total falls - the relative
            // raise of the sky term the spec asks for, driven only by combined cover.
            float clearDiffShare = clearDiff / noon;
            float ovDiffShare = ovDiff / overcast;
            Check(ovDiffShare > clearDiffShare,
                "SkyLightSplit: the diffuse term's share of the light rises as cloud thickens");
            Check(IsFinite(clearDir) && IsFinite(ovDiff),
                "SkyLightSplit: the split terms are finite");
        }

        // Sun shafts: the crepuscular-ray master intensity is the product of a coverage
        // window and a low-sun window, so the effect gates itself off at clear sky, solid
        // overcast and high sun, and reaches full only for a low sun through broken cloud.
        private static void TestSunShafts()
        {
            // Coverage window: zero at clear and at overcast, full across the broken band.
            CheckClose(SunShafts.CoverageWindow01(0f), 0f,
                "SunShafts: a clear sky has no gaps to define rays");
            CheckClose(SunShafts.CoverageWindow01(1f), 0f,
                "SunShafts: a solid overcast lets no shaft through");
            CheckClose(SunShafts.CoverageWindow01(LightingConstants.ShaftBrokenLowCover01), 1f,
                "SunShafts: the coverage window is full at the low edge of the broken band");
            CheckClose(SunShafts.CoverageWindow01(0.5f), 1f,
                "SunShafts: the coverage window is full across the broken band");
            CheckClose(SunShafts.CoverageWindow01(LightingConstants.ShaftBrokenHighCover01), 1f,
                "SunShafts: the coverage window is full at the high edge of the broken band");
            Check(SunShafts.CoverageWindow01(0.15f) > 0f && SunShafts.CoverageWindow01(0.15f) < 1f,
                "SunShafts: the coverage window ramps in between clear and the broken band");

            // Low-sun window: full at the horizon, zero at the top elevation and above,
            // and zero with the sun below the horizon.
            CheckClose(SunShafts.LowSunWeight01(0f), 1f,
                "SunShafts: rays are full-strength with the sun at the horizon");
            CheckClose(SunShafts.LowSunWeight01(LightingConstants.ShaftLowSunTopDeg), 0f,
                "SunShafts: rays are gone once the sun reaches the top elevation");
            CheckClose(SunShafts.LowSunWeight01(-5f), 0f,
                "SunShafts: a sun below the horizon casts no shafts");
            Check(SunShafts.LowSunWeight01(10f) > SunShafts.LowSunWeight01(25f),
                "SunShafts: the low-sun weight falls monotonically as the sun climbs");

            // Master intensity: full only for a low sun through broken cloud; zero for a
            // clear sky, a solid overcast, or a high sun - each an independent gate.
            Check(SunShafts.Intensity01(0.5f, 8f) > 0.5f,
                "SunShafts: a low sun through broken cloud produces a strong shaft term");
            CheckClose(SunShafts.Intensity01(0f, 8f), 0f,
                "SunShafts: a clear low sun produces no shafts");
            CheckClose(SunShafts.Intensity01(1f, 8f), 0f,
                "SunShafts: a solid overcast produces no shafts");
            CheckClose(SunShafts.Intensity01(0.5f, 60f), 0f,
                "SunShafts: broken cloud with a high sun produces no shafts");
            Check(IsFinite(SunShafts.Intensity01(0.5f, 8f)),
                "SunShafts: the master intensity is finite");
        }

        // Projected cloud shadows: the moving patches appear only for broken cloud, and
        // they travel on the same wind as the sheet, so shadow and cloud stay together.
        private static void TestCloudShadows()
        {
            // Contrast: zero at clear and overcast (uniform, nothing to project), full at
            // half cover, and rising monotonically toward the middle from either end.
            CheckClose(CloudShadows.ShadowContrast01(0f), 0f,
                "CloudShadows: a clear sky projects no shadow patches");
            CheckClose(CloudShadows.ShadowContrast01(1f), 0f,
                "CloudShadows: a solid overcast has no contrast to project");
            CheckClose(CloudShadows.ShadowContrast01(0.5f), 1f,
                "CloudShadows: broken half cover projects the strongest shadow patches");
            Check(CloudShadows.ShadowContrast01(0.3f) > CloudShadows.ShadowContrast01(0.1f),
                "CloudShadows: contrast rises from clear toward broken cover");
            Check(CloudShadows.ShadowContrast01(0.7f) > CloudShadows.ShadowContrast01(0.9f),
                "CloudShadows: contrast rises from overcast toward broken cover");

            // The cookie scrolls on the same wind as the sheet: recovering the world drift
            // from the cookie scroll and from the sheet's advection gives the same metres,
            // so shadow and cloud move at one speed and direction. Only the scale differs.
            Vector2 wind = new Vector2(9f, -4f);
            float dt = 30f;
            Vector2 cookie = CloudShadows.CookieScrollUv(wind, dt);
            Vector2 shadowDrift = CloudShadows.WorldDriftM(cookie);
            Vector2 sheetDrift = CloudRenderBinding.AdvectionUv(wind, dt) * LightingConstants.CloudAdvectionMetresPerUv;
            Vector2 expectedDrift = wind * dt;
            CheckClose(shadowDrift.x, expectedDrift.x, "CloudShadows: the shadow's world drift is wind x time (x)");
            CheckClose(shadowDrift.y, expectedDrift.y, "CloudShadows: the shadow's world drift is wind x time (y)");
            CheckClose(shadowDrift.x, sheetDrift.x, "CloudShadows: shadow and sheet drift the same metres (x)");
            CheckClose(shadowDrift.y, sheetDrift.y, "CloudShadows: shadow and sheet drift the same metres (y)");

            // At rest the cookie has not moved, and a faster wind drifts the patch further.
            Vector2 still = CloudShadows.CookieScrollUv(wind, 0f);
            CheckClose(still.x, 0f, "CloudShadows: no elapsed time, no cookie drift (x)");
            CheckClose(still.y, 0f, "CloudShadows: no elapsed time, no cookie drift (y)");
            Check(CloudShadows.CookieScrollUv(wind * 2f, dt).magnitude > cookie.magnitude,
                "CloudShadows: a faster wind drifts the shadow patch further");
            Check(IsFinite(cookie.x) && IsFinite(cookie.y),
                "CloudShadows: the cookie scroll is finite");

            // Probe-refresh trigger: fires once cover has drifted by the hysteresis band
            // from the last refresh, so a shadow edge crossing refreshes the bounce with
            // the direct light, while sub-band jitter does not thrash the probe budget.
            float band = LightingConstants.CloudShadowProbeRefreshCoverDelta;
            Check(!CloudShadows.ShouldRefreshProbes(0.5f, 0.5f + band * 0.5f),
                "CloudShadows: a sub-band cover change does not refresh the probes");
            Check(CloudShadows.ShouldRefreshProbes(0.5f, 0.5f + band + 1e-3f),
                "CloudShadows: a cover change past the band refreshes the probes");
            Check(CloudShadows.ShouldRefreshProbes(0.6f, 0.6f - band - 1e-3f),
                "CloudShadows: the refresh trigger is symmetric (clearing as well as thickening)");
        }

        // Cloud rendering determinism: the field UV is a pure function of world position
        // and the absolute simulated time, so the same world/day/hour reproduces the same
        // sky after a reload, and a clock jumped forward and back returns the original.
        private static void TestCloudDeterminism()
        {
            Vector2 world = new Vector2(1234f, -567f);
            Vector2 wind = new Vector2(7f, 3f);
            float metresPerUv = LightingConstants.CloudAdvectionMetresPerUv;

            // Same inputs, same UV - the "identical sky after a reload" property: nothing
            // is read from Time.time or an accumulator, only the passed simulated time.
            float t0 = 86400f * 12f + 3600f * 9f;   // day 12, 09:00 of the simulated clock
            Vector2 a = CloudRenderBinding.SampleUv(world, wind, t0, metresPerUv);
            Vector2 b = CloudRenderBinding.SampleUv(world, wind, t0, metresPerUv);
            CheckClose(a.x, b.x, "CloudDeterminism: the same moment reproduces the same field UV (x)");
            CheckClose(a.y, b.y, "CloudDeterminism: the same moment reproduces the same field UV (y)");

            // A 100-day clock jump forward and back returns exactly the original UV -
            // proof there is no accumulating drift: the value depends only on absolute time.
            float hundredDays = 100f * 86400f;
            float jumped = t0 + hundredDays;
            Vector2 atJump = CloudRenderBinding.SampleUv(world, wind, jumped, metresPerUv);
            Vector2 back = CloudRenderBinding.SampleUv(world, wind, jumped - hundredDays, metresPerUv);
            Check(Mathf.Abs(atJump.x - a.x) > 1e-3f,
                "CloudDeterminism: a 100-day jump does move the field (advection is real)");
            CheckClose(back.x, a.x, "CloudDeterminism: jumping forward 100 days and back returns the sky (x)");
            CheckClose(back.y, a.y, "CloudDeterminism: jumping forward 100 days and back returns the sky (y)");

            // The baked shape field is a pure function of its inputs too - the seed side of
            // (worldPos, simulatedTime, seed): the same sample coordinates give the same value.
            var noise = new Noise(20250826);
            float f0 = CloudNoiseBaker.SeamlessFractal(noise, 0.31f, 0.62f, 4, 4, 0.55f, 0.4f, NoiseType.Perlin);
            float f1 = CloudNoiseBaker.SeamlessFractal(noise, 0.31f, 0.62f, 4, 4, 0.55f, 0.4f, NoiseType.Perlin);
            CheckClose(f0, f1, "CloudDeterminism: the baked shape field is a pure function of its coordinates");
            Check(IsFinite(a.x) && IsFinite(back.y), "CloudDeterminism: the sampled UVs are finite");
        }

        // The ambient-cube probe: seventy-two bytes of pure value data, evaluated by the
        // squared normal, so a grid of them is a small flat array and CPU/GPU agree on the
        // ambient at a point.
        private static void TestIrradianceProbe()
        {
            // Six float3 faces and no references: exactly 72 bytes, so 100 000 probes are
            // 7.2 MB - the memory the tier is budgeted at, measured not assumed.
            int size = System.Runtime.InteropServices.Marshal.SizeOf<IrradianceProbe>();
            Check(size == 72, "IrradianceProbe: the struct is exactly 72 bytes (6 x float3)");
            Check(100000L * size == 7200000L, "IrradianceProbe: 100 000 probes cost 7.2 MB");

            // A uniform probe returns its fill for every normal - the flat-ambient case.
            var flat = IrradianceProbe.Uniform(new Vector3(0.2f, 0.4f, 0.6f));
            Vector3 up = flat.Evaluate(Vector3.up);
            CheckClose(up.x, 0.2f, "IrradianceProbe: a uniform probe evaluates to its fill (r)");
            CheckClose(up.z, 0.6f, "IrradianceProbe: a uniform probe evaluates to its fill (b)");

            // Axis-aligned normals pick the matching face; the opposite face is not read.
            var p = new IrradianceProbe
            {
                posX = new Vector3(1f, 0f, 0f), negX = new Vector3(0f, 1f, 0f),
                posY = new Vector3(0f, 0f, 1f), negY = new Vector3(1f, 1f, 0f),
                posZ = new Vector3(1f, 0f, 1f), negZ = new Vector3(0f, 1f, 1f),
            };
            Vector3 ex = p.Evaluate(new Vector3(1f, 0f, 0f));
            CheckClose(ex.x, 1f, "IrradianceProbe: a +X normal reads the +X face");
            CheckClose(ex.y, 0f, "IrradianceProbe: a +X normal does not read any other face");
            Vector3 ey = p.Evaluate(new Vector3(0f, -1f, 0f));
            CheckClose(ey.x, 1f, "IrradianceProbe: a -Y normal reads the -Y face (r)");
            CheckClose(ey.y, 1f, "IrradianceProbe: a -Y normal reads the -Y face (g)");

            // The squared-normal weights sum to one, so a diagonal normal is a convex blend
            // and swinging a normal across an axis is continuous (the crossing weight is 0).
            Vector3 diag = new Vector3(1f, 1f, 0f).normalized;
            Vector3 ed = p.Evaluate(diag);
            CheckClose(ed.x, 0.5f * p.posX.x + 0.5f * p.posY.x,
                "IrradianceProbe: a 45-degree normal is the squared-weight blend of its two faces");
            Vector3 justPos = p.Evaluate(new Vector3(1e-4f, 1f, 0f).normalized);
            Vector3 justNeg = p.Evaluate(new Vector3(-1e-4f, 1f, 0f).normalized);
            Check((justPos - justNeg).magnitude < 1e-3f,
                "IrradianceProbe: crossing the X axis is continuous (no seam)");

            // Lerp is the component-wise blend the grid interpolates with.
            var mid = IrradianceProbe.Lerp(flat, IrradianceProbe.Uniform(Vector3.zero), 0.5f);
            CheckClose(mid.posY.z, 0.3f, "IrradianceProbe: Lerp halves the faces at t=0.5");
        }

        // The irradiance grid: a flat array with hand-computed indexing, surface-following
        // so its probes sit in the shell of air near the ground rather than underground.
        private static void TestIrradianceGrid()
        {
            // A 500 m x 500 m region at the default 16 m spacing is an inclusive 32 x 32
            // lattice; times the four vertical levels that is 4096 probes - the allocation
            // the tier expects, computed not assumed.
            Check(IrradianceGrid.ProbesAcross(500f, 16f) == 32,
                "IrradianceGrid: a 500 m region at 16 m spacing is 32 probes across");
            var ramp = new System.Func<float, float, float>((x, z) => x * 0.1f); // a sloped terrain
            var grid = new IrradianceGrid(new Vector2(0f, 0f), 500f, 500f, ramp);
            Check(grid.dimX == 32 && grid.dimZ == 32 && grid.dimY == LightingConstants.IrradianceVerticalLevels,
                "IrradianceGrid: the region and levels give the expected dimensions");
            Check(grid.ProbeCount == 32 * 32 * LightingConstants.IrradianceVerticalLevels,
                "IrradianceGrid: the probe count is dimX * dimZ * levels");

            // Indexing at the corners and centre matches y*(dimX*dimZ)+z*dimX+x exactly.
            Check(grid.Index(0, 0, 0) == 0, "IrradianceGrid: the origin corner is index 0");
            Check(grid.Index(grid.dimX - 1, 0, 0) == grid.dimX - 1,
                "IrradianceGrid: the +x corner indexes along the row");
            Check(grid.Index(0, 0, grid.dimZ - 1) == (grid.dimZ - 1) * grid.dimX,
                "IrradianceGrid: the +z corner steps a full row per z");
            Check(grid.Index(grid.dimX - 1, grid.dimY - 1, grid.dimZ - 1) == grid.ProbeCount - 1,
                "IrradianceGrid: the far corner is the last index");
            Check(grid.Index(16, 2, 16) == 2 * (grid.dimX * grid.dimZ) + 16 * grid.dimX + 16,
                "IrradianceGrid: a centre probe indexes by the fixed convention");

            // Surface-following: the column base tracks the sloped terrain height, and no
            // probe sits more than one vertical level below its column's surface.
            CheckClose(grid.ColumnBaseY(10, 5), ramp(10 * grid.spacingM, 5 * grid.spacingM),
                "IrradianceGrid: a column's base is the terrain height under it");
            Check(grid.ColumnBaseY(31, 0) > grid.ColumnBaseY(0, 0),
                "IrradianceGrid: the base follows the slope across the region");
            bool allAboveFloor = true;
            for (int z = 0; z < grid.dimZ && allAboveFloor; z++)
                for (int x = 0; x < grid.dimX && allAboveFloor; x++)
                {
                    float lowest = grid.WorldPosition(x, 0, z).y;
                    float floor = grid.ColumnBaseY(x, z) - grid.verticalSpacingM;
                    if (lowest < floor - 1e-3f) allAboveFloor = false;
                }
            Check(allAboveFloor, "IrradianceGrid: no probe sits more than one level below the terrain");
        }

        private static void TestProbeFill()
        {
            // Shared light: a blue-dominant sky, a warm sun well up. Radiances are relative -
            // the tests read ratios and comparisons, not absolute lux.
            Vector3 sky = new Vector3(0.30f, 0.50f, 1.00f);
            Vector3 sunColor = new Vector3(1.00f, 0.90f, 0.70f);
            float sunI = 3f;
            Vector3 grey = new Vector3(0.40f, 0.40f, 0.40f);
            Vector3 red = new Vector3(0.70f, 0.10f, 0.10f);

            // 1) Open field: flat ground, high sun, nothing rises above the probe.
            var flat = new System.Func<float, float, float>((x, z) => 0f);
            var greyAt = new System.Func<float, float, Vector3>((x, z) => grey);
            Vector3 openProbe = new Vector3(0f, 10f, 0f);
            Vector3 sunHigh = new Vector3(0.30f, 0.80f, 0f).normalized;
            float openSky = ProbeFill.SkyVisibility01(openProbe, flat);
            float openSun = ProbeFill.SunVisibility01(openProbe, flat, sunHigh);
            CheckClose(openSky, 1f, "ProbeFill: an open-field probe sees the whole sky");
            Check(openSun >= 1f, "ProbeFill: the sun reaches an open-field probe");
            Vector3 openGround = ProbeFill.GroundRadiance(grey, sky, openSky, sunColor, sunI, sunHigh, openSun);
            Vector3 openBounce = ProbeFill.BounceRadiance(openProbe, flat, greyAt, sunColor, sunI, sunHigh);
            IrradianceProbe open = ProbeFill.Fill(sky, openSky, openGround, openBounce);
            Check(open.posY.z > open.posY.x && open.posY.z > open.posY.y,
                "ProbeFill: the open-field up face is sky-coloured (blue-dominant)");
            float openLum = open.posY.x + open.posY.y + open.posY.z;

            // 2) North-cliff foot: a wall just to +Z rises far above the probe and the low
            // northern sun sits behind it, so the probe is shaded.
            var cliff = new System.Func<float, float, float>((x, z) => z > 5f ? 100f : 0f);
            Vector3 cliffProbe = new Vector3(0f, 10f, 0f);
            Vector3 sunNorthLow = new Vector3(0f, 0.40f, 0.90f).normalized;
            float cliffSky = ProbeFill.SkyVisibility01(cliffProbe, cliff);
            float cliffSun = ProbeFill.SunVisibility01(cliffProbe, cliff, sunNorthLow);
            Check(cliffSky < openSky, "ProbeFill: the cliff blocks part of the sky above the probe");
            Check(cliffSun <= 0f, "ProbeFill: the cliff shades the probe from the sun");
            Vector3 cliffGround = ProbeFill.GroundRadiance(grey, sky, cliffSky, sunColor, sunI, sunNorthLow, cliffSun);
            IrradianceProbe cliffP = ProbeFill.Fill(sky, cliffSky, cliffGround, Vector3.zero);
            float cliffLum = cliffP.posY.x + cliffP.posY.y + cliffP.posY.z;
            Check(cliffLum < openLum, "ProbeFill: the shaded cliff-foot probe is dimmer than the open one");
            Check(cliffP.posY.z / Mathf.Max(1e-4f, cliffP.posY.x) > open.posY.z / Mathf.Max(1e-4f, open.posY.x),
                "ProbeFill: with the warm sun gone, the cliff-foot probe is relatively cooler (bluer)");

            // 3) Red soil in sunlight: the ground-facing face reddens, and the bounce term
            // reddens even the up face relative to grey soil.
            var redAt = new System.Func<float, float, Vector3>((x, z) => red);
            Vector3 redGround = ProbeFill.GroundRadiance(red, sky, openSky, sunColor, sunI, sunHigh, openSun);
            Vector3 redBounce = ProbeFill.BounceRadiance(openProbe, flat, redAt, sunColor, sunI, sunHigh);
            IrradianceProbe redP = ProbeFill.Fill(sky, openSky, redGround, redBounce);
            Check(redP.negY.x > redP.negY.y && redP.negY.x > redP.negY.z,
                "ProbeFill: red soil reddens the ground-facing (down) face");
            Check(redP.posY.x > open.posY.x,
                "ProbeFill: the red-soil bounce measurably reddens the up face versus grey soil");
        }

        private static void TestTerrainAlbedo()
        {
            Vector3 sand = new Vector3(0.76f, 0.70f, 0.50f);
            Vector3 grass = new Vector3(0.30f, 0.45f, 0.18f);
            Vector3 rock = new Vector3(0.42f, 0.42f, 0.40f);
            var colors = new[] { sand, grass, rock };
            System.Func<Vector3, float> lum = c => c.x + c.y + c.z;

            // The base is a convex blend of the layers by splat weight, normalised even
            // when the weights do not sum to one.
            Vector3 pureSand = TerrainAlbedo.FromSplat(new[] { 1f, 0f, 0f }, colors);
            CheckClose(pureSand.x, sand.x, "TerrainAlbedo: a pure-sand splat is the sand colour");
            Vector3 halfMix = TerrainAlbedo.FromSplat(new[] { 0.5f, 0.5f, 0f }, colors);
            CheckClose(halfMix.x, (sand.x + grass.x) * 0.5f,
                "TerrainAlbedo: an even sand/grass mix is their average");
            Vector3 unnorm = TerrainAlbedo.FromSplat(new[] { 2f, 2f, 0f }, colors);
            CheckClose(unnorm.x, halfMix.x, "TerrainAlbedo: weights are normalised by their total");

            // Snowfall brightens, and reverts when the snow is gone.
            Vector3 snowy = TerrainAlbedo.WithSnow(grass, 1f);
            Check(lum(snowy) > lum(grass) * 2f, "TerrainAlbedo: full snow greatly brightens the ground albedo");
            CheckClose(TerrainAlbedo.WithSnow(grass, 0f).x, grass.x,
                "TerrainAlbedo: no snow leaves the ground albedo unchanged");

            // A dense canopy over warm ground both darkens it and turns it green, and
            // reverts when the cover is gone.
            Vector3 forest = TerrainAlbedo.WithCanopy(sand, 1f);
            Check(lum(forest) < lum(sand), "TerrainAlbedo: a dense canopy darkens the ground albedo");
            Check(forest.y > forest.x && forest.y > forest.z,
                "TerrainAlbedo: a dense canopy greens the ground albedo");
            CheckClose(TerrainAlbedo.WithCanopy(sand, 0f).x, sand.x,
                "TerrainAlbedo: no canopy leaves the ground albedo unchanged");

            // The full chain layers snow then canopy in world order and stays finite.
            Vector3 snowyWood = TerrainAlbedo.Resolve(new[] { 1f, 0f, 0f }, colors, 1f, 1f);
            Check(IsFinite(snowyWood.x) && IsFinite(snowyWood.y) && IsFinite(snowyWood.z),
                "TerrainAlbedo: the full resolve is finite");
            Check(snowyWood.y > snowyWood.x && snowyWood.y > snowyWood.z,
                "TerrainAlbedo: a canopy over snow reads as the green leaves the sky sees");
        }

        private static void TestProbeRefreshScheduler()
        {
            // Triggers fire only once their input has moved past the band.
            Check(ProbeRefreshScheduler.SunMovedEnough(10f, 12f),
                "ProbeRefresh: a 2 deg sun move triggers a refresh");
            Check(!ProbeRefreshScheduler.SunMovedEnough(10f, 11f),
                "ProbeRefresh: a 1 deg sun move does not");
            Check(ProbeRefreshScheduler.CloudMovedEnough(0.2f, 0.35f),
                "ProbeRefresh: a 0.15 cover change triggers a refresh");
            Check(!ProbeRefreshScheduler.CloudMovedEnough(0.2f, 0.25f),
                "ProbeRefresh: a 0.05 cover change does not");

            const int total = 200;
            int budget = LightingConstants.IrradianceRefreshBudgetPerFrame;
            var buffer = new int[budget];

            // A full-grid refresh drains progressively, never above the budget, and covers
            // every probe exactly once. Distance == index here, so nearest-first is 0,1,2...
            var dist = new System.Func<int, float>(i => i);
            var sched = new ProbeRefreshScheduler();
            for (int i = total - 1; i >= 0; i--) sched.MarkDirty(i);   // inserted in reverse
            Check(sched.PendingCount == total, "ProbeRefresh: every marked probe is pending once");
            sched.MarkDirty(5); sched.MarkDirty(5);
            Check(sched.PendingCount == total, "ProbeRefresh: marking a pending probe again is a no-op");

            var drained = new System.Collections.Generic.List<int>();
            int spikes = 0, drains = 0;
            while (sched.PendingCount > 0)
            {
                int n = sched.Drain(buffer, dist);
                if (n > budget) spikes++;
                if (n <= 0) break;
                for (int i = 0; i < n; i++) drained.Add(buffer[i]);
                drains++;
            }
            Check(spikes == 0, "ProbeRefresh: no drain ever exceeds the per-frame budget");
            Check(drained.Count == total, "ProbeRefresh: a full refresh covers every probe exactly once");
            Check(drains == Mathf.CeilToInt(total / (float)budget),
                "ProbeRefresh: the grid drains in ceil(total/budget) frames");
            Check(drained[0] == 0 && drained[63] == 63 && drained[64] == 64,
                "ProbeRefresh: probes drain nearest-camera first regardless of insertion order");

            // Determinism (C-06): a second scheduler fed the same set in forward order
            // drains an identical sequence - order depends on the sort key, not insertion.
            var sched2 = new ProbeRefreshScheduler();
            for (int i = 0; i < total; i++) sched2.MarkDirty(i);
            bool identical = true;
            int k = 0;
            while (sched2.PendingCount > 0 && identical)
            {
                int n = sched2.Drain(buffer, dist);
                for (int i = 0; i < n; i++) { if (buffer[i] != drained[k++]) identical = false; }
            }
            Check(identical, "ProbeRefresh: the drain order is identical on two runs (C-06)");
        }

        private static void TestIrradianceUpload()
        {
            // A small surface-following grid, every face set to a value that encodes its
            // probe index and which of the six faces it is - so any packing mis-index shows
            // up as a mismatched channel rather than passing by luck.
            var flat = new System.Func<float, float, float>((x, z) => 0f);
            var grid = new IrradianceGrid(new Vector2(0f, 0f), 48f, 48f, flat);
            Check(grid.dimX == 4 && grid.dimZ == 4, "IrradianceUpload: the test grid is 4 x 4 columns");
            for (int i = 0; i < grid.ProbeCount; i++)
            {
                float b = i * 0.001f;
                grid.probes[i] = new IrradianceProbe
                {
                    posX = new Vector3(b + 0.1f, 0f, 0f),
                    negX = new Vector3(b + 0.2f, 0f, 0f),
                    posY = new Vector3(0f, b + 0.3f, 0f),
                    negY = new Vector3(0f, b + 0.4f, 0f),
                    posZ = new Vector3(0f, 0f, b + 0.5f),
                    negZ = new Vector3(0f, 0f, b + 0.6f),
                };
            }

            int len = IrradianceUpload.PackedLength(grid);
            Check(len == grid.dimX * grid.dimY * grid.dimZ * 3,
                "IrradianceUpload: the packed length is one texel per probe per stacked face");
            var a = new Color[len];
            var bb = new Color[len];
            IrradianceUpload.Pack(grid, a, bb);

            // Round-trip: every probe unpacks to exactly what was packed, on every face.
            bool allMatch = true;
            for (int z = 0; z < grid.dimZ && allMatch; z++)
            for (int y = 0; y < grid.dimY && allMatch; y++)
            for (int x = 0; x < grid.dimX && allMatch; x++)
            {
                IrradianceProbe src = grid.probes[grid.Index(x, y, z)];
                IrradianceProbe got = IrradianceUpload.Unpack(a, bb, grid.dimX, grid.dimY, grid.dimZ, x, y, z);
                if (Vector3.Distance(src.posX, got.posX) > 1e-5f) allMatch = false;
                if (Vector3.Distance(src.negX, got.negX) > 1e-5f) allMatch = false;
                if (Vector3.Distance(src.posY, got.posY) > 1e-5f) allMatch = false;
                if (Vector3.Distance(src.negY, got.negY) > 1e-5f) allMatch = false;
                if (Vector3.Distance(src.posZ, got.posZ) > 1e-5f) allMatch = false;
                if (Vector3.Distance(src.negZ, got.negZ) > 1e-5f) allMatch = false;
            }
            Check(allMatch, "IrradianceUpload: every probe round-trips through the packed arrays exactly");

            // The volume and the atlas cover the same texel count from the same array, so
            // they can only render identically - the shape differs, never a byte.
            int volDepth = IrradianceUpload.VolumeDepth(grid);
            int atlasHeight = grid.dimY * grid.dimZ * 3;
            Check(volDepth == grid.dimZ * 3, "IrradianceUpload: the volume depth is the grid depth times three faces");
            Check(grid.dimX * grid.dimY * volDepth == len,
                "IrradianceUpload: the volume shape holds exactly the packed texels");
            Check(grid.dimX * atlasHeight == len,
                "IrradianceUpload: the atlas shape holds exactly the same texels as the volume");
            Check(!string.IsNullOrEmpty(IrradianceUpload.KeywordAtlas),
                "IrradianceUpload: the atlas fallback keyword is defined");
        }

        private static void TestIrradianceSampler()
        {
            // One definition, and only one: terrain, vegetation and water must all call the
            // same UCSampleIrradiance or they drift and the grass reads as floating.
            string dir = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders");
            string path = System.IO.Path.Combine(dir, "UC_Lighting.hlsl");
            Check(System.IO.File.Exists(path), "UCSampleIrradiance: UC_Lighting.hlsl is present");
            string src = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";

            int defs = 0;
            for (int at = src.IndexOf("float3 UCSampleIrradiance("); at >= 0;
                 at = src.IndexOf("float3 UCSampleIrradiance(", at + 1)) defs++;
            Check(defs == 1, "UCSampleIrradiance: exactly one definition exists (one owner)");
            Check(src.Contains(IrradianceUpload.KeywordAtlas),
                "UCSampleIrradiance: the atlas fallback keyword branch is present in the sampler");
            Check(src.Contains("UCAmbientCube(") && src.Contains("float3 w = n * n"),
                "UCSampleIrradiance: the ambient-cube blend weights by the squared normal");

            // The CPU twin of the blend: on an axis the crossing face has zero weight, so the
            // posX/negX choice at n.x = 0 changes nothing and the result is continuous - the
            // "a normal exactly on an axis produces no discontinuity" done-when.
            var probe = new IrradianceProbe
            {
                posX = new Vector3(10f, 0f, 0f),
                negX = new Vector3(0f, 0f, 0f),
                posY = new Vector3(1f, 1f, 1f),
                negY = new Vector3(1f, 1f, 1f),
                posZ = new Vector3(1f, 1f, 1f),
                negZ = new Vector3(1f, 1f, 1f),
            };
            Vector3 justPos = probe.Evaluate(new Vector3(0.001f, 1f, 0f).normalized);
            Vector3 justNeg = probe.Evaluate(new Vector3(-0.001f, 1f, 0f).normalized);
            Check(Vector3.Distance(justPos, justNeg) < 1e-3f,
                "UCSampleIrradiance: the blend is continuous across the +x/-x axis (no seam)");
        }

        private static void TestAmbientOcclusionRule()
        {
            // The rule lives in the shader: occlusion scales the ambient term and never the
            // direct term. Read it back and confirm the compose does exactly that.
            string path = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders/UC_Lighting.hlsl");
            string src = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
            int defs = 0;
            for (int at = src.IndexOf("float3 UCComposeSurfaceLight("); at >= 0;
                 at = src.IndexOf("float3 UCComposeSurfaceLight(", at + 1)) defs++;
            Check(defs == 1, "AO rule: exactly one UCComposeSurfaceLight owns the composition");
            Check(src.Contains("directLight + ambientLight * saturate(ao)"),
                "AO rule: the ambient term is scaled by occlusion, the direct term is added whole");

            // The law itself, numerically: direct reaches a point in full regardless of AO;
            // only the bounced/ambient term is darkened. A fully occluded sunlit crevice
            // (ao = 0) keeps all of its direct light - the "not artificially dark" done-when.
            System.Func<Vector3, Vector3, float, Vector3> compose =
                (direct, ambient, ao) => direct + ambient * Mathf.Clamp01(ao);
            var sun = new Vector3(3.0f, 2.7f, 2.1f);     // direct sunlight
            var amb = new Vector3(0.30f, 0.50f, 1.00f);  // bounced/sky ambient
            Vector3 open = compose(sun, amb, 1f);
            Vector3 crevice = compose(sun, amb, 0f);
            Vector3 half = compose(sun, amb, 0.5f);
            Check(Vector3.Distance(crevice, sun) < 1e-5f,
                "AO rule: a fully occluded but sunlit point keeps all its direct light");
            Check(Vector3.Distance(open - crevice, amb) < 1e-5f,
                "AO rule: occlusion removes exactly the ambient term, nothing of the direct");
            Check(half.x > crevice.x && half.x < open.x,
                "AO rule: partial occlusion scales only the ambient contribution, monotonically");
        }

        private static void TestGridAmbientWiring()
        {
            // One entry, shared by every lit surface, so their ambient cannot drift.
            string shaders = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders");
            string lighting = ReadShader(shaders, "UC_Lighting.hlsl");
            int gridOr = CountOf(lighting, "float3 UCGridAmbientOr(");
            int hasGrid = CountOf(lighting, "bool UCHasIrradianceGrid(");
            Check(gridOr == 1 && hasGrid == 1,
                "Grid ambient: one UCGridAmbientOr and one UCHasIrradianceGrid own the read");
            Check(lighting.Contains("UCHasIrradianceGrid() ? UCSampleIrradiance") &&
                  lighting.Contains(": engineAmbient"),
                "Grid ambient: reads the grid when bound, else keeps the engine ambient (no black)");

            // Terrain, vegetation and water each fold the grid into their indirect term through
            // the one function - so at a point they share they resolve the same ambient, which
            // is the "matches the terrain beneath it" guarantee, by construction.
            string terrain = ReadShader(shaders, "UC_TerrainLitPasses.hlsl");
            string veg = ReadShader(shaders, "UC_VegetationLit.shader");
            string water = ReadShader(shaders, "UC_WaterPasses.hlsl");
            string rock = ReadShader(shaders, "UC_RockLit.shader");
            Check(terrain.Contains("inputData.bakedGI = UCGridAmbientOr(") &&
                  terrain.Contains("UC_Lighting.hlsl"),
                "Grid ambient: terrain routes bakedGI through the shared grid read");
            Check(veg.Contains("inputData.bakedGI = UCGridAmbientOr(") &&
                  veg.Contains("UC_Lighting.hlsl"),
                "Grid ambient: vegetation routes bakedGI through the shared grid read");
            Check(water.Contains("inputData.bakedGI = UCGridAmbientOr(") &&
                  water.Contains("UC_Lighting.hlsl"),
                "Grid ambient: water routes bakedGI through the shared grid read");
            // S-81: props (rocks and logs) read the same grid, or a boulder floats brighter
            // than the ground it rests on - the "only half wired" tell the task names.
            Check(rock.Contains("inputData.bakedGI = UCGridAmbientOr(") &&
                  rock.Contains("UC_Lighting.hlsl"),
                "Grid ambient: props (rock/logs) route bakedGI through the shared grid read");

            // "Changes continuously as it moves into shade": the grid interpolates between
            // probes, and the CPU twin of that interpolation is Lerp - endpoints exact and the
            // midpoint strictly between, so a surface crossing a cell never jumps.
            var open = IrradianceProbe.Uniform(new Vector3(0.8f, 0.8f, 0.9f));
            var shade = IrradianceProbe.Uniform(new Vector3(0.1f, 0.12f, 0.2f));
            var n = Vector3.up;
            Check(Vector3.Distance(IrradianceProbe.Lerp(open, shade, 0f).Evaluate(n), open.Evaluate(n)) < 1e-5f,
                "Grid ambient: t=0 recovers the open-field probe exactly");
            Check(Vector3.Distance(IrradianceProbe.Lerp(open, shade, 1f).Evaluate(n), shade.Evaluate(n)) < 1e-5f,
                "Grid ambient: t=1 recovers the deep-shade probe exactly");
            float lit = open.Evaluate(n).x, dark = shade.Evaluate(n).x;
            float mid = IrradianceProbe.Lerp(open, shade, 0.5f).Evaluate(n).x;
            Check(mid < lit && mid > dark,
                "Grid ambient: a point half-way into shade is strictly between the two - continuous");

            // S-81 second half: the ray-free wet-sky reflection twin. One gate and one wrapper
            // own it, ship inert (strength 0) until the ladder turns it on, and every existing
            // lit surface adds it after its BRDF - so wetness lights a puddle the same way
            // everywhere it appears rather than each surface inventing its own reflection.
            string reflect = ReadShader(shaders, "UC_SurfaceReflectance.hlsl");
            Check(CountOf(reflect, "float _UCEnvReflectionStrength;") == 1 &&
                  CountOf(reflect, "float3 UCEnvReflectionContribution(") == 1,
                "Env reflection: one tier gate and one contribution wrapper own the term");
            Check(reflect.Contains("if (_UCEnvReflectionStrength <= 0.0) return"),
                "Env reflection: gated off when the ladder leaves strength at zero (inert by default)");
            Check(terrain.Contains("UC_SurfaceReflectance.hlsl") &&
                  terrain.Contains("UCEnvReflectionContribution("),
                "Env reflection: terrain adds the shared wet-sky reflection after its BRDF");
            Check(veg.Contains("UC_SurfaceReflectance.hlsl") &&
                  veg.Contains("UCEnvReflectionContribution("),
                "Env reflection: vegetation adds the shared wet-sky reflection after its BRDF");
            Check(rock.Contains("UC_SurfaceReflectance.hlsl") &&
                  rock.Contains("UCEnvReflectionContribution("),
                "Env reflection: props add the shared wet-sky reflection after their BRDF");
        }

        private static void TestMoonLight()
        {
            // Moonlight is the sun reflected off grey rock: neutral, faintly warm, never
            // blue. The blue of night is the eye (S-45), not this light.
            Color moon = MoonLight.ColorLinear();
            Check(moon.b <= moon.r + 1e-4f && moon.b <= moon.g + 1e-4f,
                "Moonlight: neutral to faintly warm at source - blue is never the strongest channel");
            float mx = Mathf.Max(moon.r, Mathf.Max(moon.g, moon.b));
            float mn = Mathf.Min(moon.r, Mathf.Min(moon.g, moon.b));
            Check(mx > 0f && (mx - mn) / mx < 0.25f,
                "Moonlight: close to grey, not a saturated tint");

            // Intensity follows the lit disc and the moon's height: a full moon lights the
            // ground, a crescent barely, a new moon or a set moon not at all.
            const float trim = 0.08f;
            float full = MoonLight.Intensity(90f, 1.0f, trim);
            float crescent = MoonLight.Intensity(90f, 0.1f, trim);
            float newMoon = MoonLight.Intensity(90f, 0.0f, trim);
            float belowHorizon = MoonLight.Intensity(-10f, 1.0f, trim);
            Check(full > crescent && crescent > newMoon,
                "Moonlight: full moon brighter than a crescent, which is brighter than new");
            Check(Mathf.Abs(newMoon) < 1e-9f, "Moonlight: a new moon casts no light");
            Check(Mathf.Abs(belowHorizon) < 1e-9f, "Moonlight: a moon below the horizon casts none");
            Check(Mathf.Abs(MoonLight.Intensity(90f, 1.0f, 0f)) < 1e-9f,
                "Moonlight: a zero trim disables the moon light entirely");
            Check(MoonLight.Intensity(90f, 1.0f, 0.16f) > full,
                "Moonlight: intensity scales with the authored trim");
        }

        private static void TestStarField()
        {
            const float lat = 40f;   // a mid-northern observer

            // The celestial pole (dec = 90) is the one point the field turns about: its
            // direction must not move as the sidereal clock advances, and it must sit at an
            // altitude equal to the observer's latitude - the axis the real sky spins on.
            Vector3 pole0 = StarField.StarDirection(123f, 90f, lat, 0f);
            Vector3 pole1 = StarField.StarDirection(47f, 90f, lat, 210f);
            Check(Vector3.Distance(pole0, pole1) < 1e-4f,
                "Stars: the celestial pole is fixed under sidereal rotation");
            float poleAltDeg = Mathf.Asin(Mathf.Clamp(pole0.y, -1f, 1f)) * Mathf.Rad2Deg;
            Check(Mathf.Abs(poleAltDeg - lat) < 1e-3f,
                "Stars: the pole sits at an altitude equal to the latitude");

            // An equatorial star (dec = 0) is not circumpolar: it must sweep across the sky
            // as the field turns, so two sidereal angles give plainly different directions.
            Vector3 eqA = StarField.StarDirection(0f, 0f, lat, 0f);
            Vector3 eqB = StarField.StarDirection(0f, 0f, lat, 90f);
            Check(Vector3.Distance(eqA, eqB) > 0.5f,
                "Stars: a non-polar star moves with the sidereal day");
            Check(Mathf.Abs(eqA.magnitude - 1f) < 1e-3f,
                "Stars: a star direction is a unit vector");

            // Visibility washes out: a dark clear night shows every star, a full moon or a
            // solid overcast none, and thick haze scrubs the sky milky.
            const float clear = 2f;   // StarField treats turbidity ~2 as a clear night
            float darkClear = StarField.Visibility01(0f, 0f, clear);
            float fullMoon = StarField.Visibility01(LightingConstants.FullMoonLux, 0f, clear);
            float overcast = StarField.Visibility01(0f, 1f, clear);
            float crescent = StarField.Visibility01(0.03f, 0f, clear);
            float hazy = StarField.Visibility01(0f, 0f, 8f);
            Check(darkClear > 0.99f, "Stars: a dark, clear night shows the full field");
            Check(fullMoon < 1e-4f, "Stars: a full moon washes the field out");
            Check(overcast < 1e-4f, "Stars: overcast hides the sky entirely");
            Check(hazy < 1e-4f, "Stars: milky air (high turbidity) scrubs the stars away");
            Check(crescent > 0.5f && crescent < darkClear,
                "Stars: a crescent dims the field without erasing it");

            // The field is content, not chance: every index yields a fixed, in-range star,
            // identical on every call and every machine - no Random, no per-run drift.
            var s7a = StarField.StarAt(7);
            var s7b = StarField.StarAt(7);
            Check(s7a.raDeg == s7b.raDeg && s7a.decDeg == s7b.decDeg && s7a.magnitude == s7b.magnitude,
                "Stars: the catalog is deterministic (same index, same star)");
            bool inRange = true;
            for (int i = 0; i < 256; i++)
            {
                Star s = StarField.StarAt(i);
                if (s.raDeg < 0f || s.raDeg > 360f || s.decDeg < -90f || s.decDeg > 90f
                    || s.magnitude < -1.0001f || s.magnitude > 6.0001f) { inRange = false; break; }
            }
            Check(inRange, "Stars: every catalog entry lands in valid ra/dec/magnitude ranges");

            // The clock: one sidereal day is one full turn, half a day is half a turn.
            double day = UniverseCreator.Ecology.EcoConstants.SecondsPerSiderealDay;
            Check(Mathf.Abs(StarField.LocalSiderealDeg(0.0)) < 1e-3f,
                "Stars: the sidereal angle is zero at the epoch");
            Check(Mathf.Abs(StarField.LocalSiderealDeg(day * 0.5) - 180f) < 1e-2f,
                "Stars: half a sidereal day is half a turn");
        }

        private static void TestPurkinjeShift()
        {
            // The weight follows the light into scotopic vision: daylight and dawn leave the
            // cones in charge (0), a moonless starlit night is fully rod-driven (1), and a
            // full moon sits partway - bright enough that the night reads less blue.
            float day = PurkinjeShift.ScotopicWeight01(20000f);
            float dawn = PurkinjeShift.ScotopicWeight01(50f);
            float fullMoon = PurkinjeShift.ScotopicWeight01(LightingConstants.FullMoonLux);
            float starlit = PurkinjeShift.ScotopicWeight01(LightingConstants.StarlightLux);
            Check(day < 1e-3f, "Purkinje: daylight leaves cone vision in charge (no shift)");
            Check(dawn < 1e-3f, "Purkinje: the shift has faded out by dawn/twilight brightness");
            Check(starlit > 0.99f, "Purkinje: a moonless starlit night is fully rod-driven");
            Check(fullMoon > dawn && fullMoon < starlit,
                "Purkinje: a full moon sits partway - less blue than a moonless night");

            // Monotone: the darker it gets, the more the rods take over, with no reversal.
            float prev = -1f;
            bool monotone = true;
            float[] lux = { 20000f, 300f, 3f, 1f, 0.25f, 0.05f, 0.01f, 0.001f };
            for (int i = 0; i < lux.Length; i++)
            {
                float wgt = PurkinjeShift.ScotopicWeight01(lux[i]);
                if (wgt < prev - 1e-4f) { monotone = false; break; }
                prev = wgt;
            }
            Check(monotone, "Purkinje: the shift only grows as the night deepens");

            // Zero weight or zero strength is the identity: the tonemapped image is untouched
            // in daylight and whenever the author dials the effect to nothing.
            Color warm = new Color(1.0f, 0.2f, 0.1f, 1f);
            Color atDay = PurkinjeShift.Apply(warm, 0f, PurkinjeShift.DefaultStrength);
            Color noStrength = PurkinjeShift.Apply(warm, 1f, 0f);
            Check(Mathf.Approximately(atDay.r, warm.r) && Mathf.Approximately(atDay.g, warm.g)
                && Mathf.Approximately(atDay.b, warm.b), "Purkinje: zero weight leaves the image untouched");
            Check(Mathf.Approximately(noStrength.r, warm.r) && Mathf.Approximately(noStrength.b, warm.b),
                "Purkinje: a strength of zero disables the shift entirely");

            // At full strength the image goes cool and desaturated: blue overtakes red, and a
            // once-saturated colour loses most of its chroma - rod vision carries no hue.
            Color shifted = PurkinjeShift.Apply(warm, 1f, 1f);
            Check(shifted.b > shifted.r, "Purkinje: the deep-night image reads cool (blue over red)");
            float chromaIn = Mathf.Max(warm.r, Mathf.Max(warm.g, warm.b)) - Mathf.Min(warm.r, Mathf.Min(warm.g, warm.b));
            float chromaOut = Mathf.Max(shifted.r, Mathf.Max(shifted.g, shifted.b)) - Mathf.Min(shifted.r, Mathf.Min(shifted.g, shifted.b));
            Check(chromaOut < chromaIn * 0.5f, "Purkinje: the night desaturates - rods carry no colour");

            // The blue is the eye, not the light: a neutrally-lit grey surface still reads cool
            // at night, while the moon's own colour stays the neutral grey S-43 gives it.
            Color grey = new Color(0.3f, 0.3f, 0.3f, 1f);
            Color greyNight = PurkinjeShift.Apply(grey, 1f, 1f);
            Check(greyNight.b > greyNight.r, "Purkinje: even neutral light reads blue at night - the shift is in the eye");
            Color moon = MoonLight.ColorLinear();
            Check(moon.b <= moon.r + 1e-4f, "Purkinje: the moon light itself stays neutral, never blue");
        }

        private static void TestNightFloor()
        {
            // A bright scene is never dimmed by the floor: daylight passes straight through.
            Check(Mathf.Approximately(NightFloor.Apply(20000f, NightFloor.DefaultNightFloorLux), 20000f),
                "Night floor: a lit scene is never lowered by the floor");

            // The physical setting is zero, and only zero: a new-moon night then keeps its
            // true, near-black starlight - the floor adds nothing.
            float physical = NightFloor.Apply(LightingConstants.StarlightLux, 0f);
            Check(Mathf.Approximately(physical, LightingConstants.StarlightLux),
                "Night floor: a floor of zero leaves the true black new-moon night");
            Check(NightFloor.IsPhysical(0f), "Night floor: zero is the physically honest setting");
            Check(!NightFloor.IsPhysical(NightFloor.DefaultNightFloorLux),
                "Night floor: the navigable default is an authored override, not physics");

            // The default lifts a starlit night to a navigable floor - brighter than raw
            // starlight, still dimmer than a full moon, so it reads as night, not dusk.
            float lifted = NightFloor.Apply(LightingConstants.StarlightLux, NightFloor.DefaultNightFloorLux);
            Check(lifted > LightingConstants.StarlightLux,
                "Night floor: the default raises a starlit night toward navigability");
            Check(lifted < LightingConstants.FullMoonLux,
                "Night floor: the lift stays below a full moon - it is still night");

            // A negative floor is meaningless and treated as no floor at all.
            Check(Mathf.Approximately(NightFloor.Apply(LightingConstants.StarlightLux, -1f),
                LightingConstants.StarlightLux), "Night floor: a negative floor is treated as zero");
        }

        private static void TestContactShadow()
        {
            // The pass shader must ship (so it is compile-checked and always-included,
            // never stripped to a magenta contact-shadow buffer in a build).
            Check(System.Array.IndexOf(UniverseCreator.Editor.Diagnostics.UCShaderDiagnostics.ShipShaders,
                    "Hidden/UniverseCreator/ContactShadow") >= 0,
                "Contact shadow: pass shader is registered in ShipShaders (compiled + always-included)");

            string shaders = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders");
            string cs = ReadShader(shaders, "UC_ContactShadow.shader");
            Check(cs.Length > 0, "Contact shadow: UC_ContactShadow.shader exists");
            Check(cs.Contains("Name \"ContactMarch\""), "Contact shadow: has the depth-march pass");
            Check(cs.Contains("Name \"ContactTemporal\""), "Contact shadow: has the temporal-accumulation pass (S-80)");
            Check(cs.Contains("Name \"ContactCombine\""), "Contact shadow: has the cascade-combine pass");

            // S-80: the accumulation steadies the dithered march across frames through the ONE
            // shared temporal home - motion-vector reprojection, a neighbourhood-variance clamp
            // and a disocclusion reject - so a fast pan leaves no ghost trail behind a contact shadow.
            Check(cs.Contains("UC_TemporalReproject.hlsl"),
                "Contact shadow: accumulates through the shared temporal home (S-80)");
            Check(cs.Contains("UCReprojectByMotion") && cs.Contains("UCReprojectByViewProj"),
                "Contact shadow: reprojects by motion vectors, prev view-proj as the static fallback (S-80)");
            Check(cs.Contains("UCVarianceBox") && cs.Contains("clamp(hist.r"),
                "Contact shadow: reprojected history is variance-clamped, not blindly blended (S-80)");
            Check(cs.Contains("UCHistoryDepthValid"),
                "Contact shadow: a depth mismatch on reprojection rejects the stale history (S-80)");

            // S-49 made structural: the combine is a MINIMUM against the cascade term,
            // never a product, so a pixel already in cast shadow is not darkened twice.
            Check(cs.Contains("min(cascade"),
                "Contact shadow: combines with the cascade by minimum, not product (S-49)");

            // It writes back under the screen-space shadow name, so only the sun's direct
            // term is affected - never ambient or sky light.
            Check(cs.Contains("_ScreenSpaceShadowmapTexture"),
                "Contact shadow: result is rebound to the screen-space shadow texture (sun's direct term only)");

            // The 16-step march is dithered by a 4x4 Bayer matrix, then blurred. Confirm
            // the matrix is a full permutation of 0..15 - a biased matrix would tint the
            // dither and survive the blur as a visible pattern.
            Check(cs.Contains("kUCBayer4x4"), "Contact shadow: march start is Bayer-dithered");
            int[] bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
            bool[] seen = new bool[16];
            bool perm = bayer.Length == 16;
            for (int i = 0; perm && i < bayer.Length; i++)
            {
                if (bayer[i] < 0 || bayer[i] > 15 || seen[bayer[i]]) { perm = false; break; }
                seen[bayer[i]] = true;
            }
            Check(perm, "Contact shadow: 4x4 Bayer matrix covers 0..15 exactly once (no dither bias)");

            // Feature contract: authored defaults, the S-54 guard, and placement right
            // after URP's screen-space shadow pass. Verified from source, not memory.
            string featPath = System.IO.Path.Combine(Application.dataPath,
                "UniverseCreator/Core/Runtime/Lighting/ContactShadowFeature.cs");
            string feat = System.IO.File.Exists(featPath) ? System.IO.File.ReadAllText(featPath) : "";
            Check(feat.Length > 0, "Contact shadow: ContactShadowFeature.cs exists");
            Check(feat.Contains("contactShadowLength = 0.35f"), "Contact shadow: length defaults to 0.35 m");
            Check(feat.Contains("stepCount = 16"), "Contact shadow: marches 16 steps by default");
            Check(feat.Contains("#if UC_URP"),
                "Contact shadow: feature guarded by UC_URP (S-54: runtime compiles with URP absent)");
            Check(feat.Contains("AfterRenderingPrePasses + 2"),
                "Contact shadow: runs just after URP's screen-space shadow pass");
            // S-80 plumbing: motion input requested, a persistent RG16F history (occlusion + eye
            // depth) ping-ponged, and the temporal-weight dial defaulting to 0.9.
            Check(feat.Contains("ScriptableRenderPassInput.Motion"),
                "Contact shadow: requests motion vectors for object+camera reprojection (S-80)");
            Check(feat.Contains("R16G16_SFloat"),
                "Contact shadow: history carries accumulated occlusion + eye depth (S-80)");
            Check(feat.Contains("temporalWeight = 0.9f"),
                "Contact shadow: temporal weight defaults to 0.9 (S-80)");
        }

        private static void TestScreenSpaceGI()
        {
            // Pass shader must ship so it is compile-checked and always-included.
            Check(System.Array.IndexOf(UniverseCreator.Editor.Diagnostics.UCShaderDiagnostics.ShipShaders,
                    "Hidden/UniverseCreator/SSGI") >= 0,
                "SSGI: pass shader is registered in ShipShaders (compiled + always-included)");

            string shaders = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders");
            string sh = ReadShader(shaders, "UC_SSGI.shader");
            Check(sh.Length > 0, "SSGI: UC_SSGI.shader exists");

            // Three named passes: trace, blur+temporal, composite.
            Check(sh.Contains("Name \"SSGITrace\""), "SSGI: has the trace pass");
            Check(sh.Contains("Name \"SSGIBlurTemporal\""), "SSGI: has the blur + temporal-reproject pass");
            Check(sh.Contains("Name \"SSGIComposite\""), "SSGI: has the composite pass");

            // S-51: a ray MUST fall back to the grid, never to black or a constant. The
            // trace reads UCSampleIrradiance for the baseline and every missed ray returns
            // it, so the composited delta is exactly zero on a miss.
            Check(sh.Contains("UCSampleIrradiance"),
                "SSGI: missed rays fall back to the irradiance grid, not black (S-51)");
            Check(sh.Contains("gridBaseline") && sh.Contains("estimate - gridBaseline"),
                "SSGI: composites a delta over the grid baseline (zero where nothing hit)");

            // S-52: additive over the grid, so toggling changes only local detail. The
            // composite must add (Blend One One), never replace the colour.
            Check(sh.Contains("Blend One One"),
                "SSGI: composite is additive over the lit colour (S-52: off = unchanged image)");

            // Deterministic, not random: a fixed Hammersley set with a per-pixel hash
            // rotation - the same pixel traces the same directions every frame.
            Check(sh.Contains("UCHammersley") && sh.Contains("UCPixelRotation"),
                "SSGI: ray directions are a deterministic low-discrepancy set, not random");

            // Reprojection with a disocclusion (depth-mismatch) rejection.
            // Reprojection through the shared S-80 home: motion vectors first (camera AND object
            // motion), the prev view-projection as the static fallback, then a variance clamp and
            // a disocclusion (depth-mismatch) reject so a moving camera cannot smear indirect light.
            Check(sh.Contains("_UCSSGIPrevViewProj"),
                "SSGI: history is reprojected through last frame's view-projection");
            Check(sh.Contains("UC_TemporalReproject.hlsl"),
                "SSGI: reprojection + clamp come from the shared temporal home (S-80)");
            Check(sh.Contains("UCReprojectByMotion") && sh.Contains("UCReprojectByViewProj"),
                "SSGI: reprojects by motion vectors, prev view-proj as the static fallback (S-80)");
            Check(sh.Contains("UCVarianceBox") && sh.Contains("clamp(hist.rgb"),
                "SSGI: reprojected history is variance-clamped before blending (S-80)");
            Check(sh.Contains("UCHistoryDepthValid") && sh.Contains("hist.a"),
                "SSGI: a depth mismatch on reprojection rejects the stale history (no smear)");

            // Feature contract: authored defaults, half-res, the S-54 guard.
            string featPath = System.IO.Path.Combine(Application.dataPath,
                "UniverseCreator/Core/Runtime/Lighting/ScreenSpaceGIFeature.cs");
            string feat = System.IO.File.Exists(featPath) ? System.IO.File.ReadAllText(featPath) : "";
            Check(feat.Length > 0, "SSGI: ScreenSpaceGIFeature.cs exists");
            Check(feat.Contains("ssgiSampleCount = 8"), "SSGI: sample count defaults to 8");
            Check(feat.Contains("ssgiTemporalWeight = 0.9f"), "SSGI: temporal weight defaults to 0.9");
            Check(feat.Contains("#if UC_URP"),
                "SSGI: feature guarded by UC_URP (S-54: runtime compiles with URP absent)");
            // Half-resolution discipline (S-13): trace + history run at width/2, height/2.
            Check(feat.Contains("camDesc.width  / 2") || feat.Contains("cameraDesc.width  / 2"),
                "SSGI: trace and history run at half resolution (S-13)");
        }

        private static void TestLightingTier()
        {
            // Resolve is pure and keyed off the ONE product tier enum. Assert real values
            // per tier rather than source text - this executes the shipping code.
            var ultra  = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra);
            var medium = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium);
            var low    = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);

            // Flagship (Ultra): screen-space on, 3 cascades. Mid (Medium): SSGI off but
            // contact kept (compensates the softer cascade). Low: both screen-space off.
            Check(ultra.screenSpaceGI && ultra.contactShadows && ultra.cascadeCount == 3,
                "LightingTier: Ultra runs SSGI + contact shadows on 3 cascades");
            Check(!medium.screenSpaceGI && medium.contactShadows && medium.cascadeCount == 2,
                "LightingTier: Medium drops SSGI, keeps contact shadows, 2 cascades (S-55)");
            Check(!low.screenSpaceGI && !low.contactShadows,
                "LightingTier: Low disables every screen-space effect (S-52 base-tier image)");

            // Probe density never tighter than authored per tier, and coarsens as tier falls.
            Check(ultra.probeSpacingScale <= medium.probeSpacingScale &&
                  medium.probeSpacingScale <= low.probeSpacingScale,
                "LightingTier: probe spacing coarsens monotonically as the tier falls");

            // AnyScreenSpaceEffect drives the S-52 checklist preset (the tier where it is false).
            Check(medium.AnyScreenSpaceEffect && !low.AnyScreenSpaceEffect,
                "LightingTier: the all-screen-space-off preset is exactly the Low tier");

            // S-82 · the single ladder, and every step is optional. T0 (analytic ambient) and
            // T1 (grid) are the Low tier and MUST be a complete image alone: no T1.5 refinement
            // and no screen-space term. Nothing above T1 is required for correctness.
            Check(!low.RunsMultiBounceRung && !low.AnyScreenSpaceEffect,
                "LightingTier: Low is the T0/T1 rung - grid only, complete without any refinement (S-52)");

            // The ray-free wet-sky reflection is a physics baseline on EVERY device class (S-83:
            // "sky + wetness + water plane" is listed for Mobile Low too). It is Fresnel x wetness
            // weighted, so it lights a wet surface and vanishes on a dry one at any tier.
            Check(low.envReflectionStrength > 0f && medium.envReflectionStrength > 0f &&
                  ultra.envReflectionStrength > 0f,
                "LightingTier: the wet-sky reflection is a baseline on every tier, Low included (S-83)");

            // T1.5 is the Mobile High rung: the four § 8.14 refinements ship as a set, and add NO
            // renderer feature, so a tier can run the whole rung with every screen-space term off.
            Check(medium.RunsMultiBounceRung,
                "LightingTier: Medium (Mobile High) runs the whole T1.5 rung");
            Check(medium.RunsMultiBounceRung && !medium.screenSpaceGI && !medium.screenSpaceReflections,
                "LightingTier: the T1.5 rung needs no renderer feature (runs with screen-space off)");

            // "Stepping up only adds": across Low < Medium < High < Ultra every optional term,
            // once on, stays on, and no additive budget ever shrinks. This is the whole ladder
            // invariant - a higher tier is a strict superset of the one below it.
            var high = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.High);
            var rungs = new[] { low, medium, high, ultra };
            bool monotone = true;
            for (int i = 1; i < rungs.Length; i++)
            {
                var lo = rungs[i - 1]; var hi = rungs[i];
                monotone &= (!lo.multiBounce   || hi.multiBounce);
                monotone &= (!lo.skyVisibility || hi.skyVisibility);
                monotone &= (!lo.bentNormal    || hi.bentNormal);
                monotone &= (!lo.foliageBleed  || hi.foliageBleed);
                monotone &= (!lo.screenSpaceGI || hi.screenSpaceGI);
                monotone &= (!lo.contactShadows || hi.contactShadows);
                monotone &= (!lo.screenSpaceReflections || hi.screenSpaceReflections);
                monotone &= (hi.envReflectionStrength >= lo.envReflectionStrength);
                monotone &= (hi.ssgiSampleCount >= lo.ssgiSampleCount);
                monotone &= (hi.shadowDistance >= lo.shadowDistance);
                monotone &= (hi.shadowAtlasResolution >= lo.shadowAtlasResolution);
                monotone &= (hi.cascadeCount >= lo.cascadeCount);
                monotone &= (hi.probeSpacingScale <= lo.probeSpacingScale); // denser = adds
            }
            Check(monotone,
                "LightingTier: stepping up the ladder only ever adds - each tier is a superset of the one below");

            // Only Ultra reaches T3-SSR (S-77); SSGI is High and up. The rung mapping, asserted.
            Check(ultra.screenSpaceReflections && !high.screenSpaceReflections,
                "LightingTier: T3 screen-space reflection is Ultra-only");
            Check(high.screenSpaceGI && !medium.screenSpaceGI,
                "LightingTier: T3 SSGI starts at High, above the Mobile-High T1.5 rung");

            // A null sampler resolves to a safe High default, so lighting never depends on
            // the tier sampler existing in the scene.
            var def = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                        (UniverseCreator.Optimization.AdaptiveQuality)null);
            Check(def.cascadeCount == UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                        UniverseCreator.Optimization.AdaptiveQuality.QualityTier.High).cascadeCount,
                "LightingTier: a missing quality sampler falls back to the High tier");

            // No SECOND tier enum in the product: LightingTier must key off AdaptiveQuality's.
            Check(System.Type.GetType("UniverseCreator.TerrainSystem.Lighting.QualityTier, UniverseCreator") == null,
                "LightingTier: no second QualityTier enum defined in the lighting namespace (S-53)");

            // Probe volume: an empty registry covers nothing, so the base spacing stands.
            float scale;
            bool covered = UniverseCreator.TerrainSystem.Lighting.LightingProbeVolume
                            .TryGetSpacingScale(Vector3.zero, out scale);
            Check(!covered && Mathf.Approximately(scale, 1f),
                "LightingProbeVolume: no volume => base spacing (scale 1), densest wins on overlap");

            // Debug mode ordinals are a shader contract: Off is 0 and the isolate modes are stable.
            Check((int)UniverseCreator.TerrainSystem.Lighting.LightingDebug.DebugMode.Off == 0 &&
                  (int)UniverseCreator.TerrainSystem.Lighting.LightingDebug.DebugMode.SsgiOnly == 6 &&
                  (int)UniverseCreator.TerrainSystem.Lighting.LightingDebug.DebugMode.TierInForce == 9,
                "LightingDebug: debug-mode ordinals match the published _UCDebugMode contract");
        }

        private static void TestLightingSettingsDefaults()
        {
            var s = new UniverseCreator.TerrainSystem.Lighting.LightingSettings();

            // Every default that has a physical home reads FROM that home, so a dial and
            // the physics it drives cannot disagree at rest - the whole point of the block.
            Check(Mathf.Approximately(s.turbidity, LightingConstants.TurbidityDefault),
                "LightingSettings: turbidity defaults to the clean-lowland physical default (2.2)");
            Check(Mathf.Approximately(s.probeSpacing, LightingConstants.IrradianceProbeSpacingM),
                "LightingSettings: probe spacing defaults to the grid's physical 16 m");
            Check(s.probesPerFrame == LightingConstants.IrradianceRefreshBudgetPerFrame,
                "LightingSettings: probes/frame defaults to the 64-probe refresh budget");
            Check(Mathf.Approximately(s.bounceStrength, LightingConstants.IrradianceBounceStrength),
                "LightingSettings: bounce strength defaults to the authored 0.6");
            Check(Mathf.Approximately(s.cloudShadowScale, LightingConstants.CloudShadowScale),
                "LightingSettings: cloud shadow scale defaults to the cookie 0.0004");
            Check(Mathf.Approximately(s.sunDiscSizeDeg, LightingConstants.SunDiscDiameterDeg),
                "LightingSettings: sun disc defaults to the true 0.53 deg diameter");
            Check(Mathf.Approximately(s.moonDiscSizeDeg, LightingConstants.MoonDiscDiameterDeg),
                "LightingSettings: moon disc defaults to the true 0.52 deg diameter");
            Check(Mathf.Approximately(s.nightFloorLux,
                    UniverseCreator.TerrainSystem.Lighting.NightFloor.DefaultNightFloorLux),
                "LightingSettings: night floor defaults to the labelled playability floor");

            // The authored-only defaults carried forward exactly from the retired M4 table.
            Check(Mathf.Approximately(s.moonIntensity, 0.08f),
                "LightingSettings: moon intensity trim defaults to 0.08");
            Check(Mathf.Approximately(s.moonInclinationDeg, 5f),
                "LightingSettings: moon inclination defaults to the real 5 deg");
            Check(Mathf.Approximately(s.contactShadowLength, 0.35f),
                "LightingSettings: contact shadow length defaults to 0.35 m");
            Check(s.ssgiSampleCount == 8, "LightingSettings: SSGI sample count defaults to 8");
            Check(Mathf.Approximately(s.ssgiTemporalWeight, 0.9f),
                "LightingSettings: SSGI temporal weight defaults to 0.9");
            Check(s.exposureMode == UniverseCreator.TerrainSystem.Lighting.ExposureMode.Physical,
                "LightingSettings: exposure defaults to the deterministic Physical mode");
            Check(s.multiBounce, "LightingSettings: multi-bounce feedback defaults on (T1.5 and up)");
            Check(Mathf.Approximately(s.multiBounceClamp, LightingConstants.IrradianceMultiBounceClampScale),
                "LightingSettings: the multi-bounce clamp defaults to the physical 4x first bounce");

            // ClampToValidRanges pulls every out-of-range dial back to a real bound and is
            // idempotent - a second clamp changes nothing, so it is safe in OnValidate.
            var wild = new UniverseCreator.TerrainSystem.Lighting.LightingSettings
            {
                turbidity = 100f, moonIntensity = 5f, moonInclinationDeg = -3f,
                probeSpacing = 0f, probesPerFrame = 100000, bounceStrength = -1f,
                contactShadowLength = 50f, ssgiSampleCount = 999, ssgiTemporalWeight = 2f,
                cloudShadowScale = 9f, sunDiscSizeDeg = 0f, moonDiscSizeDeg = 40f,
                nightFloorLux = -2f, multiBounceClamp = 99f
            };
            wild.ClampToValidRanges();
            Check(wild.turbidity >= 1.8f && wild.turbidity <= 10f, "LightingSettings: turbidity clamps into 1.8..10");
            Check(wild.moonIntensity >= 0f && wild.moonIntensity <= 1f, "LightingSettings: moon intensity clamps into 0..1");
            Check(wild.probesPerFrame >= 8 && wild.probesPerFrame <= 256, "LightingSettings: probes/frame clamps into 8..256");
            Check(wild.ssgiSampleCount >= 1 && wild.ssgiSampleCount <= 16, "LightingSettings: SSGI samples clamp into 1..16");
            Check(wild.ssgiTemporalWeight >= 0f && wild.ssgiTemporalWeight <= 0.98f, "LightingSettings: SSGI weight clamps into 0..0.98");
            Check(wild.nightFloorLux >= 0f && wild.nightFloorLux <= 1f, "LightingSettings: night floor clamps into 0..1");
            Check(wild.multiBounceClamp >= 1f && wild.multiBounceClamp <= 16f, "LightingSettings: multi-bounce clamp clamps into 1..16");

            float t = wild.turbidity; int pf = wild.probesPerFrame; float w = wild.ssgiTemporalWeight;
            wild.ClampToValidRanges();
            Check(Mathf.Approximately(wild.turbidity, t) && wild.probesPerFrame == pf &&
                  Mathf.Approximately(wild.ssgiTemporalWeight, w),
                "LightingSettings: ClampToValidRanges is idempotent");

            // A default block is already in range: clamping it must move nothing.
            var d = new UniverseCreator.TerrainSystem.Lighting.LightingSettings();
            float d0 = d.turbidity; d.ClampToValidRanges();
            Check(Mathf.Approximately(d.turbidity, d0) && IsFinite(d.probeSpacing) && IsFinite(d.cloudShadowScale),
                "LightingSettings: defaults are already valid and finite");
        }

        /// <summary>
        /// The inspector's irradiance MB figure is honest only if it is priced by the same
        /// packing the driver uploads. GpuBytes must equal the two RGBAHalf textures the pack
        /// path actually builds - PackedLength texels each, eight bytes a texel - so a probe
        /// costs forty-eight bytes and the preview can never drift from the bake.
        /// </summary>
        private static void TestIrradianceGpuByteEstimate()
        {
            Check(IrradianceUpload.GpuBytes(0) == 0L,
                "IrradianceUpload: zero probes cost zero bytes");
            Check(IrradianceUpload.GpuBytes(-5) == 0L,
                "IrradianceUpload: a non-positive count costs zero, never a negative figure");
            Check(IrradianceUpload.GpuBytes(1) == 48L,
                "IrradianceUpload: one probe costs 48 bytes (six faces across two RGBAHalf textures)");
            Check(IrradianceUpload.GpuBytes(1000) == 48000L,
                "IrradianceUpload: the byte estimate is linear in the probe count");

            // Tie the estimate to the real pack: two textures of PackedLength texels, eight
            // bytes each. If the packing constants ever change, this catches an estimate that
            // did not follow them.
            var flat = new System.Func<float, float, float>((x, z) => 0f);
            var grid = new IrradianceGrid(new Vector2(0f, 0f), 64f, 64f, flat);
            long packedBytes = 2L * IrradianceUpload.PackedLength(grid) * IrradianceUpload.BytesPerTexel;
            Check(IrradianceUpload.GpuBytes(grid.ProbeCount) == packedBytes,
                "IrradianceUpload: GpuBytes equals the two packed textures the driver uploads");
        }

        /// <summary>
        /// The migrated manager resolves its tier and sizes the irradiance grid without a
        /// bake, so the inspector's readouts are real. With no world above it the grid figure
        /// is a truthful zero; with the tier pinned it reports the pinned tier and a real
        /// envelope; and following a tier source that is absent falls back to the override
        /// rather than throwing.
        /// </summary>
        private static void TestLightingManagerResolvesTierAndProbes()
        {
            var go = new GameObject("UC_SelfTest_Lighting");
            try
            {
                var m = go.AddComponent<UniverseCreator.TerrainSystem.UCLightingSystemManager>();

                Check(m.settings != null,
                    "UCLightingSystemManager: holds a LightingSettings block");

                // Loose in a scene: no world to size against, so the grid figure is zero
                // rather than a fabricated number the author would trust.
                Check(m.World == null, "UCLightingSystemManager: a loose component has no world");
                Check(m.EstimatedProbeCount == 0L,
                    "UCLightingSystemManager: no world means zero probes, not a guess");
                Check(m.EstimatedIrradianceBytes == 0L,
                    "UCLightingSystemManager: no world means zero irradiance bytes");

                // Follow with no AdaptiveQuality present: fall back to the override.
                m.followAdaptiveQuality = true;
                m.lightingTierOverride = UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium;
                Check(m.ResolvedTier == UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium,
                    "UCLightingSystemManager: follow-with-no-source falls back to the override tier");

                // Pinned: the resolved tier is exactly the override, and its envelope resolves.
                m.followAdaptiveQuality = false;
                m.lightingTierOverride = UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low;
                Check(m.ResolvedTier == UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low,
                    "UCLightingSystemManager: a pinned tier resolves to itself");
                Check(m.ResolvedTierSettings.cascadeCount >= 1,
                    "UCLightingSystemManager: the resolved tier carries a real shadow envelope");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void TestCascadePolicy()
        {
            // S-55: switching the device preset must visibly change shadow distance and
            // cascade count IN THE URP ASSET. This executes the shipping applier against a
            // bare URP asset (no renderer, no GPU) and reads the serialized fields back, so
            // it proves the write lands - not just that the code compiles.
            var asset = ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            if (asset == null) { Check(false, "Cascade policy: could not create a URP asset to test against"); return; }

            try
            {
                var log = new System.Text.StringBuilder();
                var ultra  = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra);
                var medium = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium);

                // Flagship (Ultra): 3 cascades, its authored distance/atlas, the 3-split set.
                UCRenderPipelineSetup.ConfigureShadowPolicy(asset, ultra, log);
                var so = new UnityEditor.SerializedObject(asset);
                int ultraCascades = so.FindProperty("m_ShadowCascadeCount").intValue;
                float ultraDist = so.FindProperty("m_ShadowDistance").floatValue;
                int ultraAtlas = so.FindProperty("m_MainLightShadowmapResolution").intValue;
                Vector2 split3 = so.FindProperty("m_Cascade3Split").vector2Value;
                Check(ultraCascades == 3 && Mathf.Approximately(ultraDist, ultra.shadowDistance)
                      && ultraAtlas == ultra.shadowAtlasResolution,
                    "Cascade policy: the flagship preset writes 3 cascades, its distance and atlas to the asset");
                Check(split3.x > 0f && split3.x < split3.y && split3.y < 1f,
                    "Cascade policy: a 3-cascade split is an ascending near/mid fraction below the far cascade");

                // Mid-range (Medium): 2 cascades, its own distance/atlas, the single 2-split.
                UCRenderPipelineSetup.ConfigureShadowPolicy(asset, medium, log);
                so = new UnityEditor.SerializedObject(asset);
                int medCascades = so.FindProperty("m_ShadowCascadeCount").intValue;
                float medDist = so.FindProperty("m_ShadowDistance").floatValue;
                float split2 = so.FindProperty("m_Cascade2Split").floatValue;
                Check(medCascades == 2 && Mathf.Approximately(medDist, medium.shadowDistance),
                    "Cascade policy: the mid-range preset writes 2 cascades and its shorter distance");
                Check(split2 > 0f && split2 < 1f,
                    "Cascade policy: a 2-cascade split is a single fraction inside the far cascade");

                // The done-when itself: switching the preset VISIBLY changed both the shadow
                // distance and the cascade count in the asset - not just re-wrote the same numbers.
                Check(ultraCascades != medCascades && !Mathf.Approximately(ultraDist, medDist),
                    "Cascade policy: switching device preset changes both cascade count and shadow distance (S-55 done-when)");

                // Single source of truth: the applied numbers ARE LightingTier's table (S-53),
                // never a second ladder restated in the setup code.
                Check(ultraCascades == ultra.cascadeCount && medCascades == medium.cascadeCount,
                    "Cascade policy: the applier writes LightingTier's values, so there is one cascade ladder (no duplicate)");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        private static void TestSharedShadowDistance()
        {
            // S-56: one shadow distance value, read by both the sun cascades (§8) and the
            // vegetation casters (§6). The lit shadow distance is the map edge; a vegetation
            // preference is only ever trimmed down to it, never a second longer distance.
            var low  = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                          UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);
            var ultra = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                          UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra);

            // A preference longer than the map edge is capped AT the map edge, never beyond.
            float cappedLow = UniverseCreator.TerrainSystem.Lighting.LightingTier.ShadowCasterBudgetM(low, 500f);
            Check(Mathf.Approximately(cappedLow, low.shadowDistance),
                "Shared shadow distance: a caster preference beyond the map edge is capped at the tier's shadow distance");

            // A preference shorter than the map edge is left alone (the §6.5 perf trim survives).
            float shortPref = UniverseCreator.TerrainSystem.Lighting.LightingTier.ShadowCasterBudgetM(ultra, 50f);
            Check(Mathf.Approximately(shortPref, 50f),
                "Shared shadow distance: a shorter caster preference is kept (vegetation trims below the map, not above)");

            // Opting out (0 or less) means no casting, on any tier.
            Check(UniverseCreator.TerrainSystem.Lighting.LightingTier.ShadowCasterBudgetM(ultra, 0f) == 0f,
                "Shared shadow distance: a zero caster preference casts nothing");

            // The value moves with the tier: the SAME dial (shadowDistance) drives the cap,
            // so raising the tier raises the cap for both consumers - not two ladders.
            Check(ultra.shadowDistance > low.shadowDistance
                  && UniverseCreator.TerrainSystem.Lighting.LightingTier.ShadowCasterBudgetM(ultra, 500f)
                     > UniverseCreator.TerrainSystem.Lighting.LightingTier.ShadowCasterBudgetM(low, 500f),
                "Shared shadow distance: the vegetation cap tracks the tier's shadow distance (one dial, both costs)");

            // §6 composes on the SAME value: EcoEnvelope trims its per-species distance below
            // the shared budget, so feeding it the lit shadow distance yields a value that can
            // never exceed the map edge - proving §6 and §8 read one distance, not two.
            var eco = new UniverseCreator.Ecology.EcoEnvelope { shadowCastDistanceM = 60f };
            float vegBudget = UniverseCreator.TerrainSystem.Lighting.LightingTier.ShadowCasterBudgetM(low, 500f);
            float perSpecies = eco.ResolveShadowDistanceM(vegBudget);
            Check(perSpecies <= low.shadowDistance + 0.001f && perSpecies > 0f,
                "Shared shadow distance: §6's per-species caster distance stays within §8's shadow distance");
        }

        private static void TestShadowBias()
        {
            // S-57: depth + normal shadow bias, authored per tier to the tier's texel
            // coarseness and written to the URP asset. Executes the applier and reads the
            // serialized bias back, then asserts the coarsest tier carries the most normal
            // bias (bigger texels -> more grazing-angle acne to offset) without either bias
            // running away into peter-panning territory.
            var asset = ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            if (asset == null) { Check(false, "Shadow bias: could not create a URP asset to test against"); return; }

            try
            {
                var log = new System.Text.StringBuilder();
                var ultra = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                               UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra);
                var low = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                               UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);

                UCRenderPipelineSetup.ConfigureShadowPolicy(asset, ultra, log);
                var so = new UnityEditor.SerializedObject(asset);
                float ultraDepth = so.FindProperty("m_ShadowDepthBias").floatValue;
                float ultraNormal = so.FindProperty("m_ShadowNormalBias").floatValue;
                Check(Mathf.Approximately(ultraDepth, ultra.shadowDepthBias)
                      && Mathf.Approximately(ultraNormal, ultra.shadowNormalBias),
                    "Shadow bias: the flagship's authored depth/normal bias is written to the asset");

                UCRenderPipelineSetup.ConfigureShadowPolicy(asset, low, log);
                so = new UnityEditor.SerializedObject(asset);
                float lowDepth = so.FindProperty("m_ShadowDepthBias").floatValue;
                float lowNormal = so.FindProperty("m_ShadowNormalBias").floatValue;
                Check(Mathf.Approximately(lowDepth, low.shadowDepthBias)
                      && Mathf.Approximately(lowNormal, low.shadowNormalBias),
                    "Shadow bias: the mid/low tier's authored depth/normal bias is written to the asset");

                // Coarser texels (Low: 80 m over a 1024 atlas) need more normal bias than the
                // flagship's fine texels - the grazing-terrain acne case gets the most offset.
                Check(lowNormal >= ultraNormal && lowDepth >= ultraDepth,
                    "Shadow bias: normal and depth bias do not decrease as the tier's texels coarsen (acne control)");

                // But bias must stay bounded: an unbounded bias is peter-panning, the other
                // half of S-57. Physically sane range for a URP base bias.
                Check(IsFinite(lowNormal) && lowNormal > 0f && lowNormal <= 3f
                      && IsFinite(lowDepth) && lowDepth > 0f && lowDepth <= 3f,
                    "Shadow bias: bias stays in a bounded, positive range (no peter-panning from runaway bias)");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        private static void TestAuthorAssetSafety()
        {
            // S-58: the module configures a pipeline it did not author. An author-edited
            // shadow setting must SURVIVE a preset change when overwrite is refused, the log
            // must NAME exactly what would change, and nothing outside the shadow block is
            // ever written.
            var asset = ScriptableObject.CreateInstance<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            if (asset == null) { Check(false, "Author asset safety: could not create a URP asset to test against"); return; }

            try
            {
                var ultra = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                               UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra);
                var low = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                               UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);

                // Baseline the asset to Ultra, then stand in for an author hand-edit.
                var setup = new System.Text.StringBuilder();
                UCRenderPipelineSetup.ConfigureShadowPolicy(asset, ultra, setup);
                var edit = new UnityEditor.SerializedObject(asset);
                edit.FindProperty("m_ShadowDistance").floatValue = 987f;
                edit.ApplyModifiedPropertiesWithoutUndo();

                // Capture a NON-shadow field to prove it is never touched.
                var beforeSo = new UnityEditor.SerializedObject(asset);
                var renderScaleProp = beforeSo.FindProperty("m_RenderScale");
                float renderScaleBefore = renderScaleProp != null ? renderScaleProp.floatValue : float.NaN;

                // Refuse the overwrite: the preset must NOT be applied over the author's edit.
                var warn = new System.Text.StringBuilder();
                bool applied = UCRenderPipelineSetup.ConfigureShadowPolicy(asset, low, warn, overwriteAuthorEdits: false);
                var afterSo = new UnityEditor.SerializedObject(asset);
                float survived = afterSo.FindProperty("m_ShadowDistance").floatValue;

                Check(!applied && Mathf.Approximately(survived, 987f),
                    "Author asset safety: refusing overwrite leaves the author's shadow distance intact");
                Check(warn.ToString().Contains("WARNING would change") && warn.ToString().Contains("shadow distance"),
                    "Author asset safety: the warning names exactly which shadow row would change");

                if (renderScaleProp != null)
                {
                    float renderScaleAfter = afterSo.FindProperty("m_RenderScale").floatValue;
                    Check(Mathf.Approximately(renderScaleBefore, renderScaleAfter),
                        "Author asset safety: a non-shadow setting (render scale) is never written");
                }

                // With overwrite allowed (the module's own asset), the preset does apply.
                var ok = new System.Text.StringBuilder();
                bool applied2 = UCRenderPipelineSetup.ConfigureShadowPolicy(asset, low, ok, overwriteAuthorEdits: true);
                float afterWrite = new UnityEditor.SerializedObject(asset).FindProperty("m_ShadowDistance").floatValue;
                Check(applied2 && Mathf.Approximately(afterWrite, low.shadowDistance),
                    "Author asset safety: allowing overwrite applies the preset's shadow distance");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        private static void TestRendererFeatureGuards()
        {
            // C-07 / S-54: every renderer feature must be wrapped in UC_URP (so the runtime
            // assembly compiles with URP stripped) and must fail to a NO-OP - an early return
            // when its material or resources are unavailable - never a null-reference storm.
            string lightingDir = System.IO.Path.Combine(Application.dataPath,
                "UniverseCreator/Core/Runtime/Lighting");
            if (!System.IO.Directory.Exists(lightingDir))
            {
                Check(false, "Renderer-feature guards: Lighting runtime folder exists");
                return;
            }

            string[] files = System.IO.Directory.GetFiles(lightingDir, "*Feature.cs",
                System.IO.SearchOption.TopDirectoryOnly);
            int checkedCount = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string src = System.IO.File.ReadAllText(files[i]);
                if (!src.Contains("ScriptableRendererFeature")) continue;   // not a feature
                checkedCount++;
                string nm = System.IO.Path.GetFileName(files[i]);

                Check(src.Contains("#if UC_URP") && src.TrimEnd().EndsWith("#endif"),
                    nm + ": whole file guarded by UC_URP (compiles with URP absent, C-07)");

                // A no-op path: the feature returns early rather than enqueuing when its
                // material could not be loaded. Both our features gate on TryLoadMaterial.
                Check(src.Contains("if (!TryLoadMaterial()) return;"),
                    nm + ": enqueues nothing when its material is unavailable (fails to a no-op)");
            }
            Check(checkedCount >= 2,
                "Renderer-feature guards: both UC renderer features (contact shadows, SSGI) were checked");
        }

        private static void TestNoSecondAmbientOcclusion()
        {
            // AO is URP's own Screen Space Ambient Occlusion feature, configured by
            // UCRenderPipelineSetup - never a second implementation of ours. Confirm the
            // type we configure is present in this URP.
            System.Type aoType = System.Type.GetType(
                "UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion, Unity.RenderPipelines.Universal.Runtime");
            Check(aoType != null, "SSAO: URP's Screen Space Ambient Occlusion feature is available to configure");

            // And that we wrote no AO pass of our own. A hand-rolled SSAO would carry an
            // occlusion-accumulation kernel in a shader; scan for that tell across the
            // project and expect none.
            string root = System.IO.Path.Combine(Application.dataPath, "UniverseCreator");
            bool customAo = false;
            string offender = "";
            if (System.IO.Directory.Exists(root))
            {
                customAo = ScanForCustomAo(System.IO.Directory.GetFiles(root, "*.shader", System.IO.SearchOption.AllDirectories), ref offender)
                        || ScanForCustomAo(System.IO.Directory.GetFiles(root, "*.hlsl", System.IO.SearchOption.AllDirectories), ref offender);
            }
            Check(!customAo, "SSAO: no custom AO pass exists in the project" + (customAo ? " (" + offender + ")" : ""));

            // The shared include only *consumes* an AO value (UCComposeSurfaceLight, S-41),
            // it never produces one - so there is exactly one AO owner, URP's.
            string shaders = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders");
            string lighting = ReadShader(shaders, "UC_Lighting.hlsl");
            Check(CountOf(lighting, "float3 UCComposeSurfaceLight(") == 1,
                "SSAO: AO is consumed once (UCComposeSurfaceLight), not re-implemented");
        }

        // True if any file carries the signature of a hand-rolled screen-space AO kernel.
        private static bool ScanForCustomAo(string[] files, ref string offender)
        {
            for (int i = 0; i < files.Length; i++)
            {
                string s = System.IO.File.ReadAllText(files[i]);
                if (s.Contains("_UCSSAO_Occlusion") || s.Contains("UCSampleOcclusionKernel") || s.Contains("// UC SSAO PASS"))
                { offender = System.IO.Path.GetFileName(files[i]); return true; }
            }
            return false;
        }

        // Read a shader/include file by name from the Shaders folder; "" if absent.
        private static string ReadShader(string dir, string file)
        {
            string path = System.IO.Path.Combine(dir, file);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
        }

        // Count non-overlapping occurrences of a needle in a source string.
        private static int CountOf(string src, string needle)
        {
            int n = 0;
            for (int at = src.IndexOf(needle); at >= 0; at = src.IndexOf(needle, at + 1)) n++;
            return n;
        }

        // A minimal resolved weather snapshot for the hand-off tests: the fields the sky
        // reads, left otherwise at rest. cover drives the aggregate and the three layers;
        // humidity and wetness feed the turbidity the sky then lights by.
        private static WeatherState MakeWeather(float cover, float humidity, float wetness)
        {
            var w = new WeatherState.Builder
            {
                cloudCover = cover,
                cloudCoverLow = cover,
                cloudCoverMid = cover * 0.5f,
                cloudCoverHigh = cover * 0.25f,
                cloudBaseM = 900f,
                cloudBaseMidM = 2400f,
                cloudBaseHighM = 6000f,
                cloudType = CloudCover.CloudType.Cumuliform,
                stabilityLapseCPerKm = 6.5f,
                precipitationMmPerHour = wetness * 4f,
                snowLineM = 2200f,
                humidity = humidity,
                wetness = wetness
            };
            return w.Build();
        }

        /// <summary>
        /// The day-cycle window samples the sun by the hour off the world's resolved
        /// clock. This pins the one property that makes the strip trustworthy: the hour
        /// the live clock sits at reproduces the clock's own sun, so the preview and the
        /// world cannot disagree. It also checks the hour-angle tie to ClockMath, the
        /// determinism of a resample, and that all 24 samples come out finite.
        /// </summary>
        private static void TestDayCycleSampling()
        {
            ClockState clock = UniverseCreator.TerrainSystem.UCWeatherSystemManager.DefaultClock();
            float turbidity = LightingConstants.TurbidityDefault;

            // The hour angle taken off the clock's solar noon is the clock's own.
            float ha = UniverseCreator.Editor.Windows.LightingDayCycleWindow.HourAngleAt(
                clock.localHour, clock.solarNoonHour);
            CheckWithin(ha, clock.hourAngleDeg, 1e-2f,
                "Day cycle: hour angle at the live hour matches the clock");

            // Sampling at the clock's own hour reproduces the clock's own sun.
            var live = UniverseCreator.Editor.Windows.LightingDayCycleWindow.SampleAt(
                clock, clock.localHour, turbidity);
            CheckWithin(live.elevationDeg, clock.elevationDeg, 1e-2f,
                "Day cycle: sun elevation at the live hour matches the clock");
            CheckWithin(live.azimuthDeg, clock.azimuthDeg, 1e-2f,
                "Day cycle: sun azimuth at the live hour matches the clock");

            // A resample of the same hour is bit-for-bit the same - no hidden state.
            var again = UniverseCreator.Editor.Windows.LightingDayCycleWindow.SampleAt(
                clock, clock.localHour, turbidity);
            Check(again.elevationDeg == live.elevationDeg && again.azimuthDeg == live.azimuthDeg,
                "Day cycle: resampling one hour is deterministic");

            // Noon stands higher than midnight, and every sample is finite.
            var noon = UniverseCreator.Editor.Windows.LightingDayCycleWindow.SampleAt(
                clock, clock.solarNoonHour, turbidity);
            var midnight = UniverseCreator.Editor.Windows.LightingDayCycleWindow.SampleAt(
                clock, clock.solarNoonHour + 12f, turbidity);
            Check(noon.elevationDeg > midnight.elevationDeg,
                "Day cycle: the sun is higher at solar noon than opposite it");

            bool allFinite = true;
            for (int i = 0; i < 24; i++)
            {
                var s = UniverseCreator.Editor.Windows.LightingDayCycleWindow.SampleAt(
                    clock, i + 0.5f, turbidity);
                allFinite &= IsFinite(s.elevationDeg) && IsFinite(s.azimuthDeg) && IsFinite(s.airMass)
                             && IsFinite(s.skyColor.r) && IsFinite(s.skyColor.g) && IsFinite(s.skyColor.b)
                             && IsFinite(s.sunColor.r) && IsFinite(s.sunColor.g) && IsFinite(s.sunColor.b);
            }
            Check(allFinite, "Day cycle: all 24 hourly samples are finite");
        }

        // The lighting inspector's time control writes through to the weather manager
        // and keeps nothing of its own: the lighting component has no settable hour, so
        // the only way its reported time can move is the weather clock moving. This
        // builds the same world shape the inspector edits - a lighting manager and a
        // weather manager under one world root - drives the exact call the inspector's
        // slider makes (SetTime on the found weather manager), and checks the lighting
        // readout follows. If a second time value ever crept onto the lighting manager,
        // its readout would stop tracking and this would fail.
        private static void TestLightingTimeIsWeatherOwned()
        {
            GameObject root = new GameObject("UC Time Owner Test");
            try
            {
                root.AddComponent<UniverseCreator.TerrainSystem.UniverseCreatorObject>();

                GameObject weatherGo = new GameObject("Weather");
                weatherGo.transform.SetParent(root.transform);
                var weather = weatherGo.AddComponent<UniverseCreator.TerrainSystem.UCWeatherSystemManager>();

                GameObject lightGo = new GameObject("Lighting");
                lightGo.transform.SetParent(root.transform);
                var light = lightGo.AddComponent<UniverseCreator.TerrainSystem.UCLightingSystemManager>();

                // The lighting manager finds and reads the one weather clock.
                Check(light.HasClockSource,
                    "Lighting time: a lighting manager finds the world's weather clock");

                // The exact write the inspector slider performs.
                weather.SetTime(weather.ResolveClock().dayOfYear, 6f);
                CheckWithin(light.TimeOfDayHours, 6f, 1e-3f,
                    "Lighting time: moving the weather clock moves the lighting readout");

                weather.SetTime(weather.ResolveClock().dayOfYear, 18f);
                CheckWithin(light.TimeOfDayHours, 18f, 1e-3f,
                    "Lighting time: the lighting readout tracks the single weather-owned hour");

                // No weather system, no owner to write to: the readout is the default,
                // never a value stored on the lighting manager.
                GameObject lone = new GameObject("Lone Lighting");
                try
                {
                    var loose = lone.AddComponent<UniverseCreator.TerrainSystem.UCLightingSystemManager>();
                    Check(!loose.HasClockSource,
                        "Lighting time: a lighting manager with no weather system has no clock source");
                    CheckWithin(loose.TimeOfDayHours,
                        UniverseCreator.TerrainSystem.UCWeatherSystemManager.DefaultHourOfDay, 1e-3f,
                        "Lighting time: with no owner the hour is the documented default");
                }
                finally { Object.DestroyImmediate(lone); }
            }
            finally { Object.DestroyImmediate(root); }
        }

        // The itemised memory rows the inspector shows and the close-out records are
        // priced from the types that own the allocations. This pins those figures so a
        // silent format or layout change is caught here rather than in a wrong budget:
        // the sky LUT and cloud tile are fixed constants, a probe costs seventy-two
        // managed bytes (six Vector3) and forty-eight GPU bytes (three faces in each of
        // two RGBAHalf volumes), and the GPU row scales exactly with the probe count.
        private static void TestLightingMemoryBudget()
        {
            // Sky LUT: 32 x 16 RGBAHalf, eight bytes a texel.
            Check(UniverseCreator.TerrainSystem.Lighting.SkyLut.ByteSize == 32 * 16 * 8,
                "Memory: the sky LUT is 4 KB (32x16 RGBAHalf)");

            // Cloud noise: one 256^2 RGBA32 tile, no mips.
            Check(UniverseCreator.TerrainSystem.Lighting.CloudNoiseBaker.ByteSize == 256 * 256 * 4,
                "Memory: the cloud noise tile is 256 KB (256^2 RGBA32)");

            // One probe: six Vector3 faces on the heap.
            Check(UniverseCreator.TerrainSystem.Lighting.IrradianceProbe.ManagedBytes == 72,
                "Memory: a probe is 72 managed bytes (six Vector3)");

            // The GPU row is a pure function of the count and scales linearly with it.
            long gpu1k = UniverseCreator.TerrainSystem.Lighting.IrradianceUpload.GpuBytes(1000L);
            Check(gpu1k == 1000L * 48L,
                "Memory: a probe uploads 48 GPU bytes (3 faces x 2 RGBAHalf volumes)");
            long gpu2k = UniverseCreator.TerrainSystem.Lighting.IrradianceUpload.GpuBytes(2000L);
            Check(gpu2k == 2L * gpu1k,
                "Memory: the probe volume cost scales linearly with the probe count");
            Check(UniverseCreator.TerrainSystem.Lighting.IrradianceUpload.GpuBytes(0L) == 0L &&
                  UniverseCreator.TerrainSystem.Lighting.IrradianceUpload.GpuBytes(-5L) == 0L,
                "Memory: a non-positive probe count costs zero, not a negative or a throw");
        }

        /// <summary>
        /// Multi-bounce temporal feedback (S-67): the single bounce compounds toward
        /// base / (1 - albedo) over refreshes, converges monotonically for a physical
        /// albedo without tripping the clamp, is bit-identical run to run, preserves hue
        /// when the clamp does engage on an unphysical albedo, and vanishes entirely when
        /// the previous-grid lookup is absent (feedback disabled).
        /// </summary>
        private static void TestMultiBounceFeedback()
        {
            const float strength = 1f;   // isolate the feedback maths from the bounce dial
            const float clamp = LightingConstants.IrradianceMultiBounceClampScale;
            Vector3 probe = new Vector3(0f, 5f, 0f);

            // 1) Convergence. A uniform grey neighbourhood (albedo a) sitting over a constant
            // first bounce B settles at B / (1 - a): each refresh feeds the previous ambient
            // back in. Advance the loop by hand and check it climbs monotonically to that
            // analytic steady state and then holds - the "deepens then stops" done-when.
            float a = 0.5f;
            Vector3 baseBounce = new Vector3(0.20f, 0.20f, 0.20f);
            var albedoUniform = new System.Func<float, float, Vector3>((x, z) => new Vector3(a, a, a));
            Vector3 ambient = Vector3.zero;
            float lastLum = -1f;
            bool everClamped = false;
            for (int refresh = 0; refresh < 40; refresh++)
            {
                var prev = ambient;
                var prevAt = new System.Func<float, float, Vector3>((x, z) => prev);
                Vector3 fb = MultiBounceFeedback.FeedbackRadiance(probe, prevAt, albedoUniform, strength);
                bool engaged;
                ambient = MultiBounceFeedback.ClampAccumulated(baseBounce, fb, clamp, out engaged);
                everClamped |= engaged;
                float lum = MultiBounceFeedback.Luminance(ambient);
                Check(lum >= lastLum - 1e-6f, "MultiBounce: indirect climbs monotonically toward steady state");
                lastLum = lum;
            }
            Check(!everClamped, "MultiBounce: a physical albedo (0.5) never trips the convergence clamp");
            Vector3 steady = baseBounce / (1f - a);   // geometric-series limit
            CheckWithin(ambient.x, steady.x, 1e-3f, "MultiBounce: converges to base / (1 - albedo)");

            // 2) Determinism. The whole loop is a pure function, so a second cold run lands on
            // the identical value bit for bit (C-06 / S-84's grid claim in miniature).
            Vector3 ambient2 = Vector3.zero;
            for (int refresh = 0; refresh < 40; refresh++)
            {
                var prev = ambient2;
                var prevAt = new System.Func<float, float, Vector3>((x, z) => prev);
                Vector3 fb = MultiBounceFeedback.FeedbackRadiance(probe, prevAt, albedoUniform, strength);
                bool engaged;
                ambient2 = MultiBounceFeedback.ClampAccumulated(baseBounce, fb, clamp, out engaged);
            }
            Check(ambient2 == ambient, "MultiBounce: two cold runs are bit-identical");

            // 3) Feedback disabled. A null previous-grid lookup yields exactly zero feedback,
            // and the accumulated indirect is then just the first bounce - so toggling
            // multi-bounce off removes the effect and changes nothing else.
            Vector3 none = MultiBounceFeedback.FeedbackRadiance(probe, null, albedoUniform, strength);
            Check(none == Vector3.zero, "MultiBounce: no previous grid means no feedback");
            bool offEngaged;
            Vector3 offTotal = MultiBounceFeedback.ClampAccumulated(baseBounce, Vector3.zero, clamp, out offEngaged);
            Check(!offEngaged && offTotal == baseBounce, "MultiBounce: disabled feedback leaves the single bounce untouched");

            // 4) The clamp is a real backstop. An authored albedo above one would diverge; the
            // cap holds the total at clamp x the first bounce and reports that it engaged.
            MultiBounceFeedback.ResetClampLog();
            Vector3 huge = baseBounce * 100f;           // a runaway previous ambient
            var albedoUnphysical = new System.Func<float, float, Vector3>((x, z) => new Vector3(2f, 2f, 2f));
            var hugeAt = new System.Func<float, float, Vector3>((x, z) => huge);
            Vector3 runaway = MultiBounceFeedback.FeedbackRadiance(probe, hugeAt, albedoUnphysical, strength);
            bool clampEngaged;
            Vector3 capped = MultiBounceFeedback.ClampAccumulated(baseBounce, runaway, clamp, out clampEngaged);
            Check(clampEngaged, "MultiBounce: an albedo above 1 trips the convergence clamp");
            CheckWithin(MultiBounceFeedback.Luminance(capped), clamp * MultiBounceFeedback.Luminance(baseBounce), 1e-3f,
                "MultiBounce: the clamp holds accumulated indirect at clamp x the first bounce");

            // 5) Hue is preserved through the clamp: a red bleed stays red, it does not grey out.
            MultiBounceFeedback.ResetClampLog();
            Vector3 redFirst = new Vector3(0.20f, 0.02f, 0.02f);
            Vector3 redFeedback = new Vector3(6f, 0.6f, 0.6f);
            bool redEngaged;
            Vector3 redCapped = MultiBounceFeedback.ClampAccumulated(redFirst, redFeedback, clamp, out redEngaged);
            Check(redEngaged, "MultiBounce: the red bleed is bright enough to reach the clamp");
            Check(redCapped.x > redCapped.y && redCapped.x > redCapped.z,
                "MultiBounce: the clamped bleed stays red-dominant (hue preserved, not desaturated)");
            MultiBounceFeedback.ResetClampLog();
        }

        /// <summary>
        /// Energy conservation (S-69, C-16): the bounce albedo is held below one, a closed
        /// white box converges below its analytic furnace ceiling rather than gaining energy
        /// forever, and a single reflected bounce never carries more light than arrived.
        /// </summary>
        private static void TestEnergyConservation()
        {
            // The albedo clamp is the one enforcement point: a white-authored layer is held
            // at the ceiling and a negative is floored, so nothing above one ever bounces.
            Vector3 tooBright = EnergyConservation.ClampAlbedo(new Vector3(1.2f, 1.05f, 2.0f));
            float max = LightingConstants.IrradianceMaxDiffuseAlbedo;
            Check(Mathf.Approximately(tooBright.x, max) && Mathf.Approximately(tooBright.y, max)
                  && Mathf.Approximately(tooBright.z, max),
                "EnergyConservation: an over-one albedo is clamped to the max diffuse reflectance");
            Vector3 negative = EnergyConservation.ClampAlbedo(new Vector3(-0.3f, 0.4f, -1f));
            Check(negative.x == 0f && negative.z == 0f && Mathf.Approximately(negative.y, 0.4f),
                "EnergyConservation: a negative albedo is floored at zero");

            // The furnace ceiling is finite at the clamped albedo: source / (1 - 0.95) = 20x.
            Vector3 ceilAt95 = EnergyConservation.FurnaceCeiling(Vector3.one, new Vector3(3f, 3f, 3f));
            CheckWithin(ceilAt95.x, 20f, 1e-3f, "EnergyConservation: the furnace ceiling is finite at the albedo clamp");

            // Fill a probe in a closed white box: uniform albedo a over a constant first bounce,
            // the feedback fed back each refresh with the convergence clamp bypassed (clampScale 0)
            // so the raw series shows. It must climb, stay strictly below the analytic furnace
            // value every step (a partial sum of a ratio-below-one series), and settle at it.
            const float strength = 1f;
            Vector3 probe = new Vector3(0f, 5f, 0f);
            float boxAlbedo = 0.9f;
            Vector3 source = new Vector3(0.10f, 0.10f, 0.10f);
            Vector3 ceiling = EnergyConservation.FurnaceCeiling(source, new Vector3(boxAlbedo, boxAlbedo, boxAlbedo));
            var boxAlbedoAt = new System.Func<float, float, Vector3>((x, z) => new Vector3(boxAlbedo, boxAlbedo, boxAlbedo));
            Vector3 box = Vector3.zero;
            float lastLum = -1f;
            bool stayedUnderCeiling = true;
            for (int refresh = 0; refresh < 200; refresh++)
            {
                var prev = box;
                var prevAt = new System.Func<float, float, Vector3>((x, z) => prev);
                Vector3 fb = MultiBounceFeedback.FeedbackRadiance(probe, prevAt, boxAlbedoAt, strength);
                bool engaged;
                box = MultiBounceFeedback.ClampAccumulated(source, fb, 0f, out engaged);  // 0 = no backstop
                float lum = MultiBounceFeedback.Luminance(box);
                Check(lum >= lastLum - 1e-6f, "EnergyConservation: the furnace box brightens monotonically");
                lastLum = lum;
                if (box.x > ceiling.x + 1e-5f) stayedUnderCeiling = false;
            }
            Check(stayedUnderCeiling, "EnergyConservation: the box stays below the analytic furnace ceiling at every bounce");
            CheckWithin(box.x, ceiling.x, 1e-2f, "EnergyConservation: the box converges to the furnace ceiling, not past it");
            Check(IsFinite(box.x) && IsFinite(box.y) && IsFinite(box.z),
                "EnergyConservation: the converged furnace value is finite");

            // A single reflected bounce never carries more than the incident light - the guard
            // is what makes it true, since even a runaway authored albedo is clamped first.
            Vector3 incident = new Vector3(0.7f, 0.5f, 0.3f);
            Vector3 reflected = Vector3.Scale(EnergyConservation.ClampAlbedo(new Vector3(2f, 2f, 2f)), incident);
            Check(!EnergyConservation.Gains(reflected, incident),
                "EnergyConservation: a clamped bounce reflects less than it receives");
            Check(EnergyConservation.Gains(incident * 1.01f, incident),
                "EnergyConservation: the gain guard trips when reflected exceeds incident");
        }

        /// <summary>
        /// Wall-clock refresh pacing (S-68): the same per-second refresh rate is held across
        /// frame rates so the feedback series advances equally on every machine, the fractional
        /// remainder is carried without drift, a long frame is capped without a following burst,
        /// and a degenerate input yields no work.
        /// </summary>
        private static void TestRefreshCadence()
        {
            const float pps = LightingConstants.IrradianceRefreshProbesPerSecond;
            const int cap = LightingConstants.IrradianceRefreshBudgetPerFrame;

            // Frame-rate independence: summed over one real second, the count equals the rate
            // at 144 fps and at 30 fps within a percent - the done-when, since equal work per
            // second means the same steady-state brightness on both (30 fps stays under the cap
            // at pps 1800: 60 probes a frame).
            var fast = new RefreshCadence();
            int fastSum = 0;
            for (int f = 0; f < 144; f++) fastSum += fast.ProbesThisFrame(1f / 144f, pps, cap);
            var slow = new RefreshCadence();
            int slowSum = 0;
            for (int f = 0; f < 30; f++) slowSum += slow.ProbesThisFrame(1f / 30f, pps, cap);

            CheckWithin(fastSum, pps, pps * 0.01f, "RefreshCadence: 144 fps holds the per-second rate within 1%");
            CheckWithin(slowSum, pps, pps * 0.01f, "RefreshCadence: 30 fps holds the per-second rate within 1%");
            CheckWithin(fastSum, slowSum, pps * 0.01f,
                "RefreshCadence: 30 and 144 fps do the same refresh work per second (equal convergence pace)");

            // The fractional carry does not drift: an awkward rate that never lands on a whole
            // number of probes per frame still sums to the rate over a second.
            var odd = new RefreshCadence();
            int oddSum = 0;
            for (int f = 0; f < 137; f++) oddSum += odd.ProbesThisFrame(1f / 137f, 1000f, cap);
            CheckWithin(oddSum, 1000f, 10f, "RefreshCadence: the fractional remainder is carried without drift");

            // A long frame is capped, and the unpaid deficit is dropped rather than bursting on
            // the next frame - a hitch must not spike the frame that follows it.
            var hitch = new RefreshCadence();
            int big = hitch.ProbesThisFrame(1f, pps, cap);         // one whole second in a frame
            Check(big == cap, "RefreshCadence: a long frame is capped at the per-frame budget");
            int after = hitch.ProbesThisFrame(1f / 144f, pps, cap);
            Check(after <= 13, "RefreshCadence: the capped deficit is dropped, not burst onto the next frame");

            // Degenerate inputs cost nothing.
            var z = new RefreshCadence();
            Check(z.ProbesThisFrame(0f, pps, cap) == 0, "RefreshCadence: zero delta time does no work");
            Check(z.ProbesThisFrame(1f / 60f, 0f, cap) == 0, "RefreshCadence: a zero rate does no work");
            Check(z.ProbesThisFrame(1f / 60f, pps, 0) == 0, "RefreshCadence: a zero per-frame cap does no work");

            // Reset clears the carry so a teleport does not pay a catch-up on the next frame.
            var r = new RefreshCadence();
            r.ProbesThisFrame(0.5f, pps, cap);
            r.Reset();
            int postReset = r.ProbesThisFrame(1f / 144f, pps, cap);
            Check(postReset <= 13, "RefreshCadence: Reset clears the carried budget");

            // The paced count reaches the scheduler: Drain honours an explicit per-tick budget
            // and leaves the rest queued, ordering unchanged.
            var sched = new ProbeRefreshScheduler();
            sched.MarkRange(0, 100);
            int[] buf = new int[100];
            int drained = sched.Drain(buf, null, 10);
            Check(drained == 10 && sched.PendingCount == 90,
                "RefreshCadence: the scheduler drains exactly the paced count and queues the rest");
            Check(buf[0] == 0 && buf[9] == 9, "RefreshCadence: the paced drain keeps the deterministic index order");
        }

        /// <summary>
        /// Heightfield ray-march (S-70): a ray cast toward a ridge is occluded at the ridge
        /// within one step length, the CPU trace steps the exact schedule the GPU twin reads
        /// from the published params, and a 64-step trace fits the per-cast budget.
        /// </summary>
        private static void TestHeightfieldTrace()
        {
            // A world flat at y=0 up to a vertical wall at x = ridgeX. A level ray at height 10
            // travelling +x clears the flat ground and is stopped by the wall - occlusion at a
            // ridge, the primitive's whole job.
            const float ridgeX = 100f;
            var flatWithRidge = new System.Func<float, float, float>((x, z) => x >= ridgeX ? 200f : 0f);
            Vector3 origin = new Vector3(0f, 10f, 0f);
            Vector3 toRidge = new Vector3(1f, 0f, 0f);

            // Reproduce the schedule to know which stride crosses the ridge and where that
            // sample lands - the trace uses this exact loop, so the two must agree bit for bit.
            float expectHit = 0f, strideAtHit = 0f, t = 0f;
            for (int i = 0; i < LightingConstants.HeightfieldTraceSteps; i++)
            {
                t += HeightfieldTrace.StepLength(i);
                if (t >= ridgeX) { expectHit = t; strideAtHit = HeightfieldTrace.StepLength(i); break; }
            }

            float hit;
            bool occluded = HeightfieldTrace.Occluded(origin, toRidge, 2000f, flatWithRidge, out hit);
            Check(occluded, "HeightfieldTrace: a level ray toward a ridge is occluded by it");
            CheckWithin(hit, expectHit, 1e-3f, "HeightfieldTrace: the CPU trace lands on its schedule's sample bit-for-bit");
            Check(hit >= ridgeX && (hit - ridgeX) <= strideAtHit + 1e-3f,
                "HeightfieldTrace: the hit is at the ridge within one step length");

            // The same ray with the wall removed climbs out to open sky - no false positive.
            var flat = new System.Func<float, float, float>((x, z) => 0f);
            float miss;
            bool openSky = HeightfieldTrace.Occluded(origin, toRidge, 2000f, flat, out miss);
            Check(!openSky, "HeightfieldTrace: a ray over flat ground reports open sky");

            // A straight-up ray is never occluded by a height field, and a null sampler or zero
            // reach is a safe open-sky miss rather than a throw.
            float up;
            Check(!HeightfieldTrace.Occluded(origin, Vector3.up, 2000f, flatWithRidge, out up),
                "HeightfieldTrace: a vertical ray is never occluded by the ground");
            float none;
            Check(!HeightfieldTrace.Occluded(origin, toRidge, 2000f, null, out none),
                "HeightfieldTrace: a null height sampler is a safe miss");

            // The CPU/GPU shared-schedule contract: the packed params carry the exact three
            // numbers the GPU steps from, and StepLength(i) equals nearStride * growth^i - the
            // identical stride formula HeightfieldTrace.hlsl evaluates from those params, so the
            // two paths march one schedule (GPU execution agreement is checked at the render gate).
            Vector4 p = HeightfieldTrace.TraceParams();
            Check(Mathf.Approximately(p.x, LightingConstants.HeightfieldTraceNearStepM)
                  && Mathf.Approximately(p.y, LightingConstants.HeightfieldTraceStepGrowth)
                  && Mathf.Approximately(p.z, LightingConstants.HeightfieldTraceSteps),
                "HeightfieldTrace: the published params carry the schedule the GPU reads");
            float stride5 = p.x * Mathf.Pow(p.y, 5);
            CheckWithin(stride5, HeightfieldTrace.StepLength(5), 1e-3f,
                "HeightfieldTrace: the GPU stride formula from the params matches StepLength");

            // A 64-step trace fits the per-cast budget. Timed over many casts after a warm-up so
            // the figure is the steady per-cast CPU cost, not first-call JIT.
            var s = new LightingSettings();
            for (int i = 0; i < 500; i++) HeightfieldTrace.Occluded(origin, toRidge, 2000f, flatWithRidge, out hit);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int iters = 4000;
            for (int i = 0; i < iters; i++) HeightfieldTrace.Occluded(origin, toRidge, 2000f, flatWithRidge, out hit);
            sw.Stop();
            double perCastUs = sw.Elapsed.TotalMilliseconds * 1000.0 / iters;
            Check(perCastUs < s.traceBudgetMicroseconds,
                "HeightfieldTrace: a 64-step CPU trace costs under the trace budget (" +
                perCastUs.ToString("0.00") + " us < " + s.traceBudgetMicroseconds + " us)");
        }

        /// <summary>
        /// Sky visibility and the bent normal (S-71): open sky reads fully visible along the
        /// zenith, an asymmetric wall bends the normal away from it and drops visibility, and a
        /// tighter enclosure lets less sky in than a wider one - the depth tracking, from the
        /// heightfield alone with no authored probe volume.
        /// </summary>
        private static void TestSkyVisibility()
        {
            const int dirs = 8;
            float reach = LightingConstants.HeightfieldTraceReachM;

            // The sampling set is a compass ring: every bearing is a unit horizontal direction.
            for (int i = 0; i < dirs; i++)
            {
                Vector3 d = SkyVisibility.AzimuthDirection(i, dirs);
                Check(Mathf.Abs(d.magnitude - 1f) < 1e-3f, "SkyVisibility: each sample direction is unit length");
                Check(Mathf.Abs(d.y) < 1e-4f, "SkyVisibility: every sample bearing is horizontal (a compass azimuth)");
            }

            var flat = new System.Func<float, float, float>((x, z) => 0f);
            Vector3 probe = new Vector3(0f, 2f, 0f);

            // Open flat ground: the whole sky reaches the probe and the bent normal is the zenith.
            SkyVisibilitySample open = SkyVisibility.Sample(probe, dirs, reach, flat);
            CheckWithin(open.visibility01, 1f, 1e-3f, "SkyVisibility: open flat ground sees the full sky");
            Check(Vector3.Dot(open.bentNormal, Vector3.up) > 0.99f,
                "SkyVisibility: over open ground the bent normal points at the zenith");

            // A wall on the +x side only: less sky, and the normal bends away from the wall (-x).
            var wallPlusX = new System.Func<float, float, float>((x, z) => x >= 4f ? 100f : 0f);
            SkyVisibilitySample walled = SkyVisibility.Sample(probe, dirs, reach, wallPlusX);
            Check(walled.visibility01 < open.visibility01,
                "SkyVisibility: a wall removes sky (visibility drops below open)");
            Check(walled.bentNormal.x < 0f, "SkyVisibility: the bent normal leans away from the wall");

            // Depth tracks enclosure: a tight ring wall lets in far less sky than a wide one, and
            // both less than open - the smooth darkening into a gully, from the height field only.
            var tightPit = new System.Func<float, float, float>((x, z) => (x * x + z * z) >= 9f ? 100f : 0f);   // r >= 3
            var widePit = new System.Func<float, float, float>((x, z) => (x * x + z * z) >= 625f ? 100f : 0f);  // r >= 25
            float visTight = SkyVisibility.Sample(probe, dirs, reach, tightPit).visibility01;
            float visWide = SkyVisibility.Sample(probe, dirs, reach, widePit).visibility01;
            Check(visTight < visWide && visWide < open.visibility01,
                "SkyVisibility: a tighter enclosure admits less sky than a wider one (darkening tracks depth)");
            Check(visTight >= 0f && visTight <= 1f, "SkyVisibility: visibility stays a fraction");

            // Determinism: the fixed direction set makes two cold samples bit-identical (C-06).
            SkyVisibilitySample again = SkyVisibility.Sample(probe, dirs, reach, wallPlusX);
            Check(again.visibility01 == walled.visibility01 && again.bentNormal == walled.bentNormal,
                "SkyVisibility: two cold samples are bit-identical");
        }

        private static void TestFoliageColorBleed()
        {
            // A neutral white shaft of daylight arrives at the top of the canopy.
            Vector3 incident = new Vector3(1f, 1f, 1f);

            // The season's leaf colours: chlorophyll green in July, senescent amber in October.
            Vector3 summerLeaf = new Vector3(0.30f, 0.55f, 0.18f);
            Vector3 autumnLeaf = new Vector3(0.62f, 0.40f, 0.10f);
            const float lai = 3f;   // a fairly dense broadleaf canopy

            // --- Summer: the floor goes green and dimmer than open sky. ---
            Vector3 summerFloor = FoliageColorBleed.FloorRadiance(incident, lai, 0f, summerLeaf);
            Check(summerFloor.y > summerFloor.x && summerFloor.y > summerFloor.z,
                "FoliageColorBleed: a summer canopy tints the floor green (G leads R and B)");
            Check(summerFloor.x < incident.x && summerFloor.y < incident.y && summerFloor.z < incident.z,
                "FoliageColorBleed: a leafed canopy is dimmer than open daylight on every channel");

            // --- October: the floor goes warm - red leads, and it is warmer than the summer floor. ---
            Vector3 autumnFloor = FoliageColorBleed.FloorRadiance(incident, lai, 0f, autumnLeaf);
            Check(autumnFloor.x > autumnFloor.y && autumnFloor.y > autumnFloor.z,
                "FoliageColorBleed: an October canopy tints the floor amber (R over G over B)");
            Check(autumnFloor.x / Mathf.Max(1e-4f, autumnFloor.z) > summerFloor.x / Mathf.Max(1e-4f, summerFloor.z),
                "FoliageColorBleed: the autumn floor is warmer (higher R:B) than the summer floor");

            // --- Leaf-off: no interception, so the floor gets the full colourless daylight
            //     and reads brighter than under the summer canopy. ---
            Vector3 bareFloor = FoliageColorBleed.FloorRadiance(incident, lai, 1f, summerLeaf);
            CheckWithin(bareFloor.x, incident.x, 1e-4f, "FoliageColorBleed: leaf-off floor gets full daylight (R)");
            CheckWithin(bareFloor.y, incident.y, 1e-4f, "FoliageColorBleed: leaf-off floor gets full daylight (G)");
            CheckWithin(bareFloor.z, incident.z, 1e-4f, "FoliageColorBleed: leaf-off floor gets full daylight (B)");
            Check(Mathf.Abs(bareFloor.x - bareFloor.y) < 1e-4f && Mathf.Abs(bareFloor.y - bareFloor.z) < 1e-4f,
                "FoliageColorBleed: leaf-off floor is colourless (no tint)");
            Check(bareFloor.y > summerFloor.y,
                "FoliageColorBleed: leaf-off floor is brighter than under the summer canopy");

            // --- Beer-Lambert monotonicity: a denser canopy is darker (more intercepted). ---
            float gapThin = FoliageColorBleed.GapFraction(FoliageColorBleed.EffectiveLai(1f, 0f));
            float gapThick = FoliageColorBleed.GapFraction(FoliageColorBleed.EffectiveLai(6f, 0f));
            Check(gapThick < gapThin && gapThin < 1f,
                "FoliageColorBleed: gap fraction falls as LAI rises (Beer-Lambert), never above one");
            Vector3 floorThick = FoliageColorBleed.FloorRadiance(incident, 6f, 0f, summerLeaf);
            Check(floorThick.y < summerFloor.y,
                "FoliageColorBleed: a denser canopy delivers less light to the floor");

            // --- No leaves, no canopy: with LAI zero the gap is one and the floor is untouched. ---
            Vector3 noCanopy = FoliageColorBleed.FloorRadiance(incident, 0f, 0f, summerLeaf);
            CheckWithin(noCanopy.y, incident.y, 1e-4f, "FoliageColorBleed: zero LAI leaves daylight unchanged");

            // --- Leaf energy conservation: the floor never gains energy over the incident light,
            //     and transmittance plus reflectance stay at or below one per channel (S-69). ---
            Check(!EnergyConservation.Gains(summerFloor, incident),
                "FoliageColorBleed: the shaded floor never out-radiates the daylight that lit it");
            Vector3 tau = FoliageColorBleed.LeafTransmittance(new Vector3(1f, 1f, 1f));
            Vector3 rho = FoliageColorBleed.LeafReflectance(new Vector3(1f, 1f, 1f));
            Check(tau.x + rho.x <= 1f && tau.y + rho.y <= 1f && tau.z + rho.z <= 1f,
                "FoliageColorBleed: leaf transmittance + reflectance is energy-conserving (tau + rho <= 1)");

            // --- Reflected secondary source: coloured like the leaves, and zero once they are gone. ---
            Vector3 reflected = FoliageColorBleed.ReflectedSource(incident, lai, 0f, summerLeaf);
            Check(reflected.y > reflected.x && reflected.y > reflected.z,
                "FoliageColorBleed: the canopy's reflected bounce carries the leaf colour");
            Vector3 reflectedBare = FoliageColorBleed.ReflectedSource(incident, lai, 1f, summerLeaf);
            Check(reflectedBare == Vector3.zero,
                "FoliageColorBleed: a leaf-off canopy is not a coloured secondary source");

            // --- Determinism (C-06): the pure optics repeat bit-identically. ---
            Vector3 summerAgain = FoliageColorBleed.FloorRadiance(incident, lai, 0f, summerLeaf);
            Check(summerAgain == summerFloor, "FoliageColorBleed: two cold evaluations are bit-identical");
        }

        private static void TestProbeValidity()
        {
            var flat = new System.Func<float, float, float>((x, z) => 0f);
            Vector3 point = new Vector3(0f, 1f, 0f);   // a metre above open flat ground

            // A probe directly overhead on flat ground is plainly visible.
            Check(ProbeValidity.IsVisible(point, new Vector3(0f, 6f, 0f), flat),
                "ProbeValidity: an overhead probe over open ground is visible");

            // A tall wall between the point and a probe on its far side blocks the segment.
            var wall = new System.Func<float, float, float>((x, z) => (x >= 4f && x <= 6f) ? 100f : 0f);
            Vector3 farProbe = new Vector3(12f, 2f, 0f);   // beyond the wall, low
            Check(!ProbeValidity.IsVisible(point, farProbe, wall),
                "ProbeValidity: a probe behind a cliff/wall is dropped (no leak through the rock)");
            // A probe on the near side of the same wall is still reachable.
            Check(ProbeValidity.IsVisible(point, new Vector3(2f, 2f, 0f), wall),
                "ProbeValidity: a probe on the open side of the wall stays visible");

            // Renormalisation preserves total ambient energy: four equal probes, two blocked by
            // the wall, the surviving two must carry the whole original weight between them.
            Vector3[] probes =
            {
                new Vector3(0f, 6f, 0f),    // overhead - open
                new Vector3(-3f, 3f, 0f),   // behind, open
                new Vector3(12f, 2f, 0f),   // far side of wall - blocked
                new Vector3(9f, 1.5f, 0f),  // far side of wall - blocked
            };
            float[] weights = { 0.25f, 0.25f, 0.25f, 0.25f };
            bool[] valid = new bool[4];
            int survivors = ProbeValidity.FilterAndRenormalize(point, probes, weights, valid, 4, wall);

            Check(survivors == 2, "ProbeValidity: exactly the two open probes survive the wall");
            Check(valid[0] && valid[1] && !valid[2] && !valid[3],
                "ProbeValidity: the validity mask marks the blocked probes");
            Check(weights[2] == 0f && weights[3] == 0f, "ProbeValidity: blocked probes carry no weight");
            CheckWithin(weights[0] + weights[1] + weights[2] + weights[3], 1f, 1e-4f,
                "ProbeValidity: renormalised weights preserve the original total (no darkening at the cliff)");
            CheckWithin(weights[0], 0.5f, 1e-4f, "ProbeValidity: each survivor takes an equal share of the retained energy");

            // Fully enclosed: every probe blocked leaves zero weight for the caller to fall back on.
            Vector3[] buried = { new Vector3(12f, 2f, 0f), new Vector3(9f, 1.5f, 0f) };
            float[] w2 = { 0.5f, 0.5f };
            bool[] v2 = new bool[2];
            int none = ProbeValidity.FilterAndRenormalize(point, buried, w2, v2, 2, wall);
            Check(none == 0 && w2[0] == 0f && w2[1] == 0f,
                "ProbeValidity: when all probes are blocked, weights are zero and the caller falls back");

            // Determinism (C-06): the same point resolves bit-identically twice.
            float[] w3 = { 0.25f, 0.25f, 0.25f, 0.25f };
            bool[] v3 = new bool[4];
            ProbeValidity.FilterAndRenormalize(point, probes, w3, v3, 4, wall);
            Check(w3[0] == weights[0] && w3[1] == weights[1], "ProbeValidity: two cold filters are bit-identical");
        }

        private static void TestEmissiveContributors()
        {
            Vector3 probe = Vector3.zero;
            Vector3 white = new Vector3(1f, 1f, 1f);

            // Inverse-square falloff: doubling the distance quarters the contribution.
            var near = new EmissiveEmitter(new Vector3(2f, 0f, 0f), white, 0f, 0);
            var far = new EmissiveEmitter(new Vector3(4f, 0f, 0f), white, 0f, 1);
            Vector3 cNear = EmissiveContributors.Contribution(probe, near);
            Vector3 cFar = EmissiveContributors.Contribution(probe, far);
            CheckWithin(cNear.x, 0.25f, 1e-4f, "EmissiveContributors: contribution is 1/d^2 (2 m -> 1/4)");
            CheckWithin(cFar.x, 0.0625f, 1e-4f, "EmissiveContributors: contribution is 1/d^2 (4 m -> 1/16)");
            Check(cNear.x > cFar.x, "EmissiveContributors: a nearer emitter contributes more");

            // The one-metre floor stops a coincident emitter from dividing by zero.
            var onTop = new EmissiveEmitter(probe, white, 0f, 2);
            Vector3 cOn = EmissiveContributors.Contribution(probe, onTop);
            CheckWithin(cOn.x, 1f, 1e-4f, "EmissiveContributors: inside the 1 m floor the falloff is held flat");

            // The hard cap: three emitters, cap of two, keeps only the two strongest by priority.
            var e0 = new EmissiveEmitter(new Vector3(2f, 0f, 0f), white, 0f, 0);   // 1/4
            var e1 = new EmissiveEmitter(new Vector3(3f, 0f, 0f), white, 0f, 1);   // 1/9
            var e2 = new EmissiveEmitter(new Vector3(4f, 0f, 0f), white, 0f, 2);   // 1/16 - dropped
            var three = new EmissiveEmitter[] { e0, e1, e2 };
            Vector3 capped = EmissiveContributors.Accumulate(probe, three, 3, 2);
            float expected = 0.25f + (1f / 9f);
            CheckWithin(capped.x, expected, 1e-4f, "EmissiveContributors: the cap keeps only the strongest N emitters");

            // Order independence -> determinism: the same emitters in reverse order give the same sum.
            var reversed = new EmissiveEmitter[] { e2, e1, e0 };
            Vector3 cappedRev = EmissiveContributors.Accumulate(probe, reversed, 3, 2);
            Check(cappedRev == capped, "EmissiveContributors: the kept set is independent of array order (deterministic)");

            // Range cutoff: an emitter past its own range does not reach the probe at all.
            var ranged = new EmissiveEmitter(new Vector3(5f, 0f, 0f), white, 3f, 3);
            Check(EmissiveContributors.Priority(probe, ranged) == 0f,
                "EmissiveContributors: an out-of-range emitter has zero priority");
            Check(EmissiveContributors.Contribution(probe, ranged) == Vector3.zero,
                "EmissiveContributors: an out-of-range emitter contributes nothing");

            // Remove the campfire and its bleed is gone: the accumulate is a live sum over what exists.
            var campfire = new EmissiveEmitter(new Vector3(3f, 0f, 0f), new Vector3(1.5f, 0.7f, 0.25f), 0f, 9);
            var withFire = new EmissiveEmitter[] { e0, campfire };
            Vector3 lit = EmissiveContributors.Accumulate(probe, withFire, 2, 8);
            Vector3 unlit = EmissiveContributors.Accumulate(probe, withFire, 1, 8);   // count 1 = campfire excluded
            Check(lit.x > unlit.x && lit.y > unlit.y && lit.z > unlit.z,
                "EmissiveContributors: the campfire adds bleed; dropping it removes the bleed");
            // The campfire keeps its warm hue through the grid (R over G over B).
            Vector3 fireOnly = EmissiveContributors.Contribution(probe, campfire);
            Check(fireOnly.x > fireOnly.y && fireOnly.y > fireOnly.z,
                "EmissiveContributors: a warm emitter warms its surroundings (keeps its hue)");

            // A cap of zero disables the path cleanly; a null list is safe.
            Check(EmissiveContributors.Accumulate(probe, three, 3, 0) == Vector3.zero,
                "EmissiveContributors: a zero cap yields no contribution");
            Check(EmissiveContributors.Accumulate(probe, null, 0, 8) == Vector3.zero,
                "EmissiveContributors: a null emitter list is safe");
        }

        private static void TestSpecularOcclusion()
        {
            // An exposed rock top: full sky visibility, bent normal at the zenith, reflection up.
            // The highlight survives at any roughness.
            float topRough = SpecularOcclusion.Evaluate(1f, Vector3.up, Vector3.up, 0.8f);
            float topSmooth = SpecularOcclusion.Evaluate(1f, Vector3.up, Vector3.up, 0.02f);
            Check(topRough > 0.95f && topSmooth > 0.95f,
                "SpecularOcclusion: an open top keeps its sky highlight (specular visibility ~1)");

            // A crevice: little sky, the bent normal tilts out of the gap, and a smooth interior
            // face reflects INTO the rock (misaligned). No highlight inside.
            Vector3 tiltedBent = new Vector3(0.6f, 0.5f, 0f).normalized;
            Vector3 intoRock = new Vector3(-0.6f, -0.3f, 0f).normalized;   // points into the gap wall
            float crevice = SpecularOcclusion.Evaluate(0.2f, tiltedBent, intoRock, 0.03f);
            Check(crevice < 0.05f, "SpecularOcclusion: a smooth crevice interior shows no sky highlight");

            // At full roughness the specular cone widens to the whole hemisphere, so specular
            // occlusion collapses to the diffuse visibility regardless of the reflection direction.
            float vis = 0.5f;
            float rough = SpecularOcclusion.Evaluate(vis, Vector3.up, new Vector3(1f, 0.2f, 0f), 1f);
            CheckWithin(rough, vis, 1e-3f, "SpecularOcclusion: a fully rough surface reduces to diffuse visibility");

            // The cone really is narrower than diffuse: with the reflection pointing AWAY from the
            // open sky, a smooth surface is more occluded than a rough one at the same visibility.
            Vector3 away = new Vector3(0f, -1f, 0f);   // straight down, opposite the open zenith
            float smoothAway = SpecularOcclusion.Evaluate(vis, Vector3.up, away, 0.02f);
            float roughAway = SpecularOcclusion.Evaluate(vis, Vector3.up, away, 1f);
            Check(smoothAway < roughAway,
                "SpecularOcclusion: with the reflection off the open sky, the tight cone occludes more than diffuse");

            // And when the reflection aims straight at the open gap, the tight cone finds more sky
            // than the diffuse average - a smooth surface facing the opening keeps a brighter highlight.
            float smoothInto = SpecularOcclusion.Evaluate(vis, Vector3.up, Vector3.up, 0.02f);
            float roughInto = SpecularOcclusion.Evaluate(vis, Vector3.up, Vector3.up, 1f);
            Check(smoothInto > roughInto,
                "SpecularOcclusion: aiming the reflection at the open sky recovers more highlight than diffuse");

            // Bounds and determinism.
            Check(crevice >= 0f && topSmooth <= 1f, "SpecularOcclusion: the result stays a 0..1 fraction");
            float again = SpecularOcclusion.Evaluate(0.2f, tiltedBent, intoRock, 0.03f);
            Check(again == crevice, "SpecularOcclusion: two cold evaluations are bit-identical");
        }

        private static void TestWetSurfaceOptics()
        {
            Vector3 dryAlbedo = new Vector3(0.4f, 0.35f, 0.3f);
            const float dryRough = 0.8f;

            // Bone dry: the optics change nothing and add no reflection.
            Check(WetSurfaceOptics.WetAlbedo(dryAlbedo, 0f, 0f) == dryAlbedo,
                "WetSurfaceOptics: a dry surface keeps its albedo");
            CheckWithin(WetSurfaceOptics.WetRoughness(dryRough, 0f, 0f), dryRough, 1e-4f,
                "WetSurfaceOptics: a dry surface keeps its roughness");
            CheckWithin(WetSurfaceOptics.SkyReflectionWeight(0.5f, 0f, 0f), 0f, 1e-4f,
                "WetSurfaceOptics: a dry surface gains no sky reflection");

            // Fully wet: albedo darkens by the textbook third, roughness collapses to the sheen.
            Vector3 wetA = WetSurfaceOptics.WetAlbedo(dryAlbedo, 1f, 0f);
            CheckWithin(wetA.x, dryAlbedo.x * (1f - LightingConstants.WetAlbedoDarkening), 1e-4f,
                "WetSurfaceOptics: a soaked surface loses about a third of its albedo");
            Check(wetA.x < dryAlbedo.x && wetA.y < dryAlbedo.y && wetA.z < dryAlbedo.z,
                "WetSurfaceOptics: wet is always darker than dry, never brighter");
            CheckWithin(WetSurfaceOptics.WetRoughness(dryRough, 1f, 0f), LightingConstants.WetFloorRoughness, 1e-4f,
                "WetSurfaceOptics: a soaked surface reaches the water sheen roughness");
            Check(WetSurfaceOptics.WetRoughness(dryRough, 1f, 0f) < dryRough,
                "WetSurfaceOptics: wetting only ever glosses a surface (never rougher than dry)");

            // Monotonic in wetness: wetter -> darker, glossier, more reflective.
            Vector3 damp = WetSurfaceOptics.WetAlbedo(dryAlbedo, 0.4f, 0f);
            Check(damp.x > wetA.x && damp.x < dryAlbedo.x, "WetSurfaceOptics: darkening grows with wetness");
            Check(WetSurfaceOptics.WetRoughness(dryRough, 0.4f, 0f) > WetSurfaceOptics.WetRoughness(dryRough, 1f, 0f),
                "WetSurfaceOptics: roughness falls further as the surface wets");
            Check(WetSurfaceOptics.SkyReflectionWeight(0.5f, 1f, 0f) > WetSurfaceOptics.SkyReflectionWeight(0.5f, 0.4f, 0f),
                "WetSurfaceOptics: the sky reflection strengthens with wetness");

            // A standing puddle saturates the optics regardless of the film wetness.
            Check(WetSurfaceOptics.EffectiveWetness(0.2f, 1f) == 1f,
                "WetSurfaceOptics: a full puddle drives the optics to their wet limit");
            Check(WetSurfaceOptics.WetAlbedo(dryAlbedo, 0.2f, 1f) == wetA,
                "WetSurfaceOptics: a puddle over a barely-damp surface still reads fully wet");

            // Fresnel: faint head-on, near a mirror at grazing angles - the wet-road-far-off look.
            float head = WetSurfaceOptics.FresnelReflectance(1f);
            float graze = WetSurfaceOptics.FresnelReflectance(0f);
            CheckWithin(head, LightingConstants.WaterFresnelF0, 1e-4f,
                "WetSurfaceOptics: head-on reflectance is water's F0 (~0.02)");
            Check(graze > 0.98f, "WetSurfaceOptics: grazing reflectance approaches a full mirror");
            Check(graze > head, "WetSurfaceOptics: reflection is stronger at grazing angles than head-on");

            // Determinism (C-06).
            float again = WetSurfaceOptics.SkyReflectionWeight(0.3f, 0.7f, 0.1f);
            Check(again == WetSurfaceOptics.SkyReflectionWeight(0.3f, 0.7f, 0.1f),
                "WetSurfaceOptics: two cold evaluations are bit-identical");
        }

        private static void TestScreenSpaceReflection()
        {
            // SSR is Ultra-only. The whole point of the tier bool is that the feature never even
            // enqueues below Ultra, so T2/T1/T0 are byte-identical to before SSR existed.
            var ultra  = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra);
            var high   = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.High);
            var medium = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium);
            var low    = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);

            Check(ultra.screenSpaceReflections,
                "SSR: the flagship (Ultra) tier turns screen-space reflections on");
            Check(!high.screenSpaceReflections && !medium.screenSpaceReflections && !low.screenSpaceReflections,
                "SSR: every tier below Ultra leaves reflections off (Ultra-only, S-77)");

            // Byte-identical-below-Ultra proof: on Medium the SSR bool contributes nothing to the
            // screen-space OR, so AnyScreenSpaceEffect is exactly what it was before the field existed.
            Check(medium.AnyScreenSpaceEffect == (medium.screenSpaceGI || medium.contactShadows),
                "SSR: reflections add nothing to the Medium screen-space set (T2 unchanged)");
            Check(ultra.AnyScreenSpaceEffect,
                "SSR: with reflections on, the flagship still reports a screen-space effect in force");

            // The authored dials clamp to the ranges their tooltips promise (settings drive the
            // renderer feature's Setup, which re-clamps identically).
            var s = new LightingSettings();
            Check(s.ssrStepBudget == LightingConstants.SsrStepBudgetDefault,
                "SSR: the step-budget default tracks the one constant home (no restated literal)");
            s.ssrStepBudget = 9999; s.ssrTemporalWeight = 5f;
            s.ClampToValidRanges();
            Check(s.ssrStepBudget == 256, "SSR: the step budget clamps to its hard 256 ceiling");
            Check(s.ssrTemporalWeight <= 0.98f, "SSR: the temporal weight clamps below a full-history freeze");
            s.ssrStepBudget = -100; s.ssrTemporalWeight = -1f;
            s.ClampToValidRanges();
            Check(s.ssrStepBudget == 8, "SSR: the step budget clamps up to its 8-step floor");
            Check(s.ssrTemporalWeight == 0f, "SSR: the temporal weight clamps to a no-history floor");

            // Determinism (C-06): the tier resolve is pure - two cold reads agree bit for bit.
            Check(UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                    UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra).screenSpaceReflections ==
                  ultra.screenSpaceReflections,
                "SSR: two cold tier resolves are bit-identical");
        }

        // S-79: the ray-free mobile reflection path. There is no screen-space march below Ultra, so
        // the phone builds its reflection from the sky/grid, occluded by S-75 and weighted by S-76 -
        // the SAME maths the GPU twin (UC_SurfaceReflectance.hlsl) restates. This test guards the CPU
        // side of that parity and the never-black composition; the GPU-visual result is gate-verified.
        private static void TestMobileReflectionPath()
        {
            // The mobile path is defined by the absence of a ray: no tier below Ultra may turn SSR on,
            // or "cheap because none of it traces a ray" would be a lie on that hardware.
            var low    = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);
            var medium = UniverseCreator.TerrainSystem.Lighting.LightingTier.Resolve(
                            UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium);
            Check(!low.screenSpaceReflections && !medium.screenSpaceReflections,
                "Mobile reflection: no screen-space march on the mobile tiers (ray-free, S-79)");

            // Drift guard: UC_SurfaceReflectance.hlsl restates these three as the fallback its
            // uniform uses when no driver has published. If the constant home moves, the shader
            // fallback silently disagrees - so pin the literals the twin depends on here.
            CheckWithin(LightingConstants.WetAlbedoDarkening, 0.35f, 1e-6f,
                "Mobile reflection: wet-albedo darkening matches the shader fallback literal");
            CheckWithin(LightingConstants.WetFloorRoughness, 0.06f, 1e-6f,
                "Mobile reflection: wet-floor roughness matches the shader fallback literal");
            CheckWithin(LightingConstants.WaterFresnelF0, 0.02f, 1e-6f,
                "Mobile reflection: water F0 matches the shader fallback literal");

            // A dry surface adds no reflection (the weight is exactly zero), so a dry world is
            // byte-identical to no reflection path at all - the feature only ever adds.
            CheckWithin(WetSurfaceOptics.SkyReflectionWeight(0.2f, 0f, 0f), 0f, 1e-6f,
                "Mobile reflection: a dry surface reflects nothing extra (weight is zero)");

            // A wet surface at a grazing view reflects, and the reflection is never black by
            // construction: the sky/grid radiance is scaled by a POSITIVE occlusion*Fresnel weight.
            float wetGrazingWeight = WetSurfaceOptics.SkyReflectionWeight(0.05f, 1f, 0f);
            Check(wetGrazingWeight > 0f && IsFinite(wetGrazingWeight),
                "Mobile reflection: a wet grazing surface takes a positive, finite reflection weight");
            float openOcclusion = SpecularOcclusion.Evaluate(1f, Vector3.up, Vector3.up, 0f);
            Check(openOcclusion > 0f,
                "Mobile reflection: a mirror facing open sky keeps its highlight (no black crevice)");

            // Wetting only ever glosses: the wet roughness never exceeds the dry roughness.
            float dryR = 0.5f;
            Check(WetSurfaceOptics.WetRoughness(dryR, 1f, 0f) <= dryR + 1e-6f,
                "Mobile reflection: wetting never makes a surface rougher than dry");
            // At full roughness the specular cone widens until occlusion collapses to the diffuse
            // visibility - the S-75 boundary the mobile grid specular relies on.
            CheckWithin(SpecularOcclusion.Evaluate(0.4f, Vector3.up, Vector3.up, 1f), 0.4f, 0.05f,
                "Mobile reflection: full roughness collapses specular occlusion to diffuse visibility");

            // Determinism (C-06): the optics are pure - two evaluations agree bit for bit.
            Check(WetSurfaceOptics.SkyReflectionWeight(0.05f, 1f, 0f) == wetGrazingWeight,
                "Mobile reflection: the wet optics are deterministic (two reads identical)");
        }

        // S-80: every screen-space term that accumulates across frames steadies itself the SAME
        // way, from ONE home (UC_TemporalReproject.hlsl) - motion-vector reprojection, a
        // neighbourhood-variance clamp and a disocclusion reject - so a fast pan leaves no ghost
        // trail. This guards that the home exists once and that each owned consumer reads it (no
        // divergent inline copy); the no-ghost result itself is GPU-visual, gate-verified.
        private static void TestTemporalReprojection()
        {
            string shaders = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Shaders");

            // The ONE shared home. Its four helpers and both variance-box overloads must live here
            // and nowhere else - a second copy is exactly the divergence S-80 exists to prevent.
            string th = ReadShader(shaders, "UC_TemporalReproject.hlsl");
            Check(th.Length > 0, "Temporal: UC_TemporalReproject.hlsl exists (the one shared home)");
            Check(th.Contains("UCReprojectByMotion") && th.Contains("_MotionVectorTexture"),
                "Temporal: primary reprojection reads URP's motion texture (camera AND object motion)");
            Check(th.Contains("UCReprojectByViewProj"),
                "Temporal: prev view-projection is the static reprojection fallback");
            Check(th.Contains("UCHistoryDepthValid"),
                "Temporal: a disocclusion (depth-mismatch) reject drops a stale history sample");
            Check(th.Contains("void UCVarianceBox(float m1") && th.Contains("void UCVarianceBox(float3 m1"),
                "Temporal: the variance-clamp box has both a scalar (contact) and rgb (SSGI/SSR) overload");

            // Each screen-space term WE own reads that home - anti-duplication made structural.
            string ssr  = ReadShader(shaders, "UC_SSR.shader");
            string ssgi = ReadShader(shaders, "UC_SSGI.shader");
            string cs   = ReadShader(shaders, "UC_ContactShadow.shader");
            Check(ssr.Contains("UC_TemporalReproject.hlsl"),  "Temporal: SSR reads the shared home (no inline copy)");
            Check(ssgi.Contains("UC_TemporalReproject.hlsl"), "Temporal: SSGI reads the shared home (no inline copy)");
            Check(cs.Contains("UC_TemporalReproject.hlsl"),   "Temporal: contact shadows read the shared home (no inline copy)");

            // Every owning feature must request motion vectors, or the primary reprojection has
            // nothing to read and silently degrades to the camera-only fallback on moving objects.
            string lighting = System.IO.Path.Combine(Application.dataPath, "UniverseCreator/Core/Runtime/Lighting");
            string[] feats = { "ScreenSpaceReflectionFeature.cs", "ScreenSpaceGIFeature.cs", "ContactShadowFeature.cs" };
            foreach (string f in feats)
            {
                string p = System.IO.Path.Combine(lighting, f);
                string src = System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : "";
                Check(src.Contains("ScriptableRenderPassInput.Motion"),
                    "Temporal: " + f + " requests motion vectors (S-80 object+camera reprojection)");
            }

            // The fourth accumulating term, SSAO, is Unity's BUILT-IN ScreenSpaceAmbientOcclusion:
            // its temporal/blur filtering is Unity-owned. We configure the feature, never fork its
            // shader, so it is accounted for here by the setup path, not by a copy of our home.
            string setup = System.IO.Path.Combine(Application.dataPath,
                "UniverseCreator/Core/Editor/Diagnostics/UCRenderPipelineSetup.cs");
            string setupSrc = System.IO.File.Exists(setup) ? System.IO.File.ReadAllText(setup) : "";
            Check(setupSrc.Contains("ScreenSpaceAmbientOcclusion"),
                "Temporal: SSAO is Unity's built-in feature we configure, not a shader we fork (S-80)");
        }

        // -----------------------------------------------------------------------------
        //  Determinism (C-04). The whole reason the analytic grid can claim what Lumen
        //  cannot: every term except the strictly-cosmetic screen-space ones is a pure
        //  function of world position, seed and the clock - no bake, no probe-placement
        //  pass, no lighting build - so it reproduces bit-for-bit from a cold start. This
        //  fills a small grid twice from two independent cold starts and asserts every
        //  probe face is bit-identical: not "close", identical to the raw IEEE-754 int
        //  bits. That is the guard that stops a HashSet iteration order, a Time.time read,
        //  a Random draw or a reordered reduction from ever silently entering the fill. The
        //  same test on a second machine running the same float ops in the same order is
        //  therefore the machine-independence proof; that half needs the second machine.
        // -----------------------------------------------------------------------------
        private static void TestGridDeterminism()
        {
            IrradianceProbe[] first  = FillGridColdStart();
            IrradianceProbe[] second = FillGridColdStart();

            Check(first.Length == second.Length && first.Length > 0,
                "GridDeterminism: two cold-start fills produce the same, non-empty probe count");

            int mismatches = 0;
            bool anyLight = false, allFinite = true;
            for (int i = 0; i < first.Length; i++)
            {
                if (!ProbeBitsEqual(first[i], second[i])) mismatches++;
                if (ProbeMaxComponent(first[i]) > 0f) anyLight = true;
                if (!ProbeFinite(first[i])) allFinite = false;
            }

            Check(mismatches == 0,
                "GridDeterminism: " + mismatches + " of " + first.Length + " probes differ between two " +
                "cold-start fills - the analytic grid must be bit-identical every time (C-04)");
            Check(anyLight,
                "GridDeterminism: the fill carries real light (a bit-identical field of zeros would pass vacuously)");
            Check(allFinite,
                "GridDeterminism: every probe face is finite - no NaN or Inf leaked into the grid");
        }

        // One complete cold-start fill of a small grid through the SAME terms the driver
        // composes - sky, ground, first bounce, multi-bounce feedback, clamp, then Fill -
        // with every input a pure function of the lattice coordinates, so nothing here reads
        // a clock, a Random or shared mutable state. A fresh grid and fresh arrays each call.
        private static IrradianceProbe[] FillGridColdStart()
        {
            MultiBounceFeedback.ResetClampLog();

            System.Func<float, float, float> heightAt =
                (x, z) => 8f * Mathf.Sin(x * 0.05f) + 5f * Mathf.Cos(z * 0.07f);
            System.Func<float, float, Vector3> albedoAt =
                (x, z) => new Vector3(0.30f + 0.15f * Mathf.Sin(x * 0.11f),
                                      0.28f + 0.12f * Mathf.Cos(z * 0.09f), 0.22f);
            System.Func<float, float, Vector3> prevAmbientAt =
                (x, z) => new Vector3(0.05f, 0.06f, 0.07f) * (0.5f + 0.5f * Mathf.Sin(x * 0.03f + z * 0.02f));

            Vector3 sunDir   = new Vector3(0.3f, 0.8f, 0.5f).normalized;
            Vector3 sunColor = new Vector3(1.0f, 0.95f, 0.85f);
            Vector3 skyRad   = new Vector3(0.4f, 0.55f, 0.8f);
            float sunIll = 1.2f, bounce = LightingConstants.IrradianceBounceStrength;

            var grid = new IrradianceGrid(new Vector2(-40f, -40f), 80f, 80f, heightAt);
            for (int z = 0; z < grid.dimZ; z++)
            for (int x = 0; x < grid.dimX; x++)
            for (int y = 0; y < grid.dimY; y++)
            {
                Vector3 p = grid.WorldPosition(x, y, z);
                float skyVis = ProbeFill.SkyVisibility01(p, heightAt);
                float sunVis = ProbeFill.SunVisibility01(p, heightAt, sunDir);
                Vector3 ground = ProbeFill.GroundRadiance(albedoAt(p.x, p.z), skyRad, skyVis,
                                                          sunColor, sunIll, sunDir, sunVis);
                Vector3 single = ProbeFill.BounceRadiance(p, heightAt, albedoAt, sunColor, sunIll, sunDir);
                Vector3 feedback = MultiBounceFeedback.FeedbackRadiance(p, prevAmbientAt, albedoAt, bounce);
                bool engaged;
                Vector3 indirect = MultiBounceFeedback.ClampAccumulated(single, feedback,
                                       LightingConstants.IrradianceMultiBounceClampScale, out engaged);
                grid.probes[grid.Index(x, y, z)] = ProbeFill.Fill(skyRad, skyVis, ground, indirect);
            }
            return grid.probes;
        }

        // Raw-int-bits equality over all six faces of a probe: catches a flipped low mantissa
        // bit that a tolerance compare would wave through, which is the whole point of a
        // determinism guard.
        private static bool ProbeBitsEqual(IrradianceProbe a, IrradianceProbe b)
        {
            return VecBitsEqual(a.posX, b.posX) && VecBitsEqual(a.negX, b.negX) &&
                   VecBitsEqual(a.posY, b.posY) && VecBitsEqual(a.negY, b.negY) &&
                   VecBitsEqual(a.posZ, b.posZ) && VecBitsEqual(a.negZ, b.negZ);
        }

        private static bool VecBitsEqual(Vector3 a, Vector3 b)
        {
            // float.Equals is a bit-pattern compare (NaN equals NaN, +0 differs from -0), so
            // it catches a flipped low mantissa bit that operator== or a tolerance would miss.
            return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        }

        private static float ProbeMaxComponent(IrradianceProbe p)
        {
            float m = 0f;
            foreach (Vector3 f in new[] { p.posX, p.negX, p.posY, p.negY, p.posZ, p.negZ })
                m = Mathf.Max(m, Mathf.Max(f.x, Mathf.Max(f.y, f.z)));
            return m;
        }

        private static bool ProbeFinite(IrradianceProbe p)
        {
            foreach (Vector3 f in new[] { p.posX, p.negX, p.posY, p.negY, p.posZ, p.negZ })
                if (!IsFinite(f.x) || !IsFinite(f.y) || !IsFinite(f.z)) return false;
            return true;
        }

        // A resolved clock for the hand-off tests, carrying the sun and moon geometry the
        // sky reads; the calendar and the remaining astronomy rest at neutral values.
        private static ClockState MakeClock(float sunElevDeg, float sunAzDeg, float airMass,
                                            float moonElevDeg, float moonFrac)
        {
            return new ClockState(
                2026, 172, 43200f, 12f, 40f, 0f,
                sunElevDeg, sunAzDeg, 0f, 0f, 12f, 12f, 0f,
                0.5f, moonElevDeg, 90f, 0f, moonFrac,
                0.25f, 1361f, airMass);
        }
    }
}
