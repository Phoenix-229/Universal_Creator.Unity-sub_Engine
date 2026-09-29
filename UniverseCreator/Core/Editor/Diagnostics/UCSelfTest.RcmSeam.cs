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
//    * U-5  The provider surface a tool reads the world through stays at EXACTLY six
//           members. A river's shape and a dam's levels travel as DATA (the reach
//           publication and the shape inbox), never as a seventh interface method,
//           because growing that surface reads as drift to any tool built against the
//           older count. This pins the count so a seventh member cannot ship silently.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Integration;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        // U-5. The IPCGDataProvider surface is the versioned contract a path tool reads the world through. B-27's whole
        // request/publish design exists to avoid growing it, so the count is pinned here: a seventh member added by a
        // well-meant "just expose the river depth" would fail this immediately rather than ship and read as drift.
        private const int ExpectedProviderMemberCount = 6;

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

        private static void TestProviderSurfaceStaysSixMembers()
        {
            Check(PathProviderSocket.PublishedProviderMemberCount == ExpectedProviderMemberCount,
                "the IPCGDataProvider surface is no longer " + ExpectedProviderMemberCount + " members (it is now "
                + PathProviderSocket.PublishedProviderMemberCount + "). A river's shape and a dam's levels must travel "
                + "as data through the reach publication and the shape inbox, never as a new provider method -- growing "
                + "this surface reads as drift to every tool built against the older count.");
        }
    }
}
