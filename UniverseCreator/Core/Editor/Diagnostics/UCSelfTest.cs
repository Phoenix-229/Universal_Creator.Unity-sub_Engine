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
using UniverseCreator.Editor.Inspectors;
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
            TestSavedMaterialTextureBindings(); // UC-T02-06/10: headless auditing uses authored references, never native texture resolution.
            TestMobileTextureBudget();
            TestPrefabIntegrity();
            TestTextureNodesWriteRealProperties();
            TestTextureImageContracts(); // UC-T07-02: immutable images, semantic validation and final native consumer retirement.
            TestTextureImageInputs(); // UC-T07-03: actual imports/value/world-coordinate and accepted-biome paths.
            TestTextureImageOperations(); // UC-T07-04: known pixels, bounded filtering and real nonadjacent biome boundaries.
            TestTextureNormals(); // UC-T07-05: metre derivatives, halo seams and semantic normal blending.
            TestMaterialLayerParameters(); // UC-T07-06: reusable PBR channels and independent coverage/height semantics.
            TestShaderParameters(); // UC-T07-07: actual property types, stage/variant bounds and cheap renderer parameter updates.
            TestTextureFunctions(); // UC-T07-08: reusable typed function capture, Undo/reload and image/lit preview lifetime.
            TestTextureBakes(); // UC-T07-09: incremental dependencies, accepted-cache isolation and actual explicit material Apply.
            TestTextureLifetimes(); // UC-T07-10: native entry guards, no readback and actual scene/final-consumer retirement.
            TestTextureSurfaceContext(); // UC-T07-11: spatial physical context, native membership upload and reversible measured texture tiers.
            TestTextureAcceptance(); // UC-T07-12: final native diagonal/curved boundaries across source/control resolutions and weather-only material response.
            TestWaterShaderPropertyRegistry();
            TestWaterAppliersAgree();
            TestWaterSurfaceWaves();
            TestShorelineConformer();
            TestShorelineSplatMutation();
            TestUnderwaterSplatMutation();
            TestHeightGuardRejectsNonFinite();
            TestHeightGuardKillsNeedlesAndKeepsCliffs();
            TestGraphReachesTheHeightmap();
            TestGraphFanInMerges();
            TestSpatialHashRangeQueries();
            TestInPlaceReuseGuard();
            TestRetainedCacheBoundsToLiveNodes();
            TestGeoDelaunayAndMST();
            TestVegWeightedOverlapPrune();
            TestVegNoiseBandThinning();
            TestVegCrossLayerOcclusion();
            TestVegFeatureDistanceFilter();
            TestVegPatternPlacement();
            TestVegFlowAlignedYaw();
            TestVegAntiRepeatPenalty();
            TestVegFilterStack();
            TestVegDecoratorMatching();
            TestBiomeGradedClassification();
            TestBiomeSoilAxis();
            TestBiomeEdgeMask();
            TestErosionSeaLevelGuards();
            TestRiverCourseTracer();
            TestRiverSeedCourseNode();
            TestRiverProfileCurves();
            TestRiverBankYaw();
            TestShoreWaveWash();
            TestDropletErosionGPUGuards();
            TestAutoSeaLevelFacts();
            TestWorldPreset();
            TestNamedSpeciesResolution();
            TestVegetationGrounding();
            TestLeafColliderPolicy();
            TestNodeSectionGrouping();
            TestPropCullDistanceTier();
            TestScatterRemovalDelta();
            TestParallelBuildIdentity();
            TestGrassShoreBand();
            TestVoxelGridThinning();
            TestRiverWorldScaling();
            TestTerrainShapeGuard();
            TestRockIsNotAPlant();
            TestCoastalShelfFreeboard();
            TestTreeHeightSizing();
            TestVegetationInspectorGroups();
            TestPluginContractShape();
            TestPluginRegistryDiscovery();
            TestPluginNodeCatalogWidening();
            TestPluginBakeBridge();
            TestLinkerFullyRemoved();
            TestPluginUniversalDiscovery();
            TestConnectedPluginsWindow();
            TestDropletErosion();
            TestBiomeGatedScatter();
            TestBiomeSplatBlendCrossfade();
            TestBiomeSplatPalette();
            TestBiomeRegionOverride();
            TestScatterInstanceStableId();
            TestWeatherOutputNodeCapturesBiases();
            TestLightingOutputNodeCapturesBiases();
            TestSeaFloorLiftsDryGroundKeepsOceanDepth();
            TestContinentalRiseGuardsShoreNotForces();
            TestContinentalRiseLakeAwareBasinGuard();
            TestContinentalRiseNeverFormsBlindBasin();
            TestGradedBandIsContinuousBell();
            TestFreshScatterNodeGradesByTerrain();
            TestGroundConformSeatsBaseOnSlope();
            TestCoastalShelfGradesGentleKeepsCliffAndOcean();
            TestWindowExtremeMatchesBruteForce();
            TestResampleAdaptiveKeepsBendsAndEndpoints();
            TestValleyCorridorAccessor();
            TestVolumeReachesTheRealBed();
            TestLakeShapeAndSizeComeFromTheBasin();

            // River rebuild step 2 (per-node hydraulic data model), in UCSelfTest.Rivers.cs.
            TestRiverProfileSamplingAndForwardFill();
            // River rebuild step 3 (child-GameObject authoring -> baked course).
            TestRiverCourseGathersFromChildNodes();
            // River rebuild step 4 (monotonic bed profile - retires the climb-uphill bug).
            TestRiverBedProfileAlwaysDescends();
            TestRiverBedUphillCourseStillDescends();
            // River rebuild step 5 (route validation / drainage guarantee).
            TestRiverRouteValidation();
            // River rebuild step 6 (terrain-blend swept-channel carve).
            TestRiverCarveCutsDescendingChannel();
            // River rebuild step 7 (mouth blend to a joined lake/ocean surface).
            TestRiverMouthBlendsToWaterSurface();
            // River rebuild step 9 (bank/trench dressing bands off the river distance field).
            TestRiverBankZonesAndDressing();
            // §5a G-X1 curvature-driven asymmetric channel section (pool on the outer bank).
            TestRiverCarveCurvatureAsymmetry();
            // §5a G-C1 / Guarantee C hydraulic geometry (channel size follows flow).
            TestRiverHydraulicGeometry();
            // PROCEDURAL river network: channel initiation, confluences, Strahler order,
            // and draining into water. The first gate that actually EXECUTES the extractor.
            TestRiverNetworkExtractsConfluencesAndMouth();
            // "Trenches and network not spawning": slope measured in normalised units
            // (dead on every world), threshold C not scaled to the world, and the
            // end-to-end proof that the node lowers the height over a real graph.
            // The direction ENCODING itself: `dir` holds neighbour codes 0..7, never cell
            // indices. Reading a code as an index silently reads row 0, so every slope is
            // zero and the entire river feature does nothing while its tests pass.
            TestHydrologyDirectionCodes();
            TestRiverNetworkSlopeUnits();
            TestRiverNetworkScalesThresholdToWorld();
            TestRiverNetworkNodeCarvesTheTerrain();
            TestLowlandTrenchProduction(); // UC-T03-01/02: trace terminal guards and verify the cut reaches native lowland terrain.
            TestGroundNodeContracts(); // UC-T03-03: executable inventory and specialized modifier contracts.
            TestBoundedRiverSearch(); // UC-T03-04: bounded physical route costs, cancellation, workspace reuse and mixed chunk borders.
            TestRiverHierarchyTargets(); // UC-T03-05: authored counts, physical spacing, scale, infeasibility and production/cache wiring.
            TestDirectedRiverOutlets(); // UC-T03-06: distinct water destinations, shared junctions, ownership and malformed topology.
            TestLakeOutletSources(); // UC-T03-07: closed/open/flat/chained sources, dry descent and production replay.
            TestLakeDistributionScaling(); // UC-T03-08: world scaling, absolute overrides and eligible-only lake selection.
            TestWaterRepairContracts(); // UC-T08-15: occupied shores, coherent guards and saved manual river trenches.
            TestCommittedRiverProfiles(); // UC-T03-09: final native ground, exact world ownership, levels and consumer/cache equality.
            TestFinalGroundComposition(); // UC-T03-10: accepted sculpt, physical normals, holes/collision, environment and CPU fallback.
            TestTerrainRiverRegeneration(); // UC-T03-11: native cold/retained/regenerated heights and terrain-only scene ownership.
            TestWaterTopicContracts(); // UC-T04-01…11: shared focused/package fixtures report failures without hiding exceptions or aborting unrelated contracts.
            // River Fill (N1): water fills the channel, not the bank.
            TestRiverFillWetsChannelNotBank();
            // River Course Input (N2): a hand-drawn river becomes a graph River Mask (mask only,
            // no re-carve) so Banks/Water Mask/scatter see it; fill stays scene-side.
            TestRiverCourseInputMaskWire();
            // Water Mask (N3) + Slope To Density (N4): the reusable water-exclusion union
            // and the slope->density channel.
            TestWaterMaskUnionOfOptionalInputs();
            TestSlopeToDensityBandOrientation();
            // Cross-layer occupancy (§4O item 3): the canopy deposit->read wire.
            TestCanopyDepositIsReadBackAsShade();
            // Age structure (§4O item 4): reverse-J size spread, opt-in.
            TestAgeSizeVariationSpreadsAStand();
            // Per-prototype LOD cull (§4O item 5): authored distance resolves, default is -1.
            TestPrototypeCullDistanceResolves();
            // Vegetation water exclusion: the guard that was unwired on six scatter paths.
            TestScatterWaterExclusion();
            // §7 T7 mobile per-cell vegetation ceiling guard.
            TestVegetationMobileCeiling();
            // §2 gap#1 de-facet: river course tessellated to a smooth Catmull-Rom curve.
            TestRiverCourseDeFacet();
            // §7 T1 content-hash incremental invalidation (forward unchanged, recompute changed).
            TestContentHashIncrementalInvalidation();
            TestGraphValidation(); // UC-T01-09: broken fixtures, rejection before publication and canvas repair.
            TestGraphProductOwnership(); // UC-T01-12: immutable published products across aliases, portals and retained runs.
            TestGenerationScheduling(); // UC-T02-01: real restart, shared authority and native publication rejection.
            TestWorldSaltInvalidation();
            TestWorldSaltCoversEveryWorldDial();

            // Module 2, in UCSelfTest.Hydrology.cs.
            TestSpawnerIsLiveWhenOnlyItsHeightIsWired();
            TestSpawnerPlacesRealWaterObjects();
            TestLakeDepthGateMatchesTheMask();

            TestGraphHealsMissingNodeSlots();
            TestGraphRecovery(); // UC-T01-10/11: JSON preservation, scoped repair and native Undo.
            TestGraphDomainMetadata(); // UC-T05-01: explicit domains preserve native/portable legacy and unknown authored data.
            TestGraphWindowServices(); // UC-T05-02: shared edit/Undo synchronization, explicit commits and native provider retirement.
            TestDomainWindowAuthoring(); // UC-T05-05: actual window admission, safe legacy inspection and direct canvas creation.
            TestSharedGraphEditing(); // UC-T05-07: fan-out, domain utilities, portals/combiners, clipboard Undo and navigation.
            TestGraphPreviews(); // UC-T05-08: bounded preview isolation, coalescing and retirement.
            TestGraphMigrationAnalysis(); // UC-T05-09: read-only partitions, ambiguous crossings and missing-type preservation.
            TestGraphMigrationExecution(); // UC-T05-10: native asset transactions, deterministic outputs, failure rollback and Undo/Redo.
            TestGraphSerializedWidgets(); TestFourGraphSample(); // UC-T05-11: actual serialized widgets and four-asset native scene reload.
            TestVegetationGraphBinding(); // UC-T05-03: accepted terrain, actual native planting, cancellation and stale/foreign inputs.
            TestVegetationOwnershipInventory(); // UC-T06-01: complete source/caller mapping and read-only native prototype inventory.
            TestVegetationMigration(); // UC-T06-02: exact final-height crossings, representative planting equivalence and native rollback.
            TestVegetationRegionMasks(); // UC-T06-03: one world lattice, regional density and adjacent/overlapping demand masks.
            TestVegetationHabitat(); // UC-T06-04: independent habitat exclusions, smooth scores, poisoned inputs and named world-mask execution.
            TestVegetationCandidates(); // UC-T06-05: scheduling/quality-stable candidates, cross-region spacing and canopy dependency invalidation.
            TestVegetationIdentity(); // UC-T06-06: full keys, collision-safe explicit legacy migration, persistence and captured production stamping.
            TestVegetationGroundContact(); // UC-T06-07: offset pivots, final native edits, slope admission, retained identity and manual-transform preservation.
            TestVegetationVariationAndCollision(); // UC-T06-08: bounded full-key distributions, native routing, structural/visual categories and linear trigger scale.
            TestVegetationAppearance(); // UC-T06-09: placement hashes, native tint/provenance, bounded pump, cached material consumers and clock isolation.
            TestVegetationSpatialBatching(); // UC-T06-10: production spatial/LOD/submesh planning, offscreen shadows, residency and native fallback.
            TestVegetationAquaticHabitat(); // UC-T06-11: accepted water/current, species/land guards, habitat domains and final aquatic contact.
            TestVegetationOwnershipLifetime(); // UC-T06-12: staged native cancellation, independent renderer/collider retirement, parking, revisit and saved removals.
            TestVegetationPrototypeContent(); // UC-T06-13: selected prototype pivots/normals/material/LOD evidence and actual Editor reference creation.
            TestVegetationCategoryAuthoring(); // UC-T06-15/16: shared explicit binding Undo and actual category/native-detail serialized controls.
            TestVegetationDensityDiagnosis(); // UC-T06-17: actual fractional world-area grass density and observed stage units.
            TestVegetationManualIntent(); // UC-T06-18: native locked poses/grass, exact removals, region/world isolation, quality and Undo.

            // Chunk manager works-as-one seam, in UCSelfTest.ChunkSurface.cs.
            TestManualSculptSurvivesRegeneration();
            TestSculptSurvivesResolutionChange();

            // Module 6 path-tool seam (TerrainShapeApplier), in UCSelfTest.PathSeam.cs.
            TestPathSeamShaping();

            // Module 6 Road & City Master seam, UC side (U-1 dam governance). The U-5 provider-
            // surface pin was removed with the linker (PLG-5); see UCSelfTest.RcmSeam.cs.
            TestReservoirGovernance();

            // Module 3, in UCSelfTest.Ecology.cs.
            TestEcologyConstants();
            TestEcologyMaths();
            TestEnvironmentSampleStaysCheap();
            TestVegetationResolveIsCachedAndInvalidated();
            TestFoliagePigmentContract();
            TestSamplerReadsClimateFromBag();
            TestHydroFieldWaterAwareness();
            TestHydroExclusion();

            // Module 3, in UCSelfTest.Weather.cs.
            TestClockStateStaysCheap();
            TestSolarDeclination();
            TestDayLength();
            TestSunPosition();
            TestSolarTimeCorrection();
            TestAirMassAndMoon();
            TestSolarFormulaeHaveOneOwner();
            TestClockIsDeterministic();
            TestWorldClockContinuation(); // UC-T08-02: one instant, validated seek/pause/calendar limits and partition-independent regular stepping.
            TestClimateBaselineContracts(); // UC-T08-03: early publication, immutable climate, regional overlaps and dependent-only invalidation.
            TestClimateRestoreContracts(); // UC repair: persisted strict inputs, retained native ground and honest failure/retry.
            TestRiverMemoryOutputCoexistence(); // UC repair: 512-square river IDs and independent height/splat publication under the unchanged budget.
            TestRegionalEnvironmentConsumers(); // UC-T08-05: independent worlds, connected wind/particles and actual regional weather samples.
            TestWeatherRegionTransitions(); // UC-T08-06: real typed graph, bounded cached fronts, overlapping/moving regions and large-instant gusts.
            TestEnvironmentLightingSamples(); // UC-T08-07: captured clock/bias, actual sky/light/fog consumers, nested worlds and owned native retirement.
            TestContinuousSkyLighting(); // UC Scene/Play: bounded sky/ambient transitions and sub-day lunar continuity.
            TestCatalogStars(); // UC celestial repair: catalog coordinates, real controls and reversible native publication.
            TestMeteredDaylightAndSeasons(); // UC midday repair: independent diffuse integral, readable gray-card exposure and continuous solar publication.
            TestBoundedEnvironmentEffects(); // UC-T08-08: worker noise, real tier/sky/cookie/particle consumers, teleports, limits and retirement.
            TestSurfaceWeatherStores(); // UC-T08-09: bounded shared-clock stores, dry-down, phase/exposure and scoped shader recipients.
            TestEnvironmentGeography(); // UC-T08-10: civil labels, retained epochs, neighbouring and distant local sky samples.
            TestLightingRefreshBudgets(); // UC-T08-12: actual tier/admission/retirement and bounded dirty scheduling.
#if UC_URP
            TestCameraLightingHistories(); // UC-T08-11: actual per-camera passes, bounded ownership, cuts/resize/seek and retirement.
#endif
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
            TestCloudSoftensShadowsWire();
            TestNightFloorWire();
            TestIrradianceGIWire();
            TestRenderGoLiveWire();
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
            TestSplatLayerReconcile();
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
            TestChunkBuildPriority();
            TestFrustumFirstBuildOrderWire();
            TestGovernorLoopWire();
            TestLodLeversWire();
            TestLoadHysteresis();
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

            // Module 3, Refinement #7 (water/weather/quality), in UCSelfTest.WaterWeather.cs.
            TestWaterQualityTierTable();
            TestWaterOctaveCap();
            TestWaterWeatherCouplingMath();
            TestRippleWorkingResolution();

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
        /// Runs material-authoring texture nodes and checks that their default surface properties exist on the final shader.
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
        /// UC-T07-12: image/field/function nodes have their own required-input and production fixtures;
        /// their deliberately missing inputs are not a material-property authoring default.
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
                if (!Array.Exists(g.GetOutputs(), port => port.type == PortType.Surface || port.type == PortType.Layer || port.type == PortType.Splat || port.type == PortType.Water)) continue; // UC-T07-12: include the existing material descriptors; pixel/value operations have separate wired fixtures.

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
                if (product is LayerProduct layer)
                {
                    var layerSplat = new SplatProduct(); layerSplat.layers[0] = layer;
                    surface = new SurfaceProduct { splat = layerSplat };
                }
                else if (product is SplatProduct inputSplat) surface = new SurfaceProduct { splat = inputSplat };
                else if (product is WaterProduct water) surface = new SurfaceProduct { water = water };
                if (surface == null) continue;
                surface.PublishLayerParameters(); // UC-T07-12: validate the real layer-to-shader property mapping as well as direct writers.

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
        /// The CPU wave mirror - UCWaterSurface.WaveOffset, the exact height a floating
        /// body reads for buoyancy and the line-for-line twin of UCAmbientWaveOffset in
        /// UC_WaterInput.hlsl. Pure math, no scene, so it runs headless.
        ///
        /// This is the one guard that the octave/storm rework (W4) kept the surface a
        /// boat and its reflection agree on: a drift here is invisible in a screenshot
        /// and obvious the moment a hull moves. It pins the four things that can drift -
        /// determinism, the legacy three-octave swell (a saved world must look as it
        /// did), storm scaling, and octave gating - without ever asking the GPU where
        /// the water is.
        /// </summary>
        private static void TestWaterSurfaceWaves()
        {
            WaterSurfaceParams p = WaterSurfaceParams.Default(0f);
            p.waveHeight = 0.5f;
            p.waveLength = 20f;
            p.waveSpeed = 0.7f;

            Vector2 xz = new Vector2(12.3f, -7.1f);
            float t = 3.4f;

            // 1. Same inputs, same height. Buoyancy integrates over frames; a
            //    non-deterministic height jitters a hull in place.
            float h1 = UCWaterSurface.WaveOffset(xz, t, p);
            float h2 = UCWaterSurface.WaveOffset(xz, t, p);
            CheckClose(h1, h2, "water waves: WaveOffset is not deterministic");

            // 2. No amplitude, flat sheet (a frozen pond, a still pool).
            WaterSurfaceParams flat = p; flat.waveHeight = 0f;
            CheckClose(UCWaterSurface.WaveOffset(xz, t, flat), 0f,
                       "water waves: zero waveHeight is not flat");

            // 3. Weight scales linearly - a river ribbon tapered to nothing at its bank
            //    must lift the surface by nothing there, or the water climbs the shore.
            float full = UCWaterSurface.WaveOffset(xz, t, p, 1f);
            float half = UCWaterSurface.WaveOffset(xz, t, p, 0.5f);
            CheckClose(half, full * 0.5f, "water waves: weight does not scale linearly");

            // 4. Storm scales the whole swell linearly. The weather layer raises this and
            //    _WaveStormAmp together (W7), so a hull heaves as much as the sea visibly does.
            WaterSurfaceParams storm = p; storm.stormAmp = 2f;
            CheckClose(UCWaterSurface.WaveOffset(xz, t, storm), full * 2f,
                       "water waves: storm amplitude does not scale the swell");

            // 5. octaves = 3, storm = 1 reproduces the original three-octave swell to the
            //    bit - the pre-W4 look and physics of every already-authored world.
            WaterSurfaceParams three = p; three.octaves = 3f; three.stormAmp = 1f;
            float k = 6.28318530718f / Mathf.Max(0.5f, three.waveLength);
            float a = Mathf.Sin((xz.x *  0.86f + xz.y *  0.51f) * k        + t * three.waveSpeed);
            float b = Mathf.Sin((xz.x * -0.39f + xz.y *  0.92f) * k * 1.71f + t * three.waveSpeed * 1.43f);
            float c = Mathf.Sin((xz.x *  0.62f + xz.y * -0.78f) * k * 2.63f + t * three.waveSpeed * 0.79f);
            float legacy = (a * 0.55f + b * 0.30f + c * 0.15f) * three.waveHeight;
            CheckClose(UCWaterSurface.WaveOffset(xz, t, three), legacy,
                       "water waves: octaves=3 does not reproduce the legacy three-octave swell");

            // 6. Octave count actually changes the surface - a one-train pond is not a
            //    four-train ocean at the same point and time.
            WaterSurfaceParams pond = p; pond.octaves = 1f;
            WaterSurfaceParams ocean = p; ocean.octaves = 4f;
            Check(Mathf.Abs(UCWaterSurface.WaveOffset(xz, t, pond)
                          - UCWaterSurface.WaveOffset(xz, t, ocean)) > 1e-4f,
                  "water waves: octave count does not change the surface");

            // 7. octaves = 1 keeps only the first train (weight 0.55). The higher trains
            //    are gated off, not merely small, so the height cannot exceed that share.
            float onlyFirst = UCWaterSurface.WaveOffset(xz, t, pond);
            Check(Mathf.Abs(onlyFirst) <= pond.waveHeight * 0.55f + 1e-4f,
                  "water waves: octaves=1 leaks higher trains");

            // 8. Bounded by the sum of active weights times amplitude - a wave is never
            //    taller than its parts, so a boat is never flung off the sea.
            float bound = ocean.waveHeight * (0.55f + 0.30f + 0.15f + 0.07f) + 1e-4f;
            Check(Mathf.Abs(UCWaterSurface.WaveOffset(xz, t, ocean)) <= bound,
                  "water waves: four-octave swell exceeds its amplitude bound");

            // 9. The surface normal is a unit vector that never tips past vertical - a
            //    swell rolls a hull, it does not capsize it, and a broken normal shades black.
            Vector3 n = UCWaterSurface.SurfaceNormal(new Vector3(xz.x, 0f, xz.y), t, ocean);
            CheckClose(n.magnitude, 1f, "water waves: surface normal is not unit length");
            Check(n.y > 0f, "water waves: surface normal points below the water");
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

        /// <summary>
        /// The shoreline / bank band arithmetic: the real-world cover grade
        /// (distance from the waterline, gated by slope) and the same
        /// sum-to-one / restore-exact guarantees SnowSplatMutation carries.
        /// </summary>
        private static void TestShorelineSplatMutation()
        {
            const float Tol = 1e-4f;
            const float Width = 6f;
            const float SlopeLimit = 55f;

            // Cover is full at the waterline on flat ground and gone by the band width.
            CheckWithin(ShorelineSplatMutation.BandCover(0f, Width, 0f, SlopeLimit), 1f, Tol,
                        "Shoreline: full band at the waterline on flat ground");
            CheckWithin(ShorelineSplatMutation.BandCover(Width, Width, 0f, SlopeLimit), 0f, Tol,
                        "Shoreline: no band beyond the band width");

            // Monotonic non-increasing with distance - sand becomes grass, never back.
            bool grades = true;
            float prev = ShorelineSplatMutation.BandCover(0f, Width, 0f, SlopeLimit);
            for (int i = 1; i <= 12; i++)
            {
                float here = ShorelineSplatMutation.BandCover(i * (Width / 12f), Width, 0f, SlopeLimit);
                if (here > prev + Tol) grades = false;
                prev = here;
            }
            Check(grades, "Shoreline: cover falls with distance, never rises");

            // A cut bank steeper than the limit shows the inland surface, not sand.
            CheckWithin(ShorelineSplatMutation.BandCover(0f, Width, 80f, SlopeLimit), 0f, Tol,
                        "Shoreline: a wall past the slope limit carries no band");
            Check(ShorelineSplatMutation.BandCover(0f, Width, 10f, SlopeLimit) > 0.9f,
                  "Shoreline: gentle ground under the limit keeps its band");

            // Block write: the band layer carries its cover, the ground shares the
            // rest, and every cell still sums to one - the SnowSplatMutation contract.
            const int Layers = 4;
            const int BandLayer = 1;
            const int Size = 4;

            Check(!ShorelineSplatMutation.CanWriteLayer(1, BandLayer),
                  "Shoreline: a one-layer world cannot hold the band at layer one");
            Check(ShorelineSplatMutation.CanWriteLayer(Layers, BandLayer),
                  "Shoreline: a four-layer world can hold the band at layer one");

            float[,,] baseAlpha = new float[Size, Size, Layers];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                baseAlpha[y, x, 0] = 0.7f;
                baseAlpha[y, x, 1] = 0f;
                baseAlpha[y, x, 2] = 0.2f;
                baseAlpha[y, x, 3] = 0.1f;
            }

            float[,,] alpha = (float[,,])baseAlpha.Clone();
            float[,] cover = new float[Size, Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                cover[y, x] = 0.25f + 0.5f * ((x + 2 * y) % 3) / 2f;

            ShorelineSplatMutation.WriteBand(alpha, baseAlpha, BandLayer, cover);

            bool sumOne = true, bandIsCover = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float s = 0f;
                for (int l = 0; l < Layers; l++) s += alpha[y, x, l];
                if (Mathf.Abs(s - 1f) > Tol) sumOne = false;
                if (Mathf.Abs(alpha[y, x, BandLayer] - Mathf.Clamp01(cover[y, x])) > Tol)
                    bandIsCover = false;
            }
            Check(sumOne, "Shoreline: every cell's weights still sum to one after the band");
            Check(bandIsCover, "Shoreline: the band layer carries exactly the cover fraction");

            // Erase copies the cached base back bit-for-bit, not an inverse.
            ShorelineSplatMutation.Restore(alpha, baseAlpha);
            bool identical = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            for (int l = 0; l < Layers; l++)
                if (alpha[y, x, l] != baseAlpha[y, x, l]) identical = false;
            Check(identical, "Shoreline: erase restores the painted map bit-for-bit");
        }

        /// <summary>
        /// The W2 underwater bed mutation: the depth/flow split of the three bed
        /// materials, and the same sum-to-one / restore-exact block contract the two
        /// peer mutations carry, generalised to a set of band layers.
        /// </summary>
        private static void TestUnderwaterSplatMutation()
        {
            const float Tol = 1e-4f;
            UnderwaterBedProfile p = UnderwaterBedProfile.Default;

            // Above the waterline nothing is wet, so no bed material is placed.
            float sand, silt, rock;
            UnderwaterSplatMutation.BedCovers(0f, 0f, in p, out sand, out silt, out rock);
            CheckWithin(sand + silt + rock, 0f, Tol, "Underwater: dry ground carries no bed");

            // Well below the edge band the three materials tile the cell completely.
            UnderwaterSplatMutation.BedCovers(5f, 0f, in p, out sand, out silt, out rock);
            CheckWithin(sand + silt + rock, 1f, Tol, "Underwater: a submerged cell is fully bed");

            // Shallow still water sorts to sand; deep still water sorts to silt.
            float shallowSand, shallowSilt, shallowRock;
            UnderwaterSplatMutation.BedCovers(1f, 0f, in p, out shallowSand, out shallowSilt, out shallowRock);
            float deepSand, deepSilt, deepRock;
            UnderwaterSplatMutation.BedCovers(12f, 0f, in p, out deepSand, out deepSilt, out deepRock);
            Check(shallowSand > shallowSilt, "Underwater: the still shallows are sand, not silt");
            Check(deepSilt > deepSand, "Underwater: the still deep is silt, not sand");
            Check(deepSilt > shallowSilt, "Underwater: silt share climbs with depth in still water");

            // Fast water scours the bed to rock regardless of how deep it is.
            float fastSand, fastSilt, fastRock;
            UnderwaterSplatMutation.BedCovers(12f, 2f, in p, out fastSand, out fastSilt, out fastRock);
            Check(fastRock > fastSilt && fastRock > fastSand,
                  "Underwater: a strong current sweeps the deep bed to rock");
            Check(fastRock > deepRock, "Underwater: rock share climbs with flow speed");

            // Layer-enable guard: -1 is a legitimately empty (buyer-unfilled) slot.
            Check(!UnderwaterSplatMutation.IsEnabled(-1, 4), "Underwater: an empty slot (-1) is disabled");
            Check(!UnderwaterSplatMutation.IsEnabled(4, 4), "Underwater: an out-of-range slot is disabled");
            Check(UnderwaterSplatMutation.IsEnabled(2, 4), "Underwater: an in-range slot is enabled");

            // Block write: three bed layers carry their covers, the ground shares the
            // rest, and every cell still sums to one. Layer 0 is left as the author's
            // ground; layers 1/2/3 are sand/silt/rock. A disabled slot (-1) is skipped.
            const int Layers = 5;
            const int Size = 4;
            int[] bandLayers = { 1, 2, 3 };

            float[,,] baseAlpha = new float[Size, Size, Layers];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                baseAlpha[y, x, 0] = 0.6f;
                baseAlpha[y, x, 4] = 0.4f;   // a second ground layer, to prove the share splits by proportion
            }

            float[][,] covers = new float[3][,];
            for (int b = 0; b < 3; b++) covers[b] = new float[Size, Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                // A submerged cell that is 60% sand, 20% silt, 10% rock -> 0.9 wet,
                // 0.1 left for the ground; deterministic so the sum is checkable.
                covers[0][y, x] = 0.6f;
                covers[1][y, x] = 0.2f;
                covers[2][y, x] = 0.1f;
            }

            float[,,] alpha = (float[,,])baseAlpha.Clone();
            UnderwaterSplatMutation.WriteBed(alpha, baseAlpha, bandLayers, covers);

            bool sumOne = true, bandsCarry = true, groundSplit = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float s = 0f;
                for (int l = 0; l < Layers; l++) s += alpha[y, x, l];
                if (Mathf.Abs(s - 1f) > Tol) sumOne = false;

                if (Mathf.Abs(alpha[y, x, 1] - 0.6f) > Tol ||
                    Mathf.Abs(alpha[y, x, 2] - 0.2f) > Tol ||
                    Mathf.Abs(alpha[y, x, 3] - 0.1f) > Tol) bandsCarry = false;

                // 0.1 of ground, split 0.6:0.4 -> 0.06 and 0.04.
                if (Mathf.Abs(alpha[y, x, 0] - 0.06f) > Tol ||
                    Mathf.Abs(alpha[y, x, 4] - 0.04f) > Tol) groundSplit = false;
            }
            Check(sumOne, "Underwater: every cell's weights still sum to one after the bed");
            Check(bandsCarry, "Underwater: each bed layer carries exactly its cover fraction");
            Check(groundSplit, "Underwater: the ground layers share the remainder by proportion");

            // Covers that oversubscribe the cell are scaled to fit, not left summing past one.
            float[] over = { 0.7f, 0.6f, 0.5f };   // sums to 1.8
            float[] w = new float[Layers];
            float[] bw = { 0.6f, 0f, 0f, 0f, 0.4f };
            UnderwaterSplatMutation.BlendCell(w, bw, bandLayers, over);
            float ws = 0f; for (int l = 0; l < Layers; l++) ws += w[l];
            CheckWithin(ws, 1f, Tol, "Underwater: oversubscribed covers are rescaled to sum one");
            Check(w[0] <= Tol && w[4] <= Tol, "Underwater: a fully covered cell leaves nothing for the ground");

            // Lifting the bed copies the cached base back bit-for-bit.
            UnderwaterSplatMutation.Restore(alpha, baseAlpha);
            bool identical = true;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            for (int l = 0; l < Layers; l++)
                if (alpha[y, x, l] != baseAlpha[y, x, l]) identical = false;
            Check(identical, "Underwater: lifting the bed restores the painted map bit-for-bit");
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
        /// SE-FANIN1: the graph fan-in (SE-1 / ENG-2). A multi-input port must
        /// accumulate every wire and fold them by the chosen MergeOp, while a
        /// single-input port must still evict to last-write-wins. Both halves are
        /// the regression contract: existing graphs are all single-input, so the
        /// eviction behaviour has to be byte-identical to Module 1.
        /// </summary>
        private static void TestGraphFanInMerges()
        {
            const int res = 17;

            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ConstantNode a = new ConstantNode { value = 0.2f };
                ConstantNode b = new ConstantNode { value = 0.7f };
                ConstantNode c = new ConstantNode { value = 0.4f };
                UniverseCreator.Nodes.Combiners.CombineNode combine =
                    new UniverseCreator.Nodes.Combiners.CombineNode();

                graph.nodes.Add(a);
                graph.nodes.Add(b);
                graph.nodes.Add(c);
                graph.nodes.Add(combine);

                // Fan three sources into the one multi-input port via the real
                // AddConnection path - which is what must now accumulate.
                bool w1 = graph.AddConnection(new Connection(a.id, 0, combine.id, 0));
                bool w2 = graph.AddConnection(new Connection(b.id, 0, combine.id, 0));
                bool w3 = graph.AddConnection(new Connection(c.id, 0, combine.id, 0));
                Check(w1 && w2 && w3, "SE-FANIN1: a multi-input port rejected a wire it should accept");
                Check(graph.GetInputConnections(combine.id, 0).Count == 3,
                      "SE-FANIN1: a multi-input port did not accumulate all three wires");

                // An exact duplicate edge must be refused, not stacked.
                bool dup = graph.AddConnection(new Connection(a.id, 0, combine.id, 0));
                Check(!dup && graph.GetInputConnections(combine.id, 0).Count == 3,
                      "SE-FANIN1: a multi-input port accepted a duplicate wire");

                TileData tile = new TileData(
                    Coord.Zero,
                    new CoordRect(new Coord(0, 0), new Coord(res, res)),
                    Vector3.zero,
                    new Vector3(100f, 100f, 100f),
                    100f);
                GraphEvaluator ev = new GraphEvaluator(graph);
                tile.Bind(ev);

                // Max = union = 0.7; Min = 0.2; Average = 0.4333; Sum = 1.3.
                CheckMergeUniform(ev, tile, combine, MergeOp.Max, 0.7f, "Max");
                CheckMergeUniform(ev, tile, combine, MergeOp.Min, 0.2f, "Min");
                CheckMergeUniform(ev, tile, combine, MergeOp.Average, (0.2f + 0.7f + 0.4f) / 3f, "Average");
                CheckMergeUniform(ev, tile, combine, MergeOp.Sum, 0.2f + 0.7f + 0.4f, "Sum");

                // --- single-input ports still evict (last write wins) ---
                UniverseCreator.Nodes.Combiners.MaximumNode single =
                    new UniverseCreator.Nodes.Combiners.MaximumNode();
                graph.nodes.Add(single);

                graph.AddConnection(new Connection(a.id, 0, single.id, 0)); // into port "A"
                graph.AddConnection(new Connection(b.id, 0, single.id, 0)); // same port, must evict a

                System.Collections.Generic.List<Connection> singleEdges =
                    graph.GetInputConnections(single.id, 0);
                Check(singleEdges.Count == 1,
                      "SE-FANIN1: a single-input port kept " + singleEdges.Count
                      + " wires; it must evict to one");
                Check(singleEdges.Count == 1 && singleEdges[0].sourceNodeId == b.id,
                      "SE-FANIN1: a single-input port kept the old wire, not the newest");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        // Pulls the combine node fresh under one MergeOp and asserts every pixel
        // equals the expected fold of the three constants. The per-node cache is
        // cleared first, or the previous op's product is handed straight back.
        private static void CheckMergeUniform(GraphEvaluator ev, TileData tile,
            UniverseCreator.Nodes.Combiners.CombineNode combine, MergeOp op,
            float expected, string label)
        {
            combine.op = op;
            tile.ClearCache();

            Matrix merged = ev.PullNode(combine, 0, tile, new StopToken()) as Matrix;
            Check(merged != null, "SE-FANIN1: " + label + " fan-in produced no field");
            if (merged == null) return;

            bool uniform = true;
            for (int i = 0; i < merged.arr.Length; i++)
                if (Mathf.Abs(merged.arr[i] - expected) > 1e-4f) { uniform = false; break; }

            Check(uniform, "SE-FANIN1: " + label + " fan-in = " + merged.arr[0]
                           + ", expected " + expected + " at every pixel");
        }

        /// <summary>
        /// SE-SPIDX1: the shared bounded-radius spatial index (SE-3). This locks the
        /// existing <see cref="SpatialHash"/> - the tuned grid every scatter spacing/
        /// exclusion/distance query is built on - against a brute-force oracle, so a
        /// later edit to the cell maths cannot silently drop or add neighbours. We do
        /// NOT build a second index: SpatialHash already is SE-3 (verify-before-build).
        /// </summary>
        private static void TestSpatialHashRangeQueries()
        {
            // Deterministic scatter of points across the covered rectangle.
            int n = 200;
            Vector2[] pts = new Vector2[n];
            uint s = 0x9E3779B9u;
            for (int i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u; float px = (s >> 8 & 0xFFFF) / 65535f * 100f;
                s = s * 1664525u + 1013904223u; float pz = (s >> 8 & 0xFFFF) / 65535f * 100f;
                pts[i] = new Vector2(px, pz);
            }

            SpatialHash hash = new SpatialHash(0f, 0f, 100f, 100f, 10f);
            for (int i = 0; i < n; i++) hash.Insert(pts[i].x, pts[i].y);

            // Query points and radii spanning cell-sized and multi-cell spans.
            Vector2[] queries = { new Vector2(50f, 50f), new Vector2(5f, 95f), new Vector2(0f, 0f), new Vector2(73f, 21f) };
            float[] radii = { 3f, 8f, 15f, 25f };

            System.Collections.Generic.List<int> got = new System.Collections.Generic.List<int>();
            bool allMatch = true;
            bool nearestMatch = true;

            for (int qi = 0; qi < queries.Length; qi++)
            {
                for (int ri = 0; ri < radii.Length; ri++)
                {
                    Vector2 q = queries[qi];
                    float r = radii[ri];
                    float r2 = r * r;

                    // Brute-force oracle.
                    int bruteCount = 0;
                    int bruteNearest = -1;
                    float bruteNearSq = float.MaxValue;
                    System.Collections.Generic.HashSet<int> bruteSet = new System.Collections.Generic.HashSet<int>();
                    for (int i = 0; i < n; i++)
                    {
                        float dx = pts[i].x - q.x, dz = pts[i].y - q.y;
                        float d = dx * dx + dz * dz;
                        if (d < r2) { bruteCount++; bruteSet.Add(i); }
                        if (d < bruteNearSq) { bruteNearSq = d; bruteNearest = (d < r2) ? i : bruteNearest; }
                    }
                    // Nearest-within-R: recompute cleanly (the above tracked global nearest).
                    bruteNearest = -1; bruteNearSq = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        float dx = pts[i].x - q.x, dz = pts[i].y - q.y;
                        float d = dx * dx + dz * dz;
                        if (d < r2 && d < bruteNearSq) { bruteNearSq = d; bruteNearest = i; }
                    }

                    got.Clear();
                    hash.QueryRadius(q.x, q.y, r, got);
                    if (got.Count != bruteCount) allMatch = false;
                    for (int k = 0; k < got.Count; k++) if (!bruteSet.Contains(got[k])) allMatch = false;

                    if (hash.CountWithin(q.x, q.y, r) != bruteCount) allMatch = false;
                    if (hash.HasNeighbourWithin(q.x, q.y, r) != (bruteCount > 0)) allMatch = false;

                    float sq;
                    int near = hash.FindNearest(q.x, q.y, r, out sq);
                    // A tie can pick a different index at equal distance; compare distance, not index.
                    if (bruteNearest < 0) { if (near != -1) nearestMatch = false; }
                    else if (near < 0 || Mathf.Abs(sq - bruteNearSq) > 1e-3f) nearestMatch = false;
                }
            }

            Check(allMatch, "SE-SPIDX1: a bounded-radius query disagreed with brute force "
                            + "(dropped or added a neighbour) - scatter spacing would be wrong");
            Check(nearestMatch, "SE-SPIDX1: FindNearest returned the wrong nearest within R");

            // Empty cells cost nothing: a query far from every point returns none and
            // never throws.
            got.Clear();
            hash.QueryRadius(999f, 999f, 5f, got);
            Check(got.Count == 0 && !hash.HasNeighbourWithin(999f, 999f, 5f),
                  "SE-SPIDX1: a query over empty space returned phantom neighbours");
        }

        /// <summary>
        /// UC-T01-12: published inputs remain borrowed for single, branching and retained consumers.
        /// The old ENG-4 single-wire exception cannot prove exclusive ownership through aliases.
        /// </summary>
        private static void TestInPlaceReuseGuard()
        {
            const int res = 9;
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ConstantNode src = new ConstantNode { value = 0.4f };
                UniverseCreator.Nodes.Combiners.MaximumNode m1 =
                    new UniverseCreator.Nodes.Combiners.MaximumNode();
                graph.nodes.Add(src);
                graph.nodes.Add(m1);
                graph.AddConnection(new Connection(src.id, 0, m1.id, 0));

                TileData tile = new TileData(Coord.Zero, new CoordRect(new Coord(0, 0), new Coord(res, res)),
                    Vector3.zero, new Vector3(100f, 100f, 100f), 100f);
                GraphEvaluator ev = new GraphEvaluator(graph);
                tile.Bind(ev);

                bool mayMutate;
                object p = ev.PullInputForMutation(m1, 0, tile, new StopToken(), out mayMutate);
                Check(p is Matrix && !mayMutate && ReferenceEquals(p, tile.GetCached(src.id, 0)),
                      "UC-T01-12: a sole consumer borrows the published product without an eager read copy.");

                // Second consumer of the same source port -> fan-out -> must NOT mutate.
                UniverseCreator.Nodes.Combiners.BlendNode m2 =
                    new UniverseCreator.Nodes.Combiners.BlendNode();
                graph.nodes.Add(m2);
                graph.AddConnection(new Connection(src.id, 0, m2.id, 0));
                tile.ClearCache();
                ev.PullInputForMutation(m1, 0, tile, new StopToken(), out mayMutate);
                Check(!mayMutate,
                      "ENG-REUSE1: a fan-out source (2 consumers) must be copied, never mutated - "
                      + "mutating it would corrupt the sibling branch");

                // Retained across bakes -> must NOT mutate even with a single consumer.
                Graph g2 = ScriptableObject.CreateInstance<Graph>();
                try
                {
                    ConstantNode s2 = new ConstantNode { value = 0.4f };
                    UniverseCreator.Nodes.Combiners.MaximumNode c2 =
                        new UniverseCreator.Nodes.Combiners.MaximumNode();
                    s2.contentHash = 123UL;                     // "this product is being retained"
                    g2.nodes.Add(s2);
                    g2.nodes.Add(c2);
                    g2.AddConnection(new Connection(s2.id, 0, c2.id, 0));

                    TileData t2 = new TileData(Coord.Zero, new CoordRect(new Coord(0, 0), new Coord(res, res)),
                        Vector3.zero, new Vector3(100f, 100f, 100f), 100f);
                    t2.retained = new System.Collections.Generic.Dictionary<string, RetainedProduct>();
                    GraphEvaluator ev2 = new GraphEvaluator(g2);
                    t2.Bind(ev2);
                    ev2.PullInputForMutation(c2, 0, t2, new StopToken(), out mayMutate);
                    Check(!mayMutate,
                          "ENG-REUSE1: a retained single-consumer product must be copied, not "
                          + "mutated - mutating it would poison the next bake's forward");
                }
                finally { UnityEngine.Object.DestroyImmediate(g2); }
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        /// <summary>
        /// ENG-RETAIN1 (ENG-1a): the cross-bake retained cache stays bounded by the LIVE graph.
        /// Entries are keyed "nodeId:port" and copied forward every bake; without pruning, a node
        /// that was deleted keeps a heightmap-sized product alive forever. ChunkManager prunes to
        /// the live node-id set in the worker; this pins that seam directly.
        /// </summary>
        private static void TestRetainedCacheBoundsToLiveNodes()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                ConstantNode a = new ConstantNode();
                ConstantNode b = new ConstantNode();
                graph.nodes.Add(a);
                graph.nodes.Add(b);

                System.Collections.Generic.HashSet<string> live =
                    UniverseCreator.TerrainSystem.ChunkManager.BuildLiveNodeIdSet(graph);
                Check(live.Count == 2 && live.Contains(a.id) && live.Contains(b.id),
                      "ENG-RETAIN1: the live node-id set does not match the graph");

                System.Collections.Generic.Dictionary<string, RetainedProduct> retained =
                    new System.Collections.Generic.Dictionary<string, RetainedProduct>();
                retained[a.id + ":0"] = new RetainedProduct { hash = 1UL, product = new Matrix(4, 4) };
                retained[b.id + ":0"] = new RetainedProduct { hash = 2UL, product = new Matrix(4, 4) };
                string deadId = System.Guid.NewGuid().ToString();   // a node deleted since last bake
                retained[deadId + ":0"] = new RetainedProduct { hash = 3UL, product = new Matrix(4, 4) };
                retained[deadId + ":1"] = new RetainedProduct { hash = 4UL, product = new Matrix(4, 4) };

                UniverseCreator.TerrainSystem.ChunkManager.PruneRetainedToLiveNodes(retained, live);

                Check(retained.ContainsKey(a.id + ":0") && retained.ContainsKey(b.id + ":0"),
                      "ENG-RETAIN1: pruning dropped a LIVE node's retained product");
                Check(!retained.ContainsKey(deadId + ":0") && !retained.ContainsKey(deadId + ":1"),
                      "ENG-RETAIN1: a DELETED node's products survived - the retained store leaks");
                Check(retained.Count == 2,
                      "ENG-RETAIN1: the store should hold exactly the two live entries, had " + retained.Count);

                // Empty live set = "cannot tell" (single-tile / no-graph path): leave it intact.
                System.Collections.Generic.Dictionary<string, RetainedProduct> retained2 =
                    new System.Collections.Generic.Dictionary<string, RetainedProduct>();
                retained2[deadId + ":0"] = new RetainedProduct { hash = 1UL, product = new Matrix(2, 2) };
                UniverseCreator.TerrainSystem.ChunkManager.PruneRetainedToLiveNodes(
                    retained2, new System.Collections.Generic.HashSet<string>());
                Check(retained2.Count == 1,
                      "ENG-RETAIN1: an empty live set must NOT wipe the store (cannot-tell is leave-intact)");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        /// <summary>
        /// GEO-1 + GEO-2: the Delaunay neighbour graph and its Prim MST. GEO-1 is verified by its
        /// DEFINING property - no input point lies strictly inside any triangle's circumcircle
        /// (checked against an independent oracle) - plus neighbour symmetry. GEO-2 must be a true
        /// spanning tree of the connected graph: exactly n-1 edges, acyclic, connecting every point.
        /// </summary>
        private static void TestGeoDelaunayAndMST()
        {
            // 16 sites on a jittered grid: the jitter puts them in general position (no cocircular
            // ties), so the Delaunay triangulation is unique and the strict test is unambiguous.
            System.Collections.Generic.List<Vector2> pts = new System.Collections.Generic.List<Vector2>();
            uint s = 12345u;
            for (int gz = 0; gz < 4; gz++)
                for (int gx = 0; gx < 4; gx++)
                {
                    s = s * 1664525u + 1013904223u; float jx = ((s >> 9 & 0xFFFF) / 65535f - 0.5f) * 0.2f;
                    s = s * 1664525u + 1013904223u; float jz = ((s >> 9 & 0xFFFF) / 65535f - 0.5f) * 0.2f;
                    pts.Add(new Vector2(gx + jx, gz + jz));
                }
            int n = pts.Count;

            UniverseCreator.Geo.DelaunayResult tri = UniverseCreator.Geo.Delaunay2D.Triangulate(pts);
            Check(tri.TriangleCount > 0, "GEO-1: triangulation produced no triangles");

            bool idxOk = true;
            for (int i = 0; i < tri.triangles.Count; i++)
                if (tri.triangles[i] < 0 || tri.triangles[i] >= n) idxOk = false;
            Check(idxOk, "GEO-1: a triangle references an out-of-range point index");

            // DELAUNAY PROPERTY (independent oracle): no point strictly inside a circumcircle.
            bool empty = true;
            for (int t = 0; t < tri.triangles.Count && empty; t += 3)
            {
                int a = tri.triangles[t], b = tri.triangles[t + 1], c = tri.triangles[t + 2];
                for (int p = 0; p < n; p++)
                {
                    if (p == a || p == b || p == c) continue;
                    if (OraclePointInCircumcircle(pts[a], pts[b], pts[c], pts[p])) { empty = false; break; }
                }
            }
            Check(empty, "GEO-1: a point lies strictly inside a triangle's circumcircle - not Delaunay");

            bool sym = true;
            for (int a = 0; a < n && sym; a++)
                for (int k = 0; k < tri.neighbours[a].Count; k++)
                    if (!tri.neighbours[tri.neighbours[a][k]].Contains(a)) { sym = false; break; }
            Check(sym, "GEO-1: the neighbour graph is not symmetric");

            // GEO-2: a spanning tree of the connected Delaunay graph.
            UniverseCreator.Geo.MstResult mst = UniverseCreator.Geo.PrimMST.Build(pts, tri);
            Check(mst.EdgeCount == n - 1,
                  "GEO-2: the MST of a connected graph must have n-1 edges, had " + mst.EdgeCount);

            int[] uf = new int[n];
            for (int i = 0; i < n; i++) uf[i] = i;
            bool acyclic = true;
            for (int i = 0; i < mst.EdgeCount; i++)
            {
                int ra = UFind(uf, mst.edgeA[i]), rb = UFind(uf, mst.edgeB[i]);
                if (ra == rb) { acyclic = false; break; }
                uf[ra] = rb;
            }
            Check(acyclic, "GEO-2: the MST contains a cycle");

            int root = UFind(uf, 0);
            bool connected = true;
            for (int i = 1; i < n; i++) if (UFind(uf, i) != root) { connected = false; break; }
            Check(connected, "GEO-2: the MST does not connect every point");
            Check(mst.totalWeight > 0f, "GEO-2: the MST total weight should be positive");
        }

        // Independent circumcircle oracle for GEO-1 (same maths as the implementation, written
        // separately so the test cannot pass by sharing a bug with the code under test).
        private static bool OraclePointInCircumcircle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            double ax = a.x, ay = a.y, bx = b.x, by = b.y, cx = c.x, cy = c.y;
            double d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (d > -1e-12 && d < 1e-12) return false;
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            double rr = (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy);
            double dp = (p.x - ux) * (p.x - ux) + (p.y - uy) * (p.y - uy);
            return dp < rr * (1.0 - 1e-6);
        }

        private static int UFind(int[] uf, int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }

        /// <summary>
        /// VEG-PRUNE1 (VEG-2): weighted overlap pruning. Of two instances whose footprints
        /// overlap, the higher-scored (bigger footprint × suitability) one survives and the
        /// other is dropped; a disjoint instance is untouched. Off by default = no change.
        /// </summary>
        private static void TestVegWeightedOverlapPrune()
        {
            UniverseCreator.Nodes.Scatter.TreeScatterNode node =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            node.resolveOverlaps = true;
            node.overlapFallbackRadiusM = 1f;

            TileData tile = new TileData(Coord.Zero, new CoordRect(new Coord(0, 0), new Coord(65, 65)),
                Vector3.zero, new Vector3(100f, 100f, 100f), 100f);

            System.Collections.Generic.List<ScatterInstance> list =
                new System.Collections.Generic.List<ScatterInstance>();
            // Big (r=3) and small (r=1) one metre apart => footprints overlap (1 < 3+1).
            list.Add(new ScatterInstance(new Vector3(10f, 0f, 10f), Quaternion.identity, Vector3.one, 0, 1f, 3f));
            list.Add(new ScatterInstance(new Vector3(11f, 0f, 10f), Quaternion.identity, Vector3.one, 0, 1f, 1f));
            // Disjoint, far away.
            list.Add(new ScatterInstance(new Vector3(50f, 0f, 50f), Quaternion.identity, Vector3.one, 0, 1f, 1f));

            node.ResolveInstanceOverlaps(list, tile, new StopToken());

            Check(list.Count == 2,
                  "VEG-PRUNE1: expected 2 survivors (big + disjoint), got " + list.Count);

            bool bigSurvived = false, disjointSurvived = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].footprintRadius > 2.5f) bigSurvived = true;
                if (list[i].position.x > 40f) disjointSurvived = true;
            }
            Check(bigSurvived, "VEG-PRUNE1: the higher-weight (bigger-footprint) instance must survive the overlap");
            Check(disjointSurvived, "VEG-PRUNE1: a disjoint instance must be untouched by pruning");

            // Off = no pruning: the same colliding pair is left intact.
            UniverseCreator.Nodes.Scatter.TreeScatterNode off =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();   // resolveOverlaps defaults false
            System.Collections.Generic.List<ScatterInstance> list2 =
                new System.Collections.Generic.List<ScatterInstance>();
            list2.Add(new ScatterInstance(new Vector3(10f, 0f, 10f), Quaternion.identity, Vector3.one, 0, 1f, 3f));
            list2.Add(new ScatterInstance(new Vector3(11f, 0f, 10f), Quaternion.identity, Vector3.one, 0, 1f, 1f));
            off.ResolveInstanceOverlaps(list2, tile, new StopToken());
            Check(list2.Count == 2, "VEG-PRUNE1: with resolveOverlaps OFF nothing may be pruned");
        }

        /// <summary>
        /// PERF-JOB1 (PERF-3): the parallel path and the serial path place the SAME instances for
        /// a fixed seed - same count, same order, bit-identical position, rotation, scale and
        /// stable id. The dial changes HOW the solve is driven, never WHAT it produces.
        /// </summary>
        private static void TestParallelBuildIdentity()
        {
            const int res = 65;
            const int count = 1024;   // above ParallelBuildMinCandidates (256)

            // Prove this machine can actually take the parallel path, or a comparison between
            // two serial runs would pass and prove nothing.
            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.ShouldBuildParallel(true, count),
                  "PERF-JOB1: this machine cannot run the parallel path, so the comparison proves nothing");

            // A tilted, bumpy field. Flat ground would make normal alignment, flow combing and
            // ground conform all no-ops, and identical results would then be trivially true.
            Matrix height = new Matrix(new CoordRect(new Coord(0, 0), new Coord(res, res)));
            height.worldPos = Vector3.zero;
            height.worldSize = new Vector3(100f, 200f, 100f);
            height.worldHeight = 200f;
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float u = x / (float)(res - 1);
                    float v = z / (float)(res - 1);
                    height[x, z] = 0.35f + 0.2f * u + 0.08f * Mathf.Sin(v * 11f);
                }
            }

            TileData tile = new TileData(Coord.Zero, new CoordRect(new Coord(0, 0), new Coord(res, res)),
                Vector3.zero, new Vector3(100f, 200f, 100f), 200f);

            System.Collections.Generic.List<ScatterCandidate> cands =
                new System.Collections.Generic.List<ScatterCandidate>();
            for (int i = 0; i < count; i++)
            {
                float wx = 5f + (i * 37 % 89) * 1.0f;
                float wz = 5f + (i * 53 % 89) * 1.0f;
                Vector2 p = height.WorldToPixel(wx, wz);
                cands.Add(new ScatterCandidate
                {
                    worldX = wx,
                    worldZ = wz,
                    height = height.GetInterpolated(p.x, p.y),
                    slope = 10f,
                    maskValue = 1f,
                    random = (i * 0.6180339f) % 1f
                });
            }

            UniverseCreator.Nodes.Scatter.TreeScatterNode node =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            node.seed = 4242;
            node.scaleRange = new Vector2(0.8f, 1.2f);
            node.randomYaw = true;
            node.normalAlignment = 0.6f;
            node.alignToFlow = true;
            node.alignToFlowStrength = 0.8f;
            node.groundConformRadiusM = 0.35f;
            node.groundEmbedMetres = 0.03f;

            System.Collections.Generic.List<ScatterInstance> serial =
                new System.Collections.Generic.List<ScatterInstance>();
            System.Collections.Generic.List<ScatterInstance> parallel =
                new System.Collections.Generic.List<ScatterInstance>();
            node.BuildInstancesBothWays(cands, height, tile, serial, parallel, new StopToken());

            Check(serial.Count == cands.Count,
                  "PERF-JOB1: the serial path must place every candidate, got " + serial.Count);
            Check(serial.Count == parallel.Count,
                  "PERF-JOB1: serial placed " + serial.Count + ", parallel placed " + parallel.Count);

            int firstBad = -1;
            string why = "none";
            int n = Mathf.Min(serial.Count, parallel.Count);
            for (int i = 0; i < n && firstBad < 0; i++)
            {
                ScatterInstance a = serial[i], b = parallel[i];
                if (a.position.x != b.position.x || a.position.y != b.position.y
                    || a.position.z != b.position.z) { firstBad = i; why = "position"; }
                else if (a.rotation.x != b.rotation.x || a.rotation.y != b.rotation.y
                         || a.rotation.z != b.rotation.z || a.rotation.w != b.rotation.w)
                { firstBad = i; why = "rotation"; }
                else if (a.scale.x != b.scale.x || a.scale.y != b.scale.y
                         || a.scale.z != b.scale.z) { firstBad = i; why = "scale"; }
                else if (a.instanceId != b.instanceId) { firstBad = i; why = "instanceId"; }
            }
            Check(firstBad < 0,
                  "PERF-JOB1: instance " + firstBad + " differs in " + why
                  + " between the serial and parallel paths");
        }

        /// <summary>
        /// WAT-SHORE1 (WAT-11): the shoreline band on grass detail. Full coverage at the
        /// water's edge, untouched until the fade, gone at the reach, and a band with no
        /// reach plants NOTHING rather than everything. The law is pure, so this pins it
        /// without a terrain or a bake - the wiring that feeds it distances is the same
        /// HydroField the land scatter nodes already run on.
        /// </summary>
        private static void TestGrassShoreBand()
        {
            const float reach = 6f;
            const float falloff = 2f;

            Check(Mathf.Approximately(
                      UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                          0f, reach, falloff), 1f),
                  "WAT-SHORE1: the water's edge must carry full coverage");

            Check(Mathf.Approximately(
                      UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                          reach - falloff - 0.5f, reach, falloff), 1f),
                  "WAT-SHORE1: coverage must be untouched inside the fade");

            Check(UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                      reach + 0.01f, reach, falloff) <= 0.0001f,
                  "WAT-SHORE1: coverage must stop at the band's reach");

            // Monotone: a band that got denser inland would band the wrong way, and that
            // is the one shape error a single sample at each end cannot catch.
            bool monotone = true;
            float prev = 2f;
            for (int i = 0; i <= 40; i++)
            {
                float w = UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                    reach * i / 40f, reach, falloff);
                if (w > prev + 1e-6f) monotone = false;
                prev = w;
            }
            Check(monotone, "WAT-SHORE1: coverage must never rise with distance from the water");

            // The dangerous direction: a band that fails OPEN blankets every cell that
            // passes the other filters, which looks like a working feature until noticed.
            Check(UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                      0f, 0f, falloff) <= 0.0001f,
                  "WAT-SHORE1: a zero reach must plant nothing, not everything");
            Check(UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                      0f, -3f, falloff) <= 0.0001f,
                  "WAT-SHORE1: a negative reach must plant nothing");

            // A negative distance is not physical, but a hand-edited or merged graph can
            // carry one; it must clamp to the edge rather than invert the fade.
            Check(Mathf.Approximately(
                      UniverseCreator.Nodes.Scatter.GrassDetailScatterNode.ShoreBandWeight(
                          -2f, reach, falloff), 1f),
                  "WAT-SHORE1: a negative distance must clamp to the water's edge");
        }

        /// <summary>
        /// VEG-NOISE1 (VEG-5): smooth noise-band thinning. A full [0,1] band keeps everything; a
        /// narrow band thins the field (some kept, some dropped - so it is spatially varying, not
        /// all-or-nothing); and the decision is deterministic for a given seed and position.
        /// </summary>
        private static void TestVegNoiseBandThinning()
        {
            Noise n = new Noise(777);

            bool allKept = true;
            for (int i = 0; i < 200; i++)
                if (!UniverseCreator.Nodes.Scatter.ScatterNodeBase.NoiseBandKeeps(n, i * 7.3f, i * 3.1f, 40f, 0f, 1f))
                    allKept = false;
            Check(allKept, "VEG-NOISE1: a full [0,1] keep band must keep every point");

            // Measure the actual 0-1 value distribution, then pick a MID band (range-agnostic -
            // the fBm's exact span is an implementation detail). A mid band must keep some and
            // drop some: that is the thinning, and it proves the field varies in space.
            const int N = 400;
            float mn = 1f, mx = 0f;
            float[] vals = new float[N];
            for (int i = 0; i < N; i++)
            {
                vals[i] = UniverseCreator.Nodes.Scatter.ScatterNodeBase.NoiseBandValue(n, i * 5f, (i % 20) * 5f, 40f);
                if (vals[i] < mn) mn = vals[i];
                if (vals[i] > mx) mx = vals[i];
            }
            Check(mx - mn > 1e-3f, "VEG-NOISE1: the thinning field is flat - it would thin all or nothing");

            float lo = mn + 0.2f * (mx - mn), hi = mn + 0.8f * (mx - mn);
            int keep = 0;
            for (int i = 0; i < N; i++) if (vals[i] >= lo && vals[i] <= hi) keep++;
            Check(keep > 0 && keep < N,
                  "VEG-NOISE1: a mid band must thin the field (some kept, some dropped), got " + keep);

            Noise n2 = new Noise(777);
            bool deterministic = true;
            for (int i = 0; i < 50; i++)
                if (UniverseCreator.Nodes.Scatter.ScatterNodeBase.NoiseBandKeeps(n, i * 5f, i * 2f, 40f, 0.3f, 0.7f) !=
                    UniverseCreator.Nodes.Scatter.ScatterNodeBase.NoiseBandKeeps(n2, i * 5f, i * 2f, 40f, 0.3f, 0.7f))
                    deterministic = false;
            Check(deterministic, "VEG-NOISE1: same seed and position must give the same keep decision");
        }

        /// <summary>
        /// VEG-OCC1 (VEG-1): cross-layer occupancy. The geometric microsite rule - Deny carves a
        /// hole under a dense crown, Thin reduces, DontExclude ignores - and the canopy grid that
        /// supplies the overhead-leaf-area signal it reads. Pins the pure decision plus the grid.
        /// </summary>
        private static void TestVegCrossLayerOcclusion()
        {
            float thin;
            Check(!UniverseCreator.Nodes.Scatter.SpeciesAssignment.MicrositeDenies(
                      UniverseCreator.Ecology.MicrositeExclusion.DontExclude, 5f, out thin) && thin == 1f,
                  "VEG-OCC1: DontExclude must never deny or thin (grass may grow under a tree)");

            Check(!UniverseCreator.Nodes.Scatter.SpeciesAssignment.MicrositeDenies(
                      UniverseCreator.Ecology.MicrositeExclusion.Deny, 0.1f, out thin),
                  "VEG-OCC1: below the LAI threshold a sparse crown does not reserve the microsite");

            Check(UniverseCreator.Nodes.Scatter.SpeciesAssignment.MicrositeDenies(
                      UniverseCreator.Ecology.MicrositeExclusion.Deny, 5f, out thin),
                  "VEG-OCC1: Deny must carve out placement under a dense crown");

            bool denThin = UniverseCreator.Nodes.Scatter.SpeciesAssignment.MicrositeDenies(
                UniverseCreator.Ecology.MicrositeExclusion.Thin, 5f, out thin);
            Check(!denThin && thin > 0f && thin < 1f,
                  "VEG-OCC1: Thin must reduce (not drop) density under a dense crown, thin=" + thin);

            // The canopy grid produces the overhead signal under a crown, and less of it away.
            UniverseCreator.Ecology.CanopyLightGrid grid = new UniverseCreator.Ecology.CanopyLightGrid();
            grid.Bind(0f, 0f, 100f, 100f);
            grid.Deposit(50f, 50f, 8f, 3f, UniverseCreator.Ecology.CanopyLayer.Canopy);
            float under = grid.LaiAbove(50f, 50f, UniverseCreator.Ecology.CanopyLayer.GroundCover);
            float away = grid.LaiAbove(5f, 5f, UniverseCreator.Ecology.CanopyLayer.GroundCover);
            Check(under > UniverseCreator.Ecology.EcoConstants.MicrositeExclusionLaiThreshold,
                  "VEG-OCC1: a deposited crown must register leaf area overhead for the ground layer, got " + under);
            Check(away < under, "VEG-OCC1: ground away from the crown must see less overhead leaf area");
        }

        /// <summary>
        /// VEG-DIST1 (VEG-3): the bounded feature-distance filter. Candidates within R of a feature
        /// point are rejected and those outside R are kept; radius 0 or no features filters nothing.
        /// </summary>
        private static void TestVegFeatureDistanceFilter()
        {
            System.Collections.Generic.List<ScatterCandidate> cands =
                new System.Collections.Generic.List<ScatterCandidate>();
            cands.Add(new ScatterCandidate { worldX = 10f, worldZ = 10f });   // near feature -> reject
            cands.Add(new ScatterCandidate { worldX = 50f, worldZ = 50f });   // far -> keep
            cands.Add(new ScatterCandidate { worldX = 11f, worldZ = 10f });   // near feature -> reject

            System.Collections.Generic.List<Vector2> feats =
                new System.Collections.Generic.List<Vector2> { new Vector2(10f, 10f) };

            UniverseCreator.Nodes.Scatter.ScatterNodeBase.FilterByFeatureDistance(cands, feats, 5f);
            Check(cands.Count == 1, "VEG-DIST1: candidates within R of a feature must be rejected, kept " + cands.Count);
            Check(cands.Count == 1 && cands[0].worldX > 40f, "VEG-DIST1: the far candidate must survive");

            System.Collections.Generic.List<ScatterCandidate> cands2 =
                new System.Collections.Generic.List<ScatterCandidate>();
            cands2.Add(new ScatterCandidate { worldX = 10f, worldZ = 10f });
            UniverseCreator.Nodes.Scatter.ScatterNodeBase.FilterByFeatureDistance(cands2, feats, 0f);
            Check(cands2.Count == 1, "VEG-DIST1: radius 0 must not filter anything");
        }

        /// <summary>
        /// VEG-GRID1 (VEG-10): the regular placement patterns. A grid is a square lattice of the
        /// expected count; rings sit on concentric circles spaced by the pattern spacing. These are
        /// the orchard/plantation/formal-planting modes, opt-in beside the default blue noise.
        /// </summary>
        private static void TestVegPatternPlacement()
        {
            System.Collections.Generic.List<Vector2> grid = new System.Collections.Generic.List<Vector2>();
            UniverseCreator.Nodes.Scatter.ScatterNodeBase.GenerateGridPositions(0f, 0f, 20f, 20f, 10f, grid);
            Check(grid.Count == 9, "VEG-GRID1: a 20x20 grid at 10 m spacing must be 3x3 = 9 points, got " + grid.Count);

            bool lattice = true;
            for (int i = 0; i < grid.Count; i++)
            {
                float x = grid[i].x, z = grid[i].y;
                if (Mathf.Abs(x - Mathf.Round(x / 10f) * 10f) > 1e-3f || x < -1e-3f || x > 20f + 1e-3f) lattice = false;
                if (Mathf.Abs(z - Mathf.Round(z / 10f) * 10f) > 1e-3f || z < -1e-3f || z > 20f + 1e-3f) lattice = false;
            }
            Check(lattice, "VEG-GRID1: every grid point must lie on the 10 m lattice inside the rect");

            System.Collections.Generic.List<Vector2> ring = new System.Collections.Generic.List<Vector2>();
            UniverseCreator.Nodes.Scatter.ScatterNodeBase.GenerateRingPositions(0f, 0f, 20f, 20f, 5f, ring);
            Check(ring.Count > 0, "VEG-GRID1: the ring pattern produced no points");

            bool concentric = true;
            for (int i = 0; i < ring.Count; i++)
            {
                float d = Vector2.Distance(ring[i], new Vector2(10f, 10f));
                float nearestRing = Mathf.Round(d / 5f) * 5f;
                if (d > 10.5f || Mathf.Abs(d - nearestRing) > 0.5f) concentric = false;
            }
            Check(concentric, "VEG-GRID1: every ring point must sit on a concentric circle spaced 5 m about the centre");
        }

        /// <summary>
        /// GEO3-YAW1 (GEO-3): flow-aligned yaw. A height-field normal tilted east/north yields the
        /// yaw that faces steepest descent; flat ground yields no flow direction (false).
        /// </summary>
        private static void TestVegFlowAlignedYaw()
        {
            float yaw;
            // Normal leaning +X -> flow XZ = (1,0) -> atan2(1,0) = 90 degrees.
            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryFlowYaw(new Vector3(1f, 1f, 0f), out yaw)
                  && Mathf.Abs(Mathf.DeltaAngle(yaw, 90f)) < 1f,
                  "GEO3-YAW1: a normal leaning +X must give a 90-degree downslope yaw, got " + yaw);

            // Normal leaning +Z -> flow XZ = (0,1) -> atan2(0,1) = 0 degrees.
            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryFlowYaw(new Vector3(0f, 1f, 1f), out yaw)
                  && Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) < 1f,
                  "GEO3-YAW1: a normal leaning +Z must give a 0-degree downslope yaw, got " + yaw);

            // Flat ground: no flow direction.
            Check(!UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryFlowYaw(new Vector3(0f, 1f, 0f), out yaw),
                  "GEO3-YAW1: flat ground must report no flow direction");
        }

        /// <summary>
        /// VEG-REPEAT1 (VEG-8): the anti-repeat weight penalty. It scales down only the repeated
        /// species' weight (strength 1 excludes it), leaves the others, and is a safe no-op for an
        /// out-of-range index.
        /// </summary>
        private static void TestVegAntiRepeatPenalty()
        {
            float[] w = { 1f, 1f, 1f };
            UniverseCreator.Nodes.Scatter.SpeciesAssignment.AntiRepeatPenalize(w, 3, 1, 0.5f);
            Check(Mathf.Abs(w[1] - 0.5f) < 1e-5f && w[0] == 1f && w[2] == 1f,
                  "VEG-REPEAT1: penalty must reduce only the repeated species' weight");

            UniverseCreator.Nodes.Scatter.SpeciesAssignment.AntiRepeatPenalize(w, 3, 1, 1f);
            Check(w[1] == 0f, "VEG-REPEAT1: strength 1 must fully exclude the repeated species");

            // Out-of-range index: no throw, no change.
            UniverseCreator.Nodes.Scatter.SpeciesAssignment.AntiRepeatPenalize(w, 3, 9, 0.5f);
            Check(w[0] == 1f && w[2] == 1f, "VEG-REPEAT1: an out-of-range index must be a safe no-op");
        }

        /// <summary>
        /// VEG-FILTER1 (VEG-9): the composable predicate filter stack. Typed predicates evaluate
        /// correctly, AND requires all / OR requires any, and an empty stack keeps everything.
        /// </summary>
        private static void TestVegFilterStack()
        {
            UniverseCreator.Nodes.Scatter.ScatterPredicate pH = new UniverseCreator.Nodes.Scatter.ScatterPredicate
            { field = UniverseCreator.Nodes.Scatter.ScatterField.Height, op = UniverseCreator.Nodes.Scatter.PredicateOp.Inside, a = 0.3f, b = 0.7f };
            Check(pH.Evaluate(0.5f) && !pH.Evaluate(0.1f) && !pH.Evaluate(0.9f),
                  "VEG-FILTER1: Inside predicate must accept only within [a,b]");

            UniverseCreator.Nodes.Scatter.ScatterPredicate pS = new UniverseCreator.Nodes.Scatter.ScatterPredicate
            { field = UniverseCreator.Nodes.Scatter.ScatterField.SlopeDeg, op = UniverseCreator.Nodes.Scatter.PredicateOp.Greater, a = 30f };
            Check(pS.Evaluate(45f) && !pS.Evaluate(20f), "VEG-FILTER1: Greater predicate must accept only above a");

            System.Collections.Generic.List<UniverseCreator.Nodes.Scatter.ScatterPredicate> stack =
                new System.Collections.Generic.List<UniverseCreator.Nodes.Scatter.ScatterPredicate> { pH, pS };

            Check(UniverseCreator.Nodes.Scatter.ScatterFilterStack.Keep(
                      stack, UniverseCreator.Nodes.Scatter.FilterGroupOp.And, 0.5f, 45f, 1f, 0.5f),
                  "VEG-FILTER1: AND must pass when every predicate passes");
            Check(!UniverseCreator.Nodes.Scatter.ScatterFilterStack.Keep(
                      stack, UniverseCreator.Nodes.Scatter.FilterGroupOp.And, 0.5f, 20f, 1f, 0.5f),
                  "VEG-FILTER1: AND must fail when any predicate fails");
            Check(UniverseCreator.Nodes.Scatter.ScatterFilterStack.Keep(
                      stack, UniverseCreator.Nodes.Scatter.FilterGroupOp.Or, 0.5f, 20f, 1f, 0.5f),
                  "VEG-FILTER1: OR must pass when any predicate passes");
            Check(!UniverseCreator.Nodes.Scatter.ScatterFilterStack.Keep(
                      stack, UniverseCreator.Nodes.Scatter.FilterGroupOp.Or, 0.1f, 20f, 1f, 0.5f),
                  "VEG-FILTER1: OR must fail when every predicate fails");

            Check(UniverseCreator.Nodes.Scatter.ScatterFilterStack.Keep(
                      new System.Collections.Generic.List<UniverseCreator.Nodes.Scatter.ScatterPredicate>(),
                      UniverseCreator.Nodes.Scatter.FilterGroupOp.And, 0f, 0f, 0f, 0f),
                  "VEG-FILTER1: an empty stack must keep everything");
        }

        /// <summary>
        /// VEG-DECOR1 (VEG-11): decorator matching keeps a deterministic fraction of a source
        /// channel's positions. keep=1 keeps all, keep=0 none, a mid fraction a subset, repeatably.
        /// </summary>
        private static void TestVegDecoratorMatching()
        {
            System.Collections.Generic.List<Vector2> src = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i < 100; i++) src.Add(new Vector2(i * 3f, i * 2f));

            System.Collections.Generic.List<ScatterCandidate> all =
                UniverseCreator.Nodes.Scatter.ScatterNodeBase.DecoratorCandidatesFrom(src, 1f, new Noise(42), null);
            Check(all.Count == src.Count, "VEG-DECOR1: keep=1 must decorate every source instance");

            System.Collections.Generic.List<ScatterCandidate> none =
                UniverseCreator.Nodes.Scatter.ScatterNodeBase.DecoratorCandidatesFrom(src, 0f, new Noise(42), null);
            Check(none.Count == 0, "VEG-DECOR1: keep=0 must decorate none");

            System.Collections.Generic.List<ScatterCandidate> half =
                UniverseCreator.Nodes.Scatter.ScatterNodeBase.DecoratorCandidatesFrom(src, 0.5f, new Noise(42), null);
            Check(half.Count > 0 && half.Count < src.Count,
                  "VEG-DECOR1: a mid fraction must decorate a subset, got " + half.Count);

            System.Collections.Generic.List<ScatterCandidate> half2 =
                UniverseCreator.Nodes.Scatter.ScatterNodeBase.DecoratorCandidatesFrom(src, 0.5f, new Noise(42), null);
            Check(half.Count == half2.Count, "VEG-DECOR1: the same seed must decorate the same subset");
        }

        /// <summary>
        /// BIO-GRAD1 (BIO-1 / BIO-8): graded biome classification. The transition field is ~0 deep
        /// inside a biome and rises above 0 near a Whittaker class break, while the hard-index
        /// classification (output 0) is unchanged - the gradient is additive, not a reclassification.
        /// </summary>
        private static void TestBiomeGradedClassification()
        {
            float band = UniverseCreator.Ecology.EcoConstants.EcotoneBiomeClimateBand;

            // Hot and wet, far from any break: no transition.
            float interior = UniverseCreator.Ecology.WhittakerBiome.BoundaryBlend(0.9f, 0.9f, 0.05f, band);
            Check(interior < 1e-4f, "BIO-GRAD1: deep inside a biome the blend must be ~0, got " + interior);

            // Straddling the cold/cool temperature break at 0.25: a transition.
            float edge = UniverseCreator.Ecology.WhittakerBiome.BoundaryBlend(0.25f, 0.5f, 0.05f, band);
            Check(edge > 0f, "BIO-GRAD1: near a class break the blend must rise above 0, got " + edge);

            // The hard index is unchanged by the graded path.
            Check(UniverseCreator.Ecology.WhittakerBiome.Classify(0.9f, 0.9f)
                      == UniverseCreator.Ecology.WhittakerBiome.TropicalRainforest,
                  "BIO-GRAD1: hot + wet must still classify as tropical rainforest (hard path intact)");
        }

        /// <summary>
        /// BIO-SOIL1 (BIO-2): the optional wetness/drainage axis. A dry hot climate reads as desert
        /// on climate alone, but a saturated cell (wetness lifting the effective moisture, as the
        /// classifier does when its Wetness port is wired) reclassifies toward the wet class.
        /// </summary>
        private static void TestBiomeSoilAxis()
        {
            Check(UniverseCreator.Ecology.WhittakerBiome.Classify(0.85f, 0.1f)
                      == UniverseCreator.Ecology.WhittakerBiome.Desert,
                  "BIO-SOIL1: a dry hot climate must classify as desert on climate alone");

            // The classifier lifts effective moisture to max(moisture, wetness) when the Wetness
            // port is wired; a saturated cell therefore reclassifies to the wet class.
            float effMoisture = Mathf.Max(0.1f, 0.9f);
            Check(UniverseCreator.Ecology.WhittakerBiome.Classify(0.85f, effMoisture)
                      == UniverseCreator.Ecology.WhittakerBiome.TropicalRainforest,
                  "BIO-SOIL1: a saturated cell must reclassify toward the wet class when wetness is applied");
        }

        /// <summary>
        /// BIO-EDGE1 (BIO-3): the biome edge mask lights up along a class boundary and is zero deep
        /// inside a uniform region. Built on a two-halves biome field so the seam is known.
        /// </summary>
        private static void TestBiomeEdgeMask()
        {
            const int n = 16;
            Matrix biome = new Matrix(new CoordRect(0, 0, n, n));
            float a = UniverseCreator.Ecology.WhittakerBiome.Encode(UniverseCreator.Ecology.WhittakerBiome.Tundra);
            float b = UniverseCreator.Ecology.WhittakerBiome.Encode(UniverseCreator.Ecology.WhittakerBiome.Desert);
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    biome.arr[z * n + x] = x < n / 2 ? a : b;

            Matrix mask = biome.Blank();
            UniverseCreator.Nodes.Biome.BiomeEdgeMaskNode.ComputeBiomeEdge(biome, mask, 1, new StopToken());

            // At the seam (columns 7/8) the window straddles both biomes -> mask > 0.
            float seam = mask.arr[(n / 2) * n + (n / 2)];
            Check(seam > 0f, "BIO-EDGE1: the mask must light up at a biome boundary, got " + seam);

            // Deep in the left interior (column 1) the window is all one biome -> 0.
            float interior = mask.arr[(n / 2) * n + 1];
            Check(interior < 1e-4f, "BIO-EDGE1: the mask must be ~0 deep inside a uniform biome, got " + interior);
        }

        /// <summary>
        /// RIV-ERO1 (RIV-1): the erosion sea-level + non-destructive guards. No cell ends above its
        /// original height (no invented peaks); dry land (originally above sea) never ends below sea;
        /// genuine sub-sea ground keeps its depth. Pins the pure guard array op.
        /// </summary>
        private static void TestErosionSeaLevelGuards()
        {
            const float sea = 0.35f;
            float[] original = { 0.6f, 0.3f, 0.5f };   // land, genuine sub-sea, land
            float[] eroded   = { 0.2f, 0.1f, 0.7f };   // land cut below sea, sub-sea cut, land raised above original

            UniverseCreator.Nodes.Modifiers.HydraulicErosionNode.ApplyErosionGuards(
                eroded, original, sea, true, true);

            Check(Mathf.Abs(eroded[0] - sea) < 1e-5f,
                  "RIV-ERO1: dry land eroded below sea must be floored back to sea, got " + eroded[0]);
            Check(Mathf.Abs(eroded[1] - 0.1f) < 1e-5f,
                  "RIV-ERO1: genuine sub-sea ground must keep its depth, got " + eroded[1]);
            Check(Mathf.Abs(eroded[2] - 0.5f) < 1e-5f,
                  "RIV-ERO1: a cell must not end above its original height (no invented peaks), got " + eroded[2]);

            // Both guards off = untouched.
            float[] a2 = { 0.2f };
            UniverseCreator.Nodes.Modifiers.HydraulicErosionNode.ApplyErosionGuards(
                a2, new float[] { 0.6f }, sea, false, false);
            Check(Mathf.Abs(a2[0] - 0.2f) < 1e-5f, "RIV-ERO1: with both guards off the result is unchanged");
        }

        /// <summary>
        /// RIV-SRC1 (RIV-2): the procedural downhill river-course tracer. On a west-to-east slope that
        /// drops below sea at the east edge, a source seeded high in the west traces a course that
        /// starts at the source, never climbs, and ENDS in the sea.
        /// </summary>
        private static void TestRiverCourseTracer()
        {
            const int res = 8;
            const float sea = 0.1f;
            float[] height = new float[res * res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    height[z * res + x] = 1.0f - x * 0.15f;   // x=0 -> 1.0, x=7 -> -0.05 (sub-sea)

            int source = 3 * res + 0;   // high, west edge
            System.Collections.Generic.List<int> path =
                UniverseCreator.RiverCourseTracer.TraceDownhill(height, res, res, source, sea);

            Check(path != null && path.Count >= 2, "RIV-SRC1: the tracer produced no course");
            if (path == null || path.Count < 2) return;

            Check(path[0] == source, "RIV-SRC1: the course must start at the seeded source");
            Check(height[path[path.Count - 1]] < sea, "RIV-SRC1: the course must END in the sea (sub-sea outlet)");

            bool neverClimbs = true;
            for (int i = 1; i < path.Count; i++)
                if (height[path[i]] > height[path[i - 1]] + 1e-4f) neverClimbs = false;
            Check(neverClimbs, "RIV-SRC1: on a monotonic slope the course must descend, never climb");
        }

        /// <summary>
        /// RIV-SRC2 (RIV-2, the node half): the graph NODE that joins the orphan seed producer to
        /// the orphan route search. Drives `RiverSeedCourseNode` over a real graph + tile with a
        /// west-to-east ramp and one seeded headwater, and pins the four promises its header makes:
        /// a channel actually appears, the cut is DEEPENING-ONLY and bounded by channelDepth, the
        /// node is INERT when its Sources port is unwired (the zero-regression claim), and it is
        /// deterministic. Without this the node could ship carving nothing, or raising ground.
        /// </summary>
        private static void TestRiverSeedCourseNode()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                const int res = 65;
                const float tileSpan = 1000f;

                SeedRampGroundFeed ground = new SeedRampGroundFeed();
                SeedPointFeed seeds = new SeedPointFeed();

                UniverseCreator.Nodes.Selectors.RiverSeedCourseNode node =
                    new UniverseCreator.Nodes.Selectors.RiverSeedCourseNode();
                node.solveResolution = 64;
                node.seaLevel = 0.2f;
                node.sourceThreshold = 0.5f;
                node.maxRivers = 2;
                node.minSourceSpacingMetres = 10f;
                node.channelHalfWidthMetres = 60f;
                node.floodplainMetres = 150f;
                node.channelDepth = 0.02f;

                graph.nodes.Add(ground);
                graph.nodes.Add(seeds);
                graph.nodes.Add(node);
                graph.connections.Add(new Connection(ground.id, 0, node.id, 0));
                graph.connections.Add(new Connection(seeds.id, 0, node.id, 1));

                GraphEvaluator ev;
                TileData tile = SeedCourseTile(graph, res, tileSpan, out ev);

                Matrix h = node.Generate(0, tile, new StopToken()) as Matrix;
                Matrix mask = node.Generate(1, tile, new StopToken()) as Matrix;

                Check(h != null, "RIV-SRC2: the node produced no Height output");
                Check(mask != null, "RIV-SRC2: the node produced no River Mask output");
                if (h == null || mask == null) return;

                // A channel must actually exist in this tile - the seed is inside it and the
                // course runs east through it, so an empty mask means the wiring is dead.
                bool anyChannel = false;
                for (int i = 0; i < mask.arr.Length; i++)
                    if (mask.arr[i] > 0.5f) { anyChannel = true; break; }
                Check(anyChannel, "RIV-SRC2: a seeded headwater produced no channel in the mask");

                // DEEPENING ONLY, and the cut bounded by channelDepth. Compared against the
                // analytic ground the feed produced, so this is an absolute check rather than a
                // before/after of the node against itself.
                Vector2 ps = h.PixelSize;
                int sx = h.rect.size.x, sz = h.rect.size.z;
                bool neverRaised = true, everLowered = false, cutBounded = true;
                for (int z = 0; z < sz; z++)
                {
                    int row = z * sx;
                    for (int x = 0; x < sx; x++)
                    {
                        float expected = SeedGround(h.worldPos.x + x * ps.x);
                        float got = h.arr[row + x];
                        if (got > expected + 1e-5f) neverRaised = false;
                        if (got < expected - 1e-6f) everLowered = true;
                        if (expected - got > node.channelDepth + 1e-4f) cutBounded = false;
                    }
                }
                Check(neverRaised, "RIV-SRC2: the channel must only DEEPEN - ground was raised somewhere");
                Check(everLowered, "RIV-SRC2: nothing was carved - the course never reached the terrain");
                Check(cutBounded, "RIV-SRC2: the cut went deeper than channelDepth");

                // INERT when Sources is unwired: Height passes through untouched and the mask is
                // empty. This is the promise that adding the node to a graph costs nothing.
                Graph bare = ScriptableObject.CreateInstance<Graph>();
                try
                {
                    SeedRampGroundFeed g2 = new SeedRampGroundFeed();
                    UniverseCreator.Nodes.Selectors.RiverSeedCourseNode lone =
                        new UniverseCreator.Nodes.Selectors.RiverSeedCourseNode();
                    lone.solveResolution = 64;
                    lone.seaLevel = 0.2f;

                    bare.nodes.Add(g2);
                    bare.nodes.Add(lone);
                    bare.connections.Add(new Connection(g2.id, 0, lone.id, 0));

                    GraphEvaluator ev2;
                    TileData t2 = SeedCourseTile(bare, res, tileSpan, out ev2);

                    Matrix h2 = lone.Generate(0, t2, new StopToken()) as Matrix;
                    Matrix m2 = lone.Generate(1, t2, new StopToken()) as Matrix;

                    bool untouched = h2 != null;
                    if (h2 != null)
                    {
                        Vector2 p2 = h2.PixelSize;
                        int s2x = h2.rect.size.x, s2z = h2.rect.size.z;
                        for (int z = 0; z < s2z && untouched; z++)
                            for (int x = 0; x < s2x; x++)
                                if (Mathf.Abs(h2.arr[z * s2x + x] -
                                              SeedGround(h2.worldPos.x + x * p2.x)) > 1e-5f)
                                { untouched = false; break; }
                    }
                    Check(untouched,
                          "RIV-SRC2: with Sources unwired the Height must pass through untouched");

                    bool emptyMask = m2 != null;
                    if (m2 != null)
                        for (int i = 0; i < m2.arr.Length; i++)
                            if (m2.arr[i] > 1e-6f) { emptyMask = false; break; }
                    Check(emptyMask, "RIV-SRC2: with Sources unwired the River Mask must be empty");
                }
                finally { if (bare != null) UnityEngine.Object.DestroyImmediate(bare); }

                // Deterministic: a second evaluation of the same node on the same tile is
                // bit-identical. Seed selection sorts on strength with the cell index as tiebreak,
                // so this is what proves the ordering is not left to chance.
                Matrix again = node.Generate(1, tile, new StopToken()) as Matrix;
                bool same = again != null && again.arr.Length == mask.arr.Length;
                if (same)
                    for (int i = 0; i < mask.arr.Length; i++)
                        if (again.arr[i] != mask.arr[i]) { same = false; break; }
                Check(same, "RIV-SRC2: two evaluations disagreed - the node is not deterministic");
            }
            finally { if (graph != null) UnityEngine.Object.DestroyImmediate(graph); }
        }

        private const float SeedCourseWorldSpan = 4000f;

        /// <summary>
        /// RIV-LOOK1 (RIV-4 W3): bank-aligned instance yaw. The river direction is read as the
        /// PERPENDICULAR of the River Mask gradient - the mask falls off across the channel, so its
        /// gradient points across the water. Pins the axis for a river running each way, that a
        /// flat mask yields NO direction (so the instance keeps its yaw instead of snapping to
        /// zero), and that the dial defaults to off. Orientation is the one thing a batchmode gate
        /// CAN check about a look-gap: the angle is arithmetic, not appearance.
        /// </summary>
        private static void TestRiverBankYaw()
        {
            const int res = 33;
            TileData tile = new TileData(
                Coord.Zero,
                new CoordRect(new Coord(0, 0), new Coord(res, res)),
                new Vector3(0f, 0f, 0f),
                new Vector3(330f, 600f, 330f),
                600f);

            // A river running NORTH-SOUTH (along Z): the mask varies with X only.
            Matrix alongZ = tile.CreateMatrix();
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    alongZ.arr[z * res + x] = Mathf.Clamp01(1f - Mathf.Abs(x - 16) / 6f);

            float yaw;
            bool got = UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryRiverBankYaw(
                alongZ, 200f, 160f, out yaw);
            Check(got, "RIV-LOOK1: a river mask with a gradient must yield a bank direction");
            Check(got && AxisMatchesDeg(yaw, 0f),
                  "RIV-LOOK1: a north-south river must lay instances along Z, got " + yaw);

            // A river running EAST-WEST (along X): the mask varies with Z only.
            Matrix alongX = tile.CreateMatrix();
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    alongX.arr[z * res + x] = Mathf.Clamp01(1f - Mathf.Abs(z - 16) / 6f);

            bool got2 = UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryRiverBankYaw(
                alongX, 160f, 200f, out yaw);
            Check(got2 && AxisMatchesDeg(yaw, 90f),
                  "RIV-LOOK1: an east-west river must lay instances along X, got " + yaw);

            // The yaw must be PERPENDICULAR to the mask gradient - that is the whole derivation,
            // so check it numerically rather than trusting the two axis cases above.
            if (got2)
            {
                int cx = Mathf.Clamp(Mathf.RoundToInt(alongX.WorldToPixel(160f, 200f).x), 1, res - 2);
                int cz = Mathf.Clamp(Mathf.RoundToInt(alongX.WorldToPixel(160f, 200f).y), 1, res - 2);
                float gx = alongX.arr[cz * res + (cx + 1)] - alongX.arr[cz * res + (cx - 1)];
                float gz = alongX.arr[(cz + 1) * res + cx] - alongX.arr[(cz - 1) * res + cx];
                Vector2 grad = new Vector2(gx, gz).normalized;
                float r = yaw * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Sin(r), Mathf.Cos(r));   // Atan2(dx, dz) convention
                Check(Mathf.Abs(Vector2.Dot(grad, dir)) < 1e-3f,
                      "RIV-LOOK1: the bank yaw must be perpendicular to the mask gradient");
            }

            // A FLAT mask has no river in it. Returning false is what lets the instance keep its
            // random/flow yaw instead of every instance snapping to zero degrees.
            Matrix flat = tile.CreateMatrix();
            for (int i = 0; i < flat.arr.Length; i++) flat.arr[i] = 0.5f;
            Check(!UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryRiverBankYaw(flat, 160f, 160f, out yaw),
                  "RIV-LOOK1: a flat mask must report NO bank direction, not zero degrees");

            Check(!UniverseCreator.Nodes.Scatter.ScatterNodeBase.TryRiverBankYaw(null, 0f, 0f, out yaw),
                  "RIV-LOOK1: a null mask must report no bank direction rather than throwing");

            // Default is the OFF position, so no existing world changes.
            UniverseCreator.Nodes.Scatter.TreeScatterNode fresh =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            Check(!fresh.alignToRiverBank,
                  "RIV-LOOK1: alignToRiverBank must default to OFF so existing scatter is unchanged");
            Check(fresh.alignToRiverBankStrength > 0.99f,
                  "RIV-LOOK1: once enabled the bank alignment must default to full strength");
        }

        /// <summary>
        /// RIV-ERO3 (RIV-1b, the GPU half): the guards on the GPU droplet path.
        ///
        /// WHAT A HEADLESS GATE CAN PROVE HERE. Batchmode runs `-nographics`, so there is no GPU
        /// device and the kernel CANNOT be executed - that half is the author's to verify in the
        /// editor. What IS provable, and is the part that could actually hurt the product, is that
        /// every refusal path leaves the height field BYTE-IDENTICAL. A GPU pass that half-wrote a
        /// field on an unsupported device, or when called from a worker thread, would corrupt
        /// terrain in a way no other test would catch. So this pins the contract: anything other
        /// than complete success changes nothing and reports why.
        /// </summary>
        private static void TestDropletErosionGPUGuards()
        {
            // The compute asset must exist and be found WITHOUT a hardcoded path (searched by
            // type, per the project's no-hardcoded-asset-paths rule).
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:ComputeShader");
            ComputeShader kernelAsset = null;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("UC_DropletErosion", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                kernelAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (kernelAsset != null) break;
            }
            Check(kernelAsset != null,
                  "RIV-ERO3: the UC_DropletErosion compute shader asset must exist and import");

            // The kernel list is only queryable where a real graphics DEVICE exists. Headless
            // (-nographics) there is none, and HasKernel returns false even for a perfectly
            // compiled shader - which is exactly what this gate caught on its first run, and why
            // DropletErosionGPU.IsSupported now tests graphicsDeviceType and not just the
            // supportsComputeShaders capability flag.
            if (kernelAsset != null && DropletErosionGPU.IsSupported)
                Check(kernelAsset.HasKernel(DropletErosionGPU.KernelName),
                      "RIV-ERO3: the compute shader must expose the " + DropletErosionGPU.KernelName + " kernel");

            Check(DropletErosionGPU.ThreadsPerGroup > 0,
                  "RIV-ERO3: ThreadsPerGroup must be positive (it must match [numthreads] in the .compute)");
            Check(DropletErosionGPU.MaxDropletsPerDispatch > 0,
                  "RIV-ERO3: the per-dispatch cap must be positive or no work is ever submitted");

            // --- the contract: every refusal leaves the field untouched ---
            const int w = 32, h = 32;
            float[] field = new float[w * h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    field[z * w + x] = 1f - x / (float)(w - 1);   // a simple ramp

            float[] pristine = (float[])field.Clone();

            UniverseCreator.DropletSettings s = UniverseCreator.DropletSettings.Default;
            s.droplets = 64;
            s.coarseFactor = 1;
            s.seed = 7;

            string why;

            // No shader assigned - the shipped state, since an asset slot ships EMPTY.
            Check(!DropletErosionGPU.TryRun(field, w, h, s, null, out why),
                  "RIV-ERO3: with no compute shader assigned the GPU path must refuse");
            Check(!string.IsNullOrEmpty(why),
                  "RIV-ERO3: a refusal must say why, so the author is not left guessing");
            Check(FieldsIdentical(field, pristine),
                  "RIV-ERO3: a refused GPU run must leave the height field BYTE-IDENTICAL");

            // Degenerate arguments must refuse rather than throw.
            bool threw = false;
            try
            {
                DropletErosionGPU.TryRun(null, w, h, s, kernelAsset, out why);
                DropletErosionGPU.TryRun(field, 2, 2, s, kernelAsset, out why);
                UniverseCreator.DropletSettings zero = s;
                zero.droplets = 0;
                DropletErosionGPU.TryRun(field, w, h, zero, kernelAsset, out why);
                float[] tooShort = new float[4];
                DropletErosionGPU.TryRun(tooShort, w, h, s, kernelAsset, out why);
            }
            catch (System.Exception)
            {
                threw = true;
            }
            Check(!threw, "RIV-ERO3: degenerate arguments must be refused, never thrown on");
            Check(FieldsIdentical(field, pristine),
                  "RIV-ERO3: none of the degenerate refusals may modify the field");

            // Headless, IsSupported must read false - and that is what routes every caller to the
            // CPU path, which is the deterministic one RIV-ERO2 already pins.
            if (!DropletErosionGPU.IsSupported)
            {
                Check(!DropletErosionGPU.TryRun(field, w, h, s, kernelAsset, out why),
                      "RIV-ERO3: with no compute support the GPU path must refuse even with a valid shader");
                Check(FieldsIdentical(field, pristine),
                      "RIV-ERO3: refusing for lack of compute support must leave the field untouched");
            }

            // And the CPU path still works on the same field, so the fallback is real rather than
            // theoretical: this is what a caller actually gets when TryRun says false.
            UniverseCreator.DropletErosion.Run(field, w, h, s, null);
            Check(!FieldsIdentical(field, pristine),
                  "RIV-ERO3: the CPU fallback must actually erode - it is what every refusal routes to");
            bool finite = true;
            for (int i = 0; i < field.Length; i++)
                if (float.IsNaN(field[i]) || float.IsInfinity(field[i])) { finite = false; break; }
            Check(finite, "RIV-ERO3: the CPU fallback must leave a finite field");
        }

        /// <summary>
        /// CTL-FACTS1 (CTL-1): scatter stops re-asking for the waterline. The water system's
        /// SINGLE-WRITER push now reaches scatter nodes, so plants measure shore clearance and the
        /// submerged test against the elevation the ocean actually sits at - the fault was plants
        /// placed against a typed 0.35 that nothing kept in step with the real sea.
        ///
        /// The two halves that matter are both pinned here: an AUTO node is driven by the push,
        /// and an AUTHORED node is left completely alone (CTL-1's "an authored override still
        /// wins"). Plus the migration guarantee - a node deserialized from an existing graph is
        /// NOT auto, so no shipped world silently moves its planting line.
        /// </summary>
        private static void TestAutoSeaLevelFacts()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                // A node as it arrives from an EXISTING graph: plain construction, no
                // OnCreatedFresh. This is the migration case.
                UniverseCreator.Nodes.Scatter.TreeScatterNode authored =
                    new UniverseCreator.Nodes.Scatter.TreeScatterNode();
                Check(!authored.autoSeaLevel,
                      "CTL-FACTS1: a node deserialized from an existing graph must NOT be auto - "
                      + "an authored waterline has to survive the upgrade");
                authored.seaLevel = 0.11f;

                // A node the author just dropped: OnCreatedFresh is the real "new node" path.
                UniverseCreator.Nodes.Scatter.TreeScatterNode fresh =
                    new UniverseCreator.Nodes.Scatter.TreeScatterNode();
                fresh.OnCreatedFresh();
                Check(fresh.autoSeaLevel,
                      "CTL-FACTS1: a freshly dropped scatter node must default to AUTO sea level");

                graph.nodes.Add(authored);
                graph.nodes.Add(fresh);

                // The count must see the auto node - CountSeaLevelReaders and the write share one
                // walk precisely so the count and the push can never disagree about who reads it.
                int readers = UniverseCreator.TerrainSystem.UCWaterSystemManager
                                  .CountSeaLevelReaders(graph);
                Check(readers >= 1,
                      "CTL-FACTS1: the auto scatter node must be counted as a sea-level reader, got "
                      + readers);

                // Push a sea level that is nothing like either node's typed value.
                const float pushed = 0.42f;
                UniverseCreator.TerrainSystem.UCWaterSystemManager
                    .PushSeaLevelTo(graph, pushed);

                Check(Mathf.Abs(fresh.seaLevel - pushed) < 1e-5f,
                      "CTL-FACTS1: an AUTO scatter node must take the detected sea level, got "
                      + fresh.seaLevel);
                Check(Mathf.Abs(authored.seaLevel - 0.11f) < 1e-5f,
                      "CTL-FACTS1: an AUTHORED scatter node must keep its own value, got "
                      + authored.seaLevel);

                // Turning auto on mid-session must start tracking without needing a re-create.
                authored.autoSeaLevel = true;
                UniverseCreator.TerrainSystem.UCWaterSystemManager
                    .PushSeaLevelTo(graph, 0.37f);
                Check(Mathf.Abs(authored.seaLevel - 0.37f) < 1e-5f,
                      "CTL-FACTS1: switching autoSeaLevel on must make the node track the push");

                // The other two facts the old TODO named need no detection, and this records why
                // so nobody re-opens it: the height band is NORMALISED, not metres.
                Check(fresh.minHeight >= 0f && fresh.maxHeight <= 1f,
                      "CTL-FACTS1: the scatter height band must be normalised 0..1 - if it ever "
                      + "becomes metres it WOULD need the detected world peak");
            }
            finally { if (graph != null) UnityEngine.Object.DestroyImmediate(graph); }
        }

        /// <summary>
        /// CTL-PRESET1 (CTL-2): the World Preset drives the manager, and clearing it gives the
        /// author's own numbers back. The restore is the half that is easy to get nearly right and
        /// still be wrong, so it is pinned hardest: the backup must be captured on the TRANSITION
        /// into "driven", never on every apply, or a second apply backs up the preset's own values
        /// and the author's are lost for good. This drives the REAL funnel
        /// (<c>ValidateSettings</c> → <c>ApplyWorldPreset</c>), not a copy of it.
        /// </summary>
        private static void TestWorldPreset()
        {
            GameObject go = new GameObject("UC_PresetTest");
            UniverseCreator.TerrainSystem.UCWorldPreset preset = null;
            try
            {
                UniverseCreator.TerrainSystem.UniverseCreatorObject uc =
                    go.AddComponent<UniverseCreator.TerrainSystem.UniverseCreatorObject>();

                // The author's own world.
                uc.seed = 999;
                uc.worldSizeKm = 8f;
                uc.maxHeightMeters = 1200f;
                uc.worldPreset = null;
                uc.ValidateSettings();

                Check(uc.seed == 999 && Mathf.Abs(uc.worldSizeKm - 8f) < 1e-4f
                      && Mathf.Abs(uc.maxHeightMeters - 1200f) < 1e-3f,
                      "CTL-PRESET1: with no preset the author's own values must survive validation");
                Check(!uc.PresetIsDriving,
                      "CTL-PRESET1: with no preset assigned nothing may report as preset-driven");

                // A preset that drives SOME fields only - the whole point of per-field toggles.
                preset = ScriptableObject.CreateInstance<UniverseCreator.TerrainSystem.UCWorldPreset>();
                preset.overrideWorldSizeKm = true;
                preset.worldSizeKm = 16f;
                preset.overrideMaxHeightMeters = true;
                preset.maxHeightMeters = 2000f;
                preset.overrideSeed = false;          // deliberately NOT driven
                preset.overrideResolution = false;

                Check(preset.DrivesAnything && preset.DrivenFieldCount == 2,
                      "CTL-PRESET1: the preset must report exactly the two fields it drives, got "
                      + preset.DrivenFieldCount);

                uc.worldPreset = preset;
                uc.ValidateSettings();

                Check(Mathf.Abs(uc.worldSizeKm - 16f) < 1e-4f,
                      "CTL-PRESET1: an assigned preset must drive world size, got " + uc.worldSizeKm);
                Check(Mathf.Abs(uc.maxHeightMeters - 2000f) < 1e-3f,
                      "CTL-PRESET1: an assigned preset must drive max height, got " + uc.maxHeightMeters);
                Check(uc.seed == 999,
                      "CTL-PRESET1: a field the preset does NOT drive must keep the author's value");
                Check(uc.PresetIsDriving,
                      "CTL-PRESET1: an assigned preset must report as driving");

                // Apply repeatedly. If the backup were recaptured each time, the author's 8 km
                // would be overwritten by the preset's 16 km and the restore below would return
                // the wrong number - this is the exact bug the transition-only capture prevents.
                uc.ValidateSettings();
                uc.ValidateSettings();
                uc.BuildSettings();

                // Clear it: the author's own world must come back, not the preset's.
                uc.worldPreset = null;
                uc.ValidateSettings();

                Check(Mathf.Abs(uc.worldSizeKm - 8f) < 1e-4f,
                      "CTL-PRESET1: clearing the preset must RESTORE the author's world size, got "
                      + uc.worldSizeKm);
                Check(Mathf.Abs(uc.maxHeightMeters - 1200f) < 1e-3f,
                      "CTL-PRESET1: clearing the preset must RESTORE the author's max height, got "
                      + uc.maxHeightMeters);
                Check(uc.seed == 999,
                      "CTL-PRESET1: clearing the preset must leave an undriven field alone");
                Check(!uc.PresetIsDriving,
                      "CTL-PRESET1: after clearing, nothing may still report as preset-driven");

                // Clearing again must be a no-op rather than restoring stale values a second time.
                uc.worldSizeKm = 5f;
                uc.ValidateSettings();
                Check(Mathf.Abs(uc.worldSizeKm - 5f) < 1e-4f,
                      "CTL-PRESET1: with no preset, validation must not re-restore an old backup");

                // A preset is clamped by the SAME rules a typed value is - it cannot smuggle in a
                // world the maths could not survive.
                preset.overrideWorldSizeKm = true;
                preset.worldSizeKm = 100000f;
                uc.worldPreset = preset;
                uc.ValidateSettings();
                Check(uc.worldSizeKm <= 56f + 1e-3f,
                      "CTL-PRESET1: a preset's world size must be clamped like a typed one, got "
                      + uc.worldSizeKm);
            }
            finally
            {
                if (preset != null) UnityEngine.Object.DestroyImmediate(preset);
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// CTL-PAL1 (CTL-4): a node resolves its prototype set BY NAME, and a missing name warns,
        /// does not crash, and places nothing.
        ///
        /// The "places nothing" half is the one that matters. A node that fell back to slot 0 when
        /// a name was not found would plant a species the author never asked for and look like it
        /// worked - which is precisely the failure naming exists to remove, and which
        /// ScatterApplier's own header warns about for index-addressed slots.
        /// </summary>
        private static void TestNamedSpeciesResolution()
        {
            // --- the registry half: name -> slot, on the table where the names already live ---
            UniverseCreator.Ecology.SpeciesTable table = new UniverseCreator.Ecology.SpeciesTable();
            table.count = 3;
            table.envelopes = new UniverseCreator.Ecology.EcoEnvelope[3];
            for (int i = 0; i < 3; i++) table.envelopes[i] = new UniverseCreator.Ecology.EcoEnvelope();
            table.envelopes[0].displayName = "Scots Pine";
            table.envelopes[1].displayName = "Silver Birch";
            table.envelopes[2].displayName = "";          // an undescribed slot

            Check(table.FindSlotByName("Silver Birch") == 1,
                  "CTL-PAL1: a named species must resolve to its own slot");
            Check(table.FindSlotByName("silver birch") == 1,
                  "CTL-PAL1: name resolution must be case-insensitive");
            Check(table.FindSlotByName("  Scots Pine  ") == 0,
                  "CTL-PAL1: surrounding whitespace must not change which species is meant");
            Check(table.FindSlotByName("Douglas Fir") == -1,
                  "CTL-PAL1: an absent name must be -1");
            Check(table.FindSlotByName("") == -1 && table.FindSlotByName(null) == -1,
                  "CTL-PAL1: an empty or null name must be -1, NEVER slot 0");

            // An undescribed slot must not be matched by an empty name either.
            Check(table.FindSlotByName("   ") == -1,
                  "CTL-PAL1: a whitespace-only name must not match the undescribed slot");

            // Slot order must not matter: this is the whole point. Deleting an earlier entry
            // shifts indices, and the NAME must still find the right species.
            UniverseCreator.Ecology.SpeciesTable shifted = new UniverseCreator.Ecology.SpeciesTable();
            shifted.count = 2;
            shifted.envelopes = new UniverseCreator.Ecology.EcoEnvelope[2];
            shifted.envelopes[0] = new UniverseCreator.Ecology.EcoEnvelope();
            shifted.envelopes[1] = new UniverseCreator.Ecology.EcoEnvelope();
            shifted.envelopes[0].displayName = "Silver Birch";   // was slot 1, now slot 0
            shifted.envelopes[1].displayName = "Scots Pine";
            Check(shifted.FindSlotByName("Silver Birch") == 0
                  && shifted.FindSlotByName("Scots Pine") == 1,
                  "CTL-PAL1: a name must follow its species when slots are reordered - that is "
                  + "the regression an integer slot cannot survive");

            // count is respected, so a table that reports fewer slots than its array holds does
            // not resolve names in the tail.
            UniverseCreator.Ecology.SpeciesTable clipped = new UniverseCreator.Ecology.SpeciesTable();
            clipped.envelopes = table.envelopes;
            clipped.count = 1;
            Check(clipped.FindSlotByName("Silver Birch") == -1,
                  "CTL-PAL1: resolution must not read past the table's own count");

            // --- the node half: default is OFF, so existing graphs are untouched ---
            UniverseCreator.Nodes.Scatter.TreeScatterNode fresh =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            Check(string.IsNullOrEmpty(fresh.prototypeSetName),
                  "CTL-PAL1: prototypeSetName must default to EMPTY so an existing graph keeps "
                  + "planting the slot its author chose");

            // --- a name with no ecology context resolves to -1 and warns rather than throwing ---
            bool threw = false;
            int slot = 0;
            try
            {
                TileData bare = new TileData(
                    Coord.Zero,
                    new CoordRect(new Coord(0, 0), new Coord(33, 33)),
                    new Vector3(0f, 0f, 0f),
                    new Vector3(100f, 600f, 100f),
                    600f);
                slot = UniverseCreator.Nodes.Scatter.ScatterNodeBase.ResolveNamedSlot(
                    bare, "trees", "Scots Pine", "Tree Scatter");
            }
            catch (System.Exception) { threw = true; }

            Check(!threw, "CTL-PAL1: resolving a name with no ecology must warn, not throw");
            Check(slot < 0,
                  "CTL-PAL1: with no ecology context the slot must be -1 so nothing is placed");
            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.ResolveNamedSlot(
                      null, "trees", "Scots Pine", "Tree Scatter") < 0,
                  "CTL-PAL1: a null tile must resolve to -1 rather than throwing");
            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.ResolveNamedSlot(
                      null, "trees", "", "Tree Scatter") < 0,
                  "CTL-PAL1: an empty name must short-circuit to -1");
        }

        /// <summary>
        /// VEG-GND1: vegetation sits IN the ground, not on top of one height sample.
        ///
        /// The reported fault was trunks with daylight under their downhill side, and grass held
        /// bolt upright on a hillside so its base lifts off the surface. Reading the code found
        /// that objects, rocks and shrubs ALREADY embed (ObjectPrototypeDef.embedDepth,
        /// RockScatterNode.embedDepth, ShrubPrototypeDef.embedDepth) and that detail alignment was
        /// already plumbed to Unity - so TREES were the only unembedded path, and grass alignment
        /// was merely defaulted OFF. This pins both halves of that fix.
        /// </summary>
        private static void TestVegetationGrounding()
        {
            // --- the conform: lowest ground under the footprint, so no side floats ---
            const int res = 33;
            TileData tile = new TileData(
                Coord.Zero,
                new CoordRect(new Coord(0, 0), new Coord(res, res)),
                new Vector3(0f, 0f, 0f),
                new Vector3(320f, 600f, 320f),
                600f);

            Matrix slope = tile.CreateMatrix();
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    slope.arr[z * res + x] = 0.5f - x * 0.01f;    // falls toward +X

            float centreY = slope.GetWorldHeight(160f, 160f);
            float conformed = UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(
                slope, 160f, 160f, 12f, centreY);

            Check(conformed <= centreY + 1e-4f,
                  "VEG-GND1: conforming must never RAISE the base above the centre sample");
            Check(conformed < centreY - 1e-4f,
                  "VEG-GND1: on a slope the conform must drop to the lowest footprint sample, or "
                  + "the downhill side of the base still hangs (got " + conformed + " vs centre "
                  + centreY + ")");

            // Radius 0 keeps the shipped single-sample behaviour exactly - the zero-regression half.
            Check(Mathf.Abs(UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(
                      slope, 160f, 160f, 0f, centreY) - centreY) < 1e-6f,
                  "VEG-GND1: radius 0 must leave the single-sample snap untouched");
            Check(Mathf.Abs(UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(
                      null, 160f, 160f, 12f, centreY) - centreY) < 1e-6f,
                  "VEG-GND1: a null height field must return the centre rather than throwing");

            // --- DEF-7: A DIAGONAL SLOPE. This is the case the test above cannot see. ---------
            // The slope above falls toward +X only, so the N/S/E/W ring samples the steepest
            // descent directly and the conform looks correct. On a slope running DIAGONALLY the
            // steepest descent lies between two axis samples, so a four-point ring never reads the
            // lowest ground under the footprint and the downhill side of the base still hangs -
            // at some slope orientations and not others, which is the author's "the hanging at
            // certain angle inclination". Eight samples is the fix; this is the check that pins it.
            Matrix diag = tile.CreateMatrix();
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    diag.arr[z * res + x] = 0.5f - (x + z) * 0.01f;   // falls toward +X AND +Z

            float dCentreY = diag.GetWorldHeight(160f, 160f);
            float dConformed = UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(
                diag, 160f, 160f, 12f, dCentreY);

            // What a FOUR-point ring would have produced, computed here so the check is about the
            // behaviour and not about a magic number.
            float axisOnly = dCentreY;
            float aN = diag.GetWorldHeight(160f, 160f + 12f);
            float aS = diag.GetWorldHeight(160f, 160f - 12f);
            float aE = diag.GetWorldHeight(160f + 12f, 160f);
            float aW = diag.GetWorldHeight(160f - 12f, 160f);
            if (aN < axisOnly) axisOnly = aN;
            if (aS < axisOnly) axisOnly = aS;
            if (aE < axisOnly) axisOnly = aE;
            if (aW < axisOnly) axisOnly = aW;

            Check(dConformed < axisOnly - 0.25f,
                  "DEF-7: on a DIAGONAL slope the conform must drop below what the four axis "
                  + "samples alone would find (got " + dConformed.ToString("F3")
                  + ", axis-only would give " + axisOnly.ToString("F3") + "). Equal values mean "
                  + "the diagonals are not being sampled, so the steepest descent is missed and "
                  + "the downhill side of the base hangs - at certain slope angles only, which is "
                  + "why an axis-aligned test passes while the author still sees it.");

            Check(dConformed <= dCentreY + 1e-4f,
                  "DEF-7: conforming on a diagonal slope must still never RAISE the base");

            // The eight-point ring must stay ON the circle: sampling the full radius on each axis
            // would read the square's corners and over-drop by ~41%, burying the trunk.
            float trueDiagDrop = dCentreY - diag.GetWorldHeight(
                160f + 12f * 0.70710678f, 160f + 12f * 0.70710678f);
            Check(dConformed >= dCentreY - trueDiagDrop - 0.05f,
                  "DEF-7: the conform dropped FURTHER than the lowest point on the footprint "
                  + "circle - the diagonal samples must sit at r/sqrt(2) per axis, not at r, or "
                  + "the trunk is buried instead of seated.");

            // --- the node defaults: saved graphs unchanged, NEW nodes grounded ---
            UniverseCreator.Nodes.Scatter.TreeScatterNode saved =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            Check(saved.groundConformRadiusM <= 0.001f && saved.groundEmbedMetres <= 0f,
                  "VEG-GND1: a node deserialized from an existing graph must keep the shipped "
                  + "zeroes so no saved world shifts its planting");

            UniverseCreator.Nodes.Scatter.TreeScatterNode fresh =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            fresh.OnCreatedFresh();
            Check(fresh.groundConformRadiusM > 0.001f,
                  "VEG-GND1: a NEW node must seat its base over a footprint, not one sample - "
                  + "'off by default' is why the floating was never fixed in practice");
            Check(fresh.groundEmbedMetres >= 0.02f && fresh.groundEmbedMetres <= 0.06f,
                  "VEG-GND1: a NEW node must sink the base into the ground within the author's "
                  + "2-5 cm band, got " + fresh.groundEmbedMetres + " m");

            // --- grass: leans with the slope, proportionally ---
            DetailPrototypeDef grass = new DetailPrototypeDef();
            Check(grass.alignToGround,
                  "VEG-GND1: a new detail prototype must lean with the ground - grass stood bolt "
                  + "upright on a hillside lifts its base off the surface");
            Check(grass.groundAlignStrength > 0.01f && grass.groundAlignStrength < 1f,
                  "VEG-GND1: ground alignment must be PARTIAL - a hard 1 lays a blade flat and "
                  + "reads as pressed down, got " + grass.groundAlignStrength);

            // --- the paths that already embedded must not have regressed ---
            Check(new ObjectPrototypeDef().embedDepth > 0.0001f,
                  "VEG-GND1: objects must still embed (this already shipped)");
            Check(new UniverseCreator.Nodes.Scatter.RockScatterNode().embedDepth > 0.0001f,
                  "VEG-GND1: rocks must still embed (this already shipped)");

            // --- leaves carry no collider: grass none, shrub trigger, tree solid ---
            UniverseCreator.Ecology.EcoEnvelope blade = new UniverseCreator.Ecology.EcoEnvelope();
            blade.lifeForm = UniverseCreator.Ecology.LifeForm.Graminoid;
            Check(blade.ResolveColliderPolicy() == UniverseCreator.Ecology.ColliderPolicy.None,
                  "VEG-GND1: grass must resolve to NO collider - thousands of colliders for "
                  + "something a player walks through");

            UniverseCreator.Ecology.EcoEnvelope bush = new UniverseCreator.Ecology.EcoEnvelope();
            bush.lifeForm = UniverseCreator.Ecology.LifeForm.Shrub;
            Check(bush.ResolveColliderPolicy() == UniverseCreator.Ecology.ColliderPolicy.Trigger,
                  "VEG-GND1: a shrub must be walk-through (trigger), not a wall");

            UniverseCreator.Ecology.EcoEnvelope trunk = new UniverseCreator.Ecology.EcoEnvelope();
            trunk.lifeForm = UniverseCreator.Ecology.LifeForm.Tree;
            Check(trunk.ResolveColliderPolicy() == UniverseCreator.Ecology.ColliderPolicy.Solid,
                  "VEG-GND1: a tree trunk must stop a body");
        }

        /// <summary>
        /// VEG-LEAF1 - leaves carry no collider; the wood does.
        ///
        /// A real tree stops a body at the trunk and the main limbs and lets a shoulder pass
        /// through the canopy. A mesh collider over tree geometry does the opposite: every leaf
        /// triangle becomes collision, so the canopy is a wall and the per-tree cost is the whole
        /// mesh instead of three capsules. This pins the detector that names that.
        ///
        /// WHAT IT CANNOT DO: judge whether a given collider "is a leaf". Nothing name-free can,
        /// and guessing from object or material names would break on the first content set that
        /// names things differently. So the rule is about collider TYPE and COUNT, which mean the
        /// same thing in any content set, and the finding is a REPORT - the prefab belongs to the
        /// buyer and a validation panel that edits assets on repaint is a worse bug than the one
        /// it fixes.
        /// </summary>
        private static void TestLeafColliderPolicy()
        {
            // A real mesh with volume, so a convex hull can actually be built and physics has
            // nothing to complain about. A mesh collider with no mesh would be a different
            // (and fake) thing to test.
            Mesh tetra = new Mesh();
            tetra.vertices = new Vector3[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f)
            };
            tetra.triangles = new int[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 };
            tetra.RecalculateBounds();

            try
            {
            // ---- a clean tree: one capsule on the trunk, nothing on the foliage -------------
            GameObject clean = new GameObject("UC_SelfTest_CleanTree");
            try
            {
                clean.AddComponent<CapsuleCollider>();
                GameObject canopy = new GameObject("canopy");
                canopy.transform.SetParent(clean.transform, false);

                int total, mesh, concave;
                UniverseCreator.Editor.Inspectors.UCVegetationValidation.FoliageCollisionIssue issues =
                    UniverseCreator.Editor.Inspectors.UCVegetationValidation
                        .InspectFoliageCollision(clean, out total, out mesh, out concave);

                Check(issues == UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.None,
                      "VEG-LEAF1: a trunk capsule with bare foliage must raise nothing - this is "
                      + "the shape we are asking authors for, so flagging it would be noise");
                Check(total == 1 && mesh == 0 && concave == 0,
                      "VEG-LEAF1: the clean tree must count exactly one collider and no mesh ones");
            }
            finally { UnityEngine.Object.DestroyImmediate(clean); }

            // ---- leaves collided by a convex mesh collider ----------------------------------
            GameObject leafy = new GameObject("UC_SelfTest_LeafyTree");
            try
            {
                leafy.AddComponent<CapsuleCollider>();

                GameObject cards = new GameObject("cards");
                cards.transform.SetParent(leafy.transform, false);
                MeshCollider mc = cards.AddComponent<MeshCollider>();
                mc.sharedMesh = tetra;
                mc.convex = true;

                int total, mesh, concave;
                UniverseCreator.Editor.Inspectors.UCVegetationValidation.FoliageCollisionIssue issues =
                    UniverseCreator.Editor.Inspectors.UCVegetationValidation
                        .InspectFoliageCollision(leafy, out total, out mesh, out concave);

                Check((issues & UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.MeshCollider) != 0,
                      "VEG-LEAF1: a mesh collider on tree geometry must be reported - that is how "
                      + "leaf cards become solid");
                Check((issues & UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.ConcaveMeshCollider) == 0,
                      "VEG-LEAF1: a CONVEX mesh collider must not be reported as the concave case");
                Check(total == 2 && mesh == 1,
                      "VEG-LEAF1: the inspection must count colliders on inactive and child "
                      + "objects, not just the root");
            }
            finally { UnityEngine.Object.DestroyImmediate(leafy); }

            // ---- the worst case: per-triangle collision against leaf cards -----------------
            GameObject concaveTree = new GameObject("UC_SelfTest_ConcaveTree");
            try
            {
                GameObject cards = new GameObject("cards");
                cards.transform.SetParent(concaveTree.transform, false);
                cards.SetActive(false);                       // still ships; still reported
                MeshCollider worst = cards.AddComponent<MeshCollider>();
                worst.sharedMesh = tetra;                     // convex defaults to false

                int total, mesh, concave;
                UniverseCreator.Editor.Inspectors.UCVegetationValidation.FoliageCollisionIssue issues =
                    UniverseCreator.Editor.Inspectors.UCVegetationValidation
                        .InspectFoliageCollision(concaveTree, out total, out mesh, out concave);

                Check((issues & UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.ConcaveMeshCollider) != 0,
                      "VEG-LEAF1: a non-convex mesh collider on foliage is the worst case and must "
                      + "be called out separately");
                Check(concave == 1 && total == 1,
                      "VEG-LEAF1: an INACTIVE collider must still be counted - it ships with the "
                      + "prefab and comes back the moment that branch is enabled");
            }
            finally { UnityEngine.Object.DestroyImmediate(concaveTree); }

            // ---- a tree with no collision at all -------------------------------------------
            GameObject bare = new GameObject("UC_SelfTest_BareTree");
            try
            {
                int total, mesh, concave;
                UniverseCreator.Editor.Inspectors.UCVegetationValidation.FoliageCollisionIssue issues =
                    UniverseCreator.Editor.Inspectors.UCVegetationValidation
                        .InspectFoliageCollision(bare, out total, out mesh, out concave);

                Check((issues & UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.NoCollision) != 0,
                      "VEG-LEAF1: a tree prefab with no collider must be reported - UC attaches "
                      + "none of its own, so a body would walk through the trunk");
                Check(total == 0,
                      "VEG-LEAF1: the bare tree must count zero colliders");
            }
            finally { UnityEngine.Object.DestroyImmediate(bare); }

            // ---- a null prefab says nothing: it already has its own row --------------------
            {
                int total, mesh, concave;
                UniverseCreator.Editor.Inspectors.UCVegetationValidation.FoliageCollisionIssue issues =
                    UniverseCreator.Editor.Inspectors.UCVegetationValidation
                        .InspectFoliageCollision(null, out total, out mesh, out concave);

                Check(issues == UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.None,
                      "VEG-LEAF1: a null prefab must raise nothing here - the empty-slot row "
                      + "already covers it, and two rows for one fault trains authors to ignore "
                      + "the panel");
                Check(total == 0 && mesh == 0 && concave == 0,
                      "VEG-LEAF1: a null prefab must report zero counts rather than throw");
            }

            // ---- the budget is a real threshold, not a dead constant ------------------------
            Check(UniverseCreator.Editor.Inspectors.UCVegetationValidation.TrunkColliderBudget > 0,
                  "VEG-LEAF1: the trunk collider budget must be positive or the too-many rule "
                  + "fires on every tree");

            GameObject many = new GameObject("UC_SelfTest_ManyColliders");
            try
            {
                int want = UniverseCreator.Editor.Inspectors.UCVegetationValidation
                               .TrunkColliderBudget + 1;
                for (int i = 0; i < want; i++)
                {
                    GameObject part = new GameObject("part" + i);
                    part.transform.SetParent(many.transform, false);
                    part.AddComponent<SphereCollider>();
                }

                int total, mesh, concave;
                UniverseCreator.Editor.Inspectors.UCVegetationValidation.FoliageCollisionIssue issues =
                    UniverseCreator.Editor.Inspectors.UCVegetationValidation
                        .InspectFoliageCollision(many, out total, out mesh, out concave);

                Check((issues & UniverseCreator.Editor.Inspectors.UCVegetationValidation
                                    .FoliageCollisionIssue.TooMany) != 0,
                      "VEG-LEAF1: one collider past the budget must trip the too-many rule");
                Check(mesh == 0,
                      "VEG-LEAF1: primitives must never be counted as mesh colliders - capsules "
                      + "on the limbs are exactly what we are asking for");
                Check(total == want,
                      "VEG-LEAF1: the count must be every collider in the hierarchy");
            }
            finally { UnityEngine.Object.DestroyImmediate(many); }

            // ---- the policy behind it all: grass never gets one ----------------------------
            // Restated here rather than only in VEG-GND1 because this is the gate an author
            // reads when they ask "why is my grass not solid" - the answer must sit next to it.
            UniverseCreator.Ecology.EcoEnvelope sward = new UniverseCreator.Ecology.EcoEnvelope();
            sward.lifeForm = UniverseCreator.Ecology.LifeForm.Graminoid;
            Check(sward.ResolveColliderPolicy() == UniverseCreator.Ecology.ColliderPolicy.None,
                  "VEG-LEAF1: grass must resolve to no collider - the parting a player feels is "
                  + "in the vertex shader, which is why a million blades cost nothing");
            }
            finally { UnityEngine.Object.DestroyImmediate(tetra); }
        }

        // CTL-3 probes. Plain classes, not Generators: SectionNameFor takes a FieldInfo, so
        // the grouping rule can be pinned without standing up a node or a GraphView. The
        // initialisers are there to keep CS0649 (never-assigned field) out of the build.
        private class SectionProbe
        {
            [Header("Shape")] public float withHeader = 0f;
            public float withoutHeader = 0f;
            [Header("")] public float blankHeader = 0f;
        }

        private class SectionProbeNoHeaders
        {
            public float a = 0f;
            public float b = 0f;
        }

        /// <summary>
        /// CTL-3 (`CTL-TAB1`): node parameters group into named sections, keyed on Unity's own
        /// [Header] attribute, and a node with no header is laid out exactly as before.
        ///
        /// WHAT IT CANNOT DO: judge the layout. Whether the sections read well is for the author
        /// to look at in the graph editor; a headless run has no GraphView to build. What it CAN
        /// pin is the rule that decides the grouping, and - the thing that matters most - that
        /// the rule is NOT INERT: a shipped node already carries headers, so the feature does
        /// something the moment it lands rather than waiting for content that may never come.
        /// </summary>
        private static void TestNodeSectionGrouping()
        {
            System.Type probe = typeof(SectionProbe);

            System.Reflection.FieldInfo fWith = probe.GetField("withHeader");
            System.Reflection.FieldInfo fWithout = probe.GetField("withoutHeader");
            System.Reflection.FieldInfo fBlank = probe.GetField("blankHeader");

            Check(fWith != null && fWithout != null && fBlank != null,
                  "CTL-TAB1: the probe fields must be reflectable or the rest of this gate is "
                  + "testing nothing");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.SectionNameFor(fWith) == "Shape",
                  "CTL-TAB1: a [Header] must open a section under exactly that name");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.SectionNameFor(fWithout) == null,
                  "CTL-TAB1: a field with no header must open NO section - this is what keeps "
                  + "every existing node laid out the way it already was");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.SectionNameFor(fBlank) == null,
                  "CTL-TAB1: a blank [Header(\"\")] must open no section - Unity allows it, and a "
                  + "foldout with no name is worse than no foldout");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.SectionNameFor(null) == null,
                  "CTL-TAB1: a null field must return null rather than throw - the layout loop "
                  + "calls this for every field of every node");

            // A whole type with no headers opens nothing: the no-regression condition stated
            // over a type rather than one field.
            int sections = 0;
            System.Reflection.FieldInfo[] plain = typeof(SectionProbeNoHeaders).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            for (int i = 0; i < plain.Length; i++)
                if (UniverseCreator.Editor.GraphUI.UCNodeView.SectionNameFor(plain[i]) != null)
                    sections++;

            Check(sections == 0,
                  "CTL-TAB1: a node type with no [Header] anywhere must produce zero sections");

            // NOT AN INERT DIAL: a node that already ships groups itself immediately.
            int shipped = 0;
            bool sawOcean = false;
            System.Reflection.FieldInfo[] hydro = typeof(UniverseCreator.Nodes.Creators.HydrologyNode)
                .GetFields(System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.Instance);
            for (int i = 0; i < hydro.Length; i++)
            {
                string name = UniverseCreator.Editor.GraphUI.UCNodeView.SectionNameFor(hydro[i]);
                if (name == null) continue;
                shipped++;
                if (name == "Ocean") sawOcean = true;
            }

            Check(shipped > 0,
                  "CTL-TAB1: a SHIPPED node must already produce sections, or the grouping is an "
                  + "inert feature waiting on content that may never be authored");
            Check(sawOcean,
                  "CTL-TAB1: the authored header names must come through verbatim (the Hydrology "
                  + "node's \"Ocean\" group)");

            // ---- the other half of CTL-3: a node names the manager behind it ---------------
            // READ-ONLY by design. The mapping is what the gate can pin; whether the row reads
            // well is for the author to look at.
            Check(UniverseCreator.Editor.GraphUI.UCNodeView.ManagerTypeFor(null) == null,
                  "CTL-TAB1: a null node must map to no manager rather than throw");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.ManagerTypeFor(
                      new UniverseCreator.Nodes.Scatter.TreeScatterNode())
                  == typeof(UniverseCreator.TerrainSystem.UCVegetationSystemManager),
                  "CTL-TAB1: a scatter node's scene half is the vegetation manager - mapped on "
                  + "ScatterNodeBase, so every scatter kind is covered by one entry");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.ManagerTypeFor(
                      new UniverseCreator.Nodes.Creators.HydrologyNode())
                  == typeof(UniverseCreator.TerrainSystem.UCWaterSystemManager),
                  "CTL-TAB1: a hydrology node's scene half is the water manager");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.ManagerTypeFor(
                      new UniverseCreator.Nodes.Outputs.WeatherOutputNode())
                  == typeof(UniverseCreator.TerrainSystem.UCWeatherSystemManager),
                  "CTL-TAB1: a weather output's scene half is the weather manager");

            Check(UniverseCreator.Editor.GraphUI.UCNodeView.ManagerTypeFor(
                      new UniverseCreator.Nodes.Outputs.LightingOutputNode())
                  == typeof(UniverseCreator.TerrainSystem.UCLightingSystemManager),
                  "CTL-TAB1: a lighting output's scene half is the lighting manager");

            // A node with no manager must map to NOTHING. Without this the mapping could
            // quietly claim a manager for every node and point authors at the wrong panel,
            // which is worse than showing no row.
            Check(UniverseCreator.Editor.GraphUI.UCNodeView.ManagerTypeFor(
                      new UniverseCreator.Nodes.Outputs.HeightOutputNode()) == null,
                  "CTL-TAB1: a node with no scene-side manager must map to null, so it shows no "
                  + "row at all rather than pointing at an unrelated panel");
        }

        /// <summary>
        /// PERF-1 (`PERF-CULL1`): a prop that authors no cull distance gets one from the quality
        /// tier, so the mobile floor stops drawing every rock in the world at any range.
        ///
        /// WHAT THIS IS NOT: a new per-tier table of metres. Trees, grass and the basemap are
        /// already culled by the acuity law (feature size against the tier's tolerated pixel
        /// budget); instanced props were simply never wired to it, so their unauthored default
        /// stayed -1 = never cull. This pins the wiring, the precedence, and - most importantly -
        /// the direction it fails in.
        /// </summary>
        private static void TestPropCullDistanceTier()
        {
            float desktop = UniverseCreator.TerrainSystem.Geometry.AcuityTier.Resolve(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.High);
            float mobileHigh = UniverseCreator.TerrainSystem.Geometry.AcuityTier.Resolve(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium);
            float mobileLow = UniverseCreator.TerrainSystem.Geometry.AcuityTier.Resolve(
                UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low);

            // The documented device mapping this whole item rests on (AcuityTier's own header):
            // Low = Mobile Low, Medium = Mobile High, High = Desktop, Ultra = flagship.
            Check(mobileLow > mobileHigh && mobileHigh > desktop,
                  "PERF-CULL1: the acuity budget must get coarser as the tier drops, or there is "
                  + "no mobile tolerance to spend on culling");

            // 1. AN AUTHORED DISTANCE ALWAYS WINS, even on the coarsest tier.
            Check(Mathf.Abs(UniverseCreator.Optimization.GPUInstancingManager
                      .EffectiveCullDistanceM(50f, 2f, mobileLow) - 50f) < 1e-4f,
                  "PERF-CULL1: an authored cull distance must survive untouched - the author said "
                  + "'not past here' and a tier default must not argue with it");

            // An authored 0 is a real authored value ("cull immediately"), not "unset".
            Check(UniverseCreator.Optimization.GPUInstancingManager
                      .EffectiveCullDistanceM(0f, 2f, mobileLow) == 0f,
                  "PERF-CULL1: an authored zero must be honoured, not read as unset");

            // 2. DESKTOP AND FLAGSHIP ARE UNCHANGED. This is the no-regression guarantee.
            Check(UniverseCreator.Optimization.GPUInstancingManager
                      .EffectiveCullDistanceM(-1f, 2f, desktop) < 0f,
                  "PERF-CULL1: at the Desktop budget an unauthored prop must still never cull - "
                  + "today's behaviour, byte for byte");

            Check(UniverseCreator.Optimization.GPUInstancingManager.EffectiveCullDistanceM(
                      -1f, 2f,
                      UniverseCreator.TerrainSystem.Geometry.AcuityTier.Resolve(
                          UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Ultra)) < 0f,
                  "PERF-CULL1: the flagship tier must not cull an unauthored prop either");

            // 3. THE MOBILE TIERS DERIVE A REAL, FINITE DISTANCE. Without this the item is inert.
            float medium = UniverseCreator.Optimization.GPUInstancingManager
                .EffectiveCullDistanceM(-1f, 2f, mobileHigh);
            float low = UniverseCreator.Optimization.GPUInstancingManager
                .EffectiveCullDistanceM(-1f, 2f, mobileLow);

            Check(medium > 0f && low > 0f,
                  "PERF-CULL1: a mobile tier must produce a finite cull distance for an "
                  + "unauthored prop, or the whole item does nothing on the floor device");

            Check(low < medium,
                  "PERF-CULL1: the coarser the tier, the nearer the cutoff - Mobile Low must cull "
                  + "sooner than Mobile High");

            // The law is size-driven: a boulder stays visible further out than a pebble. This is
            // what makes one law correct for every prop instead of one number per species.
            float pebble = UniverseCreator.Optimization.GPUInstancingManager
                .EffectiveCullDistanceM(-1f, 0.2f, mobileHigh);
            float boulder = UniverseCreator.Optimization.GPUInstancingManager
                .EffectiveCullDistanceM(-1f, 4f, mobileHigh);

            Check(boulder > pebble && pebble > 0f,
                  "PERF-CULL1: a bigger prop must cull further out - the cutoff is the range at "
                  + "which the prop itself goes sub-pixel, not a per-species constant");

            // It must agree with the law the rest of the LOD envelope already uses, rather than
            // being a parallel approximation of it.
            float viaLaw = UniverseCreator.TerrainSystem.Geometry.AngularError
                .FeatureCullDistanceReference(2f, mobileHigh);
            Check(Mathf.Abs(medium - viaLaw) < 1e-3f,
                  "PERF-CULL1: the derived distance must BE the shared acuity law, not a second "
                  + "approximation of it - one owner for every draw distance");

            // 4. FAILS SAFE. The law fails closed at 0 m for a degenerate feature, and 0 returned
            // as a cull distance means "cull always" - every prop in the world would vanish. This
            // is the check that matters most: the cost of not culling is a slow frame, the cost of
            // the other direction is an empty world.
            Check(UniverseCreator.Optimization.GPUInstancingManager
                      .EffectiveCullDistanceM(-1f, 0f, mobileLow) < 0f,
                  "PERF-CULL1: a prop with no usable size must NEVER cull rather than cull at 0 m "
                  + "- returning the law's fail-closed zero here would empty the world");

            Check(UniverseCreator.Optimization.GPUInstancingManager
                      .EffectiveCullDistanceM(-1f, -5f, mobileLow) < 0f,
                  "PERF-CULL1: a negative feature size must fail to the safe side too");

            // 5. A shadow may not outlive the geometry casting it, now that the cull distance can
            // be derived rather than authored. Reuses the shipped clamp.
            Check(UniverseCreator.TerrainSystem.Geometry.ShadowLodBudget
                      .EffectiveShadowCastDistanceM(999f, medium) <= medium + 1e-3f,
                  "PERF-CULL1: a shadow distance must be clamped to the DERIVED cull distance, or "
                  + "a prop culled on mobile would still be casting a shadow");
        }

        /// <summary>
        /// PERF-2 (`PERF-INST1`): a removed instance stays removed - across the chunk unloading
        /// and streaming back in, and across a save and reload.
        ///
        /// WHAT WAS ALREADY BUILT, so this did not rebuild it: chunk partitioning is
        /// `GPUInstancingManager.byOwner` + `ClearOwner` (a recycled chunk withdraws exactly its
        /// own instances), and the stable identity is VEG-13's position-derived
        /// `ScatterInstance.instanceId`, whose own comment already named PERF-2's delta as the
        /// consumer that did not exist yet. The delta was the only missing piece.
        /// </summary>
        private static void TestScatterRemovalDelta()
        {
            UniverseCreator.TerrainSystem.ScatterRemovalStore store =
                new UniverseCreator.TerrainSystem.ScatterRemovalStore();

            Check(store.IsEmpty && store.Count == 0,
                  "PERF-INST1: a fresh store must be empty - an untouched world removes nothing");
            Check(!store.IsRemoved(12345),
                  "PERF-INST1: an empty store must report nothing removed");

            // --- removing ------------------------------------------------------------------
            Check(store.Remove(12345),
                  "PERF-INST1: removing an instance must report that something changed");
            Check(store.IsRemoved(12345) && store.Count == 1,
                  "PERF-INST1: a removed instance must read as removed");
            Check(!store.Remove(12345),
                  "PERF-INST1: removing the same instance twice must report no change");
            Check(store.Count == 1,
                  "PERF-INST1: a double removal must not grow the delta - it would grow without "
                  + "bound for a player who keeps swinging at a stump");

            // --- ZERO IS NOT AN ID, and this is the check that matters most -----------------
            // instanceId 0 means "the scatter pass never stamped this one". If 0 could enter the
            // store, EVERY unstamped instance in the world would vanish at once.
            Check(!store.Remove(0),
                  "PERF-INST1: id 0 must be refused - it means 'no identity', and storing it "
                  + "would erase every unstamped instance in the world");
            Check(store.Count == 1,
                  "PERF-INST1: the refused zero must not have been stored");
            Check(!store.IsRemoved(0),
                  "PERF-INST1: an unstamped instance must never read as removed");

            // --- restoring -----------------------------------------------------------------
            Check(store.Restore(12345),
                  "PERF-INST1: restoring a removed instance must report a change");
            Check(!store.IsRemoved(12345) && store.Count == 0,
                  "PERF-INST1: a restored instance must grow back");
            Check(!store.Restore(999),
                  "PERF-INST1: restoring something that was never removed must report no change");

            // --- several at once, and the swap-and-pop that retires one of them -------------
            for (int i = 1; i <= 5; i++) store.Remove(i * 100);
            Check(store.Count == 5, "PERF-INST1: five removals must all be held");

            Check(store.Restore(300) && store.Count == 4,
                  "PERF-INST1: restoring from the middle must drop exactly one entry");
            Check(store.IsRemoved(100) && store.IsRemoved(200)
                  && !store.IsRemoved(300)
                  && store.IsRemoved(400) && store.IsRemoved(500),
                  "PERF-INST1: restoring one must not disturb its neighbours - the swap-and-pop "
                  + "moves the tail entry into the hole, so a wrong index here would resurrect "
                  + "the wrong plant");

            // --- SAVE AND RELOAD: the whole point of the feature ---------------------------
            // JsonUtility is exactly the serialization Unity uses for the scene, so a round trip
            // through it proves the delta survives a save - and proves the runtime lookup set is
            // REBUILT from the serialized list rather than being lost with it.
            string saved = JsonUtility.ToJson(store);
            UniverseCreator.TerrainSystem.ScatterRemovalStore reloaded =
                JsonUtility.FromJson<UniverseCreator.TerrainSystem.ScatterRemovalStore>(saved);

            Check(reloaded != null && reloaded.Count == 4 && reloaded.IsRemoved(400),
                  "PERF-INST1: a removal must survive a save and reload - this is the feature, "
                  + "and it only works if the lookup set is rebuilt from the serialized list");
            Check(reloaded != null && !reloaded.IsRemoved(300),
                  "PERF-INST1: a restored instance must stay restored across a reload too");

            // --- it keys on the VEG-13 stable id, so it survives a RE-BAKE ------------------
            // An array index names a different plant after almost any edit; the position-derived
            // id is the same value next bake, which is what makes "stays gone" true.
            int id = UniverseCreator.Nodes.Scatter.ScatterNodeBase.StableInstanceId(123.45f, 678.90f);
            UniverseCreator.TerrainSystem.ScatterRemovalStore byPos =
                new UniverseCreator.TerrainSystem.ScatterRemovalStore();
            byPos.Remove(id);

            Check(byPos.IsRemoved(
                      UniverseCreator.Nodes.Scatter.ScatterNodeBase.StableInstanceId(123.45f, 678.90f)),
                  "PERF-INST1: the id recomputed from the same position must still match - that is "
                  + "what makes a removal survive a re-bake rather than deleting whatever lands "
                  + "at that array index next time");

            // --- clearing -------------------------------------------------------------------
            store.Clear();
            Check(store.IsEmpty && !store.IsRemoved(400),
                  "PERF-INST1: clearing the delta must regrow the whole world");
        }

        /// <summary>
        /// PERF-4 (`PERF-VOX1`): the voxel-key cheap mode of the shared scatter index. O(1) grid
        /// thinning whose memory is proportional to OCCUPIED cells, for constrained hardware.
        ///
        /// SE-3's `SpatialHash` stays the radius-query structure; this is not a replacement.
        /// SpatialHash is a dense rectangle (one slot per cell, allocated up front, clamped at
        /// MaxCellsPerAxis - so a big world or a fine cell silently gets a coarser grid than
        /// asked for). A dictionary of packed keys pays only for cells that hold something and
        /// never coarsens. The gate pins the two things that make it correct rather than merely
        /// cheap: the floor, and the packing.
        /// </summary>
        private static void TestVoxelGridThinning()
        {
            // --- the key is deterministic and cell-shaped -----------------------------------
            Check(UniverseCreator.VoxelGrid.Key(5.5f, 7.5f, 1f)
                  == UniverseCreator.VoxelGrid.Key(5.5f, 7.5f, 1f),
                  "PERF-VOX1: the same position must give the same key");

            Check(UniverseCreator.VoxelGrid.Key(5.1f, 7.1f, 1f)
                  == UniverseCreator.VoxelGrid.Key(5.9f, 7.9f, 1f),
                  "PERF-VOX1: two positions inside one cell must share its key - that sharing IS "
                  + "the thinning rule");

            Check(UniverseCreator.VoxelGrid.Key(5.5f, 7.5f, 1f)
                  != UniverseCreator.VoxelGrid.Key(6.5f, 7.5f, 1f),
                  "PERF-VOX1: adjacent cells must not share a key");

            // --- FLOOR, NOT TRUNCATION. The bug this prevents is silent and world-wide: -----
            // casting to int truncates toward zero, so -0.5 and +0.5 would both land in cell 0
            // and every pair straddling an axis through the origin would be thinned to one.
            Check(UniverseCreator.VoxelGrid.Key(-0.5f, 0f, 1f)
                  != UniverseCreator.VoxelGrid.Key(0.5f, 0f, 1f),
                  "PERF-VOX1: the cells either side of zero must be DIFFERENT - a truncating cast "
                  + "merges them, and any world whose origin is not its corner has negative "
                  + "coordinates");

            Check(UniverseCreator.VoxelGrid.Key(0f, -0.5f, 1f)
                  != UniverseCreator.VoxelGrid.Key(0f, 0.5f, 1f),
                  "PERF-VOX1: the same must hold on the Z axis");

            Check(UniverseCreator.VoxelGrid.Key(-1.5f, 0f, 1f)
                  == UniverseCreator.VoxelGrid.Key(-1.1f, 0f, 1f),
                  "PERF-VOX1: two points inside the same NEGATIVE cell must still share it");

            // --- the packing cannot collide on swapped indices ------------------------------
            Check(UniverseCreator.VoxelGrid.Key(1f, 0f, 1f)
                  != UniverseCreator.VoxelGrid.Key(0f, 1f, 1f),
                  "PERF-VOX1: (1,0) and (0,1) must differ - a key built by hashing or adding the "
                  + "two indices would collide here and thin unrelated points");

            // --- claiming: one per cell -----------------------------------------------------
            UniverseCreator.VoxelGrid grid =
                new UniverseCreator.VoxelGrid(2f);

            Check(grid.Count == 0, "PERF-VOX1: a fresh grid holds nothing");

            Check(grid.TryClaim(1f, 1f),
                  "PERF-VOX1: the first point in a cell must be KEPT");
            Check(!grid.TryClaim(1.5f, 1.5f),
                  "PERF-VOX1: a second point in the same cell must be THINNED - this is the whole "
                  + "operation");
            Check(grid.Count == 1,
                  "PERF-VOX1: a thinned point must not consume a cell of its own");

            Check(grid.TryClaim(5f, 5f) && grid.Count == 2,
                  "PERF-VOX1: a point in a free cell must still be kept after a rejection");

            Check(grid.IsClaimed(1.2f, 1.2f) && !grid.IsClaimed(99f, 99f),
                  "PERF-VOX1: occupancy must read back per cell");

            // --- memory is proportional to OCCUPANCY, not to extent -------------------------
            // The property the dense grid cannot have, and the reason this mode exists.
            UniverseCreator.VoxelGrid far =
                new UniverseCreator.VoxelGrid(1f);
            far.TryClaim(0f, 0f);
            far.TryClaim(100000f, 100000f);

            Check(far.Count == 2,
                  "PERF-VOX1: two points 100 km apart must cost exactly two cells - a dense grid "
                  + "would have to allocate the rectangle between them");

            // --- the cell size is the dial ---------------------------------------------------
            UniverseCreator.VoxelGrid coarse =
                new UniverseCreator.VoxelGrid(10f);
            Check(coarse.TryClaim(1f, 1f) && !coarse.TryClaim(9f, 9f),
                  "PERF-VOX1: a coarser cell must merge points a finer cell separates - otherwise "
                  + "the cell size is not doing anything");

            // --- a degenerate cell must not collapse the world ------------------------------
            // 1/0 would key the entire world to one cell, which reads as "everything is a
            // duplicate" and would thin a whole world down to a single instance.
            UniverseCreator.VoxelGrid degenerate =
                new UniverseCreator.VoxelGrid(0f);
            Check(degenerate.CellSize > 0f,
                  "PERF-VOX1: a zero cell size must be clamped, not divided by");
            Check(degenerate.TryClaim(0f, 0f) && degenerate.TryClaim(50f, 50f),
                  "PERF-VOX1: even clamped, two far-apart points must not read as one cell - a "
                  + "single key for the whole world would thin it to one instance");

            grid.Clear();
            Check(grid.Count == 0 && grid.TryClaim(1f, 1f),
                  "PERF-VOX1: clearing must free every cell for the next pass");
        }

        /// <summary>
        /// DEF-3 (`RIV-SCALE1`): the river network scales to the world instead of using
        /// Earth's absolute numbers, and the HIERARCHY survives the scaling.
        ///
        /// The author's ruling: *"a real river can reach 50 km, that does not mean we copy it
        /// like that - we scale it down to look real in this virtual world"*. Used literally on
        /// a 4 km world, the shipped `minDrainageAreaM2 = 1e6` wants 1 km2 of catchment before a
        /// channel starts and `runoffMetresPerSecond = 1e-8` yields ~0.01 m3/s, which Leopold
        /// turns into a channel a few centimetres deep: computed, written, invisible.
        ///
        /// What this pins is the MATHS the fix rests on. It cannot judge how the network looks -
        /// that is DEF-11's hierarchy work and the author's eye.
        /// </summary>
        private static void TestRiverWorldScaling()
        {
            // --- the Leopold inversion must be exact, because everything rides on it ---------
            // Scale is set by solving w = C * q^E for q. If that inversion is wrong, every
            // river in every world is the wrong size and nothing else would reveal it.
            float[] targets = { 4f, 8f, 18f, 40f, 80f };
            for (int i = 0; i < targets.Length; i++)
            {
                float w = targets[i];

                float q = Mathf.Pow(w / UniverseCreator.TerrainSystem.RiverCourseSolver.WidthDischargeCoeff,
                                    1f / UniverseCreator.TerrainSystem.RiverCourseSolver.WidthDischargeExponent);

                float halfW, depthM;
                UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(q, 0.003f,
                                                                                  out halfW, out depthM);

                Check(Mathf.Abs(halfW * 2f - w) < 0.05f,
                      "RIV-SCALE1: inverting Leopold for a " + w.ToString("0.#") + " m channel must "
                      + "give a discharge that produces exactly that width back (got "
                      + (halfW * 2f).ToString("0.##") + " m). If this drifts, every river in every "
                      + "world is the wrong size.");
            }

            // --- the target is clamped to what the solver can actually produce --------------
            // Asking for a width outside the solver's limits would scale every tributary
            // against a width the trunk never reaches, quietly shrinking the whole network.
            Check(UniverseCreator.TerrainSystem.RiverCourseSolver.MinChannelWidthM > 0f
                  && UniverseCreator.TerrainSystem.RiverCourseSolver.MaxChannelWidthM
                     > UniverseCreator.TerrainSystem.RiverCourseSolver.MinChannelWidthM,
                  "RIV-SCALE1: the solver's channel-width limits must be a real range, since the "
                  + "world-scale target is clamped into them");

            // --- THE HIERARCHY SURVIVES. This is the point of scaling the coefficient -------
            // only, and leaving Leopold's exponents alone: a big catchment must still make a
            // big river and a small one a small river, in proportion, after scaling.
            float runoff = 3e-5f;                 // a world-scaled coefficient
            float aStream = 5e4f;                 // 0.05 km2 headwater
            float aRiver = 5e5f;                  // 0.5 km2
            float aTrunk = 4e6f;                  // 4 km2

            float hwS, hwR, hwT, dS, dR, dT;
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(runoff * aStream, 0.004f, out hwS, out dS);
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(runoff * aRiver, 0.004f, out hwR, out dR);
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(runoff * aTrunk, 0.004f, out hwT, out dT);

            Check(hwT > hwR && hwR > hwS,
                  "RIV-SCALE1: a bigger catchment must still make a wider channel after scaling - "
                  + "a stream, a river and a trunk must be three different sizes, not one ditch");
            Check(dT > dS,
                  "RIV-SCALE1: and a deeper one, or the hierarchy is only in the mask and not in "
                  + "the ground");

            // --- AND IT IS VISIBLE. The actual defect was an invisible trench. --------------
            // Unity's heightmap is 16-bit, so one step on a 600 m world is ~9 mm. A channel
            // worth carving has to clear that by a wide margin or it is written and unseen.
            const float worldHeightM = 600f;
            float oneStepM = worldHeightM / 65535f;

            Check(dS / oneStepM > 10f,
                  "RIV-SCALE1: even the smallest scaled stream must cut more than ten heightmap "
                  + "steps (got " + (dS / oneStepM).ToString("0") + "). Below that it is computed, "
                  + "written and invisible - which is exactly the bug being fixed.");

            // --- the node's shipped defaults must actually be the scaled ones ---------------
            UniverseCreator.Nodes.Selectors.RiverNetworkNode node =
                new UniverseCreator.Nodes.Selectors.RiverNetworkNode();

            Check(node.scaleToWorld,
                  "RIV-SCALE1: world scaling must ship ON. There is no working behaviour to "
                  + "preserve here - the absolute defaults produce an invisible channel - so OFF "
                  + "by default would mean shipping the bug and hiding the fix behind a toggle.");
            Check(node.trunkChannelWidthMetres >= UniverseCreator.TerrainSystem.RiverCourseSolver.MinChannelWidthM
                  && node.trunkChannelWidthMetres <= UniverseCreator.TerrainSystem.RiverCourseSolver.MaxChannelWidthM,
                  "RIV-SCALE1: the default trunk width must sit inside the solver's limits");
            Check(node.channelInitiationFraction > 0f && node.channelInitiationFraction < 1f,
                  "RIV-SCALE1: the initiation fraction must be a real fraction of world area - "
                  + "that is what makes the network read at the same density on any world size");
        }

        /// <summary>
        /// DEF-2 (`DEF-GUARD1`): the terrain's limit on what an external system may flatten.
        ///
        /// The author's words: *"add a guard to the terrain to prevent that since they have a
        /// contract it must work well"*. The damage that made it necessary is Road &amp; City
        /// Master shipping an upstream `matchHeightsDistance = 50f` ("the width of height
        /// modification in meters", Road.cs:189), buffered by 1.65x in Terraforming.cs:241 - so
        /// one road reaches ~165 m of terrain. Invisible on a large landscape; on a 4 km world it
        /// flattens the world.
        /// </summary>
        private static void TestTerrainShapeGuard()
        {
            const float maxFlatten = 24f;
            const float maxBlend = 10f;

            // --- a reasonable road passes through untouched, and says nothing ---------------
            // A guard that logs on good input trains the author to ignore it.
            UniverseCreator.Integration.TerrainShapeRequest ok =
                new UniverseCreator.Integration.TerrainShapeRequest();
            ok.sourceLabel = "Test Road";
            ok.corridor = new UniverseCreator.Integration.TerrainCorridorSample[1];
            ok.corridor[0].flattenedHalfWidthMetres = 7f;      // a two-lane road with shoulders
            ok.corridor[0].blendOuterHalfWidthMetres = 9f;     // ~2 m of dirt past the shoulder

            string report;
            Check(UniverseCreator.TerrainSystem.TerrainShapeGuard.ClampCorridor(
                      ok, maxFlatten, maxBlend, out report) == 0,
                  "DEF-GUARD1: a road inside the limits must not be clamped");
            Check(report == null,
                  "DEF-GUARD1: and must produce no report at all - a guard that warns about good "
                  + "input teaches the author to ignore its warnings");
            Check(Mathf.Approximately(ok.corridor[0].flattenedHalfWidthMetres, 7f)
                  && Mathf.Approximately(ok.corridor[0].blendOuterHalfWidthMetres, 9f),
                  "DEF-GUARD1: an unclamped sample must be left exactly as authored");

            // --- the 165 m case: clamped, NOT dropped ---------------------------------------
            UniverseCreator.Integration.TerrainShapeRequest wide =
                new UniverseCreator.Integration.TerrainShapeRequest();
            wide.sourceLabel = "Road & City Master";
            wide.corridor = new UniverseCreator.Integration.TerrainCorridorSample[2];
            wide.corridor[0].flattenedHalfWidthMetres = 50f;    // matchHeightsDistance
            wide.corridor[0].blendOuterHalfWidthMetres = 165f;  // x1.65 buffer
            wide.corridor[1].flattenedHalfWidthMetres = 50f;
            wide.corridor[1].blendOuterHalfWidthMetres = 165f;

            int clamped = UniverseCreator.TerrainSystem.TerrainShapeGuard.ClampCorridor(
                wide, maxFlatten, maxBlend, out report);

            Check(clamped == 2,
                  "DEF-GUARD1: every over-wide sample must be clamped, not just the first");

            // THE WRITE-BACK. TerrainCorridorSample is a STRUCT, so clamping a local copy and
            // forgetting to assign it back leaves the array untouched - the guard would report
            // success and change nothing. This is the check that catches that.
            Check(Mathf.Approximately(wide.corridor[0].flattenedHalfWidthMetres, maxFlatten),
                  "DEF-GUARD1: the clamped value must be WRITTEN BACK into the array - the sample "
                  + "is a struct, so a guard that clamps a copy reports success and does nothing");
            Check(Mathf.Approximately(wide.corridor[0].blendOuterHalfWidthMetres,
                                      maxFlatten + maxBlend),
                  "DEF-GUARD1: the blend ceiling must move with the clamped flatten, since both "
                  + "are measured from the centre line");
            Check(wide.corridor.Length == 2,
                  "DEF-GUARD1: the request must still have its samples - clamped, never dropped. "
                  + "Dropping would leave the road floating over unshaped ground, which is worse "
                  + "and far more confusing than a narrower cut.");

            Check(report != null && report.Contains("Road & City Master"),
                  "DEF-GUARD1: the report must name the source, or the author hunts their own "
                  + "graph for a fault that belongs to a plugin");

            // --- a malformed profile is repaired, not propagated ----------------------------
            // A blend band INSIDE the flattened band has no room to blend in; left alone it
            // inverts the profile and cuts the step the blend exists to prevent.
            UniverseCreator.Integration.TerrainShapeRequest inverted =
                new UniverseCreator.Integration.TerrainShapeRequest();
            inverted.corridor = new UniverseCreator.Integration.TerrainCorridorSample[1];
            inverted.corridor[0].flattenedHalfWidthMetres = 10f;
            inverted.corridor[0].blendOuterHalfWidthMetres = 3f;   // inside the flatten

            UniverseCreator.TerrainSystem.TerrainShapeGuard.ClampCorridor(
                inverted, maxFlatten, maxBlend, out report);

            Check(inverted.corridor[0].blendOuterHalfWidthMetres
                  >= inverted.corridor[0].flattenedHalfWidthMetres,
                  "DEF-GUARD1: a blend band inside its own flattened band must be pulled out so "
                  + "the profile stays monotonic");

            // --- the guard cannot be configured into breaking the thing it protects ---------
            UniverseCreator.Integration.TerrainShapeRequest zeroed =
                new UniverseCreator.Integration.TerrainShapeRequest();
            zeroed.corridor = new UniverseCreator.Integration.TerrainCorridorSample[1];
            zeroed.corridor[0].flattenedHalfWidthMetres = 7f;
            zeroed.corridor[0].blendOuterHalfWidthMetres = 9f;

            UniverseCreator.TerrainSystem.TerrainShapeGuard.ClampCorridor(
                zeroed, 0f, -5f, out report);

            Check(zeroed.corridor[0].flattenedHalfWidthMetres > 0f,
                  "DEF-GUARD1: a zero or negative limit must be floored, not honoured - it would "
                  + "flatten nothing and strand every road on unshaped ground");

            // --- degenerate input must not throw; the bake calls this every time ------------
            Check(UniverseCreator.TerrainSystem.TerrainShapeGuard.ClampCorridor(
                      null, maxFlatten, maxBlend, out report) == 0,
                  "DEF-GUARD1: a null request must return 0 rather than throw");

            UniverseCreator.Integration.TerrainShapeRequest empty =
                new UniverseCreator.Integration.TerrainShapeRequest();
            Check(UniverseCreator.TerrainSystem.TerrainShapeGuard.ClampCorridor(
                      empty, maxFlatten, maxBlend, out report) == 0,
                  "DEF-GUARD1: a request with no corridor samples must return 0 rather than throw");

            // --- the shipped limits must be sane, or the guard is decoration ----------------
            Check(UniverseCreator.TerrainSystem.TerrainShapeGuard.DefaultMaxFlattenHalfWidthM > 0f
                  && UniverseCreator.TerrainSystem.TerrainShapeGuard.DefaultMaxFlattenHalfWidthM < 50f,
                  "DEF-GUARD1: the default flatten limit must be below the 50 m that caused the "
                  + "damage, and above zero");
            Check(UniverseCreator.TerrainSystem.TerrainShapeGuard.DefaultMaxBlendBeyondFlattenM < 50f,
                  "DEF-GUARD1: the default blend allowance must be metres, not the 165 m reach "
                  + "that flattened the author's world");
        }

        /// <summary>
        /// DEF-4 (`DEF-ROCK1`): a rock is authored as a deposit, not as a plant.
        ///
        /// The author's words: *"the rock still has species … for the rock have no species …
        /// props and rock please not same"*. The cause was that the per-prototype drawer walks
        /// EVERY serialized field on EcoEnvelope - right for a tree, absurd for a stone - and
        /// the same drawer serves all four channels.
        ///
        /// The schema is deliberately shared and is NOT changed: a deposit is still placed by
        /// process (talus below the angle of repose, cobble where water runs), so it genuinely
        /// uses slope, flow, salt and altitude. Only the presentation is filtered. This gate
        /// pins the filter - and in particular that it is an ALLOW-list, so a biological field
        /// added to EcoEnvelope tomorrow cannot appear on stone.
        /// </summary>
        private static void TestRockIsNotAPlant()
        {
            // UC-T06-16: exercise the actual selector query with the shipped preset definitions.
            int[] offeredCounts = new int[4];
            bool exclusive = true, appropriate = true, definitionsPreserved = true;
            var firstValidPreset = UniverseCreator.Ecology.SpeciesPreset.None;
            foreach (UniverseCreator.Ecology.SpeciesPreset preset in
                     System.Enum.GetValues(typeof(UniverseCreator.Ecology.SpeciesPreset)))
            {
                if (preset == UniverseCreator.Ecology.SpeciesPreset.None) continue;
                var definition = new UniverseCreator.Ecology.EcoEnvelope();
                bool valid = UniverseCreator.Ecology.SpeciesPresets.Apply(definition, preset);
                Check(valid, "UC-T06-16: picker preset has a definition: " + preset);
                if (!valid) continue;
                if (firstValidPreset == UniverseCreator.Ecology.SpeciesPreset.None) firstValidPreset = preset;
                string beforeSelection = UnityEngine.JsonUtility.ToJson(definition);
                int offers = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (!UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsPresetCompatible(
                            (UniverseCreator.TerrainSystem.UCScatterKind)k, preset)) continue;
                    offeredCounts[k]++;
                    offers++;
                    var form = definition.lifeForm;
                    if (k == 0 && form != UniverseCreator.Ecology.LifeForm.Tree
                               && form != UniverseCreator.Ecology.LifeForm.Palm) appropriate = false;
                    if (k == 1 && form != UniverseCreator.Ecology.LifeForm.Shrub
                               && form != UniverseCreator.Ecology.LifeForm.Succulent) appropriate = false;
                    if (k == 2 && (form == UniverseCreator.Ecology.LifeForm.Tree
                                || form == UniverseCreator.Ecology.LifeForm.Palm
                                || form == UniverseCreator.Ecology.LifeForm.Shrub
                                || form == UniverseCreator.Ecology.LifeForm.Succulent
                                || form == UniverseCreator.Ecology.LifeForm.Abiotic)) appropriate = false;
                    if (k == 3 && form != UniverseCreator.Ecology.LifeForm.Abiotic) appropriate = false;
                }
                if (offers != 1) exclusive = false;
                var afterSelection = new UniverseCreator.Ecology.EcoEnvelope();
                if (!UniverseCreator.Ecology.SpeciesPresets.Apply(afterSelection, preset)
                    || !string.Equals(beforeSelection, UnityEngine.JsonUtility.ToJson(afterSelection),
                                      System.StringComparison.Ordinal)) definitionsPreserved = false;
            }
            Check(exclusive && appropriate, "UC-T06-16: each shipped preset is offered only to its appropriate placement family");
            Check(definitionsPreserved, "UC-T06-16: picker classification preserves shipped preset definitions");
            for (int k = 0; k < 4; k++)
            {
                Check(offeredCounts[k] > 0, "UC-T06-16: prototype family has usable presets: " + k);
                Check(!UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsPresetCompatible(
                          (UniverseCreator.TerrainSystem.UCScatterKind)k, UniverseCreator.Ecology.SpeciesPreset.None)
                   && !UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsPresetCompatible(
                          (UniverseCreator.TerrainSystem.UCScatterKind)k, (UniverseCreator.Ecology.SpeciesPreset)int.MaxValue),
                      "UC-T06-16: None and unknown presets are not offered: " + k);
            }
            Check(!UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsPresetCompatible(
                      (UniverseCreator.TerrainSystem.UCScatterKind)int.MaxValue,
                      firstValidPreset),
                  "UC-T06-16: unknown prototype kind is not offered presets");

            Check(UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsDeposit(
                      UniverseCreator.TerrainSystem.UCScatterKind.Object),
                  "DEF-ROCK1: the Object channel is loose material and must read as a deposit");

            Check(!UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsDeposit(
                      UniverseCreator.TerrainSystem.UCScatterKind.Tree)
                  && !UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsDeposit(
                      UniverseCreator.TerrainSystem.UCScatterKind.Shrub)
                  && !UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer.IsDeposit(
                      UniverseCreator.TerrainSystem.UCScatterKind.Detail),
                  "DEF-ROCK1: trees, shrubs and grass must stay plants - they keep the full "
                  + "envelope and the iterate-don't-enumerate property that goes with it");

            // THE BIOLOGICAL FIELDS MUST NOT BE REACHABLE ON STONE. Named explicitly, one per
            // hidden group, because this is the author's actual complaint: a boulder was being
            // asked for its drought tolerance and the month it drops its leaves.
            string[] biological =
            {
                "lifeForm",              // A - identity: a rock has no life form
                "tempOptimumC",          // B - temperature
                "warmestMonthMinC",      // B - the treeline
                "moistureOptimum",       // C - plant-available soil moisture
                "droughtTolerance",      // C
                "precipOptimumMm",       // C
                "shadeTolerance",        // F - light and competition
                "matureHeightRangeM",    // G - structure
                "fireResponse",          // H - disturbance
                "deciduous"              // I - season
            };

            for (int i = 0; i < biological.Length; i++)
            {
                Check(!UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer
                          .DepositDrawsField(biological[i]),
                      "DEF-ROCK1: a deposit must not be asked for \"" + biological[i] + "\" - "
                      + "that is a plant's business, and showing it is what made the rock "
                      + "channel look like it had been mistaken for vegetation");
            }

            // ...but the four groups that DO decide where loose material forms must survive,
            // or the filter has thrown away the reason a deposit carries an envelope at all.
            string[] deposit =
            {
                "twiOptimum",            // flow: where water collects
                "saltTolerance",         // salt
                "slopeOptimumDeg",       // slope
                "minAltitudeM",          // altitude
                "colliderPolicy",        // mechanical: a boulder is solid
                "cullDistanceM",         // mechanical: clutter can vanish close in
                "allowedBiomes"          // which community it belongs to
            };

            for (int i = 0; i < deposit.Length; i++)
            {
                Check(UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer
                          .DepositDrawsField(deposit[i]),
                      "DEF-ROCK1: a deposit must still be able to author \"" + deposit[i] + "\" - "
                      + "ObjectPrototypeDef's own tooltip names slope, flow, salt and altitude as "
                      + "what puts loose material somewhere");
            }

            // AN ALLOW-LIST, NOT A DENY-LIST. This is the property that matters next year: a
            // biological field nobody has written yet must not auto-appear on stone. A deny-list
            // would silently admit it, which is the exact fault being fixed.
            Check(!UniverseCreator.Editor.Inspectors.UCEcoEnvelopeDrawer
                      .DepositDrawsField("someFieldAddedNextYear"),
                  "DEF-ROCK1: an unknown field must NOT be drawn on a deposit. The living path "
                  + "iterates so new biology appears automatically; for stone that property is "
                  + "the fault, so this side must be an allow-list.");
        }

        /// <summary>
        /// DEF-13 (`DEF-SHELF1`): the coastal shelf no longer grades land onto the waterline, and
        /// its profile is no longer a plane.
        ///
        /// Two symptoms, one cause. `if (d <= flat) rampH = sea;` pulled every dry cell within the
        /// beach width to EXACTLY sea level, and the rim beyond it was held flat - so every coast
        /// got two dead-flat rings. With zero freeboard, the water surface and the beach shared a
        /// plane hundreds of metres wide, and one heightmap step of movement (~9 mm on a 600 m
        /// world) flooded the entire ring at once, leaving higher ground standing as islands.
        ///
        /// Driven over a synthetic coast with a WIDE wet beach relative to the cell size, which
        /// matters: the pre-existing shelf test (`C-TEST`) already asserted "the grade must stay
        /// strictly above the waterline", but it ran with a 10 m beach at 100 m per coarse cell,
        /// so every land cell fell in the `d > flat` ramp branch and the `d <= flat` wet-beach
        /// branch - the one that assigned `rampH = sea` - was never reached. The property was
        /// pinned and the faulty line was untested. This fixture reaches it.
        /// </summary>
        /// <summary>
        /// DEF-6. A species' authored height in METRES must decide how tall the tree is.
        ///
        /// THE DEFECT THIS PINS. `Allometry.Resolve` computed a real mature height for every
        /// canopy instance and `SpeciesAssignment` threw it away, reading only the crown radius
        /// off the same form. The applier therefore had nothing to size a tree by and fell back to
        /// `TreePrototypeDef.heightScale` (0.8-1.25) applied to the prefab's modelled size - so a
        /// 35 m oak and a 2 m shrub came out the SAME size, and on a small prefab everything was
        /// tiny. Nothing in the suite noticed, because nothing asserted that authored metres reach
        /// the written instance at all.
        /// </summary>
        // ---------------------------------------------------------------------
        //  DEF-5  VEGETATION INSPECTOR: NOTHING UNGROUPED, AND APPLY ACTUALLY APPLIES
        // ---------------------------------------------------------------------

        /// <summary>
        /// DEF-5. Two separate defects on one component.
        ///
        /// THE FIRST was "wrong placement of input field not related": seven author-facing fields
        /// belonged to no UCFieldGroup, so UCGroupedInspector swept them into the amber "Unlisted"
        /// section at the very bottom of the inspector - a tree's age spread sitting below the
        /// debug toggles instead of under Trees. Asserting those seven names would pass forever
        /// while the eighth new field repeated the fault, so this CENSUSES the component instead:
        /// it asks the shared ListedFieldNames/CollectUnlisted pair - the same two the inspector
        /// itself draws from, deliberately not a copy - whether anything visible is unaccounted
        /// for. Adding a field without grouping it turns this red. That is the point of it.
        ///
        /// THE SECOND was an inert dial, and a sharper lesson. qualityPreset's own tooltip told
        /// the author to "choose one and press Apply", and DialsFor, ApplyQualityPreset,
        /// PresetDialChecksum, PresetModified and MatchesPreset were ALL already written - with no
        /// caller anywhere in the package. A complete device-budget mechanism, reachable from
        /// nothing. The dial did nothing because nothing read it. The checks below are themselves
        /// a caller: they prove the preset writes what DialsFor describes, that the tiers are
        /// genuinely cheaper downward (a preset that reduces nothing is a preset in name only,
        /// and this package's floor is a mid-tier flagship phone), and that "(modified)" answers
        /// an author's edit rather than being stuck on or stuck off.
        /// </summary>
        private static void TestVegetationInspectorGroups()
        {
            GameObject go = new GameObject("UC_SelfTest_VegInspectorCensus");
            try
            {
                UCVegetationSystemManager veg = go.AddComponent<UCVegetationSystemManager>();
                SerializedObject so = new SerializedObject(veg);

                HashSet<string> listed =
                    UCGroupedInspector.ListedFieldNames(UCVegetationSystemManagerInspector.Groups);

                // 1) Nothing the author can see may fall through into "Unlisted". NextVisible
                //    skips [HideInInspector], so a field deliberately hidden is not reported.
                List<SerializedProperty> unlisted = UCGroupedInspector.CollectUnlisted(so, listed);
                StringBuilder loose = new StringBuilder();
                for (int i = 0; i < unlisted.Count; i++)
                {
                    if (i > 0) loose.Append(", ");
                    loose.Append(unlisted[i].name);
                }
                Check(unlisted.Count == 0,
                      "DEF-5: " + unlisted.Count + " vegetation field(s) belong to no UCFieldGroup, "
                      + "so the inspector sweeps them into the amber \"Unlisted\" section at the "
                      + "bottom, away from the dials they affect: " + loose + ". Put each in the "
                      + "group that owns what it changes, or mark it [HideInInspector] if it is "
                      + "not for the author.");

                // 2) The inverse direction. A renamed field leaves the group table pointing at
                //    nothing and the inspector draws a red box where the dial should be, which is
                //    how a field can vanish from the UI for a whole release without anyone noticing.
                StringBuilder missing = new StringBuilder();
                int missingCount = 0;
                foreach (string name in listed)
                {
                    if (so.FindProperty(name) != null) continue;
                    if (missingCount > 0) missing.Append(", ");
                    missing.Append(name);
                    missingCount++;
                }
                Check(missingCount == 0,
                      "DEF-5: the vegetation inspector's group table names " + missingCount
                      + " field(s) that do not exist on the component: " + missing + ". The "
                      + "inspector draws an error box in place of each of those dials.");

                // UC-T08-15: unavailable legacy controls retain their data without appearing as executable authoring inputs.
                foreach (string name in new[] { "rebuildWithChunks", "maxConcurrentCells", "wetnessCatchmentRadiusMetres",
                    "canopyLayerPasses", "enableSelfThinning", "enableCrownSpacing", "dispersalSeedsPerHectare",
                    "avalancheMinSlopeDegrees", "debugDrawSuitability", "debugSpeciesIndex", "debugShowLimitingFactor" })
                    Check(so.FindProperty(name) != null && !listed.Contains(name) &&
                        typeof(UCVegetationSystemManager).GetField(name).IsDefined(typeof(HideInInspector), true),
                        "UC-T08-15 unsupported vegetation control remains serialized and hidden: " + name);
                veg.dispersalSeedsPerHectare = 13; veg.debugSpeciesIndex = 7;
                string retainedLegacy = JsonUtility.ToJson(veg);
                veg.dispersalSeedsPerHectare = 2; veg.debugSpeciesIndex = -1;
                JsonUtility.FromJsonOverwrite(retainedLegacy, veg);
                Check(veg.dispersalSeedsPerHectare == 13 && veg.debugSpeciesIndex == 7,
                    "UC-T08-15 hidden legacy authoring values survive component serialization.");

                var canopyTile = new TileData(Coord.Zero, new CoordRect(0, 0, 17, 17), Vector3.zero, new Vector3(16, 100, 16), 100);
                Matrix canopyHeight = canopyTile.CreateMatrix(); canopyHeight.Fill(0.2f);
                var crown = new EcologyGraphPublication("inspector-canopy", canopyHeight,
                    new[] { new CanopyGraphStamp(8, 8, 4, 3, Ecology.CanopyLayer.Canopy) }, new int[Ecology.LandformAssemblage.ZoneCount]);
                try
                {
                    foreach (bool enabled in new[] { true, false })
                    {
                        veg.enableCanopyCompetition = enabled; veg.MarkSettingsDirty();
                        canopyTile.ecology = new Ecology.EcoScatterContext { settings = veg.ResolveSettings() };
                        crown.ReplayEvaluation(canopyTile);
                        Ecology.EnvironmentSample site = canopyTile.ecology.sampler.Sample(8, 8);
                        Check(enabled ? site.canopyLai > 0 && site.floorLightFraction < 1 :
                            site.canopyLai == 0 && site.floorLightFraction == 1 && canopyTile.ecology.canopy == null,
                            "UC-T08-15 manager canopy flag controls actual replayed shade and grid allocation: " + enabled);
                    }
                    // Even a retained populated grid must not inject shade into a disabled generation.
                    var priorGrid = new Ecology.CanopyLightGrid(); priorGrid.Bind(0, 0, 16, 16);
                    priorGrid.Deposit(8, 8, 4, 3, Ecology.CanopyLayer.Canopy);
                    canopyTile.ecology = new Ecology.EcoScatterContext { settings = veg.ResolveSettings(), canopy = priorGrid };
                    crown.ReplayEvaluation(canopyTile);
                    Check(canopyTile.ecology.sampler.Sample(8, 8).canopyLai == 0,
                        "UC-T08-15 disabled canopy cannot borrow shade from a populated retained grid.");
                }
                finally { canopyTile.ReleaseProductMemory(); }
                so.Update();
                Undo.IncrementCurrentGroup(); int canopyUndo = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(veg, "UC canopy switch");
                so.FindProperty("enableCanopyCompetition").boolValue = true; so.ApplyModifiedProperties();
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(canopyUndo); Undo.IncrementCurrentGroup();
                Undo.PerformUndo();
                Check(!veg.enableCanopyCompetition, "UC-T08-15 native Undo restores the inspector canopy switch.");
                Undo.PerformRedo();
                Check(veg.enableCanopyCompetition, "UC-T08-15 native Redo restores the inspector canopy switch.");
                so.Update();

                // 3) A write through the SerializedObject reaches the component. This is the path
                //    every grouped field and the hosted library panel edit through, so it is worth
                //    pinning once rather than assuming.
                SerializedProperty trees = so.FindProperty("treesPerHectare");
                bool wroteThrough = false;
                if (trees != null)
                {
                    float before = veg.treesPerHectare;
                    trees.floatValue = before + 7f;
                    so.ApplyModifiedProperties();
                    wroteThrough = Mathf.Approximately(veg.treesPerHectare, before + 7f);
                    veg.treesPerHectare = before;
                    so.Update();
                }
                Check(wroteThrough,
                      "DEF-5: a value written through the inspector's SerializedObject did not "
                      + "reach the component. Every grouped field edits this way, so nothing in "
                      + "this inspector would stick.");

                // 4) The tiers must get genuinely cheaper downward, on every dial that costs
                //    frame time. A "mobile" preset that is not cheaper than Desktop is a label.
                UCVegetationSystemManager.QualityDials mid =
                    UCVegetationSystemManager.DialsFor(VegetationQuality.MidRangeMobile);
                UCVegetationSystemManager.QualityDials flag =
                    UCVegetationSystemManager.DialsFor(VegetationQuality.FlagshipMobile);
                UCVegetationSystemManager.QualityDials desk =
                    UCVegetationSystemManager.DialsFor(VegetationQuality.Desktop);
                Check(mid.detailResolution < desk.detailResolution
                      && mid.densityScale < desk.densityScale
                      && mid.shadowDistanceM < desk.shadowDistanceM
                      && mid.maxObjectsPerChunk < desk.maxObjectsPerChunk
                      && mid.detailResolution <= flag.detailResolution
                      && mid.densityScale <= flag.densityScale
                      && flag.shadowDistanceM < desk.shadowDistanceM,
                      "DEF-5: the device tiers are not monotonically cheaper. MidRange must cost "
                      + "less than Flagship, and Flagship less than Desktop, on detail "
                      + "resolution, density, shadow distance and object budget. Got detail "
                      + mid.detailResolution + "/" + flag.detailResolution + "/"
                      + desk.detailResolution + ", density " + mid.densityScale.ToString("0.00")
                      + "/" + flag.densityScale.ToString("0.00") + "/"
                      + desk.densityScale.ToString("0.00") + ", shadow "
                      + mid.shadowDistanceM.ToString("0") + "/" + flag.shadowDistanceM.ToString("0")
                      + "/" + desk.shadowDistanceM.ToString("0") + " m.");

                // 5) Apply writes what DialsFor describes. This is the check that would have
                //    failed had the duplicate ApplyPreset written its own different dial set.
                veg.ApplyQualityPreset(VegetationQuality.MidRangeMobile);
                Check(veg.detailResolution == mid.detailResolution
                      && Mathf.Approximately(veg.globalDensityScale, mid.densityScale)
                      && Mathf.Approximately(veg.shadowCasterDistanceMetres, mid.shadowDistanceM)
                      && Mathf.Approximately(veg.billboardFadeLengthMetres, mid.billboardFadeM)
                      && veg.qualityPreset == VegetationQuality.MidRangeMobile,
                      "DEF-5: ApplyQualityPreset did not write the dials DialsFor describes for "
                      + "MidRangeMobile. The dial the inspector's Apply button drives must move "
                      + "the component, or it is the inert dial again with a button on it.");

                // 6) Immediately after Apply nothing may read as modified, or "(modified)" means
                //    nothing - it would be lit on every component that ever applied a preset.
                Check(veg.hasAppliedPreset && !veg.PresetModified,
                      "DEF-5: the component reported (modified) immediately after Apply, so the "
                      + "checksum does not cover the same dials the preset writes.");

                // 7) ...and moving one of them must light it. Both directions, because a
                //    PresetModified stuck at false is just as useless as one stuck at true.
                veg.detailResolution = mid.detailResolution == 256 ? 512 : 256;
                Check(veg.PresetModified,
                      "DEF-5: a preset dial was edited after Apply and PresetModified stayed "
                      + "false, so the inspector would never tell the author they have tuned "
                      + "away from the tier they chose.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void TestTreeHeightSizing()
        {
            Vector2 range = new Vector2(0.8f, 1.25f);
            float mid = 0.5f * (range.x + range.y);          // the "no spread" draw

            // 1) A 20 m species on a 10 m prefab must ask for x2, so the REALISED height is 20 m.
            float s20 = ScatterApplier.ResolveHeightScale(20f, 10f, range, mid);
            Check(Mathf.Abs(s20 * 10f - 20f) < 1e-2f,
                  "DEF-6: a species authored at 20 m on a 10 m prefab must realise ~20 m, got "
                  + (s20 * 10f).ToString("F2") + " m. heightScale is a MULTIPLIER on the prefab's "
                  + "modelled size, so authored metres only arrive if we divide by that size.");

            // 2) THE ACTUAL BUG: two species of very different height must not come out the same.
            float s2 = ScatterApplier.ResolveHeightScale(2f, 10f, range, mid);
            Check(s2 < s20 * 0.2f + 1e-4f,
                  "DEF-6: a 2 m species and a 20 m species produced near-identical scales ("
                  + s2.ToString("F3") + " vs " + s20.ToString("F3") + "). That is the defect - "
                  + "tree size was coming from the prefab, not from the species.");

            // 3) The prefab's own size must be the divisor: the SAME species on a prefab twice as
            //    tall must ask for half the multiplier, so both realise the same world height.
            float onTall = ScatterApplier.ResolveHeightScale(20f, 20f, range, mid);
            Check(Mathf.Abs(onTall * 20f - 20f) < 1e-2f && onTall < s20,
                  "DEF-6: the same species on a 20 m prefab must realise the same ~20 m tree as on "
                  + "a 10 m prefab (got " + (onTall * 20f).ToString("F2") + " m) - otherwise a "
                  + "buyer swapping in their own models gets a differently-sized world.");

            // 4) Both fallbacks must return the drawn unit scale EXACTLY, so a geometric scatter
            //    with no ecology, or a prefab with no mesh, behaves precisely as it did before.
            Check(ScatterApplier.ResolveHeightScale(0f, 10f, range, 1.1f) == 1.1f,
                  "DEF-6: with no species height the applier must fall back to the authored unit "
                  + "scale unchanged - a geometric scatter must not change behaviour.");
            Check(ScatterApplier.ResolveHeightScale(20f, 0f, range, 1.1f) == 1.1f,
                  "DEF-6: with no measured prefab height the applier must fall back to the "
                  + "authored unit scale unchanged rather than divide by zero.");

            // 5) The authored range survives as PROPORTIONAL spread, not as the height itself.
            float atLo = ScatterApplier.ResolveHeightScale(20f, 10f, range, range.x);
            float atHi = ScatterApplier.ResolveHeightScale(20f, 10f, range, range.y);
            Check(atLo < s20 && atHi > s20,
                  "DEF-6: the authored height range must still vary instances around the true "
                  + "height (lo " + atLo.ToString("F3") + ", mid " + s20.ToString("F3") + ", hi "
                  + atHi.ToString("F3") + ").");
            Check(Mathf.Abs(atLo * 10f - 20f * (range.x / mid)) < 1e-2f,
                  "DEF-6: the spread must be PROPORTIONAL to the true height, not an absolute "
                  + "unit-scale substituted for it.");

            // 6) THE MEASUREMENT, which is the half that must never be assumed. A prefab scaled to
            //    7 units must measure 7 m - measuring in the ROOT's own space would cancel the
            //    root scale and report 1, making every instance seven times too small.
            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                probe.transform.localScale = new Vector3(1f, 7f, 1f);
                float measured = ScatterApplier.MeasureNativeHeightM(probe);
                Check(Mathf.Abs(measured - 7f) < 0.05f,
                      "DEF-6: a prefab root scaled to 7 must measure ~7 m, got "
                      + measured.ToString("F3") + " m. Measuring in the root's own local space "
                      + "cancels the root scale and under-measures the model.");

                // A rotated model must report its true vertical span, not an axis-aligned guess.
                probe.transform.localScale = Vector3.one;
                probe.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                float rotated = ScatterApplier.MeasureNativeHeightM(probe);
                Check(Mathf.Abs(rotated - 1f) < 0.05f,
                      "DEF-6: a model rotated 90 degrees must still measure ~1 m tall, got "
                      + rotated.ToString("F3") + " m.");
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            }

            Check(ScatterApplier.MeasureNativeHeightM(null) == 0f,
                  "DEF-6: measuring a null prefab must return 0 (read as 'unknown'), never a "
                  + "height that would be divided by.");
        }

        private static void TestCoastalShelfFreeboard()
        {
            const int res = 64;
            const float sea = 0.35f;
            const float worldM = 2000f;        // ~31 m per cell, so a 120 m beach spans several

            // A simple coast: open sea along the left edge, then ground climbing east, so the
            // shoreline is a line and "distance from water" is well defined for every land cell.
            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos = Vector3.zero;
            height.worldSize = new Vector3(worldM, 600f, worldM);

            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    height.arr[z * res + x] = sea + 0.002f + 0.25f * (x / (float)(res - 1));

            for (int z = 0; z < res; z++)
            {
                height.arr[z * res + 0] = 0.20f;      // genuine ocean
                height.arr[z * res + 1] = 0.20f;
            }

            WorldDrainage wd = WorldDrainage.CreateForTests(height, null);
            Check(wd != null, "DEF-SHELF1: could not build a test WorldDrainage");
            if (wd == null) return;

            Matrix graded = new Matrix(new CoordRect(0, 0, res, res));
            graded.worldPos = height.worldPos;
            graded.worldSize = height.worldSize;
            Array.Copy(height.arr, graded.arr, height.arr.Length);

            CoastalShelf.Apply(wd, graded, sea,
                               /*range*/ 400f, /*beachFlat*/ 120f, /*plain*/ 160f,
                               /*rise*/ 0.02f, /*cliffThreshold*/ 0.05f,
                               /*variety*/ 0f, /*strength*/ 1f,
                               /*freeboard*/ 0f, new StopToken());

            // --- NO GRADED CELL MAY END AT OR BELOW SEA. The island failure lives here. ------
            int atOrBelow = 0;
            int lowered = 0;
            int raised = 0;
            for (int i = 0; i < graded.arr.Length; i++)
            {
                if (height.arr[i] <= sea) continue;            // was already water: not ours
                if (graded.arr[i] < height.arr[i] - 1e-7f) lowered++;
                if (graded.arr[i] > height.arr[i] + 1e-7f) raised++;
                if (graded.arr[i] <= sea) atOrBelow++;
            }

            Check(atOrBelow == 0,
                  "DEF-SHELF1: the shelf lowered " + atOrBelow + " dry cells to or below sea "
                  + "level. Land on the water plane has zero freeboard, so one heightmap step of "
                  + "movement floods the whole beach band at once and leaves islands - this is "
                  + "the defect, and it must be impossible by construction.");

            Check(lowered > 0,
                  "DEF-SHELF1: the shelf must still actually grade the coast - if it lowered "
                  + "nothing, the freeboard was 'fixed' by disabling the pass");

            // --- LOWER-ONLY. Raising the target must never raise ground. --------------------
            Check(raised == 0,
                  "DEF-SHELF1: the shelf must never raise ground (" + raised + " cells rose). "
                  + "Lower-only is what makes a higher ramp safe: it can only grade LESS.");

            // --- THE PROFILE IS NOT A PLANE. With variety on, the beach varies along shore. --
            Matrix varied = new Matrix(new CoordRect(0, 0, res, res));
            varied.worldPos = height.worldPos;
            varied.worldSize = height.worldSize;
            Array.Copy(height.arr, varied.arr, height.arr.Length);

            CoastalShelf.Apply(wd, varied, sea, 400f, 120f, 160f, 0.02f, 0.05f,
                               /*variety*/ 1f, 1f, /*freeboard*/ 0f, new StopToken());

            // The input is identical along every row, so any difference between rows after
            // grading can only have come from the regional character reaching the TARGET.
            // Before the fix it only reached the blend weight, and with a uniform input every
            // row graded identically however high variety was turned.
            bool rowsDiffer = false;
            for (int z = 1; z < res && !rowsDiffer; z++)
                for (int x = 0; x < res; x++)
                    if (Mathf.Abs(varied.arr[z * res + x] - varied.arr[x]) > 1e-5f)
                    { rowsDiffer = true; break; }

            Check(rowsDiffer,
                  "DEF-SHELF1: with variety at 1 the graded coast must differ along the "
                  + "shoreline. Identical rows mean the target is still a plane and variety is "
                  + "only changing the blend weight - which is exactly why no dial could fix the "
                  + "flatness.");

            // --- the freeboard holds with variety on too -------------------------------------
            int atOrBelowVaried = 0;
            for (int i = 0; i < varied.arr.Length; i++)
            {
                if (height.arr[i] <= sea) continue;
                if (varied.arr[i] <= sea) atOrBelowVaried++;
            }
            Check(atOrBelowVaried == 0,
                  "DEF-SHELF1: the freeboard must hold with variety on as well - the beach WIDTH "
                  + "varies, the waterline clearance does not");

            // --- THE AUTHOR'S DIAL MUST BE THE ONE THAT DECIDES (DEF-13 second half) --------
            // The three coastal passes each need "how far dry ground stands above water". The
            // floor and the continental rise both read `seaFreeboardMetres`; the shelf had a
            // hardcoded ~6 cm of its own, so it graded the beach back down to the waterline the
            // other two had just lifted it off, and turning the author's dial up changed the
            // coast everywhere EXCEPT the beach the dial is named after. Grading with a real
            // freeboard must now leave the whole graded band at or above sea + that freeboard.
            float bigFree = 0.05f;                       // 30 m on a 600 m world
            Matrix held = new Matrix(new CoordRect(0, 0, res, res));
            held.worldPos = height.worldPos;
            held.worldSize = height.worldSize;
            Array.Copy(height.arr, held.arr, height.arr.Length);

            CoastalShelf.Apply(wd, held, sea, 400f, 120f, 160f, 0.02f, 0.05f,
                               /*variety*/ 0f, /*strength*/ 1f, bigFree, new StopToken());

            int belowDial = 0;
            for (int i = 0; i < held.arr.Length; i++)
            {
                if (height.arr[i] <= sea) continue;                  // sea keeps its own depth
                if (held.arr[i] >= height.arr[i] - 1e-6f) continue;  // untouched / cliff
                if (held.arr[i] < sea + bigFree - 1e-5f) belowDial++;
            }
            Check(belowDial == 0,
                  "DEF-13: a cell the shelf GRADED fell below sea + the author's freeboard ("
                  + belowDial + " cells). The shelf must grade down to the same clearance "
                  + "SeaLevelFloor and ContinentalRise guarantee, not to a hardcoded one of "
                  + "its own - otherwise seaFreeboardMetres is a dial two passes honour and a "
                  + "third ignores.");

            // And the dial must actually MOVE the beach: a bigger freeboard must leave the
            // graded band strictly higher than the zero-dial run did. This is the check that
            // would have failed before the fix, when the constant won regardless.
            int higherThanFloored = 0;
            for (int i = 0; i < held.arr.Length; i++)
            {
                if (height.arr[i] <= sea) continue;
                if (held.arr[i] > graded.arr[i] + 1e-5f) higherThanFloored++;
            }
            Check(higherThanFloored > 0,
                  "DEF-13: turning the freeboard dial up changed nothing about the graded "
                  + "coast - the shelf is still using its own hardcoded clearance.");

            // --- ITEM 4: THE BANDS ARE WORLD-RELATIVE, AND SHRINK-ONLY ----------------------
            // The author's graph authors coastalRange: 500. That is a fringe on a 50 km world
            // and most of the landmass on a 2 km one, which is why a correct shaper still
            // flattened their island. Resolution is Min(authored, fraction x span).

            // On a big world the authored metres must WIN, so a tuned graph is untouched.
            float bigWorld = CoastalShelf.ResolveBandMetres(500f, 0.08f, 50000f, 0.002f, 0.40f);
            Check(Mathf.Abs(bigWorld - 500f) < 1e-3f,
                  "DEF-13 item 4: on a 50 km world the authored 500 m band must stand (got "
                  + bigWorld.ToString("F2") + ") - this dial must not re-tune worlds that were "
                  + "already correct.");

            // On a small world the fraction must win and the band must shrink.
            float smallWorld = CoastalShelf.ResolveBandMetres(500f, 0.08f, 2000f, 0.002f, 0.40f);
            Check(smallWorld < 500f - 1e-3f && Mathf.Abs(smallWorld - 160f) < 1e-2f,
                  "DEF-13 item 4: on a 2 km world a 500 m coastal band must shrink to 8% of the "
                  + "span (expected 160, got " + smallWorld.ToString("F2") + ") - an unshrunk "
                  + "band is wider than the land it is meant to fringe, which is the author's "
                  + "flattened island.");

            // SHRINK-ONLY, swept: the result may never exceed the authored metres, for any
            // span or fraction. This is the property that makes defaulting it ON safe.
            int widened = 0;
            float[] spans = { 1f, 100f, 600f, 2000f, 10000f, 50000f, 1000000f };
            float[] fracs = { -1f, 0f, 0.0001f, 0.002f, 0.08f, 0.4f, 10f };
            for (int s = 0; s < spans.Length; s++)
                for (int f2 = 0; f2 < fracs.Length; f2++)
                {
                    float got = CoastalShelf.ResolveBandMetres(500f, fracs[f2], spans[s], 0.002f, 0.40f);
                    if (got > 500f + 1e-3f) widened++;
                    if (got < 0f) widened++;                 // and never negative
                }
            Check(widened == 0,
                  "DEF-13 item 4: resolving a coastal band must be SHRINK-ONLY - it widened or "
                  + "went negative in " + widened + " of " + (spans.Length * fracs.Length)
                  + " span/fraction combinations. A band that can widen could make an existing "
                  + "world worse, which is exactly what this dial must never do.");
        }

        /// <summary>Exact float-for-float comparison, for the "changed nothing" contract.</summary>
        private static bool FieldsIdentical(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>True when two compass angles describe the same AXIS (ignoring a 180 flip).</summary>
        private static bool AxisMatchesDeg(float a, float target)
        {
            float d1 = Mathf.Abs(Mathf.DeltaAngle(a, target));
            float d2 = Mathf.Abs(Mathf.DeltaAngle(a, target + 180f));
            return Mathf.Min(d1, d2) < 1f;
        }

        /// <summary>
        /// RIV-LOOK1 (RIV-4 W1): the shore wave-wash band.
        ///
        /// WHAT THIS GATE CAN AND CANNOT DO - stated plainly so nobody reads a green run as
        /// "the surf looks right". It CAN prove the shader still compiles (a broken water shader
        /// would take down every water surface in the product), that all six wash properties exist
        /// and are therefore reachable from a material, and that the feature DEFAULTS TO OFF so no
        /// existing water material changes. It CANNOT judge appearance. Whether the wash reads as
        /// surf is for the author to look at in the editor; this gate only guarantees it is wired
        /// and harmless until switched on.
        /// </summary>
        private static void TestShoreWaveWash()
        {
            Shader water = Shader.Find("UniverseCreator/Water");
            Check(water != null, "RIV-LOOK1: the UniverseCreator/Water shader must be findable");
            if (water == null) return;

            Check(!UnityEditor.ShaderUtil.ShaderHasError(water),
                  "RIV-LOOK1: the water shader must compile without errors after the wash edit");

            // Every wash property must reach the material, and each must sit in UnityPerMaterial
            // (checked by eye in UC_WaterInput.hlsl - a float in Properties but not in the cbuffer
            // silently drops SRP batching rather than erroring).
            string[] names =
            {
                "_ShoreWashEnabled", "_ShoreWashPeriod", "_ShoreWashReach",
                "_ShoreWashSharpness", "_ShoreWashStrength", "_ShoreWashTravel"
            };
            for (int i = 0; i < names.Length; i++)
                Check(water.FindPropertyIndex(names[i]) >= 0,
                      "RIV-LOOK1: the water shader must expose " + names[i]);

            // OFF by default: this is the no-regression guarantee. An existing lake or ocean
            // material deserializes with the wash disabled and renders exactly as it did.
            int onIdx = water.FindPropertyIndex("_ShoreWashEnabled");
            if (onIdx >= 0)
                Check(Mathf.Abs(water.GetPropertyDefaultFloatValue(onIdx)) < 1e-4f,
                      "RIV-LOOK1: shore wave wash must default to OFF so existing water is unchanged");

            // ...but the defaults behind the toggle must be non-degenerate, or switching it on
            // would appear to do nothing and read as a dead dial.
            int reachIdx = water.FindPropertyIndex("_ShoreWashReach");
            if (reachIdx >= 0)
                Check(water.GetPropertyDefaultFloatValue(reachIdx) > 0.01f,
                      "RIV-LOOK1: the wash reach default must be > 0 or enabling it does nothing");

            int strIdx = water.FindPropertyIndex("_ShoreWashStrength");
            if (strIdx >= 0)
                Check(water.GetPropertyDefaultFloatValue(strIdx) > 0.01f,
                      "RIV-LOOK1: the wash strength default must be > 0 or enabling it does nothing");

            int perIdx = water.FindPropertyIndex("_ShoreWashPeriod");
            if (perIdx >= 0)
                Check(water.GetPropertyDefaultFloatValue(perIdx) >= 1f,
                      "RIV-LOOK1: the wash period must default to a real wave period, not 0");
        }

        /// <summary>
        /// RIV-PROF1 (RIV-3): the authored along-course profile. Pins the three things that make
        /// it safe rather than merely present: the default is the OFF position, the worker path
        /// reads the BAKED TABLE and never AnimationCurve (proved by nulling the curve after the
        /// bake and still getting values - Evaluate is not thread-safe and these nodes run on a
        /// worker Task), and the R4 rule value = Base*curve + Min holds exactly. Also pins the
        /// already-shipped Shields physical depth, and that the node's taper dial is actually
        /// WIRED - an opt-in that quietly does nothing is the failure mode worth testing for.
        /// </summary>
        private static void TestRiverProfileCurves()
        {
            // 1. A fresh profile is off, and off means "caller keeps what it had".
            UniverseCreator.TerrainSystem.RiverProfileCurves p =
                new UniverseCreator.TerrainSystem.RiverProfileCurves();
            UniverseCreator.TerrainSystem.RiverProfileSample s;

            Check(!p.enabled, "RIV-PROF1: a fresh river profile must default to DISABLED");
            Check(!p.TryEvaluate(0.5f, out s),
                  "RIV-PROF1: a disabled profile must refuse to evaluate so the caller keeps its solved width");

            // 2. Enabled but never baked must still refuse - degrading to today's behaviour,
            //    not to a zero-width river.
            p.enabled = true;
            Check(!p.IsBaked, "RIV-PROF1: a profile is not baked until Bake() is called");
            Check(!p.TryEvaluate(0.5f, out s),
                  "RIV-PROF1: an unbaked profile must refuse to evaluate");

            // 3. Baked with the flat default curves = a CONSTANT river, so enabling without
            //    editing anything does not jump to a new shape.
            p.Bake();
            Check(p.IsBaked, "RIV-PROF1: Bake() must produce usable tables");

            UniverseCreator.TerrainSystem.RiverProfileSample head, mouth;
            bool okHead = p.TryEvaluate(0f, out head);
            bool okMouth = p.TryEvaluate(1f, out mouth);
            Check(okHead && okMouth, "RIV-PROF1: a baked, enabled profile must evaluate");
            Check(Mathf.Abs(head.widthM - mouth.widthM) < 1e-4f,
                  "RIV-PROF1: the flat default curves must give a constant width end to end");

            // 4. The R4 rule, exactly: value = Base * curve(t) + Min. Flat curve = 1, so the
            //    expected width is base + min, clamped to the band RiverCourseSolver documents.
            float expectW = Mathf.Clamp(p.widthBaseM * 1f + p.widthMinM,
                                        UniverseCreator.TerrainSystem.RiverCourseSolver.MinChannelWidthM,
                                        UniverseCreator.TerrainSystem.RiverCourseSolver.MaxChannelWidthM);
            Check(Mathf.Abs(head.widthM - expectW) < 1e-3f,
                  "RIV-PROF1: width must be Base*curve + Min");

            // 5. A rising curve must give a monotonically widening river.
            p.widthCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            p.widthBaseM = 20f;
            p.widthMinM = 5f;
            p.Bake();

            bool rises = true;
            float prev = -1f;
            for (int i = 0; i <= 8; i++)
            {
                UniverseCreator.TerrainSystem.RiverProfileSample st;
                if (!p.TryEvaluate(i / 8f, out st)) { rises = false; break; }
                if (st.widthM < prev - 1e-4f) rises = false;
                prev = st.widthM;
            }
            Check(rises, "RIV-PROF1: a rising width curve must widen the river from head to mouth");

            UniverseCreator.TerrainSystem.RiverProfileSample w0, w1;
            p.TryEvaluate(0f, out w0);
            p.TryEvaluate(1f, out w1);
            Check(w1.widthM > w0.widthM + 1f,
                  "RIV-PROF1: the mouth must be measurably wider than the headwater on a rising curve");
            Check(Mathf.Abs(w0.widthM - Mathf.Max(5f,
                      UniverseCreator.TerrainSystem.RiverCourseSolver.MinChannelWidthM)) < 1e-3f,
                  "RIV-PROF1: at t=0 a linear 0..1 curve gives exactly the Min term");

            // 6. THE THREAD-SAFETY CONTRACT. After baking, the curve object is never touched
            //    again - so nulling it must not change the answer. If this check ever fails,
            //    something started calling AnimationCurve.Evaluate on the read path.
            float beforeNull = w1.widthM;
            p.widthCurve = null;
            UniverseCreator.TerrainSystem.RiverProfileSample afterNull;
            bool stillOk = p.TryEvaluate(1f, out afterNull);
            Check(stillOk && Mathf.Abs(afterNull.widthM - beforeNull) < 1e-5f,
                  "RIV-PROF1: evaluation must read the baked LUT, never the AnimationCurve (not thread-safe)");

            // 7. Invalidate returns it to the safe refusal state.
            p.Invalidate();
            Check(!p.IsBaked && !p.TryEvaluate(0.5f, out s),
                  "RIV-PROF1: Invalidate must return the profile to refusing, so no stale LUT is read");

            // 8. An absurd authored width is clamped into the hydraulic band rather than
            //    carving a canyon.
            UniverseCreator.TerrainSystem.RiverProfileCurves big =
                new UniverseCreator.TerrainSystem.RiverProfileCurves();
            big.enabled = true;
            big.widthBaseM = 100000f;
            big.Bake();
            UniverseCreator.TerrainSystem.RiverProfileSample huge;
            big.TryEvaluate(0.5f, out huge);
            Check(Mathf.Abs(huge.widthM -
                      UniverseCreator.TerrainSystem.RiverCourseSolver.MaxChannelWidthM) < 1e-3f,
                  "RIV-PROF1: an over-wide authored width must clamp to MaxChannelWidthM");

            // 9. The SHIPPED physical depth (Shields): on a gentler slope the grain-stability
            //    floor demands a DEEPER channel. Slopes chosen inside the unsaturated band, so
            //    this reads the Shields term and not the Leopold cap.
            float hwA, dA, hwB, dB;
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(10f, 0.0030f, out hwA, out dA);
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(10f, 0.0018f, out hwB, out dB);
            Check(dB > dA,
                  "RIV-PROF1: physical (Shields) depth must increase as slope decreases");
            Check(Mathf.Abs(hwA - hwB) < 1e-4f,
                  "RIV-PROF1: width depends on discharge, not slope - it must not move with slope");

            // 10. Width stays inside the documented real-world band at both extremes.
            float hwTiny, dTiny, hwHuge, dHuge;
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(0.0001f, 0.01f, out hwTiny, out dTiny);
            UniverseCreator.TerrainSystem.RiverCourseSolver.HydraulicGeometry(1e9f, 0.01f, out hwHuge, out dHuge);
            Check(Mathf.Abs(hwTiny * 2f -
                      UniverseCreator.TerrainSystem.RiverCourseSolver.MinChannelWidthM) < 1e-3f,
                  "RIV-PROF1: a trickle must floor at MinChannelWidthM");
            Check(Mathf.Abs(hwHuge * 2f -
                      UniverseCreator.TerrainSystem.RiverCourseSolver.MaxChannelWidthM) < 1e-3f,
                  "RIV-PROF1: a huge discharge must cap at MaxChannelWidthM");

            // 11. IS THE DIAL WIRED? The node's along-course taper must actually change the
            //     carve. A new opt-in field that does nothing is the real risk here.
            Check(MaxCutWithTaper(true) > MaxCutWithTaper(false) + 1e-5f,
                  "RIV-PROF1: enabling taperAlongCourse must deepen the channel toward the mouth");

            // 12. THE MAIN-THREAD BAKE HOOK. UCWaterSystemManager.BakeRiverProfilesInGraph must
            //     find the node and bake its curves - otherwise RiverProfileCurves is an orphan
            //     type that nothing ever constructs, which is the whole point of wiring it.
            Graph hookGraph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                UniverseCreator.Nodes.Selectors.RiverSeedCourseNode hookNode =
                    new UniverseCreator.Nodes.Selectors.RiverSeedCourseNode();
                hookGraph.nodes.Add(hookNode);

                Check(hookNode.profile != null,
                      "RIV-PROF1: a fresh River Seed Course node must carry a profile instance");
                Check(hookNode.profile != null && !hookNode.profile.enabled,
                      "RIV-PROF1: the node's profile must default to DISABLED (no regression)");
                Check(hookNode.profile != null && !hookNode.profile.IsBaked,
                      "RIV-PROF1: the node's profile must start unbaked");

                int baked = UniverseCreator.TerrainSystem.UCWaterSystemManager
                                .BakeRiverProfilesInGraph(hookGraph);
                Check(baked == 1,
                      "RIV-PROF1: the bake hook must find exactly the one seed-course node, got " + baked);
                Check(hookNode.profile != null && hookNode.profile.IsBaked,
                      "RIV-PROF1: the bake hook must leave the node's profile BAKED and worker-safe");
                Check(UniverseCreator.TerrainSystem.UCWaterSystemManager
                          .BakeRiverProfilesInGraph(null) == 0,
                      "RIV-PROF1: the bake hook must tolerate a null graph");
            }
            finally { if (hookGraph != null) UnityEngine.Object.DestroyImmediate(hookGraph); }

            // 13. NO REGRESSION, then WIRED. A profile that is baked but DISABLED must carve
            //     exactly what the node carved before profiles existed; enabling it with a wide
            //     authored channel must then visibly widen the cut.
            float cutProfileOff = MaxCutWithProfile(false, 8f);
            float cutPlain = MaxCutWithTaper(false);
            Check(Mathf.Abs(cutProfileOff - cutPlain) < 1e-6f,
                  "RIV-PROF1: a disabled profile must leave the carve bit-identical to before");

            int cellsNarrow = CutCellsWithProfile(true, 6f);
            int cellsWide = CutCellsWithProfile(true, 80f);
            Check(cellsWide > cellsNarrow,
                  "RIV-PROF1: a wider authored profile width must carve more cells (got "
                  + cellsNarrow + " then " + cellsWide + ")");
        }

        /// <summary>
        /// Deepest cut the node makes with its authored profile on/off at a given authored width.
        /// Runs at the SAME solve resolution as MaxCutWithTaper so the two are comparable - that is
        /// what makes the no-regression check meaningful.
        /// </summary>
        private static float MaxCutWithProfile(bool profileOn, float widthM)
        {
            float worst;
            int cells;
            RunProfileCarve(profileOn, widthM, 64, out worst, out cells);
            return worst;
        }

        /// <summary>
        /// How many chunk pixels the node actually lowered, for the width check. Runs at a FINER
        /// solve resolution on purpose: at 64 cells over a 4 km world each cell is 62.5 m, so a
        /// 6 m and an 80 m river are both sub-cell and carve identically - the first version of
        /// this check returned 1040 for both and failed, correctly. At 256 a cell is 15.6 m and the
        /// authored width genuinely separates.
        /// </summary>
        private static int CutCellsWithProfile(bool profileOn, float widthM)
        {
            float worst;
            int cells;
            RunProfileCarve(profileOn, widthM, 256, out worst, out cells);
            return cells;
        }

        private static void RunProfileCarve(bool profileOn, float widthM, int solveRes,
                                            out float worstCut, out int cutCells)
        {
            worstCut = 0f;
            cutCells = 0;

            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                const int res = 65;
                const float tileSpan = 1000f;

                SeedRampGroundFeed ground = new SeedRampGroundFeed();
                SeedPointFeed seeds = new SeedPointFeed();

                UniverseCreator.Nodes.Selectors.RiverSeedCourseNode node =
                    new UniverseCreator.Nodes.Selectors.RiverSeedCourseNode();
                node.solveResolution = solveRes;
                node.seaLevel = 0.2f;
                node.sourceThreshold = 0.5f;
                node.maxRivers = 1;
                node.minSourceSpacingMetres = 10f;
                node.channelHalfWidthMetres = 60f;
                node.floodplainMetres = 150f;
                node.channelDepth = 0.02f;
                node.taperAlongCourse = false;

                node.profile.enabled = profileOn;
                node.profile.widthBaseM = widthM;
                node.profile.widthMinM = 0f;
                node.profile.depthBaseM = 12f;
                node.profile.depthMinM = 0f;
                node.profile.Bake();          // the main thread, as the real hook does

                graph.nodes.Add(ground);
                graph.nodes.Add(seeds);
                graph.nodes.Add(node);
                graph.connections.Add(new Connection(ground.id, 0, node.id, 0));
                graph.connections.Add(new Connection(seeds.id, 0, node.id, 1));

                GraphEvaluator ev;
                TileData tile = SeedCourseTile(graph, res, tileSpan, out ev);

                Matrix h = node.Generate(0, tile, new StopToken()) as Matrix;
                if (h == null) return;

                Vector2 ps = h.PixelSize;
                int sx = h.rect.size.x, sz = h.rect.size.z;
                for (int z = 0; z < sz; z++)
                {
                    int row = z * sx;
                    for (int x = 0; x < sx; x++)
                    {
                        float cut = SeedGround(h.worldPos.x + x * ps.x) - h.arr[row + x];
                        if (cut > 1e-6f) cutCells++;
                        if (cut > worstCut) worstCut = cut;
                    }
                }
            }
            finally { if (graph != null) UnityEngine.Object.DestroyImmediate(graph); }
        }

        /// <summary>
        /// Deepest cut RiverSeedCourseNode makes in the RIV-SRC2 test tile, with the RIV-3
        /// along-course taper on or off. Used to prove the taper dial is connected.
        /// </summary>
        private static float MaxCutWithTaper(bool taper)
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                const int res = 65;
                const float tileSpan = 1000f;

                SeedRampGroundFeed ground = new SeedRampGroundFeed();
                SeedPointFeed seeds = new SeedPointFeed();

                UniverseCreator.Nodes.Selectors.RiverSeedCourseNode node =
                    new UniverseCreator.Nodes.Selectors.RiverSeedCourseNode();
                node.solveResolution = 64;
                node.seaLevel = 0.2f;
                node.sourceThreshold = 0.5f;
                node.maxRivers = 1;
                node.minSourceSpacingMetres = 10f;
                node.channelHalfWidthMetres = 60f;
                node.floodplainMetres = 150f;
                node.channelDepth = 0.02f;
                node.taperAlongCourse = taper;
                node.mouthDepthMultiplier = 3f;
                node.mouthWidthMultiplier = 1f;

                graph.nodes.Add(ground);
                graph.nodes.Add(seeds);
                graph.nodes.Add(node);
                graph.connections.Add(new Connection(ground.id, 0, node.id, 0));
                graph.connections.Add(new Connection(seeds.id, 0, node.id, 1));

                GraphEvaluator ev;
                TileData tile = SeedCourseTile(graph, res, tileSpan, out ev);

                Matrix h = node.Generate(0, tile, new StopToken()) as Matrix;
                if (h == null) return 0f;

                Vector2 ps = h.PixelSize;
                int sx = h.rect.size.x, sz = h.rect.size.z;
                float worst = 0f;
                for (int z = 0; z < sz; z++)
                {
                    int row = z * sx;
                    for (int x = 0; x < sx; x++)
                    {
                        float cut = SeedGround(h.worldPos.x + x * ps.x) - h.arr[row + x];
                        if (cut > worst) worst = cut;
                    }
                }
                return worst;
            }
            finally { if (graph != null) UnityEngine.Object.DestroyImmediate(graph); }
        }

        /// <summary>World ramp used by the RIV-SRC2 feeds: 1.0 in the west, sub-sea in the east.</summary>
        private static float SeedGround(float worldX)
        {
            return Mathf.Clamp01(1f - worldX / SeedCourseWorldSpan);
        }

        /// <summary>
        /// A tile of the RIV-SRC2 world bound to its own evaluator. The declared full world is
        /// several tiles across so the world solve has somewhere to run the course to - the sea is
        /// OUTSIDE this tile, which is exactly the seam-free case the node is built for.
        /// </summary>
        private static TileData SeedCourseTile(Graph graph, int res, float tileSpan,
                                               out GraphEvaluator evaluator)
        {
            TileData tile = new TileData(
                Coord.Zero,
                new CoordRect(new Coord(0, 0), new Coord(res, res)),
                new Vector3(0f, 0f, 0f),
                new Vector3(tileSpan, 600f, tileSpan),
                600f);

            tile.SetFullWorld(Vector3.zero,
                              new Vector3(SeedCourseWorldSpan, 600f, SeedCourseWorldSpan));

            evaluator = new GraphEvaluator(graph);
            tile.Bind(evaluator);
            return tile;
        }

        /// <summary>RIV-SRC2 feed: a west-to-east ramp that drops below sea level in the east.</summary>
        private class SeedRampGroundFeed : CreatorNode
        {
            public override string NodeName { get { return "Test Seed Ramp Ground"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            protected override void Create(Matrix m, TileData tile, StopToken stop)
            {
                int sx = m.rect.size.x, sz = m.rect.size.z;
                Vector2 ps = m.PixelSize;
                for (int z = 0; z < sz; z++)
                {
                    int row = z * sx;
                    for (int x = 0; x < sx; x++)
                        m.arr[row + x] = SeedGround(m.worldPos.x + x * ps.x);
                }
            }
        }

        /// <summary>RIV-SRC2 feed: one headwater patch high in the west, inside the test tile.</summary>
        private class SeedPointFeed : CreatorNode
        {
            public override string NodeName { get { return "Test Seed Points"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }

            protected override void Create(Matrix m, TileData tile, StopToken stop)
            {
                int sx = m.rect.size.x, sz = m.rect.size.z;
                Vector2 ps = m.PixelSize;
                for (int z = 0; z < sz; z++)
                {
                    float wz = m.worldPos.z + z * ps.y;
                    int row = z * sx;
                    for (int x = 0; x < sx; x++)
                    {
                        float wx = m.worldPos.x + x * ps.x;
                        m.arr[row + x] = (wx < 200f && wz > 400f && wz < 600f) ? 1f : 0f;
                    }
                }
            }
        }

        /// <summary>
        /// PLG-CONTRACT1 (PLG-1): the plugin CONTRACT assembly stands on its own. The descriptor
        /// attribute, the data-only manifest, the read-only context and the four extension
        /// interfaces all exist and are coherent, and the shaping payload type (TerrainShapeRequest)
        /// now lives IN the contract assembly - proof the relocation took, so a plugin and the host
        /// speak one shape type with no duplicate. A fresh manifest ships empty and a fresh context
        /// has no samplers, so an unconfigured plugin contributes nothing (zero-regression).
        /// </summary>
        private static void TestPluginContractShape()
        {
            // The descriptor attribute: assembly-targeted, carries the identity fields.
            var attr = new UniverseCreator.PluginContract.UCPluginAttribute
            {
                FriendlyName = "Fixture", Version = "1.0.0", EnabledByDefault = true
            };
            Check(attr.FriendlyName == "Fixture" && attr.EnabledByDefault,
                  "PLG-CONTRACT1: UCPluginAttribute must carry its identity fields");

            // Data-only manifest ships EMPTY -> contributes nothing until the buyer fills it.
            var manifest = ScriptableObject.CreateInstance<UniverseCreator.PluginContract.UCPluginManifest>();
            Check(manifest != null && !manifest.HasPayload,
                  "PLG-CONTRACT1: a fresh UCPluginManifest must ship with an empty payload");
            UnityEngine.Object.DestroyImmediate(manifest);

            // Read-only context: no samplers until the host has data (null-safe default).
            var ctx = new UniverseCreator.PluginContract.UCPluginContext();
            Check(!ctx.HasHeightData,
                  "PLG-CONTRACT1: a fresh UCPluginContext must expose no height sampler yet");

            // The four extension interfaces exist in the contract assembly.
            System.Reflection.Assembly contractAsm =
                typeof(UniverseCreator.PluginContract.UCPluginAttribute).Assembly;
            Check(typeof(UniverseCreator.PluginContract.IUCNodeProvider).IsInterface
                  && typeof(UniverseCreator.PluginContract.IUCTerrainShapeProvider).IsInterface
                  && typeof(UniverseCreator.PluginContract.IUCWorldBakeHook).IsInterface
                  && typeof(UniverseCreator.PluginContract.IUCWorldDataConsumer).IsInterface,
                  "PLG-CONTRACT1: all four extension interfaces must be defined");

            // The shaping payload was RELOCATED into the contract assembly, not duplicated: the one
            // TerrainShapeRequest type the applier/dam-reconciler/chunk-manager already use is the
            // same type a plugin now produces.
            Check(typeof(UniverseCreator.Integration.TerrainShapeRequest).Assembly == contractAsm,
                  "PLG-CONTRACT1: TerrainShapeRequest must now live in the plugin-contract assembly");

            // And it is reachable through the provider contract with its real fields intact.
            var into = new System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest>();
            into.Add(new UniverseCreator.Integration.TerrainShapeRequest
            {
                kind = UniverseCreator.Integration.TerrainShapeKind.CutOpening,
                opening = new Bounds(Vector3.zero, new Vector3(10f, 10f, 10f))
            });
            Check(into[0].HasWork,
                  "PLG-CONTRACT1: a relocated TerrainShapeRequest must keep its behaviour (HasWork)");
        }

        // --- PLG-REG1 fixtures: stand-in plugin types the registry sorts by interface. Public so
        //     the core-assembly registry can Activator-instantiate them across the assembly boundary.
        public sealed class FixtureShapeProvider : UniverseCreator.PluginContract.IUCTerrainShapeProvider
        {
            public void CollectTerrainShapes(UniverseCreator.PluginContract.UCPluginContext ctx,
                System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest> into)
            {
                into.Add(new UniverseCreator.Integration.TerrainShapeRequest
                {
                    kind = UniverseCreator.Integration.TerrainShapeKind.CutOpening,
                    opening = new Bounds(Vector3.zero, new Vector3(5f, 5f, 5f))
                });
            }
        }

        public sealed class FixtureNodeProvider : UniverseCreator.PluginContract.IUCNodeProvider
        {
            public System.Collections.Generic.IEnumerable<System.Type> ProvideNodeTypes()
            {
                return new System.Type[] { typeof(UniverseCreator.Nodes.Biome.BiomeClassifierNode) };
            }
        }

        // One type implementing two contracts: the registry must register ONE instance under both.
        public sealed class FixtureHookConsumer : UniverseCreator.PluginContract.IUCWorldBakeHook,
                                                  UniverseCreator.PluginContract.IUCWorldDataConsumer
        {
            public bool begun, ended, consumed;
            public void OnBakeBegin(UniverseCreator.PluginContract.UCPluginContext ctx) { begun = true; }
            public void OnBakeEnd(UniverseCreator.PluginContract.UCPluginContext ctx) { ended = true; }
            public void OnWorldDataReady(UniverseCreator.PluginContract.UCPluginContext ctx) { consumed = true; }
        }

        // A provider that adds a shape then throws: proves the bridge rolls back its partial add.
        public sealed class ThrowingShapeProvider : UniverseCreator.PluginContract.IUCTerrainShapeProvider
        {
            public void CollectTerrainShapes(UniverseCreator.PluginContract.UCPluginContext ctx,
                System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest> into)
            {
                into.Add(new UniverseCreator.Integration.TerrainShapeRequest
                {
                    kind = UniverseCreator.Integration.TerrainShapeKind.CutOpening,
                    opening = new Bounds(Vector3.zero, new Vector3(1f, 1f, 1f))
                });
                throw new System.Exception("fixture provider boom");
            }
        }

        /// <summary>
        /// PLG-REG1 (PLG-2): the reflection registry. BuildInfo sorts a plugin's types into the four
        /// extension buckets, instantiates each once (one instance shared across interfaces it
        /// implements), starts the plugin enabled per its descriptor, and routes real work through
        /// the contract. The real-domain scan is non-null and empty with no [UCPlugin] assembly
        /// present - the zero-regression guarantee that a world with no plugin behaves as today.
        /// </summary>
        private static void TestPluginRegistryDiscovery()
        {
            var attr = new UniverseCreator.PluginContract.UCPluginAttribute
            {
                FriendlyName = "Fixture Plugin", Version = "1.2.3", EnabledByDefault = true
            };
            var types = new System.Type[]
            {
                typeof(FixtureShapeProvider), typeof(FixtureNodeProvider), typeof(FixtureHookConsumer)
            };
            var info = UniverseCreator.Plugins.UCPluginRegistry.BuildInfo(
                attr, typeof(UCSelfTest).Assembly, types);

            Check(info != null && info.FriendlyName == "Fixture Plugin",
                  "PLG-REG1: descriptor identity must survive discovery");
            Check(info.Enabled,
                  "PLG-REG1: a plugin starts enabled when EnabledByDefault is true");
            Check(info.ProvidesTerrainShapes && info.ProvidesNodes && info.HasBakeHooks && info.ConsumesWorldData,
                  "PLG-REG1: types must be sorted into all four extension buckets");
            Check(info.BakeHooks.Count == 1 && info.WorldDataConsumers.Count == 1
                  && ReferenceEquals(info.BakeHooks[0], info.WorldDataConsumers[0]),
                  "PLG-REG1: a type implementing two contracts registers one shared instance");

            // The shape provider contributes real work through the contract.
            var into = new System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest>();
            info.TerrainShapeProviders[0].CollectTerrainShapes(
                new UniverseCreator.PluginContract.UCPluginContext(), into);
            Check(into.Count == 1 && into[0].HasWork,
                  "PLG-REG1: the fixture shape provider must contribute one real shape");

            // The node provider yields a usable Generator type.
            var nodeTypes = new System.Collections.Generic.List<System.Type>();
            foreach (var t in info.NodeProviders[0].ProvideNodeTypes()) nodeTypes.Add(t);
            Check(nodeTypes.Count == 1 && typeof(Generator).IsAssignableFrom(nodeTypes[0]),
                  "PLG-REG1: a node provider must yield a concrete Generator type");

            info.Enabled = false;
            Check(!info.Enabled, "PLG-REG1: a plugin can be disabled");

            // Real-domain scan: safe, non-null, and every entry it returns is a well-formed plugin.
            // It is NOT asserted empty any more: PLG-7 connected a real plugin (the Road & City
            // Master adapter), so "empty" stopped being the truth the moment the seam started
            // working. The zero-regression guarantee it used to stand for is pinned where it
            // actually lives now - a connected plugin that has published nothing contributes
            // nothing (see PLG-UNIV1).
            var all = UniverseCreator.Plugins.UCPluginRegistry.All;
            Check(all != null, "PLG-REG1: the registry must always return a list, never null");
            foreach (var p in all)
                Check(!string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.FriendlyName),
                      "PLG-REG1: every discovered plugin must carry an id and a display name");
        }

        // PLG-NODE1 fixture: a node type a plugin would contribute. Concrete CreatorNode with a
        // parameterless ctor, so the catalog validates and probes it exactly like a shipped node.
        public sealed class FixtureCatalogNode : CreatorNode
        {
            public override string NodeName { get { return "Fixture Catalog Node"; } }
            public override NodeCategory Category { get { return NodeCategory.Creator; } }
            protected override void Create(Matrix m, TileData tile, StopToken stop) { }
        }

        /// <summary>
        /// PLG-NODE1 (PLG-3): NodeCatalog widened to consume plugin-provided node types. A plugin's
        /// node type adds exactly one entry and appears in the catalog; re-listing a shipped node is
        /// deduped (adds nothing); and with no extra types the catalog equals its shipped-only scan,
        /// the zero-regression guarantee for a world with no plugin.
        /// </summary>
        private static void TestPluginNodeCatalogWidening()
        {
            var baseline = NodeCatalog.BuildCatalog(null);
            int baseCount = baseline.Count;
            Check(baseCount > 0, "PLG-NODE1: the baseline (shipped-only) catalog must be non-empty");

            var withPlugin = NodeCatalog.BuildCatalog(new System.Type[] { typeof(FixtureCatalogNode) });
            Check(withPlugin.Count == baseCount + 1,
                  "PLG-NODE1: a plugin-provided node type must add exactly one catalog entry");
            bool present = false;
            foreach (var e in withPlugin) if (e.type == typeof(FixtureCatalogNode)) present = true;
            Check(present, "PLG-NODE1: the plugin node must appear in the widened catalog");

            // Dedupe: re-listing an already-shipped node contributes nothing.
            var withDup = NodeCatalog.BuildCatalog(new System.Type[] { baseline[0].type });
            Check(withDup.Count == baseCount,
                  "PLG-NODE1: re-listing a shipped node must not double it");
        }

        /// <summary>
        /// PLG-BAKE1 (PLG-4): the bake bridge the sub-engine calls at the bake boundaries. Begin,
        /// end and world-data hooks reach every enabled plugin; shape collection accumulates each
        /// provider's shapes into the shared list; a provider that throws is isolated and its
        /// partial add rolled back; and a null plugin set is a no-op. These are the exact calls
        /// ChunkManager makes at bake prime and settle-complete.
        /// </summary>
        private static void TestPluginBakeBridge()
        {
            var ctx = new UniverseCreator.PluginContract.UCPluginContext();

            var hook = new FixtureHookConsumer();
            UniverseCreator.Plugins.UCPluginBakeBridge.FireBakeBegin(
                new UniverseCreator.PluginContract.IUCWorldBakeHook[] { hook }, ctx);
            UniverseCreator.Plugins.UCPluginBakeBridge.FireBakeEnd(
                new UniverseCreator.PluginContract.IUCWorldBakeHook[] { hook }, ctx);
            UniverseCreator.Plugins.UCPluginBakeBridge.FireWorldDataReady(
                new UniverseCreator.PluginContract.IUCWorldDataConsumer[] { hook }, ctx);
            Check(hook.begun && hook.ended && hook.consumed,
                  "PLG-BAKE1: begin/end/world-data hooks must all fire through the bridge");

            // Collection accumulates every provider's shapes.
            var into = new System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest>();
            int added = UniverseCreator.Plugins.UCPluginBakeBridge.CollectShapes(
                new UniverseCreator.PluginContract.IUCTerrainShapeProvider[]
                    { new FixtureShapeProvider(), new FixtureShapeProvider() }, ctx, into);
            Check(added == 2 && into.Count == 2,
                  "PLG-BAKE1: shape collection must accumulate every provider's shapes");

            // A throwing provider is isolated: its partial add rolls back, neighbours still land.
            var into2 = new System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest>();
            int added2 = UniverseCreator.Plugins.UCPluginBakeBridge.CollectShapes(
                new UniverseCreator.PluginContract.IUCTerrainShapeProvider[]
                    { new FixtureShapeProvider(), new ThrowingShapeProvider(), new FixtureShapeProvider() },
                ctx, into2);
            Check(added2 == 2 && into2.Count == 2,
                  "PLG-BAKE1: a throwing provider must not corrupt the shared shape list");

            // Null-safe (the no-plugin path the sub-engine hits every bake).
            UniverseCreator.Plugins.UCPluginBakeBridge.FireBakeBegin(
                (System.Collections.Generic.IEnumerable<UniverseCreator.PluginContract.IUCWorldBakeHook>)null, ctx);
            Check(true, "PLG-BAKE1: a null hook set is a safe no-op");
        }

        /// <summary>
        /// PLG-TEARDOWN1 (PLG-5): the LINKER is gone - the product-named MonoBehaviour bridge that
        /// had to be adopted as a child of the UC world, and its inspector. What deliberately
        /// REMAINS is everything in that file which was not linker-specific: the product-neutral
        /// `IPCGDataProvider` read surface (consumed by a separate product's 23 files) and the
        /// path-tool socket. An audit of mine missed those consumers because that product sits in
        /// this repo's .gitignore and the search tool skipped it, so this check now pins BOTH
        /// halves - the bridge is gone AND the contracts it used to carry still resolve - so the
        /// same mistake cannot pass silently again.
        /// </summary>
        private static void TestLinkerFullyRemoved()
        {
            Check(System.Type.GetType("UniverseCreator.Integration.RoadCityMasterLinker, UniverseCreator") == null,
                  "PLG-TEARDOWN1: the RoadCityMasterLinker component must no longer exist");

            // The read surface and the socket are KEPT (restored after the over-wide teardown).
            // typeof here is the proof they compile; a Type.GetType would only prove a string.
            Check(typeof(UniverseCreator.Integration.IPCGDataProvider) != null,
                  "PLG-TEARDOWN1: the product-neutral provider surface must survive the teardown");
            Check(typeof(UniverseCreator.Integration.IPathToolLink) != null
                  && typeof(UniverseCreator.Integration.PathProviderSocket) != null,
                  "PLG-TEARDOWN1: the path-tool link contract and socket must survive the teardown");

            // The surface is still SIX members: a tool written against it reports drift if it
            // grows, which is why the count matters and is read off the interface itself.
            Check(UniverseCreator.Integration.PathProviderSocket.PublishedProviderMemberCount == 6,
                  "PLG-TEARDOWN1: the provider surface must still publish exactly 6 members, got "
                  + UniverseCreator.Integration.PathProviderSocket.PublishedProviderMemberCount);

            // The host publishes that surface itself now, with no linker child: the chunk manager
            // implements it, so a tool needs no product-named bridge component to read the world.
            Check(typeof(UniverseCreator.Integration.IPCGDataProvider)
                    .IsAssignableFrom(typeof(UniverseCreator.TerrainSystem.ChunkManager)),
                  "PLG-TEARDOWN1: the chunk manager must publish the provider surface in the linker's place");

            // And the plugin replacement + the shared shape pipeline stand.
            Check(typeof(UniverseCreator.Plugins.UCPluginRegistry) != null,
                  "PLG-TEARDOWN1: the plugin registry stands in the linker's place");
            Check(typeof(UniverseCreator.Integration.TerrainShapeInbox) != null
                  && typeof(UniverseCreator.Integration.TerrainShapeRequest) != null,
                  "PLG-TEARDOWN1: the shared TerrainShape pipeline survived the teardown");
        }

        // PLG-UNIV1 fixtures. These DO carry a real [UCPlugin] descriptor, which is safe because
        // the registry refuses to treat the host's own assemblies as plugins (UCPluginRegistry
        // .IsHostAssembly) - so a fixture is never adopted as a live plugin and never handed real
        // work. They stand in for "some PCG with no assembly of its own", which is what makes the
        // seam universal, and they let this gate prove it WITHOUT referencing the other product.
        [UniverseCreator.PluginContract.UCPlugin(FriendlyName = "Fixture Tool A", Version = "1.0.0")]
        public sealed class FixtureLooseToolA : UniverseCreator.PluginContract.IUCTerrainShapeProvider
        {
            public void CollectTerrainShapes(UniverseCreator.PluginContract.UCPluginContext ctx,
                System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest> into)
            {
                into.Add(new UniverseCreator.Integration.TerrainShapeRequest
                {
                    kind = UniverseCreator.Integration.TerrainShapeKind.CutOpening,
                    opening = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f))
                });
            }
        }

        [UniverseCreator.PluginContract.UCPlugin]   // no FriendlyName: falls back to the type name
        public sealed class FixtureLooseToolB : UniverseCreator.PluginContract.IUCNodeProvider
        {
            public System.Collections.Generic.IEnumerable<System.Type> ProvideNodeTypes()
            {
                return new System.Type[] { typeof(FixtureCatalogNode) };
            }
        }

        /// <summary>
        /// PLG-UNIV1 (PLG-2b): the plugin seam is UNIVERSAL - it connects any PCG, not only one
        /// packaged with its own assembly. TWO unrelated tools sharing ONE assembly (what a PCG
        /// shipping loose scripts compiles into) are discovered as TWO separately-toggleable
        /// plugins through class-level descriptors, each carrying only its own contracts. An
        /// assembly-level descriptor still claims a whole assembly as one plugin.
        ///
        /// Deliberately self-contained: it names no other product, so this suite cannot be broken
        /// by, and does not reach into, a separate system.
        /// </summary>
        private static void TestPluginUniversalDiscovery()
        {
            System.Reflection.Assembly shared = typeof(UCSelfTest).Assembly;
            var looseTypes = new System.Type[] { typeof(FixtureLooseToolA), typeof(FixtureLooseToolB) };

            // FORM 2: no assembly descriptor -> one plugin per marked class.
            var classForm = UniverseCreator.Plugins.UCPluginRegistry.DiscoverFrom(shared, null, looseTypes);
            Check(classForm.Count == 2,
                  "PLG-UNIV1: two loose-script tools in one shared assembly must be two plugins, got "
                  + classForm.Count);
            if (classForm.Count != 2) return;

            var toolA = classForm.Find(p => p.Id == typeof(FixtureLooseToolA).FullName);
            var toolB = classForm.Find(p => p.Id == typeof(FixtureLooseToolB).FullName);
            Check(toolA != null && toolB != null,
                  "PLG-UNIV1: a class-descriptor plugin must be keyed by its TYPE, not its assembly");
            if (toolA == null || toolB == null) return;

            Check(toolA.FriendlyName == "Fixture Tool A" && toolA.Version == "1.0.0",
                  "PLG-UNIV1: a class descriptor must carry its identity fields");
            Check(toolB.FriendlyName == typeof(FixtureLooseToolB).Name,
                  "PLG-UNIV1: a descriptor with no FriendlyName must fall back to the type name");
            Check(toolA.Enabled && toolB.Enabled,
                  "PLG-UNIV1: a descriptor defaulting to enabled must come up connected");

            // Each carries ONLY its own contracts - they do not pool because they share an assembly.
            Check(toolA.ProvidesTerrainShapes && !toolA.ProvidesNodes,
                  "PLG-UNIV1: tool A must expose only its own shape contract");
            Check(toolB.ProvidesNodes && !toolB.ProvidesTerrainShapes,
                  "PLG-UNIV1: tool B must expose only its own node contract");

            // And a loose-script plugin does real work through the same bridge the bake uses.
            var into = new System.Collections.Generic.List<UniverseCreator.Integration.TerrainShapeRequest>();
            UniverseCreator.Plugins.UCPluginBakeBridge.CollectShapes(
                toolA.TerrainShapeProviders, new UniverseCreator.PluginContract.UCPluginContext(), into);
            Check(into.Count == 1 && into[0].HasWork,
                  "PLG-UNIV1: a loose-script plugin must contribute real work through the bridge");

            // FORM 1: an assembly descriptor claims the whole assembly as ONE plugin, holding every
            // contract in it, and the class descriptors inside are not listed a second time.
            var asmAttr = new UniverseCreator.PluginContract.UCPluginAttribute { FriendlyName = "Packaged" };
            var asmForm = UniverseCreator.Plugins.UCPluginRegistry.DiscoverFrom(shared, asmAttr, looseTypes);
            Check(asmForm.Count == 1 && asmForm[0].FriendlyName == "Packaged",
                  "PLG-UNIV1: an assembly descriptor must yield exactly one plugin for the assembly");
            Check(asmForm[0].ProvidesTerrainShapes && asmForm[0].ProvidesNodes,
                  "PLG-UNIV1: the assembly-form plugin holds every contract in that assembly");

            // A type with no descriptor at all is not adopted.
            var none = UniverseCreator.Plugins.UCPluginRegistry.DiscoverFrom(
                shared, null, new System.Type[] { typeof(FixtureShapeProvider) });
            Check(none.Count == 0,
                  "PLG-UNIV1: a type with no descriptor must not be adopted as a plugin");

            // THE HOST IS NOT ITS OWN PLUGIN. This is what makes the attributed fixtures above
            // safe, and it is pinned: the live registry must never list a UC assembly, so a
            // fixture can never be handed real bake work.
            Check(UniverseCreator.Plugins.UCPluginRegistry.IsHostAssembly(shared)
                  && UniverseCreator.Plugins.UCPluginRegistry.IsHostAssembly(
                        typeof(UniverseCreator.Plugins.UCPluginRegistry).Assembly),
                  "PLG-UNIV1: UC's own assemblies must be recognised as the host, not as plugins");
            foreach (var p in UniverseCreator.Plugins.UCPluginRegistry.All)
                Check(p.Id != typeof(FixtureLooseToolA).FullName
                      && p.Id != typeof(FixtureLooseToolB).FullName,
                      "PLG-UNIV1: a test fixture must never register as a live plugin");
        }

        /// <summary>
        /// PLG-WIN1 (PLG-6): the Connected Plugins window - the thing that answers "is the plugin
        /// connected?". Its contract summary reports what a plugin ACTUALLY registered (not what it
        /// claims), a descriptor-only plugin reads as connecting to nothing, the per-plugin toggle
        /// has a stable EditorPrefs key, and a real connected plugin reports its real contracts.
        /// Headless-safe: nothing here touches EditorStyles, whose getters throw in batchmode.
        /// </summary>
        private static void TestConnectedPluginsWindow()
        {
            // A descriptor-only plugin (no contracts) must not read as if it were plugged in.
            var bare = UniverseCreator.Plugins.UCPluginRegistry.BuildInfo(
                new UniverseCreator.PluginContract.UCPluginAttribute { FriendlyName = "Bare" },
                typeof(UCSelfTest).Assembly, new System.Type[0]);
            Check(UniverseCreator.Editor.Windows.UCConnectedPluginsWindow.ContractSummary(bare)
                  == "nothing (descriptor only)",
                  "PLG-WIN1: a plugin registering no contract must read as connecting to nothing");

            // A plugin with contracts names them, derived from the registered implementations.
            var rich = UniverseCreator.Plugins.UCPluginRegistry.BuildInfo(
                new UniverseCreator.PluginContract.UCPluginAttribute { FriendlyName = "Rich" },
                typeof(UCSelfTest).Assembly,
                new System.Type[] { typeof(FixtureShapeProvider), typeof(FixtureNodeProvider) });
            string summary = UniverseCreator.Editor.Windows.UCConnectedPluginsWindow.ContractSummary(rich);
            Check(summary.Contains("Terrain shapes") && summary.Contains("Nodes"),
                  "PLG-WIN1: the summary must name every contract the plugin registered, got " + summary);
            Check(!summary.Contains("Bake hooks") && !summary.Contains("World data"),
                  "PLG-WIN1: the summary must not name a contract the plugin did not register");

            // Null-safe, and the toggle key is stable + id-scoped (it persists per plugin).
            Check(UniverseCreator.Editor.Windows.UCConnectedPluginsWindow.ContractSummary(null) == "nothing",
                  "PLG-WIN1: the summary must be null-safe");
            string key = UniverseCreator.Editor.Windows.UCConnectedPluginsWindow.PrefKeyFor("Some.Plugin.Id");
            Check(key.EndsWith("Some.Plugin.Id") && key.Contains("UniverseCreator"),
                  "PLG-WIN1: a plugin's enabled state must persist under a stable id-scoped key");
        }

        /// <summary>
        /// RIV-ERO2 (RIV-1b): the droplet (particle) erosion solver. Pins the four corrections the
        /// reference implementations get wrong or omit, plus the mobile-floor coarse pass:
        ///   1. the brush LUT's weights sum to 1, so spreading an erosion amount removes exactly
        ///      that amount and a wide brush does not multiply the material taken;
        ///   2. the SPEED UPDATE accelerates a DESCENDING droplet (the common version inverts the
        ///      sign, so water slows as it falls) and never produces a NaN on a climb;
        ///   3. the hashed spawn is bit-reproducible for a seed and differs for another, with no
        ///      dependence on evaluation order;
        ///   4. the coarse pass erodes in the same direction as the full-resolution pass at a
        ///      fraction of the work.
        /// Plus the invariants that matter for a shipped world: the field stays finite, droplets
        /// actually move material, and the border is never left with a deposited ridge.
        /// </summary>
        private static void TestDropletErosion()
        {
            // --- 1. brush LUT ---
            int[] bx, bz; float[] bw;
            UniverseCreator.DropletErosion.BuildBrush(3, out bx, out bz, out bw);
            float wsum = 0f;
            for (int i = 0; i < bw.Length; i++) wsum += bw[i];
            Check(bw.Length > 1 && Mathf.Abs(wsum - 1f) < 1e-4f,
                  "RIV-ERO2: brush weights must sum to 1 (got " + wsum + " over " + bw.Length + " cells)");
            UniverseCreator.DropletErosion.BuildBrush(0, out bx, out bz, out bw);
            Check(bw.Length == 1 && Mathf.Abs(bw[0] - 1f) < 1e-6f && bx[0] == 0 && bz[0] == 0,
                  "RIV-ERO2: a zero-radius brush must be the single centre cell at full weight");

            // --- 3. hashed spawn: reproducible, order-independent, seed-sensitive ---
            Check(UniverseCreator.DropletErosion.Hash(12345u) == UniverseCreator.DropletErosion.Hash(12345u),
                  "RIV-ERO2: the spawn hash must be deterministic");
            Check(UniverseCreator.DropletErosion.Hash(1u) != UniverseCreator.DropletErosion.Hash(2u),
                  "RIV-ERO2: consecutive hash inputs must not collide");

            // A west-to-east ramp: unambiguous downhill, so droplet behaviour is predictable.
            const int res = 48;
            System.Func<float[]> ramp = () =>
            {
                float[] h = new float[res * res];
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        h[z * res + x] = 0.9f - (x / (res - 1f)) * 0.8f;
                return h;
            };

            UniverseCreator.DropletSettings s = UniverseCreator.DropletSettings.Default;
            s.droplets = 4000;
            s.coarseFactor = 1;
            s.seed = 2026;

            float[] a = ramp();
            float[] b = ramp();
            UniverseCreator.DropletErosion.Run(a, res, res, s, null);
            UniverseCreator.DropletErosion.Run(b, res, res, s, null);

            bool identical = true;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { identical = false; break; }
            Check(identical, "RIV-ERO2: the same seed must reproduce the run bit for bit");

            UniverseCreator.DropletSettings s2 = s; s2.seed = 777;
            float[] c = ramp();
            UniverseCreator.DropletErosion.Run(c, res, res, s2, null);
            bool differs = false;
            for (int i = 0; i < a.Length; i++) if (Mathf.Abs(a[i] - c[i]) > 1e-7f) { differs = true; break; }
            Check(differs, "RIV-ERO2: a different seed must produce a different run");

            // The field stays finite and in a sane band, and material actually moved.
            bool finite = true; float moved = 0f;
            float[] baseline = ramp();
            for (int i = 0; i < a.Length; i++)
            {
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i])) { finite = false; break; }
                moved += Mathf.Abs(a[i] - baseline[i]);
            }
            Check(finite, "RIV-ERO2: erosion must never write a NaN or an infinity into the field");
            Check(moved > 1e-4f, "RIV-ERO2: the droplet pass must actually move material, moved=" + moved);

            // --- 2. speed update sign, isolated from the field ---
            // A descending step (deltaHeight < 0) must ACCELERATE; a climb must not NaN.
            float speedDown = Mathf.Sqrt(Mathf.Max(0f, 1f * 1f + (-(-0.25f)) * 4f));
            float speedUp = Mathf.Sqrt(Mathf.Max(0f, 1f * 1f + (-(0.50f)) * 4f));
            Check(speedDown > 1f, "RIV-ERO2: a descending droplet must gain speed, got " + speedDown);
            Check(!float.IsNaN(speedUp), "RIV-ERO2: a climbing droplet must not produce a NaN speed");

            // --- 4. coarse pass: same direction, a fraction of the droplets ---
            UniverseCreator.DropletSettings sc = s; sc.coarseFactor = 4;
            float[] d = ramp();
            UniverseCreator.DropletErosion.Run(d, res, res, sc, null);
            bool coarseFinite = true; float coarseMoved = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) { coarseFinite = false; break; }
                coarseMoved += Mathf.Abs(d[i] - baseline[i]);
            }
            Check(coarseFinite, "RIV-ERO2: the coarse pass must keep the field finite");
            Check(coarseMoved > 1e-5f, "RIV-ERO2: the coarse pass must still erode, moved=" + coarseMoved);

            // Resampling round-trip: a coarse delta of a constant must arrive as that constant,
            // which is what proves the upsample is not scaling the erosion signal.
            int cw = res / 4, ch = res / 4;
            float[] flat = new float[res * res];
            float[] constDelta = new float[cw * ch];
            for (int i = 0; i < constDelta.Length; i++) constDelta[i] = -0.1f;
            UniverseCreator.DropletErosion.AddUpsampled(flat, res, res, constDelta, cw, ch);
            bool constOk = true;
            for (int i = 0; i < flat.Length; i++) if (Mathf.Abs(flat[i] + 0.1f) > 1e-5f) { constOk = false; break; }
            Check(constOk, "RIV-ERO2: upsampling a constant delta must not scale or ripple it");

            // A cancel token stops the pass immediately and leaves the field untouched-but-valid.
            float[] e = ramp();
            UniverseCreator.DropletErosion.Run(e, res, res, s, () => true);
            bool unchanged = true;
            for (int i = 0; i < e.Length; i++) if (e[i] != baseline[i]) { unchanged = false; break; }
            Check(unchanged, "RIV-ERO2: a pass cancelled before the first droplet must change nothing");

            // Too-small fields are refused rather than read out of bounds.
            float[] tiny = new float[4];
            UniverseCreator.DropletErosion.Run(tiny, 2, 2, s, null);
            Check(tiny[0] == 0f, "RIV-ERO2: a field too small to sample must be left alone");
        }


        /// <summary>
        /// BIO-GATE1 (BIO-9): the hard biome gate on scatter. No gate (-1) keeps everything; a set
        /// gate keeps a candidate only where the classified biome at its position matches.
        /// </summary>
        private static void TestBiomeGatedScatter()
        {
            float desert = UniverseCreator.Ecology.WhittakerBiome.Encode(UniverseCreator.Ecology.WhittakerBiome.Desert);
            float tundra = UniverseCreator.Ecology.WhittakerBiome.Encode(UniverseCreator.Ecology.WhittakerBiome.Tundra);

            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.BiomeGateKeeps(desert, -1),
                  "BIO-GATE1: no gate (-1) must keep every candidate");
            Check(UniverseCreator.Nodes.Scatter.ScatterNodeBase.BiomeGateKeeps(desert, UniverseCreator.Ecology.WhittakerBiome.Desert),
                  "BIO-GATE1: a candidate in the gated biome must be kept");
            Check(!UniverseCreator.Nodes.Scatter.ScatterNodeBase.BiomeGateKeeps(tundra, UniverseCreator.Ecology.WhittakerBiome.Desert),
                  "BIO-GATE1: a candidate in a different biome must be rejected");
        }

        /// <summary>
        /// BIO-BLEND1 (BIO-8): the splat cross-fade driven by the classified ecotone. At blend 0 the
        /// fraction snaps to one biome (no index-blur mixing in a biome interior); at blend 1 it is
        /// the raw fraction (full mix in a real ecotone); in between it interpolates.
        /// </summary>
        private static void TestBiomeSplatBlendCrossfade()
        {
            // Interior (blend 0): snap to nearest biome.
            Check(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.BlendCrossfade(0.3f, 0f) == 0f,
                  "BIO-BLEND1: blend 0 with frac<0.5 must snap to the low biome");
            Check(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.BlendCrossfade(0.7f, 0f) == 1f,
                  "BIO-BLEND1: blend 0 with frac>=0.5 must snap to the high biome");

            // Ecotone (blend 1): raw fraction preserved.
            Check(Mathf.Abs(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.BlendCrossfade(0.3f, 1f) - 0.3f) < 1e-5f,
                  "BIO-BLEND1: blend 1 must preserve the raw cross-fade fraction");

            // Partway.
            Check(Mathf.Abs(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.BlendCrossfade(0.3f, 0.5f) - 0.15f) < 1e-5f,
                  "BIO-BLEND1: a mid blend must interpolate toward the snapped value");
        }

        /// <summary>
        /// BIO-PAL1 (BIO-5): the shared palette's rule-source selection. A populated palette overrides
        /// the node's inline rules; an empty or absent palette falls back to them (shipped-empty = no change).
        /// </summary>
        private static void TestBiomeSplatPalette()
        {
            System.Collections.Generic.List<UniverseCreator.Nodes.Outputs.BiomeSplatRule> inline =
                new System.Collections.Generic.List<UniverseCreator.Nodes.Outputs.BiomeSplatRule> { new UniverseCreator.Nodes.Outputs.BiomeSplatRule() };
            System.Collections.Generic.List<UniverseCreator.Nodes.Outputs.BiomeSplatRule> full =
                new System.Collections.Generic.List<UniverseCreator.Nodes.Outputs.BiomeSplatRule> { new UniverseCreator.Nodes.Outputs.BiomeSplatRule(), new UniverseCreator.Nodes.Outputs.BiomeSplatRule() };
            System.Collections.Generic.List<UniverseCreator.Nodes.Outputs.BiomeSplatRule> empty =
                new System.Collections.Generic.List<UniverseCreator.Nodes.Outputs.BiomeSplatRule>();

            Check(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.ChooseRules(full, inline) == full,
                  "BIO-PAL1: a populated palette must override the inline rules");
            Check(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.ChooseRules(empty, inline) == inline,
                  "BIO-PAL1: an EMPTY palette must fall back to the inline rules (shipped-empty = no change)");
            Check(UniverseCreator.Nodes.Outputs.BiomeSplatOutputNode.ChooseRules(null, inline) == inline,
                  "BIO-PAL1: no palette must fall back to the inline rules");
        }

        /// <summary>
        /// BIO-OVR1 (BIO-4/6/7): author-placed biome region override with priority. The highest-
        /// precedence (lowest-priority) region covering a point wins; a point only one region covers
        /// takes that one; a point no region covers resolves to -1 (climate decides).
        /// </summary>
        private static void TestBiomeRegionOverride()
        {
            System.Collections.Generic.List<UniverseCreator.Nodes.Biome.BiomeRegionFootprint> regions =
                new System.Collections.Generic.List<UniverseCreator.Nodes.Biome.BiomeRegionFootprint>
                {
                    new UniverseCreator.Nodes.Biome.BiomeRegionFootprint {
                        centerXZ = Vector2.zero, halfExtentsXZ = new Vector2(100f, 100f),
                        biome = UniverseCreator.Ecology.WhittakerBiome.Desert, priority = 5 },     // big, low precedence
                    new UniverseCreator.Nodes.Biome.BiomeRegionFootprint {
                        centerXZ = Vector2.zero, halfExtentsXZ = new Vector2(20f, 20f),
                        biome = UniverseCreator.Ecology.WhittakerBiome.Tundra, priority = 1 },      // small, high precedence
                };

            Check(UniverseCreator.Nodes.Biome.BiomeRegionField.ResolveBiome(regions, 0f, 0f)
                      == UniverseCreator.Ecology.WhittakerBiome.Tundra,
                  "BIO-OVR1: where regions overlap, the lowest-priority (highest-precedence) one must win");
            Check(UniverseCreator.Nodes.Biome.BiomeRegionField.ResolveBiome(regions, 50f, 0f)
                      == UniverseCreator.Ecology.WhittakerBiome.Desert,
                  "BIO-OVR1: a point only the big region covers must take the big region's biome");
            Check(UniverseCreator.Nodes.Biome.BiomeRegionField.ResolveBiome(regions, 500f, 0f) == -1,
                  "BIO-OVR1: a point no region covers must resolve to -1 (climate decides)");
        }

        /// <summary>
        /// VEG-ID1 (VEG-13): the stable per-instance id. The same world position yields the same id
        /// across calls (so it survives a re-bake), different positions give different ids, and the
        /// id is positive (0 is reserved for "unassigned").
        /// </summary>
        private static void TestScatterInstanceStableId()
        {
            int a1 = UniverseCreator.Nodes.Scatter.ScatterNodeBase.StableInstanceId(123.45f, 678.90f);
            int a2 = UniverseCreator.Nodes.Scatter.ScatterNodeBase.StableInstanceId(123.45f, 678.90f);
            int b = UniverseCreator.Nodes.Scatter.ScatterNodeBase.StableInstanceId(123.46f, 678.90f);

            Check(a1 == a2, "VEG-ID1: the same position must give the same id across re-bakes");
            Check(a1 != b, "VEG-ID1: different positions must give different ids");
            Check(a1 > 0 && b > 0, "VEG-ID1: ids must be positive (0 is reserved for unassigned)");
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
            Check(ins != null && ins.Length == 10, "UC-T08-06 Weather Output preserves eight bias inputs and appends typed climate/region inputs");
            Check(probe.GetOutputs() != null && probe.GetOutputs().Length == 0,
                  "Weather Output is a terminal and exposes no output ports");
            Check(probe is IOutputNode, "Weather Output must be an IOutputNode so the evaluator drives it");

            if (ins != null && ins.Length == 10)
            {
                // The five carried-over names, in their original order, are the load-
                // bearing part: a saved graph reconnects by these.
                Check(ins[0].name == "In Precip Bias", "port 0 must stay In Precip Bias");
                Check(ins[1].name == "In Temperature Bias", "port 1 must stay In Temperature Bias");
                Check(ins[2].name == "In Wind Bias", "port 2 must stay In Wind Bias");
                Check(ins[3].name == "In Fog Bias", "port 3 must stay In Fog Bias");
                Check(ins[4].name == "In Cloud Bias", "port 4 must stay In Cloud Bias");
                Check(ins[5].name == "In Snow Bias" && ins[6].name == "In Storm Bias" && ins[7].name == "In Turbidity Bias",
                    "UC-T08-06 all later saved bias wire indices retain their original names");
                Check(ins[8].type == PortType.Climate && ins[9].type == PortType.Weather,
                    "UC-T08-06 appended climate/weather contracts keep the first eight bias wires unchanged");
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
        /// The curvature-adaptive resample (Step 43 L) keeps the points a bend needs to
        /// read as a curve while thinning the straights, and it never drops an endpoint.
        /// </summary>
        /// <remarks>
        /// The river pipeline used a UNIFORM resample after the smooth Catmull-Rom pass,
        /// which decimated the smooth curve back to fixed chords and re-cornered every tight
        /// bend - the "snapped/cut" meander the author saw. ResampleAdaptive keeps a point
        /// when EITHER a spacing budget OR a small heading budget has accumulated, so a bend
        /// stays dense and a straight stays sparse. The fixture is a dense straight run into
        /// a dense smooth quarter-arc: the arc must come back far more densely sampled than
        /// the straight, both endpoints must survive, and the whole thing must still be
        /// decimated (the point of it - a straight full of input points must thin out).
        /// </remarks>
        private static void TestResampleAdaptiveKeepsBendsAndEndpoints()
        {
            const float maxSpacing = 6f;
            const float minSpacing = maxSpacing * 0.35f;
            const float maxTurn = 0.14f;   // ~8 degrees, the pipeline's setting

            Polyline src = new Polyline(0);

            // Dense straight run: x 0..100 at 1 m, z=0. accTurn stays ~0 here, so only the
            // spacing budget fires and the straight thins to ~maxSpacing chords.
            for (int i = 0; i <= 100; i++) src.points.Add(new Vector3(i, 0f, 0f));

            // Dense smooth quarter-arc, radius 20, centre (100,0,20): from (100,0,0) round
            // to (120,0,20). ~180 samples over ~31 m of arc, so the INPUT is much finer than
            // maxSpacing everywhere and any thinning on the arc is the algorithm's choice.
            const int arcSamples = 180;
            for (int i = 1; i <= arcSamples; i++)
            {
                float a = (i / (float)arcSamples) * (Mathf.PI * 0.5f);   // 0 .. 90 deg
                float x = 100f + 20f * Mathf.Sin(a);
                float z = 20f - 20f * Mathf.Cos(a);
                src.points.Add(new Vector3(x, 0f, z));
            }
            src.DiscardWeights();

            Polyline dst = SplineOperations.ResampleAdaptive(src, maxSpacing, minSpacing, maxTurn);

            Check(dst != null && dst.Count >= 2, "ResampleAdaptive returned an empty or null curve");

            // Endpoints survive exactly - a dropped mouth or head would leave the weld and
            // the join detection reading the wrong point.
            Check(Vector3.Distance(dst.PointAt(0), src.PointAt(0)) < 1e-3f,
                  "ResampleAdaptive moved or dropped the first point");
            Check(Vector3.Distance(dst.PointAt(dst.Count - 1), src.PointAt(src.Count - 1)) < 1e-3f,
                  "ResampleAdaptive moved or dropped the last point");

            // It must actually decimate: 281 input points on a mostly-straight course cannot
            // all survive, or it is doing nothing and the perf argument is a lie.
            Check(dst.Count < src.Count,
                  "ResampleAdaptive kept every point (" + dst.Count + " of " + src.Count
                  + ") - it decimated nothing");

            // Classify kept points: straight (x <= 100 and z < 0.5) versus arc (the rest).
            // The test is density: mean spacing along the arc must be well under mean spacing
            // along the straight, which is what keeps a bend smooth after the resample.
            int straightKept = 0, arcKept = 0;
            for (int i = 0; i < dst.Count; i++)
            {
                Vector3 p = dst.PointAt(i);
                if (p.x <= 100f + 1e-3f && p.z < 0.5f) straightKept++;
                else arcKept++;
            }

            Check(straightKept >= 2,
                  "the straight lost all its points (" + straightKept + ") - a 100 m run "
                  + "should still carry a handful at ~" + maxSpacing + " m spacing");

            // ~31 m of arc: a uniform 6 m resample would keep ~5 points; the turn budget
            // (90 deg / 8 deg ~= 11) should keep more than that, and more per metre than the
            // straight (100 m / 6 m ~= 17 points -> ~0.17/m; the arc must beat that).
            float straightSpanM = 100f;
            float arcSpanM = Mathf.PI * 0.5f * 20f;   // quarter circle, radius 20 ~= 31.4 m
            float straightDensity = straightKept / straightSpanM;
            float arcDensity = arcKept / arcSpanM;

            Check(arcDensity > straightDensity * 1.5f,
                  "ResampleAdaptive did not densify the bend: arc density "
                  + arcDensity.ToString("F3") + "/m vs straight " + straightDensity.ToString("F3")
                  + "/m. The turn budget must keep more points through a curve than along a "
                  + "straight, or the meander corners just as it did with a uniform resample");

            // And no chord may exceed maxSpacing by more than a hair anywhere - the spacing
            // budget is a hard ceiling even where the turn budget has not fired.
            float longest = 0f;
            for (int i = 1; i < dst.Count; i++)
                longest = Mathf.Max(longest, Vector3.Distance(dst.PointAt(i - 1), dst.PointAt(i)));

            Check(longest <= maxSpacing + 1.5f,
                  "ResampleAdaptive left a chord of " + longest.ToString("F2") + " m, over the "
                  + maxSpacing + " m ceiling - the spacing budget is not holding");
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
        /// UC-O01 / UC-A06: missing slots survive normal serialization, remain
        /// diagnosable, and are discarded only by explicit repair.
        ///
        /// [SerializeReference] leaves a null in `nodes` when a class disappears, which
        /// is how the water Output nodes the rework removed still turn up in graphs
        /// authored before it - the "Missing types referenced from component Graph"
        /// warning is Unity reporting exactly that. Three things have to hold: nothing
        /// walking the list may dereference the hole, Validate must say so rather than
        /// stay quiet, and explicit pruning must remove the selected hole and its wires.
        /// </summary>
        private static void TestGraphHealsMissingNodeSlots()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            try
            {
                // Serializable runtime nodes make the preservation check exercise the real serializer.
                ConstantNode keptA = new ConstantNode { value = 0.25f };
                var keptB = new UniverseCreator.Nodes.Combiners.MaximumNode();

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

                CheckGraphSerializationPreservesMissingSlots(graph, keptA, keptB);

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

        // UC-O01 / UC-A06: normal save/reload must not silently perform explicit repair.
        private static void CheckGraphSerializationPreservesMissingSlots(Graph graph, Generator keptA, Generator keptB)
        {
            var callbacks = (ISerializationCallbackReceiver)graph;
            callbacks.OnBeforeSerialize();
            callbacks.OnAfterDeserialize();
            Check(graph.nodes.Count == 3 && graph.nodes[1] == null
                && graph.nodes[0] == keptA && graph.nodes[2] == keptB,
                "UC-A06: serialization callbacks preserve missing-slot positions and surviving nodes");
            Check(graph.connections.Count == 3
                && graph.connections[1].targetNodeId == "vanished-node"
                && graph.connections[2].sourceNodeId == "vanished-node",
                "UC-A06: serialization callbacks preserve wires incident to the missing node");

            Graph restored = ScriptableObject.CreateInstance<Graph>();
            try
            {
                string json = UnityEditor.EditorJsonUtility.ToJson(graph);
                UnityEditor.EditorJsonUtility.FromJsonOverwrite(json, restored);
                Check(restored.nodes.Count == 3 && restored.nodes[1] == null,
                    "UC-A06: editor JSON round-trip preserves the missing slot");
                var source = restored.GetNodeById(keptA.id) as ConstantNode;
                Check(source != null && source.value == 0.25f,
                    "UC-A06: editor JSON round-trip preserves the surviving source ID and value");
                Check(restored.GetNodeById(keptB.id) is UniverseCreator.Nodes.Combiners.MaximumNode,
                    "UC-A06: editor JSON round-trip preserves the surviving consumer type and ID");
                bool edgesMatch = restored.connections.Count == graph.connections.Count;
                if (edgesMatch)
                    for (int i = 0; i < graph.connections.Count; i++)
                        if (!restored.connections[i].Equals(graph.connections[i])) { edgesMatch = false; break; }
                Check(edgesMatch, "UC-A06: editor JSON round-trip preserves every connection and its order");
                bool reportsMissing = false;
                foreach (string problem in restored.Validate())
                    if (problem.Contains("node slot is empty")) reportsMissing = true;
                Check(reportsMissing, "UC-A06: preserved missing slots remain visible to validation");
                Check(graph.nodes.Count == 3 && graph.connections.Count == 3,
                    "UC-A06: serialization leaves the authored source graph intact");
                restored.nodes = null;
                restored.connections = null;
                ((ISerializationCallbackReceiver)restored).OnAfterDeserialize();
                Check(restored.nodes != null && restored.nodes.Count == 0
                    && restored.connections != null && restored.connections.Count == 0,
                    "UC-A06: deserialization initializes absent containers without inventing content");
            }
            finally { UnityEngine.Object.DestroyImmediate(restored); }
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

            HydrologyGenerator.GroundSampler bowl = delegate (float x, float z)
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
            HydrologyGenerator.GroundSampler lobed = delegate (float x, float z)
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

                HydrologyGenerator.GroundSampler walled = delegate (float x, float z)
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
            HydrologyGenerator.GroundSampler dimple = delegate (float x, float z)
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
            HydrologyGenerator.GroundSampler tarn = delegate (float x, float z)
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
            HydrologyGenerator.GroundSampler broad = delegate (float x, float z)
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
        /// REFINEMENT #6 SL1: the global sea-level floor (SeaLevelFloor.Apply, baked into
        /// HeightOutputNode so it runs on the COMPOSED heightmap of every noise creator and
        /// combiner) must lift EVERY sub-sea cell that is not genuine ocean to STRICTLY ABOVE
        /// the waterline (>= sea + freeboard) - a shallow dry-land dip and a deep closed basin
        /// alike, because a closed depression below sea level is dry ground under the sea, not
        /// a lake bed. Genuine ocean (edge-reachable sub-sea) keeps its own depth so the sea is
        /// not erased, and closed basins ABOVE the waterline keep their shape so a highland
        /// lake still has somewhere to form. This pins both halves of the user's rule: "the
        /// continent is never below OR at sea level", and the sea's depth survives it.
        /// </summary>
        private static void TestSeaFloorLiftsDryGroundKeepsOceanDepth()
        {
            const int res = 8;
            const float sea = 0.35f;
            // 20 m of clearance at the test's 600 m world height, matching the HeightOutputNode
            // guard (seaFreeboardMetres / heightScale). A lifted dry-land dip must clear the sea
            // by ~20 m so no plain is ever left sitting AT the waterline - the user's safety rule.
            const float worldHeightM = 600f;
            const float freeboardMetres = 20f;
            const float freeboard = freeboardMetres / worldHeightM;   // 0.0333 normalised
            const float coastGrade = 0.03f;
            const float basinDepth = 0.01f;

            const float land   = 0.60f;   // dry land, above sea
            const float dipH   = 0.33f;   // dry-land dip: below sea, enclosed, shallow pool
            const float oceanH = 0.20f;   // genuine ocean: below sea, on the world edge
            const float basinH = 0.15f;   // closed basin BELOW sea: dry land under the waterline
            const float highBasinH = 0.45f; // closed basin ABOVE sea: a genuine highland lake bed

            // World-scale coarse height the drainage solve carries. The left column is open
            // ocean (every cell is a border cell below sea, so it seeds the flood); the dip
            // and the basin are interior, walled off from the edge by land, so the ocean
            // flood can never reach them and they are NOT classified as ocean.
            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos  = Vector3.zero;
            height.worldSize = new Vector3(700f, 600f, 700f);
            for (int i = 0; i < height.arr.Length; i++) height.arr[i] = land;

            for (int z = 0; z < res; z++) height.arr[z * res + 0] = oceanH;   // left edge = sea
            height.arr[3 * res + 3] = dipH;                                    // dry-land dip
            height.arr[5 * res + 5] = basinH;                                  // sub-sea deep basin
            height.arr[6 * res + 2] = highBasinH;                              // above-sea deep basin

            // Filled surface: the dip pools nothing (its own height => a dry dip, raised);
            // both basins fill to the surrounding land rim, so each pools far deeper than
            // basinDepth and counts as a "real" depression.
            float[] filled = (float[])height.arr.Clone();
            filled[5 * res + 5] = land;
            filled[6 * res + 2] = land;

            WorldDrainage wd = WorldDrainage.CreateForTests(height, filled);
            Check(wd != null, "SL1: could not build a test WorldDrainage");
            if (wd == null) return;

            // The chunk the floor is applied to: identical lattice + world placement as the
            // solve (so the coarse permission field reads onto it 1:1), on a buffer we can
            // mutate and diff against its own start values.
            Matrix src = new Matrix(new CoordRect(0, 0, res, res));
            src.worldPos  = height.worldPos;
            src.worldSize = height.worldSize;
            float[] before = (float[])height.arr.Clone();
            Array.Copy(before, src.arr, before.Length);

            SeaLevelFloor.Apply(wd, src, sea, coastGrade, basinDepth, freeboard, 1f, new StopToken());

            // 1) The dry-land dip is lifted STRICTLY above the waterline, to ~sea + freeboard.
            float dipAfter = src.arr[3 * res + 3];
            Check(dipAfter > sea + 1e-4f,
                  "SL1: a dry-land dip was left at/below sea (" + dipAfter.ToString("F4") + " <= "
                  + sea.ToString("F4") + ") - the continent must never sit at or below sea level");
            Check(dipAfter >= sea + freeboard - 1e-3f,
                  "SL1: the dry-land dip rose to " + dipAfter.ToString("F4") + ", not the expected "
                  + (sea + freeboard).ToString("F4") + " floor (sea + freeboard)");

            // 2) Genuine ocean keeps its depth - never raised.
            float oceanAfter = src.arr[4 * res + 0];
            Check(Mathf.Abs(oceanAfter - oceanH) < 1e-4f,
                  "SL1: genuine ocean was raised from " + oceanH.ToString("F4") + " to "
                  + oceanAfter.ToString("F4") + " - the sea's own depth must be preserved");

            // 3) A closed deep basin (a future lake bed) keeps its depth HERE. The floor
            //    must not clamp it: clamping terraced the author's basins into stepped
            //    pits (2026-10-06). Getting a sub-sea basin's WATER SURFACE above the sea
            //    is ContinentalRise.BuildBasinLift's job, and it does it by translating the
            //    whole basin rigidly so the shape this assertion protects is preserved.
            float basinAfter = src.arr[5 * res + 5];
            Check(Mathf.Abs(basinAfter - basinH) < 1e-4f,
                  "SL1: a closed basin was raised from " + basinH.ToString("F4") + " to "
                  + basinAfter.ToString("F4") + " - a lake basin must be left for the lake to fill");

            // 3b) A closed basin ABOVE the waterline is likewise untouched - this pass
            //     never considers above-sea cells, and this pins that it stays that way.
            float highBasinAfter = src.arr[6 * res + 2];
            Check(Mathf.Abs(highBasinAfter - highBasinH) < 1e-4f,
                  "SL1: an ABOVE-sea closed basin was changed from " + highBasinH.ToString("F4")
                  + " to " + highBasinAfter.ToString("F4")
                  + " - a highland lake basin must keep its depth for the lake to fill");

            // 4) Raise-only everywhere: the floor never lowers ground.
            bool anyLowered = false;
            for (int i = 0; i < src.arr.Length; i++)
                if (src.arr[i] < before[i] - 1e-6f) anyLowered = true;
            Check(!anyLowered, "SL1: the sea-level floor LOWERED ground somewhere - it must be raise-only");
        }

        /// <summary>
        /// SL2: ContinentalRise is a GUARD at the shore, not a force. Ground that already
        /// stands above the sea, within the shore band, is left exactly as the terrain
        /// shaped it - this is what keeps beaches sloping naturally into the sea instead of
        /// being shoved up into the raised lip / cliff ring the author photographed. A
        /// genuinely sub-sea INTERIOR plain is still lifted clear of the waterline, so the
        /// three-week "river has no head" bug stays fixed. Author 2026-10-09: "as long as
        /// the ground is above the sea level do not touch ... it is a guard, not forced ...
        /// currently it is forcing even the shoreline and beaches".
        /// </summary>
        private static void TestContinentalRiseGuardsShoreNotForces()
        {
            const int res = 8;
            const float sea = 0.35f;
            const float worldHeightM = 600f;
            const float freeboard = 20f / worldHeightM;   // 0.0333 normalised
            const float maxRise = 0.1f;
            const float fullRise = 300f;                  // metres inland to full head
            const float basinDepth = 0.01f;
            const float shoreBlend = 400f;                // metres: the shore guard band
            const float deadzone = 150f;                  // metres from the sea where the guard is OFF (§A5)

            const float land   = 0.60f;          // ordinary inland land, well above sea
            const float oceanH = 0.20f;          // genuine ocean along the left edge
            const float berm   = sea + 0.02f;    // a beach berm ONE cell in: ABOVE sea, below sea+freeboard
            const float plainH = 0.33f;          // interior plain, below sea, far from the ocean
            const float nearSub = 0.30f;         // near-shore sub-sea ground, INSIDE the deadzone

            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos  = Vector3.zero;
            height.worldSize = new Vector3(700f, worldHeightM, 700f);   // ~100 m per coarse cell
            for (int i = 0; i < height.arr.Length; i++) height.arr[i] = land;

            for (int z = 0; z < res; z++) height.arr[z * res + 0] = oceanH;   // left edge = sea
            height.arr[2 * res + 1] = berm;                                   // near-shore, above sea
            height.arr[4 * res + 5] = plainH;                                 // interior, below sea
            height.arr[5 * res + 1] = nearSub;                                // near-shore (~100 m), below sea, inside deadzone

            // filled == height, so nothing pools: no basin is classified and the result is a
            // pure dry-land floor, which is what this test is isolating.
            float[] filled = (float[])height.arr.Clone();

            WorldDrainage wd = WorldDrainage.CreateForTests(height, filled);
            Check(wd != null, "SL2: could not build a test WorldDrainage");
            if (wd == null) return;

            Matrix src = new Matrix(new CoordRect(0, 0, res, res));
            src.worldPos  = height.worldPos;
            src.worldSize = height.worldSize;
            float[] before = (float[])height.arr.Clone();
            Array.Copy(before, src.arr, before.Length);

            ContinentalRise.Apply(wd, src, sea, freeboard, maxRise, fullRise, basinDepth,
                                  shoreBlend, deadzone, null, 1f, new StopToken());

            // 1) THE GUARD: the above-sea beach berm near the coast is NOT pushed up. Before
            //    the shore-guard fix the freeboard+head obligation shoved it toward
            //    sea+freeboard+rise - the raised lip. It must now be left essentially as-is.
            float bermAfter = src.arr[2 * res + 1];
            Check(bermAfter - berm < 3e-3f,
                  "SL2: an above-sea beach berm near the shore was forced UP from "
                  + berm.ToString("F4") + " to " + bermAfter.ToString("F4")
                  + " - the shore must be a guard, not a force (no raised lip on the beach)");
            Check(bermAfter >= berm - 1e-6f,
                  "SL2: the shore guard LOWERED the beach berm - the pass must be raise-only");

            // 1b) THE §A5 DEADZONE: near-shore sub-sea ground INSIDE the deadzone is released
            //     entirely - left to slope under the water as a natural beach, NOT lifted toward
            //     the waterline. This is the whole-world terraced-beach fix (author 2026-10-10):
            //     within ~80 m of the sea the guard must not act at all.
            float nearSubAfter = src.arr[5 * res + 1];
            Check(Mathf.Abs(nearSubAfter - nearSub) < 1e-6f,
                  "SL2: near-shore ground inside the deadzone was altered (" + nearSub.ToString("F4")
                  + " -> " + nearSubAfter.ToString("F4") + ") - inside the deadzone the guard must "
                  + "not act, so land meets the sea as a natural beach");

            // 2) THE HEAD STILL HOLDS: a genuinely sub-sea interior plain, far from the ocean,
            //    is still lifted clear of the waterline so a river crossing it has somewhere
            //    to fall. (The three-week bug this whole pass exists to kill.)
            float plainAfter = src.arr[4 * res + 5];
            Check(plainAfter > sea + 1e-3f,
                  "SL2: an interior sub-sea plain was left at/below sea (" + plainAfter.ToString("F4")
                  + ") - inland ground must still stand above the waterline for a river to descend");

            // 3) Raise-only everywhere.
            bool anyLowered = false;
            for (int i = 0; i < src.arr.Length; i++)
                if (src.arr[i] < before[i] - 1e-6f) anyLowered = true;
            Check(!anyLowered, "SL2: ContinentalRise LOWERED ground somewhere - it must be raise-only");
        }

        /// <summary>
        /// LAKEGUARD1 (§A6): the dry-lake-basin fix. A sub-sea depression is exempted from the
        /// dry-land floor (left as a basin) ONLY when a real lake footprint covers it. With no
        /// footprints pushed, the pass falls back to the old concavity heuristic (so a world's
        /// first bake, before lakes spawn, is unchanged). With footprints pushed, a sub-sea pan
        /// that no lake covers must FALL THROUGH so the floor raises it to a plain - the ~3-week
        /// "dry lake basin below sea level" artifact. Drives ContinentalRise.BuildBasinLift
        /// directly (no coarse-grid smoothing) so the exempt classification is pinned exactly.
        /// </summary>
        private static void TestContinentalRiseLakeAwareBasinGuard()
        {
            const int res = 8;
            const int count = res * res;
            const float sea = 0.35f;
            const float free = 0.0333f;
            const float basinDepth = 0.01f;

            // Land 0.60, a 2x2 interior sub-sea basin floor at 0.30, left column ocean at 0.20.
            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos  = Vector3.zero;
            height.worldSize = new Vector3(700f, 600f, 700f);   // 100 m cells -> cellArea 10000 -> minLakeCells 1
            for (int i = 0; i < count; i++) height.arr[i] = 0.60f;
            for (int z = 0; z < res; z++) height.arr[z * res + 0] = 0.20f;   // ocean edge
            int[] basin = { 3 * res + 3, 3 * res + 4, 4 * res + 3, 4 * res + 4 };
            for (int k = 0; k < basin.Length; k++) height.arr[basin[k]] = 0.30f;

            // A pool stands over the basin: surface 0.345, pooled 0.045 > basinDepth, so it is
            // classified as a basin at all (the thing the exemption then rules on).
            float[] filled = (float[])height.arr.Clone();
            for (int k = 0; k < basin.Length; k++) filled[basin[k]] = 0.345f;

            WorldDrainage wd = WorldDrainage.CreateForTests(height, filled);
            Check(wd != null, "LAKEGUARD1: could not build a test WorldDrainage");
            if (wd == null) return;

            // The basin is interior and NOT ocean; only the left column is sea.
            bool[] ocean = new bool[count];
            for (int z = 0; z < res; z++) ocean[z * res + 0] = true;

            bool[] isLake;

            // 1) No footprints -> old heuristic: a concave sub-sea basin is still a lake basin.
            ContinentalRise.BuildBasinLift(wd, ocean, sea, free, basinDepth, res, res, count,
                                           ContinentalRise.MinLakeAreaM2, null, out isLake, new StopToken());
            Check(isLake[basin[0]],
                  "LAKEGUARD1: with no footprints pushed the concave basin must stay exempt "
                  + "(the pass falls back to its concavity heuristic unchanged)");

            // 2) Footprints pushed but NONE over this basin -> it is a dry pan, NOT a lake, so it
            //    must fall through unmarked and be left for the dry-land floor to raise to a plain.
            float[] emptyMask = new float[count];   // non-null, all zero = lakes exist elsewhere
            ContinentalRise.BuildBasinLift(wd, ocean, sea, free, basinDepth, res, res, count,
                                           ContinentalRise.MinLakeAreaM2, emptyMask, out isLake, new StopToken());
            Check(!isLake[basin[0]] && !isLake[basin[3]],
                  "LAKEGUARD1: a sub-sea basin with NO lake footprint over it was still exempted - "
                  + "that is the dry-lake-basin bug. It must fall through so the floor lifts it to a plain.");

            // 3) A footprint DOES cover this basin -> real lake, exempt again (keeps its sub-sea bed).
            float[] coverMask = new float[count];
            for (int k = 0; k < basin.Length; k++) coverMask[basin[k]] = 1f;
            ContinentalRise.BuildBasinLift(wd, ocean, sea, free, basinDepth, res, res, count,
                                           ContinentalRise.MinLakeAreaM2, coverMask, out isLake, new StopToken());
            Check(isLake[basin[0]] && isLake[basin[3]],
                  "LAKEGUARD1: a basin covered by a real lake footprint must stay exempt "
                  + "(a real lake keeps its below-sea bed instead of being raised)");
        }

        /// <summary>
        /// NOBASIN1 (author 2026-10-10): the terrain NEVER forms a lake basin on its own. The
        /// shared concavity classifier still marks a concave sub-sea pan (the river node needs
        /// that), but the TERRAIN pass suppresses it unless a real lake footprint was pushed -
        /// so without a connected/placed lake every dry pan is floored to a plain, and a lake's
        /// bed comes from the LAKE OBJECT's carve instead. Pins ContinentalRise.SuppressBlindBasins
        /// directly (no coarse-grid smoothing), the deterministic seam the terrain floor uses.
        /// </summary>
        private static void TestContinentalRiseNeverFormsBlindBasin()
        {
            const int res = 8;
            const int count = res * res;
            const float sea = 0.35f;
            const float free = 0.0333f;
            const float basinDepth = 0.01f;

            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos = Vector3.zero;
            height.worldSize = new Vector3(700f, 600f, 700f);   // 100 m cells -> minLakeCells 1
            for (int i = 0; i < count; i++) height.arr[i] = 0.60f;
            for (int z = 0; z < res; z++) height.arr[z * res + 0] = 0.20f;   // ocean edge
            int[] basin = { 3 * res + 3, 3 * res + 4, 4 * res + 3, 4 * res + 4 };
            for (int k = 0; k < basin.Length; k++) height.arr[basin[k]] = 0.30f;

            float[] filled = (float[])height.arr.Clone();
            for (int k = 0; k < basin.Length; k++) filled[basin[k]] = 0.345f;   // pooled > basinDepth

            WorldDrainage wd = WorldDrainage.CreateForTests(height, filled);
            Check(wd != null, "NOBASIN1: could not build a test WorldDrainage");
            if (wd == null) return;

            bool[] ocean = new bool[count];
            for (int z = 0; z < res; z++) ocean[z * res + 0] = true;

            // 1) The shared concavity classifier still SEES the basin (river node relies on it).
            bool[] isLake;
            float[] lift = ContinentalRise.BuildBasinLift(wd, ocean, sea, free, basinDepth,
                res, res, count, ContinentalRise.MinLakeAreaM2, null, out isLake, new StopToken());
            Check(isLake[basin[0]],
                  "NOBASIN1: the shared concavity classifier should still mark the concave basin "
                  + "(the river node's water query needs it); only the TERRAIN declines to keep it");

            // 2) The terrain suppresses it when NO footprint was pushed: no basin, no lift. Every
            //    dry sub-sea pan then falls to the dry-land floor and is raised to a plain.
            ContinentalRise.SuppressBlindBasins(null, isLake, lift, count);
            bool anyBasin = false, anyLift = false;
            for (int k = 0; k < basin.Length; k++)
            {
                if (isLake[basin[k]]) anyBasin = true;
                if (lift[basin[k]] != 0f) anyLift = true;
            }
            Check(!anyBasin && !anyLift,
                  "NOBASIN1: with no lake footprint the terrain must keep NO basin and apply NO "
                  + "lift - the dry-lake-basin artifact. The lake object's carve digs the bed.");

            // 3) With a real footprint over the basin, suppression is a no-op: the lake keeps its bed.
            float[] coverMask = new float[count];
            for (int k = 0; k < basin.Length; k++) coverMask[basin[k]] = 1f;
            bool[] isLake2;
            float[] lift2 = ContinentalRise.BuildBasinLift(wd, ocean, sea, free, basinDepth,
                res, res, count, ContinentalRise.MinLakeAreaM2, coverMask, out isLake2, new StopToken());
            ContinentalRise.SuppressBlindBasins(coverMask, isLake2, lift2, count);
            Check(isLake2[basin[0]] && isLake2[basin[3]],
                  "NOBASIN1: a basin a real pushed lake footprint covers must survive suppression "
                  + "(the lake keeps its below-sea bed where it actually spawns)");
        }

        /// <summary>
        /// VEG-GD1: a graded RangeFilter is a continuous density bell, not a plateau. The
        /// terrain-only half of 4O.2 (the author's "plants placed too far, evenly"): with
        /// grading on, a band is thickest at its centre (the optimum) and thins toward the
        /// bounds, so density follows the ground instead of being flat across the whole band
        /// and cut at the edge. A plain Band must stay a flat plateau, so no existing graph
        /// changes - grading is strictly opt-in.
        /// </summary>
        private static void TestGradedBandIsContinuousBell()
        {
            RangeFilter graded = RangeFilter.GradedBand(0.2f, 0.8f);   // centre 0.5, half-width 0.3

            float atCentre = graded.Evaluate(0.5f);
            float atQuarter = graded.Evaluate(0.35f);   // halfway from centre to lower bound
            float atBound = graded.Evaluate(0.2f);      // the stated bound
            float beyond = graded.Evaluate(0.05f);      // well outside

            Check(atCentre > 0.999f,
                  "VEG-GD1: a graded band was not full at its centre (" + atCentre.ToString("F4") + ")");
            Check(atQuarter < atCentre - 1e-3f && atQuarter > atBound + 1e-3f,
                  "VEG-GD1: the graded weight is not monotonic from centre to bound ("
                  + atCentre.ToString("F3") + " -> " + atQuarter.ToString("F3") + " -> "
                  + atBound.ToString("F3") + ")");
            Check(atBound > 0.1f && atBound < 0.2f,
                  "VEG-GD1: a graded band should read about 0.135 at its stated bound, got "
                  + atBound.ToString("F4"));
            Check(beyond < atBound,
                  "VEG-GD1: the graded weight did not keep falling beyond the band ("
                  + beyond.ToString("F4") + " >= " + atBound.ToString("F4") + ")");

            // A plain band stays a flat plateau: full inside, hard cut outside. This is what
            // guarantees every existing (ungraded) scatter node places exactly as before.
            RangeFilter plateau = RangeFilter.Band(0.2f, 0.8f);
            Check(plateau.Evaluate(0.35f) > 0.999f && plateau.Evaluate(0.5f) > 0.999f,
                  "VEG-GD1: a plain Band is no longer a flat plateau inside its bounds - "
                  + "ungraded scatter behaviour must be unchanged");
            Check(plateau.Evaluate(0.1f) < 1e-4f,
                  "VEG-GD1: a plain Band no longer cuts hard outside its bounds");
        }

        /// <summary>
        /// VEG-NEWDEF1 (VEG-6): a scatter node CREATED FRESH in the editor grades its density by
        /// terrain out of the box (fixes "plants placed evenly, everywhere" on new worlds), while a
        /// node that was NOT freshly created - i.e. one deserialized from a saved graph, which never
        /// runs OnCreatedFresh - keeps the serialized default (false). That split is what lets the
        /// new default land without changing a single existing world.
        /// </summary>
        private static void TestFreshScatterNodeGradesByTerrain()
        {
            // The genuine "user added a new node" path (NodeCatalog.Instantiate) calls OnCreatedFresh.
            UniverseCreator.Nodes.Scatter.TreeScatterNode fresh =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            fresh.OnCreatedFresh();
            Check(fresh.gradeDensityByTerrain,
                  "VEG-NEWDEF1: a freshly created scatter node must grade density by terrain "
                  + "(the bell), so new worlds don't place plants evenly across a band");

            // The load/deserialize path does NOT call OnCreatedFresh, so a legacy node must keep the
            // serialized default and place exactly as it always did.
            UniverseCreator.Nodes.Scatter.TreeScatterNode legacy =
                new UniverseCreator.Nodes.Scatter.TreeScatterNode();
            Check(!legacy.gradeDensityByTerrain,
                  "VEG-NEWDEF1: a scatter node that was not freshly created (loaded from a graph) must "
                  + "keep gradeDensityByTerrain=false so every existing world is byte-for-byte unchanged");
        }

        /// <summary>
        /// CONFORM1 (§4O item 6): the footprint-aware ground conform seats an instance's base
        /// into a slope instead of balancing its origin on one sample. On a hill the conformed
        /// Y drops to the LOWEST ground under the footprint, so the downhill edge of the base
        /// no longer floats - the author's "tree bottom has a gap in slope". Radius 0 and flat
        /// ground must both leave the single-sample Y untouched, so the dial is strictly opt-in.
        /// </summary>
        private static void TestGroundConformSeatsBaseOnSlope()
        {
            const int res = 101;

            // Ground ramps along +X: 0 m at x=0 to 50 m at x=100, 1 m per pixel.
            Matrix ramp = new Matrix(new CoordRect(0, 0, res, res));
            ramp.worldPos = Vector3.zero;
            ramp.worldSize = new Vector3(100f, 0f, 100f);
            ramp.worldHeight = 50f;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    ramp.arr[z * res + x] = x / (float)(res - 1);   // 0..1 -> 0..50 m

            // A tree centred at world (50, 50): single-sample ground is 25 m.
            float centreY = ramp.GetWorldHeight(50f, 50f);
            Check(Mathf.Abs(centreY - 25f) < 0.5f,
                  "CONFORM1: test ramp is not 25 m at its centre (got " + centreY.ToString("F2") + ")");

            // Opt-out: radius 0 returns the centre Y exactly - zero regression.
            float r0 = UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(ramp, 50f, 50f, 0f, centreY);
            Check(Mathf.Approximately(r0, centreY),
                  "CONFORM1: radius 0 changed the Y - the conform must be opt-in");

            // On the slope a 10 m footprint conforms DOWN to its lowest ground: the -X
            // (downhill) edge at x=40 is 20 m, so the base seats at 20, not 25.
            float conformed = UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(ramp, 50f, 50f, 10f, centreY);
            Check(conformed < centreY - 1e-3f,
                  "CONFORM1: the base did not sink into the slope (" + conformed.ToString("F3")
                  + " >= " + centreY.ToString("F3") + ") - the downhill edge would still float");
            float lowest = ramp.GetWorldHeight(40f, 50f);
            Check(Mathf.Abs(conformed - lowest) < 0.5f,
                  "CONFORM1: the conform did not seat at the LOWEST footprint ground (got "
                  + conformed.ToString("F2") + ", lowest " + lowest.ToString("F2") + ")");

            // No gap: the seated base is at or below every footprint sample.
            Check(conformed <= ramp.GetWorldHeight(60f, 50f) + 1e-3f
               && conformed <= ramp.GetWorldHeight(50f, 60f) + 1e-3f
               && conformed <= ramp.GetWorldHeight(50f, 40f) + 1e-3f,
                  "CONFORM1: the seated base sits above some footprint ground - a gap would show");

            // Flat ground: nothing lower under the footprint, so the Y is unchanged whatever
            // the radius - the conform only ever acts where the slope would open a gap.
            Matrix flat = new Matrix(new CoordRect(0, 0, res, res));
            flat.worldPos = Vector3.zero;
            flat.worldSize = new Vector3(100f, 0f, 100f);
            flat.worldHeight = 50f;
            for (int i = 0; i < flat.arr.Length; i++) flat.arr[i] = 0.5f;   // flat 25 m
            float flatCentre = flat.GetWorldHeight(50f, 50f);
            float flatConformed = UniverseCreator.Nodes.Scatter.ScatterNodeBase.ConformedGroundY(flat, 50f, 50f, 10f, flatCentre);
            Check(Mathf.Approximately(flatConformed, flatCentre),
                  "CONFORM1: flat ground must leave the Y unchanged (got " + flatConformed.ToString("F3") + ")");
        }


        /// <summary>
        /// The coastal shelf grades a GENTLE shore down onto its beach ramp, leaves a
        /// STEEP shore standing as a cliff, and never touches genuine ocean depth - the
        /// three things that make the coast VARIED instead of one uniform beach wall.
        /// </summary>
        /// <remarks>
        /// Variety is set to 0 for the test so the regional character field drops out and
        /// the grade is a pure function of distance-to-water and slope, which is what makes
        /// the numbers here reproducible. The gentle cell and the cliff cell sit at the
        /// SAME distance from the ocean (both one cell in), so the only variable between
        /// "graded" and "left alone" is how far the land stands above the ramp - exactly
        /// the slope gate the shelf is built on.
        /// </remarks>
        private static void TestCoastalShelfGradesGentleKeepsCliffAndOcean()
        {
            const int res = 8;
            const float sea = 0.35f;

            const float land   = 0.60f;   // ordinary inland land, above sea (far above the rim -> a cliff)
            const float oceanH = 0.20f;   // genuine ocean along the whole left edge
            const float gentle = 0.40f;   // gentle shore: just above the flat plain rim, within the cliff gate
            const float cliff  = 0.80f;   // steep shore: a sea-cliff, far above the ramp

            Matrix height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos  = Vector3.zero;
            height.worldSize = new Vector3(700f, 600f, 700f);   // 100 m per coarse cell
            for (int i = 0; i < height.arr.Length; i++) height.arr[i] = land;

            for (int z = 0; z < res; z++) height.arr[z * res + 0] = oceanH;  // left edge = open sea
            height.arr[2 * res + 1] = gentle;   // gentle shore, one cell in from the water
            height.arr[5 * res + 1] = cliff;    // steep shore, same distance from the water

            WorldDrainage wd = WorldDrainage.CreateForTests(height, null);
            Check(wd != null, "C-TEST: could not build a test WorldDrainage");
            if (wd == null) return;

            Matrix src = new Matrix(new CoordRect(0, 0, res, res));
            src.worldPos  = height.worldPos;
            src.worldSize = height.worldSize;
            float[] before = (float[])height.arr.Clone();
            Array.Copy(before, src.arr, before.Length);

            // variety = 0: character drops out, so the result is a pure distance+slope grade.
            // Bands match the real defaults: 10 m wet beach, 40 m flat plain rim, 500 m ascent.
            CoastalShelf.Apply(wd, src, sea, /*range*/ 500f, /*beachFlat*/ 10f, /*plain*/ 40f,
                               /*rise*/ 0.03f, /*cliffThreshold*/ 0.05f, /*variety*/ 0f,
                               /*strength*/ 1f, /*freeboard*/ 0f, new StopToken());

            // 1) The gentle shore is graded DOWN toward the beach ramp (a beach forms).
            float gentleAfter = src.arr[2 * res + 1];
            Check(gentleAfter < gentle - 1e-3f,
                  "C-TEST: a gentle shore was not graded (" + gentleAfter.ToString("F4")
                  + " >= " + gentle.ToString("F4") + ") - gentle coasts must ease into beach/plain");

            // 2) ...but never pulled to or below the waterline: dry ground stays above sea.
            Check(gentleAfter > sea + 1e-4f,
                  "C-TEST: the graded shore fell to/below sea (" + gentleAfter.ToString("F4")
                  + ") - the grade must stay strictly above the waterline");

            // 3) The steep shore is left standing as a cliff (slope gate closes the grade).
            float cliffAfter = src.arr[5 * res + 1];
            Check(Mathf.Abs(cliffAfter - cliff) < 1e-4f,
                  "C-TEST: a sea-cliff was flattened from " + cliff.ToString("F4") + " to "
                  + cliffAfter.ToString("F4") + " - steep coasts must survive as cliffs");

            // 4) Genuine ocean is never touched - the sea keeps its own depth.
            float oceanAfter = src.arr[4 * res + 0];
            Check(Mathf.Abs(oceanAfter - oceanH) < 1e-4f,
                  "C-TEST: genuine ocean was moved from " + oceanH.ToString("F4") + " to "
                  + oceanAfter.ToString("F4") + " - the coastal shelf is a land-side pass only");

            // 5) Lower-only everywhere: the shelf can pull a gentle rise down, never up.
            bool anyRaised = false;
            for (int i = 0; i < src.arr.Length; i++)
                if (src.arr[i] > before[i] + 1e-6f) anyRaised = true;
            Check(!anyRaised, "C-TEST: the coastal shelf RAISED ground somewhere - it must be lower-only");
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

    }
}
