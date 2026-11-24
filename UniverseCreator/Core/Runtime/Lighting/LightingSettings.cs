// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    LightingSettings.cs
//  Purpose: The authored lighting dials, in one serialized block. These are the
//           numbers a world author is allowed to turn; everything physical they
//           feed - the extinction law, the illuminance ladder, the probe grid -
//           lives in LightingConstants and is not restated here. Each default is
//           read from that one home where a physical value has one, so a dial and
//           the physics it drives can never disagree at rest.
//
//  DEFAULTS ARE CARRIED FORWARD, NOT REORDERED
//  -------------------------------------------
//  The field names and order are the ones a retired setup was saved against, so an
//  existing lighting setup deserialises with every value intact. New dials may be
//  appended after the last field; renaming or reordering any of the ones below
//  would silently reset a saved world's authored value to the default, which is the
//  one change this block must never make.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// How the display exposure is chosen. Physical derives it from the scene's own
    /// key illuminance (the deterministic default that makes noon and midnight differ
    /// by the orders of magnitude they differ by in the world); Manual holds a fixed
    /// author-set value; Adaptive rate-limits toward the physical target like an eye.
    /// </summary>
    public enum ExposureMode { Physical = 0, Manual = 1, Adaptive = 2 }

    /// <summary>
    /// The authored lighting settings block. Serialisable and embedded on the lighting
    /// manager; the render driver reads it and pushes each dial to the sky model, the
    /// probe grid and the screen-space features. Defaults track <see cref="LightingConstants"/>
    /// and <see cref="NightFloor"/> so there is one source of truth per physical number.
    /// </summary>
    [System.Serializable]
    public class LightingSettings
    {
        // -- Sky and air -------------------------------------------------------

        [Tooltip("Aerosol load the clear sky is built from (dimensionless optical turbidity). " +
                 "1.8 is exceptionally clear mountain air, 10 is dense haze; weather moves it " +
                 "from this clean-lowland default.")]
        [Range(1.8f, 10f)]
        public float turbidity = LightingConstants.TurbidityDefault;

        // -- Moon --------------------------------------------------------------

        [Tooltip("Authored trim (dimensionless, 0..1) on the moon's physical 0.25 lux full-moon " +
                 "reference. Below 1 dims the physically-correct moonlight for playability without " +
                 "moving the reference itself.")]
        [Range(0f, 1f)]
        public float moonIntensity = 0.08f;

        [Tooltip("deg. Inclination of the lunar orbit to the ecliptic. 5 is the real value; it is " +
                 "why the moon does not ride the sun's exact path and why eclipses are rare.")]
        [Range(0f, 10f)]
        public float moonInclinationDeg = 5f;

        // -- Irradiance grid ---------------------------------------------------

        [Tooltip("m. Horizontal spacing of the bounce-light probe grid. The vertical stack is " +
                 "sampled twice as densely (8 m) because light changes faster with height near the " +
                 "ground; that vertical figure is fixed in LightingConstants, not authored here.")]
        [Range(4f, 64f)]
        public float probeSpacing = LightingConstants.IrradianceProbeSpacingM;

        [Tooltip("probes/frame. The refresh budget cap: at most this many probes are re-filled per " +
                 "frame, so the grid can never spike a frame's cost.")]
        [Range(8, 256)]
        public int probesPerFrame = LightingConstants.IrradianceRefreshBudgetPerFrame;

        [Tooltip("Dimensionless weight (0..2) on the terrain bounce term. 0.6 is the authored " +
                 "default; higher lifts the ambient contribution of ground colour into shade.")]
        [Range(0f, 2f)]
        public float bounceStrength = LightingConstants.IrradianceBounceStrength;

        // -- Screen-space --------------------------------------------------------

        [Tooltip("m. How far the contact-shadow march reaches toward the sun. Short by design - it " +
                 "fills the near-contact gap the shadow map is too coarse to hold, not the far field.")]
        [Range(0.05f, 2f)]
        public float contactShadowLength = 0.35f;

        [Tooltip("Samples per pixel for screen-space GI, at half resolution. 8 is the authored " +
                 "default; on a miss the shader falls back to the probe grid, so this never hard-caps " +
                 "the bounce, only its screen-space refinement.")]
        [Range(1, 16)]
        public int ssgiSampleCount = 8;

        [Tooltip("Fraction (0..0.98) of reprojected SSGI history kept each frame, with disocclusion " +
                 "rejection. 0.9 trades a little lag for a lot of stability.")]
        [Range(0f, 0.98f)]
        public float ssgiTemporalWeight = 0.9f;

        // -- Clouds --------------------------------------------------------------

        [Tooltip("Cookie UV per world metre for the cloud-shadow projection. 0.0004 covers a ~2500 m " +
                 "ground tile per cookie tile; its reciprocal is that footprint.")]
        [Range(0.00005f, 0.005f)]
        public float cloudShadowScale = LightingConstants.CloudShadowScale;

        // -- Sun and moon discs --------------------------------------------------

        [Tooltip("deg. True angular diameter of the sun disc. Also sets the shadow penumbra rate, so " +
                 "the real 0.53 is what stops every shadow being hard-edged at every distance.")]
        [Range(0.1f, 2f)]
        public float sunDiscSizeDeg = LightingConstants.SunDiscDiameterDeg;

        [Tooltip("deg. True angular diameter of the moon disc. Within a hair of the sun's 0.53, which " +
                 "is exactly why the moon can just cover it.")]
        [Range(0.1f, 2f)]
        public float moonDiscSizeDeg = LightingConstants.MoonDiscDiameterDeg;

        // UC celestial repair: visible, saved controls consumed by the existing sky owner.
        [Tooltip("Draw the retained Bright Star Catalog at night using game latitude/date/time. No per-star scene objects.")]
        public bool enableStars = true;

        /// <summary>Scales catalog star radiance from 0..4; automatic time, moonlight and weather attenuation still apply.</summary>
        [Tooltip("Artistic multiplier for catalog stars, 0..4. Relative visual magnitudes and shared sky exposure remain unchanged.")]
        [Range(0f, 4f)]
        public float starBrightness = 1f;

        // -- Night and exposure --------------------------------------------------

        [Tooltip("lux. An authored playability floor under the physical night key illuminance - an " +
                 "override, not physics. 0 (default) is the true physical starlight night with zero " +
                 "regression; raise it (0.05 is a navigable reference) to keep a moonless night from " +
                 "going runaway-dark. NightFloor.DefaultNightFloorLux holds that 0.05 reference.")]
        [Range(0f, 1f)]
        public float nightFloorLux = 0f;

        [Tooltip("How the display exposure is chosen. Physical is the deterministic default derived " +
                 "from the scene's key illuminance; Manual and Adaptive are the alternatives.")]
        public ExposureMode exposureMode = ExposureMode.Physical;

        /// <summary>UC-T08-07: fixed photographic EV100 in Manual mode, -16..24 stops; captured graph EV bias is applied afterward.</summary>
        [Tooltip("EV100. Fixed exposure in Manual mode (-16..24); lighting, sky, ambient and fog share this stop. Physical/Adaptive ignore it.")]
        [Range(-16f, 24f)]
        public float manualExposureEv = 12f;

        // -- Multi-bounce feedback (appended; do not reorder above) --------------

        [Tooltip("Compound the probe grid's single bounce into effectively-infinite bounces by " +
                 "letting each surface re-emit the ambient it already holds. On by default from " +
                 "Mobile High up (T1.5); off falls back to the single-bounce grid with no other change.")]
        public bool multiBounce = true;

        [Tooltip("Dimensionless (1..16) convergence cap on multi-bounce: accumulated indirect is held " +
                 "at this multiple of the first bounce. 4 is the steady state of a 0.75 albedo - " +
                 "physical ground never reaches it; only an albedo above 1 or an enclosed bright space " +
                 "trips it, and that logs once.")]
        [Range(1f, 16f)]
        public float multiBounceClamp = LightingConstants.IrradianceMultiBounceClampScale;

        // -- Heightfield trace (appended; do not reorder above) -----------------

        [Tooltip("microseconds. The per-cast cost ceiling for the heightfield ray-march that feeds " +
                 "probe visibility, sky visibility and long-range shadow. A 64-step trace must fit " +
                 "under this; a tier that cannot afford it lowers its step count, not this budget.")]
        [Range(2f, 200f)]
        public float traceBudgetMicroseconds = 30f;

        [Tooltip("Upper-hemisphere directions sampled per probe for sky visibility and the bent " +
                 "normal (S-71). 8 is the desktop default; a mobile tier drops to 4. More directions " +
                 "means a smoother visibility falloff into a gully at a linear trace cost.")]
        [Range(4, 32)]
        public int skyVisibilityDirections = 8;

        [Tooltip("Emissive materials and author-placed point/spot lights that may bleed onto a single " +
                 "probe through the grid (lava, a campfire, a lit window). The set is chosen by priority " +
                 "- intensity over squared distance - so it is deterministic; 8 is the desktop default " +
                 "and a mobile tier drops to 4. This caps cost per probe, not the number of emitters.")]
        [Range(0, 16)]
        public int emissiveContributorsPerProbe = LightingConstants.IrradianceEmissiveContributorsPerProbe;

        [Tooltip("The hard per-ray step ceiling for screen-space reflections (S-77, Ultra only). The " +
                 "hierarchical-depth march stops at this many steps whatever it has found, so SSR cost " +
                 "is bounded; a ray that runs out falls back (S-78) rather than marching on.")]
        [Range(8, 256)]
        public int ssrStepBudget = LightingConstants.SsrStepBudgetDefault;

        [Tooltip("How much reprojected SSR history to keep each frame (0..1). Higher is smoother but " +
                 "slower to react; the variance clamp reins in the history so it cannot ghost a moving " +
                 "reflection. 0.9 is the authored default.")]
        [Range(0f, 0.98f)]
        public float ssrTemporalWeight = 0.9f;

        // -- Cloud shadow softening (appended; do not reorder above) ------------

        [Tooltip("Opt-in. When on, cloud cover softens the sun's shadows: the directional light's " +
                 "shadow strength follows SkyLightSplit (full and hard under a clear sky, collapsing " +
                 "to shadowless under full overcast) instead of staying hard at all times. OFF " +
                 "(default) leaves the sun's shadowStrength exactly as it was - zero regression.")]
        public bool cloudSoftensShadows = false;

        // -- Irradiance GI grid go-live (appended; do not reorder above) --------

        [Tooltip("Opt-in. Fill and upload the irradiance probe grid so the sky/SSGI/reflection " +
                 "shaders read real stored bounce light (UCSampleIrradiance) instead of the engine " +
                 "ambient fallback. The probe-count and vertical-stack dials above (probeSpacing, " +
                 "probesPerFrame, bounceStrength, multiBounce, multiBounceClamp, skyVisibilityDirections, " +
                 "emissiveContributorsPerProbe) only reach the live scene when this is on. OFF (default) " +
                 "binds no grid, so the shader returns the engine ambient exactly as today - zero " +
                 "regression. Spacing scales coarser on lower tiers, so the mobile floor fills far " +
                 "fewer probes; the fill is budgeted to probesPerFrame so it never spikes a frame.")]
        public bool enableIrradianceGI = false;

        [Tooltip("Opt-in. Apply the module's tonemapping through one low-priority global URP Volume " +
                 "(ACES by default) so a physically-exposed HDR scene maps to display instead of " +
                 "clipping. OFF (default) adds no Volume, so the current look is unchanged; the Volume " +
                 "the module adds sits far below an author's own Volume, which always wins. Requires URP.")]
        public bool enableModuleTonemapping = false;

        /// <summary>
        /// Clamps every dial to the real range its tooltip promises, in place. Pure and
        /// idempotent - the same input yields the same output and a second call changes
        /// nothing - so it can run in OnValidate without drift. The bounds match the
        /// [Range] attributes above; a value already in range is returned untouched.
        /// </summary>
        public void ClampToValidRanges()
        {
            turbidity          = Mathf.Clamp(turbidity, 1.8f, 10f);
            moonIntensity      = Mathf.Clamp01(moonIntensity);
            moonInclinationDeg = Mathf.Clamp(moonInclinationDeg, 0f, 10f);
            probeSpacing       = Mathf.Clamp(probeSpacing, 4f, 64f);
            probesPerFrame     = Mathf.Clamp(probesPerFrame, 8, 256);
            bounceStrength     = Mathf.Clamp(bounceStrength, 0f, 2f);
            contactShadowLength = Mathf.Clamp(contactShadowLength, 0.05f, 2f);
            ssgiSampleCount    = Mathf.Clamp(ssgiSampleCount, 1, 16);
            ssgiTemporalWeight = Mathf.Clamp(ssgiTemporalWeight, 0f, 0.98f);
            cloudShadowScale   = Mathf.Clamp(cloudShadowScale, 0.00005f, 0.005f);
            sunDiscSizeDeg     = Mathf.Clamp(sunDiscSizeDeg, 0.1f, 2f);
            moonDiscSizeDeg    = Mathf.Clamp(moonDiscSizeDeg, 0.1f, 2f);
            starBrightness = float.IsNaN(starBrightness) || float.IsInfinity(starBrightness) ? 1f : Mathf.Clamp(starBrightness, 0f, 4f);
            nightFloorLux      = Mathf.Clamp(nightFloorLux, 0f, 1f);
            manualExposureEv = float.IsNaN(manualExposureEv) || float.IsInfinity(manualExposureEv) ? 12f : Mathf.Clamp(manualExposureEv, -16f, 24f);
            multiBounceClamp   = Mathf.Clamp(multiBounceClamp, 1f, 16f);
            traceBudgetMicroseconds = Mathf.Clamp(traceBudgetMicroseconds, 2f, 200f);
            skyVisibilityDirections = Mathf.Clamp(skyVisibilityDirections, 4, 32);
            emissiveContributorsPerProbe = Mathf.Clamp(emissiveContributorsPerProbe, 0, 16);
            ssrStepBudget = Mathf.Clamp(ssrStepBudget, 8, 256);
            ssrTemporalWeight = Mathf.Clamp(ssrTemporalWeight, 0f, 0.98f);
        }
    }
}
