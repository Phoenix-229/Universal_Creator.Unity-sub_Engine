// -----------------------------------------------------------------------------
//  Universe Creator - Geometry, density and streaming
//
//  File:    GeometryDebugView.cs
//  Purpose: The pure presentation model behind the geometry debug window - it turns
//           one GeometryDiagnosticsSnapshot into the labelled rows and normalised bars
//           the six panels draw, and it computes NOTHING about the world. Every number
//           it shows already came from a law in this section; this file only formats
//           them and states, per row, how full its bar is and whether it has crossed a
//           ceiling. Keeping that maths here - out of the EditorWindow - is what lets a
//           self-test pin the readout with no scene, no camera and no GPU: the window
//           becomes a thin layout over rows this class already decided.
//
//  A BAR IS A RATIO, AN ALERT IS A CROSSED CEILING
//  -----------------------------------------------
//  A row's bar is its value over the ceiling that governs it, clamped to [0,1], so a
//  budget panel reads as "how close to the limit" at a glance; a row with no natural
//  ceiling carries a negative bar meaning "no bar, value only". A row's alert is set
//  exactly when the thing it measures has gone wrong - a median over its budget ceiling,
//  an atlas evicting fast enough to be thrashing - so the window can colour the one row
//  that explains a degradation rather than leaving the author to read every number.
//
//  DEVICE-FREE AND CULTURE-STABLE
//  ------------------------------
//  Static, allocation-per-call only, no UnityEngine object beyond Mathf; decimals are
//  formatted with the invariant culture so the readout - and the self-test that pins it -
//  reads the same on every machine's locale. A null snapshot yields a single "no data"
//  row per panel rather than throwing, so the window is safe to draw before play begins.
// -----------------------------------------------------------------------------

using System.Globalization;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>One line in a debug panel: a label, its formatted value, an optional [0,1] bar
    /// (negative = draw no bar) and an alert flag the window colours when a ceiling is crossed.</summary>
    public struct GeometryPanelRow
    {
        public string label;
        public string value;
        public float bar01;   // [0,1] fill, or negative for "value only, no bar"
        public bool alert;    // true = this row is over its ceiling / thrashing

        public GeometryPanelRow(string label, string value, float bar01 = -1f, bool alert = false)
        {
            this.label = label;
            this.value = value;
            this.bar01 = bar01;
            this.alert = alert;
        }
    }

    /// <summary>
    /// Pure formatter of a <see cref="GeometryDiagnosticsSnapshot"/> into the six debug panels. Every method
    /// returns the rows one panel draws; a null snapshot returns a single "no live data" row so the window is
    /// always safe to draw. Device-free and culture-stable, so the self-test pins the exact rows.
    /// </summary>
    public static class GeometryDebugView
    {
        /// <summary>Eviction rate (per minute) at or above which an atlas/cache is called thrashing and its row alerts.</summary>
        public const float ThrashEvictionsPerMinute = 30f;

        /// <summary>The six panel titles, in draw order.</summary>
        public static readonly string[] PanelTitles =
        {
            "Derived distances", "Micro-relief activity", "Splat cache", "Impostor atlas",
            "Budget & thermal", "Terrain writes"
        };

        // --- small, culture-stable helpers ------------------------------------------------

        private static string F(float v, int decimals)
        {
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static int I(float v) { return Mathf.RoundToInt(v); }

        /// <summary>A value's fill against a ceiling, clamped to [0,1]. A non-positive ceiling means "no bar" (−1).</summary>
        private static float Ratio01(float value, float ceiling)
        {
            return ceiling <= 0f ? -1f : Mathf.Clamp01(value / ceiling);
        }

        /// <summary>Bytes as B / KB / MB, invariant-culture, so an atlas budget reads the way the § 9.6 table states it.</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024L) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024.0) return kb.ToString("F1", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
        }

        private static GeometryPanelRow[] NoData()
        {
            return new[] { new GeometryPanelRow("—", "no live data (enter play mode)") };
        }

        /// <summary>Panel 1 - the derived-distance model, resolved against the reference view, with the acuity dial
        /// that drives it. These are metres Unity applies to the live camera, so they are values, not bars.</summary>
        public static GeometryPanelRow[] DistanceModel(GeometryDiagnosticsSnapshot s)
        {
            if (s == null) return NoData();
            return new[]
            {
                new GeometryPanelRow("Acuity budget", F(s.acuityPixels, 2) + " px"),
                new GeometryPanelRow("Reference view", s.referenceScreenHeightPixels + " px @ " + F(s.referenceFovDeg, 0) + "°"),
                new GeometryPanelRow("Tree draw", I(s.treeDrawDistanceM) + " m"),
                new GeometryPanelRow("Detail draw", I(s.detailDrawDistanceM) + " m"),
                new GeometryPanelRow("Basemap", I(s.basemapDistanceM) + " m"),
            };
        }

        /// <summary>Panel 2 - one bar per micro-relief process, its length the activity rate against the busiest
        /// process this frame; the soloed process (if any) is flagged in its label. Rows past the tier's evaluated
        /// count are dimmed by the window, but all are listed so the author sees what the tier is withholding.</summary>
        public static GeometryPanelRow[] MicroRelief(GeometryDiagnosticsSnapshot s)
        {
            if (s == null || s.processIds == null || s.processIds.Length == 0) return NoData();
            int n = s.processIds.Length;
            float max = 0f;
            for (int i = 0; i < n; i++)
            {
                float r = s.processActivityRatesMmPerYr != null && i < s.processActivityRatesMmPerYr.Length
                    ? s.processActivityRatesMmPerYr[i] : 0f;
                if (r > max) max = r;
            }
            var rows = new GeometryPanelRow[n];
            for (int i = 0; i < n; i++)
            {
                float r = s.processActivityRatesMmPerYr != null && i < s.processActivityRatesMmPerYr.Length
                    ? s.processActivityRatesMmPerYr[i] : 0f;
                bool solo = !string.IsNullOrEmpty(s.soloProcessId) && s.soloProcessId == s.processIds[i];
                string label = solo ? "● " + s.processIds[i] : s.processIds[i];
                rows[i] = new GeometryPanelRow(label, F(r, 3) + " mm/yr", max > 0f ? Mathf.Clamp01(r / max) : 0f);
            }
            return rows;
        }

        /// <summary>Panel 3 - splat cache page residency against capacity, and the eviction rate that betrays a
        /// working set that does not fit. The residency bar alerts when it exceeds capacity; the rate row alerts on
        /// sustained thrash. When the tier runs with the cache off, one row says so.</summary>
        public static GeometryPanelRow[] SplatCache(GeometryDiagnosticsSnapshot s)
        {
            if (s == null) return NoData();
            if (!s.splatCacheEnabled)
                return new[] { new GeometryPanelRow("Cache", "off for this tier") };
            bool full = s.pageCapacity > 0 && s.residentPages > s.pageCapacity;
            bool thrash = s.splatEvictionsPerMinute >= ThrashEvictionsPerMinute;
            return new[]
            {
                new GeometryPanelRow("Resident pages", s.residentPages + " / " + s.pageCapacity,
                                     Ratio01(s.residentPages, s.pageCapacity), full),
                new GeometryPanelRow("Eviction rate", F(s.splatEvictionsPerMinute, 1) + " /min", -1f, thrash),
            };
        }

        /// <summary>Panel 4 - impostor atlas residency against capacity and memory ceiling, the re-bake queue depth,
        /// and the eviction rate. Residency alerts when the working set exceeds capacity; the rate row alerts on
        /// thrash - the two together are how an author sees a fifty-species library not fitting a six-slot pool.</summary>
        public static GeometryPanelRow[] ImpostorAtlas(GeometryDiagnosticsSnapshot s)
        {
            if (s == null) return NoData();
            bool full = s.speciesCapacity > 0 && s.residentSpecies > s.speciesCapacity;
            bool thrash = s.impostorEvictionsPerMinute >= ThrashEvictionsPerMinute;
            return new[]
            {
                new GeometryPanelRow("Resident species", s.residentSpecies + " / " + s.speciesCapacity,
                                     Ratio01(s.residentSpecies, s.speciesCapacity), full),
                new GeometryPanelRow("Atlas memory", FormatBytes(s.atlasResidentBytes) + " / " + FormatBytes(s.atlasCeilingBytes),
                                     s.atlasCeilingBytes > 0L ? Mathf.Clamp01((float)s.atlasResidentBytes / s.atlasCeilingBytes) : -1f,
                                     s.atlasResidentBytes > s.atlasCeilingBytes),
                new GeometryPanelRow("Tile / views", s.impostorTileViews),
                new GeometryPanelRow("Re-bake queue", s.rebakeQueueLength.ToString()),
                new GeometryPanelRow("Eviction rate", F(s.impostorEvictionsPerMinute, 1) + " /min", -1f, thrash),
            };
        }

        /// <summary>Panel 5 - the rolling-median cost against each tier ceiling (the median, never the mean, so a
        /// single spike moves nothing), plus the governor's advisory status line. Each metric's bar is its median
        /// over its ceiling and alerts when over; the frame-time history graph is drawn from the snapshot's ring.</summary>
        public static GeometryPanelRow[] Budget(GeometryDiagnosticsSnapshot s)
        {
            if (s == null) return NoData();
            return new[]
            {
                new GeometryPanelRow("Frame time", F(s.medianFrameMs, 1) + " / " + F(s.frameMsCeiling, 1) + " ms",
                                     Ratio01(s.medianFrameMs, s.frameMsCeiling), s.medianFrameMs > s.frameMsCeiling),
                new GeometryPanelRow("Triangles", I(s.medianTriangles) + " / " + I(s.triangleCeiling),
                                     Ratio01(s.medianTriangles, s.triangleCeiling), s.medianTriangles > s.triangleCeiling),
                new GeometryPanelRow("Draw calls", I(s.medianDrawCalls) + " / " + I(s.drawCallCeiling),
                                     Ratio01(s.medianDrawCalls, s.drawCallCeiling), s.medianDrawCalls > s.drawCallCeiling),
                new GeometryPanelRow("Memory", F(s.medianMemoryMB, 1) + " / " + F(s.memoryCeilingMB, 1) + " MB",
                                     Ratio01(s.medianMemoryMB, s.memoryCeilingMB), s.medianMemoryMB > s.memoryCeilingMB),
                new GeometryPanelRow("Governor", string.IsNullOrEmpty(s.governorStatusLine) ? "—" : s.governorStatusLine,
                                     -1f, s.overBudget),
            };
        }

        /// <summary>Panel 6 - the terrain heightmap-pixel-error write counter, the proof of the still-camera
        /// guarantee: with the camera at rest the per-frame writes settle to zero, and the total stops climbing.</summary>
        public static GeometryPanelRow[] TerrainWrites(GeometryDiagnosticsSnapshot s)
        {
            if (s == null) return NoData();
            return new[]
            {
                new GeometryPanelRow("Writes this frame", s.terrainWritesThisFrame.ToString()),
                new GeometryPanelRow("Writes total", s.terrainWritesTotal.ToString()),
                new GeometryPanelRow("Tracked cells", s.trackedCells.ToString()),
            };
        }
    }
}
