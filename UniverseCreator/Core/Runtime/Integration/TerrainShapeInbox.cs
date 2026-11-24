// -----------------------------------------------------------------------------
//  Universe Creator - Path Tool Integration (Module 6 seam)
//  File:    TerrainShapeInbox.cs
//  Purpose: The serialised place a path tool leaves its TerrainShapeRequests and
//           UC's own update pass picks them up. One per world, saved with the
//           scene, so a request survives the domain reload between the tool
//           building a road and the author pressing Update.
//
//  WHY A COMPONENT AND NOT A STATIC REGISTRY
//  -----------------------------------------
//  CutRegionRegistry and WaterCarveRegistry are static because what they hold is
//  rebuilt from live components every session and never needs to outlive one. A
//  shaping request is different: the tool that made it may not be in the scene
//  when the author finally regenerates, and a static list is empty after a domain
//  reload. Serialising the requests on a component saved with the scene is what
//  lets "build the road now, raise the water later" work at all. It is the data
//  asset U-5 calls for, expressed as the thing Unity actually serialises.
//
//  WHO WRITES AND WHO READS
//  ------------------------
//  The tool WRITES, through Deposit/Withdraw, at the end of a build, on the main
//  thread. UC READS, through Requests, once per regeneration, and applies the set
//  to ground it re-derives from pristine terrain -- so the inbox is a statement of
//  what should be true, not a log of edits, and applying it twice changes nothing.
//  The tool never calls UC here and UC never calls the tool: the inbox is the only
//  thing between them, and it is data.
//
//  REVERSIBILITY LIVES IN THE ID
//  -----------------------------
//  Every request carries a sourceId. Depositing the same id again replaces the
//  older request (the road moved; restate it), and Withdraw removes it (the road
//  was deleted). Because UC re-derives ground from pristine terrain each rebuild,
//  a withdrawn request leaves no trace after the next regeneration.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Integration
{
    /// <summary> The per-world store of <see cref="TerrainShapeRequest"/>s a path tool has asked UC to honour. Add it to
    /// the world object (the one that owns the provider); a tool finds or creates it with <see cref="FindOrCreateFor"/>
    /// and UC's regeneration reads <see cref="Requests"/>. </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Integration/Terrain Shape Inbox")]
    public class TerrainShapeInbox : MonoBehaviour
    {
        [Tooltip("The shaping requests an external path tool has asked this world to honour. Written by the tool, read "
               + "by regeneration. Restating an id replaces its entry; withdrawing one lets the ground return on the "
               + "next rebuild.")]
        [SerializeField]
        private List<TerrainShapeRequest> requests = new List<TerrainShapeRequest>();

        /// <summary> Bumped whenever the set of requests changes, so a regeneration or an inspector can tell that a
        /// rebuild is owed by comparing a number rather than the lists themselves -- the discipline WaterCarveRegistry
        /// follows for the same reason. Not serialised: a freshly loaded scene owes a rebuild anyway. </summary>
        [System.NonSerialized]
        private int version;

        public int Version { get { return version; } }

        public int Count { get { return requests != null ? requests.Count : 0; } }

        /// <summary> Read access to the current requests. The list itself is returned, not a copy, because the one
        /// reader (regeneration, main thread) only enumerates it; do not hold the reference across a Deposit. </summary>
        public IReadOnlyList<TerrainShapeRequest> Requests
        {
            get { return requests ?? (requests = new List<TerrainShapeRequest>()); }
        }

        /// <summary> Leave a request, or restate one already left under the same id. Main thread only.
        ///
        /// A request with no work (an empty corridor, a zero-size opening) is treated as a withdrawal of its id: the
        /// tool saying "there is nothing here any more" and "forget this" are the same instruction to the world. </summary>
        public void Deposit(TerrainShapeRequest request)
        {
            if (request == null) { return; }
            if (requests == null) { requests = new List<TerrainShapeRequest>(); }

            if (!request.HasWork)
            {
                Withdraw(request.sourceId);
                return;
            }

            if (!string.IsNullOrEmpty(request.sourceId))
            {
                for (int i = 0; i < requests.Count; i++)
                {
                    if (requests[i] != null && requests[i].sourceId == request.sourceId)
                    {
                        requests[i] = request;
                        version++;
                        MarkChanged();
                        return;
                    }
                }
            }

            requests.Add(request);
            version++;
            MarkChanged();
        }

        /// <summary> Forget the request under this id, if any, and say whether one was removed. An empty or unknown id
        /// removes nothing. Main thread only. </summary>
        public bool Withdraw(string sourceId)
        {
            if (requests == null || string.IsNullOrEmpty(sourceId)) { return false; }

            bool removed = false;
            for (int i = requests.Count - 1; i >= 0; i--)
            {
                if (requests[i] != null && requests[i].sourceId == sourceId)
                {
                    requests.RemoveAt(i);
                    removed = true;
                }
            }

            if (removed) { version++; MarkChanged(); }
            return removed;
        }

        /// <summary> Forget every request. Called when a world is rebuilt from scratch or a tool disconnects and takes
        /// its shapings with it. Main thread only. </summary>
        public void Clear()
        {
            if (requests == null || requests.Count == 0) { return; }
            requests.Clear();
            version++;
            MarkChanged();
        }

        /// <summary> How many requests of a given kind are held -- for the inspector's one-line summary and the self
        /// test, neither of which should have to walk the list itself. </summary>
        public int CountOfKind(TerrainShapeKind kind)
        {
            if (requests == null) { return 0; }
            int n = 0;
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i] != null && requests[i].kind == kind && requests[i].HasWork) { n++; }
            }
            return n;
        }

        /// <summary> Find the inbox on or above the given object, or below it, without creating one. Returns null when
        /// the world has none -- which UC's regeneration reads as "no external tool has asked for anything here", so a
        /// world with no path tool never gains a component from being baked. Searches the same places
        /// <see cref="FindOrCreateFor"/> would put one, so the reader and the writer always mean the same inbox.
        /// </summary>
        public static TerrainShapeInbox FindFor(GameObject anchor)
        {
            if (anchor == null) { return null; }

            TerrainShapeInbox inbox = anchor.GetComponentInParent<TerrainShapeInbox>();
            if (inbox == null) { inbox = anchor.GetComponentInChildren<TerrainShapeInbox>(true); }
            return inbox;
        }

        /// <summary> Find the inbox on or above the given object, or add one there if none exists. A tool holds the
        /// provider (a Component) and calls this with the provider's GameObject; the socket lives on the same object,
        /// so the inbox joins it. Main thread only -- it may add a component. Never returns null for a live object.
        /// </summary>
        public static TerrainShapeInbox FindOrCreateFor(GameObject anchor)
        {
            if (anchor == null) { return null; }

            TerrainShapeInbox inbox = FindFor(anchor);
            if (inbox == null) { inbox = anchor.AddComponent<TerrainShapeInbox>(); }
            return inbox;
        }

        private void MarkChanged()
        {
            #if UNITY_EDITOR
            // In edit mode the requests are only persisted if the scene is told they changed. Not wrapped in an Undo:
            // the tool deposits during a build the author already expects to change the scene, and one undo step per
            // deposited road would bury the build itself.
            if (!Application.isPlaying) { UnityEditor.EditorUtility.SetDirty(this); }
            #endif
        }
    }
}
