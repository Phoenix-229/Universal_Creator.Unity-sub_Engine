// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCScriptIcons.cs
//  Purpose: Puts the product's mark on its own components and assets, in place of
//           Unity's default grey C# badge and the grey cuboid that a script with
//           no icon draws at its transform in the scene view.
//
//  WHY AN OPERATOR AND NOT A HAND EDIT
//  -----------------------------------
//  A script's icon lives in that script's .meta file, as a file GUID plus a local
//  file ID. Hand-writing either is how a meta ends up pointing at an asset that
//  has since been re-imported under a different ID - it looks correct in the YAML
//  and renders nothing. EditorGUIUtility.SetIconForObject followed by a forced
//  re-import makes the editor itself produce those bytes.
//
//  The write half and the read half are separate on purpose. Nothing here is
//  named Run(out string), because that signature is how gate suites discover
//  read-only checks; a stamper picked up by a suite would turn verification into
//  an edit.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor
{
    public static class UCScriptIcons
    {
        private const string Prefix = "UCICON";

        /// <summary>
        /// Where to look for the scripts. Bounded to the product's own folder: the
        /// match below is by exact System.Type so it could never touch another
        /// vendor's file, but there is no reason to walk a whole project's scripts
        /// to find two of them.
        /// </summary>
        private const string SearchRoot = "Assets/UniverseCreator";

        /// <summary>
        /// The two things an author sees represented by an icon: the world object in
        /// the hierarchy and the scene view, and the node graph asset in the project
        /// window. Held as System.Type rather than as strings so renaming either one
        /// breaks this file at compile time instead of silently stamping nothing -
        /// a stamper that reports "0 of 2 matched" long after a rename is a report
        /// nobody reads.
        /// </summary>
        private static Type[] Targets()
        {
            return new Type[]
            {
                typeof(UniverseCreator.TerrainSystem.UniverseCreatorObject),
                typeof(UniverseCreator.Nodes.Graph)
            };
        }

        [MenuItem("Tools/Universe Creator/Setup/Stamp Script Icons")]
        public static void StampFromMenu()
        {
            string report;
            bool ok = StampAll(true, out report);
            if (ok) Debug.Log(report); else Debug.LogError(report);
        }

        [MenuItem("Tools/Universe Creator/Setup/Stamp Script Icons (report only)")]
        public static void ReportFromMenu()
        {
            string report;
            StampAll(false, out report);
            Debug.Log(report);
        }

        /// <summary>
        /// Headless entry: -executeMethod UniverseCreator.Editor.UCScriptIcons.StampBatch.
        /// Exits 1 when a target could not be resolved.
        /// </summary>
        public static void StampBatch()
        {
            string report;
            bool ok = StampAll(true, out report);
            Debug.Log(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool StampAll(bool write, out string report)
        {
            int changed, already, missing;
            return StampAll(write, out report, out changed, out already, out missing);
        }

        /// <summary>
        /// Assigns the brand mark to every target script's icon.
        ///
        /// write=false runs the whole scan and reports what would change without
        /// touching a file, so the census can be read before anyone agrees to a
        /// disk write and so a read-only check can reuse it. A script already
        /// carrying the right icon is left alone even when write is true: a forced
        /// re-import of an unchanged asset costs an import and a version-control
        /// diff for nothing.
        ///
        /// The return value reports whether every target RESOLVED, not whether
        /// every target was already stamped. An unstamped icon is a job still to
        /// do; an unresolvable component is a broken build.
        /// </summary>
        public static bool StampAll(bool write, out string report, out int changed, out int already, out int missing)
        {
            StringBuilder sb = new StringBuilder();
            Type[] targets = Targets();
            Texture2D mark = UCIcons.Brand();
            changed = 0;
            already = 0;
            missing = 0;

            if (mark == null)
            {
                missing = targets.Length;
                report = Prefix + " FAIL   brand mark not found at "
                       + UCBrandImages.UniverseCreatorIconPath
                       + " - run Tools/Universe Creator/Setup/Split Brand Banner first\n";
                return false;
            }

            Dictionary<Type, MonoScript> scripts = ScriptsFor(targets);

            for (int i = 0; i < targets.Length; i++)
            {
                Type type = targets[i];
                MonoScript script;
                if (!scripts.TryGetValue(type, out script) || script == null)
                {
                    missing++;
                    sb.Append(Prefix).Append(" missing  ").Append(type.FullName)
                      .Append("  no MonoScript asset resolved to this type\n");
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(script);
                if (EditorGUIUtility.GetIconForObject(script) == mark)
                {
                    already++;
                    sb.Append(Prefix).Append(" already  ").Append(type.FullName).Append("  ").Append(path).Append("\n");
                    continue;
                }

                changed++;
                if (write)
                {
                    EditorGUIUtility.SetIconForObject(script, mark);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    sb.Append(Prefix).Append(" stamped  ").Append(type.FullName).Append("  ").Append(path).Append("\n");
                }
                else
                {
                    sb.Append(Prefix).Append(" would    ").Append(type.FullName).Append("  ").Append(path).Append("\n");
                }
            }

            if (write && changed > 0) AssetDatabase.SaveAssets();

            bool ok = missing == 0;
            sb.Append(Prefix).Append(" result ").Append(ok ? "PASS" : "FAIL")
              .Append("  targets ").Append(targets.Length)
              .Append("  ").Append(write ? "stamped " : "would-stamp ").Append(changed)
              .Append("  already ").Append(already)
              .Append("  missing ").Append(missing).Append("\n");

            report = sb.ToString();
            return ok;
        }

        /// <summary>
        /// The MonoScript asset behind each requested type. Matching is by
        /// MonoScript.GetClass() and never by file name: two packages may both ship
        /// a Graph.cs, and a name match would stamp whichever the search returned
        /// first.
        /// </summary>
        private static Dictionary<Type, MonoScript> ScriptsFor(Type[] targets)
        {
            Dictionary<Type, MonoScript> found = new Dictionary<Type, MonoScript>();
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", new string[] { SearchRoot });

            for (int g = 0; g < guids.Length; g++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[g]);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;

                Type cls = script.GetClass();
                if (cls == null) continue;

                for (int t = 0; t < targets.Length; t++)
                {
                    if (cls == targets[t] && !found.ContainsKey(cls)) found.Add(cls, script);
                }
            }
            return found;
        }
    }
}
