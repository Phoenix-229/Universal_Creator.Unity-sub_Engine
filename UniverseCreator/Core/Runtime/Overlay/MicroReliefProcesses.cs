// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefProcesses.cs
//  Purpose: The thirteen named geomorphic processes that build the micro-relief field.
//           Each is a small class tied to one law, returning the relief it physically
//           makes in millimetres from the ground's real state - grain size from the
//           Udden-Wentworth phi (L-31), entrainment from the Shields threshold (L-33),
//           channels from stream power (L-44), and so on. None of them invents a knob:
//           the amplitude is a length, the wavelength is a length, and eligibility is a
//           physical condition. Sum them (MicroReliefField) and a granite ridge, a
//           limestone pavement and a periglacial slope cannot come out looking alike.
//
//  Shared maths lives in ReliefMath so every process quantises grain size, slope
//  direction and prevailing wind the same way.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Geology;

namespace UniverseCreator.Overlay
{
    /// <summary>Small shared physical conversions used by more than one process. Pure and allocation-free.</summary>
    internal static class ReliefMath
    {
        /// <summary>Grain diameter in mm from the Udden-Wentworth phi scale (L-31): d = 2^-phi mm.</summary>
        public static float GrainDiameterMm(float phi)
        {
            return Mathf.Pow(2f, -Mathf.Clamp(phi, -8f, 12f));
        }

        /// <summary>Shields critical shear stress (Pa) to entrain a grain of diameter dMm: theta_c (rho_s-rho) g d.</summary>
        public static float ShieldsCriticalShearPa(float dMm)
        {
            const float thetaC = 0.045f;      // dimensionless Shields number for coarse grains
            const float submergedWeight = (2650f - 1000f) * 9.81f; // (rho_sediment - rho_water) g
            return thetaC * submergedWeight * (dMm * 0.001f);
        }

        /// <summary>Downslope unit direction on the XZ plane from a compass aspect (0 = N/+Z, 90 = E/+X).</summary>
        public static Vector2 DownslopeDir(float aspectDeg)
        {
            float r = aspectDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
        }

        /// <summary>Distance along the downslope direction, so a pattern can run down the fall line.</summary>
        public static float AlongSlope(float x, float z, float aspectDeg)
        {
            Vector2 d = DownslopeDir(aspectDeg);
            return x * d.x + z * d.y;
        }

        /// <summary>Distance across slope (perpendicular to the fall line), for crests that run along contour.</summary>
        public static float AcrossSlope(float x, float z, float aspectDeg)
        {
            Vector2 d = DownslopeDir(aspectDeg);
            return -x * d.y + z * d.x;
        }

        /// <summary>Deterministic prevailing wind bearing in degrees from the world seed.</summary>
        public static float WindBearingDeg(int seed)
        {
            uint h = (uint)seed * 2654435761u; h ^= h >> 16;
            return (h % 360u);
        }
    }

    /// <summary>L-38: bare rock breaks along its joint and bedding planes into angular facets.</summary>
    internal sealed class JointBeddingFacetsProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "JointBeddingFacets"; } }
        public override string Law { get { return "L-38"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.litho.unit != null
                && c.cover.depthM < 0.02f                    // < 20 mm cover: rock is at the surface
                && c.litho.unit.rockClass != RockClass.Unconsolidated;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c)
        {
            // L-38: joint spacing grows with bed thickness. Clamp to a facet-scale range.
            return Mathf.Clamp(c.litho.unit.thicknessM, 0.1f, 3f);
        }

        public override float DisplacementMm(in MicroReliefContext c)
        {
            var u = c.litho.unit;
            float spacing = CharacteristicWavelengthM(c);
            // Facet height scales with bed thickness; more joint sets means more, smaller facets.
            float amp = Mathf.Clamp(u.thicknessM * 30f, 10f, 120f) / Mathf.Clamp(u.jointSetCount, 1, 6) * 2f;
            float n = ValueNoise(c.env.worldPos.x, c.env.worldPos.z, spacing, c.seed, 3801);
            float facet = Mathf.Sign(n) * Mathf.Pow(Mathf.Abs(n), 0.35f); // sharpen toward flat faces
            return amp * facet;
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Weathering-limited mechanical breakup: frost prying and wetting-drying open the joints. Slow.
            return Mathf.Clamp(0.02f + c.freezeThawCyclesPerYr * 0.002f + c.env.annualPrecipMm * 1e-5f, 0.01f, 0.5f);
        }
    }

    /// <summary>L-39: crystalline and metamorphic rock sheds curved sheets as it unloads and rounds.</summary>
    internal sealed class SpheroidalExfoliationProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "SpheroidalExfoliation"; } }
        public override string Law { get { return "L-39"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            if (c.litho.unit == null || c.cover.depthM >= 0.05f) return false;
            var rc = c.litho.unit.rockClass;
            return rc == RockClass.Crystalline || rc == RockClass.Metamorphic;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 0.8f; }

        public override float DisplacementMm(in MicroReliefContext c)
        {
            // Rounded 20-200 mm sheets on roughly metre-spaced unloading joints; thicker where joints are sparse.
            float sheet = Mathf.Lerp(200f, 60f, Mathf.Clamp01((c.litho.unit.jointSetCount - 1) / 5f));
            float n = ValueNoise(c.env.worldPos.x, c.env.worldPos.z, 0.8f, c.seed, 3901);
            return sheet * n;
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Granular disintegration and sheet spalling on crystalline rock: moisture-paced, very slow.
            return Mathf.Clamp(0.01f + c.env.annualPrecipMm * 2e-5f, 0.01f, 0.2f);
        }
    }

    /// <summary>L-40: soluble rock is fluted and pitted by dissolution where rain is plentiful (karren).</summary>
    internal sealed class KarrenProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "Karren"; } }
        public override string Law { get { return "L-40"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.litho.unit != null && c.litho.unit.solubility > 0f && c.env.annualPrecipMm > 400f;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 0.05f; } // flute spacing ~50 mm

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(c.litho.unit.solubility * (c.env.annualPrecipMm - 400f) / 10f, 5f, 150f);
            float x = c.env.worldPos.x, z = c.env.worldPos.z, aspect = c.env.aspectDeg;
            // Flutes run down the fall line: vary across slope, near-constant along it.
            float across = ReliefMath.AcrossSlope(x, z, aspect);
            float along = ReliefMath.AlongSlope(x, z, aspect);
            float n = ValueNoise(across, along * 0.25f, 0.05f, c.seed, 4001);
            float ridged = 1f - Mathf.Abs(n);          // sharp incisions between rounded ribs
            return -amp * ridged;                        // dissolution removes rock
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Carbonate dissolution scales with solubility and the runoff available to carry it away.
            return Mathf.Clamp(c.litho.unit.solubility * (c.env.annualPrecipMm - 400f) * 1e-3f, 0.05f, 2f);
        }
    }

    /// <summary>L-42: porous rock near salt hollows out into tafoni and honeycomb on sheltered faces.</summary>
    internal sealed class TafoniProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "Tafoni"; } }
        public override string Law { get { return "L-42"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.litho.unit != null
                && c.litho.unit.porosity > 0.1f
                && c.env.saltFraction > 0.2f
                && c.env.windExposure < 0.5f;            // sheltered faces only
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 0.08f; } // pit spacing ~80 mm

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(c.litho.unit.porosity * c.env.saltFraction * 400f, 10f, 120f);
            float n = ValueNoise(c.env.worldPos.x, c.env.worldPos.z, 0.08f, c.seed, 4201);
            float cavity = Mathf.Max(0f, -n);            // only lows hollow out
            return -amp * cavity;                         // salt weathering removes rock
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Salt crystallisation prises grains from porous rock; fast for a weathering process where salt is high.
            return Mathf.Clamp(c.litho.unit.porosity * c.env.saltFraction * 10f, 0.05f, 3f);
        }
    }

    /// <summary>L-33/L-41: where flow cannot move the coarse fraction, fines wash out and a clast lag armours the surface.</summary>
    internal sealed class ClastLagArmourProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "ClastLagArmour"; } }
        public override string Law { get { return "L-33/L-41"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            if (c.cover.type == CoverType.BedrockExposed) return false;
            float dMm = ReliefMath.GrainDiameterMm(c.cover.grainSizePhi);
            // Lag survives only where the boundary shear is below what it takes to entrain the clast (Shields).
            return dMm >= 2f && c.shearStressPa < ReliefMath.ShieldsCriticalShearPa(dMm);
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c)
        {
            float dMm = ReliefMath.GrainDiameterMm(c.cover.grainSizePhi);
            return Mathf.Clamp(3f * dMm * 0.001f, 0.02f, 1f); // a few clast diameters apart
        }

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float dMm = ReliefMath.GrainDiameterMm(c.cover.grainSizePhi);
            float amp = Mathf.Min(dMm, 300f) * 0.5f;         // half-buried clasts
            float cell = 3f * dMm * 0.001f;
            float n = ValueNoise(c.env.worldPos.x, c.env.worldPos.z, cell, c.seed, 3341);
            return amp * (0.5f + 0.5f * n);                   // rounded stones standing proud
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // The lag concentrates as rain and runoff winnow the fine matrix from between the clasts.
            return Mathf.Clamp(c.env.annualPrecipMm * 5e-4f, 0.05f, 1f);
        }
    }

    /// <summary>L-41: loose sand under wind organises into ripples with crests normal to the wind.</summary>
    internal sealed class AeolianRipplesProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "AeolianRipples"; } }
        public override string Law { get { return "L-41"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            bool looseSand = c.cover.type == CoverType.Aeolian
                || (c.cover.grainSizePhi > 1f && c.cover.grainSizePhi < 3f && c.cover.type != CoverType.BedrockExposed);
            return looseSand && c.env.windExposure > 0.4f;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 0.1f; } // ~100 mm saltation ripples

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(c.env.windExposure * 40f, 5f, 30f);
            float windDeg = ReliefMath.WindBearingDeg(c.seed);
            float alongWind = ReliefMath.AlongSlope(c.env.worldPos.x, c.env.worldPos.z, windDeg);
            float ripple = Mathf.Sin(alongWind / 0.1f * (2f * Mathf.PI)); // crests perpendicular to wind
            return amp * ripple;
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Loose sand is reworked continuously whenever the wind blows - a transport rate, not a weathering one, so it is high.
            return Mathf.Clamp(c.env.windExposure * 20f, 0.5f, 30f);
        }
    }

    /// <summary>L-43: raindrop impact planes down bare convexities and fills hollows - it REMOVES relief.</summary>
    internal sealed class RainSplashProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "RainSplash"; } }
        public override string Law { get { return "L-43"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            // Splash detaches loose grains; bare rock offers none, and a canopy intercepts the drops.
            return c.env.annualPrecipMm > 0f
                && c.env.canopyLai < 1f
                && c.cover.type != CoverType.BedrockExposed;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 1f; } // a diffusive smoothing, coarse

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float bare = 1f - Mathf.Clamp01(c.env.canopyLai);
            float amp = Mathf.Clamp(c.env.annualPrecipMm / 100f, 0f, 20f) * bare;
            // Convex (curvature > 0) is lowered; concave is filled - net downslope diffusion.
            return -amp * Mathf.Clamp(c.env.curvature, -1f, 1f);
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Splash detachment on bare ground rises with rainfall and falls as canopy intercepts the drops.
            float bare = 1f - Mathf.Clamp01(c.env.canopyLai);
            return Mathf.Clamp(c.env.annualPrecipMm * 0.005f * bare, 0.05f, 10f);
        }
    }

    /// <summary>L-44: concentrated overland flow (A^m S^n above threshold) cuts rills down the fall line.</summary>
    internal sealed class RillsProcess : MicroReliefProcess
    {
        private const float Threshold = 3f;

        public override string ProcessId { get { return "Rills"; } }
        public override string Law { get { return "L-44"; } }

        private static float StreamPower(in MicroReliefContext c)
        {
            float s = Mathf.Tan(Mathf.Clamp(c.env.slopeDeg, 0f, 80f) * Mathf.Deg2Rad);
            return Mathf.Pow(Mathf.Max(c.contributingAreaM2, 0f), 0.5f) * s; // m=0.5, n=1
        }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.cover.type != CoverType.BedrockExposed && StreamPower(c) > Threshold;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 0.3f; } // rill spacing

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp((StreamPower(c) - Threshold) * 20f, 20f, 300f);
            float x = c.env.worldPos.x, z = c.env.worldPos.z, aspect = c.env.aspectDeg;
            float across = ReliefMath.AcrossSlope(x, z, aspect);
            float along = ReliefMath.AlongSlope(x, z, aspect);
            float n = ValueNoise(across, along * 0.15f, 0.3f, c.seed, 4401);
            float channel = Mathf.Max(0f, -n);               // rills are the lows between interfluves
            return -amp * channel;                            // incision
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Incision rate tracks excess stream power above the threshold - fast, transport-limited.
            return Mathf.Clamp((StreamPower(c) - Threshold) * 10f, 1f, 100f);
        }
    }

    /// <summary>L-36: frost heave sorts stones into polygons on the flat and stripes on the slope.</summary>
    internal sealed class SortedPolygonsStripesProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "SortedPolygonsStripes"; } }
        public override string Law { get { return "L-36"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.freezeThawCyclesPerYr > 30f;            // needs frequent freeze-thaw
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 1f; } // patterned-ground cell ~1 m

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(50f + c.freezeThawCyclesPerYr * 1.5f, 50f, 200f);
            float x = c.env.worldPos.x, z = c.env.worldPos.z;
            float polygon = 2f * Mathf.Abs(ValueNoise(x, z, 1f, c.seed, 3601)) - 1f; // stone rims stand proud
            float stripe = Mathf.Sin(ReliefMath.AcrossSlope(x, z, c.env.aspectDeg) / 1f * (2f * Mathf.PI));
            float t = Mathf.Clamp01((c.env.slopeDeg - 3f) / 3f); // polygons <=3 deg, stripes >=6 deg
            return amp * Mathf.Lerp(polygon, stripe, t);
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Frost sorting reworks the active layer once per freeze-thaw cycle - the more cycles, the faster.
            return Mathf.Clamp(c.freezeThawCyclesPerYr * 0.5f, 1f, 50f);
        }
    }

    /// <summary>L-37: slow frozen-ground creep on moderate slopes rucks the turf into terracette steps.</summary>
    internal sealed class SolifluctionTerracettesProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "SolifluctionTerracettes"; } }
        public override string Law { get { return "L-37"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.freezeThawCyclesPerYr > 0f && c.env.slopeDeg >= 10f && c.env.slopeDeg <= 25f;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 1.5f; } // lobe spacing along slope

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(100f + (c.env.slopeDeg - 10f) * 20f, 100f, 400f); // riser height
            float u = ReliefMath.AlongSlope(c.env.worldPos.x, c.env.worldPos.z, c.env.aspectDeg) / 1.5f;
            float frac = u - Mathf.Floor(u);                 // repeating tread-then-riser down the slope
            return amp * (frac - 0.5f);
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Frozen-ground creep moves the mantle centimetres a year, steepening with slope.
            return Mathf.Clamp(10f + (c.env.slopeDeg - 10f) * 5f, 10f, 100f);
        }
    }

    /// <summary>L-45: uprooted trees leave a paired pit and mound at the root plate (treethrow).</summary>
    internal sealed class PitAndMoundProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "PitAndMound"; } }
        public override string Law { get { return "L-45"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.env.canopyLai > 0.3f && c.env.soilDepthM > 0.05f; // needs trees and soil to overturn
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 3f; } // root-plate pit+mound pair

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(200f + c.env.canopyLai * 80f, 200f, 500f);
            float density = Mathf.Clamp01(c.env.canopyLai / 3f); // more canopy, more throws
            float n = ValueNoise(c.env.worldPos.x, c.env.worldPos.z, 3f, c.seed, 4501);
            return amp * n * density;                         // paired mound (+) and pit (-)
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Treethrow is episodic; averaged over the stand it is a slow turnover that rises with canopy.
            return Mathf.Clamp(c.env.canopyLai * 0.5f, 0.1f, 2f);
        }
    }

    /// <summary>L-46: burrowing and casting fauna raise low mounds where the soil is productive.</summary>
    internal sealed class BioturbationMoundsProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "BioturbationMounds"; } }
        public override string Law { get { return "L-46"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.env.soilFertility > 0.3f && c.env.soilDepthM > 0.05f;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 0.3f; } // mound spacing

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(c.env.soilFertility * 80f, 10f, 80f);
            float n = ValueNoise(c.env.worldPos.x, c.env.worldPos.z, 0.3f, c.seed, 4601);
            return amp * Mathf.Max(0f, n);                    // mounds only, no pits
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // Burrowing and casting fauna turn over the topsoil faster where it is fertile.
            return Mathf.Clamp(c.env.soilFertility * 8f, 0.5f, 10f);
        }
    }

    /// <summary>L-48: waves cut a notch at the waterline and throw a berm just above the run-up limit.</summary>
    internal sealed class BeachBermWaveNotchProcess : MicroReliefProcess
    {
        public override string ProcessId { get { return "BeachBermWaveNotch"; } }
        public override string Law { get { return "L-48"; } }

        public override bool IsEligible(in MicroReliefContext c)
        {
            return c.env.hasWaterInRange
                && c.waveRunupHeightM > 0f
                && c.env.submergedDepthM <= c.waveRunupHeightM
                && c.env.distanceToWaterM < 20f;
        }

        public override float CharacteristicWavelengthM(in MicroReliefContext c) { return 5f; } // shore-parallel berm

        public override float DisplacementMm(in MicroReliefContext c)
        {
            float amp = Mathf.Clamp(c.waveRunupHeightM * 1000f * 0.5f, 50f, 600f);
            if (c.env.submergedDepthM > 0f)
            {
                // Below the waterline: a cut notch / wave-planed platform.
                return -amp * 0.5f * Mathf.Clamp01(c.env.submergedDepthM / c.waveRunupHeightM);
            }
            // Above the waterline: a berm that fades landward across the run-up band.
            return amp * Mathf.Clamp01(1f - c.env.distanceToWaterM / (c.waveRunupHeightM * 4f + 1e-3f));
        }

        public override float ActivityRateMmPerYr(in MicroReliefContext c)
        {
            // The swash rebuilds the berm and notch with every tide - the fastest reworking on the map.
            return Mathf.Clamp(c.waveRunupHeightM * 50f, 5f, 200f);
        }
    }
}
