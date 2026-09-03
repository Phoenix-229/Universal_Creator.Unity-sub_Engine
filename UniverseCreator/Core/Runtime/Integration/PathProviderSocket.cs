// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PathProviderSocket.cs
//  Purpose: The socket. One author-facing slot on the world side into which an
//           external path-authoring tool is entered, plus the state it displays
//           back: accepted or refused, the tool's own name, the provider surface
//           it speaks, and what it is currently holding.
//
//  WHY THE SOCKET LIVES HERE AND NOT IN THE OTHER TOOL
//  ---------------------------------------------------
//  The provider belongs to Universe Creator, and the object that owns a resource
//  is the right place to say who may use it. Universe Creator declares an
//  interface and a slot; whoever fits, fits. Nothing in this file names a
//  particular product: the name an author reads in the socket's status line is
//  read from the connected object at runtime, as data. A second path tool could
//  therefore occupy this socket with no change on this side.
//
//  ADDITIVE BY CONSTRUCTION
//  ------------------------
//  With the socket empty, or with no socket component in the scene at all, every
//  existing behaviour is exactly what it was: this file adds a component and an
//  interface and modifies nothing. Generation never consults the socket; the
//  socket only reports, and only the entered tool acts.
//
//  RELEASE IS PART OF THE CONTRACT
//  -------------------------------
//  Anything the entered tool asked the provider to hold open -- a levelled pad,
//  a notified cut region, a tunnel opening -- is owned by the provider until it
//  is given back. Clearing the socket therefore asks for it back BEFORE the
//  reference is dropped, because a reference dropped first is a hold nobody can
//  find.
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace UniverseCreator.Integration
{
    /// <summary>
    /// What an external path-authoring tool publishes back to the world side so
    /// the socket can report on it. Worded without a product name on purpose:
    /// the tool's own name is one of the values it supplies.
    /// </summary>
    public interface IPathToolLink
    {
        /// <summary>
        /// The connecting tool's own display name, for the socket's status line.
        /// Supplied as data so this side never hard-codes another product.
        /// </summary>
        string PathToolName { get; }

        /// <summary>
        /// How many members of <see cref="IPCGDataProvider"/> the connecting tool
        /// was written against. Compared with the surface actually published here,
        /// so a contract that grows a member is reported as drift instead of being
        /// silently half-used by a tool that predates it.
        /// </summary>
        int UnderstoodProviderMemberCount { get; }

        /// <summary>
        /// Whether the tool has verified a provider and is routing work through it.
        /// A tool can be entered here and still be unconnected on its own side --
        /// those are two different facts and both are worth showing.
        /// </summary>
        bool IsConnectedToProvider { get; }

        /// <summary>
        /// How many provider-owned regions the tool is holding right now: pads, cut
        /// regions and tunnel openings together. Zero after a clean release.
        /// </summary>
        int HeldRegionCount { get; }

        /// <summary>
        /// One sentence, authored by the tool, describing what it is doing. Shown
        /// verbatim: the tool knows why it is in the state it is in, and this side
        /// paraphrasing it would only produce a second, worse explanation.
        /// </summary>
        string StatusSentence { get; }

        /// <summary>
        /// Give every held region back and return to holding nothing. Returns how
        /// many were released, so the socket can report that the release path
        /// actually ran rather than assuming it.
        /// </summary>
        int ReleaseAllHeldRegions();
    }


    /// <summary> What the socket concluded about what was entered into it. </summary>
    public enum PathLinkState
    {
        /// <summary> Nothing entered. Universe Creator behaves exactly as it does with no socket at all. </summary>
        Empty = 0,

        /// <summary> Entered, accepted, and reporting. </summary>
        Accepted = 1,

        /// <summary> Something was entered that does not implement the link contract. </summary>
        NotALink = 2,

        /// <summary> A link was entered and what it named is gone from the scene. </summary>
        Vanished = 3,

        /// <summary> A link that speaks a different provider surface than this version publishes. </summary>
        SurfaceMismatch = 4
    }


    /// <summary>
    /// The socket's answer, in one shape: the state, the connecting tool's own
    /// name, the two surface sizes, what it holds, and one sentence an author can
    /// act on. The inspector reads this rather than re-deciding anything.
    /// </summary>
    public struct PathLinkStatus
    {
        public PathLinkState state;
        public string toolName;
        public int publishedMembers;
        public int understoodMembers;
        public bool toolReportsConnected;
        public int heldRegions;
        public string sentence;

        /// <summary> True whenever something was entered at all, however badly it turned out. </summary>
        public bool SocketFilled { get { return state != PathLinkState.Empty; } }

        /// <summary> True only when the socket accepted the link AND the link says it resolved a provider. </summary>
        public bool Working { get { return state == PathLinkState.Accepted && toolReportsConnected; } }
    }


    /// <summary>
    /// The slot itself. Add it to the object that owns the provider, enter an
    /// external path tool's link component, and the socket reports what that tool
    /// is doing. It never calls the tool to do work -- the direction of control is
    /// the other way round -- it only reads, reports, and releases on clear.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Integration/Path Provider Socket")]
    public class PathProviderSocket : MonoBehaviour
    {
        /// <summary>
        /// The external tool's link component, entered by hand. Typed as a
        /// MonoBehaviour because Unity cannot serialise an interface field; what it
        /// must implement is <see cref="IPathToolLink"/>, and entering something
        /// that does not is reported rather than ignored.
        /// </summary>
        [Tooltip("The path-authoring tool's link component. Entered by hand: this world never searches the scene for "
               + "one, because a scene with two candidates has no correct guess.")]
        public MonoBehaviour enteredPathTool;

        /// <summary>
        /// The tool name from the last time the socket accepted a link, saved WITH
        /// the scene. Without it, a link that has since been deleted is
        /// indistinguishable from a socket nobody ever filled in, and those two
        /// need different sentences.
        /// </summary>
        [HideInInspector]
        public string rememberedToolName = string.Empty;

        /// <summary>
        /// The provider surface this version publishes, counted off the interface
        /// itself rather than typed in, so it cannot drift from the real thing.
        /// </summary>
        public static int PublishedProviderMemberCount
        {
            get
            {
                MethodInfo[] methods = typeof(IPCGDataProvider).GetMethods();
                return methods != null ? methods.Length : 0;
            }
        }

        /// <summary> The entered link, or null when the socket is empty or holds something that is not a link. </summary>
        public IPathToolLink Link { get { return enteredPathTool as IPathToolLink; } }

        /// <summary>
        /// Read the socket and say what it holds. Pure: it decides nothing and
        /// changes nothing, so the inspector may call it every repaint and a build
        /// may call it never.
        /// </summary>
        public PathLinkStatus Status()
        {
            PathLinkStatus s = new PathLinkStatus();
            s.publishedMembers = PublishedProviderMemberCount;
            s.toolName = string.Empty;

            // Unity's own ==, which is false for a destroyed object as well as a null
            // one: a plain null test would call a deleted link alive.
            if (enteredPathTool == null)
            {
                if (string.IsNullOrEmpty(rememberedToolName))
                {
                    s.state = PathLinkState.Empty;
                    s.sentence = "No path tool is connected. Universe Creator owns the terrain and behaves exactly as "
                        + "it does on its own; enter an external path tool's link component here to let it request "
                        + "terrain changes instead of writing them itself.";
                    return s;
                }
                s.state = PathLinkState.Vanished;
                s.toolName = rememberedToolName;
                s.sentence = "'" + rememberedToolName + "' was connected here and is no longer in the scene. Anything "
                    + "it was holding open could not be released by it, so check for pads or openings left behind, "
                    + "then clear this socket or re-enter the link.";
                return s;
            }

            IPathToolLink link = Link;
            if (link == null)
            {
                s.state = PathLinkState.NotALink;
                s.toolName = enteredPathTool.name;
                s.sentence = "'" + enteredPathTool.name + "' was entered here but does not implement the path tool "
                    + "link contract, so it cannot report anything and nothing is routed to it. Enter the tool's link "
                    + "component.";
                return s;
            }

            s.toolName = string.IsNullOrEmpty(link.PathToolName) ? enteredPathTool.name : link.PathToolName;
            s.understoodMembers = link.UnderstoodProviderMemberCount;
            s.toolReportsConnected = link.IsConnectedToProvider;
            s.heldRegions = link.HeldRegionCount;

            if (s.understoodMembers != s.publishedMembers)
            {
                s.state = PathLinkState.SurfaceMismatch;
                s.sentence = "'" + s.toolName + "' was written against a provider surface of "
                    + s.understoodMembers.ToString(CultureInfo.InvariantCulture) + " members and this version "
                    + "publishes " + s.publishedMembers.ToString(CultureInfo.InvariantCulture) + ". Update whichever "
                    + "of the two is older; until they agree, treat anything the tool reports as unreliable.";
                return s;
            }

            s.state = PathLinkState.Accepted;
            s.sentence = "'" + s.toolName + "' is connected to this provider. It reports: " + link.StatusSentence;
            return s;
        }

        /// <summary>
        /// Bank an accepted link's name so a later disappearance can be named, and
        /// say whether that changed anything. Separate from <see cref="Status"/>
        /// because that one is pure and an inspector calls it every repaint; this
        /// one writes, so it is called at the moments the socket can actually
        /// change: a scene opening, and an author entering the link by hand.
        ///
        /// Enable alone is not enough, which is the whole reason this is a method
        /// with a name. Adding the component runs OnEnable immediately, while the
        /// slot is still empty; the author fills it a moment later. If banking only
        /// ever happened on enable, the name would not be recorded until the next
        /// domain reload, and a link entered and deleted inside one session would
        /// leave a socket that reads as one nobody ever filled in -- exactly the
        /// confusion <see cref="rememberedToolName"/> exists to prevent.
        /// </summary>
        public bool RememberAcceptedLink()
        {
            PathLinkStatus s = Status();
            if (s.state != PathLinkState.Accepted) { return false; }
            if (string.Equals(rememberedToolName, s.toolName, System.StringComparison.Ordinal)) { return false; }
            rememberedToolName = s.toolName;
            return true;
        }

        /// <summary>
        /// Remember an accepted link's name so a later disappearance can be named.
        /// Called on enable rather than continuously: the socket is a field an
        /// author edits, not a thing that changes on its own.
        /// </summary>
        protected virtual void OnEnable()
        {
            RememberAcceptedLink();
        }

        #if UNITY_EDITOR
        /// <summary>
        /// The other moment the socket changes: an author dropping the tool's link
        /// component into the slot. The engine calls this whenever a serialised
        /// field on this component is edited, which is the earliest point the name
        /// is knowable -- waiting for the next enable would leave the session it
        /// was entered in unable to name it if it went away again.
        /// </summary>
        protected virtual void OnValidate()
        {
            RememberAcceptedLink();
        }
        #endif

        /// <summary>
        /// Empty the socket. Asks the tool for everything back FIRST and returns how
        /// many regions it released, because the count is the proof that the release
        /// path ran; dropping the reference first would leave holds nobody owns.
        /// </summary>
        public int Clear()
        {
            int released = 0;
            IPathToolLink link = Link;
            if (link != null) { released = link.ReleaseAllHeldRegions(); }
            enteredPathTool = null;
            rememberedToolName = string.Empty;
            return released;
        }
    }
}
