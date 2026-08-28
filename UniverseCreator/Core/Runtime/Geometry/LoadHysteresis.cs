// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LoadHysteresis.cs
//  Purpose: Stop the streaming ring thrashing on a load boundary (§ 9.9 G-59). A camera
//           parked exactly on the load radius, jittering by a hair, loads and unloads the
//           same chunk forever - each cycle a build and a free, for no visible benefit. The
//           cure is a dead band: load at loadRadius, but do not unload until the chunk is
//           unloadHysteresisM (default 1.25 × cell size) FURTHER out. Between the two radii
//           the chunk keeps whatever residency it already has, so crossing the load boundary
//           back and forth changes nothing.
//
//  THE DEAD BAND IS [loadRadius, loadRadius + hysteresis]
//  ------------------------------------------------------
//  Inside the load radius a chunk loads; beyond the unload radius it unloads; between them the
//  decision is "no change". That asymmetry is the whole mechanism - a single symmetric threshold
//  is what thrashes. `Decide` returns Load / Unload / None from the distance, the two radii and
//  the current residency, so the caller cannot accidentally use one radius for both edges.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Distances and radii, no scene. The self-test parks a resident chunk on the boundary, jitters
//  it inside the band and asserts zero load/unload cycles, then pushes it past the unload radius
//  and asserts exactly one unload.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>What the streaming ring should do with a chunk this frame.</summary>
    public enum ResidencyAction { None = 0, Load = 1, Unload = 2 }

    /// <summary>
    /// Load/unload hysteresis for the streaming ring (§ 9.9 G-59): load at the load radius, unload only past a
    /// larger unload radius, hold residency in the dead band between. Kills boundary thrash. Device-free.
    /// </summary>
    public static class LoadHysteresis
    {
        /// <summary>The default unload hysteresis: 1.25 × cell size.</summary>
        public static float DefaultHysteresis(float cellSizeM)
        {
            return 1.25f * (cellSizeM < 0f ? 0f : cellSizeM);
        }

        /// <summary>The radius past which a resident chunk unloads: loadRadius + hysteresis (hysteresis clamped
        /// non-negative).</summary>
        public static float UnloadRadius(float loadRadiusM, float unloadHysteresisM)
        {
            return loadRadiusM + (unloadHysteresisM < 0f ? 0f : unloadHysteresisM);
        }

        /// <summary>Decide the action from distance, the load radius, the hysteresis and current residency: Load a
        /// non-resident chunk within the load radius; Unload a resident chunk beyond the unload radius; otherwise
        /// hold (None). The dead band between the two radii is what stops the boundary thrash.</summary>
        public static ResidencyAction Decide(float distanceM, float loadRadiusM, float unloadHysteresisM, bool currentlyResident)
        {
            float unloadR = UnloadRadius(loadRadiusM, unloadHysteresisM);
            if (!currentlyResident)
                return distanceM <= loadRadiusM ? ResidencyAction.Load : ResidencyAction.None;
            return distanceM > unloadR ? ResidencyAction.Unload : ResidencyAction.None;
        }
    }
}
