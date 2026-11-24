// -----------------------------------------------------------------------------
//  Universe Creator - Diagnostics
//  Module 6: Road & City Master seam (the UC side of B-27, tasks U-1, U-4, U-5)
//
//  File:    UCSelfTest.RcmSeam.cs
//  Purpose: Pins the behaviour of the three UC-side seam pieces a maintainer could
//           plausibly "tidy" into something wrong and never notice until a dam, a
//           riverbank or a tool link broke in a shipped world. A partial of
//           UCSelfTest, so these land in the same tally and share Check / CheckClose.
//
//  WHAT THESE CHECKS ARE FOR
//  -------------------------
//    * U-1  A dam GOVERNS a water body's surface transiently: raising it reports a
//           change, restating the same governance does not (so a settle that re-runs
//           an unmoved dam does not rebuild the reservoir), and clearing returns the
//           body to its authored level. This is the contract DamReservoirReconciler
//           drives every settle pass; the scene-heavy basin re-dig it also does is
//           covered by the build itself, not re-run here.
//    * U-5  REMOVED (PLG-5). The old IPCGDataProvider provider-surface pin went with the
//           linker: an external system no longer reads the world through a fixed
//           interface surface, it reads through UCPluginContext samplers the host hands
//           it, so there is no member-count to pin. The dam governance pin (U-1) stays -
//           it is UCWaterBody behaviour, nothing to do with the linker.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestReservoirGovernance()
        {
            GameObject go = new GameObject("uc_selftest_reservoir");
            try
            {
                go.transform.position = new Vector3(0f, 12f, 0f);
                UCWaterBody body = go.AddComponent<UCWaterBody>();

                float authored = body.SurfaceWorldY();

                // First governance raises the body and reports a change, so the reconciler knows to rebuild it.
                bool first = body.GovernAsReservoir(250f, 240f, 10f, 20f, new Vector2(0f, 1f), 30f, "dam-1");
                Check(first, "governing an ungoverned body did not report a change.");
                CheckClose(body.SurfaceWorldY(), 250f,
                    "a dam-governed body's surface did not sit at the held reservoir level.");

                // Restating the identical governance must report NO change: a settle that re-runs an unmoved dam must
                // not churn a reservoir rebuild every pass.
                bool again = body.GovernAsReservoir(250f, 240f, 10f, 20f, new Vector2(0f, 1f), 30f, "dam-1");
                Check(!again, "restating identical reservoir governance reported a change and would rebuild forever.");

                // A moved surface reports a change again.
                bool moved = body.GovernAsReservoir(260f, 240f, 10f, 20f, new Vector2(0f, 1f), 30f, "dam-1");
                Check(moved, "raising the reservoir level did not report a change.");

                // Clearing returns the body to its authored level, and only reports a change when it was governed.
                bool cleared = body.ClearReservoirGovernance();
                Check(cleared, "clearing a governed body did not report that it had been governed.");
                CheckClose(body.SurfaceWorldY(), authored,
                    "a released body did not return to its authored surface level.");
                Check(!body.ClearReservoirGovernance(),
                    "clearing an ungoverned body reported a change it did not make.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
