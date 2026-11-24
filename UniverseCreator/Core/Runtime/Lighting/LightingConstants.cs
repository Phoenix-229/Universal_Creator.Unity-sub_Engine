// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    LightingConstants.cs
//  Purpose: The named, unit-carried numbers the lighting module lights the world
//           by. It is to lighting what AtmoConstants is to weather: one place a
//           physical value lives, so no shader or accessor hides an unexplained
//           multiplier. Real photometry, not arbitrary brightness sliders.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Physical constants for sun and sky lighting, each with its unit. Astronomy
    /// (elevation, azimuth, air mass) is not restated here - it is read from the
    /// clock, which owns it.
    /// </summary>
    public static class LightingConstants
    {
        // ---- Shared altitude ------------------------------------------------
        // Civil twilight: the sun 6 deg below the horizon. It is the one altitude
        // that anchors the colour ramp's floor, the illuminance cut-off and the
        // sun/moon hand-off, so it is named once here and the three refer to it
        // rather than each carrying its own -6.
        public const float CivilTwilightDeg = -6f;

        // ---- Twilight bands --------------------------------------------------
        // The lower two twilight thresholds; civil (-6) is the shared altitude
        // above. Nautical is where the sea horizon is lost, astronomical where the
        // sky reaches full night. The twilight sky is built continuously across the
        // whole range, not painted in three steps.
        public const float NauticalTwilightDeg = -12f;
        public const float AstronomicalTwilightDeg = -18f;

        // Twilight glow lives near the horizon and dies by this elevation, leaving
        // the zenith to the day model; the grazing air mass is the long horizon
        // path that reddens the beam into the warm band on the sun's side.
        public const float TwilightGlowTopDeg = 18f;
        public const float TwilightGrazingAirMass = 38f;
        public const float TwilightGlowScale = 8f; // cd/m^2 anchor at the horizon

        // The belt of Venus and the Earth's shadow: the pink band sits within this
        // width of the shadow's rising top edge, the shadow itself a dusky blue-grey.
        // These two tints are the only authored colour; the warmth, the vertical
        // structure and the darkening across the bands are all computed.
        public const float BeltOfVenusWidthDeg = 6f;
        public static readonly Color EarthShadowColor = new Color(0.10f, 0.13f, 0.20f);
        public static readonly Color TwilightSkyColor = new Color(0.16f, 0.22f, 0.38f);

        // ---- Beam extinction (L-27) ------------------------------------------
        // Fraction removed from the direct beam per unit air mass, per channel.
        // Blue is thinned ~2.5x more than red, which is why the low sun reddens as
        // its blue is scattered out - the same scattered blue the sky is made of.
        public const float BeamExtinctionR = 0.0090f;
        public const float BeamExtinctionG = 0.0135f;
        public const float BeamExtinctionB = 0.0225f;

        /// <summary>Per-channel beam extinction, assembled for the exp(-k*airMass*turbidity) law.</summary>
        // Visible-sun extinction (per air mass, times turbidity). The beam values above are far too weak to
        // redden a sun at 10-20 degrees, so the sun stayed white until it touched the horizon. These give
        // roughly 5800 K at 30 deg, ~4000 K at 10 deg and a deep orange-red at 2 deg.
        public const float SunVisualExtinctionR = 0.0200f;
        public const float SunVisualExtinctionG = 0.0400f;
        public const float SunVisualExtinctionB = 0.0800f;

        public static Vector3 BeamExtinction
        {
            get { return new Vector3(BeamExtinctionR, BeamExtinctionG, BeamExtinctionB); }
        }

        // ---- Sun disc colour temperature (L-27) ------------------------------
        // The beam's blackbody temperature ramps from a deep-twilight ember to
        // full daylight as the sun climbs; the extinction above then reddens it
        // further near the horizon. Two effects, one direction of travel.
        public const float TwilightSunKelvin = 1800f;
        public const float DaylightSunKelvin = 6500f;

        // The ramp is keyed on elevation: fully twilight at the floor, fully
        // daylight one span above it. floor + span = +24 deg matches the height a
        // clear midday sun reaches at temperate latitudes.
        public const float SunColorElevationFloorDeg = CivilTwilightDeg;
        public const float SunColorElevationSpanDeg = 30f;

        // ---- Photometric key illuminance (S-07) ------------------------------
        // Real horizontal illuminance in lux, so the day and night differ by the
        // orders of magnitude they differ by in the world rather than by an
        // authored guess. The exposure model (S-11) is what makes both viewable.
        public const float NoonClearSkyLux = 100000f;   // full sun, clear sky
        public const float OvercastDayLux = 10000f;      // heavy cloud, still day
        public const float CivilTwilightLux = 3.4f;      // sun 6 deg below horizon
        public const float FullMoonLux = 0.25f;          // clear full moon at zenith
        public const float StarlightLux = 0.001f;        // moonless clear night

        // Below this altitude the direct sun contributes no usable illuminance;
        // its extinction has taken effectively all of the beam. Matches the civil
        // twilight floor the colour ramp starts from.
        public const float SunIlluminanceFloorDeg = CivilTwilightDeg;

        // ---- Angular disc size (S-08) ----------------------------------------
        // The true angular diameters, in degrees. They are within a hair of each
        // other, which is exactly why the moon can just cover the sun. These size
        // both the drawn disc and the shadow penumbra: a shadow softens with
        // distance at a rate set by the source's angular size, and using the real
        // number is what stops every shadow being hard-edged at every distance.
        public const float SunDiscDiameterDeg = 0.53f;
        public const float MoonDiscDiameterDeg = 0.52f;

        // ---- Moonlight colour ------------------------------------------------
        // The moon is a mirror, not a lamp: its light is the sun's own spectrum
        // reflected off grey rock. The lunar Bond albedo is about 0.11-0.14 and
        // the regolith is a faintly warm grey, so moonlight at source is the
        // daylight sun dimmed to a neutral grey - never blue. The blue of a
        // "moonlit" night is the eye's Purkinje shift applied to the final image,
        // not a tint carried on this light, so no blue lives in this triple.
        //
        // The regolith reflectance rises monotonically from blue to red across the
        // visible (the well-measured red spectral slope of lunar soil), so the three
        // channels step down R > G > B. That slope is deliberately steep enough that
        // it survives being multiplied by the daylight sun spectrum - whose own fit
        // dips slightly in green - leaving the reflected result still monotonic warm
        // (R strongest, B weakest) rather than letting the sun's green dip push blue
        // above green. The spread stays small, so the light reads as a warm grey.
        public static readonly Vector3 MoonAlbedoGrey = new Vector3(0.140f, 0.132f, 0.124f);

        // ---- Exposure (S-11) -------------------------------------------------
        // The bridge from physical illuminance to a display exposure, in the
        // standard photographic terms so the day/night span the sky module makes
        // becomes viewable instead of blown white or crushed black. EV100 is the
        // exposure value referred to ISO 100.
        //
        // Incident metering: EV100 = log2(E_lux * S / C), S the sensitivity and C
        // the incident-light calibration constant. The saturation-based sensor
        // model then sets the luminance that maps to white,
        // maxLuminance = 78 / (S * q) * 2^EV100, with q the lens/vignetting
        // attenuation - at ISO 100 that leading factor is 78 / (100 * 0.65) = 1.2.
        public const float IncidentMeterCalibration = 250f;   // C, for lux
        public const float ExposureMeterIso = 100f;           // S, the EV100 reference
        public const float SaturationSensorConstant = 78f;    // the 78 in the saturation model
        public const float LensAttenuation = 0.65f;           // q, lens + vignetting

        /// <summary>Leading factor of the saturation exposure model, 78 / (S * q) = 1.2 at ISO 100.</summary>
        public static float MaxLuminanceFactor
        {
            get { return SaturationSensorConstant / (ExposureMeterIso * LensAttenuation); }
        }

        // Eye adaptation is asymmetric: adjusting to a brighter scene is quick,
        // adjusting to darkness is slow. Authorable EV/second rates, defaulted so
        // stepping into shade brightens over about a second and stepping back is
        // faster - which is the human behaviour and the S-11 done-when.
        public const float LightAdaptationEvPerSec = 3f;   // toward brighter, the fast direction
        public const float DarkAdaptationEvPerSec = 1f;    // toward darker, the slow direction

        // ---- Atmospheric turbidity -------------------------------------------
        // Turbidity is the one aerosol-load dial the sky reads - the number that
        // feeds the exp(-k*airMass*turbidity) extinction above. It is never authored
        // as its own curve; it starts at a clean-air default and the weather moves
        // it, so the air belongs to the same physics as the rain and the humidity.
        //
        // The span is the real optical one: mountain-clear air sits near 1.8, heavy
        // industrial or dust haze near 10. The default is a lightly-hazed lowland
        // day. Humidity swells aerosols and lifts turbidity; falling rain washes
        // them out, which is why the air is famously clear right behind a front -
        // the gain and scrub below are that pull, in turbidity units at full effect.
        public const float TurbidityClearMountain = 1.8f;  // exceptionally clear
        public const float TurbidityHeavyHaze = 10f;        // dense haze / dust
        public const float TurbidityDefault = 2.2f;         // clean lowland day
        public const float TurbidityHumidityGain = 3f;      // added at full humidity
        public const float TurbidityRainScrub = 2.5f;       // removed by a full wash-out

        // ---- Cloud sheet rendering -------------------------------------------
        // The three decks are drawn as domed sheets sampling one baked, seamless
        // noise field rather than a per-pixel volumetric march. These are the
        // shaping numbers that turn that one field into a genus-specific silhouette:
        // the field itself is generated once on the CPU by the shipped Noise, so no
        // noise is reimplemented on the GPU.
        //
        // The bake resolution is a power of two so the four-corner toroidal blend
        // that makes it tile lands on exact texel boundaries; 256 is enough detail
        // for a sheet read at altitude and small enough to bake in a frame.
        public const int CloudNoiseResolution = 256;

        // Feature size of the base coverage shape, in texels of the bake: the
        // largest fBm octave. The detail and wisp channels are progressively finer,
        // eroding the base edge into cauliflower (detail) and drawn-out streaks (wisp).
        public const float CloudNoiseBaseFeatureTexels = 64f;
        public const float CloudNoiseDetailFeatureTexels = 16f;
        public const float CloudNoiseWispFeatureTexels = 6f;

        // Silhouette shaping per form, applied to the coverage-driven density in the
        // shader. Cumulus raises contrast into hard cauliflower edges; stratus flattens
        // toward an even deck; cirrus stretches the field along the wind into streaks.
        // These are exponents and blend weights, dimensionless, read by UC_Clouds.
        public const float CloudCumulusEdgeSharpness = 3f;   // > 1 hardens the edge
        public const float CloudStratusFlatten = 0.15f;      // toward a uniform deck
        public const float CloudCirrusStretch = 6f;          // along-wind streak ratio

        // Self-shadowing: how far the lit-to-shaded range spreads across a heap from
        // the sun-facing flank to the shaded base. The one number that makes cumulus
        // read three-dimensional from a flat sheet without a second light.
        public const float CloudSelfShadowDepth = 0.6f;

        // Advection: metres of horizontal drift is wind speed times elapsed time; this
        // converts that world drift into the sheet's UV scroll at the deck's own scale,
        // so a faster wind aloft visibly outruns a slow surface breeze.
        public const float CloudAdvectionMetresPerUv = 20000f;

        // Cloud shadows: the low and mid sheets' transmittance projected onto the world
        // as a cookie on the sun, so broken cloud drops moving patches of light across
        // the terrain. This is the cookie's world-to-UV scale (cookie UV per world metre);
        // its reciprocal is the ground tile the cookie covers. The shadow is advected with
        // the same wind vector as the sheet, so patch and cloud move together, and only
        // the drift scale differs (the shadow is the sheet read at a nearer ground scale).
        public const float CloudShadowScale = 0.0004f;   // cookie UV per world metre (tile ~2500 m)

        // When a cloud shadow crosses a region the bounce light must be refreshed with the
        // direct light, or the direct dims while the bounce stays bright and the shadow
        // reads as fake. The refresh is triggered by a change in cover of at least this
        // much - a hysteresis band so a shadow edge crossing fires it but per-frame noise
        // in the cover does not thrash the probe budget.
        public const float CloudShadowProbeRefreshCoverDelta = 0.1f;

        // ---- The irradiance grid ------------------------------------------------
        // Real bounce light off real terrain, stored as a grid of ambient-cube probes.
        // The horizontal and vertical spacings are deliberately asymmetric: light changes
        // faster vertically near the ground (a probe a few metres up is already out of a
        // ditch's shade) than it does horizontally across open terrain, so the vertical
        // stack is sampled twice as densely. The grid is surface-following, not a world
        // box - the vertical stack starts at the terrain height under each column and
        // rises a configured number of levels, so no probes are wasted underground or in
        // empty sky over mountains.
        public const float IrradianceProbeSpacingM = 16f;   // horizontal, metres
        public const float IrradianceVerticalSpacingM = 8f;  // vertical, metres
        public const int IrradianceVerticalLevels = 4;       // levels in each column's stack

        // Probe fill (S-36): the bounce term's strength and the horizon estimate that
        // gives a probe its ambient occlusion. Eight samples reaching out to 48 m is
        // enough to tell an open field from the foot of a cliff or the floor of a valley,
        // at no screen-space cost.
        public const float IrradianceBounceStrength = 0.6f;
        public const int IrradianceHorizonSamples = 8;
        public const float IrradianceHorizonReachM = 48f;

        // Multi-bounce feedback (S-67): a neighbour re-emits the ambient it already held,
        // so the single bounce compounds toward a steady state of base / (1 - albedo) over
        // successive refreshes. That series only runs away for an albedo of one or more, so
        // the accumulated indirect is capped at this multiple of the first bounce. Four is
        // the steady state a 0.75 albedo would reach: physical outdoor ground (0.1..0.4)
        // settles far below it and never trips the cap, while an authored albedo above one -
        // or a fully enclosed bright space - is held here instead of gaining energy forever.
        public const float IrradianceMultiBounceClampScale = 4f;

        // Energy conservation (S-69, C-16): a diffuse surface must reflect less than it
        // receives, so the bounce albedo is held below one. 0.95 is fresh snow's ceiling -
        // the brightest natural surface - and keeps the furnace series base / (1 - albedo)
        // finite (at most 20x) rather than diverging, which is what an authored albedo of
        // exactly one would do. Every ground albedo is clamped to this before it bounces.
        public const float IrradianceMaxDiffuseAlbedo = 0.95f;

        // Emissive contributors per probe (S-74): lava, a campfire, a lit window and author-placed
        // point/spot lights bleed onto the grid, each with inverse-square falloff. A probe keeps
        // only the strongest few, chosen by priority (radiance over squared distance) so the set is
        // deterministic and screen-independent. 8 is the desktop cap; a mobile tier drops to 4. This
        // bounds cost per probe, not the number of emitters in the world.
        public const int IrradianceEmissiveContributorsPerProbe = 8;
        public const int IrradianceEmissiveContributorsPerProbeMobile = 4;
        // A floor on the squared distance so a coincident emitter cannot divide by zero and blow the
        // probe out - one metre, below which the inverse-square is held flat.
        public const float IrradianceEmissiveMinDistanceM = 1f;

        // Heightfield ray-march (S-70): the one primitive behind probe-to-surface visibility,
        // sky visibility, long-range contact shadow and the off-screen reflection half. A ray
        // steps along world XZ against the terrain height with a distance-increasing stride -
        // fine where an occluder matters most (near the origin) and coarse far away, so a fixed
        // step count reaches a long way without a uniform fine step's cost. The CPU fill and the
        // HLSL screen-space passes read THESE numbers (the C# side directly, the GPU side from
        // the manager-published param global) so one ray never disagrees with the other.
        //
        // 64 steps at a 1 m near stride growing 8% per step reach ~1.7 km; the growth is the
        // whole point - a uniform 1 m step would need 1700 samples for the same reach.
        public const int HeightfieldTraceSteps = 64;
        public const float HeightfieldTraceNearStepM = 1f;
        public const float HeightfieldTraceStepGrowth = 1.08f;

        // The reach of the default step schedule, nearStep * (growth^steps - 1) / (growth - 1);
        // a trace's own maxDistance (from the tier) caps it shorter, never longer.
        public static readonly float HeightfieldTraceReachM =
            HeightfieldTraceNearStepM * (Mathf.Pow(HeightfieldTraceStepGrowth, HeightfieldTraceSteps) - 1f)
            / (HeightfieldTraceStepGrowth - 1f);

        // ---- Cumulonimbus and the lightning flash --------------------------------
        // A storm cloud is not a grey patch: it towers, its base is near-black and its
        // sunlit flank near-white, and in heavy hail the base greens. These are the
        // numbers that draw that difference from the same one sheet as every other deck.
        //
        // The tower is how far the storm's silhouette rises above its base altitude at
        // full coverage; a real cumulonimbus reaches the tropopause, ~12 km above a base
        // near 1 km. The flank gain and base darkness are the two ends of the enormous
        // real contrast - the lit side many times brighter than the shaded base.
        public const float CloudCumulonimbusTowerExtentM = 11000f;
        public const float CloudCumulonimbusFlankBrightness = 4f;   // > 1, the sunlit gain
        public const float CloudCumulonimbusBaseDarkness01 = 0.08f;  // base value, toward black

        // The green-grey a hail-bearing base takes on: the light that survives a deep
        // wet-ice column is biased green. The only authored storm colour; the darkness
        // and the flank contrast above are computed from it and the flank gain.
        public static readonly Color CloudHailBaseTint = new Color(0.10f, 0.16f, 0.13f);

        // Lightning (weather's flash event, lit here): a stroke floods the whole sky with
        // a huge illuminance for a fraction of a second. The peak is daylight-order so it
        // lights the ground, not just the cloud; the duration is far shorter than the eye's
        // light-adaptation time above, which is exactly why the exposure must not chase it -
        // the flash reads as a flash, not as the scene calmly brightening.
        public const float CloudLightningPeakLux = 80000f;
        public const float CloudLightningFlashSeconds = 0.15f;

        // ---- The two-term light change under cloud --------------------------------
        // Cloud does two separate things to light and using one term for both is wrong:
        // it dims the directional sun and it raises the diffuse sky term. So the one
        // horizontal illuminance the sky model reports is split in two - a direct beam
        // that casts shadows and a diffuse remainder that does not - and cloud drives
        // the split, not an authored overcast preset.
        //
        // On a clear day most of the horizontal illuminance arrives as the direct beam
        // and the rest as diffuse skylight; this is that clear-sky direct fraction. The
        // beam fraction then falls with cover toward zero (the probability the sun disc
        // is unobscured is ~1 - cover), so a full overcast is all diffuse - shadowless -
        // while a clear sky keeps a hard directional beam.
        public const float ClearSkyDirectBeamFraction01 = 0.85f;

        // ---- Sun shafts through broken cloud --------------------------------------
        // Crepuscular rays: the beam made visible where it streams through gaps in a
        // broken deck. They are a screen-space radial term, masked by the cloud's own
        // transmittance at the sun's screen position - never a volumetric march. The
        // strength is a product of two windows so the effect turns itself off where it
        // has no business showing: a coverage window and a low-sun window.
        //
        // Coverage window: nothing to break the light under a clear sky (no gaps to
        // define rays) and nothing gets through a solid overcast, so the term peaks in
        // the broken band and falls to zero at both ends of cover.
        public const float ShaftBrokenLowCover01 = 0.3f;   // rays reach full strength by here
        public const float ShaftBrokenHighCover01 = 0.7f;   // and hold until here, then fade out

        // Low-sun window: rays are a low-sun phenomenon, strongest when the beam rakes
        // nearly horizontal and gone once the sun is high. Full at the horizon, faded to
        // nothing by this elevation; and zero with the sun below the horizon (no beam).
        public const float ShaftLowSunTopDeg = 35f;

        // ---- Ground albedo for the bounce (S-37) ----------------------------------
        // The bounce term's colour is the ground's own albedo - splat weights times the
        // terrain layers' average colours - so sand bounces warm, grass green, rock grey.
        // Two conditions override that base from above, in the order the world stacks them:
        // snow covers the ground, and a canopy's leaves sit above the snow. Both feed back
        // into the light, closing the causal loop between what grows and how bright it is.
        //
        // Fresh snow is one of the brightest natural surfaces - albedo near 0.8, and faintly
        // blue - so a snowfall lifts the whole scene's ambient measurably. A dense canopy is
        // the opposite: dark and green, it both darkens and greens the light beneath it. The
        // influence caps how far full canopy can pull the albedo, so even a closed wood keeps
        // a little of the ground it stands on rather than reading as a flat green card.
        public static readonly Vector3 SnowAlbedoLinear = new Vector3(0.80f, 0.80f, 0.83f);
        public static readonly Vector3 CanopyBounceAlbedoLinear = new Vector3(0.08f, 0.16f, 0.06f);
        public const float CanopyAlbedoInfluence01 = 0.85f; // fraction canopy overrides ground at full cover

        // ---- Leaf optics for the canopy colour bleed (S-72) -----------------------
        // A forest floor is green because light reaches it two ways through the leaves, and
        // both carry the leaf's colour. Beer-Lambert on the leaf area index (LAI) sets how
        // much light the canopy intercepts: the unscattered gap fraction is exp(-k * LAI),
        // with k the extinction for a random (spherical) leaf-angle distribution - 0.5 is the
        // textbook value, half the leaves edge-on to a vertical beam.
        public const float CanopyExtinctionK = 0.5f;

        // The magnitude of the two leaf paths, applied to the season's own leaf colour so the
        // hue follows § 5: transmittance ~5-10% in the visible (light that passes THROUGH a
        // leaf, strongly green while chlorophyll is present), reflectance ~10-15% (the leaf as
        // a coloured secondary source). Their sum stays well under one at any leaf colour, so a
        // leaf never returns more than it receives (the S-69 leaf case).
        public const float LeafTransmittanceMag = 0.08f;
        public const float LeafReflectanceMag = 0.12f;

        // ---- Wet surface optics for the wetness reflections (S-76) ----------------
        // Water filling the pores of a surface darkens its diffuse albedo (light that would
        // scatter straight back is trapped in the film and re-absorbed) and drops its roughness
        // toward a smooth water sheen. These are the OPTICAL response only; § 7's WetnessSimulation
        // owns where and when a surface is wet and where puddles stand, and drives these.
        // A fully soaked surface loses about a third of its diffuse reflectance - the textbook
        // wet/dry darkening for soil, rock and wood.
        public const float WetAlbedoDarkening = 0.35f;
        // A wet film smooths a rough surface toward this near-mirror roughness; a standing puddle
        // reaches it fully. 0.06 is a still water sheen without being a perfect mirror.
        public const float WetFloorRoughness = 0.06f;
        // The Fresnel reflectance of water at normal incidence (index ~1.33): 0.02, the sky
        // reflection a wet surface gains, rising toward one at grazing angles by Schlick.
        public const float WaterFresnelF0 = 0.02f;

        // Screen-space reflections (S-77, Ultra only): the hierarchical-depth march has a hard
        // per-ray step ceiling so its cost is bounded whatever the scene depth complexity. 64 half-
        // resolution steps reach across a typical view; a ray that exhausts the budget falls back
        // (S-78: heightfield, then grid, then sky) rather than returning a black hole on screen.
        public const int SsrStepBudgetDefault = 64;

        // ---- Probe refresh policy (S-38) ------------------------------------------
        // Bounce light is stored, not recomputed per frame, so it must be refreshed when the
        // thing it depends on has actually moved. Three triggers cover the three inputs: a
        // chunk loading or rebuilding (the terrain itself changed), the sun climbing or
        // setting past this many degrees of elevation (the direct light moved), and cloud
        // cover changing past the shared hysteresis band (`CloudShadowProbeRefreshCoverDelta`
        // above). The sun band is a few degrees so a full sunrise refreshes the grid in a
        // handful of steps rather than every frame, and per-frame jitter in the ephemeris
        // does not thrash it.
        public const float IrradianceSunRefreshDeg = 2f;

        // The refresh is capped so it can never spike a frame: at most this many probes are
        // filled per frame, drained from a queue ordered by distance to camera then by grid
        // index. The index tiebreaker is what makes the order total and therefore identical
        // on two runs (C-06) - distance alone can tie, dictionary order is not stable.
        public const int IrradianceRefreshBudgetPerFrame = 64;

        // The wall-clock refresh pace (S-68). Multi-bounce feedback advances one bounce per
        // refill, so the refill rate must be paced on real seconds, not frames, or the light
        // compounds faster on a high-refresh display and the same scene reads brighter there.
        // This many probes are refilled per real second, carried fractionally between frames
        // for an exact long-run rate and still capped by the per-frame budget above. 1800/s
        // sits under that cap at 30 fps (60/frame) and far under it at 144 fps (~13/frame),
        // so both hold the identical pace; below ~28 fps the cap bites and the device simply
        // converges more slowly toward the same steady state, never a different one.
        public const float IrradianceRefreshProbesPerSecond = 1800f;
    }
}
