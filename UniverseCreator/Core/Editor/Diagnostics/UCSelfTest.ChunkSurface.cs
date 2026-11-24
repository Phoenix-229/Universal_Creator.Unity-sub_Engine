// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  File:    UCSelfTest.ChunkSurface.cs
//  Purpose: Asserts the "systems work as one" contract for the chunk manager -
//           that a hand sculpt survives a graph regeneration as a delta riding on
//           top of the new surface, and that the capture funnel ships on by
//           default the way its own documentation says it must.
//
//  WHY THIS EXISTS
//  ---------------
//  The chunk manager and the graph are one system with one source of truth: the
//  graph produces the surface, and a manual edit is a DELTA that is re-applied on
//  top of whatever the graph produces underneath - never a frozen absolute that
//  punches a scar through a re-run. Two failures make that promise silently
//  false, and neither is a compile error:
//
//    * captureManualEditsOnRegenerate shipping ON. It used to be pinned on, and
//      the argument was its own doc-comment: "losing sculpting work to a
//      background regeneration is the worse failure". That holds only while a
//      HUMAN is the only writer of the heightfield. With a road or plugin also
//      writing it, capture cannot tell a cut from a brush stroke - both are just
//      "the terrain differs from what UC wrote" - so ON meant the first
//      regeneration after a road bake recorded the cut as an author sculpt and
//      every later one re-applied it, permanently and silently. DEF-1 flipped the
//      default OFF and flipped this check with it; it is pinned here so a careless
//      merge cannot quietly restore the old behaviour.
//    * The override layer recording float noise from the TerrainData round-trip
//      as if it were a sculpt, which would freeze the whole chunk against the
//      graph - a divergent copy of the surface wearing an edit's clothes.
//
//  This drives TerrainOverrideLayer directly over float[] - no scene, no
//  TerrainData, no GameObject beyond the one component the default check needs -
//  so it runs headless in the batch self test with the rest.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>
        /// A sculpt captured against one generated surface must re-appear, as the
        /// same delta, on top of a completely different regenerated surface - and
        /// the capture funnel must default to on, matching its own documentation.
        /// </summary>
        private static void TestManualSculptSurvivesRegeneration()
        {
            const int res = 33;
            const float mps = 3.125f;            // metres per sample, project cell family
            const float delta = 0.05f;           // the hand-raised amount, normalised
            const float tol = 1e-4f;

            // A chunk whose min corner sits at world origin, so a stored sample key
            // (x, z) maps straight to the local index (x, z) with no offset to get
            // wrong - the arithmetic under test is the layer's, not the fixture's.
            Bounds bounds = new Bounds(
                new Vector3(res * mps * 0.5f, 300f, res * mps * 0.5f),
                new Vector3(res * mps, 600f, res * mps));

            // --- run 1: the graph produces a surface, the user sculpts a patch ---
            float[,] generated1 = new float[res, res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    generated1[z, x] = 0.2f + 0.3f * (x / (float)(res - 1));   // 0.2..0.5 ramp

            // A hand sculpt: raise a 3x3 patch at the centre by a known amount.
            float[,] edited = (float[,])generated1.Clone();
            for (int z = 15; z <= 17; z++)
                for (int x = 15; x <= 17; x++)
                    edited[z, x] += delta;

            TerrainOverrideLayer layer = new TerrainOverrideLayer();
            int captured = layer.CaptureDifference(edited, generated1, bounds, mps);

            Check(captured == 9,
                  "CaptureDifference recorded " + captured + " samples for a 3x3 sculpt, "
                  + "expected 9 - either it missed the edit or it recorded flat ground");
            Check(layer.EditCount == 9,
                  "the override layer holds " + layer.EditCount + " edits after a 3x3 sculpt, expected 9");
            Check(layer.AffectsRegion(bounds, mps),
                  "the layer reports the sculpted chunk as untouched - the apply pass would skip it");

            // --- run 2: the graph regenerates a DIFFERENT surface; the edit must ride on top ---
            // A ramp along the other axis, so if the edit were stored as a frozen
            // absolute it would show as a flat scar instead of a raised bump.
            float[,] generated2 = new float[res, res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    generated2[z, x] = 0.4f + 0.2f * (z / (float)(res - 1));   // 0.4..0.6 ramp

            float[,] expected = (float[,])generated2.Clone();
            for (int z = 15; z <= 17; z++)
                for (int x = 15; x <= 17; x++)
                    expected[z, x] += delta;

            layer.ApplyTo(generated2, bounds, mps);

            bool patchRidesOnTop = true;
            bool groundUntouched = true;
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    bool inPatch = x >= 15 && x <= 17 && z >= 15 && z <= 17;
                    if (Mathf.Abs(generated2[z, x] - expected[z, x]) > tol)
                    {
                        if (inPatch) patchRidesOnTop = false;
                        else groundUntouched = false;
                    }
                }
            }

            Check(patchRidesOnTop,
                  "the sculpted patch did not survive regeneration as a delta on top of the "
                  + "new surface - a hand edit is being lost or frozen against the graph");
            Check(groundUntouched,
                  "applying the sculpt changed ground the user never touched - the edit is "
                  + "not sparse, so the graph is no longer the source of truth off the patch");

            // --- float noise from the TerrainData round-trip must not read as a sculpt ---
            float[,] noised = (float[,])generated1.Clone();
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    noised[z, x] += 1e-6f;    // below CaptureDifference's 1e-5 threshold

            TerrainOverrideLayer noiseLayer = new TerrainOverrideLayer();
            int noiseCaptured = noiseLayer.CaptureDifference(noised, generated1, bounds, mps);
            Check(noiseCaptured == 0 && noiseLayer.IsEmpty,
                  "CaptureDifference recorded " + noiseCaptured + " samples of sub-threshold "
                  + "noise - a round-trip through TerrainData would freeze the whole chunk");

            // --- DEF-1: the capture funnel must ship OFF, and this check was INVERTED ---
            // It used to assert the flag ships ON, quoting the field's own comment that
            // losing a sculpt to a background regeneration is the worse failure. That was
            // right while a human was the only writer of the heightfield. It is wrong now:
            // capture diffs the live terrain against what UC last wrote, so a road's cut is
            // indistinguishable from a brush stroke, and shipping ON meant the first
            // regeneration after a road bake recorded that cut as an author sculpt and every
            // later regeneration re-applied it. The terrain stopped tracking the graph, the
            // original could not be recovered, and nobody had pressed Capture.
            //
            // The author hit exactly that and ruled on it: "if the nodes change or any shift,
            // no need to retain the terrain", "all must be all regenerated again". So the
            // default flipped and this gate flipped with it, deliberately and in the same
            // edit - a gate left pinning the old value would have failed and read as a
            // regression rather than as the decision it is.
            GameObject probe = new GameObject("UC_SelfTest_CaptureDefaultProbe");
            try
            {
                UniverseCreatorObject owner = probe.AddComponent<UniverseCreatorObject>();
                Check(!owner.captureManualEditsOnRegenerate,
                      "DEF-1: UniverseCreatorObject.captureManualEditsOnRegenerate must ship "
                      + "OFF. On, it cannot tell a road's cut from a hand sculpt, so the first "
                      + "regeneration bakes the cut in as an author edit and the terrain stops "
                      + "responding to the graph for ever. Capture is an explicit button now.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        /// <summary>
        /// GAP-1. A graphless sculpt captured at one heightmap resolution must re-appear
        /// at the correct WORLD position after a resolution change - not vanish, and not
        /// slide off its spot. This is the guarantee the graphless capture in
        /// UniverseCreatorObject.RebuildChunks leans on: it lifts the sculpt into the
        /// override layer while the old resolution still matches, and the layer's job is
        /// to rescale that delta onto whatever sample grid the re-fit lands on. If the
        /// layer stored raw sample indices instead of a world scale, a graphless
        /// resolution change would drop the edit in the wrong place - which is the same
        /// silent-loss failure the capture was added to prevent, just relocated.
        /// Driven over float[] at two different metres-per-sample, no scene or TerrainData.
        /// </summary>
        private static void TestSculptSurvivesResolutionChange()
        {
            // Same physical chunk (100 m, min corner at origin) sampled at two grids.
            const int resA = 41;
            const float mpsA = 2.5f;             // 100 m / (41 - 1)
            const int resB = 51;
            const float mpsB = 2.0f;             // 100 m / (51 - 1)
            const float delta = 0.05f;
            const float tol = 1e-4f;

            Bounds bounds = new Bounds(new Vector3(50f, 300f, 50f), new Vector3(100f, 600f, 100f));

            // --- captured at resolution A: a 3x3 sculpt centred in the chunk ---
            float[,] generatedA = new float[resA, resA];
            for (int z = 0; z < resA; z++)
                for (int x = 0; x < resA; x++)
                    generatedA[z, x] = 0.3f;

            float[,] editedA = (float[,])generatedA.Clone();
            const int cA = (resA - 1) / 2;                       // 20 -> world 50 m
            for (int z = cA - 1; z <= cA + 1; z++)
                for (int x = cA - 1; x <= cA + 1; x++)
                    editedA[z, x] += delta;

            TerrainOverrideLayer layer = new TerrainOverrideLayer();
            int captured = layer.CaptureDifference(editedA, generatedA, bounds, mpsA);

            Check(captured == 9,
                  "CaptureDifference recorded " + captured + " samples at the source resolution, "
                  + "expected 9 for a 3x3 sculpt");
            Check(layer.EditCount == 9,
                  "the override layer holds " + layer.EditCount + " edits after a 3x3 sculpt, expected 9");

            // --- a resolution change re-seeds a DIFFERENT-resolution flat surface ---
            // The chunk is unchanged in the world; only its sample grid moved. The scale
            // mpsA/mpsB = 1.25 maps source keys 19,20,21 to indices 24,25,26 - a 3x3 that
            // stays centred (resB centre is 25), i.e. still world ~50 m. If the layer
            // ignored the scale, the bump would land at 19,20,21 - off its world spot.
            float[,] heightsB = new float[resB, resB];
            for (int z = 0; z < resB; z++)
                for (int x = 0; x < resB; x++)
                    heightsB[z, x] = 0.3f;

            layer.ApplyTo(heightsB, bounds, mpsB);

            const int cB = (resB - 1) / 2;                       // 25 -> world 50 m
            bool patchLandedRescaled = true;
            bool groundUntouchedAtNewRes = true;
            for (int z = 0; z < resB; z++)
            {
                for (int x = 0; x < resB; x++)
                {
                    bool inRescaledPatch = x >= cB - 1 && x <= cB + 1 && z >= cB - 1 && z <= cB + 1;
                    if (inRescaledPatch)
                    {
                        if (Mathf.Abs(heightsB[z, x] - (0.3f + delta)) > tol) patchLandedRescaled = false;
                    }
                    else
                    {
                        if (Mathf.Abs(heightsB[z, x] - 0.3f) > tol) groundUntouchedAtNewRes = false;
                    }
                }
            }

            Check(patchLandedRescaled,
                  "after a resolution change the captured sculpt did not re-appear at its world "
                  + "position on the new sample grid - a graphless resolution change would lose it");
            Check(groundUntouchedAtNewRes,
                  "applying the rescaled sculpt raised ground the user never touched at the new "
                  + "resolution - the delta smeared instead of landing on its own samples");
        }
    }
}
