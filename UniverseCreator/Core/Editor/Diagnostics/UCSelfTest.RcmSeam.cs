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
//    * U-4  A published river reach reads back the width and the surface/bed levels
//           UC dug, interpolated between stations, and the publication only bumps its
//           version when the set actually changes -- an unmoved river must not make a
//           reader rebuild banks that are already right.
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

        private static void TestRiverProfilePublication()
        {
            // A two-station reach: head at (0,0) 10 m wide, surface 100, bed 98 (2 m deep); mouth at (0,100) 20 m wide,
            // surface 90, bed 86 (4 m deep). Every figure interpolates linearly along the one segment.
            RiverReachProfile reach = new RiverReachProfile { sourceId = "river-1", sourceLabel = "test river" };
            reach.stations = new[]
            {
                new RiverProfileStation { worldX = 0f, worldZ = 0f, widthMetres = 10f,
                    surfaceLevelMetres = 100f, bedLevelMetres = 98f },
                new RiverProfileStation { worldX = 0f, worldZ = 100f, widthMetres = 20f,
                    surfaceLevelMetres = 90f, bedLevelMetres = 86f },
            };

            Check(reach.HasWork, "a two-station reach did not report usable work.");
            CheckClose(reach.stations[0].DepthMetres, 2f, "the head station's design depth is not surface less bed.");

            // Sampled at the midpoint the three figures are the average of the two ends.
            float width, surface, bed;
            bool sampled = reach.TrySampleAt(0f, 50f, out width, out surface, out bed);
            Check(sampled, "sampling a two-station reach at its midpoint returned nothing.");
            CheckClose(width, 15f, "midpoint channel width is not interpolated between the stations.");
            CheckClose(surface, 95f, "midpoint water surface is not interpolated between the stations.");
            CheckClose(bed, 92f, "midpoint bed level is not interpolated between the stations.");

            Bounds footprint;
            Check(reach.TryGetFootprint(out footprint), "a usable reach reported no plan footprint.");

            // A reach with fewer than two stations is not a line: no work, and nothing to sample.
            RiverReachProfile stub = new RiverReachProfile { sourceId = "river-stub" };
            stub.stations = new[] { new RiverProfileStation { worldX = 0f, worldZ = 0f } };
            Check(!stub.HasWork, "a single-station reach was treated as a usable river.");
            Check(!stub.TrySampleAt(0f, 0f, out width, out surface, out bed),
                "a single-station reach returned a sample it cannot describe.");

            // The publication component: deposit, restate-unchanged, restate-moved, withdraw.
            GameObject go = new GameObject("uc_selftest_river_publication");
            try
            {
                RiverProfilePublication pub = go.AddComponent<RiverProfilePublication>();

                Check(pub.Deposit(reach), "depositing a new river reach reported no change.");
                Check(pub.Count == 1, "the publication did not hold the deposited reach.");
                int afterFirst = pub.Version;
                Check(afterFirst > 0, "depositing a reach did not bump the publication version.");

                // Restating the SAME reach must not bump the version -- a reader would rebuild banks that never moved.
                Check(!pub.Deposit(reach), "restating an unchanged reach reported a change.");
                Check(pub.Version == afterFirst, "restating an unchanged reach bumped the version.");

                // A moved reach under the same id replaces it and bumps the version.
                RiverReachProfile moved = new RiverReachProfile { sourceId = "river-1" };
                moved.stations = new[]
                {
                    new RiverProfileStation { worldX = 5f, worldZ = 0f, widthMetres = 10f,
                        surfaceLevelMetres = 100f, bedLevelMetres = 98f },
                    new RiverProfileStation { worldX = 5f, worldZ = 100f, widthMetres = 20f,
                        surfaceLevelMetres = 90f, bedLevelMetres = 86f },
                };
                Check(pub.Deposit(moved), "depositing a moved reach under the same id reported no change.");
                Check(pub.Count == 1, "a moved reach was added as a second entry instead of replacing the first.");
                Check(pub.Version > afterFirst, "a moved reach did not bump the version.");

                Check(pub.Withdraw("river-1"), "withdrawing a published reach reported nothing removed.");
                Check(pub.Count == 0, "a withdrawn reach was left in the publication.");
                Check(!pub.Withdraw("river-1"), "withdrawing an absent reach reported a removal.");
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
