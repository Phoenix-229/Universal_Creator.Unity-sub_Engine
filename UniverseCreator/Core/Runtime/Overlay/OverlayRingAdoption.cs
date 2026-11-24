// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    OverlayRingAdoption.cs
//  Purpose: The pure decision behind the overlay ring's domain-reload survival: given the
//           host transform and the ring root's name, is there a whole n×n ring already
//           living there to re-bind to, and if so what are its slots and shared mesh? A
//           reload wipes the driver's managed references but leaves the ring's DontSave
//           GameObjects alive; without this the driver would build a SECOND ring over the
//           first. Factoring the re-bind out here keeps that logic device-free and testable:
//           a self-test can stand up a fake surviving ring and prove the adoption re-binds
//           to it rather than allocating a new set, without ever entering play mode.
//
//  WHY THE CHILD COUNT MUST BE A PERFECT SQUARE
//  --------------------------------------------
//  The ring is exactly n×n patches under one root. A surviving root whose child count is a
//  perfect square is a ring we can take back wholesale - every slot re-addresses itself on
//  the next snap, so their ORDER need not be recovered, only their number. A count that is
//  not a perfect square is a partial teardown or a foreign hierarchy borrowing the name; the
//  driver discards it and builds clean rather than adopting a broken pool.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// Pure helpers for re-binding to a micro-relief ring that outlived a domain reload. No play-mode
    /// dependency and no side effects beyond allocating the returned slot array, so the reload path is
    /// unit-testable: the driver calls these; a self-test calls the same ones on a hand-built hierarchy.
    /// </summary>
    public static class OverlayRingAdoption
    {
        /// <summary>
        /// True when <paramref name="childCount"/> is a whole n×n grid, giving the side n. A ring is exactly
        /// n×n patches, so only a perfect-square count is an adoptable ring; anything else is a broken leftover.
        /// </summary>
        public static bool IsWholeRing(int childCount, out int side)
        {
            side = childCount > 0 ? Mathf.RoundToInt(Mathf.Sqrt(childCount)) : 0;
            return side >= 1 && side * side == childCount;
        }

        /// <summary>
        /// Re-binds to a surviving ring root named <paramref name="rootName"/> under <paramref name="host"/>.
        /// On success returns true and fills the root, the slot array (its patch children, in child order -
        /// the next snap re-addresses them so order is immaterial), the grid side, and the shared mesh taken
        /// from the first patch that carries one. Returns false with null outputs when no such root exists or
        /// its child count is not a whole n×n ring; the caller then discards any leftover and builds fresh.
        /// </summary>
        public static bool TryBind(
            Transform host, string rootName,
            out Transform root, out Transform[] slots, out int gridN, out Mesh sharedMesh)
        {
            root = null;
            slots = null;
            gridN = 0;
            sharedMesh = null;
            if (host == null) return false;

            Transform existing = host.Find(rootName);
            if (existing == null) return false;
            if (!IsWholeRing(existing.childCount, out int side)) return false;

            root = existing;
            gridN = side;
            slots = new Transform[side * side];
            for (int i = 0; i < slots.Length; i++)
            {
                Transform patch = existing.GetChild(i);
                slots[i] = patch;
                if (sharedMesh == null)
                {
                    var mf = patch.GetComponent<MeshFilter>();
                    if (mf != null) sharedMesh = mf.sharedMesh;
                }
            }
            return true;
        }
    }
}
