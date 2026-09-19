// -----------------------------------------------------------------------------
//  Universe Creator - Path Tool Integration (Module 6 seam)
//  File:    RiverProfilePublication.cs
//  Purpose: The serialised place Universe Creator leaves the profiles of the rivers
//           it has built, and a city or road tool reading the seam picks them up.
//           One per world, saved with the scene, so a reach survives the domain
//           reload between UC building the river and the tool building its banks.
//
//  THE REVERSE OF THE INBOX, THE SAME CONTRACT
//  -------------------------------------------
//  TerrainShapeInbox carries requests the OTHER way: a tool WRITES what it wants UC
//  to shape, and UC READS them at its next regeneration. This carries facts UC has
//  already made: UC WRITES a reach for every river it built, and a tool READS them
//  when it lays a bank against one. Same contract shape -- a serialised component on
//  the world object, found through FindFor from the object that owns the provider --
//  used in the opposite direction. It is deliberately NOT a new member on the
//  six-member provider surface: growing that surface would read as drift to a tool
//  built against the older count, and it would put the flow of control the wrong way
//  round, with the tool reaching in for geometry rather than UC handing it over. The
//  provider stays six members; a river's shape is published here, as data.
//
//  WHY A COMPONENT AND NOT A STATIC REGISTRY
//  -----------------------------------------
//  The same reason the inbox is one. WaterCarveRegistry is static because it is
//  rebuilt from live bodies every session; a published reach is different, because a
//  tool may build its banks in a session where UC has not rebuilt the river yet, and
//  a static list is empty after a domain reload. Serialising the reaches on a
//  component saved with the scene is what lets "UC built the river last time, the
//  tool banks it this time" work at all.
//
//  WHO WRITES AND WHO READS
//  ------------------------
//  UC WRITES, through Deposit/Withdraw, from the settle pass at the end of a
//  regeneration, once every river has re-fitted to the ground that just landed -- so
//  a reach is the river as finally cut, not a half-built guess. A tool READS, through
//  Reaches or a reach's TrySampleAt, when it needs to know how deep and wide the
//  channel runs where it is building. UC never calls the tool and the tool never
//  calls UC: the publication is the only thing between them, and it is data.
//
//  REVERSIBILITY LIVES IN THE ID
//  -----------------------------
//  Every reach carries the id of the river it describes. Depositing the same id
//  again replaces the older reach (the river moved; restate it) and only bumps the
//  version if the geometry actually changed, so a settle that re-solves an unmoved
//  river does not make a reader rebuild banks that are already right. Withdrawing an
//  id removes it (the river was deleted), so a bank built against a river that is
//  gone has nothing left to read and falls away on the reader's next rebuild.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Integration
{
    /// <summary> The per-world store of <see cref="RiverReachProfile"/>s Universe Creator has published for the rivers it
    /// built. It lives on the world object (the one that owns the provider and the inbox); UC's settle pass writes it
    /// with <see cref="Deposit"/>/<see cref="Withdraw"/> and a tool finds it with <see cref="FindFor"/>. </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Integration/River Profile Publication")]
    public class RiverProfilePublication : MonoBehaviour
    {
        [Tooltip("The profile of every river Universe Creator has built for this world -- its per-station width, water "
               + "surface level and bed level, in world metres. Written by the settle pass, read by a city or road tool "
               + "laying banks. Restating a river's id replaces its reach; withdrawing one lets a bank built against a "
               + "deleted river fall away on the next rebuild.")]
        [SerializeField]
        private List<RiverReachProfile> reaches = new List<RiverReachProfile>();

        /// <summary> Bumped whenever the published set actually changes, so a reader can tell a rebuild is owed by
        /// comparing a number rather than the reaches themselves -- the discipline WaterCarveRegistry and the inbox both
        /// follow. Deliberately NOT bumped by a deposit that restates an unchanged river, so a world that settles many
        /// times without a river moving does not make a reader rebuild its banks on every one. Not serialised: a freshly
        /// loaded scene is read on its content, not on a version carried across the reload. </summary>
        [System.NonSerialized]
        private int version;

        public int Version { get { return version; } }

        public int Count { get { return reaches != null ? reaches.Count : 0; } }

        /// <summary> Read access to the current reaches. The list itself is returned, not a copy, because the one reader
        /// (a tool's bank build, main thread) only enumerates it; do not hold the reference across a Deposit. </summary>
        public IReadOnlyList<RiverReachProfile> Reaches
        {
            get { return reaches ?? (reaches = new List<RiverReachProfile>()); }
        }

        /// <summary> Publish a river's reach, or restate one already published under the same id. Main thread only.
        /// Returns true only when the set actually changed -- a new river, a moved one, or a withdrawal -- so the caller
        /// can count real changes and the version moves only when a reader must act. A reach with no usable line (fewer
        /// than two stations) is treated as a withdrawal of its id: an unsolved river and a deleted one are the same
        /// instruction to a reader, "there is nothing to bank here". </summary>
        public bool Deposit(RiverReachProfile reach)
        {
            if (reach == null) { return false; }
            if (reaches == null) { reaches = new List<RiverReachProfile>(); }

            if (!reach.HasWork) { return Withdraw(reach.sourceId); }

            if (!string.IsNullOrEmpty(reach.sourceId))
            {
                for (int i = 0; i < reaches.Count; i++)
                {
                    if (reaches[i] != null && reaches[i].sourceId == reach.sourceId)
                    {
                        // Restating an unchanged river must not churn the version, or an idle multi-settle build would
                        // report the river as moving 25 times and a reader would rebuild banks that never shifted.
                        if (AreEquivalent(reaches[i], reach)) { return false; }
                        reaches[i] = reach;
                        version++;
                        MarkChanged();
                        return true;
                    }
                }
            }

            reaches.Add(reach);
            version++;
            MarkChanged();
            return true;
        }

        /// <summary> Withdraw the reach under this id, if any, and say whether one was removed. An empty or unknown id
        /// removes nothing. Main thread only. </summary>
        public bool Withdraw(string sourceId)
        {
            if (reaches == null || string.IsNullOrEmpty(sourceId)) { return false; }

            bool removed = false;
            for (int i = reaches.Count - 1; i >= 0; i--)
            {
                if (reaches[i] != null && reaches[i].sourceId == sourceId)
                {
                    reaches.RemoveAt(i);
                    removed = true;
                }
            }

            if (removed) { version++; MarkChanged(); }
            return removed;
        }

        /// <summary> Forget every reach. Called when a world is rebuilt from scratch or its last river is removed, so a
        /// reader is not left banking rivers that are gone. Main thread only. </summary>
        public void Clear()
        {
            if (reaches == null || reaches.Count == 0) { return; }
            reaches.Clear();
            version++;
            MarkChanged();
        }

        /// <summary> How many stations are published across every reach -- for the inspector's one-line summary and the
        /// self test, neither of which should have to walk the reaches itself. </summary>
        public int CountStations()
        {
            if (reaches == null) { return 0; }
            int n = 0;
            for (int i = 0; i < reaches.Count; i++)
            {
                if (reaches[i] != null && reaches[i].stations != null) { n += reaches[i].stations.Length; }
            }
            return n;
        }

        /// <summary> Find the publication on or above the given object, or below it, without creating one. Returns null
        /// when the world has none -- which a reader takes as "UC has published no rivers here". Searches the same places
        /// <see cref="FindOrCreateFor"/> would put one, so the writer and the reader always mean the same publication.
        /// </summary>
        public static RiverProfilePublication FindFor(GameObject anchor)
        {
            if (anchor == null) { return null; }

            RiverProfilePublication pub = anchor.GetComponentInParent<RiverProfilePublication>();
            if (pub == null) { pub = anchor.GetComponentInChildren<RiverProfilePublication>(true); }
            return pub;
        }

        /// <summary> Find the publication on or above the given object, or add one there if none exists. UC calls this
        /// from the settle pass with the world object, and only when it has a river to publish -- so a world with no
        /// rivers never gains the component from being baked. Main thread only; it may add a component. Never returns
        /// null for a live object. </summary>
        public static RiverProfilePublication FindOrCreateFor(GameObject anchor)
        {
            if (anchor == null) { return null; }

            RiverProfilePublication pub = FindFor(anchor);
            if (pub == null) { pub = anchor.AddComponent<RiverProfilePublication>(); }
            return pub;
        }

        /// <summary> True when two reaches describe the same channel to within a centimetre at every station -- the same
        /// tolerance the tunnel reconcile treats as "not moved". Different station counts are never equivalent. Used to
        /// keep <see cref="Deposit"/> from bumping the version for a river that was re-solved but did not actually shift.
        /// </summary>
        private static bool AreEquivalent(RiverReachProfile a, RiverReachProfile b)
        {
            if (a == null || b == null) { return false; }

            RiverProfileStation[] sa = a.stations;
            RiverProfileStation[] sb = b.stations;
            if (sa == null || sb == null || sa.Length != sb.Length) { return false; }

            const float eps = 0.01f;   // 1 cm, on position, width and both levels
            for (int i = 0; i < sa.Length; i++)
            {
                if (Mathf.Abs(sa[i].worldX - sb[i].worldX) > eps) { return false; }
                if (Mathf.Abs(sa[i].worldZ - sb[i].worldZ) > eps) { return false; }
                if (Mathf.Abs(sa[i].widthMetres - sb[i].widthMetres) > eps) { return false; }
                if (Mathf.Abs(sa[i].surfaceLevelMetres - sb[i].surfaceLevelMetres) > eps) { return false; }
                if (Mathf.Abs(sa[i].bedLevelMetres - sb[i].bedLevelMetres) > eps) { return false; }
            }
            return true;
        }

        private void MarkChanged()
        {
            #if UNITY_EDITOR
            // In edit mode the reaches are only persisted if the scene is told they changed. Not wrapped in an Undo: the
            // publish happens during a build the author already expects to change the scene, and one undo step per
            // published river would bury the build itself. The same discipline the inbox follows.
            if (!Application.isPlaying) { UnityEditor.EditorUtility.SetDirty(this); }
            #endif
        }
    }
}
