// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCSystemManagerInspectors.cs
//  Purpose: The real inspectors for the lighting and vegetation managers.
//
//  WHY THESE REPLACE THE PLANNED-MODULE INSPECTOR
//  ----------------------------------------------
//  The inspector these supersede drew both managers entirely disabled, under a
//  notice saying nothing read them. That was true when it was written and is not
//  any more: time of day and the whole vegetation field set are read now. A
//  disabled inspector over live fields takes the author's control away and hides
//  the work, so each manager now declares which groups are live and which are
//  still declared, and gets both in one inspector.
//
//  Weather is not here. It already has its own inspector, which draws the wind
//  manager's settings inline through a nested editor - a second weather inspector
//  would be a duplicate CustomEditor for one type, which is a compile error, and
//  would lose the wind editor besides.
//
//  THE READOUTS ARE THE POINT
//  --------------------------
//  A field dump tells the author what they typed. The readouts tell them what it
//  means: how long the day is, where the treeline lands, what spacing their
//  density actually implies. Those are the numbers the laws act on, so showing
//  them is what lets someone author a world rather than guess at one - and every
//  readout is computed by the same accessor the generator will call, so an
//  inspector that reads wrong is a generator that builds wrong, found early.
//
//  NO DIALOGS.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Ecology;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.Editor.Inspectors
{
    // =========================================================================
    //  LIGHTING
    // =========================================================================

    [CustomEditor(typeof(UCLightingSystemManager))]
    [CanEditMultipleObjects]
    public class UCLightingSystemManagerInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UCInspectorUI.HeaderBar("Lighting System", "d_Light Icon",
                UCInspectorUI.HeaderAmber, "settings live · tier resolved");

            EditorGUILayout.Space(3);
            EditorGUILayout.HelpBox(
                "Time of Day is read from the WeatherSystem child, which owns the world " +
                "clock. The settings below are authored biases and trims on the atmosphere " +
                "physics - never replacements for it - and the performance envelope is one " +
                "resolved product tier, not a second set of dials.",
                MessageType.Info);
            EditorGUILayout.Space(3);

            serializedObject.Update();
            SerializedObject so = serializedObject;

            Section("TimeAndSun", "Time and Sun", () =>
            {
                EditorGUILayout.PropertyField(so.FindProperty("lightingEnabled"));
                EditorGUILayout.PropertyField(so.FindProperty("sun"));
                EditorGUILayout.PropertyField(so.FindProperty("moonEnabled"));
                if (targets.Length == 1) DrawTimeEditor((UCLightingSystemManager)target);
                if (targets.Length == 1) DrawSolarReadout((UCLightingSystemManager)target);
            });

            Section("SkyAndAtmosphere", "Sky and Atmosphere", () =>
            {
                Field(so, "settings.turbidity");
                Field(so, "settings.moonIntensity");
                Field(so, "settings.moonInclinationDeg");
                Field(so, "settings.sunDiscSizeDeg");
                Field(so, "settings.moonDiscSizeDeg");
            });

            Section("Clouds", "Clouds", () => Field(so, "settings.cloudShadowScale"));

            Section("Exposure", "Exposure and Tonemapping",
                () => Field(so, "settings.exposureMode"));

            // __UC_LIGHTING_SECTIONS_TAIL__
            Section("Irradiance", "Irradiance Grid", () =>
            {
                Field(so, "settings.probeSpacing");
                Field(so, "settings.probesPerFrame");
                Field(so, "settings.bounceStrength");
                if (targets.Length == 1) DrawIrradianceReadout((UCLightingSystemManager)target);
            });

            Section("Shadows", "Shadows", () =>
            {
                EditorGUILayout.LabelField(
                    "Owned by the resolved tier, not authored here.", EditorStyles.miniLabel);
                if (targets.Length == 1) DrawShadowReadout((UCLightingSystemManager)target);
            });

            Section("ScreenSpace", "Screen-Space", () =>
            {
                Field(so, "settings.contactShadowLength");
                Field(so, "settings.ssgiSampleCount");
                Field(so, "settings.ssgiTemporalWeight");
                if (targets.Length == 1) DrawScreenSpaceReadout((UCLightingSystemManager)target);
            });

            Section("Night", "Night", () => Field(so, "settings.nightFloorLux"));

            Section("TierAndPerformance", "Tier and Performance", () =>
            {
                Field(so, "followAdaptiveQuality");
                using (new EditorGUI.DisabledScope(
                    so.FindProperty("followAdaptiveQuality").boolValue))
                {
                    Field(so, "lightingTierOverride");
                }
                if (targets.Length == 1) DrawTierReadout((UCLightingSystemManager)target);
            });

            Section("MemoryBudget", "Memory Budget", () =>
            {
                EditorGUILayout.LabelField(
                    "Itemised, priced from the type that owns each allocation.",
                    EditorStyles.miniLabel);
                if (targets.Length == 1) DrawMemoryReadout((UCLightingSystemManager)target);
            });

            Section("DebugViews", "Debug Views", () => Field(so, "debugView"));

            serializedObject.ApplyModifiedProperties();
        }

        // A live section with the amber accent, its open state remembered under the
        // same prefs root the grouped inspector uses, so this hand-built inspector
        // reads and behaves exactly like the two that call UCGroupedInspector.
        private const string PrefsKey = "UniverseCreator.Inspector.Lighting.";

        private static void Section(string key, string title, System.Action body)
        {
            string prefs = PrefsKey + key;
            bool open = EditorPrefs.GetBool(prefs, true);
            bool next = UCInspectorUI.Section(open, title, UCInspectorUI.HeaderAmber);
            if (next != open) EditorPrefs.SetBool(prefs, next);
            if (!next) return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(6f);
            EditorGUILayout.BeginVertical();

            // Through Panel rather than called directly. A throw inside a group body
            // would otherwise skip both End calls below as well, so the layout stack
            // is left unbalanced and the whole component draws blank from then on -
            // the failure UCGroupedInspector.Panel documents.
            UCGroupedInspector.Panel(title, body);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        // One settings sub-property, loud when its path is stale rather than silently
        // vanishing - the same guard DrawGroup makes for a renamed field.
        private static void Field(SerializedObject so, string path)
        {
            SerializedProperty p = so.FindProperty(path);
            if (p == null)
            {
                EditorGUILayout.HelpBox(
                    "No serialized field at \"" + path + "\". The inspector is out of " +
                    "date with the component.", MessageType.Error);
                return;
            }
            EditorGUILayout.PropertyField(p, true);
        }

        /// <summary>
        /// The world's time of day, edited here but stored on the weather manager and
        /// nowhere else. The lighting system keeps no clock of its own: the slider
        /// reads the resolved clock, and a change is written straight back through
        /// <see cref="UCWeatherSystemManager.SetTime"/> so the single owning value on
        /// the weather manager is the only thing that moves. With no weather system in
        /// the world there is no owner to write to, so the control is shown disabled at
        /// the documented default rather than inventing a second place to keep time.
        /// </summary>
        private static void DrawTimeEditor(UCLightingSystemManager l)
        {
            EditorGUILayout.Space(2);
            UCWeatherSystemManager weather = UCWeatherSystemManager.FindForWorld(l.transform);

            if (weather == null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.IntSlider("Day of year",
                        UCWeatherSystemManager.DefaultDayOfYear, 1, 365);
                    EditorGUILayout.Slider("Time of day (h)",
                        UCWeatherSystemManager.DefaultHourOfDay, 0f, 24f);
                }
                return;
            }

            var c = weather.ResolveClock();

            EditorGUI.BeginChangeCheck();
            int day = EditorGUILayout.IntSlider(
                new GUIContent("Day of year", "Stored on the WeatherSystem clock, not here."),
                c.dayOfYear, 1, 365);
            float hour = EditorGUILayout.Slider(
                new GUIContent("Time of day (h)", "Stored on the WeatherSystem clock, not here."),
                c.localHour, 0f, 24f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(weather, "Set world time");
                weather.SetTime(day, hour);
                EditorUtility.SetDirty(weather);
                SceneView.RepaintAll();
            }
        }

        /// <summary>
        /// The solar geometry the world's latitude and tilt produce. Shown at both
        /// solstices because that pair is what decides whether a world has a
        /// growing season at all.
        /// </summary>
        private static void DrawSolarReadout(UCLightingSystemManager l)
        {
            EditorGUILayout.Space(4);

            // W-04. When there is no weather system the numbers below are still
            // real - they are the documented fallback - but an author reading them
            // has no way to tell that from a latitude they set themselves, so it is
            // said out loud rather than left to be discovered.
            if (!l.HasClockSource)
            {
                EditorGUILayout.HelpBox(
                    "No WeatherSystem in this world, so the clock below is the " +
                    "documented default: latitude " +
                    UCWeatherSystemManager.DefaultLatitudeDeg.ToString("0") +
                    ", longitude " +
                    UCWeatherSystemManager.DefaultLongitudeDeg.ToString("0") +
                    ", day " + UCWeatherSystemManager.DefaultDayOfYear +
                    " at " + UCWeatherSystemManager.DefaultHourOfDay.ToString("0") +
                    ":00. Add a WeatherSystem to author it.",
                    MessageType.Info);
            }

            EditorGUILayout.LabelField("Resolved solar geometry", EditorStyles.boldLabel);

            UCGroupedInspector.Readout("Latitude / axial tilt",
                l.LatitudeDegrees.ToString("0.0") + " / " +
                l.AxialTiltDegrees.ToString("0.0") + " deg");

            // Phase 0 is northern midwinter and 0.5 northern midsummer, matching
            // the weather manager's season convention.
            const float Midwinter = 0f;
            const float Midsummer = 0.5f;

            UCGroupedInspector.Readout("Declination, midwinter / midsummer",
                l.SolarDeclinationDegrees(Midwinter).ToString("0.0") + " / " +
                l.SolarDeclinationDegrees(Midsummer).ToString("0.0") + " deg");

            UCGroupedInspector.Readout("Day length, midwinter / midsummer",
                l.DayLengthHours(Midwinter).ToString("0.0") + " / " +
                l.DayLengthHours(Midsummer).ToString("0.0") + " h");

            UCGroupedInspector.Readout("Solar radiation, midwinter / midsummer",
                l.ExtraterrestrialRadiationMjPerM2Day(Midwinter).ToString("0") + " / " +
                l.ExtraterrestrialRadiationMjPerM2Day(Midsummer).ToString("0") +
                " MJ/m2/day");

            UCGroupedInspector.Readout("Warm slope faces",
                l.WarmAspectDegrees.ToString("0") + " deg (" +
                (l.LatitudeDegrees >= 0f ? "south" : "north") + ")");

            if (l.AxialTiltDegrees <= 0f)
            {
                EditorGUILayout.HelpBox(
                    "Axial tilt is zero, so this world has no seasons: every day is the " +
                    "same length at every latitude and nothing seasonal will vary.",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// The bake cost the current world and spacing imply, shown before a bake the
        /// same way V-29 shows detail memory: a probe count and the megabytes it uploads,
        /// both exact functions of the world size and the resolved spacing, so the figure
        /// an author reads is the figure the driver will allocate.
        /// </summary>
        private static void DrawIrradianceReadout(UCLightingSystemManager l)
        {
            EditorGUILayout.Space(2);
            if (l.World == null)
            {
                EditorGUILayout.HelpBox(
                    "Loose in a scene with no world to size against, so no grid figure can " +
                    "be shown. Parent this under a Universe Creator world.",
                    MessageType.Info);
                return;
            }

            UCGroupedInspector.Readout("Probes (grid total)",
                l.EstimatedProbeCount.ToString("N0"));
            UCGroupedInspector.Readout("Irradiance memory",
                (l.EstimatedIrradianceBytes / (1024f * 1024f)).ToString("0.0") + " MB GPU");
        }

        /// <summary>The shadow envelope the resolved tier sets. Read-only because the
        /// tier owns it: cascades, distance and atlas size are not authored here.</summary>
        private static void DrawShadowReadout(UCLightingSystemManager l)
        {
            LightingTierSettings t = l.ResolvedTierSettings;
            UCGroupedInspector.Readout("Cascades", t.cascadeCount.ToString());
            UCGroupedInspector.Readout("Shadow distance", t.shadowDistance.ToString("0") + " m");
            UCGroupedInspector.Readout("Atlas resolution",
                t.shadowAtlasResolution.ToString() + " px");
            UCGroupedInspector.Readout("Soft shadows", t.softShadows.ToString());
        }

        /// <summary>Which screen-space passes the resolved tier runs. The dials above set
        /// their look; the tier decides whether they run at all.</summary>
        private static void DrawScreenSpaceReadout(UCLightingSystemManager l)
        {
            LightingTierSettings t = l.ResolvedTierSettings;
            UCGroupedInspector.Readout("Screen-space GI",
                t.screenSpaceGI ? "on, " + t.ssgiSampleCount + " rays" : "off");
            UCGroupedInspector.Readout("Contact shadows", t.contactShadows ? "on" : "off");
        }

        /// <summary>The tier lighting resolves to now, and the probe-spacing scale it
        /// applies to the authored spacing.</summary>
        private static void DrawTierReadout(UCLightingSystemManager l)
        {
            EditorGUILayout.Space(2);
            UCGroupedInspector.Readout("Resolved tier",
                l.ResolvedTier.ToString() +
                (l.followAdaptiveQuality ? " (from AdaptiveQuality)" : " (pinned)"));
            UCGroupedInspector.Readout("Probe spacing scale",
                l.ResolvedTierSettings.probeSpacingScale.ToString("0.00") + "x");
        }

        // The stated reference the two screen-space rows are priced at, so a
        // resolution-dependent buffer reads as one honest number rather than "it
        // depends". A device running higher scales these linearly.
        private const int MemRefWidth = 1920;
        private const int MemRefHeight = 1080;

        // The stated mid-range ceiling this readout flags against. A lighting system
        // that quietly costs more than this on a 6 GB phone has failed even inside its
        // frame budget, so the figure is checked in the inspector, not left to a bake.
        private const long MidRangeMemoryCeilingBytes = 48L * 1024L * 1024L;

        /// <summary>
        /// Every lighting allocation, itemised, each priced from the type that owns it
        /// so the figure matches what the driver reserves rather than a restated guess:
        /// the sky LUT and cloud tile are fixed constants, the probe rows scale with the
        /// resolved grid, and the two screen-space targets are shown at a stated
        /// reference resolution and only when the resolved tier actually runs them. The
        /// mid-range total is checked against a stated ceiling. (S-65)
        /// </summary>
        private static void DrawMemoryReadout(UCLightingSystemManager l)
        {
            EditorGUILayout.Space(2);
            LightingTierSettings t = l.ResolvedTierSettings;
            const float MB = 1024f * 1024f;

            long probes = l.EstimatedProbeCount;
            long skyLut = SkyLut.ByteSize;
            long cloudNoise = CloudNoiseBaker.ByteSize;
            long probeManaged = probes * IrradianceProbe.ManagedBytes;
            long probeGpu = l.EstimatedIrradianceBytes;

            long contact = t.contactShadows ? (long)MemRefWidth * MemRefHeight * 1L : 0L;
            long ssgi = t.screenSpaceGI
                ? 3L * ((MemRefWidth / 2) * (MemRefHeight / 2)) * 8L   // trace + 2 history, RGBAHalf
                : 0L;

            UCGroupedInspector.Readout("Sky LUT (32x16 RGBAHalf)",
                (skyLut / 1024f).ToString("0.#") + " KB");
            UCGroupedInspector.Readout("Cloud noise (256^2 RGBA32)",
                (cloudNoise / 1024f).ToString("0.#") + " KB");
            UCGroupedInspector.Readout("Probe array (managed)",
                (probeManaged / MB).ToString("0.00") + " MB  (" + probes.ToString("N0") + " x " +
                IrradianceProbe.ManagedBytes + " B)");
            UCGroupedInspector.Readout("Probe volumes (2x Texture3D RGBAHalf)",
                (probeGpu / MB).ToString("0.00") + " MB GPU");
            UCGroupedInspector.Readout("Contact shadow (R8 @ " + MemRefWidth + "x" + MemRefHeight + ")",
                t.contactShadows ? (contact / MB).ToString("0.00") + " MB" : "off (tier)");
            UCGroupedInspector.Readout("SSGI (half-res RGBAHalf @ ref)",
                t.screenSpaceGI ? (ssgi / MB).ToString("0.00") + " MB" : "off (tier)");

            long total = skyLut + cloudNoise + probeManaged + probeGpu + contact + ssgi;
            UCGroupedInspector.Readout("Total lighting memory",
                (total / MB).ToString("0.00") + " MB");

            float ceilingMB = MidRangeMemoryCeilingBytes / MB;
            if (total > MidRangeMemoryCeilingBytes)
            {
                EditorGUILayout.HelpBox(
                    "Over the stated " + ceilingMB.ToString("0") + " MB mid-range ceiling. " +
                    "Widen probe spacing or shrink the world to bring the probe rows down - " +
                    "they are the only part that scales.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Within the stated " + ceilingMB.ToString("0") + " MB mid-range ceiling.",
                    EditorStyles.miniLabel);
            }
        }
    }

    // =========================================================================
    //  VEGETATION
    // =========================================================================

    [CustomEditor(typeof(UCVegetationSystemManager))]
    [CanEditMultipleObjects]
    public class UCVegetationSystemManagerInspector : UnityEditor.Editor
    {
        private static readonly UCFieldGroup[] Groups =
        {
            new UCFieldGroup("Global", true,
                "Seed and the one dial that thins a whole world for a weaker device.",
                "vegetationEnabled", "seedOffset", "globalDensityScale"),

            new UCFieldGroup("Ecology", true,
                "Turns species envelopes on. Off keeps the shipped density-and-slope rules.",
                "useEcology", "suitabilityThreshold", "suitabilitySharpness",
                "allowMixedStands", "maxSpeciesPerSite"),

            new UCFieldGroup("Grass and Detail", true, "",
                "grassPerSquareMetre", "detailResolution",
                "detailResolutionPerPatch", "detailUsesWorldWind", "detailMemoryCeilingMB"),

            new UCFieldGroup("Trees", true, "",
                "treesPerHectare",
                "billboardFadeLengthMetres", "treeSpacingMetres"),

            new UCFieldGroup("Placement Rules", true,
                "Altitudes are metres above sea level, not world Y.",
                "maxSlopeDegrees", "minAltitudeMetres", "maxAltitudeMetres",
                "clearanceMetres", "avoidWater", "avoidCutRegions"),

            new UCFieldGroup("Environment Sampling", true,
                "How the site's soil, wetness and shore distance are measured.",
                "waterSearchRadiusMetres", "soilDepthMaxMetres",
                "soilAngleOfReposeDegrees", "fertilityFromCurvature",
                "wetnessCatchmentRadiusMetres", "saltSprayDecayMetres"),

            new UCFieldGroup("Competition and Canopy", true,
                "Taller species shade shorter ones, and a stand thins itself.",
                "enableCanopyCompetition", "canopyLayerPasses", "understoryLightFloor",
                "enableSelfThinning", "enableCrownSpacing"),

            new UCFieldGroup("Dispersal and Clustering", true,
                "What makes a stand read as grown rather than sprinkled.",
                "enableDispersalClustering", "dispersalSeedsPerHectare", "clusterContrast"),

            new UCFieldGroup("Disturbance", true,
                "Fire, grazing, snow and avalanche histories.",
                "enableFire", "fireReturnBaseYears", "enableBrowse", "browsePressureBase",
                "enableSnowLoad", "snowLoadAtTreelineKgPerM2", "enableAvalanche",
                "avalancheMinSlopeDegrees", "enableKrummholz"),

            new UCFieldGroup("Season", true,
                "Follows the weather manager's year unless you pin a day.",
                "enableSeasonalPhenology", "seasonFollowsWeather", "manualDayOfYear"),

            new UCFieldGroup("Biome and Masking", true, "",
                "useGraphMasks", "maskThreshold", "biomeBlendMetres"),

            new UCFieldGroup("Build", true, "",
                "rebuildWithChunks", "maxConcurrentCells"),

            new UCFieldGroup("Budget", true,
                "The ceilings that keep a large world inside a frame budget.",
                "maxInstancesPerCell", "shadowCasterDistanceMetres",
                "candidatesPerSquareMetre"),

            new UCFieldGroup("Hierarchy", true, "",
                "parentUnderVegetationSystem"),

            new UCFieldGroup("Debug", true, "",
                "debugDrawSuitability", "debugSpeciesIndex", "debugShowLimitingFactor")
        };

        // A cached editor over the world object. The "Prototype library" panel
        // below hosts the world inspector's own vegetation sections through it
        // rather than drawing a second copy - so trees, shrubs, grass, objects,
        // their budgets and the foliage settings are authored here while their
        // code, and the load-bearing slider/focus-out/drag-commit helpers behind
        // it, stay owned in one place. Rebuilt when the world under us changes,
        // and destroyed in OnDisable so it never outlives this inspector.
        private UnityEditor.Editor hostedWorldEditor;
        private UniverseCreatorObject hostedWorld;

        private void OnDisable()
        {
            if (hostedWorldEditor != null)
            {
                Object.DestroyImmediate(hostedWorldEditor);
                hostedWorldEditor = null;
                hostedWorld = null;
            }
        }

        public override void OnInspectorGUI()
        {
            UCGroupedInspector.Draw(
                serializedObject, "Vegetation",
                "Vegetation System", "d_Terrain Icon", UCInspectorUI.HeaderGreen,
                "resolve path live",
                "Every field here is read. The resolve path validates them and hands " +
                "them to the ecology layer as one snapshot, alongside the latitude, " +
                "sea level and climate it reads from the lighting, water and weather " +
                "systems rather than duplicating. The scatter pass reads that snapshot " +
                "to place trees, shrubs and objects; the Diagnostics panel below reports " +
                "what the last build placed and where the ecology model planted it.",
                Groups);

            if (targets.Length != 1) return;

            UCVegetationSystemManager veg = (UCVegetationSystemManager)target;

            // Every panel below is a readout over the rest of the world - the world
            // object, the weather manager, the prototype library, the registry - and
            // any of them can be absent while an author is still assembling a scene.
            // Wrapped one panel at a time so a throw in a diagnostic costs that
            // diagnostic and not the whole inspector; see UCGroupedInspector.Panel.
            UCGroupedInspector.Panel("Resolved", () => DrawResolvedReadout(veg));

            // Where prototypes are authored (V-53). The four prototype lists, their
            // scatter budgets and the foliage render settings now draw HERE, hosted
            // from the world inspector's own editor so there is still exactly one copy
            // of that code and its slider/focus-out helpers - this panel renders it,
            // the world object stores it.
            UCGroupedInspector.Panel("Prototype library", () => DrawHostedLibrary(veg));

            // Pre-build validation (V-51). Drawn after the resolved readout, because
            // a warning reads better once the author has seen what their dials
            // resolved to - and before any rebuild button, which is the point: catch
            // the silent-empty-world defects before the build, not after it.
            UCGroupedInspector.Panel("Validation", () => UCVegetationValidation.Draw(veg));

            // Post-build census (V-52). Last, because it is the after picture: what
            // the build the two panels above shaped actually placed, and where the
            // ecology model planted it.
            UCGroupedInspector.Panel("Diagnostics", () => UCVegetationDiagnostics.Draw(veg));
        }

        /// <summary>
        /// What the authored density and the rest of the world resolve to. Spacing
        /// is the useful one: it is where an author finds out that their stem count
        /// and their spacing were asking for different forests.
        /// </summary>
        private static void DrawResolvedReadout(UCVegetationSystemManager v)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Resolved", EditorStyles.boldLabel);

            UCGroupedInspector.Readout("Seed", v.EffectiveSeed.ToString());
            UCGroupedInspector.Readout("Latitude", v.LatitudeDegrees.ToString("0.0") + " deg");
            UCGroupedInspector.Readout("Sea level", v.SeaLevelY.ToString("0.0") + " m world Y");
            UCGroupedInspector.Readout("Day of year",
                (v.ResolvedSeasonPhase * EcoConstants.DaysPerTropicalYear).ToString("0"));

            float density = v.TreeDensityPerSquareMetre;
            UCGroupedInspector.Readout("Tree density",
                density.ToString("0.####") + " stems/m2");

            float resolved = v.ResolvedTreeSpacingMetres;
            UCGroupedInspector.Readout("Tree spacing", resolved.ToString("0.00") + " m");

            // The authored spacing losing to the density-derived one is not an
            // error, but it does mean the field above is not what will be used.
            if (resolved > v.treeSpacingMetres + Mathf.Epsilon)
            {
                EditorGUILayout.HelpBox(
                    "Spacing is being set by density, not by the Tree Spacing field: " +
                    v.treesPerHectare.ToString("0") + " stems per hectare cannot sit " +
                    "closer than " + resolved.ToString("0.00") + " m. Raise the stem " +
                    "count to tighten the stand.",
                    MessageType.Info);
            }

            if (v.Weather == null || v.Lighting == null)
            {
                EditorGUILayout.HelpBox(
                    "No " + (v.Weather == null ? "weather" : "lighting") + " manager " +
                    "found in this world, so the resolve is using documented fallbacks " +
                    "rather than authored values.",
                    MessageType.Warning);
            }

            DrawDetailMemory(v);
        }

        /// <summary>
        /// One place to author prototypes (V-53). The four prototype lists - trees,
        /// shrubs, grass, objects - are edited on the world object, each with its own
        /// eco envelope. This inspector does not repeat them: it points at the world
        /// so an author who opened the manager looking for the lists is one click from
        /// where they actually live, and there is no second editor to drift out of
        /// sync with the first.
        /// </summary>
        private static void DrawLibraryLink(UCVegetationSystemManager v)
        {
            UniverseCreatorObject world = v.World;

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "Prototypes are authored on the world object.",
                    EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(world == null))
                {
                    // The null test is repeated on the click rather than left to the
                    // disabled scope. A disabled button cannot be pressed today, but
                    // the guard and the dereference belong in the same place: moving
                    // this row out of the scope would otherwise be a null crash that
                    // compiles.
                    if (GUILayout.Button("Select World", GUILayout.Width(96)) && world != null)
                    {
                        Selection.activeGameObject = world.gameObject;
                        EditorGUIUtility.PingObject(world.gameObject);
                    }
                }
            }

            if (world == null)
            {
                EditorGUILayout.HelpBox(
                    "No world object found above this manager, so there is nowhere to " +
                    "author the scatter library. Put this manager under a Universe " +
                    "Creator world.",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// Hosts the world inspector's own vegetation sections inside this panel
        /// (V-53 revised). The world object still STORES the four prototype lists,
        /// budgets and foliage settings; this draws the world editor's authoring
        /// surface for them so an author edits vegetation on the Vegetation manager
        /// while the code and its load-bearing helpers stay in one editor. When
        /// there is no world above the manager it falls back to the link panel,
        /// which explains where the library should live.
        /// </summary>
        private void DrawHostedLibrary(UCVegetationSystemManager v)
        {
            UniverseCreatorObject world = v.World;

            if (world == null)
            {
                DrawLibraryLink(v);
                return;
            }

            // Lazily build, or rebuild if the world under us changed, the cached
            // editor. CreateEditor over a UniverseCreatorObject resolves to its
            // UniverseCreatorInspector, which owns DrawHostedVegetationLibrary.
            if (hostedWorldEditor == null || hostedWorld != world)
            {
                if (hostedWorldEditor != null) Object.DestroyImmediate(hostedWorldEditor);
                hostedWorldEditor = UnityEditor.Editor.CreateEditor(world);
                hostedWorld = world;
            }

            UniverseCreatorInspector host = hostedWorldEditor as UniverseCreatorInspector;
            if (host == null)
            {
                // CustomEditor resolution failed; show the link rather than nothing.
                DrawLibraryLink(v);
                return;
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "Authored here; stored on the world object.", EditorStyles.miniLabel);

                if (GUILayout.Button("Select World", GUILayout.Width(96)))
                {
                    Selection.activeGameObject = world.gameObject;
                    EditorGUIUtility.PingObject(world.gameObject);
                }
            }

            host.DrawHostedVegetationLibrary(world);
        }

        /// <summary>
        /// The detail-layer memory bill, before the build rather than after it.
        /// This is RAM, not frame time: it is allocated when the layer is written
        /// and held while the chunk is resident, on screen or not. (V-29)
        /// </summary>
        private static void DrawDetailMemory(UCVegetationSystemManager v)
        {
            int layers = v.DetailLayerCount;
            int chunks = v.ResidentChunkCount;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Detail Memory", EditorStyles.boldLabel);

            if (layers <= 0 || chunks <= 0)
            {
                UCGroupedInspector.Readout("Detail layers",
                    layers <= 0 ? "none authored" : layers.ToString());
                return;
            }

            float mb = v.ResolvedDetailMemoryMB;
            float perLayerKB = v.detailResolution * (float)v.detailResolution *
                               UCVegetationSystemManager.BytesPerDetailCell / 1024f;

            UCGroupedInspector.Readout("Detail layers", layers.ToString());
            UCGroupedInspector.Readout("Resident chunks", chunks.ToString());
            UCGroupedInspector.Readout("Per layer per chunk", perLayerKB.ToString("0") + " KB");
            UCGroupedInspector.Readout("Total held",
                mb.ToString(mb < 10f ? "0.00" : "0") + " MB of " +
                v.detailMemoryCeilingMB.ToString("0") + " MB");

            if (!v.DetailMemoryOverCeiling) return;

            int fits = v.DetailResolutionUnderCeiling;

            // Over the ceiling is a hard warning and not an Info, because the
            // failure it predicts is an allocation that does not come back rather
            // than a frame that runs slowly.
            EditorGUILayout.HelpBox(
                "Detail layers would hold " + mb.ToString("0") + " MB, over the " +
                v.detailMemoryCeilingMB.ToString("0") + " MB ceiling. This is RAM held " +
                "while the chunks are resident, whether or not the grass is on screen.\n\n" +
                (fits >= 8
                    ? "A detail resolution of " + fits + " fits " + layers +
                      " layers across " + chunks + " chunks. " +
                      (fits <= UCVegetationSystemManager.MobileDetailResolution
                          ? "That is at or below the mobile default of " +
                            UCVegetationSystemManager.MobileDetailResolution + "."
                          : "The mobile default is " +
                            UCVegetationSystemManager.MobileDetailResolution + ".")
                    : "No usable resolution fits this many layers across this many " +
                      "chunks: reduce the detail species count or the world size."),
                MessageType.Warning);
        }
    }
}
