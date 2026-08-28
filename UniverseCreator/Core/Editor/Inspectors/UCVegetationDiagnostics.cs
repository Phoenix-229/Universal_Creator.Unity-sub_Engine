// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UCVegetationDiagnostics.cs
//  Purpose: The post-build diagnostics panel for the vegetation inspector (V-52).
//
//  WHAT IT SHOWS AND WHY IT IS NOT THE VALIDATION PANEL
//  ----------------------------------------------------
//  Validation (V-51) answers "what is wired wrong". This answers "what did the
//  last build actually place". They are different questions with different
//  audiences - one is read before a build to stop a silent empty world, the other
//  after it to confirm the world that grew is the world that was meant. Folding
//  them together would put a defect list and a census in one heading and serve
//  neither, so they are two panels that happen to sit in one inspector.
//
//  TWO KINDS OF NUMBER, LABELLED APART ON PURPOSE
//  ----------------------------------------------
//    - COMMITTED counts (trees, shrubs, objects, detail, colliders, draws) come
//      from the instances the applier actually placed. They are what draws and
//      what costs frame time, so they are the live totals the registry keeps as
//      chunks stream in and out.
//
//    - ADMITTED placements per landform zone come from species assignment, which
//      decides them BEFORE the per-chunk render budget thins a crowded chunk. So
//      the zone census reads at or above the committed counts wherever a chunk hit
//      its ceiling. Saying so is the whole point of splitting them: an author who
//      sees 40 000 admitted and 30 000 drawn is looking at a budget doing its job,
//      not a bug.
//
//  Grass is deliberately absent from the zone census. It is a rasterised density
//  grid, never a per-site species decision, so it has no single zone to be counted
//  in - and pretending otherwise would be the dishonest number this panel exists
//  to avoid.
//
//  NO DIALOGS. Every figure is a row.
// -----------------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>
    /// Draws the vegetation registry's live census. Static and stateless: it reads
    /// the manager every repaint, so the numbers move as a world rebuilds rather
    /// than after a re-select.
    /// </summary>
    public static class UCVegetationDiagnostics
    {
        private const string FoldKey = "UniverseCreator.Inspector.Vegetation.Diagnostics";

        // Reused across repaints so reading the histogram allocates nothing: the
        // registry copies into this buffer rather than handing back a fresh array.
        // The registry's RegistryVersion is there for a caller that wants to cache;
        // this panel reads live every repaint instead, because a 17-int copy is
        // cheaper than the staleness a cache would risk across selected objects.
        private static readonly int[] ZoneBuffer = new int[LandformAssemblage.ZoneCount];

        /// <summary>
        /// The whole panel: a foldout carrying the resident-chunk count, then the
        /// committed census and the per-zone admitted census, or a single line when
        /// nothing has built.
        /// </summary>
        public static void Draw(UCVegetationSystemManager v)
        {
            if (v == null) return;

            int chunks = v.RegisteredChunkCount;

            EditorGUILayout.Space(4);

            // Closed by default: it is a post-build reference, not something an
            // author edits against, so it should not push the fields down until
            // asked for.
            bool open = EditorPrefs.GetBool(FoldKey, false);
            string heading = chunks <= 0
                ? "Diagnostics   (not built)"
                : "Diagnostics   (" + chunks + (chunks == 1 ? " chunk)" : " chunks)");

            Color accent = chunks <= 0 ? UCInspectorUI.HeaderGrey : UCInspectorUI.HeaderGreen;

            bool next = UCInspectorUI.Section(open, heading, accent);
            if (next != open) EditorPrefs.SetBool(FoldKey, next);
            if (!next) return;

            if (chunks <= 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing has registered yet. These are live counts from the build, " +
                    "so build the world once to populate them.",
                    MessageType.Info);
                return;
            }

            DrawCommittedCounts(v);
            DrawZoneCensus(v);
        }

        // =====================================================================
        //  COMMITTED - what the applier actually placed
        // =====================================================================

        private static void DrawCommittedCounts(UCVegetationSystemManager v)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Placed instances", EditorStyles.boldLabel);

            UCGroupedInspector.Readout("Trees", v.RegisteredTreeCount.ToString("N0"));
            UCGroupedInspector.Readout("Shrubs", v.RegisteredShrubCount.ToString("N0"));
            UCGroupedInspector.Readout("Objects", v.RegisteredObjectCount.ToString("N0"));
            UCGroupedInspector.Readout("Detail", v.RegisteredDetailCount.ToString("N0"));

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Scene cost", EditorStyles.boldLabel);

            UCGroupedInspector.Readout("Colliders", v.RegisteredColliderCount.ToString("N0"));
            UCGroupedInspector.Readout("GameObjects", v.RegisteredGameObjectCount.ToString("N0"));
            UCGroupedInspector.Readout("Instanced draws",
                v.RegisteredInstancedDrawCount.ToString("N0"));

            // Live is what draws; parked is memory held for a cell outside the
            // streaming window. The gap between them is the useful reading in play
            // mode - see the registry header.
            int live = v.LiveChunkCount;
            int parked = v.SuspendedChunkCount;
            UCGroupedInspector.Readout("Chunks",
                parked > 0
                    ? live + " live, " + parked + " parked"
                    : live + " live");

            float ms = v.LastBuildMilliseconds;
            UCGroupedInspector.Readout("Last chunk apply",
                ms > 0f ? ms.ToString("0.0") + " ms" : "not measured");
        }

        // =====================================================================
        //  ADMITTED - where the ecology model planted, before the budget
        // =====================================================================

        private static void DrawZoneCensus(UCVegetationSystemManager v)
        {
            int n = v.CopyZoneHistogram(ZoneBuffer);

            long total = 0;
            for (int i = 0; i < n; i++) total += ZoneBuffer[i];

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Ecology placements by zone", EditorStyles.boldLabel);

            if (total <= 0)
            {
                // Three honest reasons, because the author cannot tell them apart
                // from an empty list alone.
                EditorGUILayout.HelpBox(
                    "No ecology placements were recorded. Either ecology is off - the " +
                    "shipped density rules placed the instances above, which carry no " +
                    "zone - or no species cleared suitability, or only grass scattered, " +
                    "and grass is a density grid with no per-site zone to count.",
                    MessageType.Info);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                int c = ZoneBuffer[i];
                if (c <= 0) continue;
                UCGroupedInspector.Readout(LandformAssemblage.Name((LandformZone)i),
                                           c.ToString("N0"));
            }

            UCGroupedInspector.Readout("Admitted total", total.ToString("N0"));

            EditorGUILayout.HelpBox(
                "Where species assignment admitted planting - trees, shrubs, objects and " +
                "underwater - before the per-chunk render budget thinned it. These are " +
                "admitted counts, so they sit at or above the placed instances above " +
                "wherever a crowded chunk hit its ceiling. Grass is not counted here: it " +
                "is a density grid, not a per-site species choice.",
                MessageType.Info);
        }
    }
}
