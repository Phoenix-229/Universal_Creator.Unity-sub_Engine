// -----------------------------------------------------------------------------
//  Universe Creator - Geometry, density and streaming
//
//  File:    GeometryDiagnostics.cs
//  Purpose: The one channel through which the running geometry systems hand the
//           debug window a snapshot of their live numbers - and NOTHING computes
//           here. Every quantity in § 9.12's budget table is derived from a law
//           somewhere in this section (the acuity budget, the micro-relief activity
//           rates, the splat page residency, the impostor capacity, the rolling
//           budget median and the terrain write counter), so every quantity has to
//           be inspectable, or a support question about "why is it running at Mobile
//           Low" has no answer. This file is the mailbox those answers arrive in.
//
//  A MAILBOX, NOT A MODEL
//  ----------------------
//  The scene-layer components that already own these systems drop one immutable
//  snapshot here per frame WHILE THE GAME IS PLAYING; the editor window reads the
//  latest one and lays it out. The channel holds numbers, never the systems - it
//  runs no per-frame world math, so it is safe to leave present in edit mode where
//  it simply reports "no live data" until play begins. That keeps the edit-mode
//  guarantee (no world simulation outside play) while still letting the window show
//  a real, on-device reading the moment the scene starts publishing.
//
//  IMMUTABLE SNAPSHOT, SINGLE WRITER
//  ---------------------------------
//  The snapshot is a plain data holder the runtime fills once and never mutates
//  after publishing, so the window can read it on the editor thread without tearing.
//  The presentation maths that turns it into labelled rows and bars lives in the
//  device-free GeometryDebugView; the window is only the drawer. This split is why
//  the readout can be pinned by a self-test with no scene, no camera and no GPU.
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// An immutable, per-frame reading of the geometry systems' live numbers - the six panels the debug
    /// window draws. A plain data holder: the runtime fills one and calls <see cref="GeometryDiagnostics.Publish"/>
    /// while playing; the window reads <see cref="GeometryDiagnostics.Latest"/>. Nothing here computes.
    /// </summary>
    public sealed class GeometryDiagnosticsSnapshot
    {
        // --- Header: the active tier and the § 9.12 row values that are stated per tier ---
        public string tierName = "";
        public int targetFrameRate;
        public float acuityPixels;
        public string overlayRing = "";
        public string overlayTessellation = "";
        public int terrainLayersAchievable;

        // --- Panel 1: the derived-distance model, resolved against the live/reference view ---
        public float treeDrawDistanceM;
        public float detailDrawDistanceM;
        public float basemapDistanceM;
        public float referenceFovDeg;
        public int referenceScreenHeightPixels;

        // --- Panel 2: micro-relief process activity and the solo isolation ---
        public int processesEvaluated;         // the tier gate: 4 / 8 / 13 / 13
        public string soloProcessId = "";      // "" = no process soloed
        public string[] processIds = System.Array.Empty<string>();
        public float[] processActivityRatesMmPerYr = System.Array.Empty<float>();

        // --- Panel 3: splat cache page residency and eviction rate ---
        public bool splatCacheEnabled;
        public int residentPages;
        public int pageCapacity;
        public float splatEvictionsPerMinute;

        // --- Panel 4: impostor atlas residency and the re-bake queue ---
        public int residentSpecies;
        public int speciesCapacity;
        public long atlasCeilingBytes;
        public long atlasResidentBytes;
        public string impostorTileViews = "";
        public int rebakeQueueLength;
        public float impostorEvictionsPerMinute;

        // --- Panel 5: rolling budget medians, their ceilings, and the thermal history graph ---
        public float medianFrameMs;
        public float frameMsCeiling;
        public float medianTriangles;
        public float triangleCeiling;
        public float medianDrawCalls;
        public float drawCallCeiling;
        public float medianMemoryMB;
        public float memoryCeilingMB;
        public bool overBudget;
        public string governorStatusLine = "";
        public float[] frameMsHistory = System.Array.Empty<float>();

        // --- Panel 6: the terrain heightmap-pixel-error write counter (the still-camera guarantee) ---
        public int terrainWritesThisFrame;
        public int terrainWritesTotal;
        public int trackedCells;

        // --- Staleness: so the window can grey out a snapshot the running scene stopped refreshing ---
        public int frameCount;
        public float realtimeStamp;
    }

    /// <summary>
    /// The single, well-known place the running geometry systems publish their latest reading and the debug
    /// window reads it. Written by the scene layer during play, read by the editor window; not a simulation and
    /// not per-frame world math, so it is inert and simply empty in edit mode. Single-writer by convention: the
    /// runtime replaces the whole snapshot each frame rather than mutating the published one, so a reader on
    /// another thread sees a consistent object or the previous one, never a half-written mix.
    /// </summary>
    public static class GeometryDiagnostics
    {
        /// <summary>The most recently published reading, or null when the running scene has published none.</summary>
        public static GeometryDiagnosticsSnapshot Latest { get; private set; }

        /// <summary>True once a live snapshot has been published this session - the window's "is there data" gate.</summary>
        public static bool HasData => Latest != null;

        /// <summary>Publish the frame's reading. Called by the geometry scene component while playing; a null is
        /// ignored so a caller cannot blank the channel by accident.</summary>
        public static void Publish(GeometryDiagnosticsSnapshot snapshot)
        {
            if (snapshot != null) Latest = snapshot;
        }

        /// <summary>Drop the published reading, so the window falls back to its "no live data" state. Called when the
        /// scene stops (play exit) so a stale reading is never mistaken for a live one.</summary>
        public static void Clear()
        {
            Latest = null;
        }
    }
}
