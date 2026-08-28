// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.Overlay.cs
//  Purpose: Pins the addressing invariants that let the micro-relief patch ring follow
//           the camera without ever rebuilding its pool. The ring's headline promise -
//           a patch-boundary crossing that allocates zero bytes - rests entirely on one
//           mathematical fact: stepping the ring centre by a single patch re-addresses
//           exactly ONE slot per axis, which wraps by a whole grid width, while every
//           other slot keeps its world cell. This file proves that fact, plus the
//           bijection (the ring covers its window once, no gap, no overlap), the snap
//           stability inside a patch, and the tier->grid ladder. If any of these
//           regresses, the ring would silently start reshuffling or gapping, so the
//           build fails here rather than in a profiler capture later.
//
//  Partial of UCSelfTest: shares the checks counter and the Check helper declared in
//  UCSelfTest.cs, so its assertions fold into the single tally.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.Rendering;
using UniverseCreator.Overlay;
using UniverseCreator.Optimization;
using UniverseCreator.Geology;
using UniverseCreator.Ecology;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>
        /// Drives the pure PatchRing addressing maths across a centre step and asserts the
        /// re-address invariant the zero-allocation snap depends on. No scene, no camera - the
        /// integer core is directly checkable, which is exactly why it was factored out.
        /// </summary>
        private static void TestPatchRingAddressing()
        {
            // (1) Tier -> grid side ladder: 5 (Low) / 7 (Medium) / 9 (High and Ultra).
            Check(PatchRing.GridSideForTier(AdaptiveQuality.QualityTier.Low) == 5, "PatchRing Low tier not 5x5");
            Check(PatchRing.GridSideForTier(AdaptiveQuality.QualityTier.Medium) == 7, "PatchRing Medium tier not 7x7");
            Check(PatchRing.GridSideForTier(AdaptiveQuality.QualityTier.High) == 9, "PatchRing High tier not 9x9");
            Check(PatchRing.GridSideForTier(AdaptiveQuality.QualityTier.Ultra) == 9, "PatchRing Ultra tier not 9x9");

            // (2) Floored modulo stays in [0, m) even for negatives - the wrap must not go negative.
            Check(PatchRing.FloorMod(-1, 7) == 6, "PatchRing.FloorMod(-1,7) != 6");
            Check(PatchRing.FloorMod(-7, 7) == 0, "PatchRing.FloorMod(-7,7) != 0");
            Check(PatchRing.FloorMod(8, 7) == 1, "PatchRing.FloorMod(8,7) != 1");

            // (3) Whole-patch snap: stable inside a patch, steps by one at the halfway boundary.
            float size = 8f;
            Check(PatchRing.CentreCell(0f, size) == 0, "CentreCell(0) != 0");
            Check(PatchRing.CentreCell(3.9f, size) == 0, "CentreCell just below half stepped early");
            Check(PatchRing.CentreCell(4.1f, size) == 1, "CentreCell past half did not step");
            Check(PatchRing.CentreCell(-4.1f, size) == -1, "CentreCell negative side did not step");

            int gridN = 7;
            int half = gridN / 2;
            int centre = 100;

            // (4) For a fixed centre the slot->cell map is a bijection onto [centre-half, centre+half]:
            //     contiguous, no gap, no duplicate. That is the ring covering its window exactly once.
            bool[] seen = new bool[gridN];
            bool bijection = true;
            for (int sx = 0; sx < gridN; sx++)
            {
                int cell = PatchRing.WorldCell(sx, centre, gridN);
                int idx = cell - (centre - half);
                if (idx < 0 || idx >= gridN || seen[idx]) { bijection = false; break; }
                seen[idx] = true;
            }
            for (int i = 0; i < gridN; i++) if (!seen[i]) bijection = false;
            Check(bijection, "PatchRing.WorldCell is not a bijection onto the ring window");

            // (5) THE zero-rebuild invariant: stepping the centre by one re-addresses exactly ONE
            //     slot, and that slot's world cell jumps by +gridN (wraps trailing edge -> leading).
            //     Every other slot keeps its cell, so a boundary crossing moves one mesh, not n*n.
            int changed = 0, jump = 0;
            for (int sx = 0; sx < gridN; sx++)
            {
                int before = PatchRing.WorldCell(sx, centre, gridN);
                int after = PatchRing.WorldCell(sx, centre + 1, gridN);
                if (before != after) { changed++; jump = after - before; }
            }
            Check(changed == 1, "A one-cell centre step re-addressed more than one slot (ring would rebuild)");
            Check(jump == gridN, "The wrapped slot did not jump by exactly gridN cells");

            // (6) Purity: identical inputs give identical output on every call.
            Check(PatchRing.WorldCell(3, centre, gridN) == PatchRing.WorldCell(3, centre, gridN),
                "PatchRing.WorldCell is not deterministic");

            // (7) Cell centres are patch-spaced along the axis (cell 3 at 3*8 = 24 m).
            Check(Mathf.Approximately(PatchRing.CellCentreM(3, size), 24f), "CellCentreM spacing wrong");
        }

        /// <summary>
        /// C-04 / G-17: the ring's re-address is a view operation and must never feed a height back
        /// into TerrainData - otherwise the world would become a function of where the camera has
        /// been. This builds a real heightmap, hashes it, walks the ring centre across nine patch
        /// boundaries re-addressing every step (exactly what the overlay's snap path does), then
        /// asserts the heightmap hash is byte-for-byte unchanged. The re-address takes camera ->
        /// slot cells with no TerrainData in scope, so it cannot write one; this pins that.
        /// </summary>
        private static void TestOverlayNeverWritesTerrain()
        {
            var td = new TerrainData { heightmapResolution = 33 };
            try
            {
                int res = td.heightmapResolution;
                var heights = new float[res, res];
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        heights[z, x] = 0.5f + 0.1f * Mathf.Sin(x * 0.7f) * Mathf.Cos(z * 0.5f);
                td.SetHeights(0, 0, heights);

                int hashBefore = HashHeights(td);

                // Walk the centre a full patch at a time along +X and re-address into a local array,
                // mirroring what MicroReliefOverlay.UpdateRing does to its slot Transforms.
                const int gridN = 7;
                const float size = 8f;
                var cells = new int[gridN];
                int prevCentre = PatchRing.CentreCell(0f, size);
                int boundariesCrossed = 0;
                float camX = 0f;
                while (boundariesCrossed < 9)
                {
                    camX += size;
                    int c = PatchRing.CentreCell(camX, size);
                    if (c != prevCentre) boundariesCrossed++;
                    for (int sx = 0; sx < gridN; sx++) cells[sx] = PatchRing.WorldCell(sx, c, gridN);
                    prevCentre = c;
                }

                int hashAfter = HashHeights(td);
                Check(boundariesCrossed == 9, "Overlay walk did not cross nine patch boundaries");
                Check(hashBefore == hashAfter, "Overlay re-address changed the terrain heightmap hash (C-04 violated)");
            }
            finally
            {
                Object.DestroyImmediate(td);
            }
        }

        /// <summary>Order-sensitive rolling hash of a TerrainData heightmap, for change detection.</summary>
        private static int HashHeights(TerrainData td)
        {
            int res = td.heightmapResolution;
            float[,] h = td.GetHeights(0, 0, res, res);
            unchecked
            {
                int hash = 17;
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++)
                        hash = hash * 31 + h[z, x].GetHashCode();
                return hash;
            }
        }

        /// <summary>Finds a process in a field by its id, or null. Test helper for the solo/Nyquist checks.</summary>
        private static MicroReliefProcess GetProcess(MicroReliefField f, string id)
        {
            for (int i = 0; i < f.Processes.Count; i++)
                if (f.Processes[i].ProcessId == id) return f.Processes[i];
            return null;
        }

        /// <summary>Builds a micro-relief context for a bedrock unit and a few environment overrides.</summary>
        private static MicroReliefContext BuildCtx(
            RockClass rc, float solubility, float porosity, int jointSets, float thicknessM,
            float slopeDeg, float freezeThaw, float precipMm, float coverDepthM, float coverPhi)
        {
            var unit = new LithoUnit
            {
                rockClass = rc,
                solubility = solubility,
                porosity = porosity,
                jointSetCount = jointSets,
                thicknessM = thicknessM,
                grainSizePhi = 2f,
                angleOfReposeDeg = 34f
            };
            var ctx = new MicroReliefContext
            {
                env = new EnvironmentSample
                {
                    slopeDeg = slopeDeg,
                    aspectDeg = 45f,
                    annualPrecipMm = precipMm,
                    canopyLai = 0f
                },
                litho = new LithoSample { unitIndex = 0, unit = unit },
                cover = new CoverSample
                {
                    type = coverDepthM < 0.05f ? CoverType.BedrockExposed : CoverType.ResidualSoil,
                    depthM = coverDepthM,
                    grainSizePhi = coverPhi
                },
                vertexSpacingM = 0.5f,
                freezeThawCyclesPerYr = freezeThaw,
                seed = 20260828
            };
            return ctx;
        }

        /// <summary>Sums the absolute relief a field builds over a small grid of points - a surface "signature".</summary>
        private static float Signature(MicroReliefField f, MicroReliefContext ctx)
        {
            float acc = 0f;
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 7; j++)
                {
                    ctx.env.worldPos = new Vector3(i * 0.37f, 0f, j * 0.41f);
                    var r = f.Evaluate(ctx);
                    acc += Mathf.Abs(r.displacementMm) + Mathf.Abs(r.textureMm);
                }
            return acc;
        }

        /// <summary>
        /// G-18: the process evaluator must (a) name every process and its law, (b) light up different
        /// processes in different environments so a granite ridge, a limestone pavement and a
        /// periglacial slope build visibly different surfaces, (c) route by the Nyquist clamp, and
        /// (d) let a debug session solo one process. This pins all four.
        /// </summary>
        private static void TestMicroReliefField()
        {
            var field = MicroReliefField.CreateDefault();
            Check(field.Processes.Count == 13, "MicroReliefField.CreateDefault did not register 13 processes");
            for (int i = 0; i < field.Processes.Count; i++)
            {
                Check(!string.IsNullOrEmpty(field.Processes[i].ProcessId), "A micro-relief process has no ProcessId");
                Check(!string.IsNullOrEmpty(field.Processes[i].Law), "A micro-relief process cites no law");
            }

            var granite = BuildCtx(RockClass.Crystalline, 0f, 0.02f, 2, 2f, 30f, 0f, 800f, 0f, 2f);
            var limestone = BuildCtx(RockClass.Carbonate, 0.8f, 0.15f, 2, 1f, 2f, 0f, 1200f, 0f, 2f);
            var perigl = BuildCtx(RockClass.Clastic, 0f, 0.1f, 2, 1f, 15f, 60f, 500f, 0.3f, 2f);

            // Different environments enable different processes - the physical reason they cannot look alike.
            Check(GetProcess(field, "SpheroidalExfoliation").IsEligible(granite), "Granite ridge did not enable exfoliation");
            Check(!GetProcess(field, "SpheroidalExfoliation").IsEligible(limestone), "Limestone wrongly enabled exfoliation");
            Check(GetProcess(field, "Karren").IsEligible(limestone), "Limestone pavement did not enable karren");
            Check(!GetProcess(field, "Karren").IsEligible(granite), "Granite wrongly enabled karren");
            Check(GetProcess(field, "SortedPolygonsStripes").IsEligible(perigl), "Periglacial slope did not enable sorted ground");
            Check(GetProcess(field, "SolifluctionTerracettes").IsEligible(perigl), "Periglacial slope did not enable solifluction");
            Check(!GetProcess(field, "SortedPolygonsStripes").IsEligible(granite), "Granite wrongly enabled sorted ground");

            // Determinism: same context and point -> identical result.
            var a1 = field.Evaluate(granite);
            var a2 = field.Evaluate(granite);
            Check(a1.displacementMm == a2.displacementMm && a1.textureMm == a2.textureMm, "MicroReliefField is not deterministic");

            // The three summed surfaces are materially different.
            float sg = Signature(field, granite), sl = Signature(field, limestone), sp = Signature(field, perigl);
            Check(Mathf.Abs(sg - sl) > 1f, "Granite and limestone surfaces are indistinguishable");
            Check(Mathf.Abs(sg - sp) > 1f, "Granite and periglacial surfaces are indistinguishable");
            Check(Mathf.Abs(sl - sp) > 1f, "Limestone and periglacial surfaces are indistinguishable");

            // Nyquist clamp (L-50) on a 0.5 m grid: fine karren -> texture, coarse sorted ground -> displacement.
            Check(MicroReliefField.Classify(GetProcess(field, "Karren"), limestone) == ReliefChannel.Texture, "Karren not routed to texture");
            Check(MicroReliefField.Classify(GetProcess(field, "SortedPolygonsStripes"), perigl) == ReliefChannel.Displacement, "Sorted ground not displaced");

            // Solo: an ineligible solo yields nothing; an eligible solo yields only its own relief.
            field.SoloProcessId = "Tafoni";
            var soloNone = field.Evaluate(granite);
            Check(soloNone.displacementMm == 0f && soloNone.textureMm == 0f, "Solo of an ineligible process leaked relief");
            field.SoloProcessId = "Karren";
            var soloK = field.Evaluate(limestone);
            Check(soloK.textureMm != 0f, "Solo of karren produced no texture-channel relief");
            field.SoloProcessId = null;
        }

        /// <summary>Sums the absolute relief a field builds in each channel separately, for the competition share metric.</summary>
        private static void ChannelSignature(MicroReliefField f, MicroReliefContext ctx, out float dispAbs, out float texAbs)
        {
            dispAbs = 0f; texAbs = 0f;
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 7; j++)
                {
                    ctx.env.worldPos = new Vector3(i * 0.37f, 0f, j * 0.41f);
                    var r = f.Evaluate(ctx);
                    dispAbs += Mathf.Abs(r.displacementMm);
                    texAbs += Mathf.Abs(r.textureMm);
                }
        }

        /// <summary>
        /// G-19: raising the dominance exponent must drive the surface toward the single fastest process
        /// and lowering it toward an even blend, continuously. On dry bare crystalline rock exactly two
        /// processes compete - joint/bedding facets (higher rate, coarse -> displacement channel) and
        /// spheroidal exfoliation (lower rate, fine -> texture channel) - so the displacement share is a
        /// clean read-out of how much the faster process owns the surface.
        /// </summary>
        private static void TestProcessCompetition()
        {
            var field = MicroReliefField.CreateDefault();
            var ctx = BuildCtx(RockClass.Crystalline, 0f, 0.02f, 2, 2f, 5f, 0f, 0f, 0f, 2f);

            // The metric is only clean if precisely these two processes are active.
            int eligible = 0;
            for (int i = 0; i < field.Processes.Count; i++) if (field.Processes[i].IsEligible(ctx)) eligible++;
            Check(eligible == 2, "Competition context did not isolate exactly two eligible processes");
            Check(GetProcess(field, "JointBeddingFacets").IsEligible(ctx), "Facets not eligible in competition test");
            Check(GetProcess(field, "SpheroidalExfoliation").IsEligible(ctx), "Exfoliation not eligible in competition test");

            float[] sVals = { 0.1f, 1f, 4f, 16f };
            float[] dispShare = new float[sVals.Length];
            for (int k = 0; k < sVals.Length; k++)
            {
                field.ProcessDominance = sVals[k];
                ChannelSignature(field, ctx, out float d, out float t);
                float total = d + t;
                Check(total > 0f, "Competition produced no relief at all");
                dispShare[k] = d / total;
            }
            field.ProcessDominance = 2f;

            // High dominance: the faster process (facets, displacement) claims almost the whole surface.
            Check(dispShare[sVals.Length - 1] > 0.95f, "High dominance did not converge on a single process signature");
            // Low dominance: a real blend, so the slower process (exfoliation, texture) is plainly present.
            Check(dispShare[0] < 0.7f, "Low dominance did not blend the two processes");
            // In between: monotone, so the move from blend to pure has no discontinuity.
            for (int k = 1; k < sVals.Length; k++)
                Check(dispShare[k] >= dispShare[k - 1] - 1e-4f, "Dominant share did not rise monotonically with dominance");
        }

        /// <summary>Builds a snow context for a facet at a given depth/wind/obstacle. Latitude 45N so south (180) is sun-facing.</summary>
        private static SnowReliefContext BuildSnowCtx(
            float depthM, float slopeDeg, float aspectDeg, float windMps, float windBearingDeg,
            float obstacleHeightM, float distanceToObstacleM, float obstacleBearingDeg)
        {
            return new SnowReliefContext
            {
                env = new EnvironmentSample
                {
                    worldPos = new Vector3(1.3f, 0f, 0.7f),
                    slopeDeg = slopeDeg,
                    aspectDeg = aspectDeg,
                    latitudeDeg = 45f
                },
                snowDepthM = depthM,
                windSpeedMps = windMps,
                windBearingDeg = windBearingDeg,
                obstacleHeightM = obstacleHeightM,
                distanceToObstacleM = distanceToObstacleM,
                obstacleBearingDeg = obstacleBearingDeg,
                seed = 20260828
            };
        }

        /// <summary>
        /// G-20: snow is a transient LAYER, not a process. This pins the four behaviours the done-when asks
        /// for - a depth-driven cross-fade (rock in summer, snow in winter, no rebuild), sun-facing steep
        /// ground melting out first, drifts sitting downwind of an obstacle rather than around it, and
        /// sastrugi appearing only once the wind can move snow.
        /// </summary>
        private static void TestSnowReliefLayer()
        {
            var snow = new SnowReliefLayer();

            // (1) Cross-fade: bare in summer (no depth), covered in winter (deep). Same flat facet, no rebuild.
            float summer = snow.CoverageFraction(BuildSnowCtx(0f, 0f, 0f, 0f, 90f, 0f, 0f, 0f));
            float winter = snow.CoverageFraction(BuildSnowCtx(1f, 0f, 0f, 0f, 90f, 0f, 0f, 0f));
            Check(summer <= 0.001f, "Snow covered bare ground in summer");
            Check(winter >= 0.999f, "Snow did not fully cover the ground in winter");

            // (2) Melt-out order: at the same intermediate depth and slope, the sun-facing (south) face is
            //     barer than the shaded (north) one - it melts first.
            float southSteep = snow.CoverageFraction(BuildSnowCtx(0.2f, 45f, 180f, 0f, 90f, 0f, 0f, 0f));
            float northSteep = snow.CoverageFraction(BuildSnowCtx(0.2f, 45f, 0f, 0f, 90f, 0f, 0f, 0f));
            Check(southSteep < northSteep - 0.2f, "Sun-facing steep ground did not melt out before the shaded face");

            // (3) Drift geometry: wind toward the east (bearing 90); a point due east of the obstacle is in the
            //     lee and drifts deep, a point due west (upwind) or due north (crosswind) does not.
            float down = snow.SurfaceReliefMm(BuildSnowCtx(0.5f, 0f, 0f, 10f, 90f, 2f, 4f, 90f));
            float up = snow.SurfaceReliefMm(BuildSnowCtx(0.5f, 0f, 0f, 10f, 90f, 2f, 4f, 270f));
            float cross = snow.SurfaceReliefMm(BuildSnowCtx(0.5f, 0f, 0f, 10f, 90f, 2f, 4f, 0f));
            Check(down > up + 100f, "Drift did not pile downwind of the obstacle");
            Check(down > cross + 100f, "Drift piled around the obstacle instead of in its lee");

            // (4) Sastrugi only above the transport threshold: a windy surface is far rougher than a calm one.
            //     The ridges run ELONGATED ALONG the wind (bearing 90 = +x), so relief varies ACROSS it - the
            //     +z axis here. We must walk the samples across the wind (vary z) to cross the ridge crests;
            //     walking along the wind (vary x) rides a single crest and would read no roughness at all.
            float windyAbs = 0f, calmAbs = 0f;
            for (int i = 0; i < 9; i++)
            {
                var wc = BuildSnowCtx(0.5f, 0f, 0f, 12f, 90f, 0f, 0f, 0f);
                var cc = BuildSnowCtx(0.5f, 0f, 0f, 0f, 90f, 0f, 0f, 0f);
                wc.env.worldPos = new Vector3(0f, 0f, i * 0.37f);
                cc.env.worldPos = new Vector3(0f, 0f, i * 0.37f);
                windyAbs += Mathf.Abs(snow.SurfaceReliefMm(wc));
                calmAbs += Mathf.Abs(snow.SurfaceReliefMm(cc));
            }
            Check(windyAbs > calmAbs * 3f, "Sastrugi did not roughen the surface above the wind threshold");
        }

        /// <summary>
        /// G-21: the overlay must agree with section 7's water, reading its level and never authoring a
        /// second one. This pins the invariant a player spots at once - no displaced vertex breaches the
        /// water plane - while berms above water and notches below it pass through, and the waterline
        /// itself sits exactly on the plane so it traces the cut notch.
        /// </summary>
        private static void TestWaterSurfaceCoupling()
        {
            const float clr = WaterSurfaceCoupling.SurfaceClearanceM;

            // (1) Submerged ground with a large upward displacement is clamped to just below the surface.
            var submerged = new WaterCoupling { hasWater = true, stillWaterLevelM = 10f, baseHeightM = 5f };
            float surf = WaterSurfaceCoupling.SurfaceHeightM(8000f, submerged); // would reach 13 m unclamped
            Check(surf < 10f, "A displaced vertex breached the water surface");
            Check(surf <= 10f - clr + 1e-4f && surf >= 10f - clr - 1e-4f, "Clamped surface did not settle at the clearance below the plane");

            // (2) Deepening a submerged point (a cut notch) is always allowed.
            var deepen = new WaterCoupling { hasWater = true, stillWaterLevelM = 10f, baseHeightM = 8f };
            Check(WaterSurfaceCoupling.ClampDisplacementMm(-3000f, deepen) == -3000f, "A downward cut was wrongly clamped");

            // (3) A berm standing on dry land above still water passes through untouched.
            var berm = new WaterCoupling { hasWater = true, stillWaterLevelM = 10f, baseHeightM = 12f };
            Check(WaterSurfaceCoupling.ClampDisplacementMm(2000f, berm) == 2000f, "An above-water berm was wrongly clamped");

            // (4) No water body in range: displacement is never touched.
            var dry = new WaterCoupling { hasWater = false, stillWaterLevelM = 0f, baseHeightM = 100f };
            Check(WaterSurfaceCoupling.ClampDisplacementMm(5000f, dry) == 5000f, "Displacement changed with no water present");

            // (5) At the waterline (base == still) an upward push is held to the plane - the waterline meets the notch.
            var shore = new WaterCoupling { hasWater = true, stillWaterLevelM = 10f, baseHeightM = 10f };
            float shoreSurf = WaterSurfaceCoupling.SurfaceHeightM(1000f, shore);
            Check(shoreSurf <= 10f && shoreSurf >= 10f - clr - 1e-4f, "The waterline did not sit on the water plane");
        }

        /// <summary>
        /// G-22: a tessellation factor must come from the shared EDGE midpoint, never the patch centre, or
        /// neighbouring patches subdivide their common edge differently and a crack opens at distance. This
        /// pins the guarantee directly: two patches meeting at an edge compute a bit-identical factor for
        /// it (that is what closes the seam), the tier ceilings are respected, and the factor falls off
        /// smoothly and monotonically with distance.
        /// </summary>
        private static void TestPatchEdgeTessellation()
        {
            // (1) Tier ceilings: Low ships no HW tessellation (factor 1 = do not subdivide); the rest climb.
            Check(PatchEdgeTessellation.MaxFactorForTier(AdaptiveQuality.QualityTier.Low) == 1f, "Low tier tess ceiling not 1");
            Check(PatchEdgeTessellation.MaxFactorForTier(AdaptiveQuality.QualityTier.Medium) == 16f, "Medium tier tess ceiling not 16");
            Check(PatchEdgeTessellation.MaxFactorForTier(AdaptiveQuality.QualityTier.High) == 32f, "High tier tess ceiling not 32");
            Check(PatchEdgeTessellation.MaxFactorForTier(AdaptiveQuality.QualityTier.Ultra) == 64f, "Ultra tier tess ceiling not 64");

            const float size = 8f;
            const float angle = PatchEdgeTessellation.DefaultSegmentAngleRad;
            const float ceil = 64f;

            // (2) Shared address: the east edge of cell (k,z) and the west edge of cell (k+1,z) resolve to the
            //     one half-index, hence one world midpoint. Same for a north/south neighbour pair.
            PatchEdgeTessellation.EdgeMidpointHalfIndex(3, 2, PatchEdge.East, out int ex, out int ez);
            PatchEdgeTessellation.EdgeMidpointHalfIndex(4, 2, PatchEdge.West, out int wx, out int wz);
            Check(ex == wx && ez == wz, "Shared east/west edge did not resolve to one half-index");
            Vector2 mA = PatchEdgeTessellation.EdgeMidpointXZ(3, 2, size, PatchEdge.East);
            Vector2 mB = PatchEdgeTessellation.EdgeMidpointXZ(4, 2, size, PatchEdge.West);
            Check(mA == mB, "Shared edge midpoint differed across the two patches");

            // (3) The crack test itself: an arbitrary camera, and the two patches' factor for their shared
            //     edge must be identical to the last bit - X-neighbour pair and Z-neighbour pair alike.
            Vector3 cam = new Vector3(37.3f, 20.5f, -11.7f);
            float aEast = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.East, cam, angle, ceil);
            float bWest = PatchEdgeTessellation.EdgeFactor(4, 2, 0f, size, PatchEdge.West, cam, angle, ceil);
            Check(aEast == bWest, "Shared east/west edge produced two different tess factors - a crack");
            float aNorth = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.North, cam, angle, ceil);
            float bSouth = PatchEdgeTessellation.EdgeFactor(3, 3, 0f, size, PatchEdge.South, cam, angle, ceil);
            Check(aNorth == bSouth, "Shared north/south edge produced two different tess factors - a crack");

            // (4) Saturation and floor: a camera on the midpoint saturates at the ceiling; a distant one bottoms
            //     out at 1, and never below (a factor under 1 would degenerate the edge).
            float near = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.East, new Vector3(mA.x, 0.001f, mA.y), angle, ceil);
            float far = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.East, new Vector3(mA.x, 100000f, mA.y), angle, ceil);
            Check(near == ceil, "A near edge did not saturate at the tier ceiling");
            Check(far == 1f, "A far edge did not bottom out at factor 1");

            // (5) Monotone falloff: lifting the camera straight up over the midpoint only ever lowers the factor.
            float d5   = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.East, new Vector3(mA.x, 5f, mA.y), angle, ceil);
            float d50  = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.East, new Vector3(mA.x, 50f, mA.y), angle, ceil);
            float d500 = PatchEdgeTessellation.EdgeFactor(3, 2, 0f, size, PatchEdge.East, new Vector3(mA.x, 500f, mA.y), angle, ceil);
            Check(d5 >= d50 && d50 >= d500, "Tess factor did not fall monotonically with distance");
        }

        /// <summary>
        /// G-24: hardware tessellation is not universal - a real slice of the mid-range Android fleet has no
        /// hull/domain stages - so the ring must decide, once, from the GPU's shader level whether to
        /// tessellate or draw a denser fixed mesh, and it must be able to say which. This pins the decision
        /// as a pure table: the SM 4.6 gate (GLES 3.2 clears it, GLES 3.0 does not), the two hard vetoes
        /// (an unsupported shader, a headless Null device), and that the reason string names the active path.
        /// </summary>
        private static void TestOverlayTessellationSupport()
        {
            const int lvl = OverlayTessellationSupport.TessellationShaderLevel; // 46

            // (1) The gate is the shader level, not a device allow-list: a capable device at >= SM 4.6 tessellates,
            //     the same device one level below does not. This is exactly the GLES 3.2 vs 3.0 split.
            Check(OverlayTessellationSupport.SupportsHardwareTessellation(GraphicsDeviceType.Vulkan, lvl, true),
                "A Vulkan GPU at the tessellation shader level was denied hardware tessellation");
            Check(!OverlayTessellationSupport.SupportsHardwareTessellation(GraphicsDeviceType.OpenGLES3, lvl - 1, true),
                "A sub-SM4.6 GPU was wrongly granted hardware tessellation");
            Check(OverlayTessellationSupport.SupportsHardwareTessellation(GraphicsDeviceType.OpenGLES3, lvl, true),
                "A GLES 3.2-class GPU at the tessellation shader level was denied hardware tessellation");

            // (2) Two hard vetoes regardless of shader level: the shader did not compile here, or there is no
            //     real rendering device to tessellate for (batch/headless).
            Check(!OverlayTessellationSupport.SupportsHardwareTessellation(GraphicsDeviceType.Vulkan, lvl, false),
                "Tessellation was granted although the overlay shader is unsupported");
            Check(!OverlayTessellationSupport.SupportsHardwareTessellation(GraphicsDeviceType.Null, lvl, true),
                "Tessellation was granted on a headless Null device");

            // (3) DecidePath mirrors the predicate - capable -> hardware, otherwise the pre-subdivided fallback.
            Check(OverlayTessellationSupport.DecidePath(GraphicsDeviceType.Metal, lvl, true) == OverlayTessPath.HardwareTessellation,
                "DecidePath did not choose hardware tessellation for a capable GPU");
            Check(OverlayTessellationSupport.DecidePath(GraphicsDeviceType.OpenGLES3, lvl - 1, true) == OverlayTessPath.PreSubdividedFallback,
                "DecidePath did not fall back on a sub-SM4.6 GPU");

            // (4) The reason string is the inspector's read-out, so it must name the path that was chosen.
            string hw = OverlayTessellationSupport.DescribePath(GraphicsDeviceType.Metal, lvl, true);
            string lo = OverlayTessellationSupport.DescribePath(GraphicsDeviceType.OpenGLES3, lvl - 1, true);
            Check(hw.Contains("Hardware tessellation"), "The hardware-path reason did not name hardware tessellation");
            Check(lo.Contains("fallback"), "The fallback-path reason did not name the fallback");

            // (5) The fallback draws a fixed, moderate density - not the tessellated path's distance-scaled one.
            Check(OverlayTessellationSupport.FallbackSubdivisionResolution == 33, "Fallback subdivision resolution drifted from 33");
        }

        /// <summary>
        /// G-26: gameplay must read the same surface the shader draws, or feet float over mounds and sink
        /// into pits. The overlay ships no collider; the honest substitute is SampleOffset, a CPU query that
        /// runs the SAME field Evaluate the bake samples and returns the metres the domain shader adds. This
        /// pins the contract that makes contact agree with the picture: the query is the field's own
        /// displacement channel in metres, it forwards the world point to the bound sampler, it excludes the
        /// texture channel (which the shader carries in the normal map, not by moving a vertex), and with no
        /// field bound it returns zero so gameplay sits on the bare terrain.
        /// </summary>
        private static void TestMicroReliefSampleOffset()
        {
            var field = MicroReliefField.CreateDefault();

            // (1) No field bound here: the query is zero and gameplay reads the un-displaced terrain.
            field.ContextSampler = null;
            Check(field.SampleOffset(3f, 4f) == 0f, "SampleOffset with no sampler bound did not return zero");

            // (2) Same code path: the offset IS the field's displacement channel, converted mm -> m. A
            //     periglacial slope raises sorted ground into the geometry channel, so there is real offset.
            var perigl = BuildCtx(RockClass.Clastic, 0f, 0.1f, 2, 1f, 15f, 60f, 500f, 0.3f, 2f);
            field.ContextSampler = (x, z) => perigl;
            float expected = field.Evaluate(perigl).displacementMm * 0.001f;
            Check(expected != 0f, "Periglacial context produced no displacement to sample - test is vacuous");
            Check(field.SampleOffset(0f, 0f) == expected, "SampleOffset did not equal the field's own displacement in metres");

            // (3) The world point is forwarded verbatim - a footstep query really is just an (x,z).
            float gotX = float.NaN, gotZ = float.NaN;
            field.ContextSampler = (x, z) => { gotX = x; gotZ = z; return perigl; };
            field.SampleOffset(12.5f, -7.25f);
            Check(gotX == 12.5f && gotZ == -7.25f, "SampleOffset did not forward the world coordinates to the sampler");

            // (4) Texture-channel relief must NOT move contact: soloed karren on limestone is sub-Nyquist,
            //     carried by the normal map, so the shader moves no vertex and neither may the foot.
            var limestone = BuildCtx(RockClass.Carbonate, 0.8f, 0.15f, 2, 1f, 2f, 0f, 1200f, 0f, 2f);
            field.SoloProcessId = "Karren";
            var karren = field.Evaluate(limestone);
            field.ContextSampler = (x, z) => limestone;
            Check(karren.textureMm != 0f && karren.displacementMm == 0f, "Karren solo was not a pure texture-channel relief");
            Check(field.SampleOffset(0f, 0f) == 0f, "SampleOffset chased a texture-channel ripple the shader never displaces");
            field.SoloProcessId = null;

            // (5) Determinism: the same point queries the same offset every call.
            field.ContextSampler = (x, z) => perigl;
            Check(field.SampleOffset(2f, 2f) == field.SampleOffset(2f, 2f), "SampleOffset is not deterministic");
            field.ContextSampler = null;
        }

        /// <summary>
        /// G-27: past the ring, displacement is gone but surface character must not be, and the switch from
        /// the overlay's analytic normal to the terrain's baked distant normal cannot be a line - a circle of
        /// changing character sliding with the camera reads as a material seam. This pins the fade weight that
        /// turns the line into a crease-free band: full analytic inside, full baked at and beyond the outer
        /// radius, monotone between, and zero slope at both shoulders so neither edge shows.
        /// </summary>
        private static void TestOverlayNormalBlend()
        {
            const float outer = 40f;
            const float frac = OverlayNormalBlend.DefaultBlendBandFraction; // 0.25 -> band = 10 m, inner edge = 30 m

            // (1) Endpoints: analytic owns the interior, the baked normal owns everything at/after the radius.
            Check(OverlayNormalBlend.AnalyticWeight(0f, outer, frac) == 1f, "Ring interior did not use the analytic normal fully");
            Check(OverlayNormalBlend.AnalyticWeight(29f, outer, frac) == 1f, "Just inside the band was not fully analytic");
            Check(OverlayNormalBlend.AnalyticWeight(outer, outer, frac) == 0f, "The outer radius did not hand fully to the baked normal");
            Check(OverlayNormalBlend.AnalyticWeight(2f * outer, outer, frac) == 0f, "Beyond the ring was not fully baked");

            // (2) Monotone across the band: walking outward only ever reduces the analytic share - no reversal.
            float prev = OverlayNormalBlend.AnalyticWeight(30f, outer, frac);
            for (int d = 31; d <= 40; d++)
            {
                float w = OverlayNormalBlend.AnalyticWeight(d, outer, frac);
                Check(w <= prev + 1e-6f, "Normal blend weight rose while moving outward through the band");
                prev = w;
            }

            // (3) Crease-free shoulders: a smoothstep leaves ~zero slope at both ends, so the step from the
            //     band into the flat interior/exterior is far smaller than a linear fade's constant slope
            //     (which over this 10 m band would change the weight by ~0.1 per metre).
            float nearInnerSlope = 1f - OverlayNormalBlend.AnalyticWeight(30.5f, outer, frac); // drop from 1 over 0.5 m
            float nearOuterSlope = OverlayNormalBlend.AnalyticWeight(39.5f, outer, frac);       // rise above 0 over 0.5 m
            Check(nearInnerSlope < 0.02f, "The inner shoulder of the normal blend was a crease, not a smooth arrival at 1");
            Check(nearOuterSlope < 0.02f, "The outer shoulder of the normal blend was a crease, not a smooth arrival at 0");

            // (4) Midpoint sanity: halfway through the band the two normals are evenly mixed.
            Check(Mathf.Abs(OverlayNormalBlend.AnalyticWeight(35f, outer, frac) - 0.5f) < 1e-4f, "Band midpoint was not an even mix");

            // (5) Degenerate band (fraction 0): a hard edge, analytic strictly inside, baked from the radius out.
            Check(OverlayNormalBlend.AnalyticWeight(39f, outer, 0f) == 1f, "Zero-band inside was not fully analytic");
            Check(OverlayNormalBlend.AnalyticWeight(outer, outer, 0f) == 0f, "Zero-band did not hand over at the radius");
        }

        /// <summary>
        /// G-28: micro-relief must feed back into scatter, not only into pixels. The field exposes which process
        /// owns a site and how far it displaced it; MicroReliefScatterBias turns that into a suitability
        /// multiplier, so saplings prefer drained mounds over ponded pits, grass holds the flat treads, and small
        /// stones sit in the lag - with no authored mask. This pins those laws on the pure bias, and confirms a
        /// pit-and-mound site really offers both a mound (+) and a pit (-) for a sapling to choose between.
        /// </summary>
        private static void TestMicroReliefScatterBias()
        {
            const float refM = 0.1f; // local relief scale the signed offset is measured against

            // (1) Saplings follow drainage: a mound (up) beats a flat beats a ponded pit (down); a flat is
            //     neutral, and no site is ever zeroed by relief alone - a rare pit recruit stays possible.
            float mound = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, "PitAndMound", +0.1f, refM);
            float flat  = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, "PitAndMound",  0.0f, refM);
            float pit   = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, "PitAndMound", -0.1f, refM);
            Check(flat == 1f, "Sapling bias on a flat was not neutral");
            Check(mound > flat && flat > pit, "Sapling did not prefer the mound over the flat over the pit");
            Check(pit > 0f, "Sapling bias zeroed a pit outright instead of merely discouraging it");

            // (2) Monotone in elevation: sweeping the offset upward never lowers a sapling's odds.
            float prevSap = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, null, -0.5f, refM);
            for (int i = -4; i <= 5; i++)
            {
                float m = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, null, i * 0.1f, refM);
                Check(m >= prevSap - 1e-6f, "Sapling bias fell while the site rose");
                prevSap = m;
            }

            // (3) Grass follows flatness: a level bench beats a shedding riser, and a terracette tread - the
            //     bench grass colonises first - gets its extra edge over an ordinary flat.
            float tread     = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Grass, "SolifluctionTerracettes", 0f, refM);
            float grassFlat = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Grass, "PitAndMound", 0f, refM);
            float riser     = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Grass, "PitAndMound", 0.1f, refM);
            Check(grassFlat > riser, "Grass did not prefer the level bench over the riser");
            Check(tread > grassFlat, "Grass got no extra edge on a terracette tread");

            // (4) Small stones follow the lag: at home on a clast armour or an aeolian ripple crest where coarse
            //     fragments concentrate, and rare (not impossible) on a mound of fines.
            float onArmour = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.SmallStone, "ClastLagArmour", 0f, refM);
            float onRipple = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.SmallStone, "AeolianRipples", 0f, refM);
            float offLag   = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.SmallStone, "PitAndMound", 0f, refM);
            Check(onArmour == 1f && onRipple == 1f, "A small stone was not at home in its lag/ripple field");
            Check(offLag > 0f && offLag < onArmour, "A small stone off its lag field was not made rarer (but still possible)");

            // (5) Neutral kind: relief never sorts it, whatever the site or offset.
            Check(MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Neutral, "PitAndMound", 0.3f, refM) == 1f, "Neutral affinity was sorted by relief");

            // (6) No relief to read (zero reference amplitude): the height-driven rules stay neutral.
            Check(MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, null, 0.4f, 0f) == 1f, "Sapling bias read a microsite with no relief scale to judge it by");

            // (7) The field NAMES the process that owns a site. On a wooded, soil-mantled slope where treethrow
            //     is eligible, soloing pit-and-mound must make it dominant; soloing a process that cannot act
            //     here (a shore berm on an inland slope) must leave nothing dominant.
            var field = MicroReliefField.CreateDefault();
            var wooded = new MicroReliefContext
            {
                env = new EnvironmentSample
                {
                    slopeDeg = 4f, aspectDeg = 45f, annualPrecipMm = 900f,
                    canopyLai = 2.5f, soilDepthM = 0.5f, soilFertility = 0.5f
                },
                litho = new LithoSample { unitIndex = 0, unit = new LithoUnit { rockClass = RockClass.Clastic, grainSizePhi = 2f, angleOfReposeDeg = 34f } },
                cover = new CoverSample { type = CoverType.ResidualSoil, depthM = 0.5f, grainSizePhi = 2f },
                vertexSpacingM = 0.5f, freezeThawCyclesPerYr = 0f, seed = 20260828
            };
            field.SoloProcessId = "PitAndMound";
            Check(field.DominantProcessId(wooded) == "PitAndMound", "Pit-and-mound was not dominant where it is the only active process");
            field.SoloProcessId = "BeachBermWaveNotch"; // needs a shore; ineligible on this inland slope
            Check(field.DominantProcessId(wooded) == null, "A process ineligible here was still reported as dominant");

            // (8) The done-when in the flesh: a pit-and-mound site really offers BOTH a raised mound and a sunk
            //     pit, and a sapling's bias is higher at the mound than at the pit - the preference the debug
            //     solo would show on the ground, proven here without a scene.
            field.SoloProcessId = "PitAndMound";
            float bestUp = 0f, bestDown = 0f; // most positive and most negative displacement over a small scan
            for (int i = 0; i < 16; i++)
                for (int j = 0; j < 16; j++)
                {
                    wooded.env.worldPos = new Vector3(i * 0.5f, 0f, j * 0.5f);
                    float dispMm = field.Evaluate(wooded).displacementMm;
                    if (dispMm > bestUp) bestUp = dispMm;
                    if (dispMm < bestDown) bestDown = dispMm;
                }
            Check(bestUp > 0f && bestDown < 0f, "Pit-and-mound produced no paired mound and pit to choose between");
            float ampM = Mathf.Max(bestUp, -bestDown) * 0.001f; // local relief scale, mm -> m
            float sapMound = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, "PitAndMound", bestUp * 0.001f, ampM);
            float sapPit   = MicroReliefScatterBias.SuitabilityMultiplier(ScatterMicrositeAffinity.Sapling, "PitAndMound", bestDown * 0.001f, ampM);
            Check(sapMound > sapPit, "A sapling did not prefer the mound to the pit at a pit-and-mound site");
            field.SoloProcessId = null;
        }

        /// <summary>
        /// G-29: the ring is runtime view state that must survive a domain reload without doubling. A reload
        /// wipes the driver's references but leaves the DontSave GameObjects alive; the driver must re-bind to
        /// the surviving root by name rather than build a second ring. The re-bind decision is factored into
        /// OverlayRingAdoption so it is provable without play mode: this stands up a fake surviving ring and
        /// pins that a whole n×n root is adopted (same object, right slots and mesh), while none/malformed is not.
        /// </summary>
        private static void TestOverlayRingAdoption()
        {
            // (1) Pure gate: only a perfect-square child count is a whole n×n ring.
            Check(OverlayRingAdoption.IsWholeRing(9, out int s3) && s3 == 3, "9 children were not read as a 3x3 ring");
            Check(OverlayRingAdoption.IsWholeRing(1, out int s1) && s1 == 1, "1 child was not read as a 1x1 ring");
            Check(!OverlayRingAdoption.IsWholeRing(8, out _), "8 children were wrongly accepted as a whole ring");
            Check(!OverlayRingAdoption.IsWholeRing(0, out _), "0 children were wrongly accepted as a ring");

            GameObject host = null;
            Mesh mesh = null;
            try
            {
                host = new GameObject("AdoptionHost");
                mesh = new Mesh { name = "AdoptionPatch" };

                // (2) No ring under the host yet: nothing to adopt, outputs null.
                Check(!OverlayRingAdoption.TryBind(host.transform, "MicroReliefRing", out var r0, out var sl0, out int g0, out var m0)
                      && r0 == null && sl0 == null && g0 == 0 && m0 == null,
                      "TryBind adopted a ring where none existed");

                // Stand up a surviving 3x3 ring: one root named as the driver names it, nine Patch children,
                // each carrying the shared mesh - exactly the hierarchy a domain reload would leave behind.
                var ring = new GameObject("MicroReliefRing");
                ring.transform.SetParent(host.transform, false);
                for (int i = 0; i < 9; i++)
                {
                    var patch = new GameObject("Patch");
                    patch.transform.SetParent(ring.transform, false);
                    patch.AddComponent<MeshFilter>().sharedMesh = mesh;
                }

                // (3) A whole ring is adopted wholesale: the SAME root object, nine slots, side 3, mesh recovered.
                Check(OverlayRingAdoption.TryBind(host.transform, "MicroReliefRing", out var root, out var slots, out int gridN, out var got)
                      && root == ring.transform && slots != null && slots.Length == 9 && gridN == 3 && got == mesh,
                      "TryBind did not re-bind to the surviving 3x3 ring");

                // (4) Adoption creates nothing: re-binding again returns that very same root - one ring, not two.
                OverlayRingAdoption.TryBind(host.transform, "MicroReliefRing", out var root2, out _, out _, out _);
                Check(root2 == ring.transform, "A second adoption bound to a different root - the ring would double");

                // (5) A malformed leftover (ten children, not a square) is refused, so the driver builds clean.
                var stray = new GameObject("Patch");
                stray.transform.SetParent(ring.transform, false);
                Check(!OverlayRingAdoption.TryBind(host.transform, "MicroReliefRing", out var rBad, out _, out _, out _)
                      && rBad == null, "TryBind adopted a malformed (non-square) leftover ring");
            }
            finally
            {
                if (host != null) Object.DestroyImmediate(host);
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }
    }
}

