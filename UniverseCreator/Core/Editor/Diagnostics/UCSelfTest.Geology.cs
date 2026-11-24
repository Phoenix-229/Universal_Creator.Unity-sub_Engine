// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.Geology.cs
//  Purpose: Pins the two invariants a stratigraphic column must never lose, because
//           every downstream weathering, splat and soil process trusts them blindly.
//           First, the physical-sanity clamp: a hand-typed bed can carry impossible
//           numbers (negative thickness, a porosity above one), and the whole point of
//           Sanitize is that the rest of the engine never sees them. Second, and the one
//           a geologist would catch instantly, the carbonate-only dissolution rule: only
//           limestone and dolomite dissolve at the surface, so a silicate bed handed a
//           karst budget must have it forced back to zero rather than quietly growing
//           caves. These checks fail the build if either guarantee regresses.
//
//  Partial of UCSelfTest: shares the checks counter and the Check helper declared
//  in UCSelfTest.cs, so its assertions fold into the single tally.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Geology;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Outputs;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>
        /// Sanitize is the gate between what an author can type and what the engine may
        /// read. This drives a single bed to physically impossible extremes and asserts
        /// each field lands back inside its real-world range, then pins the cross-field
        /// rule that matters most: solubility is a carbonate privilege. A clastic bed
        /// handed a full karst budget must leave Sanitize with zero solubility, while a
        /// carbonate bed keeps its own - otherwise § 9.5 karst could eat sandstone.
        /// </summary>
        private static void TestLithologySanitize()
        {
            // A clastic bed loaded with impossible values and an illegal karst budget.
            var bad = new LithoUnit
            {
                rockClass         = RockClass.Clastic,
                thicknessM        = -50f,   // negative: physically impossible
                erodibilityKlsu   = -1f,    // must stay strictly positive
                porosity          = 2f,     // a fraction, cannot exceed 1
                solubility        = 0.9f,   // silicate rock does not dissolve
                grainSizePhi      = 999f,   // off the Udden-Wentworth scale
                jointSetCount     = 50,     // 1..6 sets only
                angleOfReposeDeg  = 400f,   // 0..60 degrees
                soilFormationRate = -10f,   // rate cannot be negative
                albedo            = 5f,      // reflectance is 0..1
                wetDarkening      = -3f
            };
            bad.Sanitize();

            Check(bad.thicknessM > 0f, "LithoUnit.Sanitize left thickness non-positive");
            Check(bad.erodibilityKlsu > 0f, "LithoUnit.Sanitize left erodibility non-positive");
            Check(bad.porosity >= 0f && bad.porosity <= 1f,
                  "LithoUnit.Sanitize left porosity outside 0..1");
            Check(bad.solubility == 0f,
                  "LithoUnit.Sanitize let a CLASTIC bed keep a karst budget - only carbonate " +
                  "dissolves, so silicate solubility must be forced to zero");
            Check(bad.grainSizePhi >= -8f && bad.grainSizePhi <= 14f,
                  "LithoUnit.Sanitize left grain size off the phi scale");
            Check(bad.jointSetCount >= 1 && bad.jointSetCount <= 6,
                  "LithoUnit.Sanitize left joint-set count outside 1..6");
            Check(bad.angleOfReposeDeg >= 0f && bad.angleOfReposeDeg <= 60f,
                  "LithoUnit.Sanitize left angle of repose outside 0..60 degrees");
            Check(bad.soilFormationRate >= 0f,
                  "LithoUnit.Sanitize left soil formation rate negative");
            Check(bad.albedo >= 0f && bad.albedo <= 1f,
                  "LithoUnit.Sanitize left albedo outside 0..1");
            Check(bad.wetDarkening >= 0f && bad.wetDarkening <= 1f,
                  "LithoUnit.Sanitize left wet darkening outside 0..1");

            // The mirror case: a carbonate bed is the one class allowed to keep dissolving.
            var carb = new LithoUnit { rockClass = RockClass.Carbonate, solubility = 0.7f };
            carb.Sanitize();
            Check(carb.solubility > 0f,
                  "LithoUnit.Sanitize wrongly zeroed a CARBONATE bed's solubility - carbonate " +
                  "is precisely the class that dissolves");
        }

        /// <summary>
        /// The § 9.4 done-when is a three-unit column an author can build without touching a
        /// noise setting. This asserts the seeded example column is exactly that: three beds,
        /// ordered oldest-first with a crystalline basement at index 0, and three DIFFERENT
        /// weathering families so the surfaces can read as visibly different rock downstream.
        /// The visible half is a G-12/G-14 responsibility; what is testable here is that the
        /// material identities feeding them are three and distinct.
        /// </summary>
        private static void TestLithologyExampleColumn()
        {
            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                profile.PopulateExampleColumn();

                Check(profile.UnitCount == 3,
                      "LithologyProfile.PopulateExampleColumn did not build the three-bed " +
                      "column the done-when asks for (got " + profile.UnitCount + ")");
                Check(profile.Basement != null &&
                      profile.Basement.rockClass == RockClass.Crystalline,
                      "The example column's basement (index 0, oldest) should be the resistant " +
                      "crystalline unit the section stands on");

                // Three distinct weathering families - the precondition for three visibly
                // different surfaces, even though the surfaces themselves are drawn later.
                var a = profile.UnitAt(0);
                var b = profile.UnitAt(1);
                var c = profile.UnitAt(2);
                Check(a != null && b != null && c != null,
                      "The example column has a null bed where an authored one is expected");
                if (a != null && b != null && c != null)
                {
                    Check(a.rockClass != b.rockClass &&
                          b.rockClass != c.rockClass &&
                          a.rockClass != c.rockClass,
                          "The example column's three beds are not three distinct rock classes, " +
                          "so they cannot read as visibly different surfaces");
                }

                Check(profile.TotalThicknessM() > 0f,
                      "LithologyProfile.TotalThicknessM returned a non-positive section height");

                // Out-of-range indices resolve to null rather than throwing.
                Check(profile.UnitAt(-1) == null && profile.UnitAt(99) == null,
                      "LithologyProfile.UnitAt should return null outside the column, not throw");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The § 9.11 lithology column presentation model (G-69).
        ///
        /// The done-when is "reordering two units visibly changes the outcrop pattern
        /// without a rebuild prompt". The outcrop moves because each bed's depth below
        /// the section top is the summed thickness of the younger beds above it, and
        /// reordering changes that sum. These checks pin the cumulative-thickness readout
        /// the column draws (youngest at zero depth, each older bed deeper by the section
        /// above it), that reordering the beds changes the readout, and that the swatch
        /// colour is always defined and distinguishes rock classes. The reorderable list,
        /// the live strike/dip scene preview and the no-rebuild redraw are the editor layer.
        /// </summary>
        private static void TestLithologyColumnView()
        {
            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                // Basement (oldest) 100 m, middle 30 m, youngest (top) 10 m.
                profile.units.Add(new LithoUnit { displayName = "Base",   rockClass = RockClass.Crystalline, thicknessM = 100f, albedo = 0.35f });
                profile.units.Add(new LithoUnit { displayName = "Middle", rockClass = RockClass.Clastic,     thicknessM = 30f,  albedo = 0.22f });
                profile.units.Add(new LithoUnit { displayName = "Top",    rockClass = RockClass.Carbonate,   thicknessM = 10f,  albedo = 0.42f });

                // Depth below the section top: youngest = 0; middle = 10 (top above it); base = 40 (top+middle).
                var depths = LithologyColumnView.DepthsBelowTop(profile);
                Check(depths.Length == 3 && Mathf.Abs(depths[2]) < 1e-3f,
                    "The youngest bed must sit at zero depth below the section top.");
                Check(Mathf.Abs(depths[1] - 10f) < 1e-3f && Mathf.Abs(depths[0] - 40f) < 1e-3f,
                    "Each older bed's depth must be the summed thickness of the beds above it.");

                // Cumulative from the bottom: basement = its own thickness; whole column = total section.
                Check(Mathf.Abs(LithologyColumnView.CumulativeFromBottomM(profile, 0) - 100f) < 1e-3f,
                    "Cumulative-from-bottom of the basement must be its own thickness.");
                Check(Mathf.Abs(LithologyColumnView.CumulativeFromBottomM(profile, 2) - profile.TotalThicknessM()) < 1e-3f,
                    "Cumulative-from-bottom of the top bed must equal the total section thickness.");

                // Reordering two beds changes the readout — the same ordering change the outcrop reads.
                var before = LithologyColumnView.DepthsBelowTop(profile);
                var tmp = profile.units[0]; profile.units[0] = profile.units[2]; profile.units[2] = tmp;
                var after = LithologyColumnView.DepthsBelowTop(profile);
                Check(Mathf.Abs(after[0] - before[0]) > 1e-3f || Mathf.Abs(after[2] - before[2]) > 1e-3f,
                    "Reordering two beds must change the cumulative-thickness readout.");

                // The swatch is always defined and distinguishes rock classes.
                Color cCarb = LithologyColumnView.SwatchColor(new LithoUnit { rockClass = RockClass.Carbonate, albedo = 0.42f });
                Color cVolc = LithologyColumnView.SwatchColor(new LithoUnit { rockClass = RockClass.Volcanic,  albedo = 0.08f });
                Check(cCarb != cVolc, "Different rock classes must produce different swatch colours.");
                Color cBright = LithologyColumnView.SwatchColor(new LithoUnit { rockClass = RockClass.Carbonate, albedo = 0.9f });
                Check(cBright.maxColorComponent > cCarb.maxColorComponent,
                    "A higher albedo must brighten the swatch within a class.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The G-11 promise is a bed field that is coherent across cell borders and rotates
        /// rigidly with strike. Coherence is guaranteed by construction - the elevation is a pure
        /// function of absolute world (x,z) with no cell input - so this pins the two facts that
        /// prove it: the along-dip coordinate is the exact projection onto the down-dip azimuth,
        /// and rotating both the strike and the sample point by the same angle leaves that
        /// coordinate unchanged (the whole pattern turns with strike rather than smearing).
        /// It also checks determinism: the seeded fault field returns the same value twice.
        /// </summary>
        private static void TestStructuralStrikeRotation()
        {
            var s = new GeologicStructure { strikeDeg = 0f, dipDeg = 10f };

            // The along-dip coordinate is the projection onto azimuth (strike + 90).
            const float x = 300f, z = 170f;
            float az = (s.strikeDeg + 90f) * Mathf.Deg2Rad;
            float expected = x * Mathf.Sin(az) + z * Mathf.Cos(az);
            Check(Mathf.Abs(s.AlongDipDistanceM(x, z) - expected) < 1e-3f,
                  "GeologicStructure.AlongDipDistanceM is not the projection onto the down-dip azimuth");

            // Rotate strike by theta and the sample point by the same theta, BOTH in the compass
            // (clockwise) sense the azimuth uses: the along-dip coordinate must be invariant, i.e.
            // the field turns rigidly with strike. (A standard CCW point rotation would fight the
            // clockwise azimuth and leave a 2-theta residue, which is a convention mismatch, not a smear.)
            const float theta = 37f;
            float r = theta * Mathf.Deg2Rad;
            float xr =  x * Mathf.Cos(r) + z * Mathf.Sin(r);
            float zr = -x * Mathf.Sin(r) + z * Mathf.Cos(r);
            var sRot = new GeologicStructure { strikeDeg = theta, dipDeg = 10f };
            Check(Mathf.Abs(sRot.AlongDipDistanceM(xr, zr) - s.AlongDipDistanceM(x, z)) < 1e-2f,
                  "Rotating strike and the sample point by the same angle changed the along-dip " +
                  "coordinate - the structural pattern does not rotate coherently with strike");

            // Determinism of the seeded fault field: same inputs, same answer.
            var f = new GeologicStructure { faultCount = 6, faultThrowM = 30f, faultSpanKm = 10f };
            Check(f.FaultThrowM(1234f, 42) == f.FaultThrowM(1234f, 42),
                  "GeologicStructure.FaultThrowM is not deterministic for a fixed seed");
        }

        /// <summary>
        /// Two facts that make the stacked bed elevations physically honest rather than a pile of
        /// numbers: at any one point an OLDER bed (lower index in the oldest-first column) sits
        /// below a younger one, because more section lies above it; and with folding off, moving
        /// down-dip lowers the bed (positive dip drops the stack in the down-dip direction). Both
        /// are read straight off BedTopElevationM, the function every outcrop query (G-12) calls.
        /// </summary>
        private static void TestStructuralStackAndDip()
        {
            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                profile.PopulateExampleColumn();
                // Isolate the dip term: no fold, no faults.
                profile.structure = new GeologicStructure
                {
                    datumElevationM = 0f, strikeDeg = 0f, dipDeg = 12f,
                    foldAmplitudeM = 0f, faultCount = 0
                };
                var st = profile.structure;
                const int seed = 7;

                // Older (index 0) below younger (index 2) at the same point: more section above it.
                float top0 = st.BedTopElevationM(profile, 0, 500f, 0f, seed);
                float top2 = st.BedTopElevationM(profile, 2, 500f, 0f, seed);
                Check(top0 < top2,
                      "The basement bed top is not below the youngest bed top at the same point - " +
                      "the oldest-first stack is upside down");

                // Down-dip (larger along-dip distance) lowers a given bed when folding is off.
                float near = st.BedTopElevationM(profile, 0, 100f, 0f, seed);
                float far  = st.BedTopElevationM(profile, 0, 5000f, 0f, seed);
                Check(far < near,
                      "Moving down-dip did not lower the bed top with dip > 0 and folding off - " +
                      "the dip term has the wrong sign");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The G-12 done-when is that two cells meeting at an edge resolve the identical unit and
        /// blend there. That coherence is guaranteed because OutcropResolver is a pure function of
        /// absolute world position - it reads no cell - so this pins the behaviour it depends on:
        /// the exposed unit is the band the surface falls in, a surface above the whole section
        /// shows the youngest bed, a surface below the column shows the basement, and the contact
        /// band ramps a blend toward the unit above. Sampling the same point twice is identical.
        /// </summary>
        private static void TestOutcropResolution()
        {
            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                profile.PopulateExampleColumn(); // crystalline(200) / shale(15) / limestone(40)
                // Flat beds so the bands are exact and hand-checkable:
                // top2 = 0, top1 = -40, top0 = -55; unit2 [-40,0], unit1 [-55,-40], unit0 below -55.
                profile.structure = new GeologicStructure
                {
                    datumElevationM = 0f, strikeDeg = 0f, dipDeg = 0f,
                    foldAmplitudeM = 0f, faultCount = 0
                };
                const int seed = 3;
                const float blendM = 4f;

                // A surface inside the youngest bed exposes unit 2.
                var mid = OutcropResolver.Resolve(profile, 123f, 456f, -20f, seed, blendM);
                Check(mid.unitIndex == 2,
                      "Outcrop at a surface inside the youngest bed did not resolve to unit 2 (got " +
                      mid.unitIndex + ")");
                Check(Mathf.Abs(mid.depthIntoUnitM - 20f) < 1e-3f,
                      "Outcrop depth into the exposed unit is wrong (expected 20 m, got " +
                      mid.depthIntoUnitM + ")");

                // A surface below the whole column falls back to the basement.
                var deep = OutcropResolver.Resolve(profile, 123f, 456f, -100f, seed, blendM);
                Check(deep.unitIndex == 0,
                      "A surface below the whole column should expose the basement (unit 0), got " +
                      deep.unitIndex);

                // A surface at or above the section top exposes the youngest bed.
                var high = OutcropResolver.Resolve(profile, 123f, 456f, 10f, seed, blendM);
                Check(high.unitIndex == 2 && high.blendWeightToAbove == 0f && high.aboveIndex == -1,
                      "A surface above the section top should expose the youngest bed with no unit " +
                      "above to blend toward");

                // Coherence: the identical point resolves identically (the border guarantee).
                var a = OutcropResolver.Resolve(profile, 777f, -321f, -50f, seed, blendM);
                var b = OutcropResolver.Resolve(profile, 777f, -321f, -50f, seed, blendM);
                Check(a.unitIndex == b.unitIndex &&
                      Mathf.Abs(a.blendWeightToAbove - b.blendWeightToAbove) < 1e-6f,
                      "OutcropResolver is not a pure function - the same point gave two answers");

                // The contact band ramps a real blend toward the unit above near a unit's top.
                var contact = OutcropResolver.Resolve(profile, 5f, 5f, -41f, seed, blendM);
                Check(contact.unitIndex == 1 && contact.aboveIndex == 2 &&
                      contact.blendWeightToAbove > 0f && contact.blendWeightToAbove < 1f,
                      "The soft contact did not ramp a partial blend toward the unit above near a " +
                      "bed top (unit " + contact.unitIndex + ", blend " + contact.blendWeightToAbove + ")");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The G-13/G-14 promise, tested on the pure core so no terrain is needed: the differential
        /// relief transform is a SINGLE deterministic pass, it preserves the tile mean, strength 0 is
        /// an exact pass-through, and a hard bed amplifies relief while a soft bed damps it. Using a
        /// box radius that spans the whole grid makes the local mean equal the global mean, so every
        /// output value is analytically predictable and can be pinned exactly rather than eyeballed.
        /// </summary>
        private static void TestLithologyDifferentialErosion()
        {
            const int res = 8;
            int count = res * res;
            float[] src = new float[count];
            float[] klsu = new float[count];

            // A high-frequency ripple around a flat base (nonzero local relief everywhere), and a
            // resistance field that is hard on the left half, soft on the right.
            float meanK = 0f;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    src[i] = 0.4f + 0.06f * Mathf.Sin(x * 1.3f) * Mathf.Cos(z * 1.1f);
                    klsu[i] = (x < res / 2) ? 4f : 0.5f;   // hard | soft
                    meanK += klsu[i];
                }
            meanK /= count;

            // Radius spans the grid -> the box mean is the GLOBAL mean for every pixel.
            const int radius = res;
            const float strength = 0.8f;

            float[] dstA = new float[count];
            float[] dstB = new float[count];
            LithologyErosionNode.ApplyDifferentialErosion(src, dstA, klsu, res, res, radius, strength, meanK);
            LithologyErosionNode.ApplyDifferentialErosion(src, dstB, klsu, res, res, radius, strength, meanK);

            // Determinism: the same inputs give a bit-identical result (single pass, no RNG).
            bool identical = true;
            for (int i = 0; i < count; i++) if (dstA[i] != dstB[i]) { identical = false; break; }
            Check(identical, "LithologyErosionNode.ApplyDifferentialErosion is not deterministic - " +
                             "the same tile produced two different heightmaps");

            // Analytic ground truth, since local mean == global mean at this radius.
            float gm = 0f; for (int i = 0; i < count; i++) gm += src[i]; gm /= count;
            float preSum = 0f;
            float[] expected = new float[count];
            for (int i = 0; i < count; i++)
            {
                float factor = Mathf.Clamp(Mathf.Lerp(1f, klsu[i] / meanK, strength), 0.25f, 4f);
                expected[i] = gm + (src[i] - gm) * factor;
                preSum += expected[i];
            }
            float srcSum = 0f; for (int i = 0; i < count; i++) srcSum += src[i];
            float delta = (srcSum - preSum) / count;
            float maxErr = 0f;
            for (int i = 0; i < count; i++)
                maxErr = Mathf.Max(maxErr, Mathf.Abs(dstA[i] - (expected[i] + delta)));
            Check(maxErr < 1e-4f,
                  "ApplyDifferentialErosion does not match the mean-preserving relief formula " +
                  "(max error " + maxErr + ")");

            // Mean preservation: the tile mean must not move (a millimetre budget is generous here).
            float outMean = 0f; for (int i = 0; i < count; i++) outMean += dstA[i]; outMean /= count;
            Check(Mathf.Abs(outMean - gm) < 1e-5f,
                  "ApplyDifferentialErosion moved the tile mean (" + gm + " -> " + outMean + ")");

            // Strength 0 is an exact pass-through - disabling the effect restores the input bit-for-bit.
            float[] off = new float[count];
            LithologyErosionNode.ApplyDifferentialErosion(src, off, klsu, res, res, radius, 0f, meanK);
            bool passthrough = true;
            for (int i = 0; i < count; i++) if (off[i] != src[i]) { passthrough = false; break; }
            Check(passthrough, "ApplyDifferentialErosion at strength 0 is not an exact pass-through");

            // Direction: a hard-bed pixel amplifies its departure from the mean, a soft-bed pixel
            // damps it. Pick two pixels with a clearly nonzero relief so the tiny mean delta is noise.
            int hard = -1, soft = -1;
            for (int i = 0; i < count && (hard < 0 || soft < 0); i++)
            {
                if (Mathf.Abs(src[i] - gm) < 0.02f) continue;
                if (klsu[i] > meanK && hard < 0) hard = i;
                if (klsu[i] < meanK && soft < 0) soft = i;
            }
            if (hard >= 0)
                Check(Mathf.Abs(dstA[hard] - gm) > Mathf.Abs(src[hard] - gm),
                      "A hard bed did not stand its relief OUT (bench/scarp) as it must");
            if (soft >= 0)
                Check(Mathf.Abs(dstA[soft] - gm) < Mathf.Abs(src[soft] - gm),
                      "A soft bed did not let its relief RECEDE (slope) as it must");
        }

        /// <summary>
        /// The G-15 done-when, pinned on the pure cover resolver: a steep ridge crest shows bare rock,
        /// a footslope below the repose angle shows the SAME rock's talus (colluvium sourced from that
        /// unit), and a flat valley floor shows allochthonous alluvium (source -1) - with no painted
        /// layer anywhere, since the answer is a pure function of slope, valley weight and bedrock.
        /// Also checks the talus thickens as the footslope flattens, and that the resolver is pure.
        /// </summary>
        private static void TestSurficialCover()
        {
            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                profile.PopulateExampleColumn();
                var unit = profile.UnitAt(2);          // the youngest exposed bed
                Check(unit != null, "TestSurficialCover could not read an example bed to source cover from");
                if (unit == null) return;
                unit.angleOfReposeDeg = 34f;           // fix the repose angle so slopes are hand-checkable
                var bedrock = new LithoSample { unitIndex = 2, unit = unit };

                // Ridge crest: slope above repose -> the slope sheds to bare rock.
                var crest = SurficialCoverResolver.Resolve(profile, bedrock, 45f, 0.0f, 0f);
                Check(crest.type == CoverType.BedrockExposed && crest.depthM <= 0f,
                      "A ridge crest steeper than the repose angle did not expose bedrock (got " +
                      crest.type + ")");

                // Footslope: below repose, not a valley, no wind -> the source rock's talus.
                var foot = SurficialCoverResolver.Resolve(profile, bedrock, 20f, 0.1f, 0f);
                Check(foot.type == CoverType.Colluvium && foot.sourceUnitIndex == 2 && foot.depthM > 0f,
                      "A footslope below the repose angle did not carry the source rock's talus (got " +
                      foot.type + ", source " + foot.sourceUnitIndex + ")");

                // Valley floor: flat and high valley weight -> allochthonous sorted alluvium.
                var valley = SurficialCoverResolver.Resolve(profile, bedrock, 1f, 0.9f, 0f);
                Check(valley.type == CoverType.Alluvium && valley.sourceUnitIndex == -1 && valley.depthM > 0f,
                      "A flat valley floor did not resolve to allochthonous alluvium (got " +
                      valley.type + ", source " + valley.sourceUnitIndex + ")");

                // Talus thickens as the footslope flattens toward the base (L-34 wedge).
                var steepFoot = SurficialCoverResolver.Resolve(profile, bedrock, 30f, 0.1f, 0f);
                var gentleFoot = SurficialCoverResolver.Resolve(profile, bedrock, 10f, 0.1f, 0f);
                Check(gentleFoot.depthM > steepFoot.depthM,
                      "Talus did not thicken as the footslope flattened - the wedge law has the wrong sign");

                // Purity: the same inputs give the same cover (cross-edge coherence by construction).
                var a = SurficialCoverResolver.Resolve(profile, bedrock, 20f, 0.1f, 0f);
                var b = SurficialCoverResolver.Resolve(profile, bedrock, 20f, 0.1f, 0f);
                Check(a.type == b.type && a.depthM == b.depthM && a.sourceUnitIndex == b.sourceUnitIndex,
                      "SurficialCoverResolver is not a pure function - the same point gave two answers");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The shared ground-shape reader both G-70 data nodes lean on. A flat tile must read as
        /// slope 0 with an up normal; a constant-gradient ramp climbing in +X must read the exact
        /// arctangent slope and FACE downhill (west, 270 deg); a convex cap must read positive
        /// curvature and a concave bowl negative - the sign convention EnvironmentSample expects.
        /// </summary>
        private static void TestMatrixSurfaceForm()
        {
            const int res = 8;
            var flat = new Matrix(new CoordRect(0, 0, res, res));
            flat.worldSize = new Vector3(70f, 100f, 70f); // 10 m between pixels, 100 m height scale
            flat.worldHeight = 100f;
            var ff = MatrixSurfaceForm.Sample(flat, 3, 3);
            Check(Mathf.Abs(ff.slopeDeg) < 1e-3f && ff.normal.y > 0.999f,
                  "MatrixSurfaceForm read a flat tile as sloped (" + ff.slopeDeg + " deg)");

            // Ramp: height rises 0.03 per pixel in X -> 3 m per 10 m run -> atan(0.3).
            var ramp = new Matrix(new CoordRect(0, 0, res, res));
            ramp.worldSize = new Vector3(70f, 100f, 70f);
            ramp.worldHeight = 100f;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    ramp.arr[z * res + x] = 0.03f * x;
            var rf = MatrixSurfaceForm.Sample(ramp, 3, 3);
            float expected = Mathf.Atan(0.3f) * Mathf.Rad2Deg;
            Check(Mathf.Abs(rf.slopeDeg - expected) < 0.5f,
                  "MatrixSurfaceForm slope on a known ramp is wrong (" + rf.slopeDeg + " vs " + expected + ")");
            Check(Mathf.Abs(Mathf.DeltaAngle(rf.aspectDeg, 270f)) < 1f,
                  "A ramp climbing east must face downhill west (270 deg), got " + rf.aspectDeg);

            // Convex cap vs concave bowl at the centre: h = +/- k*((x-c)^2 + (z-c)^2).
            var cap = new Matrix(new CoordRect(0, 0, res, res));
            var bowl = new Matrix(new CoordRect(0, 0, res, res));
            cap.worldSize = bowl.worldSize = new Vector3(70f, 100f, 70f);
            cap.worldHeight = bowl.worldHeight = 100f;
            const float c = 3.5f;
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    float r2 = (x - c) * (x - c) + (z - c) * (z - c);
                    cap.arr[z * res + x] = -0.01f * r2;  // peak at centre -> convex
                    bowl.arr[z * res + x] = 0.01f * r2;  // dip at centre  -> concave
                }
            Check(MatrixSurfaceForm.Sample(cap, 3, 3).curvature > 0f,
                  "A convex cap must read positive curvature");
            Check(MatrixSurfaceForm.Sample(bowl, 3, 3).curvature < 0f,
                  "A concave bowl must read negative curvature");
        }
        /// <summary>
        /// LithologyOutput's contract (G-70): unwired it emits a placed field of zeros (so a graph that
        /// adds it is unchanged), and wired it reports honest per-pixel geology - a valid exposed unit
        /// index, a non-negative cover depth that is positive where a footslope carries talus, and a
        /// positive bedrock erodibility - deterministically. Pinned on the pure ComputeChannel core plus
        /// the node's own unwired Generate path, with no scene and no graph evaluator.
        /// </summary>
        private static void TestLithologyOutputNode()
        {
            const int res = 8;
            var tile = new TileData(new Coord(0, 0), new CoordRect(0, 0, res, res),
                                    Vector3.zero, new Vector3(70f, 100f, 70f), 100f);
            var node = new LithologyOutput { profile = null };

            // Unwired (no profile, no evaluator bound): every channel is a placed field of zeros.
            for (int ch = 0; ch < 4; ch++)
            {
                var z = node.Generate(ch, tile, null) as Matrix;
                Check(z != null && z.arr.Length == res * res && AllZero(z.arr),
                      "LithologyOutput channel " + ch + " is not neutral zeros when unwired");
            }

            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                profile.PopulateExampleColumn();

                // Profile set but height still unwired -> still neutral zeros (adds nothing to the graph).
                node.profile = profile;
                Check(AllZero((node.Generate(0, tile, null) as Matrix).arr),
                      "LithologyOutput with a profile but no height input must still emit zeros");

                // Active core: a gentle 17-deg ramp -> a footslope that carries colluvium (depth > 0).
                var height = tile.CreateMatrix();
                for (int zz = 0; zz < res; zz++)
                    for (int xx = 0; xx < res; xx++)
                        height.arr[zz * res + xx] = 0.03f * xx;

                var idx = LithologyOutput.ComputeChannel(height, profile, 0, 12345, 2f);
                var depth = LithologyOutput.ComputeChannel(height, profile, 1, 12345, 2f);
                var ero = LithologyOutput.ComputeChannel(height, profile, 3, 12345, 2f);

                int maxUnit = profile.UnitCount - 1;
                bool idxValid = true, depthOk = true, anyCover = false, eroPositive = true;
                for (int i = 0; i < res * res; i++)
                {
                    if (idx.arr[i] < 0f || idx.arr[i] > maxUnit) idxValid = false;
                    if (depth.arr[i] < 0f) depthOk = false;
                    if (depth.arr[i] > 0f) anyCover = true;
                    if (ero.arr[i] <= 0f) eroPositive = false;
                }
                Check(idxValid, "LithologyOutput unit index left the valid [0, UnitCount-1] range");
                Check(depthOk && anyCover, "LithologyOutput cover depth is negative, or never positive on a footslope");
                Check(eroPositive, "LithologyOutput erodibility is not positive where bedrock is exposed");

                var idx2 = LithologyOutput.ComputeChannel(height, profile, 0, 12345, 2f);
                bool deterministic = true;
                for (int i = 0; i < res * res; i++) if (idx.arr[i] != idx2.arr[i]) { deterministic = false; break; }
                Check(deterministic, "LithologyOutput.ComputeChannel is not deterministic");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
        /// <summary>
        /// MicroReliefOutput's contract (G-70): unwired it emits zeros, and wired it reports the SAME
        /// field the overlay draws - a finite, deterministic offset in millimetres, and a dominant
        /// process index that, on a bare geology-and-form context with every climate/geomorphic driver
        /// left at zero, is ONLY a rock-and-form process (jointing 0, spheroidal 1, clast lag 4) or none
        /// (-1). A steep bedrock slope must actually fire one, proving the reading is not vacuous, while
        /// no rain/frost/wind/biology process can appear from inputs that never supplied them.
        /// </summary>
        private static void TestMicroReliefOutputNode()
        {
            const int res = 8;
            var tile = new TileData(new Coord(0, 0), new CoordRect(0, 0, res, res),
                                    Vector3.zero, new Vector3(70f, 100f, 70f), 100f);
            var node = new MicroReliefOutput { profile = null };

            for (int ch = 0; ch < 2; ch++)
            {
                var z = node.Generate(ch, tile, null) as Matrix;
                Check(z != null && z.arr.Length == res * res && AllZero(z.arr),
                      "MicroReliefOutput channel " + ch + " is not neutral zeros when unwired");
            }

            var profile = ScriptableObject.CreateInstance<LithologyProfile>();
            try
            {
                profile.PopulateExampleColumn();

                // A steep 45-deg ramp -> bedrock is exposed, so the jointing/spheroidal processes can act.
                var height = tile.CreateMatrix();
                for (int zz = 0; zz < res; zz++)
                    for (int xx = 0; xx < res; xx++)
                        height.arr[zz * res + xx] = 0.10f * xx;

                var offset = MicroReliefOutput.ComputeChannel(height, profile, 0, 12345, 2f);
                var proc = MicroReliefOutput.ComputeChannel(height, profile, 1, 12345, 2f);

                bool finite = true, onlyGeology = true, anyFired = false;
                for (int i = 0; i < res * res; i++)
                {
                    if (float.IsNaN(offset.arr[i]) || float.IsInfinity(offset.arr[i])) finite = false;
                    int p = Mathf.RoundToInt(proc.arr[i]);
                    if (p != -1 && p != 0 && p != 1 && p != 4) onlyGeology = false; // rock/form processes only
                    if (p != -1) anyFired = true;
                }
                Check(finite, "MicroReliefOutput produced a non-finite displacement");
                Check(onlyGeology, "MicroReliefOutput fired a climate/geomorphic process from zeroed drivers");
                Check(anyFired, "MicroReliefOutput fired nothing on exposed bedrock - the reading is vacuous");

                var off2 = MicroReliefOutput.ComputeChannel(height, profile, 0, 12345, 2f);
                bool deterministic = true;
                for (int i = 0; i < res * res; i++) if (offset.arr[i] != off2.arr[i]) { deterministic = false; break; }
                Check(deterministic, "MicroReliefOutput.ComputeChannel is not deterministic");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
        /// <summary>True when every element is exactly zero - the neutral-field check the G-70 nodes must pass.</summary>
        private static bool AllZero(float[] a)
        {
            if (a == null) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != 0f) return false;
            return true;
        }
    }
}


