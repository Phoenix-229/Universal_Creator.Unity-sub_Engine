// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  Module 3: Living World
//
//  File:    UCSelfTest.Ecology.cs
//  Purpose: The ecology half of the self test. Asserts that the physical laws in
//           EcoMath still hold, that the constants still carry their measured
//           values, that EnvironmentSample has not grown, and that the vegetation
//           manager's resolve-once cache both holds and releases.
//
//  WHY A SECOND FILE
//  -----------------
//  UCSelfTest.cs is already past two thousand lines. A partial class shares its
//  counter and its Check helper, so these checks land in the same tally and the
//  same report without that file growing again.
//
//  WHAT THESE CHECKS ARE FOR
//  -------------------------
//  Not "does it compile" - the compiler covers that. These assert the physics.
//  Every law here has a value a maintainer could plausibly "tidy" into something
//  wrong: an environmental lapse rate rounded to 6, a Liebig combiner turned into
//  a product because a product looks smoother, a Gaussian whose sigma is read as
//  the full width instead of the half width. Each of those compiles perfectly and
//  then produces a world that is subtly, unfixably wrong. So the numbers are
//  pinned here, with the law that fixes them.
//
//  The tolerance is generous on purpose. These are assertions about which law is
//  implemented, not about float determinism - C-06's bit-for-bit claim is tested
//  by the determinism harness, not here.
// -----------------------------------------------------------------------------

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // Loose enough that a different-but-equivalent arrangement of the same
        // formula passes, tight enough that a different formula does not.
        private const float EcoEpsilon = 1e-4f;

        private static void CheckClose(float actual, float expected, string message)
        {
            Check(Mathf.Abs(actual - expected) <= EcoEpsilon,
                  message + " (expected " + expected + ", got " + actual + ")");
        }

        // ---------------------------------------------------------------------
        //  V-01  CONSTANTS
        // ---------------------------------------------------------------------

        /// <summary>
        /// The measured values, pinned. Each of these is a real-world measurement
        /// rather than a tuning knob, so a change to one is a change to the physics
        /// of every world the toolkit builds and must be deliberate.
        /// </summary>
        private static void TestEcologyConstants()
        {
            CheckClose(EcoConstants.LapseRateCPerKm, 6.5f,
                       "L-04 environmental lapse rate must stay 6.5 C/km");

            CheckClose(EcoConstants.LapseRateSaturatedCPerKm, 5.0f,
                       "L-04 saturated adiabatic lapse rate must stay 5.0 C/km");

            CheckClose(EcoConstants.LapseRateDryAdiabaticCPerKm, 9.8f,
                       "L-04 dry adiabatic lapse rate must stay 9.8 C/km");

            // The ordering is the physics, independent of the values: air that has
            // condensed out its moisture cools more slowly than dry air, and the
            // real atmosphere sits between the two. If this ever inverts, cloud
            // base and treeline both end up on the wrong side of the mountain.
            Check(EcoConstants.LapseRateSaturatedCPerKm < EcoConstants.LapseRateCPerKm,
                  "saturated lapse rate must be below the environmental rate");
            Check(EcoConstants.LapseRateCPerKm < EcoConstants.LapseRateDryAdiabaticCPerKm,
                  "environmental lapse rate must be below the dry adiabatic rate");

            CheckClose(EcoConstants.TreelineWarmestMonthC, 6.4f,
                       "L-22 Korner treeline isotherm must stay 6.4 C");

            CheckClose(EcoConstants.LclMetresPerCelsius, 125f,
                       "L-06 lifting condensation level must stay 125 m per C of dew point spread");

            CheckClose(EcoConstants.ReinekeExponent, -1.605f,
                       "L-18 Reineke self-thinning exponent must stay -1.605");

            CheckClose(EcoConstants.BeerLambertDefaultK, 0.5f,
                       "L-17 extinction coefficient for spherical leaf distribution must stay 0.5");

            CheckClose(EcoConstants.SaltSprayDecayMetres, 120f,
                       "L-25 salt spray e-folding distance must stay 120 m");

            CheckClose(EcoConstants.AngleOfReposeDegrees, 35f,
                       "angle of repose for soil must stay 35 deg");

            CheckClose(EcoConstants.GddBaseTemperateC, 5f,
                       "L-24 temperate growing-degree base must stay 5 C");

            CheckClose(EcoConstants.EarthAxialTiltDegrees, 23.44f,
                       "L-02 Earth axial tilt must stay 23.44 deg");

            CheckClose(EcoConstants.DaysPerTropicalYear, 365.2422f,
                       "tropical year must stay 365.2422 days");

            CheckClose(EcoConstants.SynodicMonthDays, 29.53059f,
                       "synodic month must stay 29.53059 days");

            CheckClose(EcoConstants.SecondsPerSiderealDay, 86164.0905f,
                       "sidereal day must stay 86164.0905 s");

            CheckClose(EcoConstants.SolarConstantMjPerM2Min, 0.0820f,
                       "L-03 solar constant must stay 0.0820 MJ/m2/min");

            // Unit conversions. Wrong by a factor of ten here and every density in
            // the toolkit is wrong by a factor of ten, with nothing to point at.
            CheckClose(EcoConstants.SquareMetresPerHectare, 10000f,
                       "a hectare must stay 10000 m2");
            CheckClose(EcoConstants.MetresPerKilometre, 1000f,
                       "a kilometre must stay 1000 m");
            CheckClose(EcoConstants.HoursPerDay, 24f, "a day must stay 24 h");
            CheckClose(EcoConstants.MinutesPerDay, 1440f, "a day must stay 1440 min");

            // Sigma is half the authored tolerance, so one tolerance from the
            // optimum reads about 0.135 rather than about 0.607. Which of the two
            // this is decides whether a species' authored range means "the edge of
            // where it grows" or "where it is already gone".
            CheckClose(EcoConstants.ToleranceToSigma, 0.5f,
                       "L-15 tolerance-to-sigma must stay 0.5 (tolerance is the half width)");

            // The warm slope faces the equator. If these two are ever equal, or
            // swapped, every hillside in one hemisphere grows the wrong forest.
            CheckClose(EcoConstants.WarmAspectNorthDeg, 180f,
                       "warm aspect in the northern hemisphere must face south");
            CheckClose(EcoConstants.WarmAspectSouthDeg, 0f,
                       "warm aspect in the southern hemisphere must face north");
        }

        // ---------------------------------------------------------------------
        //  V-02, V-03  THE MATHS
        // ---------------------------------------------------------------------

        private static void TestEcologyMaths()
        {
            TestLiebigIsTheMinimum();
            TestGaussianTolerance();
            TestLapseRate();
            TestBeerLambert();
            TestReinekeAndCrowns();
            TestDensityAndSpacing();
            TestWaterBalance();
            TestDispersalAndSalt();
            TestWarmthAndFrost();
            TestWetnessIndex();
            TestDensityCeilingAndSiteFactor();
            TestInstancingAndColliderRouting();
            TestSeasonalHeightAndCanopy();
            TestWetnessSnowAppearance();
            TestKrummholzDwarfing();
            TestTreelineSofteningBandsThePresence();
            TestBareGroundAssertsNamedEdges();
        }

        /// <summary>
        /// V-27. The routing table, which is the half of instancing that can be checked
        /// without a scene. An instanced draw cannot hold a collider, so every row below
        /// is really the same assertion: collision is never lost silently.
        /// </summary>
        private static void TestInstancingAndColliderRouting()
        {
            EcoEnvelope grass = new EcoEnvelope();
            grass.lifeForm = LifeForm.Graminoid;
            Check(grass.ResolveColliderPolicy() == ColliderPolicy.None,
                  "V-27 Auto must give a graminoid no collider");

            EcoEnvelope shrub = new EcoEnvelope();
            shrub.lifeForm = LifeForm.Shrub;
            Check(shrub.ResolveColliderPolicy() == ColliderPolicy.Trigger,
                  "V-27 Auto must give a shrub a trigger, not a solid collider");

            EcoEnvelope tree = new EcoEnvelope();
            tree.lifeForm = LifeForm.Tree;
            Check(tree.ResolveColliderPolicy() == ColliderPolicy.Solid,
                  "V-27 Auto must give a tree a solid collider");

            EcoEnvelope stone = new EcoEnvelope();
            stone.lifeForm = LifeForm.Abiotic;
            Check(stone.ResolveColliderPolicy() == ColliderPolicy.Solid,
                  "V-27 Auto must give stone a solid collider");

            ColliderPolicy collider;
            bool instanced;

            // Grass needs no collider, so a GameObject would carry nothing: it batches
            // even well under the threshold.
            grass.ResolveRouting(4, 1500, true, out collider, out instanced);
            Check(instanced && collider == ColliderPolicy.None,
                  "V-27 a collider-less species must batch rather than spawn a transform");

            // A tree under budget keeps its collider, and therefore its GameObject.
            tree.ResolveRouting(20, 1500, true, out collider, out instanced);
            Check(!instanced && collider == ColliderPolicy.Solid,
                  "V-27 a tree under budget must stay a GameObject and keep its collider");

            // Past the library threshold the hierarchy guard wins. This is the shipped
            // promise that it batches "regardless of the per-prototype flag", and the
            // reason the done-when's 1500-rock chunk is reachable at all.
            stone.ResolveRouting(1600, 1500, true, out collider, out instanced);
            Check(instanced && collider == ColliderPolicy.None,
                  "V-27 the library threshold must override the collider policy");

            // The count is per prototype, so a species that is itself sparse must not be
            // dragged over the threshold by the crowd its chunk holds.
            stone.ResolveRouting(30, 1500, true, out collider, out instanced);
            Check(!instanced,
                  "V-27 density must be judged per prototype, not per chunk");

            // A spent collider budget is the shrub case: the trigger is gone, so there is
            // no longer a reason to hold the transform.
            shrub.ResolveRouting(50, 1500, false, out collider, out instanced);
            Check(instanced && collider == ColliderPolicy.None,
                  "V-27 a shrub past the collider budget must batch and drop its trigger");

            // Never is an author instruction and outranks the guard.
            EcoEnvelope never = new EcoEnvelope();
            never.lifeForm = LifeForm.Abiotic;
            never.instancingPolicy = InstancingPolicy.Never;
            never.ResolveRouting(5000, 1500, true, out collider, out instanced);
            Check(!instanced, "V-27 InstancingPolicy.Never must never batch");

            // Always may drop a collider the life form merely implied.
            EcoEnvelope always = new EcoEnvelope();
            always.lifeForm = LifeForm.Abiotic;
            always.instancingPolicy = InstancingPolicy.Always;
            always.ResolveRouting(1, 1500, true, out collider, out instanced);
            Check(instanced && collider == ColliderPolicy.None,
                  "V-27 Always must batch from the first instance when the collider is implied");

            // It may not drop one the author named. A player falling through a boulder is
            // the failure this row exists to prevent.
            EcoEnvelope named = new EcoEnvelope();
            named.lifeForm = LifeForm.Abiotic;
            named.instancingPolicy = InstancingPolicy.Always;
            named.colliderPolicy = ColliderPolicy.Solid;
            named.ResolveRouting(1, 1500, true, out collider, out instanced);
            Check(!instanced && collider == ColliderPolicy.Solid,
                  "V-27 an explicitly authored collider must survive Always");
        }

        /// <summary>
        /// V-03, the guard task. The doc's own worked example is the assertion:
        /// seven factors each at 0.8 multiply to 0.21, which reads as a hostile
        /// site although every single factor is good. The minimum returns 0.8.
        /// </summary>
        private static void TestLiebigIsTheMinimum()
        {
            CheckClose(EcoMath.LiebigMinimum(0.9f, 0.1f), 0.1f,
                       "L-16 must return the scarcest factor");

            CheckClose(EcoMath.LiebigMinimum(0.1f, 0.9f), 0.1f,
                       "L-16 must not depend on argument order");

            CheckClose(EcoMath.LiebigMinimum(0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f), 0.8f,
                       "L-16 seven good factors must stay good, not multiply down to 0.21");

            // The multiplicative answer, spelled out, so the failure message says
            // what went wrong rather than just which number differed.
            float product = 1f;
            for (int i = 0; i < 7; i++) product *= 0.8f;
            Check(Mathf.Abs(EcoMath.LiebigMinimum(0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f) - product) > 0.5f,
                  "L-16 has been replaced by a running product");

            // Every arity must agree with every other, or the scatter path and the
            // params path disagree about the same site.
            CheckClose(EcoMath.LiebigMinimum(0.7f, 0.4f, 0.9f), 0.4f, "L-16 three-factor overload");
            CheckClose(EcoMath.LiebigMinimum(0.7f, 0.4f, 0.9f, 0.3f), 0.3f, "L-16 four-factor overload");
            CheckClose(EcoMath.LiebigMinimum(0.7f, 0.4f, 0.9f, 0.3f, 0.6f), 0.3f, "L-16 five-factor overload");
            CheckClose(EcoMath.LiebigMinimum(0.7f, 0.4f, 0.9f, 0.3f, 0.6f, 0.2f), 0.2f, "L-16 six-factor overload");

            CheckClose(EcoMath.LiebigMinimum(new float[] { 0.7f, 0.4f, 0.9f, 0.3f, 0.6f }), 0.3f,
                       "L-16 params overload must agree with the fixed-arity form");

            // Not an average either: 0.9 and 0.1 average to 0.5, which would read
            // as a mediocre site rather than one missing something it needs.
            Check(Mathf.Abs(EcoMath.LiebigMinimum(0.9f, 0.1f) - 0.5f) > 0.1f,
                  "L-16 has been replaced by an average");
        }

        /// <summary>
        /// L-15. The shape of the niche curve, which is what turns an authored
        /// optimum and tolerance into a suitability rather than a hard band.
        /// </summary>
        private static void TestGaussianTolerance()
        {
            CheckClose(EcoMath.GaussianTolerance(10f, 10f, 4f), 1f,
                       "L-15 at the optimum must be exactly 1");

            // One tolerance out, sigma being half the tolerance, gives exp(-2).
            CheckClose(EcoMath.GaussianTolerance(14f, 10f, 4f), Mathf.Exp(-2f),
                       "L-15 one tolerance from the optimum must read exp(-2)");

            CheckClose(EcoMath.GaussianTolerance(6f, 10f, 4f), Mathf.Exp(-2f),
                       "L-15 must be symmetric about the optimum");

            // Monotone away from the optimum. A curve with a second bump would let
            // a species reappear at a temperature it cannot survive.
            float previous = 1f;
            for (int i = 1; i <= 8; i++)
            {
                float s = EcoMath.GaussianTolerance(10f + i, 10f, 4f);
                Check(s < previous, "L-15 must fall monotonically away from the optimum, step " + i);
                previous = s;
            }

            Check(previous > 0f, "L-15 must stay positive rather than clamping to zero");

            // A zero tolerance is an authoring mistake, not a divide by zero.
            float degenerate = EcoMath.GaussianTolerance(10f, 10f, 0f);
            Check(!float.IsNaN(degenerate) && !float.IsInfinity(degenerate),
                  "L-15 with zero tolerance must stay finite");
        }

        /// <summary>L-04. The single most consequential line in the module: it is why mountains are cold.</summary>
        private static void TestLapseRate()
        {
            CheckClose(EcoMath.LapseAdjustedTempC(15f, 0f), 15f,
                       "L-04 at sea level must return the sea-level temperature");

            CheckClose(EcoMath.LapseAdjustedTempC(15f, 1000f), 8.5f,
                       "L-04 must cool 6.5 C over the first kilometre");

            CheckClose(EcoMath.LapseAdjustedTempC(15f, 3000f), -4.5f,
                       "L-04 must reach -4.5 C at 3000 m from a 15 C sea level");

            CheckClose(EcoMath.LapseAdjustedTempC(15f, 1000f, 10f), 5f,
                       "L-04 must honour an authored lapse rate from the weather manager");

            // Below sea level is warmer, not clamped. A rift valley floor really is
            // hotter than the coast, and clamping here would flatten that away.
            Check(EcoMath.LapseAdjustedTempC(15f, -500f) > 15f,
                  "L-04 below sea level must warm rather than clamp");
        }

        /// <summary>L-17. Why an understory exists at all.</summary>
        private static void TestBeerLambert()
        {
            CheckClose(EcoMath.BeerLambertTransmittance(0f), 1f,
                       "L-17 no canopy must pass all light");

            CheckClose(EcoMath.BeerLambertTransmittance(2f, 0.5f), Mathf.Exp(-1f),
                       "L-17 must be exp(-k * LAI)");

            Check(EcoMath.BeerLambertTransmittance(4f) < EcoMath.BeerLambertTransmittance(1f),
                  "L-17 more leaf area must pass less light");

            Check(EcoMath.BeerLambertTransmittance(6f) > 0f,
                  "L-17 a closed canopy must still pass some light, or nothing shade-tolerant can exist");

            // Clamped at the maximum leaf area index rather than running to zero,
            // so an absurd authored LAI cannot make a forest floor pitch black.
            CheckClose(EcoMath.BeerLambertTransmittance(EcoConstants.LeafAreaIndexMax + 50f),
                       EcoMath.BeerLambertTransmittance(EcoConstants.LeafAreaIndexMax),
                       "L-17 must clamp at the maximum leaf area index");
        }

        /// <summary>L-18 and L-19. Why a mature forest is not a plantation.</summary>
        private static void TestReinekeAndCrowns()
        {
            float young = EcoMath.ReinekeMaxStemsPerHa(0.1f);
            float mature = EcoMath.ReinekeMaxStemsPerHa(0.5f);

            Check(mature < young,
                  "L-18 a stand of thicker trunks must carry fewer stems per hectare");

            // The exponent is negative, so this is a power law rather than a line.
            // Five times the diameter must cost far more than five times the stems.
            Check(young / mature > 5f,
                  "L-18 self-thinning must fall as a power law, not linearly");

            Check(EcoMath.ReinekeMaxStemsPerHa(0f) > 0f,
                  "L-18 a zero diameter must clamp rather than divide by zero");

            Check(EcoMath.CrownRadiusM(0.5f) > EcoMath.CrownRadiusM(0.1f),
                  "L-19 a thicker trunk must carry a wider crown");

            Check(EcoMath.CrownRadiusM(0.3f) > 0f, "L-19 crown radius must be positive");
        }

        /// <summary>L-20. Density and spacing are two readings of one fact and must round-trip.</summary>
        private static void TestDensityAndSpacing()
        {
            CheckClose(EcoMath.StemsPerHaToPerSquareMetre(10000f), 1f,
                       "ten thousand stems a hectare is one per square metre");

            CheckClose(EcoMath.StemsPerHaToPerSquareMetre(600f), 0.06f,
                       "600 stems a hectare is 0.06 per square metre");

            float sparse = EcoMath.PoissonRadiusFromDensity(0.01f);
            float dense = EcoMath.PoissonRadiusFromDensity(1f);

            Check(dense < sparse, "L-20 a denser stand must have a smaller spacing radius");

            // sqrt(1 / (pi * rho)) at rho = 1/pi is exactly 1.
            CheckClose(EcoMath.PoissonRadiusFromDensity(1f / Mathf.PI), 1f,
                       "L-20 must be sqrt(1 / (pi * density))");

            float zeroDensity = EcoMath.PoissonRadiusFromDensity(0f);
            Check(!float.IsInfinity(zeroDensity) && !float.IsNaN(zeroDensity),
                  "L-20 zero density must clamp rather than return an infinite radius");
        }

        /// <summary>L-13 and L-14. Why the same rainfall is a desert at one latitude and a wetland at another.</summary>
        private static void TestWaterBalance()
        {
            // Aridity is precipitation over demand, so equal parts is exactly 1 -
            // the boundary between a place that accumulates water and one that owes it.
            CheckClose(EcoMath.AridityIndex(800f, 800f), 1f,
                       "L-14 equal precipitation and demand must give an aridity of 1");

            Check(EcoMath.AridityIndex(200f, 1600f) < EcoMath.AridityIndex(1600f, 800f),
                  "L-14 a drier site must have a lower aridity index");

            Check(EcoMath.AridityIndex(800f, 0f) > 0f,
                  "L-14 zero demand must clamp rather than divide by zero");

            // Hargreaves needs a real temperature range: a day with no swing has
            // no radiation-driven demand to speak of.
            float pet = EcoMath.HargreavesPetMmPerDay(5f, 25f, 15f, 30f);
            Check(pet > 0f, "L-13 PET must be positive for a warm day with a real diurnal range");
            Check(!float.IsNaN(pet), "L-13 PET must not be NaN");

            Check(EcoMath.HargreavesPetMmPerDay(5f, 25f, 15f, 40f) > pet,
                  "L-13 more incoming radiation must raise demand");

            Check(EcoMath.HargreavesPetMmPerDay(5f, 25f, 15f, 0f) <= pet,
                  "L-13 no radiation must not raise demand");

            // A negative range is authoring nonsense, not a negative demand.
            Check(EcoMath.HargreavesPetMmPerDay(25f, 5f, 15f, 30f) >= 0f,
                  "L-13 an inverted temperature range must not produce negative demand");
        }

        /// <summary>L-21 and L-25. Why forests clump, and why the coast is a band rather than an edge.</summary>
        private static void TestDispersalAndSalt()
        {
            CheckClose(EcoMath.DispersalWeight(0f, 25f), 1f,
                       "L-21 a site at the parent must weight 1");

            CheckClose(EcoMath.DispersalWeight(25f, 25f), Mathf.Exp(-1f),
                       "L-21 one lambda out must weight exp(-1)");

            Check(EcoMath.DispersalWeight(100f, 25f) < EcoMath.DispersalWeight(50f, 25f),
                  "L-21 weight must fall with distance");

            CheckClose(EcoMath.DispersalCutoffM(25f), 25f * EcoConstants.DispersalCutoffLambdaMultiple,
                       "L-21 cutoff must be the documented multiple of lambda");

            CheckClose(EcoMath.SaltSprayFraction(0f), 1f,
                       "L-25 at the shoreline salt loading must be 1");

            CheckClose(EcoMath.SaltSprayFraction(EcoConstants.SaltSprayDecayMetres), Mathf.Exp(-1f),
                       "L-25 one e-folding distance inland must read exp(-1)");

            Check(EcoMath.SaltSprayFraction(600f) < 0.01f,
                  "L-25 five decay lengths inland must be effectively salt free");

            Check(EcoMath.SaltSprayFraction(-10f) <= 1f,
                  "L-25 a negative distance must not exceed full loading");
        }

        /// <summary>L-24 and L-26. Warmth accumulated, and the length of the frost-free window.</summary>
        private static void TestWarmthAndFrost()
        {
            CheckClose(EcoMath.GrowingDegreeDays(15f, 5f, 100f), 1000f,
                       "L-24 ten degrees above base for a hundred days is 1000 degree-days");

            CheckClose(EcoMath.GrowingDegreeDays(3f, 5f, 100f), 0f,
                       "L-24 below the base temperature must accumulate nothing");

            Check(EcoMath.GrowingDegreeDays(15f, 5f, 200f) >
                  EcoMath.GrowingDegreeDays(15f, 5f, 100f),
                  "L-24 a longer season must accumulate more warmth");

            // A warm mean with a small swing never freezes; a cold mean with a
            // small swing never thaws. Both are whole years, not fractions.
            CheckClose(EcoMath.FrostFreeFraction(20f, 2f), 1f,
                       "L-26 a warm climate with little swing must be frost free all year");

            CheckClose(EcoMath.FrostFreeFraction(-20f, 2f), 0f,
                       "L-26 a cold climate with little swing must never thaw");

            // A mean of exactly freezing splits the year in half - acos(0)/pi.
            CheckClose(EcoMath.FrostFreeFraction(0f, 12f), 0.5f,
                       "L-26 a mean at freezing must give half a frost-free year");

            Check(EcoMath.FrostFreeFraction(5f, 12f) > 0.5f,
                  "L-26 a mean above freezing must give more than half a year");

            CheckClose(EcoMath.FrostFreeFraction(10f, 0f), 1f,
                       "L-26 no seasonal swing above freezing must be a whole year");

            float fromExtremes = EcoMath.FrostFreeFractionFromExtremes(-6f, 6f);
            Check(fromExtremes >= 0f && fromExtremes <= 1f,
                  "L-26 from extremes must stay a fraction");
        }

        /// <summary>L-12. Why hollows are wet and ridges are dry.</summary>
        private static void TestWetnessIndex()
        {
            float hollow = EcoMath.TopographicWetnessIndex(500f, 0.05f);
            float ridge = EcoMath.TopographicWetnessIndex(5f, 0.05f);

            Check(hollow > ridge, "L-12 a larger catchment must be wetter");

            Check(EcoMath.TopographicWetnessIndex(100f, 0.6f) <
                  EcoMath.TopographicWetnessIndex(100f, 0.05f),
                  "L-12 a steeper slope must shed water and read drier");

            // Flat ground has no slope to divide by. It is also where water
            // genuinely collects, so this must clamp rather than blow up.
            float flat = EcoMath.TopographicWetnessIndex(100f, 0f);
            Check(!float.IsInfinity(flat) && !float.IsNaN(flat),
                  "L-12 flat ground must clamp rather than divide by zero");
        }

        // ---------------------------------------------------------------------
        //  V-37, V-38, V-39  SEASON, WEATHER AND THE TREELINE
        // ---------------------------------------------------------------------

        /// <summary>
        /// V-37. Grass dies back and comes back. The return is the half that a
        /// one-directional "scale it down in winter" implementation gets wrong, so
        /// summer is pinned to the authored height exactly rather than approximately.
        /// </summary>
        private static void TestSeasonalHeightAndCanopy()
        {
            Vector2 range = new Vector2(0.55f, 1f);

            CheckClose(Phenology.SeasonalHeightScale(range, 1f), 1f,
                       "V-37 a full canopy must return grass to its authored height");

            CheckClose(Phenology.SeasonalHeightScale(range, 0f), 0.55f,
                       "V-37 a dormant canopy must stand at the authored winter height");

            float mid = Phenology.SeasonalHeightScale(range, 0.5f);
            Check(mid > 0.55f && mid < 1f,
                  "V-37 grass height must track the season between the two ends");

            // An author who types the pair backwards must get a usable range rather
            // than an inverted one that grows grass in winter.
            Check(Phenology.SeasonalHeightScale(new Vector2(1f, 0.55f), 0f) <= 1f,
                  "V-37 an inverted height range must not invert the season");

            // The plateau is what makes summer a season rather than an instant: the
            // canopy must sit at exactly 1 across the whole leaf-on span.
            CheckClose(Phenology.CanopyFraction(0.5f, 0.2f, 0.7f, 1f), 1f,
                       "V-37 the canopy must hold at full through leaf-on");

            // An evergreen has no dieback at all, and neither does a site so cold it
            // never reached budburst - stripping a conifer bare there is the bug.
            CheckClose(Phenology.CanopyFraction(0.9f, 0.2f, 0.7f, 0f), 1f,
                       "V-37 an evergreen must not lose canopy");
            CheckClose(Phenology.CanopyFraction(0.9f, -1f, -1f, 1f), 1f,
                       "V-37 a site that never leafs out must never shed");
        }

        /// <summary>
        /// V-38. Rain darkens, snow settles on what is standing, and neither is
        /// allowed to touch alpha - a wet leaf is not a transparent one.
        /// </summary>
        private static void TestWetnessSnowAppearance()
        {
            Color leaf = new Color(0.4f, 0.6f, 0.2f, 1f);

            Check(Phenology.WetDarken(leaf, 0f, 1f) == leaf,
                  "V-38 dry ground must leave the leaf colour alone");

            Color wet = Phenology.WetDarken(leaf, 1f, 0.4f);
            Check(wet.r < leaf.r && wet.g < leaf.g && wet.b < leaf.b,
                  "V-38 wetness must darken the foliage");
            CheckClose(wet.a, leaf.a, "V-38 wetness must not change alpha");

            // Interception scales with standing leaf area. This is the difference
            // between a white winter conifer and the grey birch beside it.
            float conifer = Phenology.CanopySnowFraction(1f, 0.5f, 1f);
            float bare = Phenology.CanopySnowFraction(1f, 0.5f, 0.05f);
            Check(conifer > bare,
                  "V-38 a full crown must intercept more snow than a bare one");

            CheckClose(Phenology.CanopySnowFraction(0f, 1f, 1f), 0f,
                       "V-38 no lying snow must mean no snow in the canopy");

            Color snowy = Phenology.SnowBlend(leaf, 1f);
            Check(snowy.r > leaf.r && snowy.b > leaf.b,
                  "V-38 a snow-laden crown must read lighter than the bare leaf");
            CheckClose(snowy.a, leaf.a, "V-38 snow must not change alpha");
            Check(Phenology.SnowBlend(leaf, 0f) == leaf,
                  "V-38 zero canopy snow must leave the colour untouched");
        }

        /// <summary>
        /// V-39. The treeline carries dwarfed scrub rather than ending abruptly. The
        /// assertion that matters is that the multiplier is never zero: a krummholz
        /// band that dwarfs to nothing is the bare ring the task exists to remove.
        /// </summary>
        private static void TestKrummholzDwarfing()
        {
            EcoEnvelope pine = new EcoEnvelope();
            pine.lifeForm = LifeForm.Tree;
            pine.krummholzAboveExposure = true;
            pine.krummholzHeightScale = 0.25f;
            pine.windExposureMax = 0.75f;

            float h, w, b;

            Check(Allometry.Krummholz(pine, 0.9f, out h, out w, out b),
                  "V-39 a flagged species past its exposure limit must go krummholz");
            CheckClose(h, 0.25f, "V-39 the dwarfed stem must take the authored scale");
            Check(w > 1f, "V-39 a krummholz crown must spread rather than shrink");
            Check(b < 1f, "V-39 an already-bent stem must have spent its flexibility");

            Check(!Allometry.Krummholz(pine, 0.5f, out h, out w, out b),
                  "V-39 a sheltered site must grow an ordinary tree");
            CheckClose(h, 1f, "V-39 a sheltered stem must not be dwarfed at all");

            EcoEnvelope oak = new EcoEnvelope();
            oak.lifeForm = LifeForm.Tree;
            oak.krummholzAboveExposure = false;
            oak.windExposureMax = 0.75f;
            Check(!Allometry.Krummholz(oak, 0.99f, out h, out w, out b),
                  "V-39 an unflagged species must not go krummholz");

            // A zero or negative authored scale must clamp to something that still
            // stands, because the whole point is presence-but-dwarfed.
            pine.krummholzHeightScale = 0f;
            Allometry.Krummholz(pine, 0.9f, out h, out w, out b);
            Check(h > 0f, "V-39 krummholz must dwarf the stem, never erase it");

            Check(!Allometry.Krummholz(null, 0.9f, out h, out w, out b),
                  "V-39 a null envelope must not claim krummholz");
        }

        // ---------------------------------------------------------------------
        //  V-25  TREELINE AS A BAND, NOT A CONTOUR LINE  (gate TREE1)
        // ---------------------------------------------------------------------

        /// <summary>
        /// The treeline is the bug the user named: a hard warmest-month compare draws
        /// it as a painted stripe round a mountain. Softening (trees only, opt-in)
        /// turns it into the krummholz band - presence ramps across the band so the
        /// line frays and follows the ground. Four things are pinned: default-off
        /// changes nothing; a warm site is untouched by the band; the band fades a
        /// site that the hard cut rejected outright; and it is tree-only, so a shrub
        /// still takes the hard edge and keeps its ground above the line.
        /// </summary>
        private static void TestTreelineSofteningBandsThePresence()
        {
            float band = EcoConstants.EcotoneTreelineBandC;

            EcoEnvelope tree = PermissiveTree();
            Check(!tree.treelineSoftening,
                  "TREE1 treeline softening must default off, so existing worlds do not shift");

            // A site warm enough to clear the band: the band factor is 1, so turning
            // softening on must leave a warm forest exactly as it was.
            float warmOff = SpeciesSuitability.Evaluate(tree, SiteAtWarmth(tree, tree.warmestMonthMinC + band + 1f));
            tree.treelineSoftening = true;
            float warmOn = SpeciesSuitability.Evaluate(tree, SiteAtWarmth(tree, tree.warmestMonthMinC + band + 1f));
            Check(warmOff > 0.9f, "TREE1 a warm, otherwise-ideal tree site must score near 1");
            CheckClose(warmOn, warmOff, "TREE1 softening must not touch a site above the band");

            // At the old hard line the ramp is at its midpoint: present but half-vigour.
            EcoEnvelope hard = PermissiveTree();            // softening off
            float lineHard = SpeciesSuitability.Evaluate(hard, SiteAtWarmth(hard, hard.warmestMonthMinC));
            float lineSoft = SpeciesSuitability.Evaluate(tree, SiteAtWarmth(tree, tree.warmestMonthMinC));
            Check(lineSoft > 0f && lineSoft < lineHard,
                  "TREE1 at the line the softened tree must be present but weaker than the hard cut");
            CheckClose(lineSoft, 0.5f * lineHard,
                       "TREE1 the band midpoint must halve vigour (smoothstep at t=0.5)");

            // Below the old line, inside the band: the hard cut rejects outright, the
            // band keeps a dwarfed presence. This is the frayed edge the task wants.
            float belowHard = SpeciesSuitability.Evaluate(hard, SiteAtWarmth(hard, hard.warmestMonthMinC - band * 0.5f));
            float belowSoft = SpeciesSuitability.Evaluate(tree, SiteAtWarmth(tree, tree.warmestMonthMinC - band * 0.5f));
            Check(belowHard <= 0f, "TREE1 the hard cut must reject a site below the warmest-month minimum");
            Check(belowSoft > 0f, "TREE1 the band must carry presence below the old line - the krummholz zone");

            // Past the cold edge of the band there is genuinely no tree, softened or not.
            float coldSoft = SpeciesSuitability.Evaluate(tree, SiteAtWarmth(tree, tree.warmestMonthMinC - band - 1f));
            Check(coldSoft <= 0f, "TREE1 below the whole band the tree must still be absent");

            // Tree-only: a shrub survives above the treeline, so the band must not
            // apply to it - it keeps the hard edge at its own stated minimum.
            EcoEnvelope shrub = PermissiveTree();
            shrub.lifeForm = LifeForm.Shrub;
            shrub.treelineSoftening = true;
            float shrubBelow = SpeciesSuitability.Evaluate(shrub, SiteAtWarmth(shrub, shrub.warmestMonthMinC - band * 0.5f));
            Check(shrubBelow <= 0f, "TREE1 the band is a tree concern; a shrub keeps the hard cut");
        }

        /// <summary>A tree envelope with every gate but the treeline opened, so a test
        /// can vary warmest-month temperature alone and read the treeline's effect.</summary>
        private static EcoEnvelope PermissiveTree()
        {
            EcoEnvelope e = new EcoEnvelope();
            e.lifeForm = LifeForm.Tree;
            e.warmestMonthMinC = 6.4f;
            e.tempLethalMinC = -60f; e.tempLethalMaxC = 60f;
            e.frostFreeFractionMin = 0f; e.gddMinRequired = 0f;
            e.useAbsoluteAltitude = false;
            e.rockinessMax = 1f; e.soilDepthMinM = 0f;
            e.saltTolerance = 1f; e.windExposureMax = 1f; e.snowLoadMaxKgPerM2 = 1e9f;
            e.aridityMin = 0f; e.aridityMax = 1e9f;
            e.slopeOptimumDeg = 0f; e.slopeToleranceDeg = 90f;
            e.tidalZone = TidalZone.None;
            e.submergedDepthRangeM = Vector2.zero; e.floodTolerance = 1f;
            e.distanceToWaterMinM = 0f; e.distanceToWaterMaxM = -1f;
            e.waterTableMaxDepthM = -1f;
            e.shadeTolerance = 1f;              // needs no light, so the light factor is 1
            e.aspectPreferenceDeg = -1f;        // no aspect preference
            e.curvaturePreference = 0f;
            return e;
        }

        /// <summary>A site sitting at every one of a species' optima, so the Liebig
        /// minimum is ~1 and only the warmest-month temperature decides the score.</summary>
        private static EnvironmentSample SiteAtWarmth(EcoEnvelope e, float warmestMonthTempC)
        {
            return new EnvironmentSample
            {
                coldestMonthTempC = 0f,
                warmestMonthTempC = warmestMonthTempC,
                frostFreeFraction = 1f,
                gdd = 0f,
                altitudeM = 0f,
                rockiness = 0f, soilDepthM = 10f,
                saltFraction = 0f, windExposure = 0f, snowLoadKgPerM2 = 0f,
                aridity = 0.5f,
                meanAnnualTempC = e.tempOptimumC,
                soilMoisture = e.moistureOptimum,
                annualPrecipMm = e.precipOptimumMm,
                twi = e.twiOptimum,
                slopeDeg = e.slopeOptimumDeg,
                soilFertility = e.soilFertilityOptimum,
                aspectDeg = -1f,
                curvature = 0f,
                submergedDepthM = 0f,
                distanceToWaterM = 100f,
                waterTableDepthM = 0f
            };
        }

        // ---------------------------------------------------------------------
        //  V-24  BARE GROUND IS ASSERTED, NOT INFERRED  (gate BARE1)
        // ---------------------------------------------------------------------

        /// <summary>
        /// The orphan this wiring connects: BareGround was built to give a mountaintop a
        /// snowline you can point at, but nothing called it. Pinned here: the opt-in
        /// defaults off (so existing worlds are untouched); the suppression factor is the
        /// complement of coverage; a sub-freezing site resolves to full permanent-snow
        /// bare; and an otherwise-ideal temperate site stays fully vegetated.
        /// </summary>
        private static void TestBareGroundAssertsNamedEdges()
        {
            Check(!new EcoSampleSettings().assertBareGround,
                  "BARE1 asserting bare ground must default off, so no existing world shifts");

            BareGroundState quarter = new BareGroundState { bareFraction = 0.25f };
            CheckClose(BareGround.VegetationFactor(in quarter), 0.75f,
                       "BARE1 the vegetation factor must be the complement of coverage");

            EcoEnvelope tree = PermissiveTree();

            // Below freezing year-round: permanent snow, the hardest edge there is.
            BareGroundState snow = BareGround.Resolve(SiteAtWarmth(tree, -10f), 45f, null);
            Check(snow.cause == BareGroundCause.PermanentSnow,
                  "BARE1 a sub-freezing warmest month must read as permanent snow");
            CheckClose(snow.bareFraction, 1f, "BARE1 permanent snow must be fully bare");
            CheckClose(BareGround.VegetationFactor(in snow), 0f,
                       "BARE1 nothing stands on permanent snow");

            // Warm, flat, unsalted, sheltered: a vegetated site with no cause acting.
            BareGroundState none = BareGround.Resolve(SiteAtWarmth(tree, 20f), 45f, null);
            Check(none.cause == BareGroundCause.None,
                  "BARE1 an ideal temperate site must have no bare-ground cause");
            CheckClose(BareGround.VegetationFactor(in none), 1f,
                       "BARE1 a vegetated site must not be suppressed");
        }

        // ---------------------------------------------------------------------
        //  V-13  DENSITY FROM SUITABILITY
        // ---------------------------------------------------------------------

        /// <summary>
        /// The V-13 law: stems/ha = authored figure x global dial x suitability x
        /// biome mask. Four things here compile perfectly while being wrong, so all
        /// four are pinned: a manager figure of zero must not fall back to the node's
        /// own density; the global dial must reach the channels the manager has no
        /// field for; it must NOT be applied a second time to the channels it is
        /// already folded into; and the thinning must realise the factor it was given
        /// rather than merely correlate with it.
        /// </summary>
        private static void TestDensityCeilingAndSiteFactor()
        {
            EcoSampleSettings s = new EcoSampleSettings();

            // 60 stems/ha with the dial at a half, as ResolveSettings would leave it.
            s.densityScale = 0.5f;
            s.treeDensityPerM2 = EcoMath.StemsPerHaToPerSquareMetre(60f) * s.densityScale;
            s.grassDensityPerM2 = 4f * s.densityScale;

            const float NodePerSqKm = 100000f;   // 0.1 per m2, deliberately unlike the above

            CheckClose(EcoDensity.CeilingPerSquareMetre("trees", in s, NodePerSqKm),
                       0.003f,
                       "Tree ceiling must be treesPerHectare x dial, read from the manager");

            CheckClose(EcoDensity.CeilingPerSquareMetre("grass", in s, NodePerSqKm),
                       2f,
                       "Grass ceiling must be grassPerSquareMetre x dial");

            // The dial is already inside those two figures. Applying it again would
            // square it, so half a world would become a quarter of one.
            Check(EcoDensity.CeilingPerSquareMetre("trees", in s, NodePerSqKm) > 0.0029f,
                  "The global density dial must not be applied twice to trees");

            // Channels the manager has no per-hectare field for keep the node's
            // figure and still answer to the one dial.
            CheckClose(EcoDensity.CeilingPerSquareMetre("shrubs", in s, NodePerSqKm),
                       0.05f,
                       "Shrub ceiling must be the node's density x the global dial");

            // Zero is an answer. A treeless world must stay treeless.
            EcoSampleSettings none = new EcoSampleSettings();
            none.densityScale = 1f;

            CheckClose(EcoDensity.CeilingPerSquareMetre("trees", in none, NodePerSqKm), 0f,
                       "treesPerHectare of zero must mean no trees, not the node's density");

            // ---- the site term ----------------------------------------------

            CheckClose(EcoDensity.SiteFactor(0.5f, 0.5f), 0.25f,
                       "Site factor must be suitability x biome affinity");

            CheckClose(EcoDensity.SiteFactor(0.9f, 0f), 0f,
                       "A species absent from the biome must place nothing, whatever its suitability");

            CheckClose(EcoDensity.SiteFactor(2f, 2f), 1f,
                       "Site factor must be clamped to one so no site exceeds the ceiling");

            Check(EcoDensity.KeepAtSite(1f, 0.999f),
                  "A perfectly suited site must keep every instance");

            Check(!EcoDensity.KeepAtSite(0f, 0f),
                  "A site suiting nothing must keep nothing, even on a zero hash");

            // ---- the thinning actually delivers the density ------------------
            //
            // The formula is only true if a factor of 0.4 leaves four sites in ten.
            // Hashes, not sequential draws, exactly as the scatter path does it. The
            // sweep covers the full 256 range on both axes because Noise.Random is a
            // permutation table: a shorter sweep samples a window of that table
            // rather than all of it, and windows are not individually uniform.
            Noise thin = new Noise(32452843);
            int kept = 0;
            int total = 0;

            for (int x = 0; x < 256; x++)
            {
                for (int z = 0; z < 256; z++)
                {
                    total++;
                    if (EcoDensity.KeepAtSite(0.4f, thin.Random(x, z))) kept++;
                }
            }

            float realised = total > 0 ? kept / (float)total : 0f;

            // 3 points of slack: this is asserting that the hash is uniform and the
            // comparison is the right way round, not that a quantised table hits 0.4
            // exactly - a 256-entry table can only reach 0.402.
            Check(Mathf.Abs(realised - 0.4f) < 0.03f,
                  "Thinning at a site factor of 0.4 must realise ~40% of the ceiling, got "
                  + realised);

            // And the same set every time, or a rebuild would replant the forest.
            Noise again = new Noise(32452843);
            bool stable = true;

            for (int x = 0; x < 256 && stable; x++)
                for (int z = 0; z < 256 && stable; z++)
                    if (EcoDensity.KeepAtSite(0.4f, again.Random(x, z))
                        != EcoDensity.KeepAtSite(0.4f, thin.Random(x, z)))
                        stable = false;

            Check(stable, "Thinning must be a hash of position, identical on every rebuild");

            // ---- the readout conversion -------------------------------------

            CheckClose(EcoDensity.StemsPerHectare(EcoMath.StemsPerHaToPerSquareMetre(250f)),
                       250f,
                       "stems/ha to stems/m2 and back must be the identity");
        }

        // ---------------------------------------------------------------------
        //  V-04  THE SAMPLE STRUCT
        // ---------------------------------------------------------------------

        /// <summary>
        /// One of these exists per candidate, at tens of thousands of candidates
        /// per chunk, so its size is a budget rather than a detail. Reflected
        /// rather than listed, so adding a reference-type field fails here
        /// immediately instead of showing up as a GC pause on a phone.
        /// </summary>
        private static void TestEnvironmentSampleStaysCheap()
        {
            System.Type t = typeof(EnvironmentSample);

            Check(t.IsValueType, "EnvironmentSample must be a struct, not a class");

            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);

            Check(fields.Length > 0, "EnvironmentSample must expose its fields");

            for (int i = 0; i < fields.Length; i++)
            {
                System.Type ft = fields[i].FieldType;

                // A reference field would put one heap object per candidate behind
                // a struct whose whole purpose is to avoid exactly that.
                Check(ft.IsValueType,
                      "EnvironmentSample." + fields[i].Name + " is a reference type (" + ft.Name + ")");
            }

            // Float, int, sbyte, Vector3 and one enum are blittable, so for those the
            // marshalled size is the managed size rather than an estimate. The one bool
            // is not: it would marshal as a four-byte Win32 BOOL and overstate the
            // struct by three bytes, so it carries [MarshalAs(U1)] to hold the two
            // sizes together. Anyone adding a bool here must do the same or this
            // number stops describing memory.
            int size = Marshal.SizeOf(t);
            Check(size < 160,
                  "EnvironmentSample must stay under 160 bytes, is " + size);
        }

        // ---------------------------------------------------------------------
        //  RESOLVE-ONCE CACHE
        // ---------------------------------------------------------------------

        /// <summary>
        /// The manager resolves its settings once and reuses the answer, because
        /// the scatter pass asks per cell. That cache is only correct if something
        /// drops it when an input moves, so both halves are asserted: that it holds
        /// while nothing has changed, and that it releases when told to.
        ///
        /// A cache that never releases is the worse failure of the two - it edits
        /// a field and changes nothing, with no error to point at.
        /// </summary>
        private static void TestVegetationResolveIsCachedAndInvalidated()
        {
            GameObject go = new GameObject("UCSelfTest_Vegetation");

            try
            {
                UCVegetationSystemManager v = go.AddComponent<UCVegetationSystemManager>();

                EcoSampleSettings first = v.ResolveSettings();

                // Written directly rather than through the inspector, so OnValidate
                // does not run. That is exactly the case the cache must survive:
                // nothing has told it anything changed.
                float original = v.suitabilityThreshold;
                float moved = Mathf.Approximately(original, 0.5f) ? 0.25f : 0.5f;
                v.suitabilityThreshold = moved;

                EcoSampleSettings cached = v.ResolveSettings();
                CheckClose(cached.suitabilityThreshold, first.suitabilityThreshold,
                           "the resolved settings must be cached, not rebuilt per call");

                // And now the release. Without this the cache is a bug rather than
                // an optimisation.
                v.MarkSettingsDirty();

                EcoSampleSettings rebuilt = v.ResolveSettings();
                CheckClose(rebuilt.suitabilityThreshold, moved,
                           "MarkSettingsDirty must force the next resolve to rebuild");

                // The seed must survive a rebuild unchanged, or two builds of one
                // world produce two different forests - C-06.
                Check(rebuilt.seed == first.seed,
                      "the resolved seed must not change across an invalidation");
            }
            finally
            {
                // Immediate, and in a finally: a failed check above must not leave
                // a stray object in the user's scene.
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The foliage colour path must be a repigmentation and not a multiply, and
        /// the cure along a blade must run tip-first.
        /// </summary>
        /// <remarks>
        /// This asserts something that compiles either way and merely looks
        /// disappointing when it is wrong, which is exactly the class of defect
        /// this file exists for.
        ///
        /// A season tint MULTIPLIED into albedo cannot turn a green leaf orange.
        /// Green albedo carries almost no red for an orange tint to scale, so the
        /// operation can only darken, and autumn arrives as dirty green however
        /// carefully the tint is picked. Autumn is a pigment replacement -
        /// chlorophyll resorbed, carotenoids unmasked - so the operation has to be
        /// a hue rotation. The guard against that being quietly reverted is
        /// textual: the multiply form must not reappear in either shader.
        ///
        /// The one-owner checks are here for the same reason. Two copies of the HSV
        /// conversion would drift, and the grass and the leaves would then disagree
        /// about what September looks like.
        /// </remarks>
        private static void TestFoliagePigmentContract()
        {
            const string DetailShader = "UniverseCreator/WindDetail";
            const string LeafShader = "UniverseCreator/VegetationLit";

            string dir = Path.Combine(Application.dataPath, "UniverseCreator/Shaders");

            // --- the shared maths has exactly one owner ----------------------
            string seasonPath = Path.Combine(dir, "UC_Season.hlsl");
            Check(File.Exists(seasonPath), "UC_Season.hlsl is missing from " + dir);

            string season = File.Exists(seasonPath) ? File.ReadAllText(seasonPath) : "";
            string[] owned = { "UCRgbToHsv", "UCHsvToRgb", "UCRepigment", "UCCureAlongBlade" };
            for (int i = 0; i < owned.Length; i++)
                Check(season.Contains(owned[i]),
                      "UC_Season.hlsl must define " + owned[i] + " - it is the single owner");

            // --- and the shaders call it rather than restating it -------------
            string[] files = { "UC_WindDetail.shader", "UC_VegetationLit.shader" };
            for (int i = 0; i < files.Length; i++)
            {
                string path = Path.Combine(dir, files[i]);
                Check(File.Exists(path), files[i] + " is missing from " + dir);
                if (!File.Exists(path)) continue;

                string src = File.ReadAllText(path);

                Check(src.Contains("UCRepigment("),
                      files[i] + " must repigment the season tint, not multiply it");

                // The exact form that was wrong: lerp(albedo, albedo * tint, k).
                Check(!src.Contains("albedo.rgb * tint"),
                      files[i] + " multiplies the season tint into albedo again - a "
                      + "multiply cannot turn green foliage orange, only darker");

                // Calling UCRepigment is fine; owning a second HSV conversion is not.
                Check(!src.Contains("UCRgbToHsv"),
                      files[i] + " restates the HSV conversion instead of using "
                      + "UC_Season.hlsl's");
                Check(!src.Contains("UCHsvToRgb"),
                      files[i] + " restates the HSV conversion instead of using "
                      + "UC_Season.hlsl's");
            }

            string detailSrc = File.Exists(Path.Combine(dir, files[0]))
                ? File.ReadAllText(Path.Combine(dir, files[0])) : "";

            Check(detailSrc.Contains("UCCureAlongBlade("),
                  "UC_WindDetail must cure tip-first through UCCureAlongBlade - a flat "
                  + "_DryBlend over the whole blade never reads as a real sward");

            // One owner for the height fraction too: if the bend and the cure
            // measured the tip differently the blade would bend from one place and
            // straw from another.
            Check(detailSrc.Contains("UCDetailHeightFrac("),
                  "UC_WindDetail must take the blade height from UCDetailHeightFrac");

            // T-W3. The altitude/dryness cure must REPIGMENT toward _DryColor, not
            // multiply the straw colour into the green albedo. A multiply has nowhere
            // to go but darker - green has almost no red for a straw tint to scale -
            // so as the altitude term drove the cure up the hill the sward only ever
            // read as dirtier green, the "grass goes yellow / dried-out up the hill"
            // bug. The season-ON path now routes the cure through the one HSV owner
            // (UCRepigment) exactly as the season tint does. The season-OFF path keeps
            // the cheap multiply, so a material with the tint off still pays no HSV -
            // the case the mobile perf floor needs kept cheap.
            Check(detailSrc.Contains("UCRepigment(albedo.rgb, _DryColor.rgb"),
                  "UC_WindDetail must repigment the dry/altitude cure toward _DryColor "
                  + "(green->gold by hue, blade value kept), not multiply straw into "
                  + "green albedo - the multiply only darkens, which is the 'yellow up "
                  + "the hill' bug");

            // --- the properties the C# side and the inspector rely on ---------
            Shader detail = Shader.Find(DetailShader);
            Shader leaf = Shader.Find(LeafShader);

            Check(detail != null, "shader '" + DetailShader + "' not found");
            Check(leaf != null, "shader '" + LeafShader + "' not found");

            // A shader that failed to compile still resolves and still answers
            // property queries, so this has to be asked separately or every check
            // below passes on a shader that renders magenta.
            if (detail != null)
                Check(!ShaderUtil.ShaderHasError(detail),
                      "shader '" + DetailShader + "' has compile errors");
            if (leaf != null)
                Check(!ShaderUtil.ShaderHasError(leaf),
                      "shader '" + LeafShader + "' has compile errors");

            // A uniform declared in the cbuffer but not in Properties silently
            // stays at zero: no error, no warning, and the feature never runs.
            string[] detailProps = { "_DryColor", "_DryBlend", "_DryGradient", "_LeafOffFraction" };
            for (int i = 0; i < detailProps.Length; i++)
                Check(HasShaderProperty(detail, detailProps[i]),
                      DetailShader + " must declare " + detailProps[i]);

            Check(HasShaderProperty(leaf, "_LeafOffFraction"),
                  LeafShader + " must declare _LeafOffFraction - the same envelope "
                  + "figure the detail shader cures by, under the same name");

            // Both colour paths live behind this keyword. A keyword no #pragma
            // declares can be enabled all day and no #ifdef will ever see it.
            Check(HasShaderKeyword(detail, "_SEASONTINT_ON"),
                  DetailShader + " has no _SEASONTINT_ON in its keyword space");
            Check(HasShaderKeyword(leaf, "_SEASONTINT_ON"),
                  LeafShader + " has no _SEASONTINT_ON in its keyword space");

            // --- one owner per effect, on the terrain detail path --------------
            //
            // Unity hands DetailPrototype.healthyColor to the shader as vertex
            // colour and the shader multiplies it in, so the CPU tint cannot
            // repigment - only darken. Both sides writing the season is the failure
            // this guards: applied twice, an autumn meadow in rain goes near black.
            string applier = Path.Combine(
                Application.dataPath,
                "UniverseCreator/Core/Runtime/Terrain/ScatterApplier.cs");

            Check(File.Exists(applier), "ScatterApplier.cs is missing from " + applier);

            if (File.Exists(applier))
            {
                string src = File.ReadAllText(applier);

                Check(src.Contains("DetailShaderOwnsSeason("),
                      "ScatterApplier must decide the season owner in code - leaving it "
                      + "to whoever remembers to zero two material properties by hand "
                      + "is how the season gets applied twice");

                Check(src.Contains("if (!DetailShaderOwnsSeason(def, veg))"),
                      "ScatterApplier writes the seasonal prototype colours without "
                      + "checking whether the shader already owns them");

                // The height scale is deliberately NOT inside that gate: no shader
                // can resize a detail prototype, so the CPU owns height either way.
                int gate = src.IndexOf("if (!DetailShaderOwnsSeason(def, veg))");
                int height = src.IndexOf("SeasonalHeightScale(");
                Check(height >= 0 && gate >= 0 && height < gate,
                      "ScatterApplier must apply SeasonalHeightScale before the colour "
                      + "gate - only the CPU can resize a prototype, so height is not "
                      + "the shader's to own");
            }
        }

        /// <summary>
        /// The read path from the weather branch to vegetation: an EnvironmentSampler
        /// takes its annual-precipitation baseline from the chunk climate normals when a
        /// weather branch produced them, and falls back to the resolved settings when it
        /// did not. The two figures are distinct on purpose - a sampler that ignored the
        /// attached normals would keep reporting the settings figure and pass a laxer
        /// test than this one. The tail asserts the OutputBag half: the field exists and
        /// a recycled bag drops it, so no chunk inherits the last one's year.
        /// </summary>
        private static void TestSamplerReadsClimateFromBag()
        {
            const float SettingsPrecip = 700f;
            const float ClimatePrecip = 1850f;

            EcoSampleSettings settings = new EcoSampleSettings();
            settings.annualPrecipMm = SettingsPrecip;
            settings.radiationMjPerM2Day = 20f;   // a sane figure so PET stays finite
            settings.seaLevelMeanTempC = 15f;
            settings.diurnalTempRangeC = 10f;

            // No moisture mask, so the spatial factor is exactly one and the sampled
            // annual precip equals the baseline - which is what makes the two figures
            // directly comparable rather than merely correlated.
            EnvironmentSampler sampler = new EnvironmentSampler();
            sampler.Bind(in settings, null, null, null, null, 0, null);

            Check(sampler.ChunkClimate == null,
                  "A freshly bound sampler must carry no chunk climate");

            EnvironmentSample bare = sampler.Sample(0f, 0f);
            Check(IsFinite(bare.annualPrecipMm), "Sampled annual precip must be finite");
            CheckClose(bare.annualPrecipMm, SettingsPrecip,
                       "With no normals attached the baseline must be the settings figure");

            // Built by hand rather than through Build: this test is about which number
            // the sampler reads, not about the climate model that produced it.
            UniverseCreator.Weather.ClimateNormals normals =
                new UniverseCreator.Weather.ClimateNormals();
            normals.annualPrecipMm = ClimatePrecip;

            sampler.AttachClimate(normals);
            Check(sampler.ChunkClimate == normals,
                  "AttachClimate must expose the attached normals through ChunkClimate");
            // __W58_READPATH_TAIL__
            EnvironmentSample wet = sampler.Sample(0f, 0f);
            CheckClose(wet.annualPrecipMm, ClimatePrecip,
                       "An attached chunk climate must set the annual-precip baseline, "
                       + "not the resolved settings");

            // Detaching returns the sampler to the settings figure, so a recycled
            // sampler cannot leak the last chunk's climate into the next one.
            sampler.AttachClimate(null);
            EnvironmentSample dryAgain = sampler.Sample(0f, 0f);
            CheckClose(dryAgain.annualPrecipMm, SettingsPrecip,
                       "A null AttachClimate must fall the baseline back to settings");

            // The OutputBag half of the contract: the field exists and a cleared bag
            // drops it, so the next chunk reusing the bag starts with no stale year.
            UniverseCreator.Nodes.TileData tile = new UniverseCreator.Nodes.TileData(
                new Coord(0, 0),
                new CoordRect(new Coord(0, 0), new Coord(4, 4)),
                Vector3.zero, new Vector3(100f, 10f, 100f), 10f);

            tile.outputs.climateNormal = normals;
            Check(tile.outputs.climateNormal != null,
                  "OutputBag must carry the chunk climate normal");

            tile.ClearOutputs();
            Check(tile.outputs.climateNormal == null,
                  "A recycled OutputBag must not keep the last chunk's climate normal");
        }

        /// <summary>
        /// W1. HydroField is ecology's first real water awareness: before it, ctx.water
        /// was never filled by any builder, so every site read "no water in range". This
        /// asserts, on a hand-authored coarse world and WITHOUT standing up a bake, that
        /// the field tells ocean from lake from dry ground, keeps the ocean flat at the
        /// sea plane while a lake pools at its own spill level, measures fresh water as
        /// nearer than salt where a lake sits inland of the coast - and that the sampler
        /// reads all of that through its hydro branch, where the empty probe path could
        /// only ever report nothing.
        /// </summary>
        private static void TestHydroFieldWaterAwareness()
        {
            const int res = 8;
            const float mppSide = 700f;      // 100 m per coarse cell across 7 gaps
            const float sea = 0.35f;         // ecology sea plane (worldHeight = 1)
            const float land = 0.6f;         // dry plateau, above the waterline
            const float oceanH = 0.1f;       // edge-connected trench, below sea -> ocean
            const float lakeGround = 0.5f;   // a pit floor, still above sea -> not ocean
            const float lakeSurface = 0.55f; // pooled 0.05 above the floor -> a lake

            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos = Vector3.zero;
            height.worldSize = new Vector3(mppSide, 1f, mppSide);
            height.worldHeight = 1f;

            for (int i = 0; i < height.arr.Length; i++) height.arr[i] = land;

            // Two full columns of edge-connected trench flood from the border as ocean.
            for (int z = 0; z < res; z++)
            {
                height.arr[z * res + 0] = oceanH;
                height.arr[z * res + 1] = oceanH;
            }

            // One interior pit, walled off from every edge by land, pools as a fresh lake.
            int lakeCell = 4 * res + 5;      // (x=5, z=4) -> world (500, 400)
            height.arr[lakeCell] = lakeGround;

            float[] filled = (float[])height.arr.Clone();
            filled[lakeCell] = lakeSurface;

            UniverseCreator.Nodes.WorldDrainage wd =
                UniverseCreator.Nodes.WorldDrainage.CreateForTests(height, filled);
            Check(wd != null, "W1: could not build a test WorldDrainage");
            if (wd == null) return;

            HydroField field = HydroField.For(wd, sea);
            Check(field != null, "W1: HydroField.For must derive a field from a solve");
            if (field == null) return;

            Check(field.HasAnyWater, "W1: a world with an ocean and a lake must report water");

            // ---- the ocean: at the water, and flat at the sea plane ----
            Check(field.NearestKind(0f, 400f) == UniverseCreator.Nodes.WaterBodyKind.Ocean,
                  "W1: the edge-connected trench must classify as ocean");
            Check(field.DistanceToWaterM(0f, 400f) <= 1f,
                  "W1: a point in the ocean must read as at the water, not distant");
            CheckClose(field.WaterSurfaceY(0f, 400f), sea,
                       "W1: the ocean surface must sit at the flat sea plane");

            // ---- the lake: fresh, pooled at its own spill level ----
            Check(field.NearestKind(500f, 400f) == UniverseCreator.Nodes.WaterBodyKind.Lake,
                  "W1: the interior pit must classify as a lake, not ocean");
            Check(field.DistanceToWaterM(500f, 400f) <= 1f,
                  "W1: a point in the lake must read as at the water");
            CheckClose(field.WaterSurfaceY(500f, 400f), lakeSurface,
                       "W1: the lake surface must pool at its own level, not the sea plane");

            // ---- fresh water is nearer than salt where a lake sits inland ----
            float dAnyInland = field.DistanceToWaterM(400f, 400f);
            float dSaltInland = field.DistanceToSaltWaterM(400f, 400f);
            Check(dAnyInland > 0f, "W1: a dry inland point must stand at a distance from water");
            Check(dSaltInland > dAnyInland,
                  "W1: the nearest water inland is the fresh lake, so salt must be farther");

            Check(field.DistanceToSaltWaterM(200f, 400f) < field.DistanceToSaltWaterM(700f, 400f),
                  "W1: salt distance must grow away from the ocean");

            // ---- and the sampler reads all of that through its hydro branch ----
            EcoSampleSettings s = new EcoSampleSettings();
            s.seaLevelY = sea;
            s.waterSearchRadiusM = 500f;
            s.saltSprayDecayM = EcoConstants.SaltSprayDecayMetres;   // 120 m
            s.radiationMjPerM2Day = 20f;   // keep the thermal / PET fills finite
            s.seaLevelMeanTempC = 15f;
            s.diurnalTempRangeC = 10f;

            EnvironmentSampler sampler = new EnvironmentSampler();
            sampler.Bind(in s, height, null, null, null, 0, null);

            // With no field attached the pre-W1 probe path is all there is, and no builder
            // fills it, so it must honestly report no water rather than invent some.
            EnvironmentSample beforeAttach = sampler.Sample(0f, 400f);
            Check(!beforeAttach.hasWaterInRange,
                  "W1: without the hydrology field the empty probe path must report no water");

            sampler.AttachHydrology(field);

            EnvironmentSample oceanPt = sampler.Sample(0f, 400f);
            Check(oceanPt.hasWaterInRange,
                  "W1: the sampler must see the ocean once the field is attached");
            Check(oceanPt.submergedDepthM > 0f, "W1: a point in the ocean must read submerged");
            Check(oceanPt.nearestWaterKind == UniverseCreator.Nodes.WaterBodyKind.Ocean,
                  "W1: the submerged ocean point must read as ocean");
            CheckClose(oceanPt.saltFraction, 1f, "W1: seawater submersion must read fully saline");

            EnvironmentSample lakePt = sampler.Sample(500f, 400f);
            Check(lakePt.submergedDepthM > 0f, "W1: a point in the lake must read submerged");
            Check(lakePt.nearestWaterKind == UniverseCreator.Nodes.WaterBodyKind.Lake,
                  "W1: the submerged lake point must read as a lake");
            Check(lakePt.saltFraction < oceanPt.saltFraction,
                  "W1: a freshwater lake must read less saline than the sea");

            EnvironmentSample inlandPt = sampler.Sample(400f, 400f);
            Check(inlandPt.hasWaterInRange,
                  "W1: the lake is within the search radius of the inland point");
            Check(inlandPt.submergedDepthM <= 0f, "W1: a dry inland point must not read submerged");
            Check(inlandPt.distanceToWaterM > 0f && inlandPt.distanceToWaterM <= s.waterSearchRadiusM,
                  "W1: the inland point must read a positive, in-range distance to water");

            // ---- a world entirely above sea level with no basins holds no water ----
            Matrix dryWorld = new Matrix(new CoordRect(0, 0, res, res));
            dryWorld.worldPos = Vector3.zero;
            dryWorld.worldSize = new Vector3(mppSide, 1f, mppSide);
            dryWorld.worldHeight = 1f;
            for (int i = 0; i < dryWorld.arr.Length; i++) dryWorld.arr[i] = land;

            UniverseCreator.Nodes.WorldDrainage dryWd =
                UniverseCreator.Nodes.WorldDrainage.CreateForTests(dryWorld, null);
            HydroField dryField = HydroField.For(dryWd, sea);
            Check(dryField != null && !dryField.HasAnyWater,
                  "W1: a world with no ocean and no basins must hold no water");
        }

        /// <summary>
        /// W3. The hydrological exclusion kernel: pure routing of a site to land,
        /// marine soft sediment, marine hard substrate, or nothing - and the bed-flow
        /// reading it consumes. Asserts, without a bake, that dry ground stays
        /// terrestrial and untouched, a calm submerged bed reads soft while a
        /// current-scoured one reads rock (raising rockiness so the existing gate turns
        /// rooting species away), the scour fades in from the shore rather than drawing
        /// a hard line, a slope past the limit is rejected wet or dry, and the flow
        /// proxy is higher in a drainage channel than on a ridge yet zero when the solve
        /// carried no flow at all.
        /// </summary>
        private static void TestHydroExclusion()
        {
            const float deep = 5f;      // well past the half-metre bed fade
            const float maxSlope = 45f;

            // ---- dry ground is terrestrial and its rockiness is never touched ----
            Check(HydroExclusion.BedScour01(0f, 1f) == 0f,
                  "W3: dry ground carries no bed scour even under a strong flow");
            CheckClose(HydroExclusion.SubmergedRockiness(0.3f, 0f, 1f), 0.3f,
                       "W3: dry ground keeps its own rockiness unchanged");
            Check(HydroExclusion.Classify(0f, 10f, 0f, maxSlope) == HydroRoute.Terrestrial,
                  "W3: standable dry ground routes to terrestrial slots");
            Check(HydroExclusion.Classify(0f, 60f, 0f, maxSlope) == HydroRoute.Reject,
                  "W3: a dry slope past the limit carries no plant");

            // ---- a calm deep bed is soft sediment; rockiness stays put ----
            Check(HydroExclusion.BedScour01(deep, 0f) == 0f,
                  "W3: still water scours nothing, however deep");
            CheckClose(HydroExclusion.SubmergedRockiness(0.2f, deep, 0f), 0.2f,
                       "W3: a calm submerged bed keeps its base rockiness");
            Check(HydroExclusion.Classify(deep, 5f, 0f, maxSlope) == HydroRoute.MarineSediment,
                  "W3: a calm deep flat bed routes to marine soft-sediment slots");

            // ---- a scoured bed reads as rock, and the reject gate then does its job ----
            float scour = HydroExclusion.BedScour01(deep, 1f);
            Check(scour > 0.9f, "W3: a strong current scours a deep bed almost fully to rock");
            Check(HydroExclusion.SubmergedRockiness(0.2f, deep, 1f) > 0.9f,
                  "W3: the scoured bed raises rockiness so the existing gate rejects rooters");
            Check(HydroExclusion.Classify(deep, 5f, 1f, maxSlope) == HydroRoute.MarineHardSubstrate,
                  "W3: a scoured flat bed routes to holdfast (hard-substrate) slots");

            // ---- the scour fades in from the shore, and grows with flow and depth ----
            Check(HydroExclusion.BedScour01(0.05f, 1f) < 0.1f,
                  "W3: right at the shoreline even a full flow scours almost nothing (no hard line)");
            Check(HydroExclusion.BedScour01(deep, 0.8f) > HydroExclusion.BedScour01(deep, 0.3f),
                  "W3: scour grows with the flow across the bed");
            Check(HydroExclusion.BedScour01(0.4f, 1f) > HydroExclusion.BedScour01(0.15f, 1f),
                  "W3: scour grows with depth through the bed-fade band");

            // ---- MAX, not add: an already-rocky bed is not doubly rocked by calm water ----
            CheckClose(HydroExclusion.SubmergedRockiness(0.9f, deep, 0f), 0.9f,
                       "W3: a calm bed that is already rocky keeps that rockiness, not more");

            // ---- a submerged slope past the limit is rejected regardless of flow ----
            Check(HydroExclusion.Classify(deep, 60f, 0f, maxSlope) == HydroRoute.Reject,
                  "W3: a submerged cliff carries no rooted plant either");

            // ---- the flow proxy: higher in a channel than on a ridge, in 0..1 ----
            const int res = 8;
            const float mppSide = 700f;   // 100 m per coarse cell, as the W1 test uses
            Matrix flowH = new Matrix(new CoordRect(0, 0, res, res));
            flowH.worldPos = Vector3.zero;
            flowH.worldSize = new Vector3(mppSide, 1f, mppSide);
            flowH.worldHeight = 1f;
            for (int i = 0; i < flowH.arr.Length; i++) flowH.arr[i] = 0.5f;

            // A trunk channel down column x=4 carries orders more upstream area than the
            // ridge column x=0, exactly the spread the log transform is there to handle.
            float[] accum = new float[res * res];
            for (int i = 0; i < accum.Length; i++) accum[i] = 1f;
            for (int z = 0; z < res; z++) accum[z * res + 4] = 1000f;

            UniverseCreator.Nodes.WorldDrainage flowWd =
                UniverseCreator.Nodes.WorldDrainage.CreateForTests(flowH, null, accum);
            HydroField flowField = HydroField.For(flowWd, 0.35f);
            Check(flowField != null, "W3: could not derive a HydroField for the flow test");
            if (flowField != null)
            {
                float chan = flowField.BedFlow01(400f, 350f);  // x=4 column: the channel
                float ridge = flowField.BedFlow01(0f, 350f);   // x=0 column: the ridge
                Check(chan >= 0f && chan <= 1f && ridge >= 0f && ridge <= 1f,
                      "W3: the bed-flow proxy must stay within 0..1");
                Check(chan > ridge,
                      "W3: bed flow must read higher in the channel than on the ridge");
            }

            // ---- and where the solve carried no flow, the bed simply reads calm ----
            UniverseCreator.Nodes.WorldDrainage noFlowWd =
                UniverseCreator.Nodes.WorldDrainage.CreateForTests(flowH, null);
            HydroField noFlow = HydroField.For(noFlowWd, 0.35f);
            Check(noFlow != null && noFlow.BedFlow01(400f, 350f) == 0f,
                  "W3: with no flow-accumulation field the bed reads zero flow, not an error");
        }

        private static bool HasShaderProperty(Shader shader, string property)
        {
            if (shader == null) return false;

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                if (shader.GetPropertyName(i) == property) return true;

            return false;
        }

        private static bool HasShaderKeyword(Shader shader, string keyword)
        {
            if (shader == null) return false;

            string[] names = shader.keywordSpace.keywordNames;
            for (int i = 0; i < names.Length; i++)
                if (names[i] == keyword) return true;

            return false;
        }
    }
}
