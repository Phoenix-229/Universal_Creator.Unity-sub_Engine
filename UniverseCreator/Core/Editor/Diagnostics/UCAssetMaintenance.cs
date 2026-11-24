// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCAssetMaintenance.cs
//  Purpose: Brings importer .meta files up to the editor's current serialization
//           format, and never touches a guid.
//
//  WHY NOT JUST EDIT THE NUMBER
//  ----------------------------
//  serializedVersion on an importer is Unity's migration marker, not a label.
//  Raising it by hand while the body is still in the old layout makes the editor
//  skip the upgrade path and read old fields as new ones - silent, and worse than
//  leaving it alone. Only the editor can migrate correctly, so this asks it to and
//  then reports what it actually wrote.
//
//  WHY "CURRENT" IS NOT A CONSTANT HERE
//  ------------------------------------
//  Hardcoding "ModelImporter is 24501" would be a guess that rots at the next
//  editor upgrade. The target version is instead read from the project: whatever
//  the highest version this editor has written for that importer type is. Assets
//  below it are the ones asked to migrate.
//
//  A guid is never rewritten. Changing one silently breaks every prefab, material
//  and scene pointing at the asset, so every guid is captured before and compared
//  after, and a drift is a failure.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    /// <summary>
    /// Migrates stale importer metas under the package by asking the editor to
    /// reimport them, then verifies every guid survived. Editor-only.
    /// </summary>
    public static class UCAssetMaintenance
    {
        private const string PackageFolder = "UniverseCreator";

        // Stamped into userData only long enough to make the editor rewrite the
        // importer block, then removed. If this string is ever found left behind in
        // a meta, the restore pass did not complete and the run reports it.
        private const string MigrationMarker = "__uc_meta_rewrite__";

        private static readonly Regex ImporterLine =
            new Regex(@"^([A-Za-z]+Importer):\s*$", RegexOptions.Compiled);
        private static readonly Regex VersionLine =
            new Regex(@"^\s*serializedVersion:\s*(\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex GuidLine =
            new Regex(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Compiled);

        // One meta's identity as read off disk.
        private struct MetaInfo
        {
            public string MetaPath;
            public string AssetPath;   // project-relative, forward slashes
            public string Importer;
            public int Version;
            public string Guid;
        }

        [MenuItem("Tools/Universe Creator/Upgrade Importer Metas", false, 910)]
        public static void RunMenu()
        {
            bool ok = Upgrade(false, false, out string report);
            Debug.Log(report);
            if (!ok) Debug.LogError("Universe Creator: importer meta upgrade did not fully succeed.");
        }

        [MenuItem("Tools/Universe Creator/Upgrade Importer Metas (dry run)", false, 911)]
        public static void RunMenuDryRun()
        {
            Upgrade(true, false, out string report);
            Debug.Log(report);
        }

        [MenuItem("Tools/Universe Creator/Upgrade Importer Metas (forced rewrite)", false, 912)]
        public static void RunMenuForce()
        {
            bool ok = Upgrade(false, true, out string report);
            Debug.Log(report);
            if (!ok) Debug.LogError("Universe Creator: forced meta rewrite did not fully succeed.");
        }

        /// <summary>
        /// Headless dry run. Reports what would migrate and to which version, and
        /// writes nothing.
        ///
        /// Worth a boot of its own before the real pass: the target version is
        /// derived from the project rather than hardcoded, and a reimport of vendor
        /// art is not undoable from inside the editor. This is how that derived
        /// number gets read before it is acted on.
        /// </summary>
        public static void RunBatchDryRun()
        {
            Upgrade(true, false, out string report);

            Debug.Log(report);
            Console.WriteLine(report);
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Headless forced rewrite. Makes the editor reserialize each stale importer
        /// so the meta lands in the current layout, then restores every setting.
        /// Exits non-zero on a guid drift, a leftover marker, or a meta that still
        /// did not move.
        /// </summary>
        public static void RunBatchForce()
        {
            bool ok = Upgrade(false, true, out string report);

            Debug.Log(report);
            Console.WriteLine(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Headless entry point. Exits non-zero if any targeted meta failed to
        /// migrate or if any guid changed.
        /// </summary>
        public static void RunBatch()
        {
            bool ok = Upgrade(false, false, out string report);

            Debug.Log(report);

            // Console.WriteLine as well as Debug.Log: in a batch run the log file is
            // the only artefact, and this keeps the verdict greppable above the
            // import spam a reimport pass produces.
            Console.WriteLine(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Scans package metas, migrates the ones behind the highest version seen
        /// for their importer type, and reports. dryRun reports without writing.
        /// force makes a reversible change to the importer so the editor is obliged
        /// to rewrite the file - see the long note at the reimport loop.
        /// </summary>
        public static bool Upgrade(bool dryRun, bool force, out string report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== UC IMPORTER META UPGRADE"
                          + (dryRun ? " (DRY RUN)" : force ? " (FORCED REWRITE)" : "") + " ===");

            List<MetaInfo> all = Scan();
            sb.AppendLine("metas scanned  : " + all.Count);

            // Highest version per importer type, taken from this project rather than
            // a hardcoded table.
            Dictionary<string, int> target = new Dictionary<string, int>();
            for (int i = 0; i < all.Count; i++)
            {
                MetaInfo m = all[i];
                int best;
                if (!target.TryGetValue(m.Importer, out best) || m.Version > best)
                    target[m.Importer] = m.Version;
            }

            List<MetaInfo> stale = new List<MetaInfo>();
            for (int i = 0; i < all.Count; i++)
                if (all[i].Version < target[all[i].Importer]) stale.Add(all[i]);

            foreach (KeyValuePair<string, int> kv in target)
            {
                int behind = 0;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Importer == kv.Key && all[i].Version < kv.Value) behind++;
                sb.AppendLine("  " + kv.Key.PadRight(28) + " target=" + kv.Value + "  behind=" + behind);
            }

            sb.AppendLine("to migrate     : " + stale.Count);

            if (stale.Count == 0)
            {
                sb.AppendLine("=== RESULT: NOTHING TO DO ===");
                report = sb.ToString();
                return true;
            }

            if (dryRun)
            {
                for (int i = 0; i < stale.Count; i++)
                    sb.AppendLine("  would migrate v" + stale[i].Version + "  " + stale[i].AssetPath);
                sb.AppendLine("=== RESULT: DRY RUN, NOTHING WRITTEN ===");
                report = sb.ToString();
                return true;
            }

            if (force)
            {
                // WHY A PLAIN SaveAndReimport MIGRATES NOTHING
                //
                // Measured: reimporting all 48 stale metas reported migrated=0 and
                // left every file byte-unchanged. Unity only writes a .meta back out
                // when the importer's serialized data actually differs from what is
                // on disk. An importer whose old layout still reads correctly is not
                // "different", so there is nothing to write and the version number
                // stays where it is.
                //
                // So give it a real difference. userData is the one field present on
                // every importer that Unity itself never consults while importing -
                // it is free-form string storage for tools - so writing it cannot
                // move a vertex, a normal, a UV or a material assignment. Stamping
                // it forces the whole importer block to be reserialized, and the
                // editor writes that block in its CURRENT layout at its CURRENT
                // version. Restoring the value afterwards writes it a second time,
                // also current, leaving the settings exactly as authored.
                //
                // The two passes cannot share one StartAssetEditing block. Asset
                // editing batches the writes, so a stamp and a restore inside the
                // same block would net to no change and skip the rewrite - the very
                // no-op this is working around. Each pass therefore flushes.
                string[] originals = new string[stale.Count];

                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        AssetImporter imp = AssetImporter.GetAtPath(stale[i].AssetPath);
                        if (imp == null)
                        {
                            sb.AppendLine("  NO IMPORTER: " + stale[i].AssetPath);
                            continue;
                        }

                        try
                        {
                            originals[i] = imp.userData;
                            imp.userData = originals[i] + MigrationMarker;
                            imp.SaveAndReimport();
                        }
                        catch (Exception e)
                        {
                            sb.AppendLine("  THREW (stamp): " + stale[i].AssetPath + " :: " + e.Message);
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        AssetImporter imp = AssetImporter.GetAtPath(stale[i].AssetPath);
                        if (imp == null) continue;

                        try
                        {
                            // Back to exactly what was authored. A null original was
                            // an empty field, and Unity stores it as empty either way.
                            imp.userData = originals[i] == null ? string.Empty : originals[i];
                            imp.SaveAndReimport();
                        }
                        catch (Exception e)
                        {
                            sb.AppendLine("  THREW (restore): " + stale[i].AssetPath + " :: " + e.Message);
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }
            else
            {
                // SaveAndReimport rather than ImportAsset: it is the call that writes
                // the .meta back out, which is the whole point here. ImportAsset alone
                // can reimport without reserializing the importer. Kept because it is
                // the least invasive thing that could work, and it is what proves the
                // force path above is actually necessary.
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        AssetImporter imp = AssetImporter.GetAtPath(stale[i].AssetPath);
                        if (imp == null)
                        {
                            sb.AppendLine("  NO IMPORTER: " + stale[i].AssetPath);
                            continue;
                        }

                        try
                        {
                            imp.SaveAndReimport();
                        }
                        catch (Exception e)
                        {
                            // One bad asset must not abandon the rest of the pass.
                            sb.AppendLine("  THREW: " + stale[i].AssetPath + " :: " + e.Message);
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            // Re-read from disk and prove it: version moved, guid did not.
            int migrated = 0, unchanged = 0, drift = 0;
            for (int i = 0; i < stale.Count; i++)
            {
                MetaInfo before = stale[i];
                MetaInfo after;
                if (!ReadMeta(before.MetaPath, out after))
                {
                    sb.AppendLine("  UNREADABLE AFTER: " + before.AssetPath);
                    unchanged++;
                    continue;
                }

                if (after.Guid != before.Guid)
                {
                    sb.AppendLine("  !! GUID DRIFT " + before.Guid + " -> " + after.Guid +
                                  "  " + before.AssetPath);
                    drift++;
                    continue;
                }

                if (after.Version > before.Version)
                {
                    migrated++;
                }
                else
                {
                    sb.AppendLine("  still v" + after.Version + " (target " +
                                  target[before.Importer] + "): " + before.AssetPath);
                    unchanged++;
                }
            }

            sb.AppendLine("migrated       : " + migrated);
            sb.AppendLine("unchanged      : " + unchanged);
            sb.AppendLine("guid drift     : " + drift);

            // The forced path writes a temporary marker into userData. If any of it
            // survived, the restore pass did not finish and a meta is now carrying
            // our scratch data - which must fail the run rather than be shrugged off.
            int residue = 0;
            if (force)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    try
                    {
                        if (File.ReadAllText(stale[i].MetaPath).Contains(MigrationMarker))
                        {
                            sb.AppendLine("  !! MARKER LEFT BEHIND: " + stale[i].AssetPath);
                            residue++;
                        }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("  UNREADABLE ON MARKER CHECK: " + stale[i].AssetPath +
                                      " :: " + e.Message);
                        residue++;
                    }
                }
                sb.AppendLine("marker residue : " + residue);
            }

            bool ok = drift == 0 && unchanged == 0 && residue == 0;
            sb.AppendLine("=== RESULT: " + (ok ? "PASS" : "REVIEW") + " ===");

            report = sb.ToString();
            return ok;
        }

        // Every .meta under the package that declares an importer with a version.
        private static List<MetaInfo> Scan()
        {
            List<MetaInfo> found = new List<MetaInfo>();

            string root = Path.Combine(Application.dataPath, PackageFolder);
            if (!Directory.Exists(root)) return found;

            string[] metas = Directory.GetFiles(root, "*.meta", SearchOption.AllDirectories);
            for (int i = 0; i < metas.Length; i++)
            {
                MetaInfo info;
                if (ReadMeta(metas[i], out info)) found.Add(info);
            }

            return found;
        }

        // Returns false for metas with no importer version - folders, .asset files
        // and scripts have nothing to migrate.
        private static bool ReadMeta(string metaPath, out MetaInfo info)
        {
            info = new MetaInfo();

            string[] lines;
            try
            {
                lines = File.ReadAllLines(metaPath);
            }
            catch (Exception)
            {
                return false;
            }

            string importer = null;
            string guid = null;
            int version = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                if (guid == null)
                {
                    Match g = GuidLine.Match(lines[i]);
                    if (g.Success) { guid = g.Groups[1].Value; continue; }
                }

                if (importer == null)
                {
                    Match m = ImporterLine.Match(lines[i]);
                    if (m.Success) { importer = m.Groups[1].Value; continue; }
                }

                // Only the importer's own version counts; nested blocks can carry
                // their own serializedVersion further down the file.
                if (importer != null && version < 0)
                {
                    Match v = VersionLine.Match(lines[i]);
                    if (v.Success) version = int.Parse(v.Groups[1].Value);
                }
            }

            if (importer == null || guid == null || version < 0) return false;

            info.MetaPath = metaPath;
            info.Guid = guid;
            info.Importer = importer;
            info.Version = version;

            // "<abs>/Assets/UniverseCreator/x.fbx.meta" -> "Assets/UniverseCreator/x.fbx"
            string assetAbs = metaPath.Substring(0, metaPath.Length - ".meta".Length);
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            info.AssetPath = assetAbs.Substring(projectRoot.Length + 1).Replace('\\', '/');

            return true;
        }
    }
}
