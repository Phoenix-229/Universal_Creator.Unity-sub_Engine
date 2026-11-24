// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  Module 3: Living World  (Refinement #7)
//
//  File:    UCSelfTest.WaterWeather.cs
//  Purpose: The CPU-side math of the weather->water wire (W7) and the quality-tier
//           table (W8), pinned headlessly. Everything here is a pure function or a
//           read of a pure table, so a self test can nail the LAW each one claims
//           without a GPU, a play session or a device.
//
//  WHY THESE, AND WHY ONLY THESE
//  -----------------------------
//  The shader closure, the render-target allocation and "does it feel like a storm"
//  are the user's in-editor / on-device verdict - a batchmode PASS is not a claim
//  about any of them. What CAN be pinned here is the arithmetic that decides how big
//  the swell gets, how far a lake freezes, how murky a storm sea is, how many rain
//  drops a window stipples, and exactly which ripple tier / octave ceiling / caustic
//  gate each quality tier resolves to. A wrong sign or a transposed row in any of
//  those compiles perfectly and produces a world that is quietly wrong, so each is
//  asserted against the value its own comment promises.
//
//  DEGRADE, NEVER DROP - pinned, not just intended: every reachable quality row is
//  checked to keep interactive water (ripple != Off) and at least one wave train
//  (ceiling >= 1), and the octave cap is checked to be a Min against the authored
//  count (so a lake is never RAISED and the ocean is never dropped below one train).
//
//  Same partial class as the rest of the suite, so these land in the one tally and
//  share the Check / CheckWithin helpers.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // =====================================================================
        //  W8  THE QUALITY-TIER TABLE  (WaterQuality.For)
        // =====================================================================

        /// <summary>
        /// Every row of WaterQuality.For, desktop and mobile, against the table its own
        /// file documents - ripple tier, wave-octave ceiling, underwater-effect gate. A
        /// transposed row is invisible at runtime (the water still renders, just at the
        /// wrong budget for the device), so it is pinned here.
        /// </summary>
        private static void TestWaterQualityTierTable()
        {
            // Desktop: the full ladder, topping out at four wave trains + caustics.
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.Low,    false, UCWaterRippleSim.RippleTier.Low,  2f, 0f, "desktop Low");
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.Medium, false, UCWaterRippleSim.RippleTier.Mid,  3f, 0f, "desktop Medium");
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.High,   false, UCWaterRippleSim.RippleTier.High, 4f, 1f, "desktop High");
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.Ultra,  false, UCWaterRippleSim.RippleTier.High, 4f, 1f, "desktop Ultra");

            // Mobile: High and Ultra collapse onto one flagship-phone row, cheaper than
            // desktop High - three trains, not four.
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.Low,    true,  UCWaterRippleSim.RippleTier.Low,  1f, 0f, "mobile Low");
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.Medium, true,  UCWaterRippleSim.RippleTier.Mid,  2f, 0f, "mobile Medium");
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.High,   true,  UCWaterRippleSim.RippleTier.High, 3f, 1f, "mobile High");
            CheckWaterQualityRow(AdaptiveQuality.QualityTier.Ultra,  true,  UCWaterRippleSim.RippleTier.High, 3f, 1f, "mobile Ultra");

            AdaptiveQuality.QualityTier[] tiers =
            {
                AdaptiveQuality.QualityTier.Ultra, AdaptiveQuality.QualityTier.High,
                AdaptiveQuality.QualityTier.Medium, AdaptiveQuality.QualityTier.Low
            };

            for (int t = 0; t < tiers.Length; t++)
            {
                for (int m = 0; m <= 1; m++)
                {
                    bool mobile = m == 1;
                    WaterQuality q = WaterQuality.For(tiers[t], mobile);
                    string where = (mobile ? "mobile " : "desktop ") + tiers[t];
                    Check(q.screenSpaceEffects == (tiers[t] != AdaptiveQuality.QualityTier.Low),
                          "UC-T04-08 " + where + " camera depth/refraction follows the quality ceiling without removing local bathymetry.");

                    // DEGRADE, NEVER DROP: no reachable tier removes a feature outright.
                    Check(q.ripple != UCWaterRippleSim.RippleTier.Off,
                          "W8 " + where + " must keep interactive water, never switch the ripple off");
                    Check(q.waveOctaveCeiling >= 1f,
                          "W8 " + where + " must keep at least one ambient wave train (got "
                          + q.waveOctaveCeiling + ")");
                    Check(q.underwaterEffectScale >= 0f && q.underwaterEffectScale <= 1f,
                          "W8 " + where + " underwater effect gate must be a 0..1 multiplier (got "
                          + q.underwaterEffectScale + ")");

                    // Mobile-first: the phone row is never heavier than the desktop row of
                    // the same tier.
                    Check(WaterQuality.For(tiers[t], true).waveOctaveCeiling
                          <= WaterQuality.For(tiers[t], false).waveOctaveCeiling,
                          "W8 the mobile budget for " + tiers[t]
                          + " must not exceed the desktop budget");
                }
            }
        }

        private static void CheckWaterQualityRow(
            AdaptiveQuality.QualityTier tier, bool mobile,
            UCWaterRippleSim.RippleTier expectedRipple,
            float expectedCeiling, float expectedEffect, string label)
        {
            WaterQuality q = WaterQuality.For(tier, mobile);

            Check(q.ripple == expectedRipple,
                  "W8 " + label + " must run ripple tier " + expectedRipple
                  + " (got " + q.ripple + ")");
            CheckWithin(q.waveOctaveCeiling, expectedCeiling, 1e-4f,
                        "W8 " + label + " wave-octave ceiling");
            CheckWithin(q.underwaterEffectScale, expectedEffect, 1e-4f,
                        "W8 " + label + " underwater effect scale");
        }

        // =====================================================================
        //  W8  THE OCTAVE CAP IS A MIN, NOT A SET
        // =====================================================================

        /// <summary>
        /// The wave-train count the driver writes is Min(authored-for-kind, tier ceiling).
        /// That is the whole of "degrade, never drop": a lower tier can only TRIM a body's
        /// authored spectrum, never raise a calm body to a busy one, and never take any
        /// body below a single train. Checked with the exact expression the driver uses.
        /// </summary>
        private static void TestWaterOctaveCap()
        {
            // The authored spectrum by kind, straight from the one table the driver reads.
            CheckWithin(UCWaterBody.WaveOctavesFor(WaterBodyKind.Ocean), 4f, 0f, "authored ocean octaves");
            CheckWithin(UCWaterBody.WaveOctavesFor(WaterBodyKind.Lake),  2f, 0f, "authored lake octaves");
            CheckWithin(UCWaterBody.WaveOctavesFor(WaterBodyKind.River), 2f, 0f, "authored river octaves");
            CheckWithin(UCWaterBody.WaveOctavesFor(WaterBodyKind.Pool),  1f, 0f, "authored pool octaves");
            CheckWithin(UCWaterBody.WaveOctavesFor(WaterBodyKind.Swamp), 1f, 0f, "authored swamp octaves");

            // A lake authored at 2 under a desktop-High ceiling of 4 stays at 2 - the cap
            // never RAISES a calm body toward the ceiling.
            float lakeHigh = Mathf.Min(UCWaterBody.WaveOctavesFor(WaterBodyKind.Lake),
                                       WaterQuality.For(AdaptiveQuality.QualityTier.High, false).waveOctaveCeiling);
            CheckWithin(lakeHigh, 2f, 0f,
                        "W8 a lake must never be raised above its authored two trains by a ceiling of four");

            // The ocean's four-train spectrum IS trimmed on a lower tier - Min(4, 2) = 2.
            float oceanLow = Mathf.Min(UCWaterBody.WaveOctavesFor(WaterBodyKind.Ocean),
                                       WaterQuality.For(AdaptiveQuality.QualityTier.Low, false).waveOctaveCeiling);
            CheckWithin(oceanLow, 2f, 0f,
                        "W8 the ocean spectrum must be trimmed to the low desktop ceiling");

            // Mobile Low trims even the ocean to a single train - but the single train
            // survives; the sea still swells.
            float oceanMobileLow = Mathf.Min(UCWaterBody.WaveOctavesFor(WaterBodyKind.Ocean),
                                             WaterQuality.For(AdaptiveQuality.QualityTier.Low, true).waveOctaveCeiling);
            CheckWithin(oceanMobileLow, 1f, 0f,
                        "W8 mobile Low must trim the ocean to one train, and keep that one");

            // The invariant across every kind and tier: capped is in [1, authored].
            WaterBodyKind[] kinds =
            {
                WaterBodyKind.Ocean, WaterBodyKind.Lake, WaterBodyKind.River,
                WaterBodyKind.Pool, WaterBodyKind.Swamp
            };
            AdaptiveQuality.QualityTier[] tiers =
            {
                AdaptiveQuality.QualityTier.Ultra, AdaptiveQuality.QualityTier.High,
                AdaptiveQuality.QualityTier.Medium, AdaptiveQuality.QualityTier.Low
            };

            for (int k = 0; k < kinds.Length; k++)
            {
                float authored = UCWaterBody.WaveOctavesFor(kinds[k]);
                for (int t = 0; t < tiers.Length; t++)
                {
                    for (int m = 0; m <= 1; m++)
                    {
                        float ceiling = WaterQuality.For(tiers[t], m == 1).waveOctaveCeiling;
                        float capped = Mathf.Min(authored, ceiling);

                        Check(capped >= 1f,
                              "W8 at least one wave train must survive for " + kinds[k]
                              + " on " + tiers[t] + " (got " + capped + ")");
                        Check(capped <= authored + 1e-4f,
                              "W8 the cap must never raise " + kinds[k]
                              + " above its authored " + authored + " trains (got " + capped + ")");
                    }
                }
            }
        }

        // =====================================================================
        //  W7  THE WEATHER->WATER MATH  (UCWaterWeatherCoupling)
        // =====================================================================

        /// <summary>
        /// The five pure functions the weather wire is built from. Each is a handful of
        /// clamps and a lerp that compiles whatever the signs are; a wrong one produces a
        /// sea that shrinks in a storm, a lake that ices in summer or a downpour that
        /// clears the water - none of which throws. So each is pinned to the physical law
        /// its own comment states.
        /// </summary>
        private static void TestWaterWeatherCouplingMath()
        {
            // ---- storm swell multiplier: calm 1, full gale 3, clamped both ends -----
            CheckWithin(UCWaterWeatherCoupling.StormAmpFor(0f), 1f, 1e-4f,
                        "W7 a calm sea sits at the baseline swell of one");
            CheckWithin(UCWaterWeatherCoupling.StormAmpFor(1f), 3f, 1e-4f,
                        "W7 a full gale tops out at three times the swell");
            CheckWithin(UCWaterWeatherCoupling.StormAmpFor(0.5f), 2f, 1e-4f,
                        "W7 half a storm sits halfway up the swell range");
            CheckWithin(UCWaterWeatherCoupling.StormAmpFor(5f), 3f, 1e-4f,
                        "W7 a runaway storm value must clamp at the cap, not tear through the bank");
            CheckWithin(UCWaterWeatherCoupling.StormAmpFor(-1f), 1f, 1e-4f,
                        "W7 a negative storm value must clamp to calm");

            float prevAmp = UCWaterWeatherCoupling.StormAmpFor(0f);
            for (float s = 0f; s <= 1.0001f; s += 0.1f)
            {
                float amp = UCWaterWeatherCoupling.StormAmpFor(s);
                Check(amp >= prevAmp - 1e-4f && amp >= 1f && amp <= 3f,
                      "W7 the swell must rise monotonically inside one to three (storm "
                      + s + " gave " + amp + ")");
                prevAmp = amp;
            }

            // ---- ice cover fraction: open at 0 degC, locked four degrees down --------
            CheckWithin(UCWaterWeatherCoupling.IceCoverFraction01(0f), 0f, 1e-4f,
                        "W7 water is open at the freezing point");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFraction01(-4f), 1f, 1e-4f,
                        "W7 four degrees of frost locks the sheet fully");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFraction01(-2f), 0.5f, 1e-4f,
                        "W7 the sheet ramps in, half formed two degrees down");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFraction01(5f), 0f, 1e-4f,
                        "W7 warm water never ices");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFraction01(-100f), 1f, 1e-4f,
                        "W7 a deep freeze clamps at fully iced, not past it");

            // ---- only still fresh water freezes -------------------------------------
            Check(UCWaterWeatherCoupling.FreezesWhenStill(WaterBodyKind.Lake)
                  && UCWaterWeatherCoupling.FreezesWhenStill(WaterBodyKind.Pool),
                  "W7 still fresh water (lake, pool) must be able to freeze");
            Check(!UCWaterWeatherCoupling.FreezesWhenStill(WaterBodyKind.River)
                  && !UCWaterWeatherCoupling.FreezesWhenStill(WaterBodyKind.Ocean)
                  && !UCWaterWeatherCoupling.FreezesWhenStill(WaterBodyKind.Swamp),
                  "W7 moving, open and salt water must stay open however cold");

            CheckWithin(UCWaterWeatherCoupling.IceCoverFor(WaterBodyKind.Lake, -4f), 1f, 1e-4f,
                        "W7 a hard-frozen lake reads fully iced");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFor(WaterBodyKind.River, -100f), 0f, 1e-4f,
                        "W7 a river never skins over however cold");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFor(WaterBodyKind.Ocean, -100f), 0f, 1e-4f,
                        "W7 the sea never skins over however cold");
            CheckWithin(UCWaterWeatherCoupling.IceCoverFor(WaterBodyKind.Pool, -2f), 0.5f, 1e-4f,
                        "W7 a pool half freezes two degrees down");

            // ---- murk: clear at rest, floor under a storm OR heavy rain, no compound --
            CheckWithin(UCWaterWeatherCoupling.MurkVisibilityScaleFor(0f, 0f), 1f, 1e-4f,
                        "W7 clear weather leaves the water at full visibility");
            CheckWithin(UCWaterWeatherCoupling.MurkVisibilityScaleFor(1f, 0f), 0.3f, 1e-4f,
                        "W7 a full storm drives visibility to the murk floor");
            CheckWithin(UCWaterWeatherCoupling.MurkVisibilityScaleFor(0f, 25f), 0.3f, 1e-4f,
                        "W7 heavy rain alone reaches the same murk floor");
            CheckWithin(UCWaterWeatherCoupling.MurkVisibilityScaleFor(0f, 50f), 0.3f, 1e-4f,
                        "W7 rain past the heavy mark holds at the floor, not below it");
            CheckWithin(UCWaterWeatherCoupling.MurkVisibilityScaleFor(0f, 12.5f), 0.65f, 1e-4f,
                        "W7 light rain eases visibility partway toward the floor");
            CheckWithin(UCWaterWeatherCoupling.MurkVisibilityScaleFor(1f, 25f),
                        UCWaterWeatherCoupling.MurkVisibilityScaleFor(1f, 0f), 1e-4f,
                        "W7 a stormy downpour must be no murkier than the storm alone (stronger wins)");

            for (float st = 0f; st <= 1.0001f; st += 0.25f)
            {
                for (float rn = 0f; rn <= 60f; rn += 15f)
                {
                    float murk = UCWaterWeatherCoupling.MurkVisibilityScaleFor(st, rn);
                    Check(murk >= 0.3f - 1e-4f && murk <= 1f + 1e-4f,
                          "W7 murk must stay within the floor and clear (storm " + st
                          + ", rain " + rn + " gave " + murk + ")");
                }
            }

            // ---- rain drop density: zero dry, linear in rain, per-area not per-total --
            Check(UCWaterWeatherCoupling.RainDropsPerSecond(0f, 128f) == 0f,
                  "W7 a dry sky must emit no drops");
            Check(UCWaterWeatherCoupling.RainDropsPerSecond(-5f, 128f) == 0f,
                  "W7 a negative rain rate must emit no drops, not negative ones");
            CheckWithin(UCWaterWeatherCoupling.RainDropsPerSecond(10f, 200f), 100f, 1e-3f,
                        "W7 the reference downpour on a 200 m window must emit its tuned count");
            CheckWithin(UCWaterWeatherCoupling.RainDropsPerSecond(20f, 128f),
                        2f * UCWaterWeatherCoupling.RainDropsPerSecond(10f, 128f), 1e-3f,
                        "W7 twice the rain must be twice the drops");

            float density100 = UCWaterWeatherCoupling.RainDropsPerSecond(10f, 100f) / (100f * 100f);
            float density200 = UCWaterWeatherCoupling.RainDropsPerSecond(10f, 200f) / (200f * 200f);
            CheckWithin(density100, density200, 1e-5f,
                        "W7 a wide window and a narrow one must stipple to the same DENSITY, "
                        + "not the same total");
        }

        // =====================================================================
        //  W8  THE RIPPLE WORKING RESOLUTION  (UCWaterRippleSim.TexelMetres)
        // =====================================================================

        /// <summary>
        /// The Mid tier solves a QUARTER of the texels of High over the same window - the
        /// grid edge is halved, so the texel doubles. That is the whole cost saving of the
        /// mid-range-phone tier, and it is a private computation (WorkingResolution) read
        /// only through TexelMetres, so it is pinned through that public getter. Also pins
        /// the two invariants the comment promises: the authored resolution is NEVER
        /// overwritten (raising the tier back to High restores the fine grid), and the grid
        /// edge is floored at 64 so a tiny authored resolution cannot collapse to nothing.
        ///
        /// Safe headless: OnEnable allocates no render targets unless playing or previewing
        /// (simulateInEditor), so an AddComponent in batchmode is a bare CPU object.
        /// </summary>
        private static void TestRippleWorkingResolution()
        {
            GameObject go = new GameObject("UCSelfTest_Ripple");
            try
            {
                UCWaterRippleSim sim = go.AddComponent<UCWaterRippleSim>();
                sim.worldSize = 128f;
                sim.resolution = 512;

                // High runs the authored grid: 128 m over 512 texels is 25 cm.
                sim.tier = UCWaterRippleSim.RippleTier.High;
                CheckWithin(sim.TexelMetres, 0.25f, 1e-4f,
                            "W8 High ripple runs the authored grid at a quarter-metre texel");

                // Low keeps the full grid too (it degrades the SOLVE to a decal, not the
                // resolution), so its texel is the same quarter metre.
                sim.tier = UCWaterRippleSim.RippleTier.Low;
                CheckWithin(sim.TexelMetres, 0.25f, 1e-4f,
                            "W8 Low ripple keeps the authored grid (it drops the solve, not the resolution)");

                // Mid halves the grid edge (512 -> 256), so it solves a QUARTER of the
                // texels and the texel doubles to half a metre.
                sim.tier = UCWaterRippleSim.RippleTier.Mid;
                CheckWithin(sim.TexelMetres, 0.5f, 1e-4f,
                            "W8 Mid ripple halves the grid edge, so a quarter of the texels at a half-metre texel");

                // Non-destructive: the authored resolution was never overwritten, so
                // raising the tier back to High restores the fine grid exactly.
                Check(sim.resolution == 512,
                      "W8 the authored ripple resolution must survive a Mid pass unchanged (got "
                      + sim.resolution + ")");
                sim.tier = UCWaterRippleSim.RippleTier.High;
                CheckWithin(sim.TexelMetres, 0.25f, 1e-4f,
                            "W8 raising the tier back to High must restore the full ripple grid");

                // The 64-texel floor: even a tiny authored grid cannot collapse below 64
                // edge texels on Mid, so the ripple field never vanishes to a single cell.
                sim.resolution = 64;
                sim.tier = UCWaterRippleSim.RippleTier.Mid;
                CheckWithin(sim.TexelMetres, 2f, 1e-4f,
                            "W8 the Mid grid edge is floored at 64 texels, so a small authored grid stays interactive");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
