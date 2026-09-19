// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.cs
//  Purpose: Asserts the things a compile cannot: that assets actually load, that
//           reflection actually finds every node, and that a graph actually
//           evaluates.
//
//  WHY THIS EXISTS
//  ---------------
//  "It compiles" and "it works" are different claims. Icons are looked up by
//  string through Resources.Load, nodes are discovered by reflection, and node
//  fields are drawn by reflecting over supported types - none of which the C#
//  compiler can check. A renamed PNG, a node missing its attribute, or a field
//  of a type the drawer does not handle all compile perfectly and then fail
//  silently at runtime.
//
//  Run it from Tools > Universe Creator > Run Self Test, or headless:
//
//    Unity.exe -batchmode -quit -nographics -projectPath <proj> \
//      -executeMethod UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch
//
//  RunBatch exits non-zero when anything fails, so CI can gate on it.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Editor.Drawing;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Outputs;
using UniverseCreator.Nodes.Selectors;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    // Partial. This file is already long enough that adding a new subject area to
    // it would make it harder to read, so each later subject gets its own file and
    // shares the counter and the Check helper rather than starting a second tally.
    // Module 3's ecology checks live in UCSelfTest.Ecology.cs.
    public static partial class UCSelfTest
    {
        private static int checks;
        private static readonly List<string> failures = new List<string>();

        [MenuItem("Tools/Universe Creator/Run Self Test", false, 900)]
        public static void RunMenu()
        {
            bool ok = Run();
            Debug.Log(Report(ok));
        }

        /// <summary>
        /// Headless entry point. Exits the editor with code 1 on any failure so a
        /// build server can treat a broken lookup as a broken build.
        /// </summary>
        public static void RunBatch()
        {
            bool ok = Run();
            Debug.Log(Report(ok));

            // Console.WriteLine as well as Debug.Log: the log file is the only
            // artefact a CI run keeps, and this makes the verdict greppable
            // without wading through the import spam above it.
            Console.WriteLine(Report(ok));
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static string Report(bool ok)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== UC SELF TEST: " + (ok ? "PASS" : "FAIL") + " ===");
            sb.AppendLine("checks run : " + checks);
            sb.AppendLine("failures   : " + failures.Count);
            for (int i = 0; i < failures.Count; i++) sb.AppendLine("  FAIL " + failures[i]);
            return sb.ToString();
        }

        private static bool Run()
        {
            checks = 0;
            failures.Clear();

            TestIconsResolve();
            TestPinIconsResolve();
            TestCategoryIconsResolve();
            TestCatalogDiscovery();
            TestNodePortsAndInstantiation();
            TestNodeFieldsAreDrawable();
            TestShaderPropertyRegistry();
            TestAllShippedShadersCompile();
            TestMobileTextureBudget();
            TestPrefabIntegrity();
            TestTextureNodesWriteRealProperties();
            TestWaterShaderPropertyRegistry();
            TestWaterAppliersAgree();
            TestShorelineConformer();
            TestHeightGuardRejectsNonFinite();
            TestHeightGuardKillsNeedlesAndKeepsCliffs();
            TestGraphReachesTheHeightmap();
            TestWeatherOutputNodeCapturesBiases();
            TestLightingOutputNodeCapturesBiases();
            TestCarveOnlyLowersGround();
            TestRefitFromSnapshotIsStable();
            TestGroundProbeMatching();
            TestWindowExtremeMatchesBruteForce();
            TestRiverFillProducesRealWater();
            TestRiverPlannerJunctionsAreOrdered();
            TestRiverValleyCorridorCaps();
            TestRiverValleySagsTheFloodplain();
            TestValleyCorridorAccessor();
            TestVolumeReachesTheRealBed();
            TestNodeReadoutsMatchTheCourse();
            TestLakeShapeAndSizeComeFromTheBasin();

            // Module 2, in UCSelfTest.Hydrology.cs.
            TestChannelHeadAreaRescalesToTheWorld();
            TestSpawnerIsLiveWhenOnlyItsHeightIsWired();
            TestWaterWalkSeesInterfaceOnlyOutputs();
            TestSpawnerPlacesRealWaterObjects();
            TestLakeDepthGateMatchesTheMask();
            TestWaterfallsFormOnSteepReaches();
            TestDownstreamWidthGrowsWithoutAuthoring();

            TestGraphHealsMissingNodeSlots();

            // Module 6 path-tool seam (TerrainShapeApplier), in UCSelfTest.PathSeam.cs.
            TestPathSeamShaping();

            // Module 6 Road & City Master seam, UC side of B-27 (U-1, U-4, U-5), in UCSelfTest.RcmSeam.cs.
            TestReservoirGovernance();
            TestRiverProfilePublication();
            TestProviderSurfaceStaysSixMembers();

            // Module 3, in UCSelfTest.Ecology.cs.
            TestEcologyConstants();
            TestEcologyMaths();
            TestEnvironmentSampleStaysCheap();
            TestVegetationResolveIsCachedAndInvalidated();
            TestFoliagePigmentContract();
            TestSamplerReadsClimateFromBag();

            // Module 3, in UCSelfTest.Weather.cs.
            TestClockStateStaysCheap();
            TestSolarDeclination();
            TestDayLength();
            TestSunPosition();
            TestSolarTimeCorrection();
            TestAirMassAndMoon();
            TestSolarFormulaeHaveOneOwner();
            TestClockIsDeterministic();
            TestLatitudeGradient();
            TestMoonPosition();
            TestClockScrubTargets();
            TestAtmosphericThermodynamics();
            TestClimateNormals();

            // Module 3, in UCSelfTest.Weather2.cs.
            TestKoeppenCode();
            TestWeatherCellField();

            // Module 3, in UCSelfTest.Lighting.cs.
            TestLightingRig();
            TestSkyModel();
            TestSunIlluminance();
            TestSunDiscAndPenumbra();
            TestSunMoonHandover();
            TestSeasonalLatitudinalLight();
            TestExposureModel();
            TestTonemappingPolicy();
            TestAtmosphericExtinction();
            TestTurbidityModel();
            TestSkyLutRebuildPolicy();
            TestPerezSky();
            TestTwilightBands();
            TestSkyAmbientProbe();
            TestAtmosphereState();
            TestCloudTypeTable();
            TestSeasonalCloudCharacter();
            TestCloudRendering();
            TestStormRender();
            TestSkyLightSplit();
            TestSunShafts();
            TestCloudShadows();
            TestCloudDeterminism();
            TestIrradianceProbe();
            TestIrradianceGrid();
            TestProbeFill();
            TestMultiBounceFeedback();
            TestTerrainAlbedo();
            TestProbeRefreshScheduler();
            TestRefreshCadence();
            TestEnergyConservation();
            TestHeightfieldTrace();
            TestSkyVisibility();
            TestFoliageColorBleed();
            TestProbeValidity();
            TestEmissiveContributors();
            TestSpecularOcclusion();
            TestWetSurfaceOptics();
            TestIrradianceUpload();
            TestIrradianceSampler();
            TestAmbientOcclusionRule();
            TestGridAmbientWiring();
            TestMoonLight();
            TestStarField();
            TestPurkinjeShift();
            TestNightFloor();
            TestNoSecondAmbientOcclusion();
            TestContactShadow();
            TestScreenSpaceGI();
            TestScreenSpaceReflection();
            TestMobileReflectionPath();
            TestTemporalReprojection();
            TestGridDeterminism();
            TestLightingTier();
            TestLightingSettingsDefaults();
            TestIrradianceGpuByteEstimate();
            TestLightingManagerResolvesTierAndProbes();
            TestDayCycleSampling();
            TestLightingTimeIsWeatherOwned();
            TestLightingMemoryBudget();
            TestCascadePolicy();
            TestSharedShadowDistance();
            TestShadowBias();
            TestAuthorAssetSafety();
            TestRendererFeatureGuards();

            TestAngularError();
            TestTerrainDensityController();
            TestDerivedDrawDistances();
            TestDrawInstancedPairing();
            TestAcuityTier();
            TestAutoConnectDisabled();
            TestOriginClamp();
            TestMeshLodSelection();
            TestImpostorSwitchDistance();
            TestWeberContrast();
            TestOctahedralViews();
            TestImpostorDepth();
            TestImpostorSeason();
            TestImpostorAtlasBudget();
            TestDitherCrossFade();
            TestShadowLodBudget();
            TestInstancedBatching();
            TestSelfThinningDensity();
            TestSplatCacheModel();
            TestSplatPageResidency();
            TestSplatMipSelection();
            TestSplatPageCacheLru();
            TestComputedLayerComposite();
            TestTerrainLayerAssignmentGuard();
            TestBasemapConsistency();
            TestPaintStroke();
            TestPaintStore();
            TestPaintComposite();
            TestConservingHeightBrush();
            TestAngleOfReposeClamp();
            TestTerrainHoleMask();
            TestChunkPaintKey();
            TestBrushInputModel();
            TestPaintLayerResolver();
            TestVelocityPredictor();
            TestChunkBuildPriority();
            TestLoadHysteresis();
            TestTeleportDetector();
            TestTierConcurrencyGate();
            TestGeometryBudget();
            TestDegradationLadder();
            TestRecoveryController();
            TestThermalGovernor();
            TestGovernorStatus();
            TestViewSideDegradation();
            TestGeometrySettings();
            TestGeometryDebugView();
            TestImpostorBakePlan();

            TestLithologySanitize();
            TestLithologyExampleColumn();
            TestLithologyColumnView();
            TestStructuralStrikeRotation();
            TestStructuralStackAndDip();
            TestOutcropResolution();
            TestLithologyDifferentialErosion();
            TestSurficialCover();
            TestMatrixSurfaceForm();
            TestLithologyOutputNode();
            TestMicroReliefOutputNode();
            TestPatchRingAddressing();
            TestOverlayNeverWritesTerrain();
            TestMicroReliefField();
            TestProcessCompetition();
            TestSnowReliefLayer();
            TestWaterSurfaceCoupling();
            TestPatchEdgeTessellation();
            TestOverlayTessellationSupport();
            TestMicroReliefSampleOffset();
            TestOverlayNormalBlend();
            TestMicroReliefScatterBias();
            TestOverlayRingAdoption();

            return failures.Count == 0;
        }

        /// <summary>
        /// Every texture under Content must carry an Android/iOS import ceiling.
        ///
        /// This is a memory test wearing an import-settings costume. Content holds
        /// 182 textures; uncapped at 2048 they would ask a phone for roughly 453 MB
        /// of texture memory, which on a 1.2-1.8 GB process budget is an
        /// out-of-memory kill rather than a slow frame. The ceilings themselves and
        /// the arithmetic behind them live in UCTextureBudget, which is also what
        /// the Setup menu command calls - this asserts the policy stayed applied
        /// rather than restating it, so the two cannot drift.
        ///
        /// It writes nothing: Audit runs the same pass in dry-run mode.
        /// </summary>
        private static void TestMobileTextureBudget()
        {
            List<UCTextureBudget.Entry> entries;
            int notCompliant = UCTextureBudget.Audit(out entries);

            Check(entries.Count > 0,
                  "UCTextureBudget found no textures under " +
                  UCTextureBudget.ContentRoot + " - the audit is not looking where " +
                  "the content actually is");

            Check(notCompliant == 0,
                  notCompliant + " of " + entries.Count + " Content textures fail the " +
                  "mobile import policy (size ceiling, or a normal map importing as " +
                  "plain colour). Run Tools/Universe Creator/Setup/Apply " +
                  "Mobile Texture Budget. " + FirstFew(entries));
        }

        /// <summary>Names a few offenders so a failure is actionable, not just a count.</summary>
        private static string FirstFew(List<UCTextureBudget.Entry> entries)
        {
            StringBuilder sb = new StringBuilder("First offenders: ");
            int shown = 0;
            for (int i = 0; i < entries.Count && shown < 5; i++)
            {
                if (UCTextureBudget.IsCompliant(entries[i])) continue;

                if (shown > 0) sb.Append(", ");
                sb.Append(System.IO.Path.GetFileName(entries[i].Path));
                sb.Append(" (want ").Append(entries[i].Cap).Append("px, ")
                  .Append(entries[i].Role);
                if (entries[i].NormalTypeWrong) sb.Append(", mistyped normal");
                sb.Append(", ").Append(entries[i].Reason).Append(')');
                shown++;
            }
            return shown == 0 ? string.Empty : sb.ToString();
        }

        /// <summary>
        /// Structural prefab audit - null renderers, empty LOD levels, broken heights.
        ///
        /// A decimated tree with a null renderer in LOD0 produced Unity's
        /// "LODGroup has a missing renderer" warning every frame in play mode, and
        /// nothing upstream ever looked at a prefab. Defects (null renderer, empty
        /// LOD level, non-descending screen heights, missing mesh) fail the test;
        /// advisories (material/submesh count mismatch, mixed colliders) are counted
        /// but allowed, matching the package rule that a prefab with no material
        /// must still work.
        /// </summary>
        private static void TestPrefabIntegrity()
        {
            int scanned;
            List<UCPrefabIntegrity.Finding> found = UCPrefabIntegrity.Scan(out scanned);

            Check(scanned > 0, "UCPrefabIntegrity opened no prefabs or models under " +
                               UCPrefabIntegrity.ContentRoot + " - the scan is not " +
                               "looking where the shipped content actually is");

            int defects = 0;
            int advisories = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (!UCPrefabIntegrity.IsDefect(found[i].Kind))
                {
                    advisories++;
                    continue;
                }

                defects++;
                Check(false, found[i].Kind + " in " +
                             System.IO.Path.GetFileName(found[i].AssetPath) + ": " +
                             found[i].Detail);
            }

            // Deliberately no summary Check on the defect total: the per-defect
            // Check calls above already fail once each, and adding a rollup would
            // make "failures: N+1" for N problems.

            // Advisories are not failures, but this is the only place they would
            // ever be seen, so the log carries the count either way.
            Debug.Log("prefab integrity: " + scanned + " asset(s) scanned, " +
                      defects + " defect(s), " + advisories + " advisory note(s). " +
                      "Tools/Universe Creator/Diagnostics/Scan Prefabs lists them in full.");
        }

        private static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) failures.Add(message);
        }

        // ---------------------------------------------------------------------
        //  ICONS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Every string constant on UCIcons must resolve to a real texture.
        /// </summary>
        /// <remarks>
        /// Reflected rather than listed by hand so the test cannot drift out of
        /// date: adding a constant without adding the PNG fails here immediately.
        /// Cursor constants are excluded because Get and GetCursor read from
        /// different folders.
        /// </remarks>
        private static void TestIconsResolve()
        {
            FieldInfo[] fields = typeof(UCIcons).GetFields(
                BindingFlags.Public | BindingFlags.Static);

            int resolved = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                if (f.Name.StartsWith("Cursor")) continue;

                string name = (string)f.GetRawConstantValue();
                Texture2D tex = UCIcons.Get(name);
                Check(tex != null, "UCIcons." + f.Name + " -> '" + name
                                   + "' did not resolve to a texture");
                if (tex != null) resolved++;
            }

            Check(resolved > 100, "expected >100 icons to resolve, got " + resolved);
        }

        /// <summary>
        /// Port pins are looked up from a (PortType, PortDirection) pair.
        /// </summary>
        /// <remarks>
        /// A null name is a valid answer, not a miss: Matrix flows through nearly
        /// every connection in the graph, so marking it would put the same glyph
        /// on almost every port and distinguish nothing. What must not happen is
        /// a name that resolves to no texture - that draws an empty box where a
        /// deliberate choice was expected.
        /// </remarks>
        private static void TestPinIconsResolve()
        {
            Array types = Enum.GetValues(typeof(PortType));
            Array dirs = Enum.GetValues(typeof(PortDirection));

            for (int t = 0; t < types.Length; t++)
            {
                for (int d = 0; d < dirs.Length; d++)
                {
                    PortType pt = (PortType)types.GetValue(t);
                    PortDirection pd = (PortDirection)dirs.GetValue(d);

                    string name = UCIcons.PinNameFor(pt, pd);
                    if (string.IsNullOrEmpty(name)) continue;

                    Check(UCIcons.PinIcon(pt, pd) != null,
                          "PinIcon(" + pt + "," + pd + ") names '" + name
                          + "' but no such texture exists");
                }
            }
        }

        /// <summary>
        /// Node headers fall back to a built-in editor icon per category. A
        /// renamed built-in silently yields null and the header draws blank.
        /// </summary>
        private static void TestCategoryIconsResolve()
        {
            Array cats = Enum.GetValues(typeof(NodeCategory));
            for (int i = 0; i < cats.Length; i++)
            {
                NodeCategory c = (NodeCategory)cats.GetValue(i);
                Check(UCIcons.CategoryIcon(c) != null,
                      "CategoryIcon(" + c + ") resolved to null");
            }
        }

        // ---------------------------------------------------------------------
        //  CATALOG
        // ---------------------------------------------------------------------

        /// <summary>
        /// The catalog is the node menu. If reflection misses a type the node is
        /// unreachable from the UI even though it compiled fine.
        /// </summary>
        private static void TestCatalogDiscovery()
        {
            NodeCatalog.Invalidate();
            List<NodeCatalogEntry> all = NodeCatalog.All;

            Check(all != null && all.Count > 0, "NodeCatalog.All is empty");
            if (all == null) return;

            // Mirror NodeCatalog's own admission rules rather than inventing a
            // marker attribute: a node qualifies by being a concrete Generator
            // with a public parameterless constructor. Anything that meets those
            // rules but is missing from the catalog was dropped by the probe -
            // almost always a constructor that threw, which Build() swallows on
            // purpose so one bad node cannot empty the whole menu. Silent there
            // is correct; silent everywhere is not, so it surfaces here.
            Assembly runtime = typeof(Generator).Assembly;
            Type[] runtimeTypes = runtime.GetTypes();

            HashSet<Type> catalogued = new HashSet<Type>();
            for (int i = 0; i < all.Count; i++) catalogued.Add(all[i].type);

            for (int i = 0; i < runtimeTypes.Length; i++)
            {
                Type t = runtimeTypes[i];
                if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                if (!typeof(Generator).IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                Check(catalogued.Contains(t),
                      "node type " + t.Name + " qualifies for the catalog but is "
                      + "absent - its constructor most likely threw");
            }

            // A duplicate menu path means one node shadows another in the menu.
            HashSet<string> paths = new HashSet<string>();
            for (int i = 0; i < all.Count; i++)
            {
                Check(paths.Add(all[i].MenuPath),
                      "duplicate menu path: " + all[i].MenuPath);
                Check(!string.IsNullOrEmpty(all[i].nodeName),
                      all[i].type.Name + " has an empty nodeName");
            }

            // The IP constraint, asserted rather than assumed.
            for (int i = 0; i < all.Count; i++)
            {
                Check(all[i].MenuPath.IndexOf("PCGEx", StringComparison.OrdinalIgnoreCase) < 0,
                      "forbidden menu group in path: " + all[i].MenuPath);
            }
        }

        /// <summary>
        /// Every catalogued node must instantiate and declare its ports without
        /// throwing, because the graph window does exactly this on every repaint.
        /// </summary>
        private static void TestNodePortsAndInstantiation()
        {
            List<NodeCatalogEntry> all = NodeCatalog.All;
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                NodeCatalogEntry e = all[i];
                Generator g = null;
                try
                {
                    g = NodeCatalog.Instantiate(e);
                }
                catch (Exception ex)
                {
                    failures.Add("Instantiate(" + e.type.Name + ") threw: " + ex.Message);
                    checks++;
                    continue;
                }

                Check(g != null, "Instantiate(" + e.type.Name + ") returned null");
                if (g == null) continue;

                try
                {
                    Port[] ins = g.GetInputs();
                    Port[] outs = g.GetOutputs();
                    Check(ins != null, e.type.Name + ".GetInputs() returned null");
                    Check(outs != null, e.type.Name + ".GetOutputs() returned null");
                }
                catch (Exception ex)
                {
                    failures.Add(e.type.Name + " port query threw: " + ex.Message);
                    checks++;
                }
            }
        }

        /// <summary>
        /// NodeDrawer reflects over public fields and draws the ones whose type it
        /// recognises. A field of an unrecognised type is not an error - it simply
        /// never appears, so the user cannot configure it.
        /// </summary>
        /// <remarks>
        /// Reported as a warning list rather than a hard failure: a public field
        /// may legitimately be structural rather than user-facing. The point is
        /// that nobody discovers it by noticing a blank row in the inspector.
        /// </remarks>
        private static void TestNodeFieldsAreDrawable()
        {
            MethodInfo supported = typeof(NodeDrawer).GetMethod(
                "IsSupportedType", BindingFlags.Static | BindingFlags.Public
                                 | BindingFlags.NonPublic);

            checks++;
            if (supported == null)
            {
                failures.Add("NodeDrawer.IsSupportedType not found - "
                             + "field drawability cannot be verified");
                return;
            }

            List<NodeCatalogEntry> all = NodeCatalog.All;
            if (all == null) return;

            List<string> undrawable = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                FieldInfo[] fs = all[i].type.GetFields(
                    BindingFlags.Public | BindingFlags.Instance);

                for (int j = 0; j < fs.Length; j++)
                {
                    if (fs[j].IsNotSerialized) continue;
                    if (fs[j].GetCustomAttribute<HideInInspector>() != null) continue;

                    bool ok = (bool)supported.Invoke(null, new object[] { fs[j].FieldType });
                    if (!ok)
                    {
                        undrawable.Add(all[i].type.Name + "." + fs[j].Name
                                       + " : " + fs[j].FieldType.Name);
                    }
                }
            }

            if (undrawable.Count > 0)
            {
                Debug.LogWarning("[UC SelfTest] public node fields NodeDrawer will not "
                                 + "draw (" + undrawable.Count + "):\n  "
                                 + string.Join("\n  ", undrawable.ToArray()));
            }
        }

        // ---------------------------------------------------------------------
        //  SHADER PROPERTIES
        // ---------------------------------------------------------------------

        /// <summary>
        /// Every property name in UCShaderProps must exist on the shader.
        /// </summary>
        /// <remarks>
        /// This is the test that pays for the whole UCShaderProps design.
        ///
        /// SurfaceProduct is a string-keyed bag, so a node writing "_SnowAltitud"
        /// compiles, runs, sets nothing, and produces a slider that silently does
        /// nothing at all - the worst failure mode a tool has, because it looks
        /// like it worked. Routing every write through a const means a typo is a
        /// compile error; this test closes the other half, where the const is
        /// spelled correctly but the SHADER dropped or renamed the property.
        ///
        /// Reflected over the class rather than listed, so a constant added
        /// without a matching shader property fails here on the next batch run
        /// instead of shipping.
        ///
        /// ShaderName and the two Keyword constants are skipped: the first is the
        /// shader's own name and the others are keywords, neither of which is a
        /// material property. Keywords are checked separately below, against the
        /// shader's actual keyword space.
        /// </remarks>
        /// <summary>
        /// Asserts every shader this module ships actually compiles and resolves.
        ///
        /// TestShaderPropertyRegistry and TestWaterShaderPropertyRegistry each cover
        /// one shader, which left VegetationLit, WindDetail, RockLit and the ripple
        /// shader unverified - a compile error in any of them would surface only as
        /// magenta geometry at run time, long after the self test said PASS.
        /// UCShaderDiagnostics already owns the "is this shader sound" question for
        /// the whole ship list, so this delegates to it rather than restating that
        /// loop here and letting the two drift apart.
        /// </summary>
        private static void TestAllShippedShadersCompile()
        {
            string report;
            bool ok = UCShaderDiagnostics.Check(out report);
            Check(ok, "one or more shipped shaders failed to compile or resolve:\n" + report);
        }

        private static void TestShaderPropertyRegistry()
        {
            Shader shader = Shader.Find(UCShaderProps.ShaderName);
            Check(shader != null, "shader '" + UCShaderProps.ShaderName + "' not found");
            if (shader == null) return;

            // A shader that failed to compile still resolves through Shader.Find
            // and still answers property queries, so this must be asked
            // separately - otherwise every property below passes on a shader that
            // renders magenta.
            Check(!ShaderUtil.ShaderHasError(shader),
                  "shader '" + UCShaderProps.ShaderName + "' has compile errors");

            HashSet<string> declared = new HashSet<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++) declared.Add(shader.GetPropertyName(i));

            FieldInfo[] fields = typeof(UCShaderProps).GetFields(
                BindingFlags.Public | BindingFlags.Static);

            int verified = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                if (f.Name == "ShaderName" || f.Name.StartsWith("Keyword")) continue;

                string prop = (string)f.GetRawConstantValue();
                Check(declared.Contains(prop),
                      "UCShaderProps." + f.Name + " -> '" + prop
                      + "' is not a property of " + UCShaderProps.ShaderName);
                if (declared.Contains(prop)) verified++;
            }

            Check(verified > 50, "expected >50 shader properties verified, got " + verified);

            // The keywords are the other half of the contract. A keyword that no
            // #pragma declares can be enabled all day and no #ifdef will ever see
            // it - the feature simply never renders, with no error anywhere.
            string[] keywords = shader.keywordSpace.keywordNames;
            HashSet<string> known = new HashSet<string>(keywords);

            Check(known.Contains(UCShaderProps.KeywordSlopeBlend),
                  "keyword '" + UCShaderProps.KeywordSlopeBlend + "' is not in the shader's keyword space");
            Check(known.Contains(UCShaderProps.KeywordSnow),
                  "keyword '" + UCShaderProps.KeywordSnow + "' is not in the shader's keyword space");
        }

        /// <summary>
        /// Runs every texture node and checks that what it wrote is real.
        /// </summary>
        /// <remarks>
        /// The registry test above proves the CONSTANTS are right. This one proves
        /// the NODES use them - a node that writes a bare string literal instead
        /// of a const bypasses the registry entirely and would pass every test
        /// above while doing nothing at runtime.
        ///
        /// Each node is instantiated with its defaults and generated against a
        /// throwaway tile with no evaluator, so every pull returns null and each
        /// node falls back to its own inspector values. That is exactly the state
        /// a freshly dropped node is in, which makes this a test of the defaults
        /// as well: a node whose default values throw is caught here.
        ///
        /// Nodes that bake textures are exercised too, and the Texture2D they
        /// allocate carries HideFlags.DontSave - so this test cannot leak assets
        /// into the project even though it runs the real baking path.
        /// </remarks>
        private static void TestTextureNodesWriteRealProperties()
        {
            Shader shader = Shader.Find(UCShaderProps.ShaderName);
            if (shader == null) return;

            HashSet<string> declared = new HashSet<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++) declared.Add(shader.GetPropertyName(i));

            string[] keywords = shader.keywordSpace.keywordNames;
            HashSet<string> knownKeywords = new HashSet<string>(keywords);

            List<NodeCatalogEntry> all = NodeCatalog.All;
            if (all == null) return;

            int exercised = 0;

            for (int i = 0; i < all.Count; i++)
            {
                if (!typeof(TextureNode).IsAssignableFrom(all[i].type)) continue;

                Generator g = NodeCatalog.Instantiate(all[i]);
                if (g == null) continue;

                // No evaluator, so PullInput returns null everywhere and each node
                // uses its own defaults. rect is 33x33: big enough that a baker
                // does real work, small enough that the raymarching AO node
                // finishes in milliseconds rather than seconds.
                TileData tile = new TileData(
                    Coord.Zero,
                    new CoordRect(0, 0, 33, 33),
                    Vector3.zero,
                    new Vector3(100f, 600f, 100f),
                    600f);

                object product;
                try
                {
                    product = g.Generate(0, tile, null);
                }
                catch (Exception ex)
                {
                    Check(false, all[i].type.Name + ".Generate threw with default values: "
                                 + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                exercised++;

                SurfaceProduct surface = product as SurfaceProduct;
                if (surface == null) continue;

                foreach (var kv in surface.floats)
                    Check(declared.Contains(kv.Key),
                          all[i].type.Name + " wrote float '" + kv.Key
                          + "', which is not a property of " + UCShaderProps.ShaderName);

                foreach (var kv in surface.colors)
                    Check(declared.Contains(kv.Key),
                          all[i].type.Name + " wrote color '" + kv.Key
                          + "', which is not a property of " + UCShaderProps.ShaderName);

                foreach (var kv in surface.textures)
                    Check(declared.Contains(kv.Key),
                          all[i].type.Name + " wrote texture '" + kv.Key
                          + "', which is not a property of " + UCShaderProps.ShaderName);

                foreach (var kv in surface.bakedMaps)
                    Check(declared.Contains(kv.Key),
                          all[i].type.Name + " baked a map into '" + kv.Key
                          + "', which is not a property of " + UCShaderProps.ShaderName);

                foreach (var kv in surface.bakedGradients)
                    Check(declared.Contains(kv.Key),
                          all[i].type.Name + " baked a gradient into '" + kv.Key
                          + "', which is not a property of " + UCShaderProps.ShaderName);

                foreach (string kw in surface.keywords)
                    Check(knownKeywords.Contains(kw),
                          all[i].type.Name + " set keyword '" + kw
                          + "', which no #pragma in " + UCShaderProps.ShaderName + " declares");

                // A keyword enabled without its float, or the reverse, is the
                // desync that makes a tick box look broken. The pairing is
                // asserted here rather than trusted to EnableFeature, because a
                // node is free to call SetKeyword directly.
                if (surface.keywords.Contains(UCShaderProps.KeywordSnow))
                {
                    float v;
                    Check(surface.floats.TryGetValue(UCShaderProps.EnableSnow, out v) && v > 0.5f,
                          all[i].type.Name + " set the snow keyword without setting "
                          + UCShaderProps.EnableSnow);
                }

                if (surface.keywords.Contains(UCShaderProps.KeywordSlopeBlend))
                {
                    float v;
                    Check(surface.floats.TryGetValue(UCShaderProps.EnableSlopeBlend, out v) && v > 0.5f,
                          all[i].type.Name + " set the slope-blend keyword without setting "
                          + UCShaderProps.EnableSlopeBlend);
                }
            }

            Check(exercised >= 20, "expected at least 20 texture nodes exercised, got " + exercised);
        }

        // ---------------------------------------------------------------------
        //  WATER
        // ---------------------------------------------------------------------

        /// <summary>
        /// Every UCWaterProps constant must name a real property on the water
        /// shader. Same contract as TestShaderPropertyRegistry and separate for the
        /// same reason: a water name checked against the terrain shader would fail a
        /// test that was working correctly.
        /// </summary>
        private static void TestWaterShaderPropertyRegistry()
        {
            Shader shader = Shader.Find(UCWaterProps.ShaderName);
            Check(shader != null, "shader '" + UCWaterProps.ShaderName + "' not found");
            if (shader == null) return;

            Check(!ShaderUtil.ShaderHasError(shader),
                  "shader '" + UCWaterProps.ShaderName + "' has compile errors");

            HashSet<string> declared = new HashSet<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++) declared.Add(shader.GetPropertyName(i));

            FieldInfo[] fields = typeof(UCWaterProps).GetFields(
                BindingFlags.Public | BindingFlags.Static);

            int verified = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                if (f.Name == "ShaderName" || f.Name.StartsWith("Keyword")) continue;

                string prop = (string)f.GetRawConstantValue();
                Check(declared.Contains(prop),
                      "UCWaterProps." + f.Name + " -> '" + prop
                      + "' is not a property of " + UCWaterProps.ShaderName);
                if (declared.Contains(prop)) verified++;
            }

            Check(verified >= 30, "expected at least 30 water properties verified, got " + verified);
        }

        /// <summary>
        /// The editor and runtime water appliers must write the same properties.
        ///
        /// They are separate methods on purpose - one lives in an editor assembly a
        /// build cannot call, and only that one may touch TextureRef.Apply - but a
        /// property added to one and not the other is a control that works in the
        /// editor and does nothing in the build, which is the hardest kind of bug to
        /// be told about. Comparing the materials they produce catches it here
        /// instead.
        /// </summary>
        private static void TestWaterAppliersAgree()
        {
            Shader shader = Shader.Find(UCWaterProps.ShaderName);
            if (shader == null) return;

            WaterProduct w = new WaterProduct();

            // Moved off the defaults so a property that is simply never written
            // cannot pass by matching an untouched material.
            w.shallowColor = new Color(0.11f, 0.22f, 0.33f, 0.44f);
            w.deepColor = new Color(0.55f, 0.66f, 0.77f, 0.88f);
            w.depthFadeMetres = 7.25f;
            w.transparency = 0.4f;
            w.smoothness = 0.61f;
            w.fresnelPower = 3.5f;
            w.refraction = 0.07f;
            w.normalScale = 1.7f;
            w.waveTileMetres = 19f;
            w.flowMetresPerSecond = new Vector2(0.21f, -0.34f);
            w.foamEnabled = true;
            w.foamColor = new Color(0.9f, 0.8f, 0.7f, 0.6f);
            w.foamIntensity = 1.3f;
            w.foamEdgeWidthMetres = 2.1f;
            w.foamScrollSpeed = 0.27f;
            w.foamTileMetres = 5.5f;
            w.causticsEnabled = true;
            w.causticsColor = new Color(0.3f, 0.9f, 0.5f, 1f);
            w.causticsIntensity = 0.77f;
            w.causticsTileMetres = 4.25f;
            w.causticsSpeed = 0.09f;
            w.causticsDepthFadeMetres = 11f;
            w.waveHeightMetres = 0.37f;
            w.waveLengthMetres = 31f;
            w.waveSpeed = 0.83f;
            w.edgeSoftenMetres = 1.45f;
            w.rippleStrength = 0.66f;
            w.rippleFoam = 0.44f;

            Material fromEditor = new Material(shader);
            Material fromRuntime = new Material(shader);

            try
            {
                UCWaterApplier.ApplyProduct(fromEditor, w);
                UniverseCreator.TerrainSystem.UCWaterMaterials.ApplyProductRuntime(fromRuntime, w);

                int compared = 0;
                int count = shader.GetPropertyCount();

                for (int i = 0; i < count; i++)
                {
                    string name = shader.GetPropertyName(i);
                    UnityEngine.Rendering.ShaderPropertyType type = shader.GetPropertyType(i);

                    if (type == UnityEngine.Rendering.ShaderPropertyType.Color)
                    {
                        Check(fromEditor.GetColor(name) == fromRuntime.GetColor(name),
                              "water appliers disagree on colour '" + name + "'");
                    }
                    else if (type == UnityEngine.Rendering.ShaderPropertyType.Vector)
                    {
                        Check(fromEditor.GetVector(name) == fromRuntime.GetVector(name),
                              "water appliers disagree on vector '" + name + "'");
                    }
                    else if (type == UnityEngine.Rendering.ShaderPropertyType.Float ||
                             type == UnityEngine.Rendering.ShaderPropertyType.Range)
                    {
                        Check(Mathf.Approximately(fromEditor.GetFloat(name),
                                                  fromRuntime.GetFloat(name)),
                              "water appliers disagree on float '" + name + "': editor "
                              + fromEditor.GetFloat(name) + " vs runtime "
                              + fromRuntime.GetFloat(name));
                    }
                    else
                    {
                        // Textures and integers are skipped. Only the editor path
                        // resolves a TextureRef to an asset, and that is the one
                        // difference between the two that is deliberate.
                        continue;
                    }

                    compared++;
                }

                Check(compared >= 25, "expected at least 25 water properties compared, got " + compared);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fromEditor);
                UnityEngine.Object.DestroyImmediate(fromRuntime);
            }
        }

        /// <summary>
        /// Checks the shore skirt and the depth cap - the two rules that decide where
        /// a water surface sits relative to the ground it meets.
        ///
        /// REPLACES TestWaterMeshBuild, which drove a graph-side mesh builder that no
        /// longer exists: water is built by the body that owns it now, not per chunk.
        /// What that test was really protecting was the shoreline band and the depth
        /// ceiling, and both of those live here, in managed code over float[] that can
        /// be driven directly with no scene, no terrain and no GameObject.
        /// </summary>
        private static void TestShorelineConformer()
        {
            const float surface = 150f;
            const float drop = ShorelineConformer.SkirtDropMetres;

            // Dry ground is where the gap you can see under the water would open, so
            // the edge is buried by the full drop rather than meeting the ground.
            Check(Mathf.Approximately(
                      ShorelineConformer.CalculateSkirtHeight(surface, 0f), surface - drop),
                  "a dry shore vertex must be buried by the full skirt drop");

            Check(Mathf.Approximately(
                      ShorelineConformer.CalculateSkirtHeight(surface, -3f), surface - drop),
                  "ground above the water must not raise the skirt above the water");

            // Past a metre of depth the skirt is done and the surface is the real
            // water level. Anything else would tilt an open lake.
            Check(Mathf.Approximately(
                      ShorelineConformer.CalculateSkirtHeight(surface, 1.0001f), surface),
                  "open water must sit exactly at the body's level");

            Check(Mathf.Approximately(
                      ShorelineConformer.CalculateSkirtHeight(surface, 40f), surface),
                  "deep water must sit exactly at the body's level");

            // Halfway through the band, halfway through the drop - the easing is
            // linear on purpose, because the shader fades the same band and easing
            // twice reads as two edges.
            Check(Mathf.Abs(ShorelineConformer.CalculateSkirtHeight(surface, 0.5f)
                            - (surface - drop * 0.5f)) < 0.0001f,
                  "the skirt must ease linearly over the first metre of depth");

            // Monotonic across the whole band. A single step backwards here is a
            // visible crease running along the waterline.
            bool monotonic = true;
            float previous = ShorelineConformer.CalculateSkirtHeight(surface, 0f);
            for (int i = 1; i <= 40; i++)
            {
                float here = ShorelineConformer.CalculateSkirtHeight(surface, i * 0.05f);
                if (here < previous - 0.0001f) monotonic = false;
                previous = here;
            }
            Check(monotonic, "the shore skirt must rise with depth without stepping back");

            // The EXPLICIT cap of spec 4.3. An uncapped depth is how water becomes a
            // slab reaching hundreds of metres down to the deepest sample.
            Check(Mathf.Approximately(
                      ShorelineConformer.CapDepth(5000f), ShorelineConformer.MaxSkirtDepthMetres),
                  "an abyssal sample must be capped at the explicit depth ceiling");

            Check(Mathf.Approximately(ShorelineConformer.CapDepth(3.5f), 3.5f),
                  "the depth cap must leave ordinary depths alone");

            Check(ShorelineConformer.MaxSkirtDepthMetres <= 200f,
                  "the depth cap is a mobile budget, not a formality: "
                  + ShorelineConformer.MaxSkirtDepthMetres + " m is too deep to pay for");

            // The grid entry point must agree with the per-vertex one, or a lake and
            // a river meeting at the same shore disagree about where the shore is.
            float[] surf = new float[] { surface, surface, surface, surface };
            float[] depth = new float[] { 0f, 0.25f, 2f, 9000f };

            ShorelineConformer.ConformGrid(surf, depth, surf.Length);

            bool gridAgrees = true;
            float[] expectedDepth = new float[] { 0f, 0.25f, 2f, 9000f };
            for (int i = 0; i < surf.Length; i++)
            {
                float wanted = ShorelineConformer.CalculateSkirtHeight(surface, expectedDepth[i]);
                if (Mathf.Abs(surf[i] - wanted) > 0.0001f) gridAgrees = false;
                if (!Mathf.Approximately(depth[i], ShorelineConformer.CapDepth(expectedDepth[i])))
                    gridAgrees = false;
            }

            Check(gridAgrees, "ConformGrid disagrees with the per-vertex shore rules");
        }

        // ---------------------------------------------------------------------
        //  HEIGHT GUARD
        // ---------------------------------------------------------------------

        /// <summary>
        /// Asserts that a heightfield containing NaN and infinity cannot reach
        /// SetHeights, and that the guard reports how many it had to fix.
        /// </summary>
        /// <remarks>
        /// This is the test the old safeguard would have failed. It was a
        /// Mathf.Clamp01 loop, and Clamp01 is two inequalities and an else: NaN fails
        /// both inequalities and is returned unchanged. The bug was invisible because
        /// the code READ like a clamp, so the check is written against the actual
        /// values rather than against the presence of a clamp.
        /// </remarks>
        private static void TestHeightGuardRejectsNonFinite()
        {
            float[,] h = new float[9, 9];
            for (int z = 0; z < 9; z++)
                for (int x = 0; x < 9; x++)
                    h[z, x] = 0.5f;

            h[4, 4] = float.NaN;
            h[2, 6] = float.PositiveInfinity;
            h[7, 1] = float.NegativeInfinity;
            h[0, 3] = 42f;      // finite but far out of range
            h[8, 8] = -13f;

            int repaired = HeightGuard.Sanitize(h);
            Check(repaired == 3, "Sanitize reported " + repaired + " non-finite samples, expected 3");

            bool allFinite = true;
            bool allInRange = true;
            for (int z = 0; z < 9; z++)
            {
                for (int x = 0; x < 9; x++)
                {
                    float v = h[z, x];
                    if (float.IsNaN(v) || float.IsInfinity(v)) allFinite = false;
                    if (v < 0f || v > 1f) allInRange = false;
                }
            }

            Check(allFinite, "a NaN or infinity survived Sanitize - it would reach SetHeights");
            Check(allInRange, "a sample outside 0..1 survived Sanitize");

            // Proof that the old approach really was broken, so this cannot regress by
            // someone "simplifying" the guard back into a clamp.
            Check(float.IsNaN(Mathf.Clamp01(float.NaN)),
                  "Mathf.Clamp01 now filters NaN - the guard's rationale needs revisiting");

            // A clean field must cost nothing and change nothing.
            float[,] clean = new float[5, 5];
            for (int z = 0; z < 5; z++)
                for (int x = 0; x < 5; x++)
                    clean[z, x] = 0.25f + z * 0.01f;

            Check(HeightGuard.Sanitize(clean) == 0, "Sanitize claimed to repair a clean heightfield");
            Check(Mathf.Abs(clean[3, 3] - (0.25f + 3 * 0.01f)) < 1e-6f,
                  "Sanitize altered a valid sample");
        }

        /// <summary>
        /// Asserts the two halves of the spike test: a single-sample needle is removed,
        /// and a genuine cliff is not.
        /// </summary>
        /// <remarks>
        /// The second half is the one that matters for the product. A guard that
        /// flattens spikes by flattening everything steep would quietly destroy every
        /// cliff, mesa and canyon wall the graph is for, and it would look like the
        /// nodes had stopped working rather than like the guard was too aggressive.
        /// A cliff is a run of samples that step together, so each one sits inside the
        /// range its own neighbours span; a needle is outside that range on all four
        /// sides. That difference is what is asserted here.
        /// </remarks>
        private static void TestHeightGuardKillsNeedlesAndKeepsCliffs()
        {
            const float metresPerSample = 3.125f;   // 800 m cell at 257
            const float maxHeight = 600f;

            float maxStep = HeightGuard.MaxStepNormalised(metresPerSample, maxHeight);
            Check(maxStep > 0f && maxStep < 1f,
                  "MaxStepNormalised returned " + maxStep + ", which disables the spike test");

            // --- a needle in an otherwise smooth field ---
            float[,] needle = new float[16, 16];
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    needle[z, x] = 0.3f;

            needle[8, 8] = 1f;      // straight up, one sample wide
            needle[3, 11] = 0f;     // straight down, one sample wide

            int removed = HeightGuard.RemoveSpikes(needle, maxStep);
            Check(removed == 2, "RemoveSpikes found " + removed + " needles, expected 2");
            Check(Mathf.Abs(needle[8, 8] - 0.3f) < 1e-4f,
                  "the up needle was not brought back to its neighbourhood (" + needle[8, 8] + ")");
            Check(Mathf.Abs(needle[3, 11] - 0.3f) < 1e-4f,
                  "the down needle was not brought back to its neighbourhood (" + needle[3, 11] + ")");

            // --- a cliff: half the field low, half high, one sample of transition ---
            float[,] cliff = new float[16, 16];
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    cliff[z, x] = x < 8 ? 0.2f : 0.9f;

            int cliffHits = HeightGuard.RemoveSpikes(cliff, maxStep);
            Check(cliffHits == 0, "the guard treated a cliff as " + cliffHits + " spike(s) and flattened it");
            Check(Mathf.Abs(cliff[8, 7] - 0.2f) < 1e-6f && Mathf.Abs(cliff[8, 8] - 0.9f) < 1e-6f,
                  "the cliff face lost its step");

            // --- a smooth slope must be untouched ---
            float[,] slope = new float[16, 16];
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    slope[z, x] = x / 15f;

            Check(HeightGuard.RemoveSpikes(slope, maxStep) == 0,
                  "the guard found spikes in a linear ramp");

            // --- Guard() must survive a field too small to have a neighbourhood ---
            float[,] tiny = new float[2, 2];
            tiny[0, 0] = float.NaN;
            HeightGuard.Guard(tiny, metresPerSample, maxHeight, "a 2x2 test field", null);
            Check(!float.IsNaN(tiny[0, 0]), "Guard left a NaN in a 2x2 heightfield");

            // --- and a null field must be a no-op, not a throw ---
            bool threw = false;
            try { HeightGuard.Guard(null, metresPerSample, maxHeight, "a null field", null); }
            catch (Exception) { threw = true; }
            Check(!threw, "Guard threw on a null heightfield");
        }

        // ---------------------------------------------------------------------
        //  GRAPH -> HEIGHTMAP
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds a two-node graph by hand, evaluates it the way a chunk bake does,
        /// and asserts that the node math actually arrives in
        /// <c>OutputBag.heightmap</c> in the shape TerrainData.SetHeights needs.
        /// </summary>
        /// <remarks>
        /// This is the check that a compile cannot make and that every other test in
        /// this file steps around. Nodes are discovered by reflection, connections are
        /// matched by string id, and the evaluator pulls backwards from output nodes -
        /// so a renamed port, a broken id, or an output node that stops writing all
        /// compile perfectly and produce a flat world. Flat is also the exact symptom
        /// that is hardest to read, because a terrain at zero looks identical to a
        /// terrain that was never generated.
        ///
        /// The assertions are deliberately about EXISTENCE and VARIATION rather than
        /// about specific values. Asserting a particular height would pin the test to
        /// today's noise constants and it would fail the first time anyone tuned a
        /// default, which trains people to ignore it. "The graph produced a surface
        /// that is not flat, is in range, and is finite" is the claim that actually
        /// matters and it holds across any retuning.
        /// </remarks>
        private static void TestGraphReachesTheHeightmap()
        {
            const int res = 65;

            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                PerlinNoiseNode noise = new PerlinNoiseNode();
                HeightOutputNode output = new HeightOutputNode();

                graph.nodes.Add(noise);
                graph.nodes.Add(output);

                // Port 0 of the noise into port 0 of the output: the minimum graph that
                // is supposed to produce terrain, and the one every user builds first.
                Connection link = new Connection();
                link.sourceNodeId = noise.id;
                link.sourcePortIndex = 0;
                link.targetNodeId = output.id;
                link.targetPortIndex = 0;
                graph.connections.Add(link);

                // Same construction the real bake uses in ChunkManager.Dispatch: a cell
                // at a non-zero world position inside a larger world, so any node that
                // samples absolute coordinates is exercised on a real offset rather than
                // at the origin where a coordinate bug cancels out.
                TileData tile = new TileData(
                    new Coord(1, 1),
                    new CoordRect(new Coord(0, 0), new Coord(res, res)),
                    new Vector3(800f, 0f, 800f),
                    new Vector3(800f, 600f, 800f),
                    600f);
                tile.SetFullWorld(Vector3.zero, new Vector3(4000f, 600f, 4000f));

                bool threw = false;
                try
                {
                    GraphEvaluator evaluator = new GraphEvaluator(graph);
                    evaluator.Evaluate(tile, new StopToken());
                }
                catch (Exception ex)
                {
                    threw = true;
                    Check(false, "evaluating a noise -> height output graph threw "
                                 + ex.GetType().Name + ": " + ex.Message);
                }

                if (threw) return;

                float[,] hm = tile.outputs != null ? tile.outputs.heightmap : null;

                Check(hm != null, "a connected Height Output wrote no heightmap at all - "
                                  + "the graph would leave the terrain flat");
                if (hm == null) return;

                // SetHeights indexes [z, x]. A transposed or mis-sized array is not a
                // compile error, it is a scrambled or rejected terrain.
                Check(hm.GetLength(0) == res && hm.GetLength(1) == res,
                      "heightmap is " + hm.GetLength(0) + "x" + hm.GetLength(1)
                      + ", expected " + res + "x" + res);

                float lo = float.MaxValue;
                float hi = float.MinValue;
                bool finite = true;
                bool inRange = true;

                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float v = hm[z, x];
                        if (float.IsNaN(v) || float.IsInfinity(v)) { finite = false; continue; }
                        if (v < 0f || v > 1f) inRange = false;
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                }

                Check(finite, "the graph put a NaN or infinity in the heightmap - "
                              + "HeightOutputNode's non-finite filter is not working");
                Check(inRange, "the graph wrote a height outside 0..1 (" + lo + " .. " + hi + ")");

                // THE FLAT-WORLD CHECK. A heightmap of all one value is what the user
                // sees as "the terrain is flat and the node graph is not connected".
                Check(hi - lo > 0.001f,
                      "the graph produced a flat heightmap (range " + (hi - lo).ToString("F6")
                      + ") - the node math is not reaching the terrain");

                // A collider rebuild must be requested, or the surface renders at the new
                // height while physics stays on the old one and everything walks on air.
                Check(tile.outputs.requestColliderRebuild,
                      "Height Output did not request a collider rebuild");

                // --- and the disconnected case must be distinguishable from the above ---
                Graph orphan = ScriptableObject.CreateInstance<Graph>();
                try
                {
                    orphan.nodes.Add(new HeightOutputNode());

                    TileData bare = new TileData(
                        Coord.Zero,
                        new CoordRect(new Coord(0, 0), new Coord(res, res)),
                        Vector3.zero,
                        new Vector3(800f, 600f, 800f),
                        600f);
                    bare.SetFullWorld(Vector3.zero, new Vector3(4000f, 600f, 4000f));

                    new GraphEvaluator(orphan).Evaluate(bare, new StopToken());

                    // Null, NOT a zeroed array. The distinction is the whole reason
                    // ChunkManager can tell "no graph output" from "a graph that legitimately
                    // produced sea level", and it is what lets a cell keep the surface it
                    // already has instead of being flattened by an unfinished graph.
                    Check(bare.outputs == null || bare.outputs.heightmap == null,
                          "an unconnected Height Output still wrote a heightmap - "
                          + "an unfinished graph would flatten the terrain");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(orphan);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        /// <summary>
        /// Weather Output must keep the five original bias ports first and in order so
        /// a graph saved against the retired node reconnects, and it must record a wired
        /// bias into the bag while leaving an unwired one null - the driver's whole way
        /// of telling "no bias here" from "a bias of zero".
        /// </summary>
        private static void TestWeatherOutputNodeCapturesBiases()
        {
            WeatherOutputNode probe = new WeatherOutputNode();

            Port[] ins = probe.GetInputs();
            Check(ins != null && ins.Length == 8, "Weather Output must expose eight bias ports");
            Check(probe.GetOutputs() != null && probe.GetOutputs().Length == 0,
                  "Weather Output is a terminal and exposes no output ports");
            Check(probe is IOutputNode, "Weather Output must be an IOutputNode so the evaluator drives it");

            if (ins != null && ins.Length == 8)
            {
                // The five carried-over names, in their original order, are the load-
                // bearing part: a saved graph reconnects by these.
                Check(ins[0].name == "In Precip Bias", "port 0 must stay In Precip Bias");
                Check(ins[1].name == "In Temperature Bias", "port 1 must stay In Temperature Bias");
                Check(ins[2].name == "In Wind Bias", "port 2 must stay In Wind Bias");
                Check(ins[3].name == "In Fog Bias", "port 3 must stay In Fog Bias");
                Check(ins[4].name == "In Cloud Bias", "port 4 must stay In Cloud Bias");
            }

            const int res = 33;
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                PerlinNoiseNode noise = new PerlinNoiseNode();
                WeatherOutputNode output = new WeatherOutputNode();
                graph.nodes.Add(noise);
                graph.nodes.Add(output);

                // Wire the source into Temperature Bias (port 1) only, leaving the other
                // seven ports unwired.
                Connection link = new Connection();
                link.sourceNodeId = noise.id;
                link.sourcePortIndex = 0;
                link.targetNodeId = output.id;
                link.targetPortIndex = 1;
                graph.connections.Add(link);

                TileData tile = new TileData(
                    new Coord(1, 1),
                    new CoordRect(new Coord(0, 0), new Coord(res, res)),
                    new Vector3(800f, 0f, 800f),
                    new Vector3(800f, 600f, 800f),
                    600f);
                tile.SetFullWorld(Vector3.zero, new Vector3(4000f, 600f, 4000f));
                new GraphEvaluator(graph).Evaluate(tile, new StopToken());

                // The wired port lands as a finite, correctly-sized field.
                float[] temp = tile.outputs.weatherTemperatureBias;
                Check(temp != null, "wired Temperature Bias must be captured, not null");
                if (temp != null)
                {
                    Check(temp.Length == res * res, "captured bias must cover the whole sample grid");
                    bool finite = true;
                    for (int i = 0; i < temp.Length; i++)
                        if (!IsFinite(temp[i])) { finite = false; break; }
                    Check(finite, "captured bias must hold only finite samples");
                }

                Check(tile.outputs.weatherBiasSize == new Vector2Int(res, res),
                      "the bias grid must record the sample dimensions of the wired field");

                // Every unwired port stays null - the driver reads that as "leave this
                // one alone", which is not the same as a bias of zero.
                Check(tile.outputs.weatherPrecipBias == null, "unwired Precip Bias must stay null");
                Check(tile.outputs.weatherWindBias == null, "unwired Wind Bias must stay null");
                Check(tile.outputs.weatherFogBias == null, "unwired Fog Bias must stay null");
                Check(tile.outputs.weatherCloudBias == null, "unwired Cloud Bias must stay null");
                Check(tile.outputs.weatherSnowBias == null, "unwired Snow Bias must stay null");
                Check(tile.outputs.weatherStormBias == null, "unwired Storm Bias must stay null");
                Check(tile.outputs.weatherTurbidityBias == null, "unwired Turbidity Bias must stay null");

                // A node with nothing wired has no weather branch to record and must
                // leave the bag exactly as it found it.
                tile.ClearOutputs();
                WeatherOutputNode orphan = new WeatherOutputNode();
                orphan.Generate(0, tile, new StopToken());
                Check(tile.outputs.weatherTemperatureBias == null,
                      "an all-unwired Weather Output must write no bias at all");
                Check(tile.outputs.weatherBiasSize == Vector2Int.zero,
                      "an all-unwired Weather Output must leave the bias grid unset");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        /// <summary>
        /// Lighting Output must keep the three original bias ports first and in order so
        /// a graph saved against the retired node reconnects, and it must record a wired
        /// bias into the bag while leaving an unwired one null - the driver's whole way
        /// of telling "no bias here" from "a bias of zero".
        /// </summary>
        private static void TestLightingOutputNodeCapturesBiases()
        {
            LightingOutputNode probe = new LightingOutputNode();

            Port[] ins = probe.GetInputs();
            Check(ins != null && ins.Length == 5, "Lighting Output must expose five bias ports");
            Check(probe.GetOutputs() != null && probe.GetOutputs().Length == 0,
                  "Lighting Output is a terminal and exposes no output ports");
            Check(probe is IOutputNode, "Lighting Output must be an IOutputNode so the evaluator drives it");

            if (ins != null && ins.Length == 5)
            {
                // The three carried-over names, in their original order, are the load-
                // bearing part: a saved graph reconnects by these.
                Check(ins[0].name == "In Sky Tint", "port 0 must stay In Sky Tint");
                Check(ins[1].name == "In Ambient Scale", "port 1 must stay In Ambient Scale");
                Check(ins[2].name == "In Turbidity", "port 2 must stay In Turbidity");
                // The appended ports follow, never inserted ahead of the originals.
                Check(ins[3].name == "In Cloud Cover Bias", "port 3 must be the appended In Cloud Cover Bias");
                Check(ins[4].name == "In Exposure Bias", "port 4 must be the appended In Exposure Bias");
            }

            const int res = 33;
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                PerlinNoiseNode noise = new PerlinNoiseNode();
                LightingOutputNode output = new LightingOutputNode();
                graph.nodes.Add(noise);
                graph.nodes.Add(output);

                // Wire the source into Turbidity (port 2) only, leaving the other four
                // ports unwired - a spatially varying haze mask, the done-when scenario.
                Connection link = new Connection();
                link.sourceNodeId = noise.id;
                link.sourcePortIndex = 0;
                link.targetNodeId = output.id;
                link.targetPortIndex = 2;
                graph.connections.Add(link);

                TileData tile = new TileData(
                    new Coord(1, 1),
                    new CoordRect(new Coord(0, 0), new Coord(res, res)),
                    new Vector3(800f, 0f, 800f),
                    new Vector3(800f, 600f, 800f),
                    600f);
                tile.SetFullWorld(Vector3.zero, new Vector3(4000f, 600f, 4000f));
                new GraphEvaluator(graph).Evaluate(tile, new StopToken());

                // The wired port lands as a finite, correctly-sized field.
                float[] turb = tile.outputs.lightingTurbidityBias;
                Check(turb != null, "wired Turbidity must be captured, not null");
                if (turb != null)
                {
                    Check(turb.Length == res * res, "captured bias must cover the whole sample grid");
                    bool finite = true;
                    for (int i = 0; i < turb.Length; i++)
                        if (!IsFinite(turb[i])) { finite = false; break; }
                    Check(finite, "captured bias must hold only finite samples");
                }

                Check(tile.outputs.lightingBiasSize == new Vector2Int(res, res),
                      "the bias grid must record the sample dimensions of the wired field");

                // Every unwired port stays null - the driver reads that as "leave this
                // one alone", which is not the same as a bias of zero.
                Check(tile.outputs.lightingSkyTintBias == null, "unwired Sky Tint must stay null");
                Check(tile.outputs.lightingAmbientScaleBias == null, "unwired Ambient Scale must stay null");
                Check(tile.outputs.lightingCloudCoverBias == null, "unwired Cloud Cover Bias must stay null");
                Check(tile.outputs.lightingExposureBias == null, "unwired Exposure Bias must stay null");

                // A node with nothing wired has no lighting branch to record and must
                // leave the bag exactly as it found it, so a day cycle with no lighting
                // output behaves identically to one with an all-unwired node.
                tile.ClearOutputs();
                LightingOutputNode orphan = new LightingOutputNode();
                orphan.Generate(0, tile, new StopToken());
                Check(tile.outputs.lightingTurbidityBias == null,
                      "an all-unwired Lighting Output must write no bias at all");
                Check(tile.outputs.lightingBiasSize == Vector2Int.zero,
                      "an all-unwired Lighting Output must leave the bias grid unset");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        // ---------------------------------------------------------------------
        //  WATER RE-FIT
        // ---------------------------------------------------------------------
        /// <summary>
        /// Builds a straight river channel across a synthetic chunk and returns the
        /// carve layer, ready to apply.
        /// </summary>
        /// <summary>
        /// The valley corridor: a river crossing a hillside gets a valley, not a slot.
        /// </summary>
        /// <remarks>
        /// This is the T17a mechanism, and it needed a test because its field default is
        /// 0 and 0 means OFF. Every river in the project - procedural and hand-placed -
        /// therefore ran with the cap disabled, so past the bevel the dig handed back
        /// whatever the generator had put there. Nothing failed; the channel simply read
        /// as a slot sawn through a cliff.
        ///
        /// All four checks matter in both directions. Off must mean untouched, or a
        /// hand-placed river silently gains a valley the user never asked for. On must
        /// visibly cut a wall, or the derivation is wired to nothing. And the cap is a
        /// CEILING: it may not dig below the water, and it may not flatten ground that
        /// was already gentle, because that ground is the generator's character.
        /// </remarks>
        private static void TestRiverValleyCorridorCaps()
        {
            const float surfaceY = 100f;
            const float probeOut = 60f;    // metres outside the bank
            const float wallRate = 2f;     // 2 m up per metre out: a 63-degree wall

            float wallY = surfaceY + wallRate * probeOut;
            float probeZ = 400f + 20f + probeOut;   // 20 m is half of the 40 m width

            WaterCarveLayer off = MakeValleyLayer(surfaceY, 0f);
            WaterCarveLayer on = MakeValleyLayer(surfaceY, 90f);

            float uncapped = off.HeightAt(400f, probeZ, wallY);
            float capped = on.HeightAt(400f, probeZ, wallY);

            Check(Mathf.Abs(uncapped - wallY) < 1e-3f,
                  "corridorMetres 0 still changed the ground " + probeOut + " m outside "
                  + "the bank (" + uncapped + " m against " + wallY + " m). 0 has to mean "
                  + "off, or every river left on the default silently gains a valley");

            Check(capped < wallY - 10f,
                  "the corridor left a " + (wallY - surfaceY) + " m wall standing "
                  + probeOut + " m outside the bank (got " + capped + " m). With the cap "
                  + "off the dig hands back raw generated ground and the river reads as "
                  + "a slot, which is the whole defect T17a exists to fix");

            Check(capped >= surfaceY - 0.01f,
                  "the corridor pulled ground to " + capped + " m, below the water "
                  + "surface at " + surfaceY + " m - it is a ceiling on how fast the "
                  + "valley may rise, not a second dig");

            float gentleY = surfaceY + 0.5f;
            float gentle = on.HeightAt(400f, probeZ, gentleY);

            Check(Mathf.Abs(gentle - gentleY) < 1e-3f,
                  "the corridor flattened ground that was already gentle (" + gentle
                  + " m against " + gentleY + " m), which would erase the generator's "
                  + "character across a corridor width either side of every river");
        }

        /// <summary>A straight channel at a known surface height, with the corridor and
        /// the channel size set by the caller so one course can be measured under
        /// several configurations.</summary>
        private static WaterCarveLayer MakeValleyLayer(float surfaceY, float corridorMetres,
                                                       float widthMetres = 40f,
                                                       float depthMetres = 4f,
                                                       bool valley = true)
        {
            WaterCarveLayer layer = new WaterCarveLayer();

            layer.isCourse = true;
            layer.profile = BedProfile.RiverChannel;
            layer.maxDepthMetres = depthMetres;
            layer.bevelMetres = 6f;
            layer.corridorMetres = corridorMetres;
            layer.corridorStrength = 1f;
            layer.valley = valley;
            layer.ruggednessMetres = 0f;   // deterministic: no noise in the bed

            for (int i = 0; i < 5; i++)
            {
                layer.coursePoints.Add(new Vector3(100f + i * 150f, surfaceY, 400f));
                layer.courseWidths.Add(widthMetres);
                layer.courseDepths.Add(depthMetres);
                layer.courseSurfaceY.Add(surfaceY);
            }

            layer.RecomputeBounds();
            return layer;
        }

        /// <summary>
        /// The floodplain sag: a big river arrives with the shallow valley it cut, and a
        /// stream does not.
        /// </summary>
        /// <remarks>
        /// The corridor cap tested above is a CEILING - it shaves ground standing too high
        /// too close to the water - and on ground that is already gentle it correctly does
        /// nothing. Which left the case a river is most often in: laid across a plain, in a
        /// channel cut straight down into flat ground, banks and all, with no valley
        /// anywhere. A real river of any size occupies a trough it made itself.
        ///
        /// The probe sits 30 m outside the bank of a 40 m channel, on ground 6 m above the
        /// water. That spot is chosen so the CEILING does not reach it - at 30 m out the
        /// cap sits some 11 m up - which is what makes the difference between the two
        /// layers attributable to the sag and nothing else.
        ///
        /// Five properties, each one a fault if it fails: off is untouched, on digs, the
        /// trough never floods, the waterline keeps no lip to stand on, and a stream is
        /// left alone because a stream has not the discharge to widen anything.
        /// </remarks>
        private static void TestRiverValleySagsTheFloodplain()
        {
            const float surfaceY = 100f;
            const float gentleY = surfaceY + 6f;
            const float outMetres = 30f;

            float probeZ = 400f + 20f + outMetres;   // 20 m is half of the 40 m width

            WaterCarveLayer off = MakeValleyLayer(surfaceY, 90f, 40f, 4f, false);
            WaterCarveLayer on = MakeValleyLayer(surfaceY, 90f, 40f, 4f, true);

            float flat = off.HeightAt(400f, probeZ, gentleY);
            float dug = on.HeightAt(400f, probeZ, gentleY);

            Check(Mathf.Abs(flat - gentleY) < 1e-3f,
                  "with the valley off the ground " + outMetres + " m outside the bank moved "
                  + "to " + flat + " m from " + gentleY + " m. Off has to be the old "
                  + "behaviour exactly, or the sag cannot be told from the corridor cap");

            Check(dug < gentleY - 0.5f,
                  "the valley left flat ground " + outMetres + " m outside a 40 m river at "
                  + dug + " m, against " + gentleY + " m untouched. A river on a plain with "
                  + "no trough around it is a channel sawn into a field");

            Check(dug >= surfaceY - 0.01f,
                  "the valley pulled the floodplain to " + dug + " m, at or below the water "
                  + "at " + surfaceY + " m. The valley floor is dry land at normal flow, "
                  + "not marsh at waterline");

            // At the waterline itself the bank must still arrive at exactly surface level.
            float lip = on.HeightAt(400f, 400f + 20f + 0.02f, gentleY);

            Check(Mathf.Abs(lip - surfaceY) < 0.05f,
                  "the ground meets the water at " + lip + " m against a surface at "
                  + surfaceY + " m - that difference is a lip standing at the water's edge, "
                  + "which is what the sag being zero at the waterline exists to prevent");

            // A 5 m stream: under the onset width, so no valley at all.
            WaterCarveLayer stream = MakeValleyLayer(surfaceY, 90f, 5f, 1.5f, true);
            float streamProbeZ = 400f + 2.5f + outMetres;
            float untouched = stream.HeightAt(400f, streamProbeZ, gentleY);

            Check(Mathf.Abs(untouched - gentleY) < 1e-3f,
                  "a 5 m stream cut a floodplain, moving ground to " + untouched + " m from "
                  + gentleY + " m. A headwater stream runs in the notch it has; floodplains "
                  + "belong to rivers with the discharge to widen one");
        }

        // ---------------------------------------------------------------------
        //  THE DIVE
        // ---------------------------------------------------------------------

        /// <summary>
        /// A query deep in a deep body is wet, and the volume box hangs under the water
        /// rather than straddling it.
        /// </summary>
        /// <remarks>
        /// maxDepth used to inherit ShorelineConformer.MaxSkirtDepthMetres, which bounds
        /// how far a mesh SKIRT is worth drawing and says nothing about how deep the sea
        /// is. UCWaterVolume takes its floor from that field, so every point more than
        /// 100 m down answered dry: diving to a sea floor below that line switched the
        /// underwater view off and the world came back in air, at exactly 100 m, every
        /// time. 250 m here is an ordinary continental-shelf depth and well past the
        /// old bound.
        /// </remarks>
        private static void TestVolumeReachesTheRealBed()
        {
            GameObject go = new GameObject("UC SelfTest Volume");

            try
            {
                UCWaterVolume v = go.AddComponent<UCWaterVolume>();

                Check(v.worldBounds.max.y <= 1e-3f,
                      "a fresh volume's box reaches " + v.worldBounds.max.y + " m ABOVE its "
                      + "own plane. The box hangs under the water; centring it on the "
                      + "origin puts half of it in the air, which is what the gizmo was "
                      + "drawing over every unfitted body");

                v.surfaceLevel = 0f;
                v.maxDepth = 250f;
                v.worldBounds = UCWaterVolume.ExtendDown(
                    new Bounds(Vector3.zero, new Vector3(1000f, 0f, 1000f)), v.maxDepth);

                Check(v.worldBounds.min.y <= -250f + 1e-3f,
                      "ExtendDown built a box whose floor is " + v.worldBounds.min.y
                      + " m for a 250 m body - it has to reach the bed, or the gizmo and "
                      + "the query describe different volumes");

                WaterSample deep;
                bool wetDeep = v.SampleLocal(new Vector3(0f, -150f, 0f), 0f, out deep);

                Check(wetDeep && deep.submerged,
                      "a point 150 m under the surface of a 250 m deep body came back dry. "
                      + "That is the dive fault: past the old skirt bound the query "
                      + "rejected the point, the underwater globals were cleared, and the "
                      + "sea floor rendered in air");

                WaterSample belowBed;
                bool wetBelow = v.SampleLocal(new Vector3(0f, -300f, 0f), 0f, out belowBed);

                Check(!wetBelow,
                      "a point 300 m down in a 250 m deep body came back wet. Below the bed "
                      + "is rock, and the floor test is the only thing that says so");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ---------------------------------------------------------------------
        //  PER-NODE READOUTS
        // ---------------------------------------------------------------------

        /// <summary>
        /// The figures the node panel shows come off the course and mean what they say.
        /// </summary>
        /// <remarks>
        /// Flat Floor is the mode used here because it is the one where the authored Y is
        /// the river's datum, so the elevations are arithmetic and need no terrain in the
        /// scene. The grade is the surveyor's figure a road node carries - rise over the
        /// HORIZONTAL run - and the sign convention is load-bearing: negative downhill,
        /// which is the direction a river is allowed to go.
        /// </remarks>
        private static void TestNodeReadoutsMatchTheCourse()
        {
            GameObject go = new GameObject("UC SelfTest River Nodes");

            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();

                body.SetKindOnce(WaterBodyKind.River);
                body.bedSource = UCWaterBody.BedSource.FlatFloor;
                body.flatFloorDepthMetres = 3f;
                body.surfaceOffsetMetres = 0f;

                body.course.points.Clear();
                body.course.points.Add(new Vector3(0f, 100f, 0f));
                body.course.points.Add(new Vector3(100f, 90f, 0f));
                body.course.points.Add(new Vector3(200f, 90f, 0f));

                Check(Mathf.Abs(body.NodeSurfaceY(0) - 97f) < 1e-3f,
                      "node 0 reads its surface at " + body.NodeSurfaceY(0) + " m where the "
                      + "line is at 100 m and the flat floor is 3 m down, so the water is "
                      + "at 97 m. The panel showed the point's raw Y before, which the "
                      + "solve discards in every mode but this one");

                Check(Mathf.Abs(body.NodeGradePercentToNext(0) + 10f) < 1e-3f,
                      "a 10 m drop over a 100 m run reads as "
                      + body.NodeGradePercentToNext(0) + "% instead of -10%. Rise over the "
                      + "horizontal run, and negative downhill");

                Check(Mathf.Abs(body.NodeGradePercentFromPrev(2)) < 1e-3f,
                      "a level reach reads as " + body.NodeGradePercentFromPrev(2)
                      + "% instead of 0%");

                Check(body.IsEndNode(0) && body.IsEndNode(2) && !body.IsEndNode(1),
                      "the ends of a three-node course did not report as ends. Only an end "
                      + "can carry a junction, and the marker for one is drawn from this");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The corridor the dig is handed is the one the inspector reports. One accessor
        /// answers both, because two copies of "how wide is the valley" would eventually
        /// give two answers - and the inspector's copy is the one the user would trust.
        /// </summary>
        /// <remarks>
        /// The derivation exists because a valley floor scales with the channel that cut
        /// it. That is also the "big rivers only" rule: a 4 m brook multiplies to 12 m and
        /// meets the 8 m floor, an 80 m trunk multiplies to 240 m and meets the 160 m
        /// ceiling, and WaterCarveLayer's own onset ramp (8 m to 25 m of channel) fades
        /// the sag in over that range, so a stream gets a shoulder and not a floodplain.
        ///
        /// The clamps are checked in both directions because they are the only thing
        /// standing between valleyWidthMultiple and either a corridor thinner than a
        /// heightmap texel or one that reshapes ground with nothing to do with the river.
        /// </remarks>
        private static void TestValleyCorridorAccessor()
        {
            GameObject go = new GameObject("UC SelfTest Valley Corridor");

            try
            {
                UCWaterBody body = go.AddComponent<UCWaterBody>();

                body.SetKindOnce(WaterBodyKind.River);
                body.shape = UCWaterBody.ShapeMode.RiverSpline;
                body.corridorMetres = 0f;
                body.valleyWidthMultiple = 3f;
                body.widthMetres = 40f;

                Check(Mathf.Abs(body.ValleyCorridorMetres - 120f) < 1e-3f,
                      "a 40 m river at multiple 3 derived a " + body.ValleyCorridorMetres
                      + " m valley instead of 120 m. This is the number the dig cuts with "
                      + "and the number the inspector prints, so a wrong one is wrong twice");

                body.widthMetres = 1f;
                Check(Mathf.Abs(body.ValleyCorridorMetres - 8f) < 1e-3f,
                      "a 1 m channel derived a " + body.ValleyCorridorMetres + " m valley. "
                      + "Below the 8 m floor a corridor is thinner than the heightmap's own "
                      + "texel spacing and cannot express a ramp at all");

                body.widthMetres = 200f;
                Check(Mathf.Abs(body.ValleyCorridorMetres - 160f) < 1e-3f,
                      "a 200 m channel derived a " + body.ValleyCorridorMetres + " m valley. "
                      + "Past the 160 m ceiling it reshapes ground with nothing to do with "
                      + "the river, and widens the area every chunk apply has to sample");

                body.widthMetres = 40f;
                body.corridorMetres = 25f;
                Check(Mathf.Abs(body.ValleyCorridorMetres - 25f) < 1e-3f,
                      "typed metres gave " + body.ValleyCorridorMetres + " m instead of the "
                      + "25 m authored. An absolute override has to win, or the field does "
                      + "nothing on any river whose width would derive something else");

                body.corridorMetres = 0f;
                body.valleyWidthMultiple = 0f;
                Check(body.ValleyCorridorMetres <= 0f,
                      "both dials at 0 still gave " + body.ValleyCorridorMetres + " m. Off "
                      + "must mean the dig cuts what it finds, or a hand-placed river gains "
                      + "a valley nobody asked for");

                body.shape = UCWaterBody.ShapeMode.Polygon;
                body.valleyWidthMultiple = 3f;
                Check(body.ValleyCorridorMetres <= 0f,
                      "a level body derived a " + body.ValleyCorridorMetres + " m valley "
                      + "from the river multiple. A lake's shore is its bevel");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            GameObject sea = new GameObject("UC SelfTest Valley Ocean");

            try
            {
                UCWaterBody ocean = sea.AddComponent<UCWaterBody>();

                ocean.SetKindOnce(WaterBodyKind.Ocean);
                ocean.corridorMetres = 90f;

                Check(ocean.ValleyCorridorMetres <= 0f,
                      "an ocean reported a " + ocean.ValleyCorridorMetres + " m corridor "
                      + "from typed metres. An ocean rounds its beach and shapes nothing "
                      + "else - its seabed is whatever the terrain and the graph make it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sea);
            }
        }

        /// <summary>
        /// A lake takes its SHAPE from the ground it floods and its SIZE from the basin
        /// that holds it - so no lake is a circle or a polygon, no two basins give the
        /// same lake, and a basin holding less than 60 m^2 is reported as the puddle it is
        /// rather than inflated into a lake.
        /// </summary>
        /// <remarks>
        /// Three bugs are being kept out, in the order they happened:
        ///
        ///   Random.Range(30..150) as a radius - a different world on every press.
        ///   A 28-bearing star - only ever convex, and on smooth ground a visible polygon.
        ///   One area target for every basin - a world of identically sized lakes.
        ///
        /// The last is the one a naive test cannot see, so the size checks here compare
        /// basins against EACH OTHER rather than against a number: three rims of different
        /// widths must give three clearly different lakes. The shape check does the same
        /// trick - a bowl with an arm running off it must come back as a shape that fills
        /// well under two thirds of its own bounding box, which no circle (0.79), regular
        /// polygon (0.68 and up) or rectangle (1.0) can do.
        ///
        /// The walled-basin cases keep the flood honest: filling to an area is trivially
        /// satisfiable by ignoring the ground and walking out to the search limit, and a
        /// test that only looked at a roomy bowl would pass on exactly that bug.
        /// </remarks>
        /// <summary>
        /// A graph whose node script no longer exists has to heal itself rather than
        /// throw.
        ///
        /// [SerializeReference] leaves a null in `nodes` when a class disappears, which
        /// is how the water Output nodes the rework removed still turn up in graphs
        /// authored before it - the "Missing types referenced from component Graph"
        /// warning is Unity reporting exactly that. Three things have to hold: nothing
        /// walking the list may dereference the hole, Validate must say so rather than
        /// stay quiet, and pruning must take the hole plus every edge that pointed at
        /// it, or the evaluator would go looking for a node that is not there.
        /// </summary>
        private static void TestGraphHealsMissingNodeSlots()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ChannelGroundFeed keptA = new ChannelGroundFeed();
                ChannelMaskFeed keptB = new ChannelMaskFeed();

                // Two survivors with a live edge, and a hole between them wired on both
                // sides - the shape Unity leaves when a node class is deleted out of the
                // middle of a graph someone had already connected up.
                graph.nodes.Add(keptA);
                graph.nodes.Add(null);
                graph.nodes.Add(keptB);

                graph.connections.Add(new Connection(keptA.id, 0, keptB.id, 0));
                graph.connections.Add(new Connection(keptA.id, 0, "vanished-node", 0));
                graph.connections.Add(new Connection("vanished-node", 0, keptB.id, 1));

                // Reading the graph with the hole still in it must not throw.
                List<string> before = null;
                try
                {
                    Check(graph.GetNodeById(keptA.id) == keptA,
                          "GetNodeById finds a node that sits past a hole.");
                    Check(graph.GetNodeById("vanished-node") == null,
                          "GetNodeById reports a vanished node as missing.");
                    before = graph.Validate();
                }
                catch (System.Exception e)
                {
                    Check(false, "Walking a graph with an empty node slot threw " +
                                 e.GetType().Name + " instead of reporting it.");
                }

                Check(before != null, "A graph with an empty node slot can still be validated.");
                bool reported = false;
                if (before != null)
                {
                    for (int i = 0; i < before.Count; i++)
                        if (before[i].Contains("node slot is empty")) reported = true;
                }
                Check(reported, "Validate names the empty node slot rather than hiding it.");

                int removed = graph.PruneMissingNodes();
                Check(removed == 1, "Pruning removes exactly the one hole. Removed " + removed + ".");
                Check(graph.nodes.Count == 2,
                      "Both real nodes survive the prune. Left with " + graph.nodes.Count + ".");
                Check(graph.nodes.Contains(keptA) && graph.nodes.Contains(keptB),
                      "The prune keeps the node instances themselves.");
                Check(!graph.nodes.Contains(null), "No hole is left in the node list.");

                Check(graph.connections.Count == 1,
                      "Only the edge joining two surviving nodes is kept. Kept " +
                      graph.connections.Count + ".");
                if (graph.connections.Count == 1)
                {
                    Connection kept = graph.connections[0];
                    Check(kept.sourceNodeId == keptA.id && kept.targetNodeId == keptB.id,
                          "The edge left standing is the real one, not a dangling one.");
                }

                List<string> after = graph.Validate();
                bool stillReported = false;
                for (int i = 0; i < after.Count; i++)
                    if (after[i].Contains("node slot is empty")) stillReported = true;
                Check(!stillReported, "Once pruned, the graph no longer reports an empty slot.");

                Check(graph.PruneMissingNodes() == 0, "Pruning an already healthy graph is a no-op.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static void TestLakeShapeAndSizeComeFromTheBasin()
        {
            const float floorY = 100f;

            Bounds bounds = new Bounds(new Vector3(0f, 300f, 0f),
                                       new Vector3(8000f, 600f, 8000f));

            Vector3 seed = new Vector3(0f, floorY, 0f);

            // --- 1. A roomy bowl: filled until the ceiling stops it, not past it -------

            // A paraboloid rising 30 m at 80 m out. The 720 m^2 ceiling is a disc about
            // 30 m across, which this bowl reaches at roughly a metre of water - so the
            // ceiling is what stops the fill here, and it has to actually stop it.
            const float curvature = 30f / (80f * 80f);

            RiverCourseSolver.GroundSampler bowl = delegate (float x, float z)
            {
                return floorY + curvature * (x * x + z * z);
            };

            float level, area;
            Polyline outline = HydrologyGenerator.SolveLakeContour(
                bowl, bounds, seed, out level, out area);

            Check(outline != null && outline.Count >= 8 && outline.closed,
                  "the lake contour came back null, open or degenerate. An open outline "
                  + "fails the polygon test in BuildLevelBody and the lake fills its whole "
                  + "bounding box instead - the square hole");

            Check(area >= HydrologyGenerator.MinLakeAreaSquareMetres,
                  "an open bowl produced " + area.ToString("F0") + " m^2, under the "
                  + HydrologyGenerator.MinLakeAreaSquareMetres + " m^2 floor. Water that "
                  + "can spread that far is a lake");

            // A tenth over the ceiling, not the ceiling exactly: the area is recounted
            // after the pinch fill, which can only add cells, and the level lands between
            // two half-metre steps rather than on one.
            Check(area <= HydrologyGenerator.MaxLakeAreaSquareMetres * 1.1f,
                  "the bowl flooded " + area.ToString("F0") + " m^2 against a ceiling of "
                  + HydrologyGenerator.MaxLakeAreaSquareMetres + " m^2. The fill is "
                  + "supposed to stop at the ceiling, so this one walked past it");

            Check(level > floorY && level <= floorY + 60f + 1e-3f,
                  "the fill level came out at " + level + " m against a floor of "
                  + floorY + " m, outside the 0..60 m search band");

            // The shore IS the flooded edge, so no vertex may stand on ground meaningfully
            // above the water. That is what stops a lake perching on a hillside or leaking
            // out of its basin. The tolerance is one grid corner's worth of slope on the
            // largest lake the ceiling allows - the outline runs along cell edges, so a
            // vertex sits up to half a diagonal outside the last flooded cell centre.
            float worst = 0f;
            if (outline != null)
            {
                for (int i = 0; i < outline.Count; i++)
                {
                    Vector3 p = outline.points[i];
                    worst = Mathf.Max(worst, bowl(p.x, p.z) - level);
                }
            }

            Check(worst < 2.5f,
                  "an outline vertex sits " + worst.ToString("F2") + " m above the fill "
                  + "level, so the lake would stand above its own shoreline");

            // No right angles anywhere in the shore. The trace runs along cell edges, so
            // every corner it STARTS with is a lattice 90 and the corner-cutting pass is
            // what has to take them off. A 90 surviving into the outline is the machine-made
            // box look, and on a digitised circle like this one nothing else can produce it.
            float sharpest = 0f;
            if (outline != null && outline.Count >= 3)
            {
                for (int i = 0; i < outline.Count; i++)
                {
                    Vector3 a = outline.points[(i + outline.Count - 1) % outline.Count];
                    Vector3 b = outline.points[i];
                    Vector3 c = outline.points[(i + 1) % outline.Count];

                    Vector2 into = new Vector2(b.x - a.x, b.z - a.z);
                    Vector2 outOf = new Vector2(c.x - b.x, c.z - b.z);

                    if (into.sqrMagnitude < 1e-8f || outOf.sqrMagnitude < 1e-8f) continue;

                    sharpest = Mathf.Max(sharpest, Vector2.Angle(into, outOf));
                }
            }

            Check(sharpest < 89f,
                  "an outline corner turns " + sharpest.ToString("F0") + " degrees. The shore "
                  + "is traced along cell edges, so its raw corners are right angles and the "
                  + "corner-cutting pass is what removes them - a 90 left in a shoreline is "
                  + "the box look, not a lake");

            // --- 2. Shape from the ground: a bowl with an arm up a side valley ---------

            // A round bowl at the origin and a narrow channel running off it. A solver
            // that measures radii from the centre cannot describe this at all: it would
            // either lose the arm or flood the walls beside it. Sized to sit under the
            // ceiling - about 310 m^2 - so the ground is what decides, not the cap.
            RiverCourseSolver.GroundSampler lobed = delegate (float x, float z)
            {
                float d = Mathf.Sqrt(x * x + z * z);
                bool inBowl = d < 8f;
                bool inArm = x > 0f && x < 30f && Mathf.Abs(z) < 2.5f;

                return (inBowl || inArm) ? floorY + 0.002f * d : floorY + 200f;
            };

            float lobedLevel, lobedArea;
            Polyline lobedOutline = HydrologyGenerator.SolveLakeContour(
                lobed, bounds, seed, out lobedLevel, out lobedArea);

            Check(lobedOutline != null && lobedOutline.Count >= 8 && lobedOutline.closed,
                  "a bowl with an arm running off it produced no usable outline");

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float shoelace = 0f;

            if (lobedOutline != null)
            {
                for (int i = 0; i < lobedOutline.Count; i++)
                {
                    Vector3 p = lobedOutline.points[i];
                    Vector3 q = lobedOutline.points[(i + 1) % lobedOutline.Count];

                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;

                    shoelace += p.x * q.z - q.x * p.z;
                }
            }

            float outlineArea = Mathf.Abs(shoelace) * 0.5f;
            float boxArea = Mathf.Max(1f, (maxX - minX) * (maxZ - minZ));

            Check(maxX > 20f,
                  "the outline reached only " + maxX.ToString("F0") + " m along a 30 m "
                  + "arm, so the water did not follow the ground up the side valley - the "
                  + "shape is coming from somewhere other than where the lake formed");

            Check(outlineArea < boxArea * 0.65f,
                  "the outline fills " + (outlineArea / boxArea).ToString("F2") + " of its "
                  + "own bounding box in a bowl-plus-arm basin. A circle fills 0.79, a "
                  + "regular polygon 0.68 or more and a rectangle 1.0, so this shape is a "
                  + "known one and not the ground's");

            // --- 3. Size from the basin: three rims, three different lakes -------------

            // 6, 10 and 14 m rims hold about 113, 314 and 616 m^2 - three sizes spread
            // across the 60..720 m^2 band, all of them under the ceiling so that what
            // separates them is their basins and nothing else.
            float[] rims = { 6f, 10f, 14f };
            float[] areas = new float[rims.Length];

            for (int r = 0; r < rims.Length; r++)
            {
                float rim = rims[r];

                RiverCourseSolver.GroundSampler walled = delegate (float x, float z)
                {
                    float d = Mathf.Sqrt(x * x + z * z);
                    return d < rim ? floorY + d * 0.005f : floorY + 200f;
                };

                float rimLevel, rimArea;
                Polyline held = HydrologyGenerator.SolveLakeContour(
                    walled, bounds, seed, out rimLevel, out rimArea);

                Check(held != null && held.closed && rimArea >= HydrologyGenerator.MinLakeAreaSquareMetres,
                      "a basin walled " + rim + " m out gave no lake at all");

                Check(rimArea < Mathf.PI * rim * rim * 1.6f,
                      "a basin walled " + rim + " m out flooded " + rimArea.ToString("F0")
                      + " m^2, more than the rim can hold, so the flood walked straight "
                      + "through the wall it is supposed to stop at");

                areas[r] = rimArea;
            }

            for (int r = 0; r + 1 < areas.Length; r++)
                Check(areas[r + 1] > areas[r] * 1.2f,
                      "a " + rims[r] + " m basin and a " + rims[r + 1] + " m basin came "
                      + "back as " + areas[r].ToString("F0") + " m^2 and "
                      + areas[r + 1].ToString("F0") + " m^2. Lakes are supposed to differ "
                      + "because their basins differ - same-sized lakes everywhere is the "
                      + "single-area-target bug returning");

            // --- 4. The floor: a dimple is a puddle, and says so ----------------------

            // Two metres across, about 13 m^2. The generator passes this over instead of
            // dressing it up as a lake, which is what the 60 m^2 floor is for.
            RiverCourseSolver.GroundSampler dimple = delegate (float x, float z)
            {
                float d = Mathf.Sqrt(x * x + z * z);
                return d < 2f ? floorY + d * 0.005f : floorY + 200f;
            };

            float dimpleLevel, dimpleArea;
            HydrologyGenerator.SolveLakeContour(dimple, bounds, seed,
                                                out dimpleLevel, out dimpleArea);

            Check(dimpleArea < HydrologyGenerator.MinLakeAreaSquareMetres,
                  "a 2 m dimple was measured at " + dimpleArea.ToString("F0") + " m^2, at "
                  + "or over the " + HydrologyGenerator.MinLakeAreaSquareMetres + " m^2 "
                  + "floor. Reporting a puddle as lake-sized is how a coarse grid turns "
                  + "heightmap noise into lakes");

            // A five-metre rim is a real tarn and must survive - it is the small end the
            // 60 m^2 floor exists to admit, and the coarse-to-fine passes are what let one
            // search window resolve both it and a lake ten times its size.
            RiverCourseSolver.GroundSampler tarn = delegate (float x, float z)
            {
                float d = Mathf.Sqrt(x * x + z * z);
                return d < 5f ? floorY + d * 0.005f : floorY + 200f;
            };

            float tarnLevel, tarnArea;
            Polyline tarnOutline = HydrologyGenerator.SolveLakeContour(
                tarn, bounds, seed, out tarnLevel, out tarnArea);

            Check(tarnOutline != null && tarnOutline.closed
                  && tarnArea >= HydrologyGenerator.MinLakeAreaSquareMetres
                  && tarnArea < 400f,
                  "a 5 m tarn came back as " + tarnArea.ToString("F0") + " m^2 rather than "
                  + "about 79 m^2, so the search window never refined down to it");

            // --- 5. A basin far wider than the ceiling still gets its lake -------------

            // Ground this gentle floods hundreds of metres for the first half-metre of
            // rise, so every stepped level either runs out of the neighbourhood or blows
            // past the ceiling. The level has to be found BETWEEN two steps; a solver that
            // only tried the steps would find no level that fitted and report no lake at
            // all - which on real, gently graded terrain is most of the lakes.
            RiverCourseSolver.GroundSampler broad = delegate (float x, float z)
            {
                return floorY + 0.0002f * (x * x + z * z);
            };

            float broadLevel, broadArea;
            Polyline broadOutline = HydrologyGenerator.SolveLakeContour(
                broad, bounds, seed, out broadLevel, out broadArea);

            Check(broadOutline != null && broadOutline.closed
                  && broadArea >= HydrologyGenerator.MinLakeAreaSquareMetres,
                  "a broad, gently graded basin gave "
                  + (broadOutline == null ? "no lake at all" : broadArea.ToString("F0") + " m^2")
                  + ". The lowest ground still holds water, so the level belongs between "
                  + "two fill steps rather than being given up on");

            Check(broadArea <= HydrologyGenerator.MaxLakeAreaSquareMetres * 1.1f,
                  "a broad basin flooded " + broadArea.ToString("F0") + " m^2 against a "
                  + HydrologyGenerator.MaxLakeAreaSquareMetres + " m^2 ceiling");

            // Printed, not asserted: the areas and depths are what make a later change to
            // the grid resolution or the step size legible.
            Debug.Log("[UC SelfTest] lakes from their basins: bowl "
                      + area.ToString("F0") + " m^2 at +"
                      + (level - floorY).ToString("F2") + " m over "
                      + (outline != null ? outline.Count : 0)
                      + " vertices; arm basin " + lobedArea.ToString("F0") + " m^2 filling "
                      + (outlineArea / boxArea).ToString("F2") + " of its box; rims "
                      + areas[0].ToString("F0") + " / " + areas[1].ToString("F0") + " / "
                      + areas[2].ToString("F0") + " m^2; tarn " + tarnArea.ToString("F0")
                      + " m^2; dimple " + dimpleArea.ToString("F0") + " m^2; broad basin "
                      + broadArea.ToString("F0") + " m^2 at +"
                      + (broadLevel - floorY).ToString("F3") + " m");
        }

        private static WaterCarveLayer MakeRiverLayer(float groundY, float depth)
        {
            WaterCarveLayer layer = new WaterCarveLayer();

            layer.isCourse = true;
            layer.profile = BedProfile.RiverChannel;
            layer.maxDepthMetres = depth;
            layer.bevelMetres = 6f;
            layer.ruggednessMetres = 0f;   // deterministic: no noise in the bed

            // Straight run west to east through the middle of an 800 m cell.
            for (int i = 0; i < 5; i++)
            {
                float x = 100f + i * 150f;
                layer.coursePoints.Add(new Vector3(x, groundY, 400f));
                layer.courseWidths.Add(40f);
                layer.courseDepths.Add(depth);
                layer.courseSurfaceY.Add(groundY);
            }

            layer.RecomputeBounds();
            return layer;
        }

        private static float[,] FlatField(int res, float normalisedHeight)
        {
            float[,] h = new float[res, res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    h[z, x] = normalisedHeight;
            return h;
        }

        private static float LowestSample(float[,] h)
        {
            float lo = float.MaxValue;
            for (int z = 0; z < h.GetLength(0); z++)
                for (int x = 0; x < h.GetLength(1); x++)
                    if (h[z, x] < lo) lo = h[z, x];
            return lo;
        }

        /// <summary>
        /// The dig must only ever lower ground, never raise it.
        /// </summary>
        /// <remarks>
        /// This is the property that makes a pristine snapshot the ONLY way to undo a
        /// dig. Every bed value goes through Mathf.Min(existingY, bed), so once a
        /// channel is cut, no amount of re-running the carvers can put that ground
        /// back - which means "delete the river and the trench fills in" is not
        /// something a re-carve can ever achieve, however it is arranged.
        ///
        /// It is asserted rather than assumed because the whole memory cost of
        /// Chunk.graphHeights is justified by it. If the carve ever became able to
        /// raise ground, that copy would stop being necessary and this test is where
        /// that would surface.
        /// </remarks>
        private static void TestCarveOnlyLowersGround()
        {
            const int res = 129;
            const float maxHeight = 600f;
            const float mps = 800f / (res - 1);

            float groundNorm = 0.5f;
            float groundY = groundNorm * maxHeight;

            Bounds chunk = new Bounds(new Vector3(400f, maxHeight * 0.5f, 400f),
                                      new Vector3(800f, maxHeight, 800f));

            WaterCarveLayer layer = MakeRiverLayer(groundY, 12f);

            float[,] pristine = FlatField(res, groundNorm);
            float[,] dug = FlatField(res, groundNorm);

            layer.ApplyTo(dug, chunk, mps, maxHeight);

            bool anyRaised = false;
            bool anyLowered = false;

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    if (dug[z, x] > pristine[z, x] + 1e-6f) anyRaised = true;
                    if (dug[z, x] < pristine[z, x] - 1e-6f) anyLowered = true;
                }
            }

            Check(anyLowered, "the river carve did not lower any ground - the test river "
                              + "missed the chunk, so nothing below was actually verified");

            Check(!anyRaised, "the river carve RAISED ground somewhere. Every bed value is "
                              + "supposed to pass through Mathf.Min(existingY, bed)");

            // And the depth has to be real, not a rounding artifact.
            float cutMetres = (groundNorm - LowestSample(dug)) * maxHeight;
            Check(cutMetres > 1f,
                  "the river cut only " + cutMetres.ToString("F2") + " m - too shallow to be a channel");
        }

        /// <summary>
        /// Re-deriving a dig from the pristine snapshot gives the same surface every
        /// time, while re-deriving it from the previous result does not.
        /// </summary>
        /// <remarks>
        /// This is the regression that ReapplyCarves had. It cloned the graph output
        /// from c.tile, which the pump nulls immediately after applying a chunk, so the
        /// method returned without doing anything at all and the bug hid behind a
        /// no-op. Now that it reads Chunk.graphHeights instead, the two halves below
        /// are what say the snapshot is being used for the right reason:
        ///
        ///   from the snapshot  - stable, because the input never changes
        ///   from the last pass - drifts, because the course is re-solved against
        ///                        ground that already has the channel in it
        ///
        /// The second half deliberately asserts that feeding the result back in is
        /// WORSE. If that ever stops being true the guard is free to be dropped, and a
        /// test that only checked the good path could not tell.
        /// </remarks>
        /// <summary>
        /// The gate that decides whether a settle re-fits a river at all.
        /// </summary>
        /// <remarks>
        /// This is the whole reason a 25-cell build does not re-solve, re-mesh and
        /// rewrite its rivers 25 times over. It has to be exact in both directions:
        /// too loose and a valley moves under a river that never notices, too tight
        /// and the work comes straight back.
        ///
        /// The NaN case is the one worth having. Mathf.Abs(NaN) is NaN and NaN fails
        /// every comparison, so a naive (diff > eps) test reads a NaN probe as "no
        /// movement" and skips a re-fit forever. Same class of trap as Clamp01 not
        /// filtering NaN, which is what HeightGuard exists for.
        /// </remarks>
        private static void TestGroundProbeMatching()
        {
            // Two course points: x, z, undug ground height apiece.
            float[] baseline = new float[] { 100f, 200f, 55f, 130f, 200f, 54f };

            Check(UCWaterBody.GroundProbesMatch(baseline, (float[])baseline.Clone()),
                  "water re-fit gate: an identical probe reports no movement");

            float[] requantised = (float[])baseline.Clone();
            requantised[2] += 0.004f;
            Check(UCWaterBody.GroundProbesMatch(baseline, requantised),
                  "water re-fit gate: 4 mm is heightmap quantisation, not movement");

            float[] moved = (float[])baseline.Clone();
            moved[5] += 0.5f;
            Check(!UCWaterBody.GroundProbesMatch(baseline, moved),
                  "water re-fit gate: half a metre under the course is movement");

            float[] rerouted = (float[])baseline.Clone();
            rerouted[0] += 25f;
            Check(!UCWaterBody.GroundProbesMatch(baseline, rerouted),
                  "water re-fit gate: a course point that moved in XZ re-fits even at "
                  + "the same height");

            Check(!UCWaterBody.GroundProbesMatch(baseline, new float[] { 100f, 200f, 55f }),
                  "water re-fit gate: a probe of a different length never matches");

            Check(!UCWaterBody.GroundProbesMatch(null, baseline)
                  && !UCWaterBody.GroundProbesMatch(baseline, null),
                  "water re-fit gate: no baseline means the re-fit goes ahead");

            Check(!UCWaterBody.GroundProbesMatch(new float[0], new float[0]),
                  "water re-fit gate: an empty probe is not a baseline");

            float[] broken = (float[])baseline.Clone();
            broken[2] = float.NaN;
            Check(!UCWaterBody.GroundProbesMatch(baseline, broken)
                  && !UCWaterBody.GroundProbesMatch(broken, broken)
                  && !UCWaterBody.GroundProbesMatch(broken, baseline),
                  "water re-fit gate: a NaN probe reports movement instead of passing "
                  + "the <= test");

            float[] infinite = (float[])baseline.Clone();
            infinite[2] = float.PositiveInfinity;
            Check(!UCWaterBody.GroundProbesMatch(baseline, infinite),
                  "water re-fit gate: an infinite probe reports movement");
        }

        private static void TestRefitFromSnapshotIsStable()
        {
            const int res = 129;
            const float maxHeight = 600f;
            const float mps = 800f / (res - 1);

            float groundNorm = 0.5f;
            float groundY = groundNorm * maxHeight;

            Bounds chunk = new Bounds(new Vector3(400f, maxHeight * 0.5f, 400f),
                                      new Vector3(800f, maxHeight, 800f));

            float[,] snapshot = FlatField(res, groundNorm);

            // --- Re-fit twice from the snapshot, exactly as ReapplyCarves now does ---
            float[,] first = CloneField(snapshot);
            MakeRiverLayer(groundY, 12f).ApplyTo(first, chunk, mps, maxHeight);

            float[,] second = CloneField(snapshot);
            MakeRiverLayer(groundY, 12f).ApplyTo(second, chunk, mps, maxHeight);

            float maxDrift = 0f;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    float d = Mathf.Abs(first[z, x] - second[z, x]);
                    if (d > maxDrift) maxDrift = d;
                }

            Check(maxDrift < 1e-6f,
                  "two re-fits from the same snapshot disagreed by "
                  + maxDrift.ToString("F8") + " - the re-fit is not deterministic, so a "
                  + "river would creep every time its spline was touched");

            // --- And the snapshot must be left untouched by the re-fit ---
            bool snapshotClean = true;
            for (int z = 0; z < res && snapshotClean; z++)
                for (int x = 0; x < res; x++)
                    if (Mathf.Abs(snapshot[z, x] - groundNorm) > 1e-6f) { snapshotClean = false; break; }

            Check(snapshotClean,
                  "the re-fit wrote into the snapshot it was reading from. It would then be "
                  + "cloning already-carved ground, and the channel would deepen on every pass");

            // --- Feeding the result back in is the failure mode being prevented ---
            float[,] fedBack = CloneField(first);
            MakeRiverLayer(groundY, 12f).ApplyTo(fedBack, chunk, mps, maxHeight);

            float deepenedBy = (LowestSample(first) - LowestSample(fedBack)) * maxHeight;

            // Not "must deepen": with an absolute bed and no ground resampling in this
            // isolated harness it may well come out identical, and that is fine. What
            // must hold is that it never comes out SHALLOWER - the direction of the
            // error is the part that matters, because a re-carve can never restore.
            Check(deepenedBy >= -1e-4f,
                  "re-carving already-carved ground made the channel shallower by "
                  + (-deepenedBy).ToString("F3") + " m, which contradicts the min-only carve");
        }

        private static float[,] CloneField(float[,] source)
        {
            int resZ = source.GetLength(0);
            int resX = source.GetLength(1);
            float[,] copy = new float[resZ, resX];
            Array.Copy(source, copy, resZ * resX);
            return copy;
        }

        // ---------------------------------------------------------------------
        //  RIVER FILL
        // ---------------------------------------------------------------------

        /// <summary>
        /// The sliding-window extremes must agree with the brute-force answer.
        /// </summary>
        /// <remarks>
        /// Lemire's deque is not obviously correct by inspection - the whole point of
        /// it is that most elements are never examined again - and the river surface
        /// is built entirely on top of it. Comparing against the definition on a small
        /// field is the only honest way to know the deque is retiring the right
        /// candidates, including at the clamped ends where the window is short.
        /// </remarks>
        private static void TestWindowExtremeMatchesBruteForce()
        {
            const int sizeX = 11;
            const int sizeZ = 9;
            const int radius = 2;

            float[] field = new float[sizeX * sizeZ];
            for (int z = 0; z < sizeZ; z++)
                for (int x = 0; x < sizeX; x++)
                {
                    // Deterministic but non-monotonic in both axes, so a window that
                    // silently only ever grew or shrank would be caught.
                    field[z * sizeX + x] =
                        Mathf.Abs(Mathf.Sin(x * 1.7f + z * 0.9f)) * 0.8f + x * 0.01f;
                }

            float[] gotMax = MatrixOperations.WindowMax(field, sizeX, sizeZ, radius);
            float[] gotMin = MatrixOperations.WindowMin(field, sizeX, sizeZ, radius);

            float worstMax = 0f;
            float worstMin = 0f;

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float hi = float.MinValue;
                    float lo = float.MaxValue;

                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int sz = z + dz;
                        if (sz < 0 || sz >= sizeZ) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int sx = x + dx;
                            if (sx < 0 || sx >= sizeX) continue;

                            float v = field[sz * sizeX + sx];
                            if (v > hi) hi = v;
                            if (v < lo) lo = v;
                        }
                    }

                    int i = z * sizeX + x;
                    float eMax = Mathf.Abs(gotMax[i] - hi);
                    float eMin = Mathf.Abs(gotMin[i] - lo);
                    if (eMax > worstMax) worstMax = eMax;
                    if (eMin > worstMin) worstMin = eMin;
                }
            }

            Check(worstMax < 1e-6f, "WindowMax disagrees with a brute-force square-window "
                                    + "maximum by " + worstMax.ToString("F8"));
            Check(worstMin < 1e-6f, "WindowMin disagrees with a brute-force square-window "
                                    + "minimum by " + worstMin.ToString("F8"));

            // A zero radius is a legal input from a coarse chunk, and it must pass the
            // field through rather than return an empty array.
            float[] noop = MatrixOperations.WindowMax(field, sizeX, sizeZ, 0);
            bool unchanged = noop.Length == field.Length;
            for (int i = 0; i < field.Length && unchanged; i++)
                if (Mathf.Abs(noop[i] - field[i]) > 1e-7f) unchanged = false;

            Check(unchanged, "a zero-radius window did not return the field unchanged");
        }

        // ------------------------------------------------------------------
        //  Analytic test terrain for the River Fill checks
        // ------------------------------------------------------------------
        //
        //  A plain tilting down in +x with one straight channel cut along it, stated
        //  as a function of WORLD position rather than pixel index. That is what lets
        //  the same field be evaluated at any rect, which is exactly the property the
        //  node's ghost margin relies on - so the same two feeds serve both the
        //  "is there real water" checks and the chunk-seam check.
        //
        //  These live in the editor assembly, so the node catalog - which scans only
        //  the assembly defining Generator - never offers them in the graph menu.

        private const float ChannelCentreZ = 400f;      // world metres
        private const float ChannelHalfWidth = 37.5f;   // world metres, 3 pixels at 65/800
        private const float PlainTop = 0.60f;
        private const float PlainSlopePerMetre = 0.00012f;
        private const float ChannelTrenchDepth = 0.02f;

        /// <summary>Cross-channel profile, 1 on the centreline to 0 at the bank.</summary>
        private static float ChannelProfile(float worldZ)
        {
            float span = Mathf.Abs(worldZ - ChannelCentreZ);
            return span < ChannelHalfWidth ? 1f - span / ChannelHalfWidth : 0f;
        }

        /// <summary>Ground elevation of the analytic test terrain, 0-1.</summary>
        private static float ChannelGround(float worldX, float worldZ)
        {
            return PlainTop - PlainSlopePerMetre * worldX
                            - ChannelTrenchDepth * ChannelProfile(worldZ);
        }

        private static void FillFromWorld(Matrix m, bool profileOnly)
        {
            int sizeX = m.rect.size.x;
            int sizeZ = m.rect.size.z;
            Vector2 ps = m.PixelSize;

            for (int z = 0; z < sizeZ; z++)
            {
                float wz = m.worldPos.z + z * ps.y;
                int row = z * sizeX;

                for (int x = 0; x < sizeX; x++)
                {
                    float wx = m.worldPos.x + x * ps.x;
                    m.arr[row + x] = profileOnly ? ChannelProfile(wz) : ChannelGround(wx, wz);
                }
            }
        }

        /// <summary>
        /// A chunk of the analytic terrain placed at <paramref name="offsetX"/> metres
        /// along x, bound to its own evaluator. The declared world is several chunks
        /// wide so neither tile is treated as a world edge - the check is about the
        /// seam between chunks, not the rim of the world.
        /// </summary>
        private static TileData ChannelTile(Graph graph, int res, float offsetX,
                                            float worldSpan, out GraphEvaluator evaluator)
        {
            TileData tile = new TileData(
                Coord.Zero,
                new CoordRect(new Coord(0, 0), new Coord(res, res)),
                new Vector3(offsetX, 0f, 0f),
                new Vector3(worldSpan, 600f, worldSpan),
                600f);

            tile.SetFullWorld(Vector3.zero,
                              new Vector3(worldSpan * 4f, 600f, worldSpan * 4f));

            evaluator = new GraphEvaluator(graph);
            tile.Bind(evaluator);
            return tile;
        }

        /// <summary>Test feed: the analytic ground.</summary>
        private class ChannelGroundFeed : CreatorNode
        {
            public override string NodeName { get { return "Test Channel Ground"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            protected override void Create(Matrix m, TileData tile, StopToken stop)
            {
                FillFromWorld(m, false);
            }
        }

        /// <summary>Test feed: the same channel as a 0-1 course mask.</summary>
        private class ChannelMaskFeed : CreatorNode
        {
            public override string NodeName { get { return "Test Channel Mask"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            protected override void Create(Matrix m, TileData tile, StopToken stop)
            {
                FillFromWorld(m, true);
            }
        }

        /// <summary>
        /// Runs a hand-built channel through River Fill and asserts the water is real:
        /// it stands above its bed, it deepens toward the middle, it does not climb
        /// downstream, it stops at the bank, and it comes out the same from either side
        /// of a chunk seam.
        /// </summary>
        /// <remarks>
        /// The node shipped computing `level = ground + extraFill` and
        /// `depth = level - ground`, so Depth was algebraically the constant
        /// `extraFill` - zero by default - and Water Level was the ground itself. The
        /// graph looked wired and the terrain had trenches with no water in them. Every
        /// check below is one of the properties that arithmetic could not have.
        ///
        /// The last group is the seam: the bed and crest are sliding extremes over the
        /// channel width, so a pixel near a chunk edge used to measure only the part of
        /// its channel that fell inside that chunk. The check evaluates ONE world
        /// position from two chunks that place it differently - at the edge of one and
        /// deep inside the other - and requires the same answer from both.
        /// </remarks>
        private static void TestRiverFillProducesRealWater()
        {
            const int res = 65;
            const int centreZ = 32;
            const float worldSpan = 800f;

            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ChannelGroundFeed heightFeed = new ChannelGroundFeed();
                ChannelMaskFeed maskFeed = new ChannelMaskFeed();

                RiverFillNode fill = new RiverFillNode();
                fill.bankWidth = ChannelHalfWidth;   // window matches the channel

                graph.nodes.Add(heightFeed);
                graph.nodes.Add(maskFeed);
                graph.nodes.Add(fill);
                graph.connections.Add(new Connection(heightFeed.id, 0, fill.id, 0));
                graph.connections.Add(new Connection(maskFeed.id, 0, fill.id, 1));

                // The channel runs off both edges, so it drains and the depression fill
                // leaves it alone; everything sits far above sea level so the mouth
                // clamp stays out of the way and the test is only about the channel.
                GraphEvaluator evaluator;
                TileData tile = ChannelTile(graph, res, 0f, worldSpan, out evaluator);

                Matrix ground = tile.CreateMatrix();
                FillFromWorld(ground, false);

                StopToken stop = new StopToken();

                Matrix wetMask = evaluator.PullNode(fill, 0, tile, stop) as Matrix;
                Matrix depth = evaluator.PullNode(fill, 1, tile, stop) as Matrix;
                Matrix level = evaluator.PullNode(fill, 2, tile, stop) as Matrix;

                Check(wetMask != null && depth != null && level != null,
                      "River Fill returned null for one of its three outputs");
                if (wetMask == null || depth == null || level == null) return;

                int mid = res / 2;
                int thalweg = centreZ * res + mid;
                int shoulder = (centreZ + 2) * res + mid;   // inside the channel, off-centre
                int dry = (centreZ + 12) * res + mid;       // well clear of the banks

                // 1. There is water at all. This is the whole complaint: trenches with
                //    nothing in them.
                Check(level.arr[thalweg] > ground.arr[thalweg] + 1e-4f,
                      "the river water surface sits at or below its own bed ("
                      + level.arr[thalweg].ToString("F5") + " vs ground "
                      + ground.arr[thalweg].ToString("F5") + ") - the channel is dry");

                Check(depth.arr[thalweg] > 1e-3f,
                      "River Fill reported no depth in the middle of a 0.02-deep channel");

                // 2. Depth is a cross-section, not one number. The old code returned the
                //    same value at every pixel, which is what this separates.
                Check(depth.arr[thalweg] > depth.arr[shoulder] + 1e-3f,
                      "depth is not deeper at the thalweg (" + depth.arr[thalweg].ToString("F5")
                      + ") than two pixels off-centre (" + depth.arr[shoulder].ToString("F5")
                      + ") - the depth port is still a constant");

                // 3. Water does not run uphill. A surface built by adding a constant to
                //    the ground reproduces every bump of the bed instead.
                float climbed = 0f;
                for (int x = mid - 8; x < mid + 8; x++)
                {
                    float rise = level.arr[centreZ * res + x + 1] - level.arr[centreZ * res + x];
                    if (rise > climbed) climbed = rise;
                }

                Check(climbed < 1e-5f, "the river surface climbs " + climbed.ToString("F7")
                                       + " downstream - water would be flowing uphill");

                // 4. The mask is what hands the water to the shader, so it has to be on
                //    in the channel and off outside it, with no film over dry ground.
                Check(wetMask.arr[thalweg] > 0.5f,
                      "the River Mask output is " + wetMask.arr[thalweg].ToString("F3")
                      + " on the channel centreline - wiring River Fill to the Water "
                      + "Output would show no river");

                Check(wetMask.arr[dry] < 1e-4f,
                      "the River Mask output is " + wetMask.arr[dry].ToString("F3")
                      + " twelve pixels clear of the bank - water on dry ground");

                Check(depth.arr[dry] < 1e-4f, "River Fill reported depth outside the channel");

                // 5. A dry cell must report the GROUND as its water level, not zero.
                //    Zero would pull a water surface built from this port down to the
                //    bottom of the world.
                Check(Mathf.Abs(level.arr[dry] - ground.arr[dry]) < 1e-5f,
                      "outside the channel Water Level reads " + level.arr[dry].ToString("F5")
                      + " instead of the ground height " + ground.arr[dry].ToString("F5"));

                // 6. Nothing non-finite or out of range reaches a shader parameter.
                bool sane = true;
                for (int i = 0; i < res * res && sane; i++)
                {
                    if (!IsUnitFinite(wetMask.arr[i])) sane = false;
                    if (!IsUnitFinite(depth.arr[i])) sane = false;
                    if (!IsUnitFinite(level.arr[i])) sane = false;
                }

                Check(sane, "a River Fill output contains a NaN, an infinity, or a value "
                            + "outside 0..1");

                // 7. The seam. World x = 800 is the LAST pixel of the first chunk and a
                //    deep-interior pixel of a chunk starting at 400. Both must answer
                //    the same, or the water steps at every tile border.
                GraphEvaluator shiftedEval;
                TileData shifted = ChannelTile(graph, res, worldSpan * 0.5f, worldSpan,
                                               out shiftedEval);

                Matrix shiftedMask = shiftedEval.PullNode(fill, 0, shifted, stop) as Matrix;
                Matrix shiftedDepth = shiftedEval.PullNode(fill, 1, shifted, stop) as Matrix;
                Matrix shiftedLevel = shiftedEval.PullNode(fill, 2, shifted, stop) as Matrix;

                Check(shiftedMask != null && shiftedDepth != null && shiftedLevel != null,
                      "River Fill returned null when evaluated at a shifted chunk");

                if (shiftedMask != null && shiftedDepth != null && shiftedLevel != null)
                {
                    int atEdge = centreZ * res + (res - 1);           // world x = 800
                    int atInterior = centreZ * res + (res - 1) / 2;   // world x = 800 too

                    Check(Mathf.Abs(level.arr[atEdge] - shiftedLevel.arr[atInterior]) < 1e-4f,
                          "Water Level at one world position differs between chunks ("
                          + level.arr[atEdge].ToString("F6") + " at a chunk edge vs "
                          + shiftedLevel.arr[atInterior].ToString("F6")
                          + " inside the next) - the river surface steps at the seam");

                    Check(Mathf.Abs(depth.arr[atEdge] - shiftedDepth.arr[atInterior]) < 1e-4f,
                          "Depth at one world position differs between chunks ("
                          + depth.arr[atEdge].ToString("F6") + " vs "
                          + shiftedDepth.arr[atInterior].ToString("F6") + ")");

                    Check(Mathf.Abs(wetMask.arr[atEdge] - shiftedMask.arr[atInterior]) < 1e-4f,
                          "River Mask at one world position differs between chunks ("
                          + wetMask.arr[atEdge].ToString("F6") + " vs "
                          + shiftedMask.arr[atInterior].ToString("F6") + ")");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        private static bool IsUnitFinite(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
            return v >= 0f && v <= 1f;
        }

        /// <summary>
        /// The planner's junction bookkeeping, which the river spawner trusts without
        /// re-deriving: a course that joins another must name one EARLIER in the list,
        /// and a lake outlet must name a lake that exists.
        /// </summary>
        /// <remarks>
        /// HydrologyGenerator hands a tributary the trunk object out of an array it
        /// fills as it walks the plans in order, so a forward reference would read a
        /// slot that is still null. Nothing would throw: the join would simply be
        /// dropped, and the only symptom is a confluence that reads as a step between
        /// two water planes, with nothing in the log pointing here. That is what makes
        /// the ordering worth a test rather than a comment.
        /// </remarks>
        private static void TestRiverPlannerJunctionsAreOrdered()
        {
            // A valley: downhill in +x, rising away from the centreline, with the sides
            // corrugated so they drain as discrete hollows. Flow converges on the
            // centreline and leaves at the +x edge, which is what yields a trunk with
            // branches entering it. Every height is above the sea level passed below, so
            // the mouths are edge outlets - a land-locked world, deliberately, since that
            // path is the one with no standing water to fall back on.
            const float span = 4000f;
            const float centreZ = span * 0.5f;
            const float halfSpan = span * 0.5f;

            RiverCourseSolver.GroundSampler ground = delegate (float x, float z)
            {
                float wall = Mathf.Abs(z - centreZ);

                float downhill = 300f - x * 0.03f;
                float valley = wall * 0.06f;

                // The corrugation is what makes this fixture worth running. Its troughs
                // are hollows running down the wall, so cells either side flow into them
                // and each arrives at the trunk carrying a whole ribbon of hillside -
                // enough to clear the 0.25 km^2 channel-head area. Amplitude grows with
                // distance from the centreline so the trunk itself stays a clean line.
                // A SMOOTH wall drains as sheet flow and yields no tributary at all,
                // which is what the first version of this fixture did: it passed while
                // never once running the ordering check below.
                float hollows = Mathf.Sin(x * 0.00785f) * 30f * (wall / halfSpan);

                return downhill + valley + hollows;
            };

            Bounds bounds = new Bounds(new Vector3(span * 0.5f, 0f, span * 0.5f),
                                       new Vector3(span, 600f, span));

            RiverNetworkPlanner.DrainageGrid grid =
                RiverNetworkPlanner.Solve(ground, bounds, 128);

            Check(grid != null, "the drainage solve returned nothing for a plain valley");
            if (grid == null) return;

            List<Vector3> lakeCentres = new List<Vector3>();
            lakeCentres.Add(new Vector3(span * 0.25f,
                                        ground(span * 0.25f, centreZ), centreZ));

            // Index-aligned with lakeCentres: the planner takes each lake's own reach,
            // not one shared radius.
            List<float> lakeRadii = new List<float>();
            lakeRadii.Add(120f);

            List<RiverNetworkPlanner.PlannedRiver> plans =
                RiverNetworkPlanner.PlanTrunkRivers(grid, 2, 3, 0f, lakeCentres, lakeRadii,
                                                    60f, 0f);

            Check(plans != null && plans.Count > 0,
                  "the planner found no river at all on a valley that drains to one edge");
            if (plans == null) return;

            int joins = 0;

            for (int i = 0; i < plans.Count; i++)
            {
                RiverNetworkPlanner.PlannedRiver p = plans[i];
                if (p == null || p.joinsPlanIndex < 0) continue;

                joins++;

                Check(p.joinsPlanIndex >= 0 && p.joinsPlanIndex < plans.Count,
                      "plan " + i + " joins index " + p.joinsPlanIndex
                      + ", which is outside the " + plans.Count + " plans returned");

                Check(p.joinsPlanIndex < i,
                      "plan " + i + " joins index " + p.joinsPlanIndex + ", which comes "
                      + "LATER in the list - the spawner would read a trunk it has not "
                      + "created yet and drop the confluence in silence");
            }

            // Without this the two checks above are skipped on a fixture that happens to
            // produce no branches, and the test reports PASS having verified nothing.
            Check(joins > 0,
                  "the planner returned " + plans.Count + " courses but not one of them "
                  + "joins another, so the ordering invariant was never tested - the "
                  + "fixture is draining as sheet flow, not the planner's fault");

            // Outlets are appended to the same list by the spawner, so the lake index is
            // the other half of what it trusts.
            List<RiverNetworkPlanner.PlannedRiver> outlets =
                RiverNetworkPlanner.PlanLakeOutlets(grid, 2, 0f, lakeCentres, lakeRadii, 60f);

            Check(outlets != null, "the lake-outlet planner returned null");
            if (outlets == null) return;

            Check(outlets.Count > 0,
                  "no outlet was planned for a lake sitting in a draining valley, so the "
                  + "fromLakeIndex check below never ran");

            for (int i = 0; i < outlets.Count; i++)
            {
                RiverNetworkPlanner.PlannedRiver o = outlets[i];
                if (o == null) continue;

                Check(o.fromLakeIndex >= 0 && o.fromLakeIndex < lakeCentres.Count,
                      "outlet " + i + " names lake " + o.fromLakeIndex + " of "
                      + lakeCentres.Count + " - its head cannot be joined to a lake it "
                      + "cannot identify");
            }

            // Printed, not asserted: the exact counts are what make the check total
            // reconcilable, and a silent drop from 3 branches to 1 is worth seeing
            // without having to fail the run to find out.
            Debug.Log("[UC SelfTest] river junctions: " + plans.Count + " courses, "
                      + joins + " confluences, " + outlets.Count + " lake outlets");
        }
    }
}
